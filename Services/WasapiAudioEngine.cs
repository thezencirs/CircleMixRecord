// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CircleMixRecord.Services
{
    public class WasapiAudioEngine : IDisposable
    {
        public double CurrentMicDb { get; private set; } = -60.0;
        public double CurrentSystemDb { get; private set; } = -60.0;
        public bool MicMuted { get; set; } = false;
        public bool SystemMuted { get; set; } = false;

        // Stüdyo Mikrofon Ayarları — Temiz varsayılanlar: hafif +6 dB kazanç, AGC/Gate KAPALI.
        // (Ses seviyesi dengeleme kayıt sonunda FFmpeg loudnorm ile kayıpsız yapılır; canlı AGC oda gürültüsünü pompalıyordu.)
        public double MicGainBoostDb { get; set; } = 6.0;  // +6 dB Dijital Pre-Amp
        public bool AutoVoiceBoost { get; set; } = false;  // İsteğe bağlı canlı AGC
        public bool NoiseGateEnabled { get; set; } = false; // İsteğe bağlı hafif dip gürültü kapısı
        public string PreferredMicName { get; set; } = ""; // Seçili mikrofon adı (Wireless Mic Rx / Circle Broadcast)

        private Thread _micThread;
        private Thread _sysThread;
        private volatile bool _running;
        private volatile bool _recording;
        private volatile bool _paused;

        private BinaryWriter _micWavWriter;
        private BinaryWriter _sysWavWriter;
        private int _micBytesWritten;
        private int _sysBytesWritten;
        private int _micSampleRate = 48000;
        private int _micChannels = 2;
        private int _sysSampleRate = 48000;
        private int _sysChannels = 2;

        private float _agcEnvelope = 0.08f;
        private readonly object _lock = new();
        public List<float> RecordedMicPeaks { get; } = new();

        public void StartMonitoring()
        {
            if (_running) return;
            _running = true;

            _micThread = new Thread(() => CaptureMultiMicLoop())
            {
                IsBackground = true,
                Name = "WASAPI_Mic_Capture"
            };
            _sysThread = new Thread(() => CaptureLoop(isLoopback: true))
            {
                IsBackground = true,
                Name = "WASAPI_SysLoopback_Capture"
            };

            _micThread.Start();
            _sysThread.Start();
        }

        public void StartRecordingToWav(string micWavPath, string sysWavPath)
        {
            lock (_lock)
            {
                RecordedMicPeaks.Clear();
                _micBytesWritten = 0;
                _sysBytesWritten = 0;
                _recSamplesBroadcast = 0;
                _recSamplesRaw = 0;

                _micWavWriter = CreateWavHeader(micWavPath, _micSampleRate, _micChannels);
                _sysWavWriter = CreateWavHeader(sysWavPath, _sysSampleRate, _sysChannels);
                _paused = false;
                _recording = true;
            }
        }

        public void SetPaused(bool paused) => _paused = paused;

        public void StopRecordingWav()
        {
            lock (_lock)
            {
                _recording = false;
                _paused = false;
                FinalizeWav(ref _micWavWriter, _micBytesWritten);
                FinalizeWav(ref _sysWavWriter, _sysBytesWritten);
            }
        }

        private static BinaryWriter CreateWavHeader(string path, int sampleRate, int channels)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var bw = new BinaryWriter(fs);
            int bitsPerSample = 16;
            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(0);
            return bw;
        }

        private static void FinalizeWav(ref BinaryWriter bw, int dataBytes)
        {
            if (bw == null) return;
            try
            {
                bw.Flush();
                bw.Seek(4, SeekOrigin.Begin);
                bw.Write(36 + dataBytes);
                bw.Seek(40, SeekOrigin.Begin);
                bw.Write(dataBytes);
                bw.Flush();
                bw.Dispose();
            }
            catch { }
            bw = null;
        }

        private sealed class ActiveMicEndpoint
        {
            public IntPtr Device;
            public IntPtr AudioClient;
            public IntPtr CaptureClient;
            public IntPtr Pwfx;
            public int SampleRate;
            public short Channels;
            public short BitsPerSample;
            public bool IsFloat;
            public string Name = "";
            public bool IsBroadcast;
            public long LastSignalMs = -1;   // > -66 dB sinyal görüldüğü son an
            public long LastVoiceMs = -1;    // > -22 dB konuşma tepe noktası görüldüğü son an
            public int VoiceEvents;          // ayrı konuşma tepe sayısı
        }

        // Tek Aktif Mikrofon Seçimi (Uç noktalar arası 10 ms'de bir zıplama YOK → cızırtı/kesik ses yok)
        // Canlı yayın için işlenmiş PCM musluğu (16-bit, interleaved): veri, örnekleme hızı, kanal sayısı
        public event Action<byte[], int, int> OnMicPcm;
        public event Action<byte[], int, int> OnSystemPcm;

        public string ActiveMicName { get; private set; } = "";
        public bool ActiveMicIsBroadcast { get; private set; }
        public bool BroadcastMicSilent { get; private set; }
        private long _recSamplesBroadcast;
        private long _recSamplesRaw;
        private string _pendingNotice;

        /// <summary>Kayıt boyunca çoğunlukla Circle Broadcast (AI gürültü temizlenmiş) mikrofon mu kullanıldı?</summary>
        public bool RecordingUsedBroadcastMic => _recSamplesBroadcast > _recSamplesRaw;

        /// <summary>UI için tek seferlik durum mesajı (ör. Broadcast mikrofonu ses vermiyor → yedeğe geçildi).</summary>
        public bool TryConsumeNotice(out string msg)
        {
            msg = Interlocked.Exchange(ref _pendingNotice, null);
            return msg != null;
        }

        private static readonly Guid PKEY_Device_FriendlyName_Fmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        private static unsafe string GetDeviceFriendlyName(IntPtr dev)
        {
            IntPtr store = IntPtr.Zero;
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                IntPtr* vtblDev = *(IntPtr**)dev;
                var openStore = Marshal.GetDelegateForFunctionPointer<OpenPropertyStoreDelegate>(vtblDev[4]);
                if (openStore(dev, 0 /* STGM_READ */, out store) != 0 || store == IntPtr.Zero) return "";
                IntPtr* vtblStore = *(IntPtr**)store;
                var getValue = Marshal.GetDelegateForFunctionPointer<PropStoreGetValueDelegate>(vtblStore[5]);
                var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName_Fmt, pid = 14 };
                if (getValue(store, ref key, pv) != 0) return "";
                ushort vt = (ushort)Marshal.ReadInt16(pv, 0);
                string name = vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) ?? "" : "";
                PropVariantClear(pv);
                return name;
            }
            catch { return ""; }
            finally
            {
                if (store != IntPtr.Zero) Marshal.Release(store);
                Marshal.FreeCoTaskMem(pv);
            }
        }

        /// <summary>
        /// Öncelik: 1) Kullanıcının seçtiği mikrofon, 2) Circle Broadcast (AI gürültü temizleme), 3) Fiziksel mikrofon.
        /// Broadcast mikrofonu siz konuşurken bile tamamen sessizse (kaynak seçilmemiş / efekt kapalı) otomatik
        /// fiziksel mikrofona geçer; Broadcast tekrar ses vermeye başlarsa geri döner.
        /// </summary>
        private int ChooseMicEndpoint(List<ActiveMicEndpoint> eps, long nowMs, int currentIdx)
        {
            // "Ölü" uç nokta: oturum boyunca HİÇ sinyal vermemiş, ama diğer mikrofon en az 3 kez konuşma algılamış.
            // (Çalışan Broadcast, siz ilk konuştuğunuzda mutlaka sinyal verir; klavye sesini sildiği için
            //  sessiz anlarda yanlışlıkla 'ölü' sayılmaz — bir kez sinyal verdiyse kalıcı olarak 'canlı' kabul edilir.)
            bool IsDead(ActiveMicEndpoint e)
            {
                if (e.LastSignalMs >= 0) return false;
                int otherVoice = 0;
                foreach (var o in eps)
                    if (!ReferenceEquals(o, e)) otherVoice += o.VoiceEvents;
                return otherVoice >= 3;
            }

            // Kayıt sırasında mikrofon değiştirme (ses karakteri ortada değişmesin) — yalnızca aktif olan ölüyse geç
            if (_recording && currentIdx >= 0 && currentIdx < eps.Count && !IsDead(eps[currentIdx]))
                return currentIdx;

            string pref = (PreferredMicName ?? "").Trim();
            if (pref.Length > 0)
            {
                for (int i = 0; i < eps.Count; i++)
                    if (eps[i].Name.Equals(pref, StringComparison.OrdinalIgnoreCase) && !IsDead(eps[i])) return i;
            }

            for (int i = 0; i < eps.Count; i++)
            {
                if (eps[i].IsBroadcast)
                {
                    bool dead = IsDead(eps[i]);
                    BroadcastMicSilent = dead;
                    if (!dead) return i;
                }
            }

            int best = -1;
            for (int i = 0; i < eps.Count; i++)
            {
                if (eps[i].IsBroadcast) continue;
                if (best < 0 || eps[i].LastSignalMs > eps[best].LastSignalMs) best = i;
            }
            return best >= 0 ? best : 0;
        }

        /// <summary>
        /// Sistemdeki TÜM aktif mikrofon uç noktalarını (Wireless Mic Rx, Circle Broadcast, Fiziksel Mikrofon vb.)
        /// donanım seviyesinde %100 sese açar ve en güçlü sinyali veren mikrofondan +16dB Stüdyo Pre-Amp
        /// ve Otomatik Broadcast Compressor/Limiter ile kristal netliğinde ses yakalar.
        /// </summary>
        private unsafe void CaptureMultiMicLoop()
        {
            CoInitializeEx(IntPtr.Zero, 0);
            IntPtr enumerator = IntPtr.Zero;
            IntPtr collection = IntPtr.Zero;
            var endpoints = new List<ActiveMicEndpoint>();

            try
            {
                Guid clsidEnum = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                Guid iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                Guid iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                Guid iidEndpointVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                Guid emptyGuid = Guid.Empty;

                int hr = CoCreateInstance(ref clsidEnum, IntPtr.Zero, 23, ref iidEnum, out enumerator);
                if (hr != 0 || enumerator == IntPtr.Zero) return;

                IntPtr* vtblEnum = *(IntPtr**)enumerator;
                var enumEndpoints = Marshal.GetDelegateForFunctionPointer<EnumAudioEndpointsDelegate>(vtblEnum[3]);
                hr = enumEndpoints(enumerator, 1, 1, out collection); // eCapture = 1, DEVICE_STATE_ACTIVE = 1

                if (hr == 0 && collection != IntPtr.Zero)
                {
                    IntPtr* vtblCol = *(IntPtr**)collection;
                    var getCount = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(vtblCol[3]);
                    var getItem = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(vtblCol[4]);
                    getCount(collection, out uint count);

                    for (uint i = 0; i < count; i++)
                    {
                        if (getItem(collection, i, out IntPtr dev) != 0 || dev == IntPtr.Zero) continue;

                        IntPtr* vtblDev = *(IntPtr**)dev;
                        var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(vtblDev[3]);

                        // 1) Donanım ses seviyesini %100 yap ve Mute kilidini kaldır
                        if (activate(dev, ref iidEndpointVol, 23, IntPtr.Zero, out IntPtr epVol) == 0 && epVol != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr* vtblVol = *(IntPtr**)epVol;
                                var setVol = Marshal.GetDelegateForFunctionPointer<SetMasterVolumeLevelScalarDelegate>(vtblVol[7]);
                                var setMute = Marshal.GetDelegateForFunctionPointer<SetMuteDelegate>(vtblVol[14]);
                                setMute(epVol, 0, ref emptyGuid);
                                setVol(epVol, 1.0f, ref emptyGuid);
                            }
                            catch { }
                            finally
                            {
                                Marshal.Release(epVol);
                            }
                        }

                        // 2) IAudioClient başlat
                        if (activate(dev, ref iidAudioClient, 23, IntPtr.Zero, out IntPtr audioClient) != 0 || audioClient == IntPtr.Zero)
                        {
                            Marshal.Release(dev);
                            continue;
                        }

                        IntPtr* vtblClient = *(IntPtr**)audioClient;
                        var getMixFormat = Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(vtblClient[8]);
                        if (getMixFormat(audioClient, out IntPtr pwfx) != 0 || pwfx == IntPtr.Zero)
                        {
                            Marshal.Release(audioClient);
                            Marshal.Release(dev);
                            continue;
                        }

                        short ch = Marshal.ReadInt16(pwfx, 2);
                        int sr = Marshal.ReadInt32(pwfx, 4);
                        short bps = Marshal.ReadInt16(pwfx, 14);

                        var initClient = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(vtblClient[3]);
                        if (initClient(audioClient, 0, 0u, 10000000L, 0, pwfx, IntPtr.Zero) != 0)
                        {
                            CoTaskMemFree(pwfx);
                            Marshal.Release(audioClient);
                            Marshal.Release(dev);
                            continue;
                        }

                        var getService = Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(vtblClient[14]);
                        if (getService(audioClient, ref iidCapture, out IntPtr captureClient) != 0 || captureClient == IntPtr.Zero)
                        {
                            CoTaskMemFree(pwfx);
                            Marshal.Release(audioClient);
                            Marshal.Release(dev);
                            continue;
                        }

                        var startClient = Marshal.GetDelegateForFunctionPointer<StartStopDelegate>(vtblClient[10]);
                        startClient(audioClient);

                        string epName = GetDeviceFriendlyName(dev);
                        endpoints.Add(new ActiveMicEndpoint
                        {
                            Device = dev,
                            AudioClient = audioClient,
                            CaptureClient = captureClient,
                            Pwfx = pwfx,
                            SampleRate = sr,
                            Channels = ch,
                            BitsPerSample = bps,
                            IsFloat = (bps == 32),
                            Name = epName,
                            IsBroadcast = epName.IndexOf("Broadcast", StringComparison.OrdinalIgnoreCase) >= 0
                        });
                    }
                }

                // Hiçbir çoklu uç nokta açılamadıysa varsayılan tekli döngüye yedeklen
                if (endpoints.Count == 0)
                {
                    CaptureLoop(isLoopback: false);
                    return;
                }

                _micChannels = 2;
                int outChannels = 2;

                var swTimer = Stopwatch.StartNew();
                int activeIdx = -1;
                long activeLastDataMs = 0;
                var perEndpointSamples = new List<float>[endpoints.Count];
                for (int k = 0; k < endpoints.Count; k++) perEndpointSamples[k] = new List<float>(4096);

                while (_running)
                {
                    Thread.Sleep(10);
                    long nowMs = swTimer.ElapsedMilliseconds;

                    // 1) TÜM uç noktaların tamponlarını boşalt (taşma olmasın) + sinyal/konuşma takibi yap
                    for (int eIdx = 0; eIdx < endpoints.Count; eIdx++)
                    {
                        var ep = endpoints[eIdx];
                        var buf = perEndpointSamples[eIdx];
                        buf.Clear();

                        IntPtr* vtblCap = *(IntPtr**)ep.CaptureClient;
                        var getNextPacketSize = Marshal.GetDelegateForFunctionPointer<GetNextPacketSizeDelegate>(vtblCap[5]);
                        var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(vtblCap[3]);
                        var releaseBuffer = Marshal.GetDelegateForFunctionPointer<ReleaseBufferDelegate>(vtblCap[4]);

                        if (getNextPacketSize(ep.CaptureClient, out uint packetLen) != 0) continue;
                        float epPeak = 0f;

                        while (packetLen > 0 && _running)
                        {
                            if (getBuffer(ep.CaptureClient, out IntPtr dataPtr, out uint numFrames, out uint bufFlags, out _, out _) != 0)
                                break;

                            bool silentFlag = (bufFlags & 0x2) != 0;
                            if (!silentFlag && dataPtr != IntPtr.Zero && numFrames > 0)
                            {
                                int chs = Math.Max(1, (int)ep.Channels);
                                float inv = 1f / chs;
                                if (ep.IsFloat)
                                {
                                    float* fPtr = (float*)dataPtr;
                                    for (uint f = 0; f < numFrames; f++)
                                    {
                                        // Kanalların ORTALAMASI (mikrofon dizisi için doğru mono; 'en büyük değer' seçimi bozulma yapar)
                                        float s = 0f;
                                        for (int c = 0; c < chs; c++) s += fPtr[f * chs + c];
                                        s *= inv;
                                        float abs = Math.Abs(s);
                                        if (abs > epPeak) epPeak = abs;
                                        buf.Add(s);
                                    }
                                }
                                else if (ep.BitsPerSample == 16)
                                {
                                    short* sPtr = (short*)dataPtr;
                                    for (uint f = 0; f < numFrames; f++)
                                    {
                                        float s = 0f;
                                        for (int c = 0; c < chs; c++) s += sPtr[f * chs + c] / 32768f;
                                        s *= inv;
                                        float abs = Math.Abs(s);
                                        if (abs > epPeak) epPeak = abs;
                                        buf.Add(s);
                                    }
                                }
                                else
                                {
                                    for (uint f = 0; f < numFrames; f++) buf.Add(0f);
                                }
                            }
                            else if (numFrames > 0)
                            {
                                for (uint f = 0; f < numFrames; f++) buf.Add(0f);
                            }

                            releaseBuffer(ep.CaptureClient, numFrames);
                            if (getNextPacketSize(ep.CaptureClient, out packetLen) != 0) break;
                        }

                        if (!MicMuted)
                        {
                            if (epPeak > 0.0005f) ep.LastSignalMs = nowMs;           // > -66 dB
                            if (epPeak > 0.08f)                                        // > -22 dB konuşma tepesi
                            {
                                if (ep.LastVoiceMs < 0 || nowMs - ep.LastVoiceMs > 350) ep.VoiceEvents++;
                                ep.LastVoiceMs = nowMs;
                            }
                        }
                    }

                    // 2) Tek aktif mikrofonu seç (kayıt sırasında sabit kalır)
                    int chosen = ChooseMicEndpoint(endpoints, nowMs, activeIdx);
                    if (chosen != activeIdx)
                    {
                        bool wasBroadcast = activeIdx >= 0 && endpoints[activeIdx].IsBroadcast;
                        activeIdx = chosen;
                        activeLastDataMs = nowMs;
                        var a = endpoints[activeIdx];
                        ActiveMicName = string.IsNullOrEmpty(a.Name) ? "Mikrofon" : a.Name;
                        ActiveMicIsBroadcast = a.IsBroadcast;
                        if (!_recording) _micSampleRate = a.SampleRate;
                        if (wasBroadcast && !a.IsBroadcast)
                        {
                            _pendingNotice = $"⚠️ Circle Broadcast mikrofonu ses vermiyor → '{ActiveMicName}' ile devam ediliyor. " +
                                             "Düzeltmek için Circle Broadcast > Mikrofon > Kaynak: 'Mikrofon Dizisi (Fiziksel Mikrofon)' seçip 'Gürültü Giderme'yi açın.";
                        }
                        else
                        {
                            _pendingNotice = $"🎙️ Aktif mikrofon: {ActiveMicName}" + (a.IsBroadcast ? " (AI Gürültü Temizleme)" : "");
                        }
                    }

                    var active = endpoints[activeIdx];
                    var samples = perEndpointSamples[activeIdx];
                    int capturedFrames = samples.Count;

                    if (capturedFrames == 0)
                    {
                        CurrentMicDb = Math.Max(-60.0, CurrentMicDb - 2.5);
                        // Yalnızca cihaz gerçekten durduysa (>200 ms veri yok) sessizlik ekle; aksi halde
                        // her boş 10 ms turunda sessizlik eklemek sesi uzatır, kesik/cızırtılı yapar.
                        long gap = nowMs - activeLastDataMs;
                        if (gap > 200)
                        {
                            int silentFrames = (int)((_micSampleRate * Math.Min(gap, 500)) / 1000);
                            activeLastDataMs = nowMs;
                            if (_recording && !_paused && silentFrames > 0)
                                WriteSilenceFrames(isLoopback: false, silentFrames, outChannels);
                        }
                        continue;
                    }
                    activeLastDataMs = nowMs;

                    // 3) Temiz kazanç zinciri: Pre-Amp → (isteğe bağlı) AGC → yalnızca tepe noktalarında yumuşak limiter.
                    // Eski zincir (+16 dB + AGC x3.2 + her örnekte tanh) sesi doyuruyor, oda gürültüsünü pompalıyordu.
                    float baseGain = (float)Math.Pow(10.0, Math.Clamp(MicGainBoostDb, 0.0, 32.0) / 20.0);
                    float postPeak = 0f;
                    byte[] pcm16 = new byte[capturedFrames * outChannels * 2];
                    int outIdx = 0;

                    for (int i = 0; i < capturedFrames; i++)
                    {
                        float raw = MicMuted ? 0f : samples[i];
                        float absRaw = Math.Abs(raw);

                        // Hafif Dip Gürültü Kapısı (isteğe bağlı, -56 dB altını yarıya indirir)
                        if (NoiseGateEnabled && absRaw < 0.0016f)
                            raw *= 0.5f;

                        float amplified = raw * baseGain;
                        float absAmp = Math.Abs(amplified);

                        if (AutoVoiceBoost)
                        {
                            if (absAmp > _agcEnvelope)
                                _agcEnvelope = _agcEnvelope * 0.995f + absAmp * 0.005f;
                            else
                                _agcEnvelope = _agcEnvelope * 0.99995f + absAmp * 0.00005f;

                            float agcMul = Math.Clamp(0.35f / Math.Max(0.05f, _agcEnvelope), 1.0f, 2.0f);
                            amplified *= agcMul;
                        }

                        // Yumuşak diz limiter: 0.85 altına DOKUNMAZ (temiz ses), üstünü 0.98'e yumuşakça sıkıştırır
                        float a = Math.Abs(amplified);
                        if (a > 0.85f)
                        {
                            float over = a - 0.85f;
                            float lim = 0.85f + 0.13f * (float)Math.Tanh(over / 0.13f);
                            amplified = amplified < 0 ? -lim : lim;
                        }
                        float absFinal = Math.Abs(amplified);
                        if (absFinal > postPeak) postPeak = absFinal;

                        int s16 = (int)(Math.Clamp(amplified, -0.985f, 0.985f) * 32767.0f);
                        byte b0 = (byte)(s16 & 0xFF);
                        byte b1 = (byte)((s16 >> 8) & 0xFF);

                        // Stereo (Sol + Sağ eşit merkezli eğitmen sesi!)
                        for (int c = 0; c < outChannels; c++)
                        {
                            pcm16[outIdx++] = b0;
                            pcm16[outIdx++] = b1;
                        }
                    }

                    double db = postPeak > 0.00001f ? Math.Clamp(20.0 * Math.Log10(postPeak), -60.0, 0.0) : -60.0;
                    CurrentMicDb = MicMuted ? -60.0 : db;
                    try { OnMicPcm?.Invoke(pcm16, active.SampleRate, outChannels); } catch { }

                    if (_recording && !_paused && pcm16.Length > 0)
                    {
                        lock (_lock)
                        {
                            if (_micWavWriter != null)
                            {
                                _micWavWriter.Write(pcm16);
                                _micBytesWritten += pcm16.Length;
                                if (active.IsBroadcast) _recSamplesBroadcast += capturedFrames; else _recSamplesRaw += capturedFrames;
                                if (RecordedMicPeaks.Count < 20000)
                                    RecordedMicPeaks.Add(postPeak);
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                foreach (var ep in endpoints)
                {
                    try
                    {
                        IntPtr* vtblClient = *(IntPtr**)ep.AudioClient;
                        var stopClient = Marshal.GetDelegateForFunctionPointer<StartStopDelegate>(vtblClient[11]);
                        stopClient(ep.AudioClient);
                    }
                    catch { }
                    if (ep.Pwfx != IntPtr.Zero) CoTaskMemFree(ep.Pwfx);
                    if (ep.CaptureClient != IntPtr.Zero) Marshal.Release(ep.CaptureClient);
                    if (ep.AudioClient != IntPtr.Zero) Marshal.Release(ep.AudioClient);
                    if (ep.Device != IntPtr.Zero) Marshal.Release(ep.Device);
                }
                if (collection != IntPtr.Zero) Marshal.Release(collection);
                if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
            }
        }

        private unsafe void CaptureLoop(bool isLoopback)
        {
            CoInitializeEx(IntPtr.Zero, 0);
            IntPtr enumerator = IntPtr.Zero;
            IntPtr device = IntPtr.Zero;
            IntPtr audioClient = IntPtr.Zero;
            IntPtr captureClient = IntPtr.Zero;
            IntPtr pwfx = IntPtr.Zero;

            try
            {
                Guid clsidEnum = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                Guid iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
                int hr = CoCreateInstance(ref clsidEnum, IntPtr.Zero, 23, ref iidEnum, out enumerator);
                if (hr != 0 || enumerator == IntPtr.Zero) return;

                int dataFlow = isLoopback ? 0 : 1;
                IntPtr* vtblEnum = *(IntPtr**)enumerator;
                var getDefaultEndpoint = Marshal.GetDelegateForFunctionPointer<GetDefaultAudioEndpointDelegate>(vtblEnum[4]);
                hr = getDefaultEndpoint(enumerator, dataFlow, 0, out device);
                if (hr != 0 || device == IntPtr.Zero) return;

                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                IntPtr* vtblDev = *(IntPtr**)device;
                var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(vtblDev[3]);
                hr = activate(device, ref iidAudioClient, 23, IntPtr.Zero, out audioClient);
                if (hr != 0 || audioClient == IntPtr.Zero) return;

                IntPtr* vtblClient = *(IntPtr**)audioClient;
                var getMixFormat = Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(vtblClient[8]);
                hr = getMixFormat(audioClient, out pwfx);
                if (hr != 0 || pwfx == IntPtr.Zero) return;

                short channels = Marshal.ReadInt16(pwfx, 2);
                int sampleRate = Marshal.ReadInt32(pwfx, 4);
                short bitsPerSample = Marshal.ReadInt16(pwfx, 14);
                bool isFloat = (bitsPerSample == 32);

                if (isLoopback)
                {
                    _sysSampleRate = sampleRate;
                    _sysChannels = Math.Clamp((int)channels, 1, 2);
                }
                else
                {
                    _micSampleRate = sampleRate;
                    _micChannels = Math.Clamp((int)channels, 1, 2);
                }

                uint flags = isLoopback ? 0x00020000u : 0u;
                var initClient = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(vtblClient[3]);
                hr = initClient(audioClient, 0, flags, 10000000L, 0, pwfx, IntPtr.Zero);
                if (hr != 0) return;

                Guid iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                var getService = Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(vtblClient[14]);
                hr = getService(audioClient, ref iidCapture, out captureClient);
                if (hr != 0 || captureClient == IntPtr.Zero) return;

                var startClient = Marshal.GetDelegateForFunctionPointer<StartStopDelegate>(vtblClient[10]);
                startClient(audioClient);

                IntPtr* vtblCap = *(IntPtr**)captureClient;
                var getNextPacketSize = Marshal.GetDelegateForFunctionPointer<GetNextPacketSizeDelegate>(vtblCap[5]);
                var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(vtblCap[3]);
                var releaseBuffer = Marshal.GetDelegateForFunctionPointer<ReleaseBufferDelegate>(vtblCap[4]);

                int outChannels = isLoopback ? _sysChannels : _micChannels;
                var swSilence = Stopwatch.StartNew();
                long lastPacketMs = 0;

                while (_running)
                {
                    Thread.Sleep(12);
                    hr = getNextPacketSize(captureClient, out uint packetLength);
                    if (hr != 0) break;

                    if (packetLength == 0)
                    {
                        if (isLoopback) CurrentSystemDb = Math.Max(-60.0, CurrentSystemDb - 3.0);
                        else CurrentMicDb = Math.Max(-60.0, CurrentMicDb - 3.0);

                        long nowMs = swSilence.ElapsedMilliseconds;
                        if (nowMs - lastPacketMs >= 40)
                        {
                            int silentFrames = (int)((sampleRate * (nowMs - lastPacketMs)) / 1000);
                            lastPacketMs = nowMs;
                            if (_recording && !_paused && silentFrames > 0 && silentFrames < sampleRate)
                            {
                                WriteSilenceFrames(isLoopback, silentFrames, outChannels);
                            }
                        }
                        continue;
                    }

                    while (packetLength > 0 && _running)
                    {
                        hr = getBuffer(captureClient, out IntPtr dataPtr, out uint numFrames, out uint bufFlags, out ulong devPos, out ulong qpcPos);
                        if (hr != 0) break;

                        lastPacketMs = swSilence.ElapsedMilliseconds;
                        bool silentFlag = (bufFlags & 0x2) != 0;
                        bool muted = isLoopback ? SystemMuted : MicMuted;

                        float peak = 0f;
                        byte[] pcm16 = new byte[numFrames * outChannels * 2];

                        if (!silentFlag && !muted && dataPtr != IntPtr.Zero && numFrames > 0)
                        {
                            float micGain = isLoopback ? 1.0f : (float)Math.Pow(10.0, Math.Clamp(MicGainBoostDb, 0.0, 32.0) / 20.0);
                            if (isFloat)
                            {
                                float* fPtr = (float*)dataPtr;
                                int outIdx = 0;
                                for (uint f = 0; f < numFrames; f++)
                                {
                                    for (int c = 0; c < outChannels; c++)
                                    {
                                        int srcCh = Math.Min(c, channels - 1);
                                        float sample = fPtr[f * channels + srcCh] * micGain;
                                        if (!isLoopback)
                                            sample = (float)(Math.Tanh(sample * 1.12) / Math.Tanh(1.12));
                                        float abs = Math.Abs(sample);
                                        if (abs > peak) peak = abs;

                                        int s16 = (int)(Math.Clamp(sample, -1.0f, 1.0f) * 32767.0f);
                                        pcm16[outIdx++] = (byte)(s16 & 0xFF);
                                        pcm16[outIdx++] = (byte)((s16 >> 8) & 0xFF);
                                    }
                                }
                            }
                            else if (bitsPerSample == 16)
                            {
                                short* sPtr = (short*)dataPtr;
                                int outIdx = 0;
                                for (uint f = 0; f < numFrames; f++)
                                {
                                    for (int c = 0; c < outChannels; c++)
                                    {
                                        int srcCh = Math.Min(c, channels - 1);
                                        float sample = (sPtr[f * channels + srcCh] / 32768f) * micGain;
                                        if (!isLoopback)
                                            sample = (float)(Math.Tanh(sample * 1.12) / Math.Tanh(1.12));
                                        float abs = Math.Abs(sample);
                                        if (abs > peak) peak = abs;

                                        int s16 = (int)(Math.Clamp(sample, -1.0f, 1.0f) * 32767.0f);
                                        pcm16[outIdx++] = (byte)(s16 & 0xFF);
                                        pcm16[outIdx++] = (byte)((s16 >> 8) & 0xFF);
                                    }
                                }
                            }
                        }

                        releaseBuffer(captureClient, numFrames);
                        try
                        {
                            if (isLoopback) OnSystemPcm?.Invoke(pcm16, sampleRate, outChannels);
                            else OnMicPcm?.Invoke(pcm16, sampleRate, outChannels);
                        }
                        catch { }

                        double db = peak > 0.00001f ? Math.Clamp(20.0 * Math.Log10(peak), -60.0, 0.0) : -60.0;
                        if (isLoopback)
                            CurrentSystemDb = db;
                        else
                            CurrentMicDb = db;

                        if (_recording && !_paused && pcm16.Length > 0)
                        {
                            lock (_lock)
                            {
                                if (isLoopback && _sysWavWriter != null)
                                {
                                    _sysWavWriter.Write(pcm16);
                                    _sysBytesWritten += pcm16.Length;
                                }
                                else if (!isLoopback && _micWavWriter != null)
                                {
                                    _micWavWriter.Write(pcm16);
                                    _micBytesWritten += pcm16.Length;
                                    if (RecordedMicPeaks.Count < 20000)
                                        RecordedMicPeaks.Add(peak);
                                }
                            }
                        }

                        hr = getNextPacketSize(captureClient, out packetLength);
                        if (hr != 0) break;
                    }
                }

                var stopClient = Marshal.GetDelegateForFunctionPointer<StartStopDelegate>(vtblClient[11]);
                stopClient(audioClient);
            }
            catch { }
            finally
            {
                if (pwfx != IntPtr.Zero) CoTaskMemFree(pwfx);
                if (captureClient != IntPtr.Zero) Marshal.Release(captureClient);
                if (audioClient != IntPtr.Zero) Marshal.Release(audioClient);
                if (device != IntPtr.Zero) Marshal.Release(device);
                if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
            }
        }

        private void WriteSilenceFrames(bool isLoopback, int frames, int channels)
        {
            byte[] zeros = new byte[frames * channels * 2];
            lock (_lock)
            {
                if (isLoopback && _sysWavWriter != null)
                {
                    _sysWavWriter.Write(zeros);
                    _sysBytesWritten += zeros.Length;
                }
                else if (!isLoopback && _micWavWriter != null)
                {
                    _micWavWriter.Write(zeros);
                    _micBytesWritten += zeros.Length;
                    if (RecordedMicPeaks.Count < 20000)
                        RecordedMicPeaks.Add(0f);
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            StopRecordingWav();
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(IntPtr pvar);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OpenPropertyStoreDelegate(IntPtr self, int stgmAccess, out IntPtr ppProperties);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropStoreGetValueDelegate(IntPtr self, ref PROPERTYKEY key, IntPtr pv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAudioEndpointsDelegate(IntPtr self, int dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCountDelegate(IntPtr self, out uint pcDevices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ItemDelegate(IntPtr self, uint nDevice, out IntPtr ppDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr ppEndpoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetMasterVolumeLevelScalarDelegate(IntPtr self, float fLevel, ref Guid pguidEventContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetMuteDelegate(IntPtr self, int bMute, ref Guid pguidEventContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMixFormatDelegate(IntPtr self, out IntPtr ppDeviceFormat);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(IntPtr self, int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetServiceDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartStopDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNextPacketSizeDelegate(IntPtr self, out uint pNumFramesInNextPacket);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferDelegate(IntPtr self, out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseBufferDelegate(IntPtr self, uint numFramesRead);
    }
}

