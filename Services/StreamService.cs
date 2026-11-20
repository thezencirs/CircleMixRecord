// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CircleMixRecord.Services
{
    /// <summary>Tek yayın hedefi (Circle Çoklu Kanal RTMP).</summary>
    public sealed class StreamDestination
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string ServerUrl { get; set; } = "";
        public string KeyProtected { get; set; } = ""; // DPAPI (CurrentUser) ile şifreli, Base64

        [System.Text.Json.Serialization.JsonIgnore]
        public string Key
        {
            get => DpapiUnprotect(KeyProtected);
            set => KeyProtected = DpapiProtect(value ?? "");
        }

        public string BuildUrl()
        {
            string server = (ServerUrl ?? "").Trim();
            string key = (Key ?? "").Trim();
            if (string.IsNullOrEmpty(server)) return "";
            if (string.IsNullOrEmpty(key)) return server;
            return server.EndsWith("/") ? server + key : server + "/" + key;
        }

        // ---- Windows DPAPI (ek NuGet paketi gerektirmez) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static string DpapiProtect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = Encoding.UTF8.GetBytes(plain);
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var outBlob = new DATA_BLOB();
            try
            {
                Marshal.Copy(data, 0, inBlob.pbData, data.Length);
                if (!CryptProtectData(ref inBlob, "CircleStreamKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1 /*UI_FORBIDDEN*/, ref outBlob)) return "";
                byte[] enc = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, enc, 0, enc.Length);
                return Convert.ToBase64String(enc);
            }
            finally
            {
                Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static string DpapiUnprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(b64);
                var inBlob = new DATA_BLOB { cbData = enc.Length, pbData = Marshal.AllocHGlobal(enc.Length) };
                var outBlob = new DATA_BLOB();
                try
                {
                    Marshal.Copy(enc, 0, inBlob.pbData, enc.Length);
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob)) return "";
                    byte[] dec = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, dec, 0, dec.Length);
                    return Encoding.UTF8.GetString(dec);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBlob.pbData);
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            catch { return ""; }
        }
    }

    /// <summary>Yayın ayarları — %APPDATA%\CircleMixRecord\stream_settings.json (anahtarlar şifreli).</summary>
    public sealed class StreamSettings
    {
        public List<StreamDestination> Destinations { get; set; } = new();
        public int BitrateKbps { get; set; } = 6000;
        public int Height { get; set; } = 1080;      // 1080 veya 720
        public bool Vertical { get; set; } = false;  // true: 9:16 dikey yayın
        public int MicPercent { get; set; } = 100;
        public int DesktopPercent { get; set; } = 55;
        public string IntermissionImagePath { get; set; } = "";

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CircleMixRecord", "stream_settings.json");

        public static StreamSettings CreateDefault() => new()
        {
            Destinations = new List<StreamDestination>
            {
                new() { Name = "Circle Kanal 1", ServerUrl = "" },
                new() { Name = "Circle Kanal 2", ServerUrl = "" },
                new() { Name = "Circle Kanal 3", ServerUrl = "" },
                new() { Name = "Circle Özel RTMP", ServerUrl = "" }
            }
        };

        public static StreamSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(FilePath));
                    if (s != null && s.Destinations != null && s.Destinations.Count >= 4) return s;
                }
            }
            catch { }
            return CreateDefault();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>
    /// Canlı mikrofon + masaüstü sesini 48 kHz s16le stereo olarak karıştırıp FFmpeg'e adlandırılmış boru (named pipe) ile verir.
    /// Ses gelmezse sessizlik yazar (FFmpeg asla aç kalmaz → yayın donmaz), birikme olursa eski veriyi atar (gecikme sabit kalır).
    /// </summary>
    public sealed class StreamAudioMixer : IDisposable
    {
        private const int Rate = 48000;
        private readonly object _lock = new();
        private readonly Queue<float> _mic = new();   // stereo interleaved
        private readonly Queue<float> _sys = new();
        private NamedPipeServerStream _pipe;
        private Thread _thread;
        private volatile bool _running;
        public string PipePath { get; }
        public float MicGain { get; set; } = 1.0f;
        public float SysGain { get; set; } = 0.55f;

        public StreamAudioMixer()
        {
            string name = "cmr_audio_" + Guid.NewGuid().ToString("N");
            PipePath = @"\\.\pipe\" + name;
            _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        }

        public void PushMic(byte[] pcm16, int sampleRate, int channels) => Push(_mic, pcm16, sampleRate, channels);
        public void PushSystem(byte[] pcm16, int sampleRate, int channels) => Push(_sys, pcm16, sampleRate, channels);

        private void Push(Queue<float> q, byte[] pcm, int sampleRate, int channels)
        {
            if (!_running || pcm == null || pcm.Length < 4) return;
            channels = Math.Clamp(channels, 1, 2);
            int frames = pcm.Length / (2 * channels);
            if (frames <= 0) return;
            lock (_lock)
            {
                // Basit doğrusal yeniden örnekleme (cihaz 44.1k vb. ise)
                double step = sampleRate > 0 ? (double)sampleRate / Rate : 1.0;
                int outFrames = (int)(frames / step);
                for (int i = 0; i < outFrames; i++)
                {
                    double pos = i * step;
                    int i0 = Math.Min((int)pos, frames - 1);
                    int i1 = Math.Min(i0 + 1, frames - 1);
                    float t = (float)(pos - i0);
                    for (int c = 0; c < 2; c++)
                    {
                        int ch = Math.Min(c, channels - 1);
                        float a = BitConverter.ToInt16(pcm, (i0 * channels + ch) * 2) / 32768f;
                        float b = BitConverter.ToInt16(pcm, (i1 * channels + ch) * 2) / 32768f;
                        q.Enqueue(a + (b - a) * t);
                    }
                }
                // Gecikme sınırı: 250 ms'den fazla birikirse eskileri at
                int max = Rate * 2 / 4;
                while (q.Count > max) q.Dequeue();
            }
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(WriterLoop) { IsBackground = true, Name = "Circle_StreamAudio" };
            _thread.Start();
        }

        private void WriterLoop()
        {
            try
            {
                _pipe.WaitForConnection();
            }
            catch { return; }

            const int chunkFrames = 480; // 10 ms
            byte[] outBuf = new byte[chunkFrames * 4];
            var sw = Stopwatch.StartNew();
            long framesWritten = 0;
            while (_running)
            {
                // Gerçek zaman hızında yaz
                long due = (long)(sw.Elapsed.TotalSeconds * Rate);
                if (framesWritten >= due) { Thread.Sleep(2); continue; }

                lock (_lock)
                {
                    for (int i = 0; i < chunkFrames * 2; i++)
                    {
                        float m = _mic.Count > 0 ? _mic.Dequeue() : 0f;
                        float s = _sys.Count > 0 ? _sys.Dequeue() : 0f;
                        float v = m * MicGain + s * SysGain;
                        if (v > 0.98f) v = 0.98f; else if (v < -0.98f) v = -0.98f;
                        short sv = (short)(v * 32767f);
                        outBuf[i * 2] = (byte)(sv & 0xFF);
                        outBuf[i * 2 + 1] = (byte)((sv >> 8) & 0xFF);
                    }
                }
                try { _pipe.Write(outBuf, 0, outBuf.Length); }
                catch { break; }
                framesWritten += chunkFrames;
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }

    /// <summary>
    /// Çok platformlu canlı yayın oturumu: TEK kodlama (NVENC) → FFmpeg tee ile Circle Çoklu Yayın Kanallarına aynı anda.
    /// </summary>
    public sealed class StreamSession : IDisposable
    {
        private Process _proc;
        private StreamAudioMixer _audio;
        private readonly StringBuilder _errTail = new();
        private readonly Stopwatch _uptime = new();
        public int InputWidth { get; }
        public int InputHeight { get; }
        public bool Vertical { get; }
        public List<string> DestinationNames { get; } = new();
        public string LastStats { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public bool IsAlive => _proc != null && !_proc.HasExited;
        public TimeSpan Uptime => _uptime.Elapsed;
        public StreamAudioMixer Audio => _audio;

        public StreamSession(int inW, int inH, bool vertical)
        {
            InputWidth = inW; InputHeight = inH; Vertical = vertical;
        }

        public bool Start(StreamSettings s, HardwareEncoderInfo encoder, out string error)
        {
            error = "";
            var urls = new List<string>();
            foreach (var d in s.Destinations)
            {
                if (!d.Enabled) continue;
                string u = d.BuildUrl();
                bool isCustom = d.Name.StartsWith("Özel", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(d.Key) && !isCustom) { error = $"{d.Name}: yayın anahtarı boş."; return false; }
                if (string.IsNullOrWhiteSpace(u)) { error = $"{d.Name}: sunucu adresi boş."; return false; }
                if (!(u.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"{d.Name}: sunucu adresi rtmp:// veya rtmps:// ile başlamalı."; return false;
                }
                urls.Add(u);
                DestinationNames.Add(d.Name);
            }
            if (urls.Count == 0) { error = "En az bir yayın hedefi seçin ve anahtarını girin."; return false; }

            int outH = s.Height >= 1080 ? 1080 : 720;
            int outW = (int)Math.Round(outH * 16.0 / 9.0);
            if (Vertical) { int t = outW; outW = outH; outH = t; } // 1080x1920 / 720x1280
            int kbps = Math.Clamp(s.BitrateKbps, 1500, 12000);

            string codec = encoder?.CodecName ?? HardwareEncoderService.DetectBestEncoder().CodecName;
            string venc;
            if (codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                venc = $"-c:v h264_nvenc -preset p4 -tune hq -profile:v high -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g 60 -bf 2";
            else if (codec.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                venc = $"-c:v h264_qsv -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g 60";
            else if (codec.Contains("amf", StringComparison.OrdinalIgnoreCase))
                venc = $"-c:v h264_amf -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g 60";
            else if (codec.Contains("h264_mf", StringComparison.OrdinalIgnoreCase))
                venc = $"-c:v h264_mf -b:v {kbps}k -g 60";
            else if (codec.Contains("openh264", StringComparison.OrdinalIgnoreCase))
                venc = $"-c:v libopenh264 -b:v {kbps}k -maxrate {kbps}k -g 60";
            else
                venc = $"-c:v {codec} -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g 60";

            _audio = new StreamAudioMixer
            {
                MicGain = Math.Clamp(s.MicPercent, 0, 200) / 100f,
                SysGain = Math.Clamp(s.DesktopPercent, 0, 200) / 100f
            };
            _audio.Start();

            string output;
            if (urls.Count == 1)
                output = $"-f flv -flvflags no_duration_filesize \"{urls[0]}\"";
            else
                output = "-f tee \"" + string.Join("|", urls.Select(u => $"[f=flv:onfail=ignore:flvflags=no_duration_filesize]{u}")) + "\"";

            string args =
                "-hide_banner -loglevel warning -stats " +
                $"-thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt bgr24 -s {InputWidth}x{InputHeight} -i - " +
                $"-thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f s16le -ar 48000 -ac 2 -i \"{_audio.PipePath}\" " +
                "-map 0:v:0 -map 1:a:0 " +
                $"-vf \"scale={outW}:{outH}:flags=lanczos,format=yuv420p\" -r 30 -fps_mode cfr " +
                "-af \"aresample=async=1000\" " +
                $"{venc} -c:a aac -b:a 160k -ar 48000 -ac 2 " +
                output;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };
                _proc = Process.Start(psi);
                if (_proc == null) { error = "FFmpeg başlatılamadı."; return false; }
                var t = new Thread(ReadStderr) { IsBackground = true, Name = "Circle_StreamStats" };
                t.Start();
                _uptime.Restart();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void ReadStderr()
        {
            try
            {
                var sr = _proc.StandardError;
                var line = new StringBuilder();
                int ch;
                while ((ch = sr.Read()) >= 0)
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        if (line.Length > 0) HandleLine(line.ToString());
                        line.Clear();
                    }
                    else line.Append((char)ch);
                }
            }
            catch { }
        }

        private void HandleLine(string l)
        {
            if (l.StartsWith("frame=", StringComparison.Ordinal) || l.Contains(" bitrate="))
            {
                string fps = Field(l, "fps="), br = Field(l, "bitrate="), drop = Field(l, "drop="), dup = Field(l, "dup="), speed = Field(l, "speed=");
                LastStats = $"{fps} fps • {br} • düşen kare {(string.IsNullOrEmpty(drop) ? "0" : drop)} • hız {speed}";
                return;
            }
            lock (_errTail)
            {
                _errTail.AppendLine(l);
                if (_errTail.Length > 4000) _errTail.Remove(0, _errTail.Length - 4000);
            }
            if (l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Slave", StringComparison.OrdinalIgnoreCase) >= 0)
                LastError = l.Length > 220 ? l.Substring(0, 220) : l;
        }

        private static string Field(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            i += key.Length;
            while (i < line.Length && line[i] == ' ') i++;
            int j = i;
            while (j < line.Length && line[j] != ' ') j++;
            return line.Substring(i, j - i);
        }

        public void WriteFrame(byte[] raw)
        {
            if (!IsAlive || raw == null) return;
            try { _proc.StandardInput.BaseStream.Write(raw, 0, raw.Length); }
            catch { }
        }

        public string ErrorTail()
        {
            lock (_errTail) return _errTail.ToString();
        }

        public void Dispose()
        {
            _uptime.Stop();
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try { _proc.StandardInput.Close(); } catch { }
                    _audio?.Dispose();
                    if (!_proc.WaitForExit(4000)) _proc.Kill(true);
                }
            }
            catch { }
            finally
            {
                _audio?.Dispose();
                _proc?.Dispose();
                _proc = null;
            }
        }
    }
}

