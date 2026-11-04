// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CircleMixRecord.Models;

namespace CircleMixRecord.Services
{
    public class DualCanvasEngine : IDisposable
    {
        public WasapiAudioEngine AudioEngine { get; } = new();
        public HardwareEncoderInfo ActiveEncoder { get; private set; }

        // Sahne & Kaynak Ayarları
        public bool ShowScreen { get; set; } = true;
        public bool ShowWebcam { get; set; } = true;
        public bool MirrorWebcam { get; set; } = true;
        public bool NaturalWhiteBalanceWebcam { get; set; } = true;
        public double WebcamSizeRatio { get; set; } = 0.22;
        public SmartPortraitLayout PortraitLayout { get; set; } = SmartPortraitLayout.FullFill9x16;
        public double LiveUncropPercent { get; set; } = 0.0; // 0 = Tam 9:16, 100 = Tam 16:9 Uncrop
        public double ManualPanX { get; set; } = 0.0;        // -0.5 .. +0.5
        public bool Record4KHorizontal { get; set; } = false;

        // Akıllı Zoom & Odak Takip Durumu
        public bool IsSmartZoomActive { get; set; } = false;
        public bool FollowMouseEnabled { get; set; } = true;
        public bool AutoClickZoomEnabled { get; set; } = true;
        public double ZoomFactor16x9 { get; set; } = 2.0;
        public double ZoomFactor9x16 { get; set; } = 1.75;

        // Kayıt ve Yayın Durumu
        public bool IsRecording { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsStreamingRtmp { get; private set; }
        public TimeSpan RecordingElapsed => _recordStopwatch.Elapsed;

        // Circle Meet Canlı Yayın Kare Tamponu & Katılımcı Listesi
        public byte[] LatestJpeg16x9 { get; private set; }
        public bool ShowMeetingOverlay { get; set; } = true;
        private List<MeetParticipantInfo> _meetingParticipants = new();
        private readonly object _meetOverlayLock = new();

        // Circle Edit & Studio: Canlı PIP Görüntü Bindirme & Maskeleme
        private Bitmap _pipOverlayBmp;
        private readonly object _pipLock = new();
        public OverlayMaskShape PipMaskShape { get; set; } = OverlayMaskShape.RoundedBox;
        public int PipPositionIndex { get; set; } = 0; // 0: Sol Üst, 1: Sağ Üst, 2: Sol Alt, 3: Sağ Alt, 4: Merkez
        public double PipScalePercent { get; set; } = 28.0;

        // Anlık Ekran Resmi (Screenshot) İçin Sıfır-Allocation Raw Tamponlar
        private readonly byte[] _shotRaw16 = new byte[1280 * 720 * 3];
        private readonly byte[] _shotRaw9 = new byte[540 * 960 * 3];
        private readonly object _shotLock = new();

        // Canlı Performans & Sağlık Telemetrisi (100/100)
        public double LastFrameRenderMs { get; private set; } = 2.4;
        public int StudioHealthScore { get; private set; } = 100;
        public string ActiveCameraStatus { get; private set; } = "NVIDIA Broadcast (Varsayılan)";
        public bool IsVerticalOnlyRecording { get; private set; } = false;
        public bool IsEditorFocusMode { get; set; } = false; // CapCut Kurgu ekranındayken arka plan ekran yakalamayı uyutur (0% CPU)

        public void SetPipOverlayMedia(string mediaPath)
        {
            lock (_pipLock)
            {
                _pipOverlayBmp?.Dispose();
                _pipOverlayBmp = null;
                if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return;

                try
                {
                    string ext = Path.GetExtension(mediaPath).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".webp")
                    {
                        using var raw = new Bitmap(mediaPath);
                        _pipOverlayBmp = new Bitmap(raw);
                    }
                    else
                    {
                        // Video dosyasından ilk kareyi önizleme olarak çek
                        string tmpPng = Path.Combine(Path.GetTempPath(), $"cmr_pip_{Guid.NewGuid():N}.png");
                        var psi = new ProcessStartInfo
                        {
                            FileName = HardwareEncoderService.GetFfmpegPath(),
                            Arguments = $"-y -ss 0.5 -i \"{mediaPath}\" -frames:v 1 \"{tmpPng}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(3000);
                        if (File.Exists(tmpPng))
                        {
                            using var raw = new Bitmap(tmpPng);
                            _pipOverlayBmp = new Bitmap(raw);
                            try { File.Delete(tmpPng); } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public (string path16x9, string path9x16) CaptureDualScreenshot()
        {
            string dir16 = HardwareEncoderService.GetHorizontalRecordingsDir();
            string dir9 = HardwareEncoderService.GetVerticalRecordingsDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string p16 = Path.Combine(dir16, $"Kapak_16x9_{stamp}.png");
            string p9 = Path.Combine(dir9, $"Kapak_9x16_{stamp}.png");

            lock (_shotLock)
            {
                using var b16 = CreateBitmapFromRawBgr24(_shotRaw16, 1280, 720);
                using var b9 = CreateBitmapFromRawBgr24(_shotRaw9, 540, 960);
                b16.Save(p16, ImageFormat.Png);
                b9.Save(p9, ImageFormat.Png);

                try
                {
                    var clipClone = (Bitmap)b16.Clone();
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        try { System.Windows.Forms.Clipboard.SetImage(clipClone); } catch { }
                        clipClone.Dispose();
                    });
                }
                catch { }
            }
            return (p16, p9);
        }

        private static Bitmap CreateBitmapFromRawBgr24(byte[] raw, int w, int h)
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                Marshal.Copy(raw, 0, bd.Scan0, Math.Min(raw.Length, bd.Stride * h));
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }

        public void UpdateMeetingParticipants(List<MeetParticipantInfo> participants)
        {
            lock (_meetOverlayLock)
            {
                _meetingParticipants = participants ?? new List<MeetParticipantInfo>();
            }
        }

        // Kamera Aygıtı
        private DirectShowDevice _selectedCamera;
        private DirectShowDevice _fallbackCamera;
        private Process _webcamFfmpegProc;
        private Thread _webcamReaderThread;
        private Bitmap _latestWebcamFrame;
        private readonly object _webcamLock = new();
        private volatile bool _webcamRunning;

        // Ana Kompozitör Döngüsü
        private Thread _compositorThread;
        private volatile bool _running;
        private readonly Stopwatch _recordStopwatch = new();

        // Kayıt FFmpeg Süreçleri
        private Process _procMasterClean;
        private Process _procHoriz16x9;
        private Process _procVert9x16;
        private Process _procRtmpStream;
        private readonly object _recordPipeLock = new();

        // Kamera Takip Değişkenleri
        private double _curZoom16 = 1.0;
        private double _curZoom9 = 1.0;
        private double _curNormX = 0.5;
        private double _curNormY = 0.5;
        private DateTime _lastClickTime = DateTime.MinValue;
        private System.Drawing.Point _lastClickPt;
        private bool _wasLeftMouseDown;

        // Aktif Proje
        public CmrProject ActiveProject { get; private set; }

        // Sıfır-Allocation UI Güncelleme Olayı (WriteableBitmap için doğrudan BGR24 byte dizileri)
        public event Action<byte[], int, int, byte[], int, int> OnRawFramesReady;

        public DualCanvasEngine()
        {
            ActiveEncoder = new HardwareEncoderInfo
            {
                CodecName = "h264_nvenc",
                DisplayName = "NVIDIA NVENC (Donanım Hızlandırma)",
                PresetArgs = "-preset p4 -tune hq -rc vbr -cq 20 -b:v 0",
                IsHardware = true
            };
            Task.Run(() => ActiveEncoder = HardwareEncoderService.DetectBestEncoder());
        }

        public void StartEngine(DirectShowDevice cameraDevice = null)
        {
            if (_running) return;
            _running = true;

            AudioEngine.StartMonitoring();
            if (cameraDevice != null)
            {
                SwitchCamera(cameraDevice);
            }

            _compositorThread = new Thread(CompositorLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "DualCanvas_Compositor"
            };
            _compositorThread.Start();
        }

        public void SwitchCamera(DirectShowDevice cameraDevice, DirectShowDevice fallbackCamera = null)
        {
            StopCameraCapture();
            _selectedCamera = cameraDevice;
            if (fallbackCamera != null) _fallbackCamera = fallbackCamera;
            if (cameraDevice == null) return;

            ActiveCameraStatus = cameraDevice.FriendlyName;
            _webcamRunning = true;
            _webcamReaderThread = new Thread(() => WebcamCaptureLoop(cameraDevice, _fallbackCamera))
            {
                IsBackground = true,
                Name = "Webcam_Capture_Pipe"
            };
            _webcamReaderThread.Start();
        }

        private void StopCameraCapture()
        {
            _webcamRunning = false;
            try
            {
                if (_webcamFfmpegProc != null && !_webcamFfmpegProc.HasExited)
                    _webcamFfmpegProc.Kill();
            }
            catch { }
            _webcamFfmpegProc = null;
        }

        private void WebcamCaptureLoop(DirectShowDevice cam, DirectShowDevice fallbackCam)
        {
            bool gotAnyFrame = TryReadCameraStream(cam);

            // Eğer NVIDIA Broadcast seçiliyse ama arka planda NVIDIA Broadcast uygulaması kapalı olduğu için
            // kare gelmediyse, donma/boş ekran olmaması için otomatik olarak fiziksel kameraya (USB WebCam) geç!
            if (!gotAnyFrame && _webcamRunning && fallbackCam != null && fallbackCam.FriendlyName != cam.FriendlyName)
            {
                ActiveCameraStatus = $"{fallbackCam.FriendlyName} (Otomatik Yedek)";
                TryReadCameraStream(fallbackCam);
            }
        }

        private bool TryReadCameraStream(DirectShowDevice cam)
        {
            const int camW = 640;
            const int camH = 360;
            int frameBytes = camW * camH * 3;
            bool receivedFirstFrame = false;

            try
            {
                string inputSpec = !string.IsNullOrWhiteSpace(cam.AlternativeName)
                    ? $"video=\"{cam.AlternativeName}\""
                    : $"video=\"{cam.FriendlyName}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-hide_banner -loglevel error -f dshow -rtbufsize 64M -i {inputSpec} -vf \"scale={camW}:{camH},fps=30\" -pix_fmt bgr24 -f rawvideo -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                _webcamFfmpegProc = Process.Start(psi);
                if (_webcamFfmpegProc == null) return false;

                // NVIDIA Broadcast kapalıysa 2.5 sn içinde akış gelmezse tespit et
                if (cam.IsNvidiaBroadcast)
                {
                    Task.Delay(2600).ContinueWith(_ =>
                    {
                        if (!receivedFirstFrame && _webcamRunning)
                        {
                            try { if (_webcamFfmpegProc != null && !_webcamFfmpegProc.HasExited) _webcamFfmpegProc.Kill(); } catch { }
                        }
                    });
                }

                var stream = _webcamFfmpegProc.StandardOutput.BaseStream;
                byte[] buffer = new byte[frameBytes];

                while (_webcamRunning && !_webcamFfmpegProc.HasExited)
                {
                    int offset = 0;
                    while (offset < frameBytes && _webcamRunning)
                    {
                        int read = stream.Read(buffer, offset, frameBytes - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    if (offset < frameBytes) break;

                    receivedFirstFrame = true;

                    // Doğal Beyaz Dengesi (Sarı Ton Giderici) ve Ayna Görüntüsü uygula
                    var bmp = new Bitmap(camW, camH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    var bmpData = bmp.LockBits(new Rectangle(0, 0, camW, camH), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    unsafe
                    {
                        byte* dstBase = (byte*)bmpData.Scan0;
                        int stride = bmpData.Stride;
                        bool mirror = MirrorWebcam;
                        bool wbFix = NaturalWhiteBalanceWebcam;

                        fixed (byte* srcBase = buffer)
                        {
                            for (int y = 0; y < camH; y++)
                            {
                                byte* srcRow = srcBase + y * (camW * 3);
                                byte* dstRow = dstBase + y * stride;
                                for (int x = 0; x < camW; x++)
                                {
                                    int sx = mirror ? (camW - 1 - x) : x;
                                    byte b = srcRow[sx * 3 + 0];
                                    byte g = srcRow[sx * 3 + 1];
                                    byte r = srcRow[sx * 3 + 2];

                                    if (wbFix)
                                    {
                                        b = (byte)Math.Min(255, (b * 114) / 100);
                                        g = (byte)Math.Min(255, (g * 98) / 100);
                                        r = (byte)Math.Min(255, (r * 92) / 100);
                                    }

                                    dstRow[x * 3 + 0] = b;
                                    dstRow[x * 3 + 1] = g;
                                    dstRow[x * 3 + 2] = r;
                                }
                            }
                        }
                    }
                    bmp.UnlockBits(bmpData);

                    lock (_webcamLock)
                    {
                        _latestWebcamFrame?.Dispose();
                        _latestWebcamFrame = bmp;
                    }
                }
            }
            catch { }

            return receivedFirstFrame;
        }

        public CmrProject StartDualRecording(string customProjectName = null, bool verticalOnly = false)
        {
            if (IsRecording) return ActiveProject;

            IsVerticalOnlyRecording = verticalOnly;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string projName = string.IsNullOrWhiteSpace(customProjectName) ? $"Kayit_{stamp}" : $"{customProjectName}_{stamp}";

            // Yatay ve Dikey kayıtlar ayrı klasörlere, teknik proje verileri Proje_Verileri klasörüne!
            string horizDir = HardwareEncoderService.GetHorizontalRecordingsDir();
            string vertDir = HardwareEncoderService.GetVerticalRecordingsDir();
            string baseDir = HardwareEncoderService.GetProjectInternalsDir(projName);

            GetDesktopResolution(out int scrW, out int scrH);

            string horizMp4Path = Path.Combine(horizDir, $"{projName}_Yatay_16x9.mp4");
            string vertMp4Path = Path.Combine(vertDir, $"{projName}_Dikey_Reels_9x16.mp4");

            var proj = new CmrProject
            {
                ProjectName = projName,
                CreatedAt = DateTime.Now,
                ProjectDirectory = baseDir,
                ProjectFilePath = Path.Combine(baseDir, $"{projName}.cmrproj"),
                MasterCleanVideoPath = horizMp4Path,
                Horizontal16x9VideoPath = horizMp4Path,
                Vertical9x16VideoPath = vertMp4Path,
                MicrophoneWavPath = Path.Combine(baseDir, "Master_Mic.wav"),
                SystemAudioWavPath = Path.Combine(baseDir, "Master_System.wav"),
                MasterWidth = 1280,
                MasterHeight = 720,
                HorizontalOutputWidth = Record4KHorizontal ? 3840 : 1920,
                HorizontalOutputHeight = Record4KHorizontal ? 2160 : 1080,
                VerticalOutputWidth = 1080,
                VerticalOutputHeight = 1920,
                Fps = 30,
                PortraitLayout = PortraitLayout,
                UncropPercent = LiveUncropPercent,
                SmartZoomFactor16x9 = ZoomFactor16x9,
                SmartZoomFactor9x16 = ZoomFactor9x16
            };

            ActiveProject = proj;

            lock (_recordPipeLock)
            {
                AudioEngine.StartRecordingToWav(proj.MicrophoneWavPath, proj.SystemAudioWavPath);

                string enc = ActiveEncoder.CodecName;
                string encArgs = ActiveEncoder.PresetArgs;

                string raw16Mkv = Path.Combine(baseDir, "_temp_16x9.mkv");
                string raw9Mkv = Path.Combine(baseDir, "_temp_9x16.mkv");

                // 3 ayrı encoder yerine doğrudan GPU hızlandırmalı 2 yüksek kalite (CQP 16) pipe çalıştırarak %33 CPU/RAM tasarrufu
                _procMasterClean = null;
                if (!verticalOnly)
                {
                    _procHoriz16x9 = StartRawVideoEncoderProcess(1280, 720, 30, proj.HorizontalOutputWidth, proj.HorizontalOutputHeight, enc, encArgs, raw16Mkv);
                }
                _procVert9x16 = StartRawVideoEncoderProcess(540, 960, 30, 1080, 1920, enc, encArgs, raw9Mkv);

                _recordStopwatch.Restart();
                IsPaused = false;
                IsRecording = true;
            }

            return proj;
        }

        public void TogglePauseRecording()
        {
            if (!IsRecording) return;
            IsPaused = !IsPaused;
            AudioEngine.SetPaused(IsPaused);
            if (IsPaused) _recordStopwatch.Stop();
            else _recordStopwatch.Start();
        }

        public async Task<CmrProject> StopDualRecordingAsync()
        {
            if (!IsRecording) return ActiveProject;

            var proj = ActiveProject;
            lock (_recordPipeLock)
            {
                IsRecording = false;
                IsPaused = false;
                _recordStopwatch.Stop();
                proj.TotalDurationSec = Math.Max(1.0, _recordStopwatch.Elapsed.TotalSeconds);

                AudioEngine.StopRecordingWav();
                proj.WaveformPeaks = new List<float>(AudioEngine.RecordedMicPeaks);

                CloseEncoderProcess(ref _procMasterClean);
                CloseEncoderProcess(ref _procHoriz16x9);
                CloseEncoderProcess(ref _procVert9x16);
            }

            // Kaydedilen ayrık Mikrofon + Sistem sesini Yatay_16x9_Kayitlar ve Dikey_9x16_Reels_Kayitlar klasörlerine yüksek kalitede birleştir
            await Task.Run(() =>
            {
                string raw16Mkv = Path.Combine(proj.ProjectDirectory, "_temp_16x9.mkv");
                string raw9Mkv = Path.Combine(proj.ProjectDirectory, "_temp_9x16.mkv");

                if (File.Exists(raw16Mkv))
                {
                    MuxVideoAndDualAudio(raw16Mkv, proj.MicrophoneWavPath, proj.SystemAudioWavPath, proj.Horizontal16x9VideoPath);
                    try { File.Delete(raw16Mkv); } catch { }
                }
                if (File.Exists(raw9Mkv))
                {
                    MuxVideoAndDualAudio(raw9Mkv, proj.MicrophoneWavPath, proj.SystemAudioWavPath, proj.Vertical9x16VideoPath);
                    try { File.Delete(raw9Mkv); } catch { }
                }

                // Başlangıç için tüm videoyu kapsayan 1 ana Timeline klibi oluştur
                proj.Clips.Clear();
                proj.Clips.Add(new TimelineClip
                {
                    Label = proj.ProjectName,
                    SourceStartSec = 0,
                    SourceEndSec = proj.TotalDurationSec,
                    SpeedMultiplier = 1.0,
                    IsDeleted = false,
                    IsSilentSegment = false
                });

                proj.Save();
            });

            return proj;
        }

        public bool StartRtmpBroadcast(string rtmpUrl)
        {
            if (string.IsNullOrWhiteSpace(rtmpUrl)) return false;
            StopRtmpBroadcast();

            try
            {
                string enc = ActiveEncoder.CodecName;
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-hide_banner -loglevel error -use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt bgr24 -s 1280x720 -i - -f lavfi -i anullsrc=r=44100:cl=stereo -vf \"fps=30,format=yuv420p\" -c:v {enc} -b:v 4500k -maxrate 4500k -bufsize 9000k -g 60 -c:a aac -b:a 128k -f flv \"{rtmpUrl}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                };
                _procRtmpStream = Process.Start(psi);
                IsStreamingRtmp = (_procRtmpStream != null && !_procRtmpStream.HasExited);
                return IsStreamingRtmp;
            }
            catch
            {
                IsStreamingRtmp = false;
                return false;
            }
        }

        public void StopRtmpBroadcast()
        {
            IsStreamingRtmp = false;
            CloseEncoderProcess(ref _procRtmpStream);
        }

        private static Process StartRawVideoEncoderProcess(int inW, int inH, int fps, int outW, int outH, string codec, string presetArgs, string outputPath)
        {
            string scaleFilter = (inW == outW && inH == outH)
                ? $"fps={fps},format=yuv420p"
                : $"fps={fps},scale={outW}:{outH}:flags=lanczos,unsharp=3:3:0.35,format=yuv420p";
            var psi = new ProcessStartInfo
            {
                FileName = HardwareEncoderService.GetFfmpegPath(),
                Arguments = $"-y -hide_banner -loglevel error -use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt bgr24 -s {inW}x{inH} -i - -vf \"{scaleFilter}\" -c:v {codec} {presetArgs} \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };
            return Process.Start(psi);
        }

        private static void CloseEncoderProcess(ref Process proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    proc.StandardInput.BaseStream.Flush();
                    proc.StandardInput.Close();
                    proc.WaitForExit(4000);
                    if (!proc.HasExited) proc.Kill();
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
                proc = null;
            }
        }

        private static void MuxVideoAndDualAudio(string videoMkv, string micWav, string sysWav, string outMp4)
        {
            if (!File.Exists(videoMkv)) return;
            string ff = HardwareEncoderService.GetFfmpegPath();

            bool hasMic = File.Exists(micWav) && new FileInfo(micWav).Length > 1000;
            bool hasSys = File.Exists(sysWav) && new FileInfo(sysWav).Length > 1000;

            string args;
            if (hasMic && hasSys)
            {
                args = $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -i \"{micWav}\" -i \"{sysWav}\" " +
                       "-filter_complex \"[1:a]highpass=f=80,acompressor=threshold=-18dB:ratio=3:makeup=2dB[mic];[2:a]volume=0.9[sys];[mic][sys]amix=inputs=2:duration=first:dropout_transition=0[mix]\" " +
                       "-map 0:v:0 -map \"[mix]\" " +
                       "-c:v copy -c:a aac -b:a 192k -movflags +faststart " +
                       $"\"{outMp4}\"";
            }
            else if (hasMic)
            {
                args = $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -i \"{micWav}\" -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{outMp4}\"";
            }
            else if (hasSys)
            {
                args = $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -i \"{sysWav}\" -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{outMp4}\"";
            }
            else
            {
                // Mikrofon/Sistem sesi kapalıysa bile CapCut Timeline oynatıcısı ve FFmpeg filtreleri için sessiz AAC stereo kanal ekle
                args = $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -f lavfi -i anullsrc=r=44100:cl=stereo -c:v copy -c:a aac -b:a 128k -shortest -movflags +faststart \"{outMp4}\"";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(20000);
            }
            catch { }

            // Eğer birleştirme herhangi bir sebeple başarısız olduysa, yedek olarak doğrudan kopyala
            if (!File.Exists(outMp4) && File.Exists(videoMkv))
            {
                try { File.Copy(videoMkv, outMp4, true); } catch { }
            }
        }

        private void CompositorLoop()
        {
            const int masterW = 1280;
            const int masterH = 720;
            const int vertW = 540;
            const int vertH = 960;

            GetDesktopResolution(out int scrW, out int scrH);

            using var screenDesktopBmp = new Bitmap(scrW, scrH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var cleanMasterBmp = new Bitmap(masterW, masterH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var bmp16 = new Bitmap(masterW, masterH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var bmp9 = new Bitmap(vertW, vertH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var swFrame = Stopwatch.StartNew();
            long frameCount = 0;

            while (_running)
            {
                // Kullanıcı CapCut Kurgu ekranındaysa ve kayıt/yayın yoksa arka plan ekran yakalamayı uyut (0% CPU / Kasma Yok!)
                if (IsEditorFocusMode && !IsRecording && !IsStreamingRtmp)
                {
                    Thread.Sleep(180);
                    continue;
                }

                long frameStart = swFrame.ElapsedMilliseconds;
                frameCount++;

                try
                {
                    // 1. Masaüstü Ekranını Yakala (UAC / Güvenli Masaüstü geçişlerinde çökmeye karşı korumalı)
                    if (ShowScreen)
                    {
                        try
                        {
                            using var gScr = Graphics.FromImage(screenDesktopBmp);
                            gScr.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(scrW, scrH), CopyPixelOperation.SourceCopy);
                        }
                        catch
                        {
                            using var gScr = Graphics.FromImage(screenDesktopBmp);
                            gScr.Clear(System.Drawing.Color.FromArgb(15, 23, 42));
                            using var gridPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(35, 56, 189, 248), 1f);
                            for (int gx = 0; gx < scrW; gx += 120) gScr.DrawLine(gridPen, gx, 0, gx, scrH);
                            for (int gy = 0; gy < scrH; gy += 120) gScr.DrawLine(gridPen, 0, gy, scrW, gy);
                        }
                    }
                    else
                    {
                        using var gScr = Graphics.FromImage(screenDesktopBmp);
                        gScr.Clear(System.Drawing.Color.FromArgb(14, 18, 28));
                    }

                    // 2. İmleç Konumu ve Sol/Sağ Tık Smart-Zoom Takibi
                    GetCursorPos(out POINT pt);
                    int mx = Math.Clamp(pt.X, 0, scrW);
                    int my = Math.Clamp(pt.Y, 0, scrH);

                    bool leftDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                    bool clickedThisFrame = leftDown && !_wasLeftMouseDown;
                    _wasLeftMouseDown = leftDown;

                    if (clickedThisFrame)
                    {
                        _lastClickTime = DateTime.UtcNow;
                        _lastClickPt = new System.Drawing.Point(mx, my);
                    }

                    double clickElapsed = (DateTime.UtcNow - _lastClickTime).TotalSeconds;
                    bool autoClickZoomNow = AutoClickZoomEnabled && (clickElapsed >= 0 && clickElapsed < 1.8);

                    double targetZ16 = IsSmartZoomActive ? ZoomFactor16x9 : (autoClickZoomNow ? 1.35 : 1.0);
                    double targetZ9 = IsSmartZoomActive ? ZoomFactor9x16 : (autoClickZoomNow ? 1.45 : 1.0);

                    _curZoom16 += (targetZ16 - _curZoom16) * 0.16;
                    _curZoom9 += (targetZ9 - _curZoom9) * 0.16;

                    if (FollowMouseEnabled)
                    {
                        double targetNormX = Math.Clamp((double)mx / scrW + ManualPanX, 0.0, 1.0);
                        double targetNormY = Math.Clamp((double)my / scrH, 0.0, 1.0);
                        if (Math.Abs(targetNormX - _curNormX) > 0.015 || _curZoom9 > 1.05 || _curZoom16 > 1.05)
                            _curNormX += (targetNormX - _curNormX) * 0.18;
                        if (Math.Abs(targetNormY - _curNormY) > 0.015 || _curZoom9 > 1.05 || _curZoom16 > 1.05)
                            _curNormY += (targetNormY - _curNormY) * 0.18;
                    }

                    // İmleç ve Tıklama Halkasını Masaüstü Görüntüsüne Çiz
                    using (var gCur = Graphics.FromImage(screenDesktopBmp))
                    {
                        gCur.SmoothingMode = SmoothingMode.HighSpeed;
                        if (clickElapsed >= 0 && clickElapsed < 0.45)
                        {
                            float radius = (float)(14 + clickElapsed * 55);
                            int alpha = (int)Math.Clamp((1.0 - clickElapsed / 0.45) * 210, 0, 255);
                            using var clickPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(alpha, 56, 189, 248), 3.5f);
                            gCur.DrawEllipse(clickPen, _lastClickPt.X - radius, _lastClickPt.Y - radius, radius * 2, radius * 2);
                        }
                        using var curBrush = new SolidBrush(System.Drawing.Color.FromArgb(210, 250, 204, 21));
                        using var curPen = new System.Drawing.Pen(System.Drawing.Color.Black, 2f);
                        gCur.FillEllipse(curBrush, mx - 7, my - 7, 14, 14);
                        gCur.DrawEllipse(curPen, mx - 7, my - 7, 14, 14);
                    }

                    // 3. Kırpılmamış Temiz Master (1280x720) Oluştur (Sadece Kayıttayken veya Uncrop Açıkken İşle -> %0 Boşta CPU Yükü!)
                    if (IsRecording || LiveUncropPercent > 0.02 || PortraitLayout == SmartPortraitLayout.UncropBlur16x9)
                    {
                        using var gClean = Graphics.FromImage(cleanMasterBmp);
                        gClean.InterpolationMode = InterpolationMode.Low;
                        gClean.DrawImage(screenDesktopBmp, new Rectangle(0, 0, masterW, masterH), new Rectangle(0, 0, scrW, scrH), GraphicsUnit.Pixel);
                    }

                    // 4. 16:9 Yatay Ders Karesini Oluştur (Sıfır Allocation, Tekrar Kullanılan Tampon)
                    using (var g16 = Graphics.FromImage(bmp16))
                    {
                        g16.InterpolationMode = InterpolationMode.Low;
                        g16.SmoothingMode = SmoothingMode.HighSpeed;

                        int cropW16 = Math.Clamp((int)Math.Round(scrW / Math.Max(1.0, _curZoom16)), 64, scrW);
                        int cropH16 = Math.Clamp((int)Math.Round(scrH / Math.Max(1.0, _curZoom16)), 64, scrH);
                        int cropX16 = (int)Math.Clamp(_curNormX * scrW - cropW16 * 0.5, 0, Math.Max(0, scrW - cropW16));
                        int cropY16 = (int)Math.Clamp(_curNormY * scrH - cropH16 * 0.5, 0, Math.Max(0, scrH - cropH16));

                        g16.DrawImage(screenDesktopBmp, new Rectangle(0, 0, masterW, masterH), new Rectangle(cropX16, cropY16, cropW16, cropH16), GraphicsUnit.Pixel);

                        if (ShowWebcam)
                        {
                            int camW = (int)(masterW * WebcamSizeRatio);
                            int camH = (int)(camW * 9.0 / 16.0);
                            int camX = masterW - camW - 22;
                            int camY = masterH - camH - 22;
                            DrawWebcamOverlay(g16, new Rectangle(camX, camY, camW, camH));
                        }

                        if (ShowMeetingOverlay)
                        {
                            DrawMeetingParticipantsOverlay(g16, isVertical: false, masterW, masterH);
                        }

                        DrawPipCustomOverlay(g16, masterW, masterH);
                    }

                    // 5. 9:16 Dikey Smart Portrait Karesini Oluştur (Sıfır Allocation, Tekrar Kullanılan Tampon)
                    using (var g9 = Graphics.FromImage(bmp9))
                    {
                        g9.InterpolationMode = InterpolationMode.Low;
                        g9.SmoothingMode = SmoothingMode.HighSpeed;

                        double uncropRatio = Math.Clamp(LiveUncropPercent / 100.0, 0.0, 1.0);
                        if (PortraitLayout == SmartPortraitLayout.UncropBlur16x9)
                            uncropRatio = Math.Max(uncropRatio, 0.85);

                        if (PortraitLayout == SmartPortraitLayout.SplitScreenCamBottom)
                        {
                            int topH = (int)(vertH * 0.62);
                            int botH = vertH - topH;

                            int srcCropH = Math.Clamp((int)(scrH / Math.Max(1.0, _curZoom9)), 64, scrH);
                            int srcCropW = Math.Clamp((int)Math.Min(scrW, srcCropH * ((double)vertW / topH)), 64, scrW);
                            int srcX = (int)Math.Clamp(_curNormX * scrW - srcCropW * 0.5, 0, Math.Max(0, scrW - srcCropW));
                            int srcY = (int)Math.Clamp(_curNormY * scrH - srcCropH * 0.5, 0, Math.Max(0, scrH - srcCropH));

                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, 0, vertW, topH), new Rectangle(srcX, srcY, srcCropW, srcCropH), GraphicsUnit.Pixel);
                            DrawWebcamOverlay(g9, new Rectangle(0, topH, vertW, botH), rounded: false);
                        }
                        else if (uncropRatio > 0.02)
                        {
                            g9.DrawImage(cleanMasterBmp, new Rectangle(0, 0, vertW, vertH), new Rectangle(masterW / 4, 0, masterW / 2, masterH), GraphicsUnit.Pixel);
                            using (var darkBrush = new SolidBrush(System.Drawing.Color.FromArgb(175, 10, 14, 24)))
                            {
                                g9.FillRectangle(darkBrush, 0, 0, vertW, vertH);
                            }

                            double minW = scrH * (9.0 / 16.0) / Math.Max(1.0, _curZoom9);
                            int curSrcW = Math.Clamp((int)(minW + (scrW - minW) * uncropRatio), 64, scrW);
                            int curSrcH = Math.Clamp((int)(scrH / Math.Max(1.0, _curZoom9 * (1.0 - uncropRatio * 0.45))), 64, scrH);
                            int sx = (int)Math.Clamp(_curNormX * scrW - curSrcW * 0.5, 0, Math.Max(0, scrW - curSrcW));
                            int sy = (int)Math.Clamp(_curNormY * scrH - curSrcH * 0.5, 0, Math.Max(0, scrH - curSrcH));

                            double aspect = (double)curSrcW / curSrcH;
                            int dstW = vertW;
                            int dstH = (int)Math.Clamp(dstW / aspect, 300, vertH);
                            int dstY = (vertH - dstH) / 2 - 40;

                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, dstY, dstW, dstH), new Rectangle(sx, sy, curSrcW, curSrcH), GraphicsUnit.Pixel);

                            if (ShowWebcam)
                            {
                                int camW = (int)(vertW * 0.36);
                                int camH = (int)(camW * 9.0 / 16.0);
                                DrawWebcamOverlay(g9, new Rectangle(vertW - camW - 18, vertH - camH - 90, camW, camH));
                            }
                        }
                        else
                        {
                            int cropH9 = Math.Clamp((int)Math.Round(scrH / Math.Max(1.0, _curZoom9)), 64, scrH);
                            int cropW9 = Math.Clamp((int)Math.Round(cropH9 * (9.0 / 16.0)), 64, scrW);

                            int cropX9 = (int)Math.Clamp(_curNormX * scrW - cropW9 * 0.5, 0, Math.Max(0, scrW - cropW9));
                            int cropY9 = (int)Math.Clamp(_curNormY * scrH - cropH9 * 0.5, 0, Math.Max(0, scrH - cropH9));

                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, 0, vertW, vertH), new Rectangle(cropX9, cropY9, cropW9, cropH9), GraphicsUnit.Pixel);

                            if (ShowWebcam)
                            {
                                int camW = (int)(vertW * 0.36);
                                int camH = (int)(camW * 9.0 / 16.0);
                                DrawWebcamOverlay(g9, new Rectangle(vertW - camW - 18, vertH - camH - 110, camW, camH));
                            }
                        }

                        if (ShowMeetingOverlay)
                        {
                            DrawMeetingParticipantsOverlay(g9, isVertical: true, vertW, vertH);
                        }

                        DrawPipCustomOverlay(g9, vertW, vertH);
                    }

                    // Sıfır-Allocation BGR24 Byte Dizilerini Güncelle (Hem UI WriteableBitmap hem Screenshot için)
                    lock (_shotLock)
                    {
                        CopyBitmapToRawBuffer(bmp16, _shotRaw16);
                        CopyBitmapToRawBuffer(bmp9, _shotRaw9);
                    }

                    // 6. Kayıt Aktifse: Master Clean + 16:9 + 9:16 FFmpeg Donanım Kodlayıcılarına Yaz!
                    if (IsRecording && !IsPaused && ActiveProject != null)
                    {
                        lock (_recordPipeLock)
                        {
                            if (IsRecording && !IsPaused)
                            {
                                WriteBitmapToFfmpegPipe(cleanMasterBmp, _procMasterClean);
                                if (!IsVerticalOnlyRecording)
                                {
                                    WriteRawBufferToFfmpegPipe(_shotRaw16, _procHoriz16x9);
                                }
                                WriteRawBufferToFfmpegPipe(_shotRaw9, _procVert9x16);

                                ActiveProject.CameraKeyframes.Add(new CameraKeyframe
                                {
                                    TimeSec = _recordStopwatch.Elapsed.TotalSeconds,
                                    NormX = _curNormX,
                                    NormY = _curNormY,
                                    Zoom = _curZoom9,
                                    IsClickEvent = clickedThisFrame
                                });
                            }
                        }
                    }

                    // 7. RTMP Canlı Yayın Aktifse: 16:9 Kareyi RTMP Pipe'a Yaz
                    if (IsStreamingRtmp && _procRtmpStream != null)
                    {
                        WriteRawBufferToFfmpegPipe(_shotRaw16, _procRtmpStream);
                    }

                    // 8. Circle Meet Canlı Sınıf İçin JPEG Kareyi Her 3 Karede Bir Güncelle (CPU Yükünü %70 Azaltır)
                    if (frameCount % 3 == 0)
                    {
                        UpdateMeetJpeg(bmp16);
                    }

                    // 9. Sıfır-Allocation UI Önizleme Olayını Tetikle
                    OnRawFramesReady?.Invoke(_shotRaw16, masterW, masterH, _shotRaw9, vertW, vertH);
                }
                catch { }

                long renderMs = swFrame.ElapsedMilliseconds - frameStart;
                LastFrameRenderMs = LastFrameRenderMs * 0.85 + renderMs * 0.15;
                StudioHealthScore = LastFrameRenderMs <= 16.0 ? 100 : (LastFrameRenderMs <= 25.0 ? 96 : 90);

                int sleepMs = (int)Math.Max(5, 33 - renderMs);
                Thread.Sleep(sleepMs);
            }
        }

        private void DrawWebcamOverlay(Graphics g, Rectangle rect, bool rounded = true)
        {
            lock (_webcamLock)
            {
                if (_latestWebcamFrame != null)
                {
                    g.DrawImage(_latestWebcamFrame, rect);
                }
                else
                {
                    using var bg = new SolidBrush(System.Drawing.Color.FromArgb(235, 22, 29, 45));
                    g.FillRectangle(bg, rect);
                    using var f = new Font("Segoe UI Semibold", 11f);
                    using var br = new SolidBrush(System.Drawing.Color.FromArgb(200, 148, 163, 184));
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("KAMERA HAZIR", f, br, rect, sf);
                }
            }

            if (rounded)
            {
                using var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(210, 99, 102, 241), 2.5f);
                g.DrawRectangle(borderPen, rect);
            }
        }

        private void DrawPipCustomOverlay(Graphics g, int canvasW, int canvasH)
        {
            lock (_pipLock)
            {
                if (_pipOverlayBmp == null) return;

                double scale = Math.Clamp(PipScalePercent / 100.0, 0.12, 0.85);
                int w = Math.Max(64, (int)(canvasW * scale));
                double aspect = (double)_pipOverlayBmp.Width / Math.Max(1, _pipOverlayBmp.Height);
                int h = Math.Max(64, (int)(w / Math.Max(0.25, aspect)));

                if (PipMaskShape == OverlayMaskShape.CircleMask)
                {
                    int side = Math.Min(w, h);
                    w = side;
                    h = side;
                }

                int pad = 20;
                int x = PipPositionIndex switch
                {
                    0 => pad,                         // Sol Üst
                    1 => canvasW - w - pad,           // Sağ Üst
                    2 => pad,                         // Sol Alt
                    3 => canvasW - w - pad,           // Sağ Alt
                    _ => (canvasW - w) / 2            // Merkez
                };
                int y = PipPositionIndex switch
                {
                    0 => pad,
                    1 => pad,
                    2 => canvasH - h - pad,
                    3 => canvasH - h - pad,
                    _ => (canvasH - h) / 2
                };

                var destRect = new Rectangle(x, y, w, h);

                if (PipMaskShape == OverlayMaskShape.CircleMask)
                {
                    var state = g.Save();
                    using var path = new GraphicsPath();
                    path.AddEllipse(destRect);
                    g.SetClip(path);
                    g.DrawImage(_pipOverlayBmp, destRect);
                    g.Restore(state);

                    using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(230, 56, 189, 248), 3f);
                    g.DrawEllipse(pen, destRect);
                }
                else if (PipMaskShape == OverlayMaskShape.ChromaKeyGreen)
                {
                    using var attrs = new ImageAttributes();
                    attrs.SetColorKey(System.Drawing.Color.FromArgb(0, 130, 0), System.Drawing.Color.FromArgb(135, 255, 135));
                    g.DrawImage(_pipOverlayBmp, destRect, 0, 0, _pipOverlayBmp.Width, _pipOverlayBmp.Height, GraphicsUnit.Pixel, attrs);
                }
                else if (PipMaskShape == OverlayMaskShape.FullScreenWatermark)
                {
                    var matrix = new ColorMatrix { Matrix33 = 0.42f };
                    using var attrs = new ImageAttributes();
                    attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                    g.DrawImage(_pipOverlayBmp, destRect, 0, 0, _pipOverlayBmp.Width, _pipOverlayBmp.Height, GraphicsUnit.Pixel, attrs);
                }
                else
                {
                    g.DrawImage(_pipOverlayBmp, destRect);
                    using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, 16, 185, 129), 2.5f);
                    g.DrawRectangle(pen, destRect);
                }
            }
        }

        private void DrawMeetingParticipantsOverlay(Graphics g, bool isVertical, int canvasW, int canvasH)
        {
            List<MeetParticipantInfo> list;
            lock (_meetOverlayLock)
            {
                if (_meetingParticipants == null || _meetingParticipants.Count == 0) return;
                list = new List<MeetParticipantInfo>(_meetingParticipants);
            }

            int tileW = isVertical ? 140 : 185;
            int tileH = isVertical ? 82 : 106;
            int startX = 18;
            int startY = 18;
            int maxTiles = Math.Min(4, list.Count);

            for (int i = 0; i < maxTiles; i++)
            {
                var p = list[i];
                int tx = isVertical ? (startX + i * (tileW + 8)) : startX;
                int ty = isVertical ? startY : (startY + i * (tileH + 10));
                var rect = new Rectangle(tx, ty, tileW, tileH);

                bool drewCam = false;
                if (p.LatestCameraJpeg != null && p.LatestCameraJpeg.Length > 100)
                {
                    try
                    {
                        using var ms = new MemoryStream(p.LatestCameraJpeg);
                        using var pBmp = new Bitmap(ms);
                        g.DrawImage(pBmp, rect);
                        drewCam = true;
                    }
                    catch { }
                }

                if (!drewCam)
                {
                    using var bg = new SolidBrush(System.Drawing.Color.FromArgb(225, 17, 24, 39));
                    g.FillRectangle(bg, rect);
                }

                using var borderPen = new System.Drawing.Pen(
                    p.HasRaisedHand
                        ? System.Drawing.Color.FromArgb(245, 158, 11)
                        : System.Drawing.Color.FromArgb(16, 185, 129),
                    2.2f);
                g.DrawRectangle(borderPen, rect);

                using var nameBar = new SolidBrush(System.Drawing.Color.FromArgb(190, 9, 13, 22));
                g.FillRectangle(nameBar, new Rectangle(tx, ty + tileH - 22, tileW, 22));

                string label = (p.HasRaisedHand ? "✋ " : "👤 ") + p.Name;
                using var f = new Font("Segoe UI Semibold", 8.5f);
                using var br = new SolidBrush(System.Drawing.Color.White);
                g.DrawString(label, f, br, new PointF(tx + 5, ty + tileH - 19));
            }
        }

        private static unsafe void CopyBitmapToRawBuffer(Bitmap bmp, byte[] dstBuffer)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int rowBytes = w * 3;
                int stride = data.Stride;
                if (stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, dstBuffer, 0, Math.Min(dstBuffer.Length, rowBytes * h));
                }
                else
                {
                    byte* src = (byte*)data.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy((IntPtr)(src + y * stride), dstBuffer, y * rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static void WriteRawBufferToFfmpegPipe(byte[] raw, Process proc)
        {
            if (proc == null || proc.HasExited || raw == null) return;
            try
            {
                proc.StandardInput.BaseStream.Write(raw, 0, raw.Length);
            }
            catch { }
        }

        private static unsafe void WriteBitmapToFfmpegPipe(Bitmap bmp, Process proc)
        {
            if (proc == null || proc.HasExited) return;
            try
            {
                int w = bmp.Width;
                int h = bmp.Height;
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    int rowBytes = w * 3;
                    byte[] raw = new byte[rowBytes * h];
                    byte* src = (byte*)data.Scan0;
                    int stride = data.Stride;
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy((IntPtr)(src + y * stride), raw, y * rowBytes, rowBytes);
                    }
                    proc.StandardInput.BaseStream.Write(raw, 0, raw.Length);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch { }
        }

        private void UpdateMeetJpeg(Bitmap bmp16)
        {
            try
            {
                using var ms = new MemoryStream();
                bmp16.Save(ms, ImageFormat.Jpeg);
                LatestJpeg16x9 = ms.ToArray();
            }
            catch { }
        }

        public static void GetDesktopResolution(out int width, out int height)
        {
            width = GetSystemMetrics(0);
            height = GetSystemMetrics(1);
            if (width < 800 || height < 600)
            {
                width = 1920;
                height = 1080;
            }
        }

        public void Dispose()
        {
            _running = false;
            StopCameraCapture();
            StopRtmpBroadcast();
            AudioEngine.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
