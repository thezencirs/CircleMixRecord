// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CircleMixRecord.Models;
using CircleMixRecord.Services;

namespace CircleMixRecord
{
    public partial class MainWindow : Window
    {
        private readonly DualCanvasEngine _engine = new();
        private readonly CircleMeetServer _meetServer;
        private readonly DispatcherTimer _uiTimer;

        private CmrProject _currentProject = new();
        private double _playheadSec = 0.0;
        private int _selectedClipIndex = 0;
        private bool _isDraggingPlayhead = false;
        private int _trimDragMode = 0; // 0: yok, 1: sol uç kırpma, 2: sağ uç kırpma
        private double _trimDragStartMouseX = 0.0;
        private double _trimDragOrigSourceStart = 0.0;
        private double _trimDragOrigSourceEnd = 0.0;
        private double _trimDragInitialNetDur = 10.0;
        private bool _uiReady = false;
        private string _activeStudioMode = "Home";
        private string _lastExported16x9Path = "";
        private string _lastExported9x16Path = "";

        private List<DirectShowDevice> _videoDevices = new();
        private List<DirectShowDevice> _audioDevices = new();
        private IntPtr _hwnd;
        private WriteableBitmap _wb16x9;
        private WriteableBitmap _wb9x16;
        private int _uiRenderBusy = 0;

        // Circle Edit Kurgu Oynatıcı & Undo Durumu
        private bool _isPlayerPlaying = false;
        private bool _isPlayerMuted = false;
        private bool _isCircleEditShowingVertical = false;
        private string _activeCircleEditVideoPath = "";
        private double _activeTimelapseSpeed = 1.0;
        private System.Windows.Point _folderDragStartPoint;
        private readonly Stack<List<TimelineClip>> _undoStack = new();

        // Global Hotkey IDs (Circle Studio Kısayolları)
        private const int HK_REC = 9001;            // Ctrl + Shift + R (İkili Kayıt Başlat/Bitir)
        private const int HK_PAUSE = 9002;          // Ctrl + Shift + P (Duraklat/Devam Et)
        private const int HK_ZOOM = 9003;           // Alt + Q (2x Smart Zoom)
        private const int HK_LOCK = 9004;           // Alt + E (İmleç Takibi Sabitle/Aç)
        private const int HK_MARKER = 9005;         // Alt + M (Marker At)
        private const int HK_SCREENSHOT = 9006;     // Alt + S (Ekran Resmi Al)
        private const int HK_REC_VERT = 9007;       // Ctrl + Shift + V (Sadece Dikey 9:16 Kayıt)
        private const int HK_MARKER_CSM = 9008;     // Ctrl + Shift + M (Marker At)
        private const int HK_SPLIT_PART = 9009;     // Ctrl + Shift + N (Kaydı Yeni Part'a Böl)
        private const int HK_SCREENSHOT_CSK = 9010; // Ctrl + Shift + K (Kapak Fotoğrafı Al)
        private const int HK_CAM_TOGGLE = 9011;     // Alt + Shift + C (Kamera Aç/Kapa)
        private const int HK_MIC_MUTE = 9012;       // Alt + Shift + M (Mikrofon Sustur/Aç)
        private const int HK_SYS_MUTE = 9013;       // Alt + Shift + D (Masaüstü Sesi Sustur/Aç)
        private const int HK_SCENE_1 = 9014;        // Alt + Shift + 1 (Sahne 1)
        private const int HK_SCENE_2 = 9015;        // Alt + Shift + 2 (Sahne 2)
        private const int HK_SCENE_3 = 9016;        // Alt + Shift + 3 (Sahne 3)
        private const int HK_SCENE_4 = 9017;        // Alt + Shift + 4 (Sahne 4)
        private const int HK_INTERMISSION = 9030;   // Alt + Shift + B (Bekleme Ekranı)

        public MainWindow()
        {
            InitializeComponent();
            _meetServer = new CircleMeetServer(_engine);
            _meetServer.OnNewMessage += OnMeetMessageReceived;
            _meetServer.OnTunnelReady += pubUrl =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_meetServer.IsRunning) return;
                    TxtMeetUrl.Text = pubUrl;
                    TxtStatusBanner.Text = $"Canlı Oda internete açıldı • Oda Kodu: {_meetServer.RoomCode} • PIN: {_meetServer.RoomPin}";
                }));
            };
            _meetServer.OnRemoteControlRequested += (cid, name) =>
            {
                Dispatcher.BeginInvoke(new Action(() => ShowRemoteControlApprovalDialog(cid, name)));
            };

            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _uiTimer.Tick += UiTimer_Tick;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CircleUpdaterService.CleanupOldExecutable();
            if (!CircleLicenseService.EnsureLicensed(this))
            {
                Close();
                return;
            }
            if (CircleLicenseService.IsFounderMachine() && BtnLicenseManager != null)
            {
                BtnLicenseManager.Visibility = Visibility.Visible;
            }
            CircleUpdaterService.CheckForUpdatesInBackground(this, manual: false);

            _uiReady = true;

            // Global Kısayolları Kaydet (Circle Studio Global Kısayollar + Tam Ekran 3B / CAD içindeyken de çalışır)
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(_hwnd);
                source?.AddHook(WndProc);

                // MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4
                const uint CTRL_SHIFT = 0x0002 | 0x0004;
                const uint ALT_ONLY = 0x0001;
                const uint ALT_SHIFT = 0x0001 | 0x0004;

                RegisterHotKey(_hwnd, HK_REC, CTRL_SHIFT, 0x52);            // Ctrl + Shift + R
                RegisterHotKey(_hwnd, HK_REC_VERT, CTRL_SHIFT, 0x56);       // Ctrl + Shift + V
                RegisterHotKey(_hwnd, HK_PAUSE, CTRL_SHIFT, 0x50);          // Ctrl + Shift + P
                RegisterHotKey(_hwnd, HK_MARKER_CSM, CTRL_SHIFT, 0x4D);     // Ctrl + Shift + M
                RegisterHotKey(_hwnd, HK_SPLIT_PART, CTRL_SHIFT, 0x4E);     // Ctrl + Shift + N
                RegisterHotKey(_hwnd, HK_SCREENSHOT_CSK, CTRL_SHIFT, 0x4B); // Ctrl + Shift + K
                RegisterHotKey(_hwnd, HK_ZOOM, ALT_ONLY, 0x51);             // Alt + Q
                RegisterHotKey(_hwnd, HK_LOCK, ALT_ONLY, 0x45);             // Alt + E
                RegisterHotKey(_hwnd, HK_MARKER, ALT_ONLY, 0x4D);           // Alt + M
                RegisterHotKey(_hwnd, HK_SCREENSHOT, ALT_ONLY, 0x53);       // Alt + S
                RegisterHotKey(_hwnd, HK_CAM_TOGGLE, ALT_SHIFT, 0x43);      // Alt + Shift + C
                RegisterHotKey(_hwnd, HK_MIC_MUTE, ALT_SHIFT, 0x4D);        // Alt + Shift + M
                RegisterHotKey(_hwnd, HK_SYS_MUTE, ALT_SHIFT, 0x44);        // Alt + Shift + D
                RegisterHotKey(_hwnd, HK_SCENE_1, ALT_SHIFT, 0x31);         // Alt + Shift + 1
                RegisterHotKey(_hwnd, HK_SCENE_2, ALT_SHIFT, 0x32);         // Alt + Shift + 2
                RegisterHotKey(_hwnd, HK_SCENE_3, ALT_SHIFT, 0x33);         // Alt + Shift + 3
                RegisterHotKey(_hwnd, HK_SCENE_4, ALT_SHIFT, 0x34);         // Alt + Shift + 4
            RegisterHotKey(_hwnd, HK_INTERMISSION, ALT_SHIFT, 0x42);    // Alt + Shift + B
            }
            catch { }

            TxtEncoderBadge.Text = "Circle GPU Ultra HQ";

            // Yatay ve Dikey klasörleri hazırla
            HardwareEncoderService.GetHorizontalRecordingsDir();
            HardwareEncoderService.GetVerticalRecordingsDir();
            HardwareEncoderService.GetEditedExportsDir();

            // Sıfır-Allocation WriteableBitmap Önizleme Tuvalini Hazırla (Donma / Duraksama = 0)
            _wb16x9 = new WriteableBitmap(DualCanvasEngine.Canvas16W, DualCanvasEngine.Canvas16H, 96, 96, PixelFormats.Bgr24, null);
            _wb9x16 = new WriteableBitmap(DualCanvasEngine.Canvas9W, DualCanvasEngine.Canvas9H, 96, 96, PixelFormats.Bgr24, null);
            ImgPreview16x9.Source = _wb16x9;
            ImgPreview9x16.Source = _wb9x16;

            // Başlangıç boş Circle Edit kurgu projesini hazırla ve doğrudan Sade Stüdyo Tuvalini aç
            InitializeStarterTimelineProject();
            RefreshCircleEditFolderLists();
            RegenerateSocialMediaMetadata();

            var cmdArgs = Environment.GetCommandLineArgs();
            bool openInEdit = cmdArgs.Any(a => string.Equals(a, "--edit", StringComparison.OrdinalIgnoreCase));
            string screenshotTarget = cmdArgs.Any(a => string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase))
                ? System.IO.Path.Combine(HardwareEncoderService.GetBaseStorageDir(), $"CircleMixRecord_Ekran_{DateTime.Now:yyyyMMdd_HHmmss}.png")
                : null;
            for (int i = 1; i < cmdArgs.Length - 1; i++)
            {
                if (string.Equals(cmdArgs[i], "--screenshot", StringComparison.OrdinalIgnoreCase) && !cmdArgs[i + 1].StartsWith("-"))
                {
                    screenshotTarget = cmdArgs[i + 1];
                    break;
                }
            }
            string editTarget = cmdArgs.Skip(1).FirstOrDefault(a => !a.StartsWith("-") && !string.Equals(a, screenshotTarget, StringComparison.OrdinalIgnoreCase) && File.Exists(a));

            if (openInEdit || !string.IsNullOrWhiteSpace(editTarget))
            {
                if (ChkProView != null) ChkProView.IsChecked = true;
                SetStudioViewMode("Edit");
                if (RightTabControl != null) RightTabControl.SelectedIndex = 2; // Kurgu & Medya sekmesi
                if (!string.IsNullOrWhiteSpace(editTarget))
                {
                    bool isVert = editTarget.Contains("9x16", StringComparison.OrdinalIgnoreCase) || editTarget.Contains("Dikey", StringComparison.OrdinalIgnoreCase);
                    LoadVideoIntoCircleEditEditor(editTarget, isVertical: isVert, preserveCurrentProject: false);
                }
                else if (LstHorizFolderFiles.Items.Count > 0 && LstHorizFolderFiles.Items[0] is FolderMediaItem hFirst && hFirst.FilePath.Contains("ElegancePalace", StringComparison.OrdinalIgnoreCase))
                {
                    LstHorizFolderFiles.SelectedIndex = 0;
                    LoadVideoIntoCircleEditEditor(hFirst.FilePath, isVertical: false, preserveCurrentProject: false);
                }
                else if (LstVertFolderFiles.Items.Count > 0 && LstVertFolderFiles.Items[0] is FolderMediaItem vFirst)
                {
                    LstVertFolderFiles.SelectedIndex = 0;
                    LoadVideoIntoCircleEditEditor(vFirst.FilePath, isVertical: true, preserveCurrentProject: false);
                }

                bool merge4kCli = cmdArgs.Any(a => string.Equals(a, "--merge-4k", StringComparison.OrdinalIgnoreCase));
                if (CircleEditMediaPlayer != null && (merge4kCli || !string.IsNullOrWhiteSpace(screenshotTarget)))
                {
                    CircleEditMediaPlayer.IsMuted = true;
                    CircleEditMediaPlayer.Volume = 0;
                }

                // Normal "--edit" açılışı artık hazır demo içerik YÜKLEMEZ: boş editör veya kullanıcının seçtiği dosya.
                // Aşağıdaki sahne keşfi yalnızca ayrı "--merge-4k" komut satırı yolunda çalışır (eksik klasörler korumalı).
                if (_currentProject != null && merge4kCli)
                {
                    string horizRecDir = HardwareEncoderService.GetHorizontalRecordingsDir();
                    string vertRecDir = HardwareEncoderService.GetVerticalRecordingsDir();
                    var flowScenes = Directory.Exists(horizRecDir)
                        ? Directory.GetFiles(horizRecDir, "0*_ElegancePalace_*.mp4")
                            .OrderBy(f => f)
                            .ToList()
                        : new List<string>();
                    if (flowScenes.Count == 0 && Directory.Exists(vertRecDir))
                    {
                        flowScenes = Directory.GetFiles(vertRecDir, "0*_*.mp4")
                            .Concat(Directory.GetFiles(vertRecDir, "1*_*.mp4"))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(f => f)
                            .ToList();
                    }
                    if (flowScenes.Count > 0 && TxtMergedVideosStatus != null)
                    {
                        _currentProject.ExternalMergedVideoPaths = flowScenes;
                        TxtMergedVideosStatus.Text = $"✅ Circle Mix 4K birleştirme listesi ({flowScenes.Count} sahne): " +
                                                     string.Join(" ➔ ", flowScenes.Select(System.IO.Path.GetFileName));
                    }

                    string luxuryAudio = System.IO.Path.Combine(HardwareEncoderService.GetBaseStorageDir(), "Zencircle_Iskocya_BeatSync_Cello_Piano_SubDrop_48kHz.wav");
                    if (File.Exists(luxuryAudio))
                    {
                        _currentProject.ExternalAudioTrackPath = luxuryAudio;
                        if (TxtExternalAudioStatus != null)
                            TxtExternalAudioStatus.Text = $"🎵 Arka plan müziği: {System.IO.Path.GetFileName(luxuryAudio)}";
                    }

                    if (flowScenes.Count > 0)
                    {
                        string[] lensLabels = new[]
                        {
                            "01 • [Kapı Girişi] Crane Down & Dolly-In",
                            "02 • [Perde & Masa] Sinematik Zoom-In",
                            "03 • [Elit Orkestra] Sahne Tilt-Up",
                            "04 • [Mermer Pist] Crane Down & Pull-Out",
                            "05 • [Giriş Dansı] Orbital Zoom-In",
                            "06 • [Elit Davetliler] Alkış & Rack Focus",
                            "07 • [Mermer Masa] Alçak Açı Dolly-In",
                            "08 • [Final] Kameraya Bakış & Crane-Up"
                        };
                        double stepSec = 7.5;
                        _currentProject.TotalDurationSec = Math.Round(flowScenes.Count * stepSec, 2);
                        _currentProject.Clips = new List<TimelineClip>();
                        for (int ci = 0; ci < flowScenes.Count; ci++)
                        {
                            string lbl = ci < lensLabels.Length ? lensLabels[ci] : $"{ci + 1:00} • Sahne {ci + 1}";
                            _currentProject.Clips.Add(new TimelineClip
                            {
                                Label = lbl,
                                SourceStartSec = Math.Round(ci * stepSec, 2),
                                SourceEndSec = Math.Round((ci + 1) * stepSec, 2),
                                SpeedMultiplier = 0.98
                            });
                        }
                        RedrawTimeline();

                        await ExecuteNativeCircleMix4KMergeAsync(showDialogOnComplete: false);
                        if (CircleEditMediaPlayer != null)
                        {
                            CircleEditMediaPlayer.IsMuted = true;
                            CircleEditMediaPlayer.Volume = 0;
                        }
                        await Task.Delay(900);
                        SeekCircleEditPlayer(53.8);
                        RedrawTimeline();
                    }
                    else
                    {
                        TxtStatusBanner.Text = "--merge-4k: Birleştirilecek sahne bulunamadı.";
                    }
                }

                if (!string.IsNullOrWhiteSpace(screenshotTarget))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2600);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                var rootVis = this.Content as FrameworkElement;
                                int w = Math.Max(1280, (int)(rootVis?.ActualWidth > 0 ? rootVis.ActualWidth : ActualWidth));
                                int h = Math.Max(780, (int)(rootVis?.ActualHeight > 0 ? rootVis.ActualHeight : ActualHeight));
                                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                                rtb.Render((Visual)(this.Content ?? this));
                                var enc = new PngBitmapEncoder();
                                enc.Frames.Add(BitmapFrame.Create(rtb));
                                using var fs = File.Create(screenshotTarget);
                                enc.Save(fs);
                            }
                            catch { }
                        });
                    });
                }
            }
            else
            {
                SetStudioViewMode("Record");
            }

            // Dual-Canvas motorunu anında başlat, kameraları arka planda algılayıp bağla
            _engine.OnRawFramesReady += Engine_OnRawFramesReady;
            _engine.StartEngine(null);
            _uiTimer.Start();
            RedrawTimeline();

            // GÜVENLİK: Circle Meet sunucusu / internet tüneli / kod yayını açılışta BAŞLAMAZ.
            // Yalnızca kullanıcı "Canlı Odayı Aç" (veya "Odamı Aç") dediğinde başlar.
            TxtMyRoomCode.Text = _meetServer.RoomCode;
            BtnWelcomeStartMeet.Content = $"Odamı Aç ({_meetServer.RoomCode})";
            UpdateMeetUiState();

            await Task.Run(() =>
            {
                var (vids, auds) = HardwareEncoderService.EnumerateDirectShowDevices();
                _videoDevices = vids;
                _audioDevices = auds;
            });

            PopulateCameraCombo();
            var defaultCam = _videoDevices.FirstOrDefault();
            var fallbackCam = _videoDevices.FirstOrDefault(d => !d.IsStudioBroadcast && !d.IsVirtual) ?? _videoDevices.Skip(1).FirstOrDefault();
            if (defaultCam != null)
            {
                _engine.SwitchCamera(defaultCam, fallbackCam);
            }

            // Kamera arka plan modu varsayılanı: 1 • DSLR Bokeh Bulanık Arka Plan (özel görsel kullanıcı seçerse yüklenir)
            _engine.WebcamBgMode = 1;
            LoadStreamSettingsIntoUi();

            UpdateCoursePrompterUI(0);
        }

        private void InitializeStarterTimelineProject()
        {
            string baseDir = HardwareEncoderService.GetBaseStorageDir();

            _currentProject = new CmrProject
            {
                ProjectName = "Circle_Kurgu_Projesi",
                ProjectDirectory = baseDir,
                TotalDurationSec = 10.0
            };

            RefreshSubtitleListBox();
        }

        private void PopulateCameraCombo()
        {
            CmbCameras.Items.Clear();
            if (_videoDevices.Count == 0)
            {
                CmbCameras.Items.Add("Kamera Bulunamadı (Sadece Ekran)");
                CmbCameras.SelectedIndex = 0;
            }
            else
            {
                foreach (var dev in _videoDevices)
                {
                    CmbCameras.Items.Add(dev);
                }
                CmbCameras.SelectedIndex = 0; // #1 Varsayılan: ⭐ Camera (Circle Broadcast)
            }

            if (CmbMicrophones != null)
            {
                CmbMicrophones.Items.Clear();
                if (_audioDevices.Count == 0)
                {
                    CmbMicrophones.Items.Add("⭐ Mikrofon (Circle Broadcast) (Varsayılan)");
                    CmbMicrophones.SelectedIndex = 0;
                }
                else
                {
                    foreach (var adev in _audioDevices)
                    {
                        CmbMicrophones.Items.Add(adev);
                    }
                    CmbMicrophones.SelectedIndex = 0; // #1 Varsayılan: ⭐ Mikrofon (Circle Broadcast)
                }
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HK_REC:
                        BtnStartStopRec_Click(null, null);
                        handled = true;
                        break;
                    case HK_REC_VERT:
                        StartStopVerticalOnlyRec();
                        handled = true;
                        break;
                    case HK_PAUSE:
                        BtnPauseRec_Click(null, null);
                        handled = true;
                        break;
                    case HK_ZOOM:
                        BtnToggleSmartZoom_Click(null, null);
                        handled = true;
                        break;
                    case HK_LOCK:
                        BtnToggleFollow_Click(null, null);
                        handled = true;
                        break;
                    case HK_MARKER:
                    case HK_MARKER_CSM:
                        BtnAddMarker_Click(null, null);
                        handled = true;
                        break;
                    case HK_SPLIT_PART:
                        _ = SplitRecordingPartAsync();
                        handled = true;
                        break;
                    case HK_SCREENSHOT:
                    case HK_SCREENSHOT_CSK:
                        BtnTakeScreenshot_Click(null, null);
                        handled = true;
                        break;
                    case HK_CAM_TOGGLE:
                        BtnToggleWebcamQuick_Click(null, null);
                        handled = true;
                        break;
                    case HK_MIC_MUTE:
                        ChkMuteMic.IsChecked = !(ChkMuteMic.IsChecked == true);
                        TxtStatusBanner.Text = ChkMuteMic.IsChecked == true ? "🔇 Mikrofon Susturuldu (Alt+Shift+M)" : "🎙️ Mikrofon Açıldı (Alt+Shift+M)";
                        handled = true;
                        break;
                    case HK_SYS_MUTE:
                        ChkMuteSys.IsChecked = !(ChkMuteSys.IsChecked == true);
                        TxtStatusBanner.Text = ChkMuteSys.IsChecked == true ? "🔇 Masaüstü Sesi Susturuldu (Alt+Shift+D)" : "🔊 Masaüstü Sesi Açıldı (Alt+Shift+D)";
                        handled = true;
                        break;
                    case HK_SCENE_1:
                        LstScenes.SelectedIndex = 0;
                        handled = true;
                        break;
                    case HK_SCENE_2:
                        LstScenes.SelectedIndex = 1;
                        handled = true;
                        break;
                    case HK_SCENE_3:
                        LstScenes.SelectedIndex = 2;
                        handled = true;
                        break;
                    case HK_INTERMISSION:
                        ToggleIntermission();
                        handled = true;
                        break;
                    case HK_SCENE_4:
                        LstScenes.SelectedIndex = 3;
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void Engine_OnRawFramesReady(byte[] raw16, int w16, int h16, byte[] raw9, int w9, int h9)
        {
            if (_activeStudioMode == "Edit") return; // Circle Edit Kurgu ekranındayken canlı önizleme çizimi yapma (0% CPU)

            // Eğer UI thread bir önceki kareyi henüz çiziyorsa kuyruk biriktirme (Sıfır Donma / Sıfır Lag!)
            if (System.Threading.Interlocked.CompareExchange(ref _uiRenderBusy, 1, 0) != 0)
                return;

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    _wb16x9?.WritePixels(new Int32Rect(0, 0, w16, h16), raw16, w16 * 3, 0);
                    _wb9x16?.WritePixels(new Int32Rect(0, 0, w9, h9), raw9, w9 * 3, 0);
                }
                catch { }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _uiRenderBusy, 0);
                }
            }));
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            // WASAPI Ses Seviyeleri
            double micDb = _engine.AudioEngine.CurrentMicDb;
            double sysDb = _engine.AudioEngine.CurrentSystemDb;
            TxtMicDb.Text = $"{micDb:0} dB";
            UpdateStreamStatsUi();
            if (_engine.AudioEngine.TryConsumeNotice(out string micNotice) && TxtStatusBanner != null)
                TxtStatusBanner.Text = micNotice;
            TxtSysDb.Text = $"{sysDb:0} dB";
            BarMicLevel.Value = Math.Clamp(60.0 + micDb, 0, 60);
            BarSysLevel.Value = Math.Clamp(60.0 + sysDb, 0, 60);

            // Canlı Sağlık & Performans Puanı (100%)
            if (TxtHealthBadge != null)
            {
                TxtHealthBadge.Text = _activeStudioMode == "Edit"
                    ? "100%"
                    : $"{_engine.StudioHealthScore}%";
            }

            // Circle Edit Video Oynatıcı Playhead Senkronizasyonu + Silinen/Kırpılan Klipleri Otomatik Atlama + Canlı Timelapse Hızı
            if (_activeStudioMode == "Edit" && _isPlayerPlaying && CircleEditMediaPlayer.Source != null && !_isDraggingPlayhead)
            {
                try
                {
                    double posSec = CircleEditMediaPlayer.Position.TotalSeconds;
                    if (posSec >= 0)
                    {
                        _playheadSec = posSec;
                        if (_currentProject?.Clips != null && _currentProject.Clips.Count > 0)
                        {
                            var activeClips = _currentProject.Clips.Where(x => !x.IsDeleted && x.SourceDurationSec >= 0.08).OrderBy(x => x.SourceStartSec).ToList();
                            if (activeClips.Count > 0)
                            {
                                var currentActive = activeClips.FirstOrDefault(x => _playheadSec >= x.SourceStartSec - 0.04 && _playheadSec < x.SourceEndSec);
                                if (currentActive != null)
                                {
                                    _selectedClipIndex = _currentProject.Clips.IndexOf(currentActive);
                                    double targetRatio = Math.Clamp(currentActive.SpeedMultiplier, 0.25, 8.0);
                                    if (Math.Abs(CircleEditMediaPlayer.SpeedRatio - targetRatio) > 0.05)
                                    {
                                        CircleEditMediaPlayer.SpeedRatio = targetRatio;
                                    }
                                }
                                else
                                {
                                    // Silinmiş (Del) veya baştan/ortadan kırpılmış bölgedeyiz -> anında sıradaki sağlam klibe birleştirerek atla!
                                    var nextActive = activeClips.FirstOrDefault(x => x.SourceStartSec > _playheadSec - 0.02);
                                    if (nextActive != null)
                                    {
                                        CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(nextActive.SourceStartSec);
                                        _playheadSec = nextActive.SourceStartSec;
                                        _selectedClipIndex = _currentProject.Clips.IndexOf(nextActive);
                                    }
                                    else
                                    {
                                        // Son sağlam klibin bitişine ulaşıldı (sondan kırpılmış kısma girmeden durdur)
                                        _playheadSec = activeClips.Last().SourceEndSec;
                                        PauseCircleEditPlayer();
                                    }
                                }
                            }
                        }

                        double curMag = SourceSecToMagneticSec(_playheadSec, _selectedClipIndex, out double netTotalDur);
                        TxtTimelineTimecode.Text = $"{FormatShortTime(curMag)} / {FormatShortTime(netTotalDur)}";
                        UpdateLiveSubtitleOverlay();
                        RedrawTimeline();
                    }
                }
                catch { }
            }

            // Kayıt Sayacı
            if (_engine.IsRecording)
            {
                var ts = _engine.RecordingElapsed;
                TxtRecTimer.Text = _engine.IsPaused
                    ? $"DURAKLATILDI • {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
                    : $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
                DotRecStatus.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            }
            else
            {
                TxtRecTimer.Text = "00:00:00";
                DotRecStatus.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
            }

            string res16 = _engine.Record4KHorizontal ? "4K • 60 FPS" : "1080p • 60 FPS";
            string zoomLbl = _engine.IsSmartZoomActive ? $" • {_engine.ZoomFactor16x9:0.0}x" : "";
            TxtBadge16x9.Text = $"{res16}{zoomLbl}";
            TxtBadge9x16.Text = _engine.LiveUncropPercent > 1
                ? $"1080x1920 • Uncrop %{_engine.LiveUncropPercent:0}"
                : "1080x1920 • 60 FPS";
        }

        // ======================= 4 ADIMLI SADE GÖRÜNÜM & TAM CIRCLE EDIT KURGU EKRANI GEÇİŞİ =======================

        private void SetStudioViewMode(string mode)
        {
            _activeStudioMode = mode;
            bool pro = ChkProView?.IsChecked == true;
            bool isEdit = (mode == "Edit");

            // Circle Edit ekranındayken arka plan ekran yakalamayı uyut (Bilgisayarı 0% yorar!)
            _engine.IsEditorFocusMode = isEdit;
            if (!isEdit && _isPlayerPlaying)
            {
                PauseCircleEditPlayer();
            }

            OverlayWelcome.Visibility = mode == "Home" ? Visibility.Visible : Visibility.Collapsed;
            OverlaySocial.Visibility = mode == "Social" ? Visibility.Visible : Visibility.Collapsed;

            // Orta Ekran Geçişi: Edit modunda Kurgu Monitörü, diğerlerinde Kayıt Ekranı!
            if (BdrLiveRecordPreview != null)
                BdrLiveRecordPreview.Visibility = isEdit ? Visibility.Collapsed : Visibility.Visible;
            if (BdrCircleEditCenterMonitor != null)
                BdrCircleEditCenterMonitor.Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed;

            // Circle Mod Seçici vurguları (Transparent pasif & #EF5F18 aktif)
            var idleBg = System.Windows.Media.Brushes.Transparent;
            var activeOrange = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));

            BtnModeHome.Background = mode == "Home" ? activeOrange : idleBg;
            BtnModeRecord.Background = (mode == "Record" || mode == "Meet") ? activeOrange : idleBg;
            BtnModeEdit.Background = mode == "Edit" ? activeOrange : idleBg;
            BtnModeSocial.Background = mode == "Social" ? activeOrange : idleBg;

            switch (mode)
            {
                case "Home":
                    SetPanelsVisibility(showLeftRecord: false, showLeftEditPool: false, showRight: false, showTimeline: false);
                    break;

                case "Record":
                    if (RightTabControl != null && RightTabControl.SelectedIndex > 1)
                        RightTabControl.SelectedIndex = 0; // 🎓 1+1+1 Eğitim Seti sekmesi
                    SetPanelsVisibility(showLeftRecord: pro, showLeftEditPool: false, showRight: pro, showTimeline: pro);
                    break;

                case "Meet":
                    RightTabControl.SelectedIndex = 4; // Yayın & Meet sekmesi
                    SetPanelsVisibility(showLeftRecord: pro, showLeftEditPool: false, showRight: true, showTimeline: pro);
                    break;

                case "Edit":
                    if (RightTabControl.SelectedIndex == 4 || RightTabControl.SelectedIndex == 0)
                        RightTabControl.SelectedIndex = 3; // Renk, Hız Eğrisi & Geçiş sekmesi
                    RefreshCircleEditFolderLists();
                    // Edit modunda Sol Panelde Açık Yatay & Dikey Klasörler her zaman açık!
                    SetPanelsVisibility(showLeftRecord: false, showLeftEditPool: true, showRight: pro, showTimeline: true);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        RedrawTimeline();
                        RedrawSpeedCurveCanvas();
                    }));
                    break;

                case "Social":
                    RegenerateSocialMediaMetadata();
                    SetPanelsVisibility(showLeftRecord: false, showLeftEditPool: false, showRight: false, showTimeline: false);
                    break;
            }
        }

        private void SetPanelsVisibility(bool showLeftRecord, bool showLeftEditPool, bool showRight, bool showTimeline)
        {
            ColLeftPanel.Width = (showLeftRecord || showLeftEditPool) ? new GridLength(285) : new GridLength(0);
            BdrLeftPanel.Visibility = showLeftRecord ? Visibility.Visible : Visibility.Collapsed;
            if (BdrCircleEditMediaPool != null)
                BdrCircleEditMediaPool.Visibility = showLeftEditPool ? Visibility.Visible : Visibility.Collapsed;

            ColRightPanel.Width = showRight ? new GridLength(325) : new GridLength(0);
            BdrRightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;

            RowTimeline.Height = showTimeline ? new GridLength(210) : new GridLength(0);
            BdrTimelinePanel.Visibility = showTimeline ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnModeHome_Click(object sender, RoutedEventArgs e) => SetStudioViewMode("Home");
        private void BtnModeRecord_Click(object sender, RoutedEventArgs e) => SetStudioViewMode("Record");
        private void BtnModeEdit_Click(object sender, RoutedEventArgs e) => SetStudioViewMode("Edit");
        private void BtnModeSocial_Click(object sender, RoutedEventArgs e) => SetStudioViewMode("Social");

        private void ChkProView_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (_activeStudioMode == "Home" || _activeStudioMode == "Social")
                SetStudioViewMode("Record");
            else
                SetStudioViewMode(_activeStudioMode);
        }

        private void BtnWelcomeStartRecord_Click(object sender, RoutedEventArgs e)
        {
            SetStudioViewMode("Record");
            TxtStatusBanner.Text = "Circle Kayıt Modu hazır! ● İkili Kaydı Başlat butonuna veya Ctrl+Shift+R tuşlarına basarak hemen kayda girebilirsiniz.";
        }

        private void BtnWelcomeStartMeet_Click(object sender, RoutedEventArgs e)
        {
            BtnQuickStartMeet_Click(sender, e);
        }

        private async void BtnQuickStartMeet_Click(object sender, RoutedEventArgs e)
        {
            if (!_meetServer.IsRunning)
            {
                await _meetServer.StartServerAsync();
                UpdateMeetUiState();
                if (!_meetServer.IsRunning)
                {
                    TxtStatusBanner.Text = "⚠️ Circle Meet odası başlatılamadı (5880 portu kullanımda olabilir).";
                    return;
                }
            }

            ChkShowMeetTiles.IsChecked = true;
            _engine.ShowMeetingOverlay = true;
            SetStudioViewMode("Meet");

            string invite = _meetServer.BestInviteUrl;
            try
            {
                System.Windows.Clipboard.SetText(invite);
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = invite,
                    UseShellExecute = true
                });
            }
            catch { }

            TxtStatusBanner.Text = $"Canlı Toplantı (Circle Meet) açıldı, davet linki kopyalandı • PIN: {_meetServer.RoomPin} — Kayda basarak toplantıyı 16:9 + 9:16 kaydedebilirsiniz!";
        }

        /// <summary>Meet panelindeki link / PIN / buton durumlarını sunucu durumuna göre günceller.</summary>
        private void UpdateMeetUiState()
        {
            bool running = _meetServer.IsRunning;
            if (TxtMeetUrl != null)
                TxtMeetUrl.Text = running ? _meetServer.BestInviteUrl : "Oda kapalı — davet linki için 'Canlı Odayı Aç'a basın";
            if (TxtMeetPin != null)
                TxtMeetPin.Text = running ? _meetServer.RoomPin : "——————";
            if (BtnToggleMeet != null)
            {
                BtnToggleMeet.Content = running ? "● Canlı Odayı Kapat" : "● Canlı Odayı Aç";
                BtnToggleMeet.Background = running
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            }
        }

        /// <summary>
        /// Katılımcı uzaktan kontrol istediğinde host'a marka renklerinde "İzin ver / Reddet" onay penceresi gösterir.
        /// </summary>
        private void ShowRemoteControlApprovalDialog(string clientId, string participantName)
        {
            if (!_meetServer.IsRunning || !_meetServer.AllowRemoteControl) return;

            var orange = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            var surface = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 19, 76));
            var border = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 42, 148));
            var card = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));

            var dlg = new Window
            {
                Title = "Circle Uzaktan Kontrol",
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = surface,
                Topmost = true,
                ShowInTaskbar = false
            };

            bool approved = false;
            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = "🖱️ CIRCLE UZAKTAN KONTROL İSTEĞİ",
                Foreground = orange,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = $"{participantName} uzaktan kontrol istiyor — İzin ver / Reddet",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            root.Children.Add(new TextBlock
            {
                Text = "İzin verirseniz bu katılımcı farenizi ve klavyenizi kullanabilir. İzni istediğiniz an 'Uzaktan Fare & Klavye Kontrolü' kutucuğunu kapatarak geri alabilirsiniz.",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 181, 253)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var btnDeny = new System.Windows.Controls.Button
            {
                Content = "Reddet",
                Padding = new Thickness(18, 7, 18, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Background = card,
                BorderBrush = border,
                Foreground = System.Windows.Media.Brushes.White,
                IsCancel = true,
                IsDefault = true
            };
            var btnAllow = new System.Windows.Controls.Button
            {
                Content = "İzin ver",
                Padding = new Thickness(18, 7, 18, 7),
                Background = orange,
                BorderBrush = orange,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            btnDeny.Click += (_, __) => { approved = false; dlg.Close(); };
            btnAllow.Click += (_, __) => { approved = true; dlg.Close(); };
            buttons.Children.Add(btnDeny);
            buttons.Children.Add(btnAllow);
            root.Children.Add(buttons);
            dlg.Content = root;

            try { dlg.ShowDialog(); } catch { }

            _meetServer.SetRemoteApproval(clientId, approved);
            TxtStatusBanner.Text = approved
                ? $"🖱️ {participantName} için Circle Uzaktan Kontrol izni verildi."
                : $"🔒 {participantName} adlı katılımcının uzaktan kontrol isteği reddedildi.";
        }

        // ======================= SAHNE VE KAYNAK OLAYLARI =======================

        private void LstScenes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            switch (LstScenes.SelectedIndex)
            {
                case 0: // Eğitim + Köşe Kamera
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = true;
                    ChkShowMeetTiles.IsChecked = true;
                    if (CmbWebcamCorner != null) CmbWebcamCorner.SelectedIndex = 0;
                    CmbPortraitLayout.SelectedIndex = 2; // Ekran + Kamera: telefonda en okunur dikey düzen
                    break;
                case 1: // Canlı Toplantı (Circle Meet + Katılımcılar)
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = true;
                    ChkShowMeetTiles.IsChecked = true;
                    RightTabControl.SelectedIndex = 4;
                    break;
                case 2: // Tam Ekran 3D Modelleme
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = false;
                    CmbPortraitLayout.SelectedIndex = 2; // Kamera kapalıyken ekran kartı ortada, okunur yakınlıkta
                    break;
                case 3: // Dikey Split (Üst Ekran + Alt Kamera)
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = true;
                    CmbPortraitLayout.SelectedIndex = 2;
                    break;
                case 4: // Sadece Eğitmen Kamerası (İntro / Outro)
                    ChkShowScreen.IsChecked = false;
                    ChkShowWebcam.IsChecked = true;
                    if (CmbWebcamCorner != null) CmbWebcamCorner.SelectedIndex = 5;
                    break;
            }
        }

        private void CmbCameras_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            if (CmbCameras.SelectedItem is DirectShowDevice dev)
            {
                var fallback = _videoDevices.FirstOrDefault(d => !d.IsStudioBroadcast && !d.IsVirtual) ?? _videoDevices.Skip(1).FirstOrDefault();
                _engine.SwitchCamera(dev, fallback);
                TxtStatusBanner.Text = $"Kamera bağlandı: {dev.FriendlyName}";
            }
        }

        private void CmbMicrophones_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            if (CmbMicrophones.SelectedItem is DirectShowDevice adev)
            {
                _engine.AudioEngine.PreferredMicName = adev.FriendlyName;
                TxtStatusBanner.Text = $"🎙️ Aktif Stüdyo Mikrofonu: {adev.FriendlyName} (+{_engine.AudioEngine.MicGainBoostDb:0} dB • ses seviyesi kayıt sonunda otomatik dengelenir)";
            }
        }

        private void WebcamStudioOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;

            if (CmbWebcamBgMode != null)
                _engine.WebcamBgMode = Math.Max(0, CmbWebcamBgMode.SelectedIndex);

            if (SldSubjectSharpness != null)
            {
                _engine.WebcamSubjectSharpness = SldSubjectSharpness.Value;
                if (TxtSubjectSharpVal != null)
                    TxtSubjectSharpVal.Text = $"%{(int)SldSubjectSharpness.Value}";
            }

            if (ChkStudioKeyLight != null)
                _engine.WebcamStudioKeyLight = ChkStudioKeyLight.IsChecked == true;

            if (SldWebcamZoom != null)
            {
                _engine.WebcamDigitalZoom = SldWebcamZoom.Value;
                if (TxtWebcamZoomVal != null)
                    TxtWebcamZoomVal.Text = $"{SldWebcamZoom.Value:0.00}x";
            }

            if (SldWebcamTiltY != null)
            {
                _engine.WebcamTiltY = SldWebcamTiltY.Value / 100.0;
                if (TxtWebcamTiltVal != null)
                    TxtWebcamTiltVal.Text = $"{(int)SldWebcamTiltY.Value:+#;-#;0}%";
            }

            if (SldWebcamPanX != null && SldWebcamRoll != null)
            {
                _engine.WebcamPanX = SldWebcamPanX.Value / 100.0;
                _engine.WebcamRollDeg = SldWebcamRoll.Value;
                if (TxtWebcamPanVal != null)
                    TxtWebcamPanVal.Text = $"{(int)SldWebcamPanX.Value:+#;-#;0}% • {(int)SldWebcamRoll.Value:+#;-#;0}°";
            }

            if (CmbWebcamShape != null)
                _engine.WebcamFrameShape = Math.Max(0, CmbWebcamShape.SelectedIndex);

            if (CmbWebcamCorner != null)
                _engine.WebcamCornerIndex = Math.Max(0, CmbWebcamCorner.SelectedIndex);

            if (ChkWebcamAngleGuide != null)
                _engine.ShowWebcamAngleGuide = ChkWebcamAngleGuide.IsChecked == true;
        }

        private void CmbCameraAnglePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || CmbCameraAnglePreset == null) return;
            switch (CmbCameraAnglePreset.SelectedIndex)
            {
                case 0: // 🎯 Göz Hizası Eğitim Portresi (1.22x)
                    SldWebcamZoom.Value = 1.22;
                    SldWebcamTiltY.Value = -4;
                    SldWebcamPanX.Value = 0;
                    SldWebcamRoll.Value = 0;
                    break;
                case 1: // 📐 Geniş Stüdyo Açısı (1.00x)
                    SldWebcamZoom.Value = 1.00;
                    SldWebcamTiltY.Value = 0;
                    SldWebcamPanX.Value = 0;
                    SldWebcamRoll.Value = 0;
                    break;
                case 2: // 🔍 Circle Yakın Portre (1.50x)
                    SldWebcamZoom.Value = 1.50;
                    SldWebcamTiltY.Value = -6;
                    SldWebcamPanX.Value = 0;
                    break;
                case 3: // ↗ Üstten Kamera Açısı Düzelt (Tilt -14%)
                    SldWebcamZoom.Value = 1.28;
                    SldWebcamTiltY.Value = -14;
                    break;
                case 4: // ↘ Alttan Kamera Açısı Düzelt (Tilt +14%)
                    SldWebcamZoom.Value = 1.28;
                    SldWebcamTiltY.Value = 14;
                    break;
            }
            WebcamStudioOptions_Changed(sender, e);
            TxtStatusBanner.Text = $"🎥 Kamera Açısı Ön Ayarı uygulandı: {(CmbCameraAnglePreset.SelectedItem as ComboBoxItem)?.Content}";
        }

        private void BtnCalibrateWebcamBg_Click(object sender, RoutedEventArgs e)
        {
            _engine.CalibrateWebcamBackgroundNow();
            TxtStatusBanner.Text = "🎯 Arka Plan Tanıma kalibre edildi! AI portre maskesi yüz/omuz silüetinize kilitlendi ve netleme (%75 DSLR Focus) aktif.";
        }

        private void BtnPickWebcamBgImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Kamera Arka Plan Görseli Seç (Render veya Stüdyo Görseli)",
                Filter = "Görsel Dosyaları (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
            };
            string defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrWhiteSpace(defaultDir) && Directory.Exists(defaultDir)) dlg.InitialDirectory = defaultDir;

            if (dlg.ShowDialog() == true)
            {
                _engine.SetCustomWebcamBackground(dlg.FileName);
                if (CmbWebcamBgMode != null) CmbWebcamBgMode.SelectedIndex = 5;
                TxtStatusBanner.Text = $"🖼️ Kamera arka planı değiştirildi: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
        }

        private void MicAudioOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (SldMicGainDb != null)
            {
                _engine.AudioEngine.MicGainBoostDb = SldMicGainDb.Value;
                if (TxtMicBoostVal != null)
                    TxtMicBoostVal.Text = $"+{(int)SldMicGainDb.Value} dB (Stüdyo)";
            }
            if (ChkAutoVoiceBoost != null)
                _engine.AudioEngine.AutoVoiceBoost = ChkAutoVoiceBoost.IsChecked == true;
            if (ChkNoiseGate != null)
                _engine.AudioEngine.NoiseGateEnabled = ChkNoiseGate.IsChecked == true;
        }

        private async void BtnTestMicVoice_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRecording)
            {
                TxtStatusBanner.Text = "Kayıt sırasındayken ses testi yerine canlı dB çubuğunu takip edebilirsiniz.";
                return;
            }

            BtnTestMicVoice.IsEnabled = false;
            string testDir = System.IO.Path.Combine(HardwareEncoderService.GetBaseStorageDir(), "Proje_Verileri", "_SesTesti");
            Directory.CreateDirectory(testDir);
            string testMicWav = System.IO.Path.Combine(testDir, "MicTest_Boosted.wav");
            string testSysWav = System.IO.Path.Combine(testDir, "SysTest_Dummy.wav");

            try
            {
                _engine.AudioEngine.StartRecordingToWav(testMicWav, testSysWav);
                for (int s = 3; s >= 1; s--)
                {
                    BtnTestMicVoice.Content = $"🎙️ Konuşun... ({s} sn)";
                    TxtStatusBanner.Text = $"🎙️ Mikrofon Ses Testi: Lütfen normal eğitim ses tonunuzla konuşun ({s} sn)... [+{_engine.AudioEngine.MicGainBoostDb:0} dB Boost Aktif]";
                    await Task.Delay(1000);
                }
                _engine.AudioEngine.StopRecordingWav();

                float maxPeak = _engine.AudioEngine.RecordedMicPeaks.Count > 0
                    ? _engine.AudioEngine.RecordedMicPeaks.Max()
                    : 0f;
                double peakDb = maxPeak > 0.0001f ? 20.0 * Math.Log10(maxPeak) : -60.0;

                BtnTestMicVoice.Content = "🔊 Kaydedilen Sesiniz Çalınıyor...";
                TxtStatusBanner.Text = $"🔊 Sesiniz dinletiliyor! Yakalanan Tepe Seviye: {peakDb:0.0} dB (Circle & thezencirs Stüdyo Standardı)";

                if (File.Exists(testMicWav))
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            using var player = new System.Media.SoundPlayer(testMicWav);
                            player.PlaySync();
                        }
                        catch { }
                    });
                }

                TxtStatusBanner.Text = $"✅ Ses Testi Tamamlandı (Tepe: {peakDb:0.0} dB). Sesiniz artık güçlü, tok ve net kaydediliyor!";
            }
            catch
            {
                TxtStatusBanner.Text = "Ses testi tamamlandı.";
            }
            finally
            {
                BtnTestMicVoice.Content = "🎙️ Sesimi Test Et (3 sn Kaydet & Dinle)";
                BtnTestMicVoice.IsEnabled = true;
            }
        }

        private void CmbCourseModule_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || CmbCourseModule == null) return;
            UpdateCoursePrompterUI(CmbCourseModule.SelectedIndex);
        }

        private void UpdateCoursePrompterUI(int moduleIdx)
        {
            if (TxtCoursePrompter == null || TxtCourseModuleHeader == null) return;

            switch (moduleIdx)
            {
                case 0:
                    TxtCourseModuleHeader.Text = "SAAT 1 (60 DK): AI MODELLEME & KURULUMLAR (3 YOL)";
                    TxtCoursePrompter.Text =
                        "⏱️ [00:00 - 15:00] BÖLÜM 1: ZORUNLU KURULUMLAR & SİSTEM HAZIRLIĞI\n" +
                        "• Circle Studio & thezencirs çalışma ortamı + 3B modelleme köprüleri kurulumu\n" +
                        "• Global Mimari Kurallar: Kutu kutu / çizgi film modelleme yasağı, gerçek cm/mm birim ve katman (Layer) standardı\n\n" +
                        "🚀 [15:00 - 34:00] 1. YOL: OTONOM 3B CAD/BIM MİMARİ MODELLEME\n" +
                        "• Mimari kat planlarını doğrudan okuyup gerçek ölçekte 3B kütle üretimi\n" +
                        "• Duvar, 6.34m galeri boşluğu, U-dönüşlü uçan ceviz merdiven, tül/fon perde rayı, duvar çıtası ve gizli LED kanallarını otonom inşa ettirme\n\n" +
                        "🪑 [34:00 - 48:00] 2. YOL: GÖRSELDEN / ESKİZDEN 3B MESH & MOBİLYA ÜRETİMİ\n" +
                        "• Tek referans fotoğraftan özel tasarım berjer, organik sehpa ve banyo seramiği üretimi\n" +
                        "• Sahneyi şişirmeden temiz dörtgen poligon (Quad Retopology) ile yerleştirme\n\n" +
                        "⚡ [48:00 - 60:00] 3. YOL: HİBRİT PARAMETRİK AI MİMARİ ASİSTAN\n" +
                        "• Yarım kalan projeleri bir mimar gözüyle tamamlatma, cephe fuga/silme otomasyonu ve temiz poligon optimizasyonu.";
                    break;

                case 1:
                    TxtCourseModuleHeader.Text = "SAAT 2 (60 DK): AI FOTOGERÇEKÇİ RENDER & KURULUMLAR (3 YOL)";
                    TxtCoursePrompter.Text =
                        "⏱️ [00:00 - 12:00] BÖLÜM 1: RENDER MOTORU & KANAL KURULUMLARI\n" +
                        "• Derinlik (ZDepth), Normal, Renk Maskesi (WireColor) ve Işık Karışımı kanallarının kurulumu\n" +
                        "• Kamera Açısı & Lens Kuralları: 16mm-24mm Mimari Geniş Açı ve 2 Nokta Perspektif (Dikey Düzeltme)\n\n" +
                        "💎 [12:00 - 30:00] 1. YOL: CIRCLE RENDER 2 AŞAMALI HİBRİT 1:1 MİMARİ RENDER AKIŞI\n" +
                        "• 3B yazılımdan alınan 30 saniyelik ham kütleyi %0 geometri sapmasıyla (1:1 sadakat) dergi kalitesinde fotogerçekçi rendera dönüştürme\n" +
                        "• Gündüz Güneşli, Altın Saat Günbatımı ve Mavi Saat Gece LED varyasyonları\n\n" +
                        "🎛️ [30:00 - 46:00] 2. YOL: DERİNLİK & MASKE KİLİTLİ 8K PBR DOKU YÜKSELTME\n" +
                        "• Derinlik ve malzeme maskeleriyle mermer damarlarını, keten tül perde geçirgenliğini ve honlanmış traverteni 4K/8K PBR netliğe yükseltme\n\n" +
                        "☀️ [46:00 - 60:00] 3. YOL: GERÇEK ZAMANLI IŞIN İZLEME & AI DOKU İYİLEŞTİRME\n" +
                        "• Müşteri yanında saniyeler içinde malzeme/ışık değiştirme, insan ve peyzaj dokularını gerçek fotoğraf kalitesine dönüştürme.";
                    break;

                case 2:
                    TxtCourseModuleHeader.Text = "SAAT 3 (60 DK): AI SİNEMATİK ANİMASYON & 4K KURGU (2 YOL)";
                    TxtCoursePrompter.Text =
                        "⏱️ [00:00 - 12:00] BÖLÜM 1: SİNEMATİK ANİMASYON & CIRCLE STÜDYO KURULUMLARI\n" +
                        "• Sinematik kamera fiziği (24mm sabit lens, erime/bozulma önleme kuralları)\n" +
                        "• Circle Studio, GPU Donanım Kodlayıcı ve -14 LUFS Ses Mühendisliği kurulumu\n\n" +
                        "🎬 [12:00 - 36:00] 1. YOL: İLK KARE -> SON KARE KİLİTLİ SİNEMATİK MİMARİ FİLM\n" +
                        "• Başlangıç ve bitiş karelerini kilitleyerek binanın tasarımını hiç bozmadan sinematik dış ve iç mekan kamera yürüyüşü\n" +
                        "• Rüzgarda hafif dalgalanan 6m tül perdeler, şömine alevi, su yansımaları ve yaşayan mekan senaryoları\n\n" +
                        "🎥 [36:00 - 50:00] 2. YOL: 3B KAMERA YOLU & YAŞAYAN MEKAN KURGUSU\n" +
                        "• 3B sahne kamera yolunu birebir koruyarak fotogerçekçi 60 FPS mimari film üretimi\n\n" +
                        "🎵 [50:00 - 60:00] FİNAL: CIRCLE İLE 4K BEAT-SYNC KURGU & SUNUM STANDARDI\n" +
                        "• 08.0s • 15.7s • 23.4s müzik vuruşlarına kilitli 4K UHD kurgu, 16:9 yatay + 9:16 dikey çift çıktı ve müşteri teslim standardı.";
                    break;
            }
        }

        private string GetCoursePackageDirectory()
        {
            // Uygulama klasörüne göreli (kişisel masaüstü yolu yok)
            string dir = System.IO.Path.Combine(HardwareEncoderService.GetBaseStorageDir(), "Egitim_Seti");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void BtnOpenCoursePackage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = GetCoursePackageDirectory();
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                TxtStatusBanner.Text = $"🎓 1+1+1 Saatlik Circle & thezencirs Eğitim Seti klasörü açıldı: {dir}";
            }
            catch { }
        }

        private void BtnOpenCourseGuideHtml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string htmlPath = System.IO.Path.Combine(GetCoursePackageDirectory(), "thezencirs_Circle_1_1_1_Egitim_Rehberi.html");
                if (File.Exists(htmlPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = htmlPath, UseShellExecute = true });
                }
                else
                {
                    BtnOpenCoursePackage_Click(sender, e);
                }
            }
            catch { }
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            var (vids, auds) = HardwareEncoderService.EnumerateDirectShowDevices();
            _videoDevices = vids;
            _audioDevices = auds;
            PopulateCameraCombo();
            TxtStatusBanner.Text = $"{_videoDevices.Count} kamera ve {_audioDevices.Count} mikrofon algılandı (Varsayılan: Circle Broadcast).";
        }

        private void SourceOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            _engine.ShowScreen = ChkShowScreen.IsChecked == true;
            _engine.ShowWebcam = ChkShowWebcam.IsChecked == true;
            _engine.MirrorWebcam = ChkMirrorWebcam.IsChecked == true;
            _engine.NaturalWhiteBalanceWebcam = ChkNaturalWb.IsChecked == true;
            _engine.ShowMeetingOverlay = ChkShowMeetTiles.IsChecked == true;
            _engine.Record4KHorizontal = ChkRecord4K.IsChecked == true;
        }

        private void SldCamScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            _engine.WebcamSizeRatio = SldCamScale.Value / 100.0;
            if (TxtCamScaleVal != null)
                TxtCamScaleVal.Text = $"%{(int)SldCamScale.Value}";
        }

        private void ChkMuteAudio_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            _engine.AudioEngine.MicMuted = ChkMuteMic.IsChecked == true;
            _engine.AudioEngine.SystemMuted = ChkMuteSys.IsChecked == true;
        }

        // ======================= KAYIT VE TRANSPORT KONTROLLERİ =======================

        private async void BtnStartStopRec_Click(object sender, RoutedEventArgs e)
        {
            if (!_engine.IsRecording)
            {
                if (_activeStudioMode != "Record" && _activeStudioMode != "Meet")
                    SetStudioViewMode("Record");

                var proj = _engine.StartDualRecording("CircleDers", verticalOnly: false);
                _currentProject = proj;
                BtnStartStopRec.Content = "⏹ Bitir & Kurgu";
                BtnStartStopRec.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
                BtnPauseRec.IsEnabled = true;
                TxtStatusBanner.Text = "Kayıtta • 16:9 + 9:16 (60 FPS)";
            }
            else
            {
                BtnStartStopRec.IsEnabled = false;
                BtnPauseRec.IsEnabled = false;
                TxtStatusBanner.Text = "Kayıt tamamlanıyor...";

                var proj = await _engine.StopDualRecordingAsync();
                _currentProject = proj;
                _lastExported16x9Path = proj.Horizontal16x9VideoPath;
                _lastExported9x16Path = proj.Vertical9x16VideoPath;
                _undoStack.Clear();

                // Kayıt biter bitmez otomatik sessizlik analizi ve altyazı üretimi çalıştır!
                AutoCutSilenceService.AnalyzeMicrophoneSilence(
                    _currentProject,
                    SldSilenceDb.Value,
                    SldSilenceDur.Value,
                    (int)SldSilencePad.Value);
                TimelineRenderService.GenerateAutoSubtitlesFromSpeechSegments(_currentProject);
                _currentProject.Save();

                RefreshSubtitleListBox();
                UpdateSilenceSummaryText();
                RegenerateSocialMediaMetadata();

                // Kayıt bittiği anda otomatik olarak Kayıt ekranını kapatıp TAM CIRCLE EDIT KURGU EKRANINA geç!
                SetStudioViewMode("Edit");
                RefreshCircleEditFolderLists();

                string fileToPreview = File.Exists(proj.Horizontal16x9VideoPath)
                    ? proj.Horizontal16x9VideoPath
                    : proj.Vertical9x16VideoPath;

                if (!string.IsNullOrWhiteSpace(fileToPreview) && File.Exists(fileToPreview))
                {
                    LoadVideoIntoCircleEditEditor(fileToPreview, isVertical: !File.Exists(proj.Horizontal16x9VideoPath), preserveCurrentProject: true);
                }
                RedrawTimeline();

                BtnStartStopRec.Content = "● Kayıt";
                BtnStartStopRec.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
                BtnStartStopRec.IsEnabled = true;

                TxtStatusBanner.Text = "Kayıt tamamlandı • Kurgu hazır";
            }
        }

        private async void StartStopVerticalOnlyRec()
        {
            if (!_engine.IsRecording)
            {
                if (_activeStudioMode != "Record" && _activeStudioMode != "Meet")
                    SetStudioViewMode("Record");

                var proj = _engine.StartDualRecording("CircleDikey", verticalOnly: true);
                _currentProject = proj;
                BtnStartStopRec.Content = "⏹ Bitir & Kurgu";
                BtnPauseRec.IsEnabled = true;
                TxtStatusBanner.Text = $"Kayıtta • 9:16 Dikey ({System.IO.Path.GetFileName(proj.Vertical9x16VideoPath)})";
            }
            else
            {
                BtnStartStopRec_Click(null, null);
            }
        }

        private async Task SplitRecordingPartAsync()
        {
            if (!_engine.IsRecording) return;
            TxtStatusBanner.Text = "Yeni part başlatılıyor...";
            bool wasVertOnly = _engine.IsVerticalOnlyRecording;
            await _engine.StopDualRecordingAsync();
            var newProj = _engine.StartDualRecording("CircleDers_Part", verticalOnly: wasVertOnly);
            _currentProject = newProj;
            RefreshCircleEditFolderLists();
            TxtStatusBanner.Text = $"Yeni part aktif • {newProj.ProjectName}";
        }

        private void BtnToggleWebcamQuick_Click(object sender, RoutedEventArgs e)
        {
            bool nextState = !_engine.ShowWebcam;
            _engine.ShowWebcam = nextState;
            ChkShowWebcam.IsChecked = nextState;
            if (BtnToggleWebcamQuick != null)
            {
                BtnToggleWebcamQuick.Content = nextState ? "Kamera" : "Kamera: Gizli";
                BtnToggleWebcamQuick.Background = nextState
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            }
            TxtStatusBanner.Text = nextState ? "Kamera açık" : "Kamera gizlendi";
        }

        private void BtnLicenseManager_Click(object sender, RoutedEventArgs e)
        {
            CircleLicenseService.ShowFounderLicenseAdminWindow(this);
        }

        private void BtnShowShortcuts_Click(object sender, RoutedEventArgs e)
        {
            string info =
                "CIRCLE STUDIO v1.1 — KISAYOLLAR & HAKKINDA\n" +
                "by thezencirs\n" +
                $"Lisans Durumu: {CircleLicenseService.ActiveLicenseStatus}\n" +
                "Copyright © 2026 thezencirs. Tüm hakları saklıdır.\n" +
                "Medya işleme altyapısında LGPL v3 lisanslı FFmpeg kullanılmaktadır.\n" +
                "──────────────────────────────────────────────────────────\n\n" +
                "STÜDYO & KAYIT:\n" +
                "• Ctrl + Shift + R   İkili Kayıt Başlat / Bitir (16:9 + 9:16)\n" +
                "• Ctrl + Shift + V   Sadece Dikey Kayıt (9:16)\n" +
                "• Ctrl + Shift + P   Duraklat / Devam\n" +
                "• Ctrl + Shift + N   Yeni Part'a Böl\n" +
                "• Alt + Q            2x Akıllı Zoom\n" +
                "• Alt + E            İmleç Takibini Kilitle / Aç\n" +
                "• Alt + Shift + C    Kamerayı Göster / Gizle\n" +
                "• Alt + Shift + M    Mikrofonu Sustur / Aç\n" +
                "• Alt + S            Çift Ekran Fotoğrafı Al\n\n" +
                "KURGU & TIMELAPSE:\n" +
                "• Space              Oynat / Duraklat\n" +
                "• S / Ctrl + B       Klibi Kes (Split)\n" +
                "• Delete             Seçili Klibi Sil\n" +
                "• Ctrl + Z           Geri Al (Undo)\n" +
                "• Sol / Sağ Ok       1 Saniye Geri / İleri";

            System.Windows.MessageBox.Show(info, "Circle — by thezencirs", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnPauseRec_Click(object sender, RoutedEventArgs e)
        {
            if (!_engine.IsRecording) return;
            _engine.TogglePauseRecording();
            BtnPauseRec.Content = _engine.IsPaused ? "▶ Devam" : "Duraklat";
        }

        private void BtnToggleSmartZoom_Click(object sender, RoutedEventArgs e)
        {
            _engine.IsSmartZoomActive = !_engine.IsSmartZoomActive;
            BtnToggleSmartZoom.Content = _engine.IsSmartZoomActive
                ? "2x Zoom: Açık"
                : "2x Zoom";
            BtnToggleSmartZoom.Background = _engine.IsSmartZoomActive
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
        }

        private void BtnToggleFollow_Click(object sender, RoutedEventArgs e)
        {
            _engine.FollowMouseEnabled = !_engine.FollowMouseEnabled;
            BtnToggleFollow.Content = _engine.FollowMouseEnabled
                ? "Takip: Açık"
                : "Takip: Kilitli";
        }

        private void BtnAddMarker_Click(object sender, RoutedEventArgs e)
        {
            double t = _engine.IsRecording ? _engine.RecordingElapsed.TotalSeconds : _playheadSec;
            _currentProject.Markers.Add(new TimelineMarker
            {
                TimeSec = Math.Round(t, 2),
                Title = $"İşaret {_currentProject.Markers.Count + 1}"
            });
            TxtStatusBanner.Text = $"İşaret eklendi • {t:0.0} sn";
            RedrawTimeline();
        }

        private void BtnTakeScreenshot_Click(object sender, RoutedEventArgs e)
        {
            var (p16, p9) = _engine.CaptureDualScreenshot();
            RefreshCircleEditFolderLists();
            TxtStatusBanner.Text = $"Fotoğraf kaydedildi • {System.IO.Path.GetFileName(p16)}";
        }

        // ======================= SEKME 1: SMART PORTRAIT & NON-DESTRUCTIVE UNCROP =======================

        private void CmbPortraitLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            _engine.PortraitLayout = (SmartPortraitLayout)CmbPortraitLayout.SelectedIndex;
            _currentProject.PortraitLayout = _engine.PortraitLayout;
        }

        private void SldUncrop_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            _engine.LiveUncropPercent = SldUncrop.Value;
            _currentProject.UncropPercent = SldUncrop.Value;
            if (TxtUncropVal != null)
            {
                TxtUncropVal.Text = SldUncrop.Value < 3
                    ? "%0 (Tam 9:16)"
                    : $"%{(int)SldUncrop.Value} (Uncrop Geniş)";
            }
        }

        private void SldPanX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            _engine.ManualPanX = SldPanX.Value / 100.0;
            _currentProject.ManualPanX = _engine.ManualPanX;
            if (TxtPanXVal != null)
                TxtPanXVal.Text = $"{(int)SldPanX.Value:+#;-#;0}%";
        }

        private void SmartZoomOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            _engine.AutoClickZoomEnabled = ChkAutoClickZoom.IsChecked == true;
            _engine.ZoomFactor16x9 = SldZoom16.Value;
            _engine.ZoomFactor9x16 = SldZoom9.Value;
            if (TxtZoom16Val != null) TxtZoom16Val.Text = $"{SldZoom16.Value:0.0}x";
            if (TxtZoom9Val != null) TxtZoom9Val.Text = $"{SldZoom9.Value:0.00}x";
        }

        private async void BtnReRenderUncrop_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null ||
                (string.IsNullOrWhiteSpace(_currentProject.MasterCleanVideoPath) && string.IsNullOrWhiteSpace(_currentProject.Horizontal16x9VideoPath)))
            {
                System.Windows.MessageBox.Show(
                    "Önce kısa bir kayıt alın veya 'Proje / Video Aç' butonundan bir video seçin.",
                    "Circle Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outFile = await TimelineRenderService.RenderNonDestructiveUncrop9x16Async(
                _currentProject,
                SldUncrop.Value,
                SldPanX.Value / 100.0,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(outFile) && File.Exists(outFile))
            {
                RefreshCircleEditFolderLists();
                LoadVideoIntoCircleEditEditor(outFile, isVertical: true, preserveCurrentProject: true);
                TxtStatusBanner.Text = $"Kayıpsız Uncrop 9:16 dikey video üretildi: {outFile}";
                System.Windows.MessageBox.Show(
                    $"Master kaynaktan kayıpsız Uncrop (%{SldUncrop.Value:0}) ile yeni 9:16 dikey video üretildi:\n\n{outFile}",
                    "Non-Destructive Uncrop Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ======================= SEKME 2: AKILLI SESSİZLİK KALDIRICI (AUTO-CUT SILENCE) =======================

        private void SilenceParams_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            if (TxtSilenceDbVal != null) TxtSilenceDbVal.Text = $"{SldSilenceDb.Value:0} dB";
            if (TxtSilenceDurVal != null) TxtSilenceDurVal.Text = $"{SldSilenceDur.Value:0.00} sn";
            if (TxtSilencePadVal != null) TxtSilencePadVal.Text = $"{(int)SldSilencePad.Value} ms";
            if (TxtDeadAirSpeedVal != null) TxtDeadAirSpeedVal.Text = $"{SldDeadAirSpeed.Value:0.0}x Hız";
        }

        private void BtnAnalyzeSilence_Click(object sender, RoutedEventArgs e)
        {
            var silences = AutoCutSilenceService.AnalyzeMicrophoneSilence(
                _currentProject,
                SldSilenceDb.Value,
                SldSilenceDur.Value,
                (int)SldSilencePad.Value);

            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = $"Mikrofon sessizlik analizi tamamlandı: {silences.Count} sessiz aralık tespit edildi.";
        }

        private void BtnRippleDeleteSilence_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject.DetectedSilences.Count == 0)
            {
                AutoCutSilenceService.AnalyzeMicrophoneSilence(_currentProject, SldSilenceDb.Value, SldSilenceDur.Value, (int)SldSilencePad.Value);
            }

            AutoCutSilenceService.ApplySilenceActionToTimeline(_currentProject, SilenceActionMode.RippleDelete, SldDeadAirSpeed.Value);
            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = "Ripple Delete uygulandı: Sessiz kısımlar kesildi ve konuşma blokları birleştirildi!";
        }

        private void BtnSpeedUpSilence_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject.DetectedSilences.Count == 0)
            {
                AutoCutSilenceService.AnalyzeMicrophoneSilence(_currentProject, SldSilenceDb.Value, SldSilenceDur.Value, (int)SldSilencePad.Value);
            }

            double spd = SldDeadAirSpeed.Value;
            AutoCutSilenceService.ApplySilenceActionToTimeline(_currentProject, SilenceActionMode.SpeedUpDeadAir, spd);
            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = $"3D Modelleme Modu uygulandı: Sessiz kısımlar silinmeden {spd:0.#}x hızlandırıldı!";
        }

        private void BtnResetSilenceCuts_Click(object sender, RoutedEventArgs e)
        {
            AutoCutSilenceService.ApplySilenceActionToTimeline(_currentProject, SilenceActionMode.KeepNormal, 1.0);
            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = "Tüm kesimler ve hızlandırmalar kayıpsız olarak orijinal haline döndürüldü.";
        }

        private void UpdateSilenceSummaryText()
        {
            double total = Math.Max(0.1, _currentProject.TotalDurationSec);
            double silenceTotal = _currentProject.DetectedSilences.Sum(s => s.DurationSec);
            double newOutDur = _currentProject.Clips.Sum(c => c.OutputDurationSec);
            double saved = Math.Max(0, total - newOutDur);

            TxtSilenceSummary.Text =
                $"• Tespit Edilen Sessizlik: {_currentProject.DetectedSilences.Count} bölge ({silenceTotal:0.1f} sn)\n" +
                $"• Orijinal Süre: {total:0.1f} sn  ➔  Yeni Süre: {newOutDur:0.1f} sn\n" +
                $"• Kazanılan Süre: {saved:0.1f} sn (%{saved / total * 100.0:0} daha akıcı)";
        }

        // ======================= SEKME 3: CIRCLE EDIT KURGU, MEDYA, MASKELEME & ALTYAZI =======================

        private void BtnAddMergeVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Birleştirilecek Ek Video Seçin (Intro / B-Roll / Ek Sahne)",
                Filter = "Video Dosyaları (*.mp4;*.mov;*.mkv)|*.mp4;*.mov;*.mkv|Tüm Dosyalar (*.*)|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    if (!_currentProject.ExternalMergedVideoPaths.Contains(f))
                        _currentProject.ExternalMergedVideoPaths.Add(f);
                }
                TxtMergedVideosStatus.Text = $"✅ {_currentProject.ExternalMergedVideoPaths.Count} ek video birleştirme listesine eklendi: " +
                                             string.Join(", ", _currentProject.ExternalMergedVideoPaths.Select(System.IO.Path.GetFileName));
                TxtStatusBanner.Text = $"{_currentProject.ExternalMergedVideoPaths.Count} adet dış video birleştirme kuyruğuna eklendi.";
            }
        }

        private void BtnClearMergedVideos_Click(object sender, RoutedEventArgs e)
        {
            _currentProject.ExternalMergedVideoPaths.Clear();
            TxtMergedVideosStatus.Text = "Birleştirilecek ek video seçilmedi.";
            TxtStatusBanner.Text = "Video birleştirme listesi temizlendi.";
        }

        private async void BtnMergeAndExport4K_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteNativeCircleMix4KMergeAsync(showDialogOnComplete: sender != null);
        }

        private async Task ExecuteNativeCircleMix4KMergeAsync(bool showDialogOnComplete)
        {
            if (_currentProject == null || _currentProject.ExternalMergedVideoPaths == null || _currentProject.ExternalMergedVideoPaths.Count == 0)
            {
                TxtStatusBanner.Text = "Birleştirilecek sahne bulunamadı. Lütfen '+ Video Ekle' ile sahneleri seçin.";
                return;
            }

            if (BtnMergeAndExport4K != null) BtnMergeAndExport4K.IsEnabled = false;
            var (out1080, out4K) = await TimelineRenderService.RenderMultiClipSlowMoMasterAsync(
                _currentProject,
                slowMoStretchFactor: 1.38,
                xfadeDurationSec: 0.65,
                onProgress: msg => Dispatcher.Invoke(() =>
                {
                    if (TxtStatusBanner != null) TxtStatusBanner.Text = msg;
                    if (TxtMergedVideosStatus != null) TxtMergedVideosStatus.Text = msg;
                }));
            if (BtnMergeAndExport4K != null) BtnMergeAndExport4K.IsEnabled = true;

            RefreshCircleEditFolderLists();
            string bestTarget = !string.IsNullOrWhiteSpace(out4K) && File.Exists(out4K) ? out4K : out1080;
            if (!string.IsNullOrWhiteSpace(bestTarget) && File.Exists(bestTarget))
            {
                bool isVertTarget = bestTarget.Contains("9x16", StringComparison.OrdinalIgnoreCase) || bestTarget.Contains("Dikey", StringComparison.OrdinalIgnoreCase);
                if (isVertTarget) _lastExported9x16Path = bestTarget;
                else _lastExported16x9Path = bestTarget;
                LoadVideoIntoCircleEditEditor(bestTarget, isVertical: isVertTarget, preserveCurrentProject: true);
                if (TxtMergedVideosStatus != null)
                {
                    TxtMergedVideosStatus.Text = $"✅ Circle Mix 4K UHD Slow-Mo Birleştirildi ({_currentProject.ExternalMergedVideoPaths.Count} Sahne • 60fps • -14 LUFS): " +
                                                 System.IO.Path.GetFileName(bestTarget);
                }
                if (showDialogOnComplete)
                {
                    System.Windows.MessageBox.Show(
                        $"Circle Studio 4K UHD Slow-Mo Birleştirme Tamamlandı!\n\n4K UHD: {out4K}\n1080p: {out1080}",
                        "Circle Studio — 4K Slow-Mo Master",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }

        private void BtnSelectPipMedia_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Görüntü / B-Roll Bindirme (PIP) İçin Resim veya Video Seçin",
                Filter = "Görsel ve Video (*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov)|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _currentProject.PipOverlayMediaPath = dlg.FileName;
                _engine.SetPipOverlayMedia(dlg.FileName);
                TxtPipMediaStatus.Text = $"✅ Aktif Bindirme: {System.IO.Path.GetFileName(dlg.FileName)}";
                TxtStatusBanner.Text = $"🖼️ Görüntü bindirme ve maskeleme aktif: {System.IO.Path.GetFileName(dlg.FileName)} (Canlı önizlemede görebilirsiniz!)";
            }
        }

        private void BtnClearPipMedia_Click(object sender, RoutedEventArgs e)
        {
            _currentProject.PipOverlayMediaPath = "";
            _engine.SetPipOverlayMedia(null);
            TxtPipMediaStatus.Text = "Aktif bindirme yok.";
            TxtStatusBanner.Text = "Görüntü bindirme kaldırıldı.";
        }

        private void PipOptions_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            var mask = (OverlayMaskShape)Math.Clamp(CmbPipMaskShape.SelectedIndex, 0, 3);
            int pos = Math.Clamp(CmbPipPosition.SelectedIndex, 0, 4);

            _currentProject.PipMaskShape = mask;
            _currentProject.PipPositionIndex = pos;
            _engine.PipMaskShape = mask;
            _engine.PipPositionIndex = pos;
        }

        private void PipScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            _currentProject.PipScalePercent = SldPipScale.Value;
            _engine.PipScalePercent = SldPipScale.Value;
            if (TxtPipScaleVal != null)
                TxtPipScaleVal.Text = $"%{(int)SldPipScale.Value}";
        }

        private void BtnSelectExternalAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Dışarıdan Arka Plan Müziği veya Ses Efekti Seçin",
                Filter = "Ses Dosyaları (*.mp3;*.wav;*.m4a;*.aac)|*.mp3;*.wav;*.m4a;*.aac|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _currentProject.ExternalAudioTrackPath = dlg.FileName;
                TxtExternalAudioStatus.Text = $"🎵 Fon Müziği: {System.IO.Path.GetFileName(dlg.FileName)}";
                TxtStatusBanner.Text = $"🎵 Arka plan ses dosyası eklendi: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
        }

        private void BtnClearExternalAudio_Click(object sender, RoutedEventArgs e)
        {
            _currentProject.ExternalAudioTrackPath = "";
            TxtExternalAudioStatus.Text = "Fon Müziği Ses Oranı (Konuşma Altı):";
            TxtStatusBanner.Text = "Dış ses / müzik kaldırıldı.";
        }

        private void BgmVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            _currentProject.ExternalAudioVolumePercent = SldBgmVolume.Value;
            if (TxtBgmVolVal != null)
                TxtBgmVolVal.Text = $"%{(int)SldBgmVolume.Value}";
        }

        private void ColorFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            _currentProject.ColorFilter = (ColorLutPreset)Math.Clamp(CmbColorFilter.SelectedIndex, 0, 14);
            TxtStatusBanner.Text = $"🎨 Renk & LUT seçildi: {(CmbColorFilter.SelectedItem as ComboBoxItem)?.Content}";
        }

        private void ColorAdjustSliders_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady || _currentProject == null) return;
            if (SldAdjBrightness != null) _currentProject.AdjustBrightness = SldAdjBrightness.Value;
            if (SldAdjContrast != null) _currentProject.AdjustContrast = SldAdjContrast.Value;
            if (SldAdjSaturation != null) _currentProject.AdjustSaturation = SldAdjSaturation.Value;
            if (SldAdjWarmth != null) _currentProject.AdjustWarmth = SldAdjWarmth.Value;
            if (SldAdjSharpen != null) _currentProject.AdjustSharpen = SldAdjSharpen.Value;
            if (SldAdjVignette != null) _currentProject.AdjustVignette = SldAdjVignette.Value;

            if (TxtAdjBrightContrastVal != null && SldAdjBrightness != null && SldAdjContrast != null)
                TxtAdjBrightContrastVal.Text = $"{(int)SldAdjBrightness.Value:+#;-#;0} • {(int)SldAdjContrast.Value:+#;-#;0}";
            if (TxtAdjSatWarmthVal != null && SldAdjSaturation != null && SldAdjWarmth != null)
                TxtAdjSatWarmthVal.Text = $"{(int)SldAdjSaturation.Value:+#;-#;0} • {(int)SldAdjWarmth.Value:+#;-#;0}";
            if (TxtAdjSharpVignetteVal != null && SldAdjSharpen != null && SldAdjVignette != null)
                TxtAdjSharpVignetteVal.Text = $"%{(int)SldAdjSharpen.Value} • %{(int)SldAdjVignette.Value}";
        }

        private void BtnAutoColorAdjust_Click(object sender, RoutedEventArgs e)
        {
            if (SldAdjBrightness != null) SldAdjBrightness.Value = 4;
            if (SldAdjContrast != null) SldAdjContrast.Value = 12;
            if (SldAdjSaturation != null) SldAdjSaturation.Value = 10;
            if (SldAdjWarmth != null) SldAdjWarmth.Value = 6;
            if (SldAdjSharpen != null) SldAdjSharpen.Value = 35;
            if (SldAdjVignette != null) SldAdjVignette.Value = 15;
            TxtStatusBanner.Text = "✨ Otomatik Stüdyo Renk Dengelemesi uygulandı (Kontrast +12, Doygunluk +10, Sıcaklık +6, HDR Keskinlik %35).";
        }

        private void BtnResetColorAdjust_Click(object sender, RoutedEventArgs e)
        {
            if (CmbColorFilter != null) CmbColorFilter.SelectedIndex = 0;
            if (SldAdjBrightness != null) SldAdjBrightness.Value = 0;
            if (SldAdjContrast != null) SldAdjContrast.Value = 0;
            if (SldAdjSaturation != null) SldAdjSaturation.Value = 0;
            if (SldAdjWarmth != null) SldAdjWarmth.Value = 0;
            if (SldAdjSharpen != null) SldAdjSharpen.Value = 0;
            if (SldAdjVignette != null) SldAdjVignette.Value = 0;
            TxtStatusBanner.Text = "Renk ve LUT ayarları doğal stüdyo değerlerine sıfırlandı.";
        }

        private void TransitionOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady || _currentProject == null) return;
            if (CmbTransitionPreset != null)
                _currentProject.TransitionPreset = (ClipTransitionPreset)Math.Clamp(CmbTransitionPreset.SelectedIndex, 0, 11);
            if (SldTransitionDuration != null)
            {
                _currentProject.TransitionDurationSec = Math.Round(SldTransitionDuration.Value, 2);
                if (TxtTransitionDurVal != null)
                    TxtTransitionDurVal.Text = $"{_currentProject.TransitionDurationSec:0.00} sn";
            }
        }

        private void CmbCaptionStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || _currentProject == null || CmbCaptionStyle == null) return;
            _currentProject.CaptionStyle = (CaptionStylePreset)Math.Clamp(CmbCaptionStyle.SelectedIndex, 0, 3);
            if (BdrLiveSubtitleOverlay != null)
            {
                switch (_currentProject.CaptionStyle)
                {
                    case CaptionStylePreset.OrangeKaraoke:
                        BdrLiveSubtitleOverlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 239, 95, 24));
                        BdrLiveSubtitleOverlay.BorderBrush = System.Windows.Media.Brushes.White;
                        BdrLiveSubtitleOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                    case CaptionStylePreset.MinimalCinema:
                        BdrLiveSubtitleOverlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 20, 14, 56));
                        BdrLiveSubtitleOverlay.BorderBrush = System.Windows.Media.Brushes.Transparent;
                        BdrLiveSubtitleOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                    case CaptionStylePreset.TopBanner:
                        BdrLiveSubtitleOverlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 38, 26, 102));
                        BdrLiveSubtitleOverlay.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
                        BdrLiveSubtitleOverlay.VerticalAlignment = VerticalAlignment.Top;
                        break;
                    default:
                        BdrLiveSubtitleOverlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(216, 20, 14, 56));
                        BdrLiveSubtitleOverlay.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 37, 128));
                        BdrLiveSubtitleOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                }
            }
            UpdateLiveSubtitleOverlay();
        }

        private void BtnFitTargetDuration_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TxtTargetDurationSec.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double targetSec) && targetSec >= 3.0)
            {
                AutoCutSilenceService.ApplyTargetDurationCompression(_currentProject, targetSec);
                UpdateSilenceSummaryText();
                RedrawTimeline();
                TxtStatusBanner.Text = $"Video klipleri hedef {targetSec:0.#} saniye süresine otomatik sıkıştırıldı!";
            }
        }

        private void BtnGenerateSubtitles_Click(object sender, RoutedEventArgs e)
        {
            TimelineRenderService.GenerateAutoSubtitlesFromSpeechSegments(_currentProject);
            RefreshSubtitleListBox();
            UpdateLiveSubtitleOverlay();
            RedrawTimeline();
            TxtStatusBanner.Text = $"{_currentProject.Subtitles.Count} adet konuşma senkronlu altyazı bloğu üretildi (Çift tıklayarak metni düzenleyebilirsiniz).";
        }

        private void BtnImportSrt_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "SRT Altyazı Dosyası Seçin",
                Filter = "SRT Altyazı (*.srt)|*.srt|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                if (TimelineRenderService.ImportSrtFile(_currentProject, dlg.FileName))
                {
                    RefreshSubtitleListBox();
                    UpdateLiveSubtitleOverlay();
                    RedrawTimeline();
                    TxtStatusBanner.Text = $"SRT altyazı yüklendi ({_currentProject.Subtitles.Count} satır): {System.IO.Path.GetFileName(dlg.FileName)}";
                }
                else
                {
                    TxtStatusBanner.Text = "SRT dosyası okunamadı.";
                }
            }
        }

        private void LstSubtitles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = LstSubtitles.SelectedIndex;
            if (_currentProject?.Subtitles == null || idx < 0 || idx >= _currentProject.Subtitles.Count)
                return;

            var sub = _currentProject.Subtitles[idx];
            var win = new Window
            {
                Title = $"Altyazı Düzenle ({sub.StartSec:0.0}s - {sub.EndSec:0.0}s)",
                Width = 480,
                Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 14, 56)),
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Zaman Aralığı: {sub.StartSec:0.00}s — {sub.EndSec:0.00}s",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var tb = new System.Windows.Controls.TextBox
            {
                Text = sub.Text,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 42, 148)),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12)
            };
            sp.Children.Add(tb);

            var btnSave = new System.Windows.Controls.Button
            {
                Content = "Kaydet",
                Width = 110,
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                IsDefault = true
            };
            btnSave.Click += (_, _) => { win.DialogResult = true; win.Close(); };
            sp.Children.Add(btnSave);
            win.Content = sp;

            if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
            {
                sub.Text = tb.Text.Trim();
                RefreshSubtitleListBox();
                UpdateLiveSubtitleOverlay();
                RedrawTimeline();
                TxtStatusBanner.Text = "Altyazı metni güncellendi.";
            }
        }

        private void UpdateLiveSubtitleOverlay()
        {
            if (BdrLiveSubtitleOverlay == null || TxtLiveSubtitleOverlay == null) return;
            if (_currentProject?.Subtitles == null || _currentProject.Subtitles.Count == 0 || CircleEditMediaPlayer.Visibility != Visibility.Visible)
            {
                BdrLiveSubtitleOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var activeSub = _currentProject.Subtitles.FirstOrDefault(s => _playheadSec >= s.StartSec && _playheadSec <= s.EndSec);
            if (activeSub != null && !string.IsNullOrWhiteSpace(activeSub.Text))
            {
                TxtLiveSubtitleOverlay.Text = activeSub.Text;
                BdrLiveSubtitleOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                BdrLiveSubtitleOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSaveSrt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject.Subtitles.Count == 0)
                TimelineRenderService.GenerateAutoSubtitlesFromSpeechSegments(_currentProject);

            string srt = TimelineRenderService.WriteSrtFile(_currentProject);
            TxtStatusBanner.Text = $"SRT altyazı dosyası kaydedildi: {srt}";
            System.Windows.MessageBox.Show($"Altyazı dosyası kaydedildi:\n{srt}", "SRT Altyazı Hazır", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshSubtitleListBox()
        {
            LstSubtitles.Items.Clear();
            foreach (var s in _currentProject.Subtitles)
            {
                LstSubtitles.Items.Add($"[{s.StartSec:00.0}s - {s.EndSec:00.0}s] {s.Text}");
            }
        }

        private async void BtnExportAll_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null ||
                (string.IsNullOrWhiteSpace(_currentProject.Horizontal16x9VideoPath) && string.IsNullOrWhiteSpace(_currentProject.Vertical9x16VideoPath)))
            {
                System.Windows.MessageBox.Show(
                    "Dışa aktarmak için önce kısa bir kayıt alın veya '📂 Video Aç' butonundan bir video seçin.",
                    "Circle Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentProject.EnableNoiseSuppression = ChkAudioNoiseSuppress.IsChecked == true;
            _currentProject.EnableLufsNormalization = ChkAudioLufsNorm.IsChecked == true;
            _currentProject.BurnInSubtitles = ChkBurnInSubtitles?.IsChecked ?? true;

            var (out16, out9, outSrt) = await TimelineRenderService.ExportEditedSocialMediaPackageAsync(
                _currentProject,
                ChkRecord4K.IsChecked == true,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(out16)) _lastExported16x9Path = out16;
            if (!string.IsNullOrWhiteSpace(out9)) _lastExported9x16Path = out9;

            string exportErr = TimelineRenderService.LastExportError;
            if (!string.IsNullOrWhiteSpace(exportErr))
            {
                TxtStatusBanner.Text = "⚠️ Dışa aktarma başarısız oldu — kesilmemiş/yanlış video üretilmedi.";
                System.Windows.MessageBox.Show(
                    "Circle Edit dışa aktarma sırasında hata oluştu. Hatalı çıktı kaydedilmedi.\n\n" +
                    (out16 != null ? $"✅ Circle Yatay (16:9): {out16}\n" : "") +
                    (out9 != null ? $"✅ Circle Dikey (9:16): {out9}\n" : "") +
                    $"\nHata ayrıntısı:\n{exportErr}",
                    "Circle — Dışa Aktarma Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                if (out16 == null && out9 == null) return;
            }

            SetStudioViewMode("Social");

            System.Windows.MessageBox.Show(
                $"Circle Kurgu Paketi Hazır!\n\n" +
                $"📺 Circle Yatay (16:9): {out16 ?? "Üretilemedi"}\n" +
                $"📱 Circle Dikey (9:16): {out9 ?? "Üretilemedi"}\n" +
                $"📝 Altyazı (.SRT): {outSrt ?? "Yok"}\n\n" +
                $"Şimdi sağdaki butonlardan tek tıkla 16:9 veya 9:16 paketini klasörde seçili açıp paylaşabilirsiniz!",
                "Circle — Paylaşıma Hazır",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ======================= CIRCLE PAYLAŞIM MERKEZİ =======================

        private void RegenerateSocialMediaMetadata()
        {
            if (TxtSocialTitle == null || TxtSocialCaption == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🎓 Bu eğitim videosunda adım adım uygulamalı teknikleri, kısayolları ve profesyonel iş akışını inceledik.");
            sb.AppendLine();
            sb.AppendLine("⏱️ BÖLÜMLER (CIRCLE ZAMAN DAMGALARI):");
            sb.AppendLine("00:00 Giriş & Genel Bakış");

            double elapsedOut = 0.0;
            int stepIdx = 1;
            if (_currentProject?.Clips != null)
            {
                foreach (var clip in _currentProject.Clips.Where(c => !c.IsDeleted))
                {
                    if (!clip.IsSilentSegment && elapsedOut >= 2.0)
                    {
                        var ts = TimeSpan.FromSeconds(elapsedOut);
                        sb.AppendLine($"{(int)ts.TotalMinutes:00}:{ts.Seconds:00} {clip.Label}");
                        stepIdx++;
                    }
                    elapsedOut += clip.OutputDurationSec;
                }
            }

            if (_currentProject?.Markers != null)
            {
                foreach (var m in _currentProject.Markers)
                {
                    var ts = TimeSpan.FromSeconds(m.TimeSec);
                    sb.AppendLine($"{(int)ts.TotalMinutes:00}:{ts.Seconds:00} 🚩 {m.Title}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("🔥 #circle #thezencirs #mimarimodel #içmimarlık #3bmodelleme #tasarım #eğitim #render");

            TxtSocialCaption.Text = sb.ToString();
        }

        private void BtnRegenerateSocialMeta_Click(object sender, RoutedEventArgs e)
        {
            RegenerateSocialMediaMetadata();
            TxtStatusBanner.Text = "Paylaşım başlığı, Circle bölüm zaman damgaları ve etiketler yenilendi.";
        }

        private void BtnCopySocialText_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string full = $"{TxtSocialTitle.Text}\n\n{TxtSocialCaption.Text}";
                System.Windows.Clipboard.SetText(full);
                TxtStatusBanner.Text = "Başlık, açıklama ve etiketler panoya kopyalandı (Ctrl+V ile yapıştırabilirsiniz).";
            }
            catch { }
        }

        private async void BtnShareWide16x9_Click(object sender, RoutedEventArgs e)
        {
            await ShareCirclePackageAsync(
                packageName: "Circle 16:9 Geniş Ekran Paketi",
                preferVertical9x16: false);
        }

        private async void BtnShareVertical9x16_Click(object sender, RoutedEventArgs e)
        {
            await ShareCirclePackageAsync(
                packageName: "Circle 9:16 Dikey Video Paketi",
                preferVertical9x16: true);
        }

        private async void BtnShareShort9x16_Click(object sender, RoutedEventArgs e)
        {
            await ShareCirclePackageAsync(
                packageName: "Circle 9:16 Kısa Klip Paketi",
                preferVertical9x16: true);
        }

        private async void BtnShareDirectDesktop_Click(object sender, RoutedEventArgs e)
        {
            await ShareCirclePackageAsync(
                packageName: "Circle Masaüstü & Hızlı Paylaşım Paketi",
                preferVertical9x16: true);
        }

        private async Task ShareCirclePackageAsync(string packageName, bool preferVertical9x16)
        {
            // 1. Başlık ve açıklamayı otomatik panoya kopyala
            try
            {
                string fullMeta = $"{TxtSocialTitle.Text}\n\n{TxtSocialCaption.Text}";
                System.Windows.Clipboard.SetText(fullMeta);
            }
            catch { }

            // 2. Kurgulanmış video henüz üretilmediyse ve ham kayıt varsa otomatik üret
            string targetVideo = preferVertical9x16
                ? (!string.IsNullOrWhiteSpace(_lastExported9x16Path) && File.Exists(_lastExported9x16Path) ? _lastExported9x16Path : _currentProject?.Vertical9x16VideoPath)
                : (!string.IsNullOrWhiteSpace(_lastExported16x9Path) && File.Exists(_lastExported16x9Path) ? _lastExported16x9Path : _currentProject?.Horizontal16x9VideoPath);

            if (!string.IsNullOrWhiteSpace(targetVideo) && File.Exists(targetVideo) &&
                string.IsNullOrWhiteSpace(_lastExported16x9Path) &&
                _currentProject != null && _currentProject.Clips.Any(c => c.IsDeleted || Math.Abs(c.SpeedMultiplier - 1.0) > 0.05))
            {
                var (out16, out9, _) = await TimelineRenderService.ExportEditedSocialMediaPackageAsync(
                    _currentProject,
                    ChkRecord4K.IsChecked == true,
                    msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));
                if (!string.IsNullOrWhiteSpace(out16)) _lastExported16x9Path = out16;
                if (!string.IsNullOrWhiteSpace(out9)) _lastExported9x16Path = out9;
                if (!string.IsNullOrWhiteSpace(TimelineRenderService.LastExportError))
                {
                    System.Windows.MessageBox.Show(
                        "Kurgu dışa aktarılamadı; paylaşım iptal edildi.\n\n" + TimelineRenderService.LastExportError,
                        "Circle — Dışa Aktarma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                targetVideo = preferVertical9x16 ? _lastExported9x16Path : _lastExported16x9Path;
            }

            // 3. Videoyu Windows Gezgini'nde seçili olarak aç (Sürükle-bırak kolaylığı)
            if (!string.IsNullOrWhiteSpace(targetVideo) && File.Exists(targetVideo))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{targetVideo}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            else
            {
                BtnOpenFolder_Click(null, null);
            }

            TxtStatusBanner.Text = $"🚀 {packageName} hazır! Başlık & bölüm damgaları panoya kopyalandı (Ctrl+V), video dosyası klasörde seçildi.";
        }

        // ======================= SEKME 4: CANLI YAYIN (RTMP), CIRCLE MEET & UZAKTAN KODLA BAĞLANTI =======================

        private void BtnCopyRoomCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_meetServer.IsRunning)
                {
                    System.Windows.Clipboard.SetText($"Circle Meet Oda Kodu: {_meetServer.RoomCode} • PIN: {_meetServer.RoomPin}");
                    TxtStatusBanner.Text = $"🔑 Oda kodu ve PIN panoya kopyalandı: {_meetServer.RoomCode} • PIN {_meetServer.RoomPin}";
                }
                else
                {
                    System.Windows.Clipboard.SetText(_meetServer.RoomCode);
                    TxtStatusBanner.Text = $"🔑 Oda kodu panoya kopyalandı: {_meetServer.RoomCode} — PIN, oda açıldığında oluşturulur.";
                }
            }
            catch { }
        }

        private void ChkAllowRemoteControl_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            bool allow = ChkAllowRemoteControl.IsChecked == true;
            _meetServer.AllowRemoteControl = allow; // kapatıldığında tüm katılımcı onayları da geri alınır
            TxtStatusBanner.Text = allow
                ? "🖱️ Circle Uzaktan Kontrol açıldı. Katılımcılar istek gönderebilir; her istek için ayrıca onayınız sorulur."
                : "🔒 Circle Uzaktan Fare & Klavye Kontrolü KAPATILDI ve verilen tüm izinler geri alındı.";
        }

        private async void BtnWelcomeConnectByCode_Click(object sender, RoutedEventArgs e)
        {
            TxtRemoteJoinCode.Text = TxtWelcomeJoinCode.Text;
            SetStudioViewMode("Meet");
            await ConnectToRemoteByCodeInternalAsync(TxtWelcomeJoinCode.Text);
        }

        private async void BtnConnectByCode_Click(object sender, RoutedEventArgs e)
        {
            await ConnectToRemoteByCodeInternalAsync(TxtRemoteJoinCode.Text);
        }

        private async Task ConnectToRemoteByCodeInternalAsync(string codeOrUrl)
        {
            if (string.IsNullOrWhiteSpace(codeOrUrl))
            {
                System.Windows.MessageBox.Show(
                    "Lütfen bağlanmak istediğiniz arkadaşınızın 6 haneli kodunu (Örn: 482-915) girin.",
                    "Circle Studio — Kodla Bağlan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtStatusBanner.Text = $"🔍 '{codeOrUrl}' kodu internet üzerinde aranıyor...";
            string resolvedUrl = await _meetServer.ResolveCodeToUrlAsync(codeOrUrl);

            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                System.Windows.MessageBox.Show(
                    $"'{codeOrUrl}' koduna ait aktif bir oda bulunamadı.\nKarşı bilgisayarda CircleMixRecord.exe'nin açık olduğundan emin olun.",
                    "Oda Bulunamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatusBanner.Text = $"'{codeOrUrl}' kodu bulunamadı.";
                return;
            }

            TxtStatusBanner.Text = $"🚀 '{codeOrUrl}' odasına bağlanıldı: {resolvedUrl}";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = resolvedUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private async void BtnToggleMeet_Click(object sender, RoutedEventArgs e)
        {
            if (!_meetServer.IsRunning)
            {
                await _meetServer.StartServerAsync();
                UpdateMeetUiState();
                TxtStatusBanner.Text = _meetServer.IsRunning
                    ? $"Circle Meet & Uzaktan Bağlantı Odası aktif (Kod: {_meetServer.RoomCode} • PIN: {_meetServer.RoomPin}) — internet linki hazırlanıyor..."
                    : "⚠️ Circle Meet odası başlatılamadı (5880 portu kullanımda olabilir).";
            }
            else
            {
                await _meetServer.StopServerAsync();
                if (ChkAllowRemoteControl != null) ChkAllowRemoteControl.IsChecked = false;
                UpdateMeetUiState();
                TxtStatusBanner.Text = "Circle Meet odası kapatıldı. Eski davet linkleri ve PIN artık geçersiz.";
            }
        }

        private void BtnOpenMeetBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (!_meetServer.IsRunning)
            {
                TxtStatusBanner.Text = "Önce 'Canlı Odayı Aç' ile odayı başlatın.";
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _meetServer.BestInviteUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnCopyMeetLink_Click(object sender, RoutedEventArgs e)
        {
            if (!_meetServer.IsRunning)
            {
                TxtStatusBanner.Text = "Önce 'Canlı Odayı Aç' ile odayı başlatın.";
                return;
            }
            try
            {
                System.Windows.Clipboard.SetText(_meetServer.BestInviteUrl);
                TxtStatusBanner.Text = $"Davet linki panoya kopyalandı (link erişim anahtarı içerir, yalnızca güvendiğiniz kişilerle paylaşın) • PIN: {_meetServer.RoomPin}";
            }
            catch { }
        }

        private void BtnSendMeetChat_Click(object sender, RoutedEventArgs e)
        {
            string txt = TxtInstructorChat.Text?.Trim();
            if (string.IsNullOrWhiteSpace(txt)) return;
            TxtInstructorChat.Clear();
            _meetServer.SendInstructorMessage(txt);
        }

        private void OnMeetMessageReceived(MeetChatMessage msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string prefix = msg.IsHandRaise ? "✋ [SÖZ İSTEDİ] " : "";
                LstMeetChat.Items.Add($"[{msg.Time}] {prefix}{msg.Sender}: {msg.Text}");
                LstMeetChat.ScrollIntoView(LstMeetChat.Items[LstMeetChat.Items.Count - 1]);
                if (msg.IsHandRaise)
                {
                    TxtStatusBanner.Text = $"✋ {msg.Sender} canlı sınıfta söz istedi!";
                }
            }));
        }

        // ======================= CIRCLE CANLI YAYIN (ÇOKLU KANAL) =======================
        private StreamSettings _streamSettings;

        private void LoadStreamSettingsIntoUi()
        {
            try
            {
                _streamSettings = StreamSettings.Load();
                var d = _streamSettings.Destinations;
                ChkDestStream1.IsChecked = d[0].Enabled; TxtDestStream1Url.Text = d[0].ServerUrl; PwdDestStream1.Password = d[0].Key;
                ChkDestStream2.IsChecked = d[1].Enabled; TxtDestStream2Url.Text = d[1].ServerUrl; PwdDestStream2.Password = d[1].Key;
                ChkDestStream3.IsChecked = d[2].Enabled; TxtDestStream3Url.Text = d[2].ServerUrl; PwdDestStream3.Password = d[2].Key;
                ChkDestCustom.IsChecked = d[3].Enabled;  TxtDestCustomUrl.Text = d[3].ServerUrl;  PwdDestCustom.Password = d[3].Key;
                CmbStreamOrientation.SelectedIndex = _streamSettings.Vertical ? 1 : 0;
                CmbStreamResolution.SelectedIndex = _streamSettings.Height >= 1080 ? 0 : 1;
                CmbStreamBitrate.SelectedIndex = _streamSettings.BitrateKbps switch { <= 3000 => 0, <= 4500 => 1, <= 6000 => 2, _ => 3 };
                SldStreamMic.Value = _streamSettings.MicPercent;
                SldStreamSys.Value = _streamSettings.DesktopPercent;
                if (!string.IsNullOrWhiteSpace(_streamSettings.IntermissionImagePath))
                    _engine.SetIntermissionImage(_streamSettings.IntermissionImagePath);
            }
            catch
            {
                _streamSettings = StreamSettings.CreateDefault();
            }
        }

        private StreamSettings SaveStreamSettingsFromUi()
        {
            _streamSettings ??= StreamSettings.CreateDefault();
            var d = _streamSettings.Destinations;
            d[0].Enabled = ChkDestStream1.IsChecked == true; d[0].ServerUrl = TxtDestStream1Url.Text.Trim(); d[0].Key = PwdDestStream1.Password.Trim();
            d[1].Enabled = ChkDestStream2.IsChecked == true; d[1].ServerUrl = TxtDestStream2Url.Text.Trim(); d[1].Key = PwdDestStream2.Password.Trim();
            d[2].Enabled = ChkDestStream3.IsChecked == true; d[2].ServerUrl = TxtDestStream3Url.Text.Trim(); d[2].Key = PwdDestStream3.Password.Trim();
            d[3].Enabled = ChkDestCustom.IsChecked == true;  d[3].ServerUrl = TxtDestCustomUrl.Text.Trim();  d[3].Key = PwdDestCustom.Password.Trim();
            _streamSettings.Vertical = CmbStreamOrientation.SelectedIndex == 1;
            _streamSettings.Height = CmbStreamResolution.SelectedIndex == 0 ? 1080 : 720;
            _streamSettings.BitrateKbps = CmbStreamBitrate.SelectedIndex switch { 0 => 3000, 1 => 4500, 2 => 6000, _ => 8000 };
            _streamSettings.MicPercent = (int)SldStreamMic.Value;
            _streamSettings.DesktopPercent = (int)SldStreamSys.Value;
            _streamSettings.Save();
            return _streamSettings;
        }

        private void StreamMix_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtStreamMicVal != null && SldStreamMic != null) TxtStreamMicVal.Text = $"%{(int)SldStreamMic.Value}";
            if (TxtStreamSysVal != null && SldStreamSys != null) TxtStreamSysVal.Text = $"%{(int)SldStreamSys.Value}";
            var a = _engine?.ActiveStream?.Audio;
            if (a != null)
            {
                a.MicGain = (float)(SldStreamMic.Value / 100.0);
                a.SysGain = (float)(SldStreamSys.Value / 100.0);
            }
        }

        private async void BtnToggleRtmp_Click(object sender, RoutedEventArgs e)
        {
            if (!_engine.IsStreamingRtmp)
            {
                var settings = SaveStreamSettingsFromUi();
                if (!_engine.StartMultiStream(settings, out string err))
                {
                    TxtStatusBanner.Text = "⚠️ Yayın başlatılamadı: " + err;
                    TxtStreamStats.Text = "⚠️ " + err;
                    return;
                }
                BtnToggleRtmp.Content = "■ Yayını Durdur";
                BtnToggleRtmp.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
                string names = string.Join(" + ", _engine.ActiveStream.DestinationNames);
                TxtStatusBanner.Text = $"🔴 CANLI: {names} ({(settings.Vertical ? "9:16" : "16:9")} • {settings.Height}p • {settings.BitrateKbps} kbps) — bağlantı kuruluyor...";

                // İlk 6 saniyede çöken yayını yakala (yanlış anahtar / sunucu)
                await System.Threading.Tasks.Task.Delay(6000);
                var st = _engine.ActiveStream;
                if (_engine.IsStreamingRtmp && st != null && !st.IsAlive)
                {
                    string tail = st.LastError;
                    if (string.IsNullOrWhiteSpace(tail)) tail = st.ErrorTail();
                    _engine.StopRtmpBroadcast();
                    ResetStreamButton();
                    TxtStatusBanner.Text = "⚠️ Yayın sunucusu bağlantıyı reddetti. Anahtarı ve sunucu adresini kontrol edin.";
                    TxtStreamStats.Text = "⚠️ " + (tail ?? "").Trim();
                }
            }
            else
            {
                _engine.StopRtmpBroadcast();
                ResetStreamButton();
                TxtStatusBanner.Text = "Canlı yayın durduruldu.";
            }
        }

        private void ResetStreamButton()
        {
            BtnToggleRtmp.Content = "● Yayını Başlat";
            BtnToggleRtmp.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            TxtStreamStats.Text = "Yayın kapalı";
        }

        private void UpdateStreamStatsUi()
        {
            var st = _engine?.ActiveStream;
            if (st == null || TxtStreamStats == null) return;
            if (!st.IsAlive)
            {
                TxtStreamStats.Text = "⚠️ Yayın kesildi: " + (string.IsNullOrWhiteSpace(st.LastError) ? "bağlantı koptu" : st.LastError);
                return;
            }
            string up = st.Uptime.ToString(@"hh\:mm\:ss");
            string warn = string.IsNullOrWhiteSpace(st.LastError) ? "" : "\n⚠️ " + st.LastError;
            TxtStreamStats.Text = $"🔴 CANLI {up} • {string.Join(" + ", st.DestinationNames)}\n{st.LastStats}{warn}";
        }

        private void BtnToggleIntermission_Click(object sender, RoutedEventArgs e) => ToggleIntermission();

        private void ToggleIntermission()
        {
            _engine.IntermissionActive = !_engine.IntermissionActive;
            if (BtnToggleIntermission != null)
            {
                BtnToggleIntermission.Content = _engine.IntermissionActive ? "▶ Yayına Dön (Alt+Shift+B)" : "⏸ Bekleme Ekranı (Alt+Shift+B)";
                BtnToggleIntermission.Background = _engine.IntermissionActive
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
            }
            TxtStatusBanner.Text = _engine.IntermissionActive ? "⏸ Bekleme ekranı yayında (ekranınız gizlendi)." : "▶ Ekran paylaşımına dönüldü.";
        }

        private void BtnPickIntermissionImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Bekleme / Ara Ekranı Görseli Seç",
                Filter = "Görseller|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
            };
            if (dlg.ShowDialog() == true)
            {
                _engine.SetIntermissionImage(dlg.FileName);
                _streamSettings ??= StreamSettings.CreateDefault();
                _streamSettings.IntermissionImagePath = dlg.FileName;
                _streamSettings.Save();
                TxtStatusBanner.Text = $"Bekleme görseli ayarlandı: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
        }
        // ======================= PROJE, YATAY / DİKEY KLASÖRLER & SÜRÜKLE-BIRAK CIRCLE EDIT STÜDYOSU =======================

        private sealed class FolderMediaItem
        {
            public string FilePath { get; set; } = "";
            public string DisplayText { get; set; } = "";
            public bool IsVertical { get; set; }
            public override string ToString() => DisplayText;
        }

        private void RefreshCircleEditFolderLists()
        {
            if (LstHorizFolderFiles == null || LstVertFolderFiles == null) return;

            try
            {
                string horizDir = HardwareEncoderService.GetHorizontalRecordingsDir();
                string vertDir = HardwareEncoderService.GetVerticalRecordingsDir();

                LstHorizFolderFiles.Items.Clear();
                var hFiles = Directory.GetFiles(horizDir)
                    .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .ToList();

                foreach (var f in hFiles)
                {
                    var fi = new FileInfo(f);
                    double mb = fi.Length / (1024.0 * 1024.0);
                    LstHorizFolderFiles.Items.Add(new FolderMediaItem
                    {
                        FilePath = f,
                        IsVertical = false,
                        DisplayText = $"{fi.Name}  •  {mb:0.1} MB"
                    });
                }

                LstVertFolderFiles.Items.Clear();
                var vFiles = Directory.GetFiles(vertDir)
                    .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .ToList();

                foreach (var f in vFiles)
                {
                    var fi = new FileInfo(f);
                    double mb = fi.Length / (1024.0 * 1024.0);
                    LstVertFolderFiles.Items.Add(new FolderMediaItem
                    {
                        FilePath = f,
                        IsVertical = true,
                        DisplayText = $"{fi.Name}  •  {mb:0.1} MB"
                    });
                }
            }
            catch { }
        }

        private void BtnRefreshCircleEditFolders_Click(object sender, RoutedEventArgs e)
        {
            RefreshCircleEditFolderLists();
            TxtStatusBanner.Text = "Medya listesi yenilendi";
        }

        private void BtnOpenHorizFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = HardwareEncoderService.GetHorizontalRecordingsDir();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        private void BtnOpenVertFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = HardwareEncoderService.GetVerticalRecordingsDir();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        private void BtnCircleEditNewBlank_Click(object sender, RoutedEventArgs e)
        {
            PauseCircleEditPlayer();
            CircleEditMediaPlayer.Source = null;
            CircleEditMediaPlayer.Visibility = Visibility.Collapsed;
            BdrCircleEditSpeedBadge.Visibility = Visibility.Collapsed;
            BdrCircleEditEmptyState.Visibility = Visibility.Visible;
            _activeCircleEditVideoPath = "";
            _activeTimelapseSpeed = 1.0;
            _undoStack.Clear();

            InitializeStarterTimelineProject();
            TxtCircleEditActiveFile.Text = "Video seçin veya sürükleyin";
            TxtStatusBanner.Text = "Tuval temizlendi";
            RedrawTimeline();
        }

        private void BtnImportExternalToPool_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Circle Edit Kurguya Video / Ses / Görsel Ekle",
                Filter = "Medya Dosyaları (*.mp4;*.mkv;*.mov;*.mp3;*.wav;*.png;*.jpg)|*.mp4;*.mkv;*.mov;*.mp3;*.wav;*.png;*.jpg|Tüm Dosyalar (*.*)|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                HandleDroppedOrSelectedFiles(dlg.FileNames);
            }
        }

        private void LstHorizFolderFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstHorizFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
            }
        }

        private void LstVertFolderFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstVertFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
            }
        }

        private void LstHorizFolderFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstHorizFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
                PlayCircleEditPlayer();
            }
        }

        private void LstVertFolderFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstVertFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
                PlayCircleEditPlayer();
            }
        }

        private void FolderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _folderDragStartPoint = e.GetPosition(null);
        }

        private void FolderList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            System.Windows.Point pos = e.GetPosition(null);
            Vector diff = _folderDragStartPoint - pos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
                {
                    var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new string[] { item.FilePath });
                    DragDrop.DoDragDrop(lb, data, System.Windows.DragDropEffects.Copy);
                }
            }
        }

        private void CircleEditDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void CircleEditDropZone_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    HandleDroppedOrSelectedFiles(files);
                }
            }
        }

        private void HandleDroppedOrSelectedFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;
            if (_activeStudioMode != "Edit")
                SetStudioViewMode("Edit");

            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();

                if (ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".aac" || ext == ".ogg")
                {
                    _currentProject.ExternalAudioTrackPath = file;
                    if (TxtExternalAudioStatus != null)
                        TxtExternalAudioStatus.Text = $"🎵 Fon Müziği: {System.IO.Path.GetFileName(file)}";
                    TxtStatusBanner.Text = $"🎵 Ses / Müzik dosyası Timeline'a eklendi: {System.IO.Path.GetFileName(file)}";
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp")
                {
                    _currentProject.PipOverlayMediaPath = file;
                    _engine.SetPipOverlayMedia(file);
                    if (TxtPipMediaStatus != null)
                        TxtPipMediaStatus.Text = $"🖼️ Bindirme (PIP): {System.IO.Path.GetFileName(file)}";
                    TxtStatusBanner.Text = $"🖼️ Görsel bindirme (PIP Overlay) eklendi: {System.IO.Path.GetFileName(file)}";
                }
                else if (ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".avi" || ext == ".webm")
                {
                    bool isVert = file.Contains("Dikey", StringComparison.OrdinalIgnoreCase) || file.Contains("9x16", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(_activeCircleEditVideoPath))
                    {
                        LoadVideoIntoCircleEditEditor(file, isVert, preserveCurrentProject: false);
                    }
                    else if (!string.Equals(_activeCircleEditVideoPath, file, StringComparison.OrdinalIgnoreCase))
                    {
                        // Halihazırda açık bir video varsa hem oynatıcıya yükle hem de birleştirme listesine ekle
                        LoadVideoIntoCircleEditEditor(file, isVert, preserveCurrentProject: false);
                    }
                }
            }
        }

        private void LoadVideoIntoCircleEditEditor(string videoPath, bool isVertical, bool preserveCurrentProject)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return;

            var probe = TimelineRenderService.ProbeMediaInfo(videoPath);
            if (probe.HasVideo && probe.Width > 0 && probe.Height > 0 && !preserveCurrentProject)
            {
                // Videonun gerçek çözünürlük ve telefon Display Matrix döndürme bilgisinden otomatik Dikey (9:16) / Yatay (16:9) algıla!
                isVertical = probe.IsVertical;
            }

            _activeCircleEditVideoPath = videoPath;
            _isCircleEditShowingVertical = isVertical;

            if (!preserveCurrentProject)
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
                string horizCandidate = videoPath;
                string vertCandidate = videoPath;

                // Eşleşen yatay/dikey kardeş dosyayı otomatik bul
                if (isVertical)
                {
                    string hMatch = System.IO.Path.Combine(
                        HardwareEncoderService.GetHorizontalRecordingsDir(),
                        baseName.Replace("_Dikey_9x16", "_Yatay_16x9") + ".mp4");
                    if (File.Exists(hMatch) && !string.Equals(hMatch, videoPath, StringComparison.OrdinalIgnoreCase))
                        horizCandidate = hMatch;
                }
                else
                {
                    string vMatch = System.IO.Path.Combine(
                        HardwareEncoderService.GetVerticalRecordingsDir(),
                        baseName.Replace("_Yatay_16x9", "_Dikey_9x16") + ".mp4");
                    if (File.Exists(vMatch) && !string.Equals(vMatch, videoPath, StringComparison.OrdinalIgnoreCase))
                        vertCandidate = vMatch;
                }

                _currentProject = new CmrProject
                {
                    ProjectName = baseName.Replace("_Yatay_16x9", "").Replace("_Dikey_9x16", ""),
                    ProjectDirectory = HardwareEncoderService.GetBaseStorageDir(),
                    MasterCleanVideoPath = horizCandidate,
                    Horizontal16x9VideoPath = horizCandidate,
                    Vertical9x16VideoPath = vertCandidate,
                    TotalDurationSec = probe.DurationSec > 0.5 ? probe.DurationSec : 15.0,
                    MetadataRotationCwDeg = probe.RotationCwDeg,
                    UserExtraRotationCwDeg = 0,
                    Enable4KUltraUpscale = ChkCircle4KUltraUpscale?.IsChecked ?? true
                };
                _undoStack.Clear();
            }

            ApplyCircleEditPlayerRotationTransform();
            UpdateCircleEditHeaderOrientationBadges();

            BdrCircleEditEmptyState.Visibility = Visibility.Collapsed;
            CircleEditMediaPlayer.Visibility = Visibility.Visible;
            BdrCircleEditSpeedBadge.Visibility = Visibility.Visible;

            try
            {
                CircleEditMediaPlayer.Stop();
                CircleEditMediaPlayer.Source = new Uri(videoPath, UriKind.Absolute);
                CircleEditMediaPlayer.SpeedRatio = Math.Clamp(_activeTimelapseSpeed, 0.25, 8.0);
                CircleEditMediaPlayer.Play();
                _isPlayerPlaying = true;
                UpdatePlayPauseButtonLabels();
            }
            catch { }

            string orientInfo = _isCircleEditShowingVertical ? "9:16 Dikey" : "16:9 Yatay";
            TxtStatusBanner.Text = $"🎬 Yüklendi ({orientInfo}): {System.IO.Path.GetFileName(videoPath)} — 'Kes' (S) + 'Sil' (Del) ile ortadan kesin, 'Başı/Sonu Kırp' ile kırpın ve '4K Ultra Çıktı Al' ile kaydedin.";
        }

        private void ApplyCircleEditPlayerRotationTransform()
        {
            if (CircleEditMediaPlayer == null) return;
            int metaRot = _currentProject?.MetadataRotationCwDeg ?? 0;
            int userRot = _currentProject?.UserExtraRotationCwDeg ?? 0;
            int totalRot = ((metaRot + userRot) % 360 + 360) % 360;
            CircleEditMediaPlayer.LayoutTransform = totalRot == 0
                ? System.Windows.Media.Transform.Identity
                : new System.Windows.Media.RotateTransform(totalRot);
        }

        private void UpdateCircleEditHeaderOrientationBadges()
        {
            bool isVert = _isCircleEditShowingVertical;
            int userRot = _currentProject?.UserExtraRotationCwDeg ?? 0;
            string rotSuffix = userRot > 0 ? $" • ↻{userRot}°" : "";
            if (TxtCircleEditActiveFile != null && !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath))
            {
                TxtCircleEditActiveFile.Text = $"{System.IO.Path.GetFileName(_activeCircleEditVideoPath)} ({(isVert ? "9:16 Dikey" : "16:9 Yatay")}{rotSuffix})";
            }

            var activeBtnBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            var idleBtnBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
            if (BtnCircleEditView16x9 != null) BtnCircleEditView16x9.Background = !isVert ? activeBtnBg : idleBtnBg;
            if (BtnCircleEditView9x16 != null) BtnCircleEditView9x16.Background = isVert ? activeBtnBg : idleBtnBg;
        }

        private void BtnRotateVideo90_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || string.IsNullOrWhiteSpace(_activeCircleEditVideoPath))
            {
                TxtStatusBanner.Text = "Döndürmek için önce bir video yükleyin.";
                return;
            }
            _currentProject.UserExtraRotationCwDeg = (_currentProject.UserExtraRotationCwDeg + 90) % 360;
            _isCircleEditShowingVertical = !_isCircleEditShowingVertical;
            ApplyCircleEditPlayerRotationTransform();
            UpdateCircleEditHeaderOrientationBadges();
            RedrawTimeline();
            TxtStatusBanner.Text = $"↻ Video 90° döndürüldü (Toplam ek döndürme: {_currentProject.UserExtraRotationCwDeg}° • Format: {(_isCircleEditShowingVertical ? "9:16 Dikey" : "16:9 Yatay")}).";
        }

        private void CircleEditMediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (CircleEditMediaPlayer.NaturalDuration.HasTimeSpan)
            {
                double realDur = Math.Max(0.5, CircleEditMediaPlayer.NaturalDuration.TimeSpan.TotalSeconds);
                _currentProject.TotalDurationSec = realDur;

                if (_currentProject.Clips.Count == 0 ||
                    (_currentProject.Clips.Count == 1 && Math.Abs(_currentProject.Clips[0].SourceEndSec - realDur) > 0.5))
                {
                    _currentProject.Clips.Clear();
                    _currentProject.Clips.Add(new TimelineClip
                    {
                        Label = System.IO.Path.GetFileNameWithoutExtension(_activeCircleEditVideoPath),
                        SourceStartSec = 0.0,
                        SourceEndSec = Math.Round(realDur, 2),
                        SpeedMultiplier = _activeTimelapseSpeed,
                        IsDeleted = false,
                        IsSilentSegment = false
                    });

                    // Harici MP4 veya video yüklendiyse FFmpeg PCM üzerinden %100 gerçek ses dalga formunu çıkar
                    if (_currentProject.WaveformPeaks == null || _currentProject.WaveformPeaks.Count == 0)
                    {
                        string activePath = _activeCircleEditVideoPath;
                        _ = Task.Run(() =>
                        {
                            var realPeaks = AutoCutSilenceService.ExtractRealWaveformPeaks(activePath, out _, 480);
                            if (realPeaks.Count > 0)
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (_currentProject != null)
                                    {
                                        _currentProject.WaveformPeaks = realPeaks;
                                        RedrawTimeline();
                                    }
                                }));
                            }
                        });
                    }
                }

                _playheadSec = 0.0;
                _selectedClipIndex = 0;
                TxtTimelineTimecode.Text = $"{FormatShortTime(0)} / {FormatShortTime(realDur)}";
                UpdateLiveSubtitleOverlay();
                RedrawTimeline();
            }
        }

        private void CircleEditMediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            CircleEditMediaPlayer.Position = TimeSpan.Zero;
            _playheadSec = 0.0;
            PauseCircleEditPlayer();
            RedrawTimeline();
        }

        private void CircleEditMediaPlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BtnPlayerPlayPause_Click(sender, e);
        }

        private void PlayCircleEditPlayer()
        {
            if (CircleEditMediaPlayer.Source == null) return;
            try
            {
                CircleEditMediaPlayer.Play();
                _isPlayerPlaying = true;
                UpdatePlayPauseButtonLabels();
            }
            catch { }
        }

        private void PauseCircleEditPlayer()
        {
            try
            {
                CircleEditMediaPlayer.Pause();
            }
            catch { }
            _isPlayerPlaying = false;
            UpdatePlayPauseButtonLabels();
        }

        private void UpdatePlayPauseButtonLabels()
        {
            string txt = _isPlayerPlaying ? "⏸ Duraklat" : "▶ Oynat";
            if (BtnPlayerPlayPause != null) BtnPlayerPlayPause.Content = txt;
            if (BtnTimelinePlayPause != null) BtnTimelinePlayPause.Content = txt;
        }

        private void BtnPlayerPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (CircleEditMediaPlayer.Source == null)
            {
                // Oynatıcı boşsa Yatay veya Dikey klasördeki en son videoyu otomatik yükle
                if (LstHorizFolderFiles.Items.Count > 0 && LstHorizFolderFiles.Items[0] is FolderMediaItem hItem)
                {
                    LoadVideoIntoCircleEditEditor(hItem.FilePath, isVertical: false, preserveCurrentProject: false);
                    return;
                }
                if (LstVertFolderFiles.Items.Count > 0 && LstVertFolderFiles.Items[0] is FolderMediaItem vItem)
                {
                    LoadVideoIntoCircleEditEditor(vItem.FilePath, isVertical: true, preserveCurrentProject: false);
                    return;
                }
                TxtStatusBanner.Text = "Soldaki medya listesinden bir video seçin veya yeni kayıt alın";
                return;
            }

            if (_isPlayerPlaying)
                PauseCircleEditPlayer();
            else
                PlayCircleEditPlayer();
        }

        private void BtnPlayerStart_Click(object sender, RoutedEventArgs e)
        {
            SeekCircleEditPlayer(0.0);
        }

        private void BtnPlayerBack_Click(object sender, RoutedEventArgs e)
        {
            SeekCircleEditPlayer(Math.Max(0.0, _playheadSec - 3.0));
        }

        private void BtnPlayerForward_Click(object sender, RoutedEventArgs e)
        {
            double maxDur = Math.Max(1.0, _currentProject?.TotalDurationSec ?? 10.0);
            SeekCircleEditPlayer(Math.Min(maxDur, _playheadSec + 3.0));
        }

        private void SeekCircleEditPlayer(double targetSec)
        {
            _playheadSec = Math.Max(0.0, targetSec);
            if (CircleEditMediaPlayer.Source != null)
            {
                try
                {
                    CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec);
                }
                catch { }
            }
            double totalDur = Math.Max(1.0, _currentProject?.TotalDurationSec ?? 10.0);
            TxtTimelineTimecode.Text = $"{FormatShortTime(_playheadSec)} / {FormatShortTime(totalDur)}";
            RedrawTimeline();
        }

        private void BtnTogglePlayerMute_Click(object sender, RoutedEventArgs e)
        {
            _isPlayerMuted = !_isPlayerMuted;
            CircleEditMediaPlayer.IsMuted = _isPlayerMuted;
            if (BtnTogglePlayerMute != null)
            {
                BtnTogglePlayerMute.Content = _isPlayerMuted ? "Ses: Kapalı" : "Ses";
            }
        }

        private void BtnCircleEditSwitch16x9_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentProject?.Horizontal16x9VideoPath) && File.Exists(_currentProject.Horizontal16x9VideoPath))
            {
                LoadVideoIntoCircleEditEditor(_currentProject.Horizontal16x9VideoPath, isVertical: false, preserveCurrentProject: true);
            }
            else if (LstHorizFolderFiles.Items.Count > 0 && LstHorizFolderFiles.Items[0] is FolderMediaItem item)
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
            }
        }

        private void BtnCircleEditSwitch9x16_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentProject?.Vertical9x16VideoPath) && File.Exists(_currentProject.Vertical9x16VideoPath))
            {
                LoadVideoIntoCircleEditEditor(_currentProject.Vertical9x16VideoPath, isVertical: true, preserveCurrentProject: true);
            }
            else if (LstVertFolderFiles.Items.Count > 0 && LstVertFolderFiles.Items[0] is FolderMediaItem item)
            {
                LoadVideoIntoCircleEditEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
            }
        }

        // ======================= TIMELAPSE (HIZLANDIRMA: 0.5x - 30x) & 60 FPS TIMELAPSE KAYDET =======================

        private bool _isRecordingVoiceover = false;
        private string _activeVoiceoverWavPath = "";
        private bool _syncingSpeedCurveCombos = false;

        private void CmbQuickSpeedCurve_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || _syncingSpeedCurveCombos || CmbQuickSpeedCurve == null) return;
            _syncingSpeedCurveCombos = true;
            if (CmbSpeedCurvePreset != null)
                CmbSpeedCurvePreset.SelectedIndex = CmbQuickSpeedCurve.SelectedIndex;
            _syncingSpeedCurveCombos = false;
            ApplySelectedSpeedCurveMode(CmbQuickSpeedCurve.SelectedIndex, applyToTimelineImmediately: true);
        }

        private void CmbSpeedCurvePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || _syncingSpeedCurveCombos || CmbSpeedCurvePreset == null) return;
            _syncingSpeedCurveCombos = true;
            if (CmbQuickSpeedCurve != null)
                CmbQuickSpeedCurve.SelectedIndex = CmbSpeedCurvePreset.SelectedIndex;
            _syncingSpeedCurveCombos = false;
            ApplySelectedSpeedCurveMode(CmbSpeedCurvePreset.SelectedIndex, applyToTimelineImmediately: false);
        }

        private void ApplySelectedSpeedCurveMode(int presetIdx, bool applyToTimelineImmediately)
        {
            var preset = (SpeedCurvePreset)Math.Clamp(presetIdx, 0, 8);
            if (_currentProject != null)
                _currentProject.ActiveSpeedCurve = preset;

            var pts = SpeedCurveCatalog.GetPresetPoints(preset, _activeTimelapseSpeed);
            double minSpd = pts.Min(p => p.Speed);
            double maxSpd = pts.Max(p => p.Speed);

            if (TxtSpeedCurvePeakInfo != null)
            {
                TxtSpeedCurvePeakInfo.Text = preset == SpeedCurvePreset.Constant
                    ? $"{_activeTimelapseSpeed:0.#}x Sabit"
                    : $"{minSpd:0.#}x ➔ {maxSpd:0.#}x";
            }

            if (TxtCircleEditSpeedBadge != null)
            {
                TxtCircleEditSpeedBadge.Text = preset == SpeedCurvePreset.Constant
                    ? $"{_activeTimelapseSpeed:0.#}x"
                    : $"⚡ {preset}";
            }

            RedrawSpeedCurveCanvas();

            if (applyToTimelineImmediately && _currentProject != null && _currentProject.Clips.Count > 0)
            {
                PushUndoSnapshot();
                TimelineRenderService.ApplySpeedCurveToTimelineClip(_currentProject, _selectedClipIndex, preset, _activeTimelapseSpeed);
                RedrawTimeline();
                TxtStatusBanner.Text = preset == SpeedCurvePreset.Constant
                    ? $"Sabit hız seçildi ({_activeTimelapseSpeed:0.#}x)."
                    : $"⚡ '{preset}' dinamik hız eğrisi ({minSpd:0.#}x ➔ {maxSpd:0.#}x) Timeline üzerine uygulandı!";
            }
        }

        private void BtnApplySpeedCurveToTimeline_Click(object sender, RoutedEventArgs e)
        {
            int idx = CmbSpeedCurvePreset?.SelectedIndex ?? 0;
            ApplySelectedSpeedCurveMode(idx, applyToTimelineImmediately: true);
        }

        private void SpeedCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawSpeedCurveCanvas();

        private void RedrawSpeedCurveCanvas()
        {
            if (SpeedCurveCanvas == null || SpeedCurveCanvas.ActualWidth < 20 || SpeedCurveCanvas.ActualHeight < 16)
                return;

            SpeedCurveCanvas.Children.Clear();
            double w = SpeedCurveCanvas.ActualWidth;
            double h = SpeedCurveCanvas.ActualHeight;

            // Orta referans çizgisi (1.0x normal hız)
            var refLine = new Line
            {
                X1 = 0,
                X2 = w,
                Y1 = h * 0.72,
                Y2 = h * 0.72,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 37, 128)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            SpeedCurveCanvas.Children.Add(refLine);

            var preset = _currentProject?.ActiveSpeedCurve ?? SpeedCurvePreset.Constant;
            var pts = SpeedCurveCatalog.GetPresetPoints(preset, _activeTimelapseSpeed);
            if (pts.Count < 2) return;

            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                StrokeThickness = 2.2
            };

            foreach (var p in pts)
            {
                double x = 8 + (Math.Clamp(p.NormalizedProgress, 0, 100) / 100.0) * (w - 16);
                double normY = Math.Clamp((p.Speed - 0.2) / 7.0, 0.0, 1.0);
                double y = (h - 8) - normY * (h - 16);
                poly.Points.Add(new System.Windows.Point(x, y));
            }
            SpeedCurveCanvas.Children.Add(poly);

            foreach (var pt in poly.Points)
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = System.Windows.Media.Brushes.White,
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(dot, pt.X - 3);
                Canvas.SetTop(dot, pt.Y - 3);
                SpeedCurveCanvas.Children.Add(dot);
            }
        }

        private void BtnDetectBeats_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;
            var beats = AutoCutSilenceService.DetectAudioBeatMarkers(_currentProject, _activeCircleEditVideoPath, autoSplitClipsOnBeats: false);
            RedrawTimeline();
            TxtStatusBanner.Text = beats.Count > 0
                ? $"🎵 Ses ritim analizi tamamlandı: {beats.Count} adet '🎵 BEAT' vuruş noktası Timeline üzerine işaretlendi!"
                : "🎵 Belirgin ritim vuruşu bulunamadı (Arka plan müziği veya sesli bir video yükleyin).";
        }

        private void BtnSplitOnBeats_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;
            PushUndoSnapshot();
            var beats = AutoCutSilenceService.DetectAudioBeatMarkers(_currentProject, _activeCircleEditVideoPath, autoSplitClipsOnBeats: true);
            RedrawTimeline();
            TxtStatusBanner.Text = beats.Count > 0
                ? $"✂️ Video klipleri {beats.Count} adet müzik ritim (BEAT) vuruşundan otomatik bölündü!"
                : "🎵 Bölünecek ritim vuruşu bulunamadı.";
        }

        private async void BtnRecordVoiceover_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecordingVoiceover)
            {
                string voDir = System.IO.Path.Combine(HardwareEncoderService.GetBaseStorageDir(), "Proje_Verileri", "Seslendirmeler");
                Directory.CreateDirectory(voDir);
                _activeVoiceoverWavPath = System.IO.Path.Combine(voDir, $"Circle_Seslendirme_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                string dummySysWav = System.IO.Path.Combine(voDir, "_vo_sys_ignore.wav");

                _isRecordingVoiceover = true;
                if (BtnRecordVoiceover != null)
                {
                    BtnRecordVoiceover.Content = "⏹ Seslendirmeyi Bitir";
                    BtnRecordVoiceover.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
                }

                // Oynatıcıyı başa alıp sessiz oynatırken mikrofonu kaydet
                if (CircleEditMediaPlayer.Source != null)
                {
                    CircleEditMediaPlayer.IsMuted = true;
                    SeekCircleEditPlayer(0);
                    PlayCircleEditPlayer();
                }

                _engine.AudioEngine.StartRecordingToWav(_activeVoiceoverWavPath, dummySysWav);
                TxtStatusBanner.Text = "🎙️ Canlı Seslendirme (Voiceover) Kaydediliyor... Konuşmanız bittiğinde '⏹ Seslendirmeyi Bitir' butonuna basın.";
            }
            else
            {
                _isRecordingVoiceover = false;
                _engine.AudioEngine.StopRecordingWav();
                PauseCircleEditPlayer();
                CircleEditMediaPlayer.IsMuted = _isPlayerMuted;

                if (BtnRecordVoiceover != null)
                {
                    BtnRecordVoiceover.Content = "🎙️ Seslendirme";
                    BtnRecordVoiceover.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
                }

                await Task.Delay(150);
                if (!string.IsNullOrWhiteSpace(_activeVoiceoverWavPath) && File.Exists(_activeVoiceoverWavPath))
                {
                    _currentProject.ExternalAudioTrackPath = _activeVoiceoverWavPath;
                    _currentProject.ExternalAudioVolumePercent = 80;
                    if (SldBgmVolume != null) SldBgmVolume.Value = 80;
                    if (TxtExternalAudioStatus != null)
                        TxtExternalAudioStatus.Text = $"🎙️ Seslendirme: {System.IO.Path.GetFileName(_activeVoiceoverWavPath)}";
                    TxtStatusBanner.Text = $"✅ Seslendirme kaydedildi ve projeye bağlandı: {System.IO.Path.GetFileName(_activeVoiceoverWavPath)}";
                }
            }
        }

        private async void BtnExtractAudio_Click(object sender, RoutedEventArgs e)
        {
            string srcVideo = !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath) && File.Exists(_activeCircleEditVideoPath)
                ? _activeCircleEditVideoPath
                : (_isCircleEditShowingVertical ? _currentProject?.Vertical9x16VideoPath : _currentProject?.Horizontal16x9VideoPath);

            if (string.IsNullOrWhiteSpace(srcVideo) || !File.Exists(srcVideo))
            {
                TxtStatusBanner.Text = "Sesi ayıklamak için önce bir video seçin.";
                return;
            }

            string wavPath = await TimelineRenderService.ExtractAudioToWavAsync(
                srcVideo,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
            {
                TxtStatusBanner.Text = $"🔊 Ses kanalı 48 kHz Stereo WAV olarak ayıklandı: {System.IO.Path.GetFileName(wavPath)}";
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{wavPath}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private async void BtnReverseClip_Click(object sender, RoutedEventArgs e)
        {
            string srcVideo = !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath) && File.Exists(_activeCircleEditVideoPath)
                ? _activeCircleEditVideoPath
                : (_isCircleEditShowingVertical ? _currentProject?.Vertical9x16VideoPath : _currentProject?.Horizontal16x9VideoPath);

            if (string.IsNullOrWhiteSpace(srcVideo) || !File.Exists(srcVideo))
            {
                TxtStatusBanner.Text = "Ters çevirmek için önce bir video seçin.";
                return;
            }

            PauseCircleEditPlayer();
            string outMp4 = await TimelineRenderService.RenderReversedClipAsync(
                srcVideo,
                _isCircleEditShowingVertical,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(outMp4) && File.Exists(outMp4))
            {
                RefreshCircleEditFolderLists();
                LoadVideoIntoCircleEditEditor(outMp4, _isCircleEditShowingVertical, preserveCurrentProject: false);
                TxtStatusBanner.Text = $"⏪ Video 60 FPS geriye doğru (Reverse) çevrildi ve yüklendi: {System.IO.Path.GetFileName(outMp4)}";
            }
        }

        private void ApplyTimelapseSpeed(double speed)
        {
            PushUndoSnapshot();
            _activeTimelapseSpeed = speed;
            if (_currentProject != null)
                _currentProject.ActiveSpeedCurve = SpeedCurvePreset.Constant;

            _syncingSpeedCurveCombos = true;
            if (CmbQuickSpeedCurve != null) CmbQuickSpeedCurve.SelectedIndex = 0;
            if (CmbSpeedCurvePreset != null) CmbSpeedCurvePreset.SelectedIndex = 0;
            _syncingSpeedCurveCombos = false;
            RedrawSpeedCurveCanvas();

            if (_currentProject?.Clips != null && _currentProject.Clips.Count > 0)
            {
                if (_currentProject.Clips.Count == 1)
                {
                    _currentProject.Clips[0].SpeedMultiplier = speed;
                }
                else if (_selectedClipIndex >= 0 && _selectedClipIndex < _currentProject.Clips.Count)
                {
                    _currentProject.Clips[_selectedClipIndex].SpeedMultiplier = speed;
                }
            }

            if (CircleEditMediaPlayer.Source != null)
            {
                try
                {
                    CircleEditMediaPlayer.SpeedRatio = Math.Clamp(speed, 0.25, 8.0);
                }
                catch { }
            }

            if (TxtCircleEditSpeedBadge != null)
            {
                TxtCircleEditSpeedBadge.Text = $"{speed:0.#}x";
            }
            if (TxtSpeedCurvePeakInfo != null)
            {
                TxtSpeedCurvePeakInfo.Text = $"{speed:0.#}x Sabit";
            }

            RedrawTimeline();
            TxtStatusBanner.Text = $"Hız: {speed:0.#}x";
        }

        private void BtnSpeed05x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(0.5);
        private void BtnSpeed1x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(1.0);
        private void BtnSpeed2x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(2.0);
        private void BtnSpeed4x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(4.0);
        private void BtnSpeed8x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(8.0);
        private void BtnSpeed16x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(16.0);
        private void BtnSpeed30x_Click(object sender, RoutedEventArgs e) => ApplyTimelapseSpeed(30.0);

        private async void BtnRenderInstantTimelapse_Click(object sender, RoutedEventArgs e)
        {
            string srcVideo = !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath) && File.Exists(_activeCircleEditVideoPath)
                ? _activeCircleEditVideoPath
                : (_isCircleEditShowingVertical ? _currentProject?.Vertical9x16VideoPath : _currentProject?.Horizontal16x9VideoPath);

            if (string.IsNullOrWhiteSpace(srcVideo) || !File.Exists(srcVideo))
            {
                System.Windows.MessageBox.Show(
                    "Önce soldaki Yatay veya Dikey klasörden bir video seçin veya yeni bir kayıt alın.",
                    "Circle Studio — Timelapse & Hız Eğrisi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var curvePreset = _currentProject?.ActiveSpeedCurve ?? SpeedCurvePreset.Constant;
            double spd = _activeTimelapseSpeed <= 1.05 ? 4.0 : _activeTimelapseSpeed;
            PauseCircleEditPlayer();

            string outFile = await TimelineRenderService.RenderSpeedCurveVideoAsync(
                srcVideo,
                _isCircleEditShowingVertical,
                curvePreset,
                spd,
                muteAudio: _isPlayerMuted || (curvePreset == SpeedCurvePreset.Constant && spd >= 8.0),
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(outFile) && File.Exists(outFile))
            {
                RefreshCircleEditFolderLists();
                LoadVideoIntoCircleEditEditor(outFile, _isCircleEditShowingVertical, preserveCurrentProject: false);
                _activeTimelapseSpeed = 1.0;
                string modeDesc = curvePreset == SpeedCurvePreset.Constant ? $"Timelapse ({spd:0.#}x)" : $"Hız Eğrisi ({curvePreset})";
                TxtStatusBanner.Text = $"✅ 60 FPS {modeDesc} videosu üretildi ve klasöre eklendi: {System.IO.Path.GetFileName(outFile)}";
            }
        }

        private void PushUndoSnapshot()
        {
            if (_currentProject?.Clips == null) return;
            var cloneList = _currentProject.Clips.Select(c => new TimelineClip
            {
                Label = c.Label,
                SourceStartSec = c.SourceStartSec,
                SourceEndSec = c.SourceEndSec,
                SpeedMultiplier = c.SpeedMultiplier,
                IsDeleted = c.IsDeleted,
                IsSilentSegment = c.IsSilentSegment
            }).ToList();
            _undoStack.Push(cloneList);
        }

        private void BtnUndoEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count > 0 && _currentProject != null)
            {
                _currentProject.Clips = _undoStack.Pop();
                _selectedClipIndex = Math.Clamp(_selectedClipIndex, 0, Math.Max(0, _currentProject.Clips.Count - 1));
                UpdateSilenceSummaryText();
                RedrawTimeline();
                TxtStatusBanner.Text = "↩️ Son kesme / silme / hız işlemi geri alındı (Ctrl+Z).";
            }
            else
            {
                BtnResetSilenceCuts_Click(sender, e);
            }
        }

        private void BtnDuplicateClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex < 0 || _selectedClipIndex >= _currentProject.Clips.Count) return;
            PushUndoSnapshot();
            var src = _currentProject.Clips[_selectedClipIndex];
            var copy = new TimelineClip
            {
                Label = src.Label + " (Kopya)",
                SourceStartSec = src.SourceStartSec,
                SourceEndSec = src.SourceEndSec,
                SpeedMultiplier = src.SpeedMultiplier,
                IsDeleted = false,
                IsSilentSegment = src.IsSilentSegment
            };
            _currentProject.Clips.Insert(_selectedClipIndex + 1, copy);
            _selectedClipIndex++;
            RedrawTimeline();
            TxtStatusBanner.Text = $"📋 Klip çoğaltıldı: {copy.Label}";
        }

        private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Circle Studio Projesi (.cmrproj) veya Video (.mp4/.mkv) Seçin",
                Filter = "Circle Mix & Video Dosyaları (*.cmrproj;*.mp4;*.mkv)|*.cmrproj;*.mp4;*.mkv|Tüm Dosyalar (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                SetStudioViewMode("Edit");

                if (ext == ".cmrproj")
                {
                    _currentProject = CmrProject.Load(dlg.FileName);
                    string vPath = File.Exists(_currentProject.Horizontal16x9VideoPath)
                        ? _currentProject.Horizontal16x9VideoPath
                        : _currentProject.Vertical9x16VideoPath;
                    if (File.Exists(vPath))
                        LoadVideoIntoCircleEditEditor(vPath, isVertical: false, preserveCurrentProject: true);
                }
                else
                {
                    bool isVert = dlg.FileName.Contains("Dikey", StringComparison.OrdinalIgnoreCase) || dlg.FileName.Contains("9x16", StringComparison.OrdinalIgnoreCase);
                    LoadVideoIntoCircleEditEditor(dlg.FileName, isVert, preserveCurrentProject: false);
                }

                RefreshSubtitleListBox();
                UpdateSilenceSummaryText();
                RedrawTimeline();
                TxtStatusBanner.Text = $"Yüklendi: {dlg.FileName}";
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = HardwareEncoderService.GetBaseStorageDir();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        // ======================= MANYETİK BİRLEŞİK TIMELINE (MAGNETIC RIPPLE TIMELINE) VE SES DALGA FORMU =======================

        private struct MagneticClipSpan
        {
            public int ClipIndex;
            public TimelineClip Clip;
            public double MagStartSec;
            public double MagEndSec;
            public double X1;
            public double X2;
        }

        private List<MagneticClipSpan> GetMagneticTimelineSpans(double headerW, double trackAreaW, out double netTotalDur)
        {
            var list = new List<MagneticClipSpan>();
            double curMag = 0.0;

            if (_currentProject?.Clips != null)
            {
                for (int i = 0; i < _currentProject.Clips.Count; i++)
                {
                    var c = _currentProject.Clips[i];
                    if (c.IsDeleted || c.SourceDurationSec < 0.05) continue;

                    double outDur = Math.Max(0.05, c.OutputDurationSec);
                    list.Add(new MagneticClipSpan
                    {
                        ClipIndex = i,
                        Clip = c,
                        MagStartSec = curMag,
                        MagEndSec = curMag + outDur
                    });
                    curMag += outDur;
                }
            }

            netTotalDur = Math.Max(0.5, curMag > 0.05 ? curMag : (_currentProject?.TotalDurationSec ?? 10.0));
            for (int i = 0; i < list.Count; i++)
            {
                var sp = list[i];
                sp.X1 = headerW + (sp.MagStartSec / netTotalDur) * trackAreaW;
                sp.X2 = headerW + (sp.MagEndSec / netTotalDur) * trackAreaW;
                list[i] = sp;
            }
            return list;
        }

        private double SourceSecToMagneticSec(double srcSec, int preferredClipIdx, out double netTotalDur)
        {
            var spans = GetMagneticTimelineSpans(105, 1000, out netTotalDur);
            if (spans.Count == 0) return Math.Clamp(srcSec, 0.0, netTotalDur);

            foreach (var sp in spans)
            {
                if (sp.ClipIndex == preferredClipIdx &&
                    srcSec >= sp.Clip.SourceStartSec - 0.05 &&
                    srcSec <= sp.Clip.SourceEndSec + 0.05)
                {
                    double localOut = Math.Clamp(srcSec - sp.Clip.SourceStartSec, 0.0, sp.Clip.SourceDurationSec) / Math.Max(0.1, sp.Clip.SpeedMultiplier);
                    return Math.Clamp(sp.MagStartSec + localOut, 0.0, netTotalDur);
                }
            }

            foreach (var sp in spans)
            {
                if (srcSec >= sp.Clip.SourceStartSec - 0.05 &&
                    srcSec <= sp.Clip.SourceEndSec + 0.05)
                {
                    double localOut = Math.Clamp(srcSec - sp.Clip.SourceStartSec, 0.0, sp.Clip.SourceDurationSec) / Math.Max(0.1, sp.Clip.SpeedMultiplier);
                    return Math.Clamp(sp.MagStartSec + localOut, 0.0, netTotalDur);
                }
            }

            foreach (var sp in spans)
            {
                if (sp.Clip.SourceStartSec >= srcSec)
                    return sp.MagStartSec;
            }

            return netTotalDur;
        }

        private double MagneticSecToSourceSec(double magSec, out int clipIdx, out double netTotalDur)
        {
            var spans = GetMagneticTimelineSpans(105, 1000, out netTotalDur);
            clipIdx = 0;
            if (spans.Count == 0) return Math.Clamp(magSec, 0.0, netTotalDur);

            double clampedMag = Math.Clamp(magSec, 0.0, netTotalDur);
            for (int i = 0; i < spans.Count; i++)
            {
                var sp = spans[i];
                if (clampedMag <= sp.MagEndSec + 0.001 || i == spans.Count - 1)
                {
                    clipIdx = sp.ClipIndex;
                    double localMag = Math.Clamp(clampedMag - sp.MagStartSec, 0.0, Math.Max(0.01, sp.MagEndSec - sp.MagStartSec));
                    double srcSec = sp.Clip.SourceStartSec + localMag * Math.Max(0.1, sp.Clip.SpeedMultiplier);
                    return Math.Clamp(srcSec, sp.Clip.SourceStartSec, sp.Clip.SourceEndSec);
                }
            }

            clipIdx = spans[^1].ClipIndex;
            return spans[^1].Clip.SourceEndSec;
        }

        private void RenumberActiveClips()
        {
            if (_currentProject?.Clips == null) return;
            _currentProject.Clips.RemoveAll(c => c.IsDeleted || c.SourceDurationSec < 0.04);

            string baseName = !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath)
                ? System.IO.Path.GetFileNameWithoutExtension(_activeCircleEditVideoPath)
                : (_currentProject.ProjectName ?? "Klip");
            if (baseName.Length > 20) baseName = baseName.Substring(0, 20);

            if (_currentProject.Clips.Count == 1)
            {
                _currentProject.Clips[0].Label = baseName;
            }
            else
            {
                for (int i = 0; i < _currentProject.Clips.Count; i++)
                {
                    _currentProject.Clips[i].Label = $"{baseName} #{i + 1}";
                }
            }
        }

        private void BtnSplitClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _currentProject.Clips.Count == 0) return;

            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                if (c.IsDeleted) continue;
                if (_playheadSec > c.SourceStartSec + 0.12 && _playheadSec < c.SourceEndSec - 0.12)
                {
                    PushUndoSnapshot();
                    double splitPoint = Math.Round(_playheadSec, 2);
                    double origEnd = c.SourceEndSec;
                    bool hadPreviousCut = (i > 0 || c.SourceStartSec > 0.10);
                    c.SourceEndSec = splitPoint;

                    var secondHalf = new TimelineClip
                    {
                        Label = c.Label,
                        SourceStartSec = splitPoint,
                        SourceEndSec = origEnd,
                        SpeedMultiplier = c.SpeedMultiplier,
                        IsDeleted = false,
                        IsSilentSegment = c.IsSilentSegment
                    };
                    _currentProject.Clips.Insert(i + 1, secondHalf);
                    RenumberActiveClips();

                    // Eğer kullanıcı ortadan bir bölümü çıkarmak için 2. kesimi yaptıysa, iki kesim arasındaki orta parçayı (i) seçili bırak ki Sil (Del) tuşu o orta parçayı silip kalanları birleştirsin!
                    _selectedClipIndex = Math.Clamp(hadPreviousCut ? i : (i + 1), 0, _currentProject.Clips.Count - 1);
                    var selClip = _currentProject.Clips[_selectedClipIndex];
                    TxtStatusBanner.Text = $"✂️ {FormatShortTime(splitPoint)} noktasından kesildi! Seçili parça: {selClip.Label} ({FormatShortTime(selClip.SourceStartSec)}–{FormatShortTime(selClip.SourceEndSec)}) — Silip otomatik birleştirmek için 'Sil (Birleştir)' veya Del tuşuna basın.";
                    RedrawTimeline();
                    return;
                }
            }
            TxtStatusBanner.Text = "✂️ Bölmek için turuncu çizgiyi (Playhead) klibin içinde bir noktaya getirin ve 'Kes' (S) tuşuna basın.";
        }

        private void BtnTrimStartToPlayhead_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _currentProject.Clips.Count == 0) return;
            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                if (c.IsDeleted) continue;
                if (_playheadSec > c.SourceStartSec + 0.10 && _playheadSec < c.SourceEndSec - 0.15)
                {
                    PushUndoSnapshot();
                    double oldStart = c.SourceStartSec;
                    c.SourceStartSec = Math.Round(_playheadSec, 2);
                    _playheadSec = c.SourceStartSec;
                    RenumberActiveClips();
                    _selectedClipIndex = Math.Clamp(i, 0, _currentProject.Clips.Count - 1);
                    UpdateSilenceSummaryText();
                    RedrawTimeline();
                    TxtStatusBanner.Text = $"✂️ Klip başı kırpıldı ve manyetik birleştirildi ({FormatShortTime(oldStart)} ➔ {FormatShortTime(c.SourceStartSec)}). Geri almak için Ctrl+Z.";
                    return;
                }
            }
            TxtStatusBanner.Text = "✂️ Baştan kırpmak için turuncu çizgiyi klibin içinde kırpmak istediğiniz saniyeye getirin ve '✂ Başı Kırp' tuşuna basın.";
        }

        private void BtnTrimEndToPlayhead_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _currentProject.Clips.Count == 0) return;
            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                if (c.IsDeleted) continue;
                if (_playheadSec > c.SourceStartSec + 0.15 && _playheadSec < c.SourceEndSec - 0.10)
                {
                    PushUndoSnapshot();
                    double oldEnd = c.SourceEndSec;
                    c.SourceEndSec = Math.Round(_playheadSec, 2);
                    RenumberActiveClips();
                    _selectedClipIndex = Math.Clamp(i, 0, _currentProject.Clips.Count - 1);
                    if (_selectedClipIndex + 1 < _currentProject.Clips.Count)
                    {
                        _playheadSec = _currentProject.Clips[_selectedClipIndex + 1].SourceStartSec;
                    }
                    else
                    {
                        _playheadSec = c.SourceEndSec;
                    }
                    if (CircleEditMediaPlayer != null && CircleEditMediaPlayer.Source != null)
                    {
                        try { CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec); } catch { }
                    }
                    UpdateSilenceSummaryText();
                    RedrawTimeline();
                    TxtStatusBanner.Text = $"✂️ Klip sonu kırpıldı ve manyetik birleştirildi ({FormatShortTime(c.SourceEndSec)} ➔ {FormatShortTime(oldEnd)} kesildi). Geri almak için Ctrl+Z.";
                    return;
                }
            }
            TxtStatusBanner.Text = "✂️ Sondan kırpmak için turuncu çizgiyi klibin içinde kırpmak istediğiniz bitiş saniyesine getirin ve '✂ Sonu Kırp' tuşuna basın.";
        }

        private async void BtnExportActiveKurgu4K_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || string.IsNullOrWhiteSpace(_activeCircleEditVideoPath) || !File.Exists(_activeCircleEditVideoPath))
            {
                System.Windows.MessageBox.Show(
                    "4K Ultra HD çıktı almak için önce soldaki listeden veya '+ İçe Aktar' butonundan bir video seçin.",
                    "Circle Studio — 4K Ultra Kurgu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PauseCircleEditPlayer();
            bool use4K = ChkCircle4KUltraUpscale?.IsChecked ?? true;
            _currentProject.EnableNoiseSuppression = ChkAudioNoiseSuppress?.IsChecked ?? true;
            _currentProject.EnableLufsNormalization = ChkAudioLufsNorm?.IsChecked ?? true;
            _currentProject.BurnInSubtitles = ChkBurnInSubtitles?.IsChecked ?? true;

            if (BtnExportActiveKurgu4K != null) BtnExportActiveKurgu4K.IsEnabled = false;
            try
            {
                var (outPath, err) = await TimelineRenderService.ExportActiveTimeline4KUpscaleAsync(
                    _currentProject,
                    _activeCircleEditVideoPath,
                    _isCircleEditShowingVertical,
                    use4K,
                    msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

                if (!string.IsNullOrWhiteSpace(outPath) && File.Exists(outPath))
                {
                    RefreshCircleEditFolderLists();
                    string resDesc = _isCircleEditShowingVertical
                        ? (use4K ? "2160×3840 (4K Dikey 9:16 • 60 FPS)" : "1080×1920 (Full HD Dikey 9:16 • 60 FPS)")
                        : (use4K ? "3840×2160 (4K Yatay 16:9 • 60 FPS)" : "1920×1080 (Full HD Yatay 16:9 • 60 FPS)");

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{outPath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch { }

                    System.Windows.MessageBox.Show(
                        $"Circle 4K Ultra HD Kurgu Çıktısı Hazır!\n\n" +
                        $"• Dosya: {System.IO.Path.GetFileName(outPath)}\n" +
                        $"• Çözünürlük & Format: {resDesc}\n" +
                        $"• Birleştirilen Sağlam Parça: {_currentProject.Clips.Count(c => !c.IsDeleted)} parça (silinen yerler otomatik birleştirildi)\n\n" +
                        $"Dosya klasörde seçili olarak açıldı.",
                        "Circle Studio — 4K Ultra HD Çıktı Hazır",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "4K kurgu çıktısı üretilirken hata oluştu:\n\n" + (err ?? "Bilinmeyen hata"),
                        "Circle Studio — Dışa Aktarma Hatası",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                if (BtnExportActiveKurgu4K != null) BtnExportActiveKurgu4K.IsEnabled = true;
            }
        }

        private void BtnToggleDeleteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex < 0 || _selectedClipIndex >= _currentProject.Clips.Count) return;

            var activeClips = _currentProject.Clips.Where(c => !c.IsDeleted && c.SourceDurationSec >= 0.05).ToList();
            if (activeClips.Count <= 1)
            {
                TxtStatusBanner.Text = "⚠️ Tek kalan ana klip tamamen silinemez. İstemediğiniz bölümü önce 'Kes' (S) ile ayırıp silin veya '✂ Başı Kırp / Sonu Kırp' kullanın.";
                return;
            }

            PushUndoSnapshot();
            var removedClip = _currentProject.Clips[_selectedClipIndex];
            string removedRange = $"{FormatShortTime(removedClip.SourceStartSec)}–{FormatShortTime(removedClip.SourceEndSec)}";
            _currentProject.Clips.RemoveAt(_selectedClipIndex);
            RenumberActiveClips();

            _selectedClipIndex = Math.Clamp(_selectedClipIndex, 0, _currentProject.Clips.Count - 1);
            _playheadSec = _currentProject.Clips[_selectedClipIndex].SourceStartSec;
            if (CircleEditMediaPlayer != null && CircleEditMediaPlayer.Source != null)
            {
                try { CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec); } catch { }
            }

            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = $"🔗 Seçili parça ({removedRange}) silindi ve kalan parçalar boşluksuz otomatik birleştirildi! (Geri almak için Ctrl+Z)";
        }

        private void BtnSpeed8xSelectedClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex < 0 || _selectedClipIndex >= _currentProject.Clips.Count) return;
            PushUndoSnapshot();
            var clip = _currentProject.Clips[_selectedClipIndex];
            clip.IsDeleted = false;
            clip.SpeedMultiplier = Math.Abs(clip.SpeedMultiplier - 8.0) < 0.1 ? 1.0 : 8.0;
            UpdateSilenceSummaryText();
            RedrawTimeline();
        }

        private void BtnMoveClipLeft_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex <= 0 || _selectedClipIndex >= _currentProject.Clips.Count) return;
            PushUndoSnapshot();
            var c = _currentProject.Clips[_selectedClipIndex];
            _currentProject.Clips.RemoveAt(_selectedClipIndex);
            _selectedClipIndex--;
            _currentProject.Clips.Insert(_selectedClipIndex, c);
            RenumberActiveClips();
            RedrawTimeline();
            TxtStatusBanner.Text = $"◀ '{c.Label}' sıralamada sola taşındı ve manyetik birleştirildi.";
        }

        private void BtnMoveClipRight_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex < 0 || _selectedClipIndex >= _currentProject.Clips.Count - 1) return;
            PushUndoSnapshot();
            var c = _currentProject.Clips[_selectedClipIndex];
            _currentProject.Clips.RemoveAt(_selectedClipIndex);
            _selectedClipIndex++;
            _currentProject.Clips.Insert(_selectedClipIndex, c);
            RenumberActiveClips();
            RedrawTimeline();
            TxtStatusBanner.Text = $"▶ '{c.Label}' sıralamada sağa taşındı ve manyetik birleştirildi.";
        }

        private void BtnGrabEditorFrame_Click(object sender, RoutedEventArgs e)
        {
            string srcVideo = !string.IsNullOrWhiteSpace(_activeCircleEditVideoPath) && File.Exists(_activeCircleEditVideoPath)
                ? _activeCircleEditVideoPath
                : (_isCircleEditShowingVertical ? _currentProject?.Vertical9x16VideoPath : _currentProject?.Horizontal16x9VideoPath);

            if (string.IsNullOrWhiteSpace(srcVideo) || !File.Exists(srcVideo))
            {
                TxtStatusBanner.Text = "Kare yakalamak için önce bir video seçin.";
                return;
            }

            string targetDir = _isCircleEditShowingVertical
                ? HardwareEncoderService.GetVerticalRecordingsDir()
                : HardwareEncoderService.GetHorizontalRecordingsDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outPng = System.IO.Path.Combine(targetDir, $"Kare_{(_isCircleEditShowingVertical ? "9x16" : "16x9")}_{stamp}.png");

            if (TimelineRenderService.ExtractSingleFramePng(srcVideo, _playheadSec, outPng))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(outPng, UriKind.Absolute);
                    bmp.EndInit();
                    System.Windows.Clipboard.SetImage(bmp);
                }
                catch { }
                RefreshCircleEditFolderLists();
                TxtStatusBanner.Text = $"📸 {_playheadSec:0.00}. saniyedeki kare PNG olarak kaydedildi ve panoya kopyalandı: {System.IO.Path.GetFileName(outPng)}";
            }
        }

        private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

        private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            System.Windows.Point pt = e.GetPosition(TimelineCanvas);
            _trimDragMode = 0;

            const double headerW = 105;
            double trackAreaW = Math.Max(100, TimelineCanvas.ActualWidth - headerW - 10);
            var spans = GetMagneticTimelineSpans(headerW, trackAreaW, out double netTotalDur);

            // V1 katmanı (Y: 20..58) üzerinde bir klibin sol veya sağ Trim tutamacına mı tıklandı kontrol et
            if (pt.Y >= 20 && pt.Y <= 58 && spans.Count > 0)
            {
                // Önce seçili klibin tutamaçlarını kontrol et
                foreach (var sp in spans)
                {
                    if (sp.ClipIndex != _selectedClipIndex) continue;
                    if (Math.Abs(pt.X - sp.X1) <= 8)
                    {
                        PushUndoSnapshot();
                        _trimDragMode = 1;
                        _trimDragStartMouseX = pt.X;
                        _trimDragOrigSourceStart = sp.Clip.SourceStartSec;
                        _trimDragOrigSourceEnd = sp.Clip.SourceEndSec;
                        _trimDragInitialNetDur = netTotalDur;
                        break;
                    }
                    if (Math.Abs(pt.X - sp.X2) <= 8)
                    {
                        PushUndoSnapshot();
                        _trimDragMode = 2;
                        _trimDragStartMouseX = pt.X;
                        _trimDragOrigSourceStart = sp.Clip.SourceStartSec;
                        _trimDragOrigSourceEnd = sp.Clip.SourceEndSec;
                        _trimDragInitialNetDur = netTotalDur;
                        break;
                    }
                }
            }

            _isDraggingPlayhead = true;
            TimelineCanvas.CaptureMouse();
            if (_trimDragMode == 0)
                UpdatePlayheadFromMouse(pt.X);
        }

        private void TimelineCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingPlayhead) return;
            double mouseX = e.GetPosition(TimelineCanvas).X;

            if (_trimDragMode != 0 && _currentProject?.Clips != null &&
                _selectedClipIndex >= 0 && _selectedClipIndex < _currentProject.Clips.Count)
            {
                const double headerW = 105;
                double trackAreaW = Math.Max(100, TimelineCanvas.ActualWidth - headerW - 10);
                double rawTotalDur = Math.Max(1.0, _currentProject.TotalDurationSec);
                var sel = _currentProject.Clips[_selectedClipIndex];

                double deltaPx = mouseX - _trimDragStartMouseX;
                double deltaMagSec = (deltaPx / trackAreaW) * Math.Max(0.5, _trimDragInitialNetDur);
                double deltaSrcSec = deltaMagSec * Math.Max(0.1, sel.SpeedMultiplier);

                if (_trimDragMode == 1)
                {
                    double minAllowed = 0.0;
                    if (_selectedClipIndex > 0 && !_currentProject.Clips[_selectedClipIndex - 1].IsDeleted &&
                        _currentProject.Clips[_selectedClipIndex - 1].SourceEndSec <= _trimDragOrigSourceStart + 0.05)
                    {
                        minAllowed = _currentProject.Clips[_selectedClipIndex - 1].SourceEndSec;
                    }
                    sel.SourceStartSec = Math.Round(Math.Clamp(_trimDragOrigSourceStart + deltaSrcSec, minAllowed, Math.Max(minAllowed, sel.SourceEndSec - 0.25)), 2);
                    _playheadSec = sel.SourceStartSec;
                }
                else if (_trimDragMode == 2)
                {
                    double maxAllowed = rawTotalDur;
                    if (_selectedClipIndex + 1 < _currentProject.Clips.Count && !_currentProject.Clips[_selectedClipIndex + 1].IsDeleted &&
                        _currentProject.Clips[_selectedClipIndex + 1].SourceStartSec >= _trimDragOrigSourceEnd - 0.05)
                    {
                        maxAllowed = _currentProject.Clips[_selectedClipIndex + 1].SourceStartSec;
                    }
                    sel.SourceEndSec = Math.Round(Math.Clamp(_trimDragOrigSourceEnd + deltaSrcSec, sel.SourceStartSec + 0.25, maxAllowed), 2);
                    _playheadSec = sel.SourceEndSec;
                }

                if (CircleEditMediaPlayer != null && CircleEditMediaPlayer.Source != null)
                {
                    try { CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec); } catch { }
                }

                double curMag = SourceSecToMagneticSec(_playheadSec, _selectedClipIndex, out double netDur);
                TxtTimelineTimecode.Text = $"{FormatShortTime(curMag)} / {FormatShortTime(netDur)}";
                TxtStatusBanner.Text = $"✂️ Manyetik Kırpma ({sel.Label}): Kaynak {FormatShortTime(sel.SourceStartSec)} — {FormatShortTime(sel.SourceEndSec)} (Net Süre: {FormatShortTime(netDur)})";
                RedrawTimeline();
                return;
            }

            UpdatePlayheadFromMouse(mouseX);
        }

        private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                _isDraggingPlayhead = false;
                _trimDragMode = 0;
                TimelineCanvas.ReleaseMouseCapture();
            }
        }

        private void UpdatePlayheadFromMouse(double mouseX)
        {
            const double headerW = 105;
            double trackAreaW = Math.Max(100, TimelineCanvas.ActualWidth - headerW - 10);
            double norm = Math.Clamp((mouseX - headerW) / trackAreaW, 0.0, 1.0);

            GetMagneticTimelineSpans(headerW, trackAreaW, out double netTotalDur);
            double targetMagSec = norm * netTotalDur;
            _playheadSec = MagneticSecToSourceSec(targetMagSec, out int clickedClipIdx, out netTotalDur);
            _selectedClipIndex = clickedClipIdx;

            if (CircleEditMediaPlayer != null && CircleEditMediaPlayer.Source != null)
            {
                try
                {
                    CircleEditMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec);
                }
                catch { }
            }

            TxtTimelineTimecode.Text = $"{FormatShortTime(targetMagSec)} / {FormatShortTime(netTotalDur)}";
            UpdateLiveSubtitleOverlay();
            RedrawTimeline();
        }

        private static string FormatShortTime(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }

        private void RedrawTimeline()
        {
            if (TimelineCanvas == null || TimelineCanvas.ActualWidth < 50 || TimelineCanvas.ActualHeight < 40)
                return;

            // Varsa silinmiş (IsDeleted) eski klipleri otomatik temizle ki Timeline'da asla '[Silindi]' ölü kutusu veya siyah boşluk kalmasın!
            if (_currentProject?.Clips != null && _currentProject.Clips.Any(c => c.IsDeleted))
            {
                if (_currentProject.Clips.Count(c => !c.IsDeleted) >= 1)
                {
                    RenumberActiveClips();
                    _selectedClipIndex = Math.Clamp(_selectedClipIndex, 0, Math.Max(0, _currentProject.Clips.Count - 1));
                }
                else
                {
                    foreach (var c in _currentProject.Clips) c.IsDeleted = false;
                }
            }

            TimelineCanvas.Children.Clear();

            double canvasW = TimelineCanvas.ActualWidth;
            double canvasH = TimelineCanvas.ActualHeight;
            const double headerW = 105;
            double trackAreaW = Math.Max(100, canvasW - headerW - 10);
            double rawTotalDur = Math.Max(1.0, _currentProject?.TotalDurationSec ?? 10.0);

            var spans = GetMagneticTimelineSpans(headerW, trackAreaW, out double netTotalDur);

            // 4 Katman Yüksekliği: Ruler (20px), V1 (34px), V2 (26px), T1 (24px), A1 Waveform (Kalan)
            double[] rowTops = { 0, 22, 60, 90, 118 };
            double[] rowHeights = { 20, 34, 26, 24, Math.Max(36, canvasH - 122) };
            string v1Label = _isCircleEditShowingVertical ? "V1 • 9:16" : "V1 • 16:9";
            string[] rowLabels = { "ZAMAN", v1Label, "V2 • KADRAJ", "T1 • METİN", "A1 • SES" };

            for (int r = 0; r < rowLabels.Length; r++)
            {
                var bgRect = new System.Windows.Shapes.Rectangle
                {
                    Width = canvasW,
                    Height = rowHeights[r],
                    Fill = new SolidColorBrush(r % 2 == 0
                        ? System.Windows.Media.Color.FromRgb(18, 12, 49)
                        : System.Windows.Media.Color.FromRgb(23, 16, 66))
                };
                Canvas.SetLeft(bgRect, 0);
                Canvas.SetTop(bgRect, rowTops[r]);
                TimelineCanvas.Children.Add(bgRect);

                var lbl = new TextBlock
                {
                    Text = rowLabels[r],
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.White,
                    Opacity = 0.72
                };
                Canvas.SetLeft(lbl, 10);
                Canvas.SetTop(lbl, rowTops[r] + (rowHeights[r] - 14) / 2);
                TimelineCanvas.Children.Add(lbl);
            }

            // 1. Manyetik Zaman Cetveli (Ruler: 0.0s .. netTotalDur) & Marker Bayrakları
            int tickCount = 10;
            for (int i = 0; i <= tickCount; i++)
            {
                double t = (netTotalDur * i) / tickCount;
                double x = headerW + (trackAreaW * i) / tickCount;
                var tickTxt = new TextBlock
                {
                    Text = $"{t:0.0}s",
                    FontSize = 9.5,
                    Foreground = System.Windows.Media.Brushes.White,
                    Opacity = 0.65
                };
                Canvas.SetLeft(tickTxt, x - 10);
                Canvas.SetTop(tickTxt, 3);
                TimelineCanvas.Children.Add(tickTxt);
            }

            if (_currentProject?.Markers != null)
            {
                foreach (var m in _currentProject.Markers)
                {
                    double mMagSec = SourceSecToMagneticSec(m.TimeSec, -1, out _);
                    double mx = headerW + (mMagSec / netTotalDur) * trackAreaW;
                    var mFlag = new Border
                    {
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock { Text = m.Title, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White }
                    };
                    Canvas.SetLeft(mFlag, mx - 8);
                    Canvas.SetTop(mFlag, 1);
                    TimelineCanvas.Children.Add(mFlag);
                }
            }

            // Timeline Özeti (Net Birleşik Süre & Kazanılan Süre)
            if (TxtTimelineStats != null)
            {
                int activeCount = spans.Count;
                double cutSavedSec = Math.Max(0.0, rawTotalDur - netTotalDur);
                TxtTimelineStats.Text = (activeCount > 1 || cutSavedSec >= 0.4)
                    ? $"🔗 {activeCount} Parça Manyetik Birleşik  •  Net Çıktı: {FormatShortTime(netTotalDur)} (Ham: {FormatShortTime(rawTotalDur)} • {FormatShortTime(cutSavedSec)} kırpıldı)"
                    : $"Timeline • {activeCount} Parça • Net Süre: {FormatShortTime(netTotalDur)}";
            }

            // 2. V1 Katmanı: Manyetik Birleşik Video Klipleri (Sıfır Boşluk, Sıfır '[Silindi]' Kutusu)
            for (int sIdx = 0; sIdx < spans.Count; sIdx++)
            {
                var sp = spans[sIdx];
                var c = sp.Clip;
                int i = sp.ClipIndex;
                bool isSelected = (i == _selectedClipIndex);
                double x1 = sp.X1;
                double x2 = sp.X2;
                double w = Math.Max(6, x2 - x1 - 1);

                System.Windows.Media.Color fillCol = isSelected
                    ? System.Windows.Media.Color.FromRgb(239, 95, 24)
                    : (c.SpeedMultiplier > 1.2
                        ? System.Windows.Media.Color.FromRgb(186, 72, 18)
                        : System.Windows.Media.Color.FromRgb(38, 26, 102));

                string speedTag = Math.Abs(c.SpeedMultiplier - 1.0) > 0.05 ? $" • {c.SpeedMultiplier:0.#}x" : "";
                string clipText = w > 140
                    ? $"{c.Label} ({FormatShortTime(c.SourceStartSec)}–{FormatShortTime(c.SourceEndSec)}){speedTag}"
                    : $"{c.Label}{speedTag}";

                var bdr = new Border
                {
                    Width = w,
                    Height = rowHeights[1] - 4,
                    Background = new SolidColorBrush(fillCol),
                    BorderBrush = new SolidColorBrush(isSelected
                        ? System.Windows.Media.Color.FromRgb(255, 255, 255)
                        : System.Windows.Media.Color.FromRgb(59, 42, 148)),
                    BorderThickness = new Thickness(isSelected ? 1.8 : 1),
                    CornerRadius = new CornerRadius(4),
                    ClipToBounds = true,
                    ToolTip = $"{c.Label} • Kaynak: {FormatShortTime(c.SourceStartSec)} – {FormatShortTime(c.SourceEndSec)} (Süre: {FormatShortTime(c.OutputDurationSec)})",
                    Child = new TextBlock
                    {
                        Text = clipText,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(7, 6, 4, 0)
                    }
                };
                Canvas.SetLeft(bdr, x1);
                Canvas.SetTop(bdr, rowTops[1] + 2);
                TimelineCanvas.Children.Add(bdr);

                // Seçili klibin sol ve sağ uçlarında sürüklenebilir Beyaz/Turuncu Trim Tutamaçları
                if (isSelected && w >= 16)
                {
                    var leftHandle = new Border
                    {
                        Width = 5,
                        Height = rowHeights[1] - 4,
                        Background = System.Windows.Media.Brushes.White,
                        CornerRadius = new CornerRadius(3, 0, 0, 3),
                        ToolTip = "Klip başlangıcını manyetik kırpmak için sürükleyin"
                    };
                    Canvas.SetLeft(leftHandle, x1);
                    Canvas.SetTop(leftHandle, rowTops[1] + 2);
                    TimelineCanvas.Children.Add(leftHandle);

                    var rightHandle = new Border
                    {
                        Width = 5,
                        Height = rowHeights[1] - 4,
                        Background = System.Windows.Media.Brushes.White,
                        CornerRadius = new CornerRadius(0, 3, 3, 0),
                        ToolTip = "Klip bitişini manyetik kırpmak için sürükleyin"
                    };
                    Canvas.SetLeft(rightHandle, Math.Max(x1 + 5, x1 + w - 5));
                    Canvas.SetTop(rightHandle, rowTops[1] + 2);
                    TimelineCanvas.Children.Add(rightHandle);
                }

                // İki kesilmiş klip arasındaki Manyetik Birleşme Dikişi (🔗 Seam)
                if (sIdx > 0)
                {
                    var seamBadge = new Border
                    {
                        Width = 14,
                        Height = 14,
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                        BorderBrush = System.Windows.Media.Brushes.White,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(7),
                        ToolTip = $"Manyetik Birleşme Noktası ({FormatShortTime(sp.MagStartSec)})",
                        Child = new TextBlock
                        {
                            Text = "🔗",
                            FontSize = 7.5,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Canvas.SetLeft(seamBadge, x1 - 7);
                    Canvas.SetTop(seamBadge, rowTops[1] + (rowHeights[1] - 14) / 2);
                    TimelineCanvas.Children.Add(seamBadge);
                }
            }

            // 3. V2 Katmanı: Circle Kadraj & Tıklama Keyframe Elmasları
            var v2Bar = new Border
            {
                Width = trackAreaW,
                Height = rowHeights[2] - 6,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 37, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = $"{(_isCircleEditShowingVertical ? "9:16 Dikey Kadraj" : "16:9 Yatay Kadraj")} • {_currentProject.CameraKeyframes.Count} Keyframe • Uncrop %{_currentProject.UncropPercent:0}",
                    FontSize = 10,
                    Foreground = System.Windows.Media.Brushes.White,
                    Opacity = 0.85,
                    Margin = new Thickness(6, 3, 0, 0)
                }
            };
            Canvas.SetLeft(v2Bar, headerW);
            Canvas.SetTop(v2Bar, rowTops[2] + 3);
            TimelineCanvas.Children.Add(v2Bar);

            foreach (var kf in _currentProject.CameraKeyframes.Where(k => k.IsClickEvent))
            {
                double kMag = SourceSecToMagneticSec(kf.TimeSec, -1, out _);
                double kx = headerW + (kMag / netTotalDur) * trackAreaW;
                var diamond = new TextBlock
                {
                    Text = "◆",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24))
                };
                Canvas.SetLeft(diamond, kx - 5);
                Canvas.SetTop(diamond, rowTops[2] + 4);
                TimelineCanvas.Children.Add(diamond);
            }

            // 4. T1 Katmanı: Otomatik Altyazı Blokları (Manyetik Zaman Eşlemeli)
            foreach (var sub in _currentProject.Subtitles)
            {
                double sm1 = SourceSecToMagneticSec(sub.StartSec, -1, out _);
                double sm2 = SourceSecToMagneticSec(sub.EndSec, -1, out _);
                if (sm2 - sm1 < 0.08) continue;
                double sx1 = headerW + (sm1 / netTotalDur) * trackAreaW;
                double sx2 = headerW + (sm2 / netTotalDur) * trackAreaW;
                var subBdr = new Border
                {
                    Width = Math.Max(8, sx2 - sx1 - 2),
                    Height = rowHeights[3] - 4,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 239, 95, 24)),
                    CornerRadius = new CornerRadius(3),
                    ClipToBounds = true,
                    Child = new TextBlock
                    {
                        Text = sub.Text,
                        FontSize = 9.5,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(4, 2, 2, 0)
                    }
                };
                Canvas.SetLeft(subBdr, sx1);
                Canvas.SetTop(subBdr, rowTops[3] + 2);
                TimelineCanvas.Children.Add(subBdr);
            }

            // 5. A1 Katmanı: Her Klibin Kendi Manyetik Konumuna Göre Kesilmiş & Birleşmiş Ses Dalga Formu (Waveform)
            double wfTop = rowTops[4];
            double wfH = rowHeights[4];

            var peaks = _currentProject.WaveformPeaks;
            if (peaks != null && peaks.Count > 0 && spans.Count > 0)
            {
                foreach (var sp in spans)
                {
                    double clipPixelW = Math.Max(4.0, sp.X2 - sp.X1);
                    int barCount = Math.Max(1, (int)(clipPixelW / 3.0));
                    double barStep = clipPixelW / barCount;

                    int startPeakIdx = Math.Clamp((int)((sp.Clip.SourceStartSec / rawTotalDur) * peaks.Count), 0, peaks.Count - 1);
                    int endPeakIdx = Math.Clamp((int)((sp.Clip.SourceEndSec / rawTotalDur) * peaks.Count), startPeakIdx + 1, peaks.Count);
                    int peakSpan = Math.Max(1, endPeakIdx - startPeakIdx);

                    for (int b = 0; b < barCount; b++)
                    {
                        int srcIdx = Math.Clamp(startPeakIdx + (b * peakSpan) / barCount, 0, peaks.Count - 1);
                        float pk = Math.Clamp(peaks[srcIdx], 0.04f, 1.0f);
                        double bh = pk * (wfH - 6);
                        var line = new System.Windows.Shapes.Rectangle
                        {
                            Width = Math.Max(1.5, barStep - 1),
                            Height = bh,
                            Fill = new SolidColorBrush(pk < 0.08f
                                ? System.Windows.Media.Color.FromRgb(239, 95, 24)
                                : System.Windows.Media.Color.FromRgb(255, 255, 255))
                        };
                        Canvas.SetLeft(line, sp.X1 + b * barStep);
                        Canvas.SetTop(line, wfTop + (wfH - bh) / 2);
                        TimelineCanvas.Children.Add(line);
                    }
                }
            }

            // 6. Turuncu Manyetik Playhead Çizgisi
            double curMagSec = SourceSecToMagneticSec(_playheadSec, _selectedClipIndex, out _);
            double px = headerW + (curMagSec / netTotalDur) * trackAreaW;
            var playheadLine = new Line
            {
                X1 = px,
                Y1 = 0,
                X2 = px,
                Y2 = canvasH,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24)),
                StrokeThickness = 2
            };
            TimelineCanvas.Children.Add(playheadLine);
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
                return;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
                return;

            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && _activeStudioMode == "Edit")
            {
                BtnPlayerPlayPause_Click(null, null);
                e.Handled = true;
            }
            else if ((e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None) ||
                     (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control))
            {
                BtnSplitClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                BtnToggleDeleteClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BtnUndoEdit_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Left && _activeStudioMode == "Edit")
            {
                SeekCircleEditPlayer(Math.Max(0.0, _playheadSec - 1.0));
                e.Handled = true;
            }
            else if (e.Key == Key.Right && _activeStudioMode == "Edit")
            {
                SeekCircleEditPlayer(Math.Min(_currentProject?.TotalDurationSec ?? 10.0, _playheadSec + 1.0));
                e.Handled = true;
            }
            else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
            {
                BtnAddMarker_Click(null, null);
                e.Handled = true;
            }
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _uiTimer.Stop();
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    int[] allHotkeys =
                    {
                        HK_REC, HK_PAUSE, HK_ZOOM, HK_LOCK, HK_MARKER, HK_SCREENSHOT,
                        HK_REC_VERT, HK_MARKER_CSM, HK_SPLIT_PART, HK_SCREENSHOT_CSK,
                        HK_CAM_TOGGLE, HK_MIC_MUTE, HK_SYS_MUTE,
                        HK_SCENE_1, HK_SCENE_2, HK_SCENE_3, HK_SCENE_4, HK_INTERMISSION
                    };
                    foreach (int hk in allHotkeys)
                        UnregisterHotKey(_hwnd, hk);
                }
            }
            catch { }

            _meetServer.Dispose();
            _engine.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}


