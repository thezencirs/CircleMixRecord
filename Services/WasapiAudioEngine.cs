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

        private readonly object _lock = new();
        public List<float> RecordedMicPeaks { get; } = new();

        public void StartMonitoring()
        {
            if (_running) return;
            _running = true;

            _micThread = new Thread(() => CaptureLoop(isLoopback: false))
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
            bw.Write(36); // Placeholder
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
            bw.Write(0); // Placeholder
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

        private unsafe void CaptureLoop(bool isLoopback)
        {
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
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

                // IMMDeviceEnumerator::GetDefaultAudioEndpoint (slot 4)
                // dataFlow: 0 = eRender, 1 = eCapture; role: 0 = eConsole
                int dataFlow = isLoopback ? 0 : 1;
                IntPtr* vtblEnum = *(IntPtr**)enumerator;
                var getDefaultEndpoint = Marshal.GetDelegateForFunctionPointer<GetDefaultAudioEndpointDelegate>(vtblEnum[4]);
                hr = getDefaultEndpoint(enumerator, dataFlow, 0, out device);
                if (hr != 0 || device == IntPtr.Zero) return;

                // IMMDevice::Activate (slot 3)
                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                IntPtr* vtblDev = *(IntPtr**)device;
                var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(vtblDev[3]);
                hr = activate(device, ref iidAudioClient, 23, IntPtr.Zero, out audioClient);
                if (hr != 0 || audioClient == IntPtr.Zero) return;

                // IAudioClient::GetMixFormat (slot 8)
                IntPtr* vtblClient = *(IntPtr**)audioClient;
                var getMixFormat = Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(vtblClient[8]);
                hr = getMixFormat(audioClient, out pwfx);
                if (hr != 0 || pwfx == IntPtr.Zero) return;

                short channels = Marshal.ReadInt16(pwfx, 2);
                int sampleRate = Marshal.ReadInt32(pwfx, 4);
                short bitsPerSample = Marshal.ReadInt16(pwfx, 14);
                short formatTag = Marshal.ReadInt16(pwfx, 0);
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

                // IAudioClient::Initialize (slot 3)
                // AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000
                uint flags = isLoopback ? 0x00020000u : 0u;
                var initClient = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(vtblClient[3]);
                hr = initClient(audioClient, 0, flags, 10000000L, 0, pwfx, IntPtr.Zero);
                if (hr != 0) return;

                // IAudioClient::GetService (slot 14) -> IAudioCaptureClient
                Guid iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                var getService = Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(vtblClient[14]);
                hr = getService(audioClient, ref iidCapture, out captureClient);
                if (hr != 0 || captureClient == IntPtr.Zero) return;

                // IAudioClient::Start (slot 10)
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

                        // Loopback sessizken WAV senkronunu korumak için sıfır (sessizlik) paketi yaz
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
                        bool silentFlag = (bufFlags & 0x2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                        bool muted = isLoopback ? SystemMuted : MicMuted;

                        float peak = 0f;
                        byte[] pcm16 = new byte[numFrames * outChannels * 2];

                        if (!silentFlag && !muted && dataPtr != IntPtr.Zero && numFrames > 0)
                        {
                            if (isFloat)
                            {
                                float* fPtr = (float*)dataPtr;
                                int outIdx = 0;
                                for (uint f = 0; f < numFrames; f++)
                                {
                                    for (int c = 0; c < outChannels; c++)
                                    {
                                        int srcCh = Math.Min(c, channels - 1);
                                        float sample = fPtr[f * channels + srcCh];
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
                                        short s16 = sPtr[f * channels + srcCh];
                                        float abs = Math.Abs(s16 / 32768f);
                                        if (abs > peak) peak = abs;

                                        pcm16[outIdx++] = (byte)(s16 & 0xFF);
                                        pcm16[outIdx++] = (byte)((s16 >> 8) & 0xFF);
                                    }
                                }
                            }
                        }

                        releaseBuffer(captureClient, numFrames);

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

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr ppEndpoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

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
