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
        public double WebcamSizeRatio { get; set; } = 0.24;
        // Varsayılan dikey düzen: Üstte okunur ekran bölgesi + altta büyük kamera (telefonda en anlaşılır düzen)
        public SmartPortraitLayout PortraitLayout { get; set; } = SmartPortraitLayout.SplitScreenCamBottom;
        public double LiveUncropPercent { get; set; } = 0.0; // 0 = Tam 9:16, 100 = Tam 16:9 Uncrop
        public double ManualPanX { get; set; } = 0.0;        // -0.5 .. +0.5
        public bool Record4KHorizontal { get; set; } = false;

        // Eğitim Videosu Kamera Stüdyosu: Arka Plan Tanıma/Değiştirme, Beni Netleme (DSLR Focus) & Kamera Açısı (PTZ)
        // 0: Doğal Kamera, 1: DSLR Bokeh Blur, 2: TheZencirs Mimari Stüdyo, 3: Circle Karanlık Spotlight, 4: Şeffaf Silüet (Arka Plansız), 5: Özel Görsel Arka Plan
        public int WebcamBgMode { get; set; } = 1;
        public double WebcamSubjectSharpness { get; set; } = 75.0; // %0..%100 Eğitmen Yüz/Portre Netleme (Unsharp Mask + Göz/Kontur Keskinliği)
        public bool WebcamStudioKeyLight { get; set; } = true;     // Eğitmenin yüzünü aydınlatan yumuşak stüdyo ışığı
        public double WebcamDigitalZoom { get; set; } = 1.22;      // 1.0x..2.4x Kamera Açısı / Portre Yakınlaştırma
        public double WebcamPanX { get; set; } = 0.0;              // -0.40..+0.40 Kamera Yatay Açı Kaydırma
        public double WebcamTiltY { get; set; } = -0.04;           // -0.40..+0.40 Kamera Dikey Göz Hizası Açısı
        public double WebcamRollDeg { get; set; } = 0.0;           // -15°..+15° Kamera Ufuk/Eğim Düzeltme
        public int WebcamCornerIndex { get; set; } = 0;            // 0: Sağ Alt, 1: Sol Alt, 2: Sağ Üst, 3: Sol Üst, 4: Sağ Orta Kart, 5: Tam Ekran Eğitmen
        public int WebcamFrameShape { get; set; } = 0;             // 0: Yuvarlatılmış Sinematik, 1: Daire Portre (Circle), 2: Dikey Portre (4:5)
        public bool ShowWebcamAngleGuide { get; set; } = false;    // Göz Hizası & Altın Oran Kamera Açısı Kılavuz Çizgileri
        private byte[] _calibratedBgFrameBgr;
        private volatile bool _captureBgCalibrationNextFrame;
        private byte[] _customBgBgr640x360;
        private readonly object _customBgLock = new();

        public void CalibrateWebcamBackgroundNow()
        {
            _captureBgCalibrationNextFrame = true;
        }

        public void SetCustomWebcamBackground(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return;
            try
            {
                using var src = new Bitmap(imagePath);
                using var scaled = new Bitmap(640, 360, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, 640, 360));
                }
                byte[] buf = new byte[640 * 360 * 3];
                CopyBitmapToRawBuffer(scaled, buf);
                lock (_customBgLock)
                {
                    _customBgBgr640x360 = buf;
                }
                WebcamBgMode = 5;
            }
            catch { }
        }

        // Akıllı Zoom & Odak Takip Durumu
        public bool IsSmartZoomActive { get; set; } = false;
        public bool FollowMouseEnabled { get; set; } = true;
        // Her tıklamada otomatik yakınlaşma varsayılan KAPALI (sürekli zıplayan görüntü izleyiciyi yoruyordu). Yakınlaşma: Alt+Q
        public bool AutoClickZoomEnabled { get; set; } = false;
        public double ZoomFactor16x9 { get; set; } = 2.0;
        public double ZoomFactor9x16 { get; set; } = 1.75;
        // Dikey (9:16) ekran bölgesinin temel yakınlığı: telefonda yazılar okunsun diye ekranın ~%60'ı gösterilir
        public double PortraitBaseZoom { get; set; } = 1.3;
        // Ölü bölge: imleç kadrajın orta %60'ında gezinirken kamera KIPIRDAMAZ, yalnızca kenara yaklaşınca yumuşakça kayar
        public double FollowDeadZone { get; set; } = 0.30;

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

        // Kompozisyon Çözünürlükleri — 16:9 ekranla BİREBİR 1920x1080 (eski 1280x720 küçültüp tekrar büyütmek yazıları bulanıklaştırıyordu).
        // 9:16 iç tuval 720x1280: kırpılan ekran bölgesi ~750 px genişliğinde olduğundan doğal çözünürlüğe denk; FFmpeg lanczos ile 1080x1920'ye çıkarır.
        public const int Canvas16W = 1920;
        public const int Canvas16H = 1080;
        public const int Canvas9W = 720;
        public const int Canvas9H = 1280;

        // Anlık Ekran Resmi (Screenshot) İçin Sıfır-Allocation Raw Tamponlar
        private readonly byte[] _shotRaw16 = new byte[Canvas16W * Canvas16H * 3];
        private readonly byte[] _shotRaw9 = new byte[Canvas9W * Canvas9H * 3];
        private readonly object _shotLock = new();

        // Canlı Performans & Sağlık Telemetrisi (100/100)
        public double LastFrameRenderMs { get; private set; } = 2.4;
        public int StudioHealthScore { get; private set; } = 100;
        public string ActiveCameraStatus { get; private set; } = "Circle Broadcast (Varsayılan)";
        public bool IsVerticalOnlyRecording { get; private set; } = false;
        public bool IsEditorFocusMode { get; set; } = false; // Circle Edit Kurgu ekranındayken arka plan ekran yakalamayı uyutur (0% CPU)

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
                using var b16 = CreateBitmapFromRawBgr24(_shotRaw16, Canvas16W, Canvas16H);
                using var b9 = CreateBitmapFromRawBgr24(_shotRaw9, Canvas9W, Canvas9H);
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
        
        private readonly object _recordPipeLock = new();

        // Kamera Takip Değişkenleri
        private double _curZoom16 = 1.0;
        private double _curZoom9 = 1.0;
        private double _curNormX = 0.5;
        private double _curNormY = 0.5;
        private DateTime _lastClickTime = DateTime.MinValue;
        private System.Drawing.Point _lastClickPt;
        private bool _wasLeftMouseDown;
        private double _tgtNormX = 0.5;
        private double _tgtNormY = 0.5;
        private double _lastCropFracW9 = 0.42;
        private double _lastCropFracH9 = 0.77;

        // Ekran karesi çizimi: kaliteli ve hızlı (ölçümde eski 'Low' moddan hem net hem ~%40 daha hızlı)
        private static void SetFastHq(Graphics g)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.HighSpeed;
        }

        // Bindirmeler (kamera, yazı, çerçeve) için saydamlık destekli mod
        private static void SetOverlayMode(Graphics g)
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        // Aktif Proje
        public CmrProject ActiveProject { get; private set; }

        // Sıfır-Allocation UI Güncelleme Olayı (WriteableBitmap için doğrudan BGR24 byte dizileri)
        public event Action<byte[], int, int, byte[], int, int> OnRawFramesReady;

        public DualCanvasEngine()
        {
            ActiveEncoder = new HardwareEncoderInfo
            {
                CodecName = "h264_nvenc",
                DisplayName = "Circle GPU (Donanım Hızlandırma)",
                PresetArgs = "-preset p4 -tune hq -rc vbr -cq 20 -b:v 0",
                IsHardware = true
            };
            Task.Run(() => ActiveEncoder = HardwareEncoderService.DetectBestEncoder());
        }

        public void StartEngine(DirectShowDevice cameraDevice = null)
        {
            if (_running) return;
            _running = true;

            Task.Run(RecoverUnfinishedRecordings);
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

        /// <summary>
        /// Elektrik kesintisi, erken kapatma veya yarım kalan birleştirme sonrası Proje_Verileri içindeki
        /// _temp_16x9.mkv / _temp_9x16.mkv ve Master_Mic.wav dosyalarından bozuk/eksik MP4 kayıtlarını otomatik kurtarır.
        /// </summary>
        public static void RecoverUnfinishedRecordings()
        {
            try
            {
                string baseRoot = Path.Combine(HardwareEncoderService.GetBaseStorageDir(), "Proje_Verileri");
                if (!Directory.Exists(baseRoot)) return;
                string horizDir = HardwareEncoderService.GetHorizontalRecordingsDir();
                string vertDir = HardwareEncoderService.GetVerticalRecordingsDir();

                foreach (string projDir in Directory.GetDirectories(baseRoot))
                {
                    string projName = Path.GetFileName(projDir);
                    if (string.IsNullOrWhiteSpace(projName) || projName.StartsWith("_")) continue;

                    string raw16 = Path.Combine(projDir, "_temp_16x9.mkv");
                    string raw9 = Path.Combine(projDir, "_temp_9x16.mkv");
                    if (!File.Exists(raw16) && !File.Exists(raw9)) continue;

                    string out16 = Path.Combine(horizDir, $"{projName}_Yatay_16x9.mp4");
                    string out9 = Path.Combine(vertDir, $"{projName}_Dikey_9x16.mp4");

                    bool need16 = File.Exists(raw16) && (!File.Exists(out16) || new FileInfo(out16).Length < new FileInfo(raw16).Length * 0.85);
                    bool need9 = File.Exists(raw9) && (!File.Exists(out9) || new FileInfo(out9).Length < new FileInfo(raw9).Length * 0.85);

                    if (!need16 && !need9)
                    {
                        try { if (File.Exists(raw16)) File.Delete(raw16); } catch { }
                        try { if (File.Exists(raw9)) File.Delete(raw9); } catch { }
                        continue;
                    }

                    string micWav = Path.Combine(projDir, "Master_Mic.wav");
                    string sysWav = Path.Combine(projDir, "Master_System.wav");
                    string masterM4a = PrepareMasterAudioTrack(projDir, micWav, sysWav, micAlreadyDenoised: true);

                    if (need16 && MuxVideoWithPreparedAudio(raw16, masterM4a, out16))
                    {
                        try { File.Delete(raw16); } catch { }
                    }
                    if (need9 && MuxVideoWithPreparedAudio(raw9, masterM4a, out9))
                    {
                        try { File.Delete(raw9); } catch { }
                    }
                    if (!string.IsNullOrEmpty(masterM4a) && File.Exists(masterM4a))
                    {
                        try { File.Delete(masterM4a); } catch { }
                    }
                }
            }
            catch { }
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

            // Eğer Circle Broadcast seçiliyse ama arka planda Circle Broadcast uygulaması kapalı olduğu için
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

                // Circle Broadcast kapalıysa 2.5 sn içinde akış gelmezse tespit et
                if (cam.IsStudioBroadcast)
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
                byte[] workBuf = new byte[frameBytes];
                byte[] blurHoriz = new byte[frameBytes];
                byte[] blurFinal = new byte[frameBytes];
                byte[] fgMask = new byte[camW * camH];
                float smoothHeadCx = camW * 0.50f;
                float smoothHeadCy = camH * 0.40f;

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

                    bool mirror = MirrorWebcam;
                    bool wbFix = NaturalWhiteBalanceWebcam;
                    int bgMode = WebcamBgMode;
                    float sharpAmt = (float)Math.Clamp(WebcamSubjectSharpness / 100.0, 0.0, 1.25);
                    bool keyLight = WebcamStudioKeyLight;
                    float zoom = (float)Math.Clamp(WebcamDigitalZoom, 1.0, 2.5);
                    float panX = (float)Math.Clamp(WebcamPanX, -0.45, 0.45);
                    float tiltY = (float)Math.Clamp(WebcamTiltY, -0.45, 0.45);
                    float rollRad = (float)(Math.Clamp(WebcamRollDeg, -18.0, 18.0) * Math.PI / 180.0);
                    float cosR = (float)Math.Cos(rollRad);
                    float sinR = (float)Math.Sin(rollRad);
                    bool hasRoll = Math.Abs(rollRad) > 0.003f;

                    // 1) Kamera Açısı (PTZ Digital Zoom + Pan + Tilt + Roll + Ayna + Doğal Beyaz Dengesi)
                    unsafe
                    {
                        fixed (byte* srcBase = buffer)
                        fixed (byte* wrkBase = workBuf)
                        {
                            float invZ = 1.0f / zoom;
                            float cx = camW * 0.5f;
                            float cy = camH * 0.5f;
                            float shiftX = panX * camW * 0.5f;
                            float shiftY = tiltY * camH * 0.5f;

                            for (int y = 0; y < camH; y++)
                            {
                                byte* dstRow = wrkBase + y * (camW * 3);
                                float dy = (y - cy) * invZ;
                                for (int x = 0; x < camW; x++)
                                {
                                    float dx = (x - cx) * invZ;
                                    float rx = hasRoll ? (dx * cosR - dy * sinR) : dx;
                                    float ry = hasRoll ? (dx * sinR + dy * cosR) : dy;

                                    int srcX = (int)(cx + rx + shiftX);
                                    int srcY = (int)(cy + ry + shiftY);
                                    if (srcX < 0) srcX = 0; else if (srcX >= camW) srcX = camW - 1;
                                    if (srcY < 0) srcY = 0; else if (srcY >= camH) srcY = camH - 1;

                                    int sx = mirror ? (camW - 1 - srcX) : srcX;
                                    int idx = (srcY * camW + sx) * 3;
                                    byte b = srcBase[idx + 0];
                                    byte g = srcBase[idx + 1];
                                    byte r = srcBase[idx + 2];

                                    if (wbFix)
                                    {
                                        b = (byte)Math.Min(255, (b * 112) / 100);
                                        g = (byte)Math.Min(255, (g * 99) / 100);
                                        r = (byte)Math.Min(255, (r * 94) / 100);
                                    }

                                    dstRow[x * 3 + 0] = b;
                                    dstRow[x * 3 + 1] = g;
                                    dstRow[x * 3 + 2] = r;
                                }
                            }
                        }
                    }

                    if (_captureBgCalibrationNextFrame)
                    {
                        _captureBgCalibrationNextFrame = false;
                        _calibratedBgFrameBgr = (byte[])workBuf.Clone();
                    }

                    // 2) AI Portre & Eğitmen Yüz/Gövde Tanıma (YCbCr Cilt Kümesi + Omuz Silüeti + Kalibre Arka Plan Farkı)
                    long sumSkinX = 0, sumSkinY = 0, skinCount = 0;
                    unsafe
                    {
                        fixed (byte* wrkBase = workBuf)
                        {
                            for (int y = 24; y < camH - 24; y += 2)
                            {
                                byte* row = wrkBase + y * (camW * 3);
                                for (int x = 48; x < camW - 48; x += 2)
                                {
                                    int b = row[x * 3 + 0];
                                    int g = row[x * 3 + 1];
                                    int r = row[x * 3 + 2];
                                    int luma = (r * 77 + g * 150 + b * 29) >> 8;
                                    int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
                                    int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);

                                    if (luma > 42 && luma < 240 && cb >= 76 && cb <= 133 && cr >= 131 && cr <= 179 && r > b + 12)
                                    {
                                        sumSkinX += x;
                                        sumSkinY += y;
                                        skinCount++;
                                    }
                                }
                            }
                        }
                    }

                    if (skinCount > 80)
                    {
                        float detCx = (float)sumSkinX / skinCount;
                        float detCy = (float)sumSkinY / skinCount;
                        smoothHeadCx = smoothHeadCx * 0.82f + Math.Clamp(detCx, camW * 0.22f, camW * 0.78f) * 0.18f;
                        smoothHeadCy = smoothHeadCy * 0.82f + Math.Clamp(detCy, camH * 0.18f, camH * 0.65f) * 0.18f;
                    }

                    byte[] calBg = _calibratedBgFrameBgr;
                    byte[] customBg = null;
                    lock (_customBgLock) { customBg = _customBgBgr640x360; }

                    unsafe
                    {
                        fixed (byte* wrkBase = workBuf)
                        fixed (byte* maskBase = fgMask)
                        {
                            float hCx = smoothHeadCx;
                            float hCy = smoothHeadCy;
                            float tCx = smoothHeadCx;
                            float tCy = Math.Min(camH * 0.92f, smoothHeadCy + camH * 0.42f);

                            float invHeadRx = 1.0f / (camW * 0.19f);
                            float invHeadRy = 1.0f / (camH * 0.30f);
                            float invTorsoRx = 1.0f / (camW * 0.34f);
                            float invTorsoRy = 1.0f / (camH * 0.48f);

                            for (int y = 0; y < camH; y++)
                            {
                                byte* row = wrkBase + y * (camW * 3);
                                int rowOff = y * camW;
                                float dHy = (y - hCy) * invHeadRy;
                                float dTy = (y - tCy) * invTorsoRy;

                                for (int x = 0; x < camW; x++)
                                {
                                    float dHx = (x - hCx) * invHeadRx;
                                    float dTx = (x - tCx) * invTorsoRx;

                                    float distHead = dHx * dHx + dHy * dHy;
                                    float distTorso = dTx * dTx + dTy * dTy;
                                    float geomDist = Math.Min(distHead, distTorso);

                                    int b = row[x * 3 + 0];
                                    int g = row[x * 3 + 1];
                                    int r = row[x * 3 + 2];
                                    int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
                                    int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
                                    bool isSkin = (cb >= 75 && cb <= 135 && cr >= 129 && cr <= 180 && r > b + 10);

                                    if (isSkin && geomDist < 1.9f)
                                        geomDist *= 0.62f;

                                    if (calBg != null && calBg.Length == frameBytes)
                                    {
                                        int pIdx = (rowOff + x) * 3;
                                        int diff = Math.Abs(b - calBg[pIdx]) + Math.Abs(g - calBg[pIdx + 1]) + Math.Abs(r - calBg[pIdx + 2]);
                                        if (diff > 48) geomDist *= 0.68f;
                                        else if (diff < 22 && !isSkin) geomDist *= 1.35f;
                                    }

                                    // Yumuşak geçişli (feathered) eğitmen maskesi (0 = Arka Plan, 255 = Eğitmen)
                                    float alphaF = Math.Clamp((1.32f - geomDist) * 2.15f, 0.0f, 1.0f);
                                    maskBase[rowOff + x] = (byte)(alphaF * 255.0f);
                                }
                            }
                        }
                    }

                    // 3) Arka Plan Bulanıklaştırma (Bokeh Blur Modu İçin Hızlı Ayrılabilir Kutu Blur)
                    if (bgMode == 1)
                    {
                        unsafe
                        {
                            fixed (byte* src = workBuf)
                            fixed (byte* tmp = blurHoriz)
                            fixed (byte* dst = blurFinal)
                            {
                                const int rad = 7;
                                const int diam = rad * 2 + 1;
                                for (int y = 0; y < camH; y++)
                                {
                                    byte* sRow = src + y * (camW * 3);
                                    byte* tRow = tmp + y * (camW * 3);
                                    for (int x = 0; x < camW; x++)
                                    {
                                        int sb = 0, sg = 0, sr = 0;
                                        for (int k = -rad; k <= rad; k++)
                                        {
                                            int xx = x + k;
                                            if (xx < 0) xx = 0; else if (xx >= camW) xx = camW - 1;
                                            sb += sRow[xx * 3 + 0];
                                            sg += sRow[xx * 3 + 1];
                                            sr += sRow[xx * 3 + 2];
                                        }
                                        tRow[x * 3 + 0] = (byte)(sb / diam);
                                        tRow[x * 3 + 1] = (byte)(sg / diam);
                                        tRow[x * 3 + 2] = (byte)(sr / diam);
                                    }
                                }
                                for (int y = 0; y < camH; y++)
                                {
                                    byte* dRow = dst + y * (camW * 3);
                                    for (int x = 0; x < camW; x++)
                                    {
                                        int sb = 0, sg = 0, sr = 0;
                                        for (int k = -rad; k <= rad; k++)
                                        {
                                            int yy = y + k;
                                            if (yy < 0) yy = 0; else if (yy >= camH) yy = camH - 1;
                                            byte* tRow = tmp + yy * (camW * 3);
                                            sb += tRow[x * 3 + 0];
                                            sg += tRow[x * 3 + 1];
                                            sr += tRow[x * 3 + 2];
                                        }
                                        // Bokeh arka planı hafif sinematik koyulaştır (%86) ki eğitmen öne çıksın
                                        dRow[x * 3 + 0] = (byte)((sb / diam) * 88 / 100);
                                        dRow[x * 3 + 1] = (byte)((sg / diam) * 86 / 100);
                                        dRow[x * 3 + 2] = (byte)((sr / diam) * 86 / 100);
                                    }
                                }
                            }
                        }
                    }

                    // 4) Beni Netleme (DSLR Unsharp Mask + Stüdyo Yüz Işığı) & Arka Plan Kompozitleme
                    var outFormat = (bgMode == 4)
                        ? System.Drawing.Imaging.PixelFormat.Format32bppArgb
                        : System.Drawing.Imaging.PixelFormat.Format24bppRgb;
                    var bmp = new Bitmap(camW, camH, outFormat);
                    var bmpData = bmp.LockBits(new Rectangle(0, 0, camW, camH), ImageLockMode.WriteOnly, outFormat);

                    unsafe
                    {
                        byte* dstBase = (byte*)bmpData.Scan0;
                        int stride = bmpData.Stride;
                        int rowBytes = camW * 3;

                        fixed (byte* wrkBase = workBuf)
                        fixed (byte* blrBase = blurFinal)
                        fixed (byte* mskBase = fgMask)
                        fixed (byte* cstBase = customBg)
                        {
                            for (int y = 0; y < camH; y++)
                            {
                                byte* srcRow = wrkBase + y * rowBytes;
                                byte* dstRow = dstBase + y * stride;
                                int rowOff = y * camW;
                                bool canSharpenY = (y > 0 && y < camH - 1 && sharpAmt > 0.02f);

                                for (int x = 0; x < camW; x++)
                                {
                                    int pIdx = x * 3;
                                    int fgA = mskBase[rowOff + x];

                                    int fgB = srcRow[pIdx + 0];
                                    int fgG = srcRow[pIdx + 1];
                                    int fgR = srcRow[pIdx + 2];

                                    // Beni Netleme: Eğitmen yüz ve omuz bölgesine DSLR Unsharp Mask keskinleştirme uygula
                                    if (canSharpenY && fgA > 45 && x > 0 && x < camW - 1)
                                    {
                                        byte* upRow = srcRow - rowBytes;
                                        byte* dnRow = srcRow + rowBytes;

                                        int lapB = fgB * 4 - upRow[pIdx] - dnRow[pIdx] - srcRow[pIdx - 3] - srcRow[pIdx + 3];
                                        int lapG = fgG * 4 - upRow[pIdx + 1] - dnRow[pIdx + 1] - srcRow[pIdx - 2] - srcRow[pIdx + 4];
                                        int lapR = fgR * 4 - upRow[pIdx + 2] - dnRow[pIdx + 2] - srcRow[pIdx - 1] - srcRow[pIdx + 5];

                                        float effSharp = sharpAmt * (fgA / 255.0f);
                                        fgB = Math.Clamp((int)(fgB + lapB * effSharp), 0, 255);
                                        fgG = Math.Clamp((int)(fgG + lapG * effSharp), 0, 255);
                                        fgR = Math.Clamp((int)(fgR + lapR * effSharp), 0, 255);
                                    }

                                    // Stüdyo Yüz Işığı (Soft Key-Light): Eğitmenin yüzünü aydınlatır ve canlılaştırır
                                    if (keyLight && fgA > 60)
                                    {
                                        int lift = (fgA * 14) >> 8;
                                        fgB = Math.Min(255, fgB + (lift * 8) / 10);
                                        fgG = Math.Min(255, fgG + lift);
                                        fgR = Math.Min(255, fgR + lift);
                                    }

                                    if (bgMode == 4)
                                    {
                                        // Şeffaf Silüet Kesim (Masaüstü ekranının üzerinde kutusuz eğitmen)
                                        dstRow[x * 4 + 0] = (byte)fgB;
                                        dstRow[x * 4 + 1] = (byte)fgG;
                                        dstRow[x * 4 + 2] = (byte)fgR;
                                        dstRow[x * 4 + 3] = (byte)fgA;
                                        continue;
                                    }

                                    int bgB = fgB, bgG = fgG, bgR = fgR;
                                    if (bgMode == 1)
                                    {
                                        byte* bRow = blrBase + y * rowBytes;
                                        bgB = bRow[pIdx + 0];
                                        bgG = bRow[pIdx + 1];
                                        bgR = bRow[pIdx + 2];
                                    }
                                    else if (bgMode == 2)
                                    {
                                        // TheZencirs Lüks Mimari Stüdyo Arka Planı (Koyu İndigo Dikey Lamel + Sıcak Turuncu Gizli LED)
                                        bool slat = ((x / 14) % 2 == 0);
                                        float glow = Math.Max(0f, 1.0f - (Math.Abs(x - smoothHeadCx) / (camW * 0.42f)) - (Math.Abs(y - smoothHeadCy) / (camH * 0.55f)));
                                        bgB = (int)Math.Clamp((slat ? 64 : 46) + glow * 28, 0, 255);
                                        bgG = (int)Math.Clamp((slat ? 24 : 16) + glow * 62, 0, 255);
                                        bgR = (int)Math.Clamp((slat ? 34 : 24) + glow * 145, 0, 255);
                                    }
                                    else if (bgMode == 3)
                                    {
                                        // Circle Karanlık Spotlight Stüdyo
                                        float radSpot = Math.Max(0f, 1.0f - ((x - smoothHeadCx) * (x - smoothHeadCx)) / (camW * camW * 0.22f) - ((y - smoothHeadCy) * (y - smoothHeadCy)) / (camH * camH * 0.28f));
                                        bgB = (int)(16 + radSpot * 42);
                                        bgG = (int)(11 + radSpot * 28);
                                        bgR = (int)(18 + radSpot * 36);
                                    }
                                    else if (bgMode == 5 && cstBase != null)
                                    {
                                        byte* cRow = cstBase + y * rowBytes;
                                        bgB = cRow[pIdx + 0];
                                        bgG = cRow[pIdx + 1];
                                        bgR = cRow[pIdx + 2];
                                    }

                                    if (bgMode == 0)
                                    {
                                        dstRow[pIdx + 0] = (byte)fgB;
                                        dstRow[pIdx + 1] = (byte)fgG;
                                        dstRow[pIdx + 2] = (byte)fgR;
                                    }
                                    else
                                    {
                                        int invA = 255 - fgA;
                                        dstRow[pIdx + 0] = (byte)((fgB * fgA + bgB * invA) >> 8);
                                        dstRow[pIdx + 1] = (byte)((fgG * fgA + bgG * invA) >> 8);
                                        dstRow[pIdx + 2] = (byte)((fgR * fgA + bgR * invA) >> 8);
                                    }
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
            string vertMp4Path = Path.Combine(vertDir, $"{projName}_Dikey_9x16.mp4");

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
                MasterWidth = Canvas16W,
                MasterHeight = Canvas16H,
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
                    _procHoriz16x9 = StartRawVideoEncoderProcess(Canvas16W, Canvas16H, 30, proj.HorizontalOutputWidth, proj.HorizontalOutputHeight, enc, encArgs, raw16Mkv);
                }
                _procVert9x16 = StartRawVideoEncoderProcess(Canvas9W, Canvas9H, 30, 1080, 1920, enc, encArgs, raw9Mkv);

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

            // Kaydedilen ayrık Mikrofon + Sistem sesini Yatay_16x9_Kayitlar ve Dikey_9x16_Kayitlar klasörlerine yüksek kalitede birleştir
            bool usedBroadcastMic = AudioEngine.RecordingUsedBroadcastMic;
            await Task.Run(() =>
            {
                string raw16Mkv = Path.Combine(proj.ProjectDirectory, "_temp_16x9.mkv");
                string raw9Mkv = Path.Combine(proj.ProjectDirectory, "_temp_9x16.mkv");
                string masterAudioM4a = PrepareMasterAudioTrack(proj.ProjectDirectory, proj.MicrophoneWavPath, proj.SystemAudioWavPath, usedBroadcastMic);

                if (File.Exists(raw16Mkv))
                {
                    if (MuxVideoWithPreparedAudio(raw16Mkv, masterAudioM4a, proj.Horizontal16x9VideoPath))
                    {
                        try { File.Delete(raw16Mkv); } catch { }
                    }
                }
                if (File.Exists(raw9Mkv))
                {
                    if (MuxVideoWithPreparedAudio(raw9Mkv, masterAudioM4a, proj.Vertical9x16VideoPath))
                    {
                        try { File.Delete(raw9Mkv); } catch { }
                    }
                }
                if (!string.IsNullOrEmpty(masterAudioM4a) && File.Exists(masterAudioM4a))
                {
                    try { File.Delete(masterAudioM4a); } catch { }
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

        // ===================== ÇOK PLATFORMLU CANLI YAYIN (YouTube + Twitch + Kick + Özel, tek kodlama) =====================
        private StreamSession _stream;
        private bool _streamAudioHooked;
        public StreamSession ActiveStream => _stream;

        public bool StartMultiStream(StreamSettings settings, out string error)
        {
            StopRtmpBroadcast();
            error = "";
            if (!_streamAudioHooked)
            {
                // Canlı mikrofon + masaüstü sesi yayına (eskiden yayın SESSİZ gidiyordu)
                AudioEngine.OnMicPcm += (pcm, rate, ch) => _stream?.Audio?.PushMic(pcm, rate, ch);
                AudioEngine.OnSystemPcm += (pcm, rate, ch) => _stream?.Audio?.PushSystem(pcm, rate, ch);
                _streamAudioHooked = true;
            }
            var session = settings.Vertical
                ? new StreamSession(Canvas9W, Canvas9H, vertical: true)
                : new StreamSession(Canvas16W, Canvas16H, vertical: false);
            if (!session.Start(settings, ActiveEncoder, out error))
            {
                session.Dispose();
                IsStreamingRtmp = false;
                return false;
            }
            _stream = session;
            IsStreamingRtmp = true;
            return true;
        }

        /// <summary>Geriye dönük uyumluluk: tek bir tam RTMP adresine yayın.</summary>
        public bool StartRtmpBroadcast(string rtmpUrl)
        {
            if (string.IsNullOrWhiteSpace(rtmpUrl)) return false;
            var s = new StreamSettings();
            s.Destinations.Add(new StreamDestination { Name = "Özel RTMP", Enabled = true, ServerUrl = rtmpUrl.Trim() });
            return StartMultiStream(s, out _);
        }

        public void StopRtmpBroadcast()
        {
            IsStreamingRtmp = false;
            var s = _stream;
            _stream = null;
            s?.Dispose();
        }

        // ===================== BEKLEME / ARA EKRANI (Alt+Shift+B) =====================
        public bool IntermissionActive { get; set; }
        private Bitmap _intermissionBmp;
        private readonly object _intermissionLock = new();

        public void SetIntermissionImage(string path)
        {
            lock (_intermissionLock)
            {
                _intermissionBmp?.Dispose();
                _intermissionBmp = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        using var raw = new Bitmap(path);
                        _intermissionBmp = new Bitmap(raw);
                    }
                }
                catch { _intermissionBmp = null; }
            }
        }

        private void DrawIntermission(Graphics g, int w, int h)
        {
            lock (_intermissionLock)
            {
                if (_intermissionBmp != null)
                {
                    // Cover-fit (kırparak doldur)
                    double sa = (double)_intermissionBmp.Width / _intermissionBmp.Height;
                    double da = (double)w / h;
                    Rectangle src = sa > da
                        ? new Rectangle((int)((_intermissionBmp.Width - _intermissionBmp.Height * da) / 2), 0, (int)(_intermissionBmp.Height * da), _intermissionBmp.Height)
                        : new Rectangle(0, (int)((_intermissionBmp.Height - _intermissionBmp.Width / da) / 2), _intermissionBmp.Width, (int)(_intermissionBmp.Width / da));
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(_intermissionBmp, new Rectangle(0, 0, w, h), src, GraphicsUnit.Pixel);
                    return;
                }
            }
            // Varsayılan marka kartı
            g.Clear(System.Drawing.Color.FromArgb(20, 14, 56));
            using var accent = new SolidBrush(System.Drawing.Color.FromArgb(239, 95, 24));
            g.FillRectangle(accent, 0, h - h / 60, w, h / 60);
            using var f1 = new Font("Segoe UI Semibold", Math.Max(18f, h / 14f), System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var f2 = new Font("Segoe UI", Math.Max(12f, h / 32f), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(System.Drawing.Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString("BİRAZDAN DÖNÜYORUZ", f1, white, new RectangleF(0, h * 0.38f, w, h * 0.14f), sf);
            g.DrawString("CIRCLE MIX RECORD", f2, accent, new RectangleF(0, h * 0.52f, w, h * 0.08f), sf);
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
                    proc.WaitForExit(15000);
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

        private static string PrepareMasterAudioTrack(string projectDir, string micWav, string sysWav, bool micAlreadyDenoised)
        {
            string ff = HardwareEncoderService.GetFfmpegPath();
            string outM4a = Path.Combine(projectDir, "_temp_master_audio.m4a");

            bool hasMic = File.Exists(micWav) && new FileInfo(micWav).Length > 1000;
            bool hasSys = File.Exists(sysWav) && new FileInfo(sysWav).Length > 1000;
            if (!hasMic && !hasSys) return null;

            // Tek geçişli hızlı stüdyo ses mastering zinciri (2 saatlik kaydı iki kez işlemek yerine tek seferde ~15 sn içinde AAC'ye kodlar)
            string micChain = micAlreadyDenoised
                ? "highpass=f=80,acompressor=threshold=-20dB:ratio=3:attack=10:release=200:makeup=3dB,alimiter=limit=0.95,aresample=48000"
                : "highpass=f=90,afftdn=nf=-25:tn=1,acompressor=threshold=-20dB:ratio=3:attack=10:release=200:makeup=3dB,alimiter=limit=0.95,aresample=48000";

            string args;
            if (hasMic && hasSys)
            {
                args = $"-y -hide_banner -loglevel error -threads 8 -i \"{micWav}\" -i \"{sysWav}\" " +
                       $"-filter_complex \"[0:a]{micChain}[mic];[1:a]aresample=48000,volume=0.55[sys];[mic][sys]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95[mix]\" " +
                       $" -map \"[mix]\" -c:a aac -b:a 192k \"{outM4a}\"";
            }
            else if (hasMic)
            {
                args = $"-y -hide_banner -loglevel error -threads 8 -i \"{micWav}\" -af \"{micChain}\" -c:a aac -b:a 192k \"{outM4a}\"";
            }
            else
            {
                args = $"-y -hide_banner -loglevel error -threads 8 -i \"{sysWav}\" -c:a aac -b:a 192k \"{outM4a}\"";
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
                p?.WaitForExit(600000); // 10 dakikaya kadar izin ver (2-4 saatlik ders kayıtlarında yarıda kesilmesin!)
                if (File.Exists(outM4a) && new FileInfo(outM4a).Length > 512)
                    return outM4a;
            }
            catch { }
            return null;
        }

        private static bool MuxVideoWithPreparedAudio(string videoMkv, string masterAudioM4a, string outMp4)
        {
            if (!File.Exists(videoMkv)) return false;
            string ff = HardwareEncoderService.GetFfmpegPath();
            string tmpMp4 = outMp4 + ".muxing.mp4";

            bool hasAudio = !string.IsNullOrEmpty(masterAudioM4a) && File.Exists(masterAudioM4a) && new FileInfo(masterAudioM4a).Length > 512;
            string args = hasAudio
                ? $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -i \"{masterAudioM4a}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a copy -movflags +faststart \"{tmpMp4}\""
                : $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -f lavfi -i anullsrc=r=48000:cl=stereo -c:v copy -c:a aac -b:a 128k -shortest -movflags +faststart \"{tmpMp4}\"";

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
                bool exited = p != null && p.WaitForExit(600000);
                if (exited && p.ExitCode == 0 && File.Exists(tmpMp4) && new FileInfo(tmpMp4).Length > 1024)
                {
                    File.Move(tmpMp4, outMp4, overwrite: true);
                    return true;
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tmpMp4)) File.Delete(tmpMp4); } catch { }
            }

            // Yedek: Doğrudan sadece video akışını MP4 konteynerine kayıpsız aktar
            try
            {
                var psiFallback = new ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = $"-y -hide_banner -loglevel error -i \"{videoMkv}\" -c:v copy -movflags +faststart \"{outMp4}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p2 = Process.Start(psiFallback);
                p2?.WaitForExit(180000);
                return File.Exists(outMp4) && new FileInfo(outMp4).Length > 1024;
            }
            catch { return false; }
        }

        private void CompositorLoop()
        {
            const int masterW = Canvas16W;
            const int masterH = Canvas16H;
            const int vertW = Canvas9W;
            const int vertH = Canvas9H;

            GetDesktopResolution(out int scrW, out int scrH);

            using var screenDesktopBmp = new Bitmap(scrW, scrH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var cleanMasterBmp = new Bitmap(masterW, masterH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var bmp16 = new Bitmap(masterW, masterH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var bmp9 = new Bitmap(vertW, vertH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var swFrame = Stopwatch.StartNew();
            long frameCount = 0;

            while (_running)
            {
                // Kullanıcı Circle Edit Kurgu ekranındaysa ve kayıt/yayın yoksa arka plan ekran yakalamayı uyut (0% CPU / Kasma Yok!)
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
                    bool intermissionNow = IntermissionActive;
                    if (intermissionNow)
                    {
                        using var gScr = Graphics.FromImage(screenDesktopBmp);
                        DrawIntermission(gScr, scrW, scrH);
                    }
                    else if (ShowScreen)
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

                    // 2. İmleç Konumu, (isteğe bağlı) Tık-Zoom ve Ölü Bölgeli Sakin Kamera Takibi
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

                    // Dikey temel yakınlık yalnızca "Ekran + Kamera" düzeninde (telefonda okunur yazı); diğerlerinde 1.0
                    double baseZ9 = (PortraitLayout == SmartPortraitLayout.SplitScreenCamBottom && !intermissionNow) ? Math.Clamp(PortraitBaseZoom, 1.0, 2.5) : 1.0;
                    if (intermissionNow) autoClickZoomNow = false;
                    double targetZ16 = IsSmartZoomActive ? ZoomFactor16x9 : (autoClickZoomNow ? 1.35 : 1.0);
                    double targetZ9 = IsSmartZoomActive ? Math.Max(ZoomFactor9x16, baseZ9) : (autoClickZoomNow ? baseZ9 * 1.3 : baseZ9);

                    _curZoom16 += (targetZ16 - _curZoom16) * 0.12;
                    _curZoom9 += (targetZ9 - _curZoom9) * 0.12;

                    if (FollowMouseEnabled)
                    {
                        // Görünen en dar kadrajın (genelde 9:16) boyutuna göre ölü bölge: imleç kadrajın orta kısmında
                        // gezinirken görüntü SABİT kalır; yalnızca kenara yaklaşınca yumuşakça kayar (baş dönmesi yok).
                        double fracW = Math.Clamp(_lastCropFracW9, 0.05, 1.0);
                        double fracH = Math.Clamp(_lastCropFracH9, 0.05, 1.0);
                        if (_curZoom16 > 1.05)
                        {
                            fracW = Math.Min(fracW, 1.0 / _curZoom16);
                            fracH = Math.Min(fracH, 1.0 / _curZoom16);
                        }
                        double dz = Math.Clamp(FollowDeadZone, 0.0, 0.48);
                        double dzX = fracW * dz;
                        double dzY = fracH * dz;
                        double cx = Math.Clamp((double)mx / scrW + ManualPanX, 0.0, 1.0);
                        double cy = Math.Clamp((double)my / scrH, 0.0, 1.0);

                        if (cx > _tgtNormX + dzX) _tgtNormX = cx - dzX;
                        else if (cx < _tgtNormX - dzX) _tgtNormX = cx + dzX;
                        if (cy > _tgtNormY + dzY) _tgtNormY = cy - dzY;
                        else if (cy < _tgtNormY - dzY) _tgtNormY = cy + dzY;

                        _tgtNormX = Math.Clamp(_tgtNormX, fracW * 0.5, 1.0 - fracW * 0.5);
                        _tgtNormY = Math.Clamp(_tgtNormY, fracH * 0.5, 1.0 - fracH * 0.5);

                        _curNormX += (_tgtNormX - _curNormX) * 0.10;
                        _curNormY += (_tgtNormY - _curNormY) * 0.10;
                    }

                    // İmleç ve Tıklama Halkasını Masaüstü Görüntüsüne Çiz (bekleme ekranında çizilmez)
                    if (!intermissionNow)
                    using (var gCur = Graphics.FromImage(screenDesktopBmp))
                    {
                        gCur.SmoothingMode = SmoothingMode.AntiAlias;
                        if (clickElapsed >= 0 && clickElapsed < 0.45)
                        {
                            float radius = (float)(14 + clickElapsed * 55);
                            int alpha = (int)Math.Clamp((1.0 - clickElapsed / 0.45) * 210, 0, 255);
                            using var clickPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(alpha, 239, 95, 24), 3.5f);
                            gCur.DrawEllipse(clickPen, _lastClickPt.X - radius, _lastClickPt.Y - radius, radius * 2, radius * 2);
                        }
                        using var curBrush = new SolidBrush(System.Drawing.Color.FromArgb(200, 239, 95, 24));
                        using var curPen = new System.Drawing.Pen(System.Drawing.Color.White, 2f);
                        gCur.FillEllipse(curBrush, mx - 8, my - 8, 16, 16);
                        gCur.DrawEllipse(curPen, mx - 8, my - 8, 16, 16);
                    }

                    // 3. Kırpılmamış Temiz Master (Sadece Kayıttayken veya Uncrop Açıkken İşle -> %0 Boşta CPU Yükü!)
                    if (IsRecording || LiveUncropPercent > 0.02 || PortraitLayout == SmartPortraitLayout.UncropBlur16x9)
                    {
                        using var gClean = Graphics.FromImage(cleanMasterBmp);
                        SetFastHq(gClean);
                        gClean.DrawImage(screenDesktopBmp, new Rectangle(0, 0, masterW, masterH), new Rectangle(0, 0, scrW, scrH), GraphicsUnit.Pixel);
                    }

                    // 4. 16:9 Yatay Kare — ekranla birebir 1920x1080 (zoom yokken piksel piksel net)
                    using (var g16 = Graphics.FromImage(bmp16))
                    {
                        int cropW16 = Math.Clamp((int)Math.Round(scrW / Math.Max(1.0, _curZoom16)), 64, scrW);
                        int cropH16 = Math.Clamp((int)Math.Round(scrH / Math.Max(1.0, _curZoom16)), 64, scrH);
                        int cropX16 = (int)Math.Clamp(_curNormX * scrW - cropW16 * 0.5, 0, Math.Max(0, scrW - cropW16));
                        int cropY16 = (int)Math.Clamp(_curNormY * scrH - cropH16 * 0.5, 0, Math.Max(0, scrH - cropH16));

                        SetFastHq(g16);
                        g16.DrawImage(screenDesktopBmp, new Rectangle(0, 0, masterW, masterH), new Rectangle(cropX16, cropY16, cropW16, cropH16), GraphicsUnit.Pixel);
                        SetOverlayMode(g16);

                        if (ShowWebcam)
                        {
                            var camRect16 = ComputeWebcamRect(masterW, masterH, isVertical: false);
                            DrawWebcamOverlay(g16, camRect16, rounded: WebcamCornerIndex != 5);
                        }

                        if (ShowMeetingOverlay)
                        {
                            DrawMeetingParticipantsOverlay(g16, isVertical: false, masterW, masterH);
                        }

                        DrawPipCustomOverlay(g16, masterW, masterH);
                    }

                    // 5. 9:16 Dikey Smart Portrait Karesi
                    using (var g9 = Graphics.FromImage(bmp9))
                    {
                        double sv = vertW / 540.0; // eski 540 px tasarım ölçülerini yeni tuvale ölçekle

                        double uncropRatio = Math.Clamp(LiveUncropPercent / 100.0, 0.0, 1.0);
                        if (PortraitLayout == SmartPortraitLayout.UncropBlur16x9)
                            uncropRatio = Math.Max(uncropRatio, 0.85);

                        if (PortraitLayout == SmartPortraitLayout.SplitScreenCamBottom)
                        {
                            // Üstte okunur ekran bölgesi (~%58) + altta büyük kamera. Kamera kapalıysa ekran kartı ortada, arka plan karartılmış.
                            bool cam = ShowWebcam;
                            int screenH = cam ? (int)(vertH * 0.58) : (int)(vertW * 1.12);
                            int screenY = cam ? 0 : (vertH - screenH) / 2;
                            double aspect = (double)vertW / screenH;

                            int srcCropH = Math.Clamp((int)(scrH / Math.Max(1.0, _curZoom9)), 64, scrH);
                            int srcCropW = (int)(srcCropH * aspect);
                            if (srcCropW > scrW) { srcCropW = scrW; srcCropH = Math.Clamp((int)(scrW / aspect), 64, scrH); }
                            srcCropW = Math.Clamp(srcCropW, 64, scrW);
                            int srcX = (int)Math.Clamp(_curNormX * scrW - srcCropW * 0.5, 0, Math.Max(0, scrW - srcCropW));
                            int srcY = (int)Math.Clamp(_curNormY * scrH - srcCropH * 0.5, 0, Math.Max(0, scrH - srcCropH));
                            _lastCropFracW9 = (double)srcCropW / scrW;
                            _lastCropFracH9 = (double)srcCropH / scrH;
                            var srcRect = new Rectangle(srcX, srcY, srcCropW, srcCropH);

                            SetFastHq(g9);
                            if (!cam)
                            {
                                g9.DrawImage(screenDesktopBmp, new Rectangle(0, 0, vertW, vertH), srcRect, GraphicsUnit.Pixel);
                            }
                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, screenY, vertW, screenH), srcRect, GraphicsUnit.Pixel);
                            SetOverlayMode(g9);

                            if (!cam)
                            {
                                using var darkBrush = new SolidBrush(System.Drawing.Color.FromArgb(200, 20, 14, 56));
                                g9.FillRectangle(darkBrush, 0, 0, vertW, screenY);
                                g9.FillRectangle(darkBrush, 0, screenY + screenH, vertW, vertH - screenY - screenH);
                            }
                            else
                            {
                                DrawWebcamOverlay(g9, new Rectangle(0, screenH, vertW, vertH - screenH), rounded: false);
                                using var divider = new SolidBrush(System.Drawing.Color.FromArgb(239, 95, 24));
                                g9.FillRectangle(divider, 0, screenH - 2, vertW, 4);
                            }
                        }
                        else if (uncropRatio > 0.02)
                        {
                            SetFastHq(g9);
                            g9.DrawImage(cleanMasterBmp, new Rectangle(0, 0, vertW, vertH), new Rectangle(masterW / 4, 0, masterW / 2, masterH), GraphicsUnit.Pixel);
                            SetOverlayMode(g9);
                            using (var darkBrush = new SolidBrush(System.Drawing.Color.FromArgb(175, 10, 14, 24)))
                            {
                                g9.FillRectangle(darkBrush, 0, 0, vertW, vertH);
                            }

                            double minW = scrH * (9.0 / 16.0) / Math.Max(1.0, _curZoom9);
                            int curSrcW = Math.Clamp((int)(minW + (scrW - minW) * uncropRatio), 64, scrW);
                            int curSrcH = Math.Clamp((int)(scrH / Math.Max(1.0, _curZoom9 * (1.0 - uncropRatio * 0.45))), 64, scrH);
                            int sx = (int)Math.Clamp(_curNormX * scrW - curSrcW * 0.5, 0, Math.Max(0, scrW - curSrcW));
                            int sy = (int)Math.Clamp(_curNormY * scrH - curSrcH * 0.5, 0, Math.Max(0, scrH - curSrcH));
                            _lastCropFracW9 = (double)curSrcW / scrW;
                            _lastCropFracH9 = (double)curSrcH / scrH;

                            double aspect = (double)curSrcW / curSrcH;
                            int dstW = vertW;
                            int dstH = (int)Math.Clamp(dstW / aspect, 300 * sv, vertH);
                            int dstY = (vertH - dstH) / 2 - (int)(40 * sv);

                            SetFastHq(g9);
                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, dstY, dstW, dstH), new Rectangle(sx, sy, curSrcW, curSrcH), GraphicsUnit.Pixel);
                            SetOverlayMode(g9);

                            if (ShowWebcam)
                            {
                                var camRect9 = ComputeWebcamRect(vertW, vertH, isVertical: true);
                                DrawWebcamOverlay(g9, camRect9, rounded: WebcamCornerIndex != 5);
                            }
                        }
                        else
                        {
                            int cropH9 = Math.Clamp((int)Math.Round(scrH / Math.Max(1.0, _curZoom9)), 64, scrH);
                            int cropW9 = Math.Clamp((int)Math.Round(cropH9 * ((double)vertW / vertH)), 64, scrW);

                            int cropX9 = (int)Math.Clamp(_curNormX * scrW - cropW9 * 0.5, 0, Math.Max(0, scrW - cropW9));
                            int cropY9 = (int)Math.Clamp(_curNormY * scrH - cropH9 * 0.5, 0, Math.Max(0, scrH - cropH9));
                            _lastCropFracW9 = (double)cropW9 / scrW;
                            _lastCropFracH9 = (double)cropH9 / scrH;

                            SetFastHq(g9);
                            g9.DrawImage(screenDesktopBmp, new Rectangle(0, 0, vertW, vertH), new Rectangle(cropX9, cropY9, cropW9, cropH9), GraphicsUnit.Pixel);
                            SetOverlayMode(g9);

                            if (ShowWebcam)
                            {
                                var camRect9 = ComputeWebcamRect(vertW, vertH, isVertical: true);
                                DrawWebcamOverlay(g9, camRect9, rounded: WebcamCornerIndex != 5);
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
                    var liveStream = _stream;
                    if (IsStreamingRtmp && liveStream != null)
                    {
                        liveStream.WriteFrame(liveStream.Vertical ? _shotRaw9 : _shotRaw16);
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

        private Rectangle ComputeWebcamRect(int canvasW, int canvasH, bool isVertical)
        {
            if (WebcamCornerIndex == 5)
            {
                return new Rectangle(0, 0, canvasW, canvasH);
            }

            double baseRatio = isVertical ? Math.Clamp(WebcamSizeRatio * 1.55, 0.25, 0.58) : Math.Clamp(WebcamSizeRatio, 0.14, 0.42);
            int camW = (int)(canvasW * baseRatio);
            int camH = WebcamFrameShape switch
            {
                1 => camW,                         // Daire Portre (1:1 Circle)
                2 => (int)(camW * 1.15),           // Dikey Portre Kartı (4:5)
                _ => (int)(camW * 9.0 / 16.0)      // Yuvarlatılmış Sinematik Kutu (16:9)
            };

            if (WebcamFrameShape == 1)
            {
                int side = Math.Min(camW, (int)(canvasH * 0.34));
                camW = side;
                camH = side;
            }

            double ps = canvasW / (isVertical ? 540.0 : 1280.0); // eski tasarım ölçülerini yeni çözünürlüğe ölçekle
            int padX = (int)((isVertical ? 18 : 22) * ps);
            int padY = (int)((isVertical ? 96 : 22) * ps);
            int padT = (int)(22 * ps);

            return WebcamCornerIndex switch
            {
                1 => new Rectangle(padX, canvasH - camH - padY, camW, camH),                     // Sol Alt
                2 => new Rectangle(canvasW - camW - padX, padT, camW, camH),                     // Sağ Üst
                3 => new Rectangle(padX, padT, camW, camH),                                      // Sol Üst
                4 => new Rectangle(canvasW - camW - padX, (canvasH - camH) / 2, camW, camH),     // Sağ Orta Kart
                _ => new Rectangle(canvasW - camW - padX, canvasH - camH - padY, camW, camH)     // Sağ Alt (Varsayılan)
            };
        }

        private void DrawWebcamOverlay(Graphics g, Rectangle rect, bool rounded = true)
        {
            bool circleShape = (WebcamFrameShape == 1 && WebcamCornerIndex != 5);
            bool transparentCutout = (WebcamBgMode == 4);

            GraphicsState savedState = null;
            if (circleShape)
            {
                savedState = g.Save();
                using var clipPath = new GraphicsPath();
                clipPath.AddEllipse(rect);
                g.SetClip(clipPath);
            }

            lock (_webcamLock)
            {
                if (_latestWebcamFrame != null)
                {
                    if (circleShape)
                    {
                        // Daire portrede merkez 1:1 kareyi kırparak çiz (sündürme yapmaz!)
                        int fw = _latestWebcamFrame.Width;
                        int fh = _latestWebcamFrame.Height;
                        int side = Math.Min(fw, fh);
                        var srcSq = new Rectangle((fw - side) / 2, (fh - side) / 2, side, side);
                        g.DrawImage(_latestWebcamFrame, rect, srcSq, GraphicsUnit.Pixel);
                    }
                    else
                    {
                        // Hedef kutunun en-boy oranına göre ortadan kırp (kamera görüntüsü asla sündürülmez/yamulmaz)
                        int fw = _latestWebcamFrame.Width;
                        int fh = _latestWebcamFrame.Height;
                        double ra = rect.Width / (double)Math.Max(1, rect.Height);
                        double fa = fw / (double)Math.Max(1, fh);
                        Rectangle srcFit = fa > ra
                            ? new Rectangle((int)((fw - fh * ra) / 2), 0, (int)(fh * ra), fh)
                            : new Rectangle(0, (int)((fh - fw / ra) / 2), fw, (int)(fw / ra));
                        g.DrawImage(_latestWebcamFrame, rect, srcFit, GraphicsUnit.Pixel);
                    }
                }
                else
                {
                    using var bg = new SolidBrush(System.Drawing.Color.FromArgb(235, 20, 14, 56));
                    g.FillRectangle(bg, rect);
                    using var f = new Font("Segoe UI Semibold", 10.5f);
                    using var br = new SolidBrush(System.Drawing.Color.FromArgb(230, 255, 255, 255));
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("KAMERA AÇILIYOR...", f, br, rect, sf);
                }
            }

            if (savedState != null)
            {
                g.Restore(savedState);
            }

            // Kamera Açısı Kılavuz Çizgileri (Yalnızca Canlı Önizlemede, Kayıt Dışında veya Rehber Açıkken)
            if (ShowWebcamAngleGuide && !IsRecording)
            {
                int eyeY = rect.Y + (int)(rect.Height * 0.36);
                int centerX = rect.X + rect.Width / 2;
                using var guidePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(215, 239, 95, 24), 1.6f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawLine(guidePen, rect.X + 6, eyeY, rect.Right - 6, eyeY);
                g.DrawLine(guidePen, centerX, rect.Y + 6, centerX, rect.Bottom - 6);
                using var fGuide = new Font("Segoe UI Bold", 8f);
                using var brGuide = new SolidBrush(System.Drawing.Color.FromArgb(235, 239, 95, 24));
                g.DrawString("GÖZ HİZASI (1/3)", fGuide, brGuide, new PointF(rect.X + 8, eyeY - 14));
            }

            if (rounded && !transparentCutout)
            {
                using var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(235, 239, 95, 24), 2.5f);
                if (circleShape)
                    g.DrawEllipse(borderPen, rect);
                else
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

            double ts = canvasW / (isVertical ? 540.0 : 1280.0);
            int tileW = (int)((isVertical ? 140 : 185) * ts);
            int tileH = (int)((isVertical ? 82 : 106) * ts);
            int startX = (int)(18 * ts);
            int startY = (int)(18 * ts);
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

                int barH = (int)(22 * ts);
                using var nameBar = new SolidBrush(System.Drawing.Color.FromArgb(190, 9, 13, 22));
                g.FillRectangle(nameBar, new Rectangle(tx, ty + tileH - barH, tileW, barH));

                string label = (p.HasRaisedHand ? "✋ " : "👤 ") + p.Name;
                using var f = new Font("Segoe UI Semibold", (float)(8.5 * ts));
                using var br = new SolidBrush(System.Drawing.Color.White);
                g.DrawString(label, f, br, new PointF(tx + (float)(5 * ts), ty + tileH - (float)(19 * ts)));
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

        private Bitmap _meetJpegBmp;

        private void UpdateMeetJpeg(Bitmap bmp16)
        {
            try
            {
                // Circle Meet için 1280x720'ye küçült (1080p JPEG kodlaması CPU'yu gereksiz yorar)
                _meetJpegBmp ??= new Bitmap(1280, 720, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(_meetJpegBmp))
                {
                    SetFastHq(g);
                    g.DrawImage(bmp16, new Rectangle(0, 0, 1280, 720));
                }
                using var ms = new MemoryStream();
                _meetJpegBmp.Save(ms, ImageFormat.Jpeg);
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

