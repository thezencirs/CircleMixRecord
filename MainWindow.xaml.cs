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
        private bool _isCapCutShowingVertical = false;
        private string _activeCapCutVideoPath = "";
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

        public MainWindow()
        {
            InitializeComponent();
            _meetServer = new CircleMeetServer(_engine);
            _meetServer.OnNewMessage += OnMeetMessageReceived;
            _meetServer.OnTunnelReady += pubUrl =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtMeetUrl.Text = pubUrl;
                    TxtStatusBanner.Text = $"Canlı Oda aktif • Oda Kodu: {_meetServer.RoomCode}";
                }));
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
            }
            catch { }

            TxtEncoderBadge.Text = "Circle GPU Ultra HQ";

            // Yatay ve Dikey klasörleri hazırla
            HardwareEncoderService.GetHorizontalRecordingsDir();
            HardwareEncoderService.GetVerticalRecordingsDir();
            HardwareEncoderService.GetEditedExportsDir();

            // Sıfır-Allocation WriteableBitmap Önizleme Tuvalini Hazırla (Donma / Duraksama = 0)
            _wb16x9 = new WriteableBitmap(1280, 720, 96, 96, PixelFormats.Bgr24, null);
            _wb9x16 = new WriteableBitmap(540, 960, 96, 96, PixelFormats.Bgr24, null);
            ImgPreview16x9.Source = _wb16x9;
            ImgPreview9x16.Source = _wb9x16;

            // Başlangıç boş Circle Edit kurgu projesini hazırla ve doğrudan Sade Stüdyo Tuvalini aç
            InitializeStarterTimelineProject();
            RefreshCapCutFolderLists();
            RegenerateSocialMediaMetadata();
            SetStudioViewMode("Record");

            // Dual-Canvas motorunu anında başlat, kameraları arka planda algılayıp bağla
            _engine.OnRawFramesReady += Engine_OnRawFramesReady;
            _engine.StartEngine(null);
            _uiTimer.Start();
            RedrawTimeline();

            // Circle Meet & Uzaktan Bağlantı Sunucusunu otomatik başlat ve 6 haneli kodu göster
            TxtMyRoomCode.Text = _meetServer.RoomCode;
            BtnWelcomeStartMeet.Content = $"Odamı Aç ({_meetServer.RoomCode})";
            string meetUrl = await _meetServer.StartServerAsync();
            TxtMeetUrl.Text = meetUrl;
            BtnToggleMeet.Content = "Odayı Kapat";
            BtnToggleMeet.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));

            await Task.Run(() =>
            {
                var (vids, auds) = HardwareEncoderService.EnumerateDirectShowDevices();
                _videoDevices = vids;
                _audioDevices = auds;
            });

            PopulateCameraCombo();
            var defaultCam = _videoDevices.FirstOrDefault();
            var fallbackCam = _videoDevices.FirstOrDefault(d => !d.IsNvidiaBroadcast && !d.IsVirtual) ?? _videoDevices.Skip(1).FirstOrDefault();
            if (defaultCam != null)
            {
                _engine.SwitchCamera(defaultCam, fallbackCam);
            }
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
                CmbCameras.SelectedIndex = 0; // #1 Varsayılan: ⭐ Camera (NVIDIA Broadcast)
            }

            if (CmbMicrophones != null)
            {
                CmbMicrophones.Items.Clear();
                if (_audioDevices.Count == 0)
                {
                    CmbMicrophones.Items.Add("⭐ Mikrofon (NVIDIA Broadcast) (Varsayılan)");
                    CmbMicrophones.SelectedIndex = 0;
                }
                else
                {
                    foreach (var adev in _audioDevices)
                    {
                        CmbMicrophones.Items.Add(adev);
                    }
                    CmbMicrophones.SelectedIndex = 0; // #1 Varsayılan: ⭐ Mikrofon (NVIDIA Broadcast)
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
            if (_activeStudioMode == "Edit") return; // CapCut Kurgu ekranındayken canlı önizleme çizimi yapma (0% CPU)

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

            // Circle Edit Video Oynatıcı Playhead Senkronizasyonu + Silinen Klipleri Otomatik Atlama + Canlı Timelapse Hızı
            if (_activeStudioMode == "Edit" && _isPlayerPlaying && CapCutMediaPlayer.Source != null && !_isDraggingPlayhead)
            {
                try
                {
                    double posSec = CapCutMediaPlayer.Position.TotalSeconds;
                    if (posSec >= 0)
                    {
                        _playheadSec = posSec;
                        if (_currentProject?.Clips != null && _currentProject.Clips.Count > 0)
                        {
                            for (int i = 0; i < _currentProject.Clips.Count; i++)
                            {
                                var c = _currentProject.Clips[i];
                                if (_playheadSec >= c.SourceStartSec && _playheadSec < c.SourceEndSec)
                                {
                                    _selectedClipIndex = i;
                                    // Eğer bu klip silindiyse (Ripple Delete / Del), anında bir sonraki sağlam klibe atla!
                                    if (c.IsDeleted)
                                    {
                                        double jumpTarget = c.SourceEndSec + 0.03;
                                        if (jumpTarget < _currentProject.TotalDurationSec)
                                        {
                                            CapCutMediaPlayer.Position = TimeSpan.FromSeconds(jumpTarget);
                                            _playheadSec = jumpTarget;
                                        }
                                        else
                                        {
                                            PauseCapCutPlayer();
                                        }
                                        break;
                                    }

                                    // Klip hızına göre (Timelapse / 8x Dead Air) gerçek zamanlı oynatma hızını ayarla
                                    double targetRatio = Math.Clamp(c.SpeedMultiplier, 0.25, 8.0);
                                    if (Math.Abs(CapCutMediaPlayer.SpeedRatio - targetRatio) > 0.05)
                                    {
                                        CapCutMediaPlayer.SpeedRatio = targetRatio;
                                    }
                                    break;
                                }
                            }
                        }

                        double totalDur = Math.Max(1.0, _currentProject?.TotalDurationSec ?? 10.0);
                        TxtTimelineTimecode.Text = $"{FormatShortTime(_playheadSec)} / {FormatShortTime(totalDur)}";
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

            // Circle Edit Kurgu ekranındayken arka plan ekran yakalamayı uyut (Bilgisayarı 0% yorar!)
            _engine.IsEditorFocusMode = isEdit;
            if (!isEdit && _isPlayerPlaying)
            {
                PauseCapCutPlayer();
            }

            OverlayWelcome.Visibility = mode == "Home" ? Visibility.Visible : Visibility.Collapsed;
            OverlaySocial.Visibility = mode == "Social" ? Visibility.Visible : Visibility.Collapsed;

            // Orta Ekran Geçişi: Edit modunda Kayıt Ekranı KAPANIR, Tam Circle Edit Kurgu Monitörü AÇILIR!
            if (BdrLiveRecordPreview != null)
                BdrLiveRecordPreview.Visibility = isEdit ? Visibility.Collapsed : Visibility.Visible;
            if (BdrCapCutCenterMonitor != null)
                BdrCapCutCenterMonitor.Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed;

            // Apple Segmented Control vurguları (Transparent pasif & #EF5F18 aktif)
            var idleBg = System.Windows.Media.Brushes.Transparent;
            var activeOrange = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));

            BtnModeHome.Background = mode == "Home" ? activeOrange : idleBg;
            BtnModeRecord.Background = (mode == "Record" || mode == "Meet") ? activeOrange : idleBg;
            BtnModeEdit.Background = mode == "Edit" ? activeOrange : idleBg;
            BtnModeSocial.Background = mode == "Social" ? activeOrange : idleBg;

            switch (mode)
            {
                case "Home":
                    SetPanelsVisibility(showLeftRecord: false, showLeftCapCutPool: false, showRight: false, showTimeline: false);
                    break;

                case "Record":
                    SetPanelsVisibility(showLeftRecord: pro, showLeftCapCutPool: false, showRight: pro, showTimeline: pro);
                    break;

                case "Meet":
                    RightTabControl.SelectedIndex = 3; // Yayın & Meet sekmesi
                    SetPanelsVisibility(showLeftRecord: pro, showLeftCapCutPool: false, showRight: true, showTimeline: pro);
                    break;

                case "Edit":
                    if (RightTabControl.SelectedIndex == 3)
                        RightTabControl.SelectedIndex = 1; // Sessizlik Sil / Kurgu sekmesi
                    RefreshCapCutFolderLists();
                    // Edit modunda Sol Panelde Açık Yatay & Dikey Klasörler (BdrCapCutMediaPool) her zaman açık!
                    SetPanelsVisibility(showLeftRecord: false, showLeftCapCutPool: true, showRight: pro, showTimeline: true);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RedrawTimeline));
                    break;

                case "Social":
                    RegenerateSocialMediaMetadata();
                    SetPanelsVisibility(showLeftRecord: false, showLeftCapCutPool: false, showRight: false, showTimeline: false);
                    break;
            }
        }

        private void SetPanelsVisibility(bool showLeftRecord, bool showLeftCapCutPool, bool showRight, bool showTimeline)
        {
            ColLeftPanel.Width = (showLeftRecord || showLeftCapCutPool) ? new GridLength(255) : new GridLength(0);
            BdrLeftPanel.Visibility = showLeftRecord ? Visibility.Visible : Visibility.Collapsed;
            if (BdrCapCutMediaPool != null)
                BdrCapCutMediaPool.Visibility = showLeftCapCutPool ? Visibility.Visible : Visibility.Collapsed;

            ColRightPanel.Width = showRight ? new GridLength(320) : new GridLength(0);
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
                string url = await _meetServer.StartServerAsync();
                TxtMeetUrl.Text = url;
                BtnToggleMeet.Content = "● Canlı Odayı Kapat";
                BtnToggleMeet.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            }

            ChkShowMeetTiles.IsChecked = true;
            _engine.ShowMeetingOverlay = true;
            SetStudioViewMode("Meet");

            try
            {
                System.Windows.Clipboard.SetText(TxtMeetUrl.Text);
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = TxtMeetUrl.Text,
                    UseShellExecute = true
                });
            }
            catch { }

            TxtStatusBanner.Text = $"Canlı Toplantı (Circle Meet) açıldı ve davet linki kopyalandı: {TxtMeetUrl.Text} — Kayda basarak toplantıyı 16:9 + 9:16 kaydedebilirsiniz!";
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
                    CmbPortraitLayout.SelectedIndex = 0;
                    break;
                case 1: // Canlı Toplantı (Circle Meet + Katılımcılar)
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = true;
                    ChkShowMeetTiles.IsChecked = true;
                    RightTabControl.SelectedIndex = 3;
                    break;
                case 2: // Tam Ekran 3D Modelleme
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = false;
                    CmbPortraitLayout.SelectedIndex = 0;
                    break;
                case 3: // Dikey Split (Üst Ekran + Alt Kamera)
                    ChkShowScreen.IsChecked = true;
                    ChkShowWebcam.IsChecked = true;
                    CmbPortraitLayout.SelectedIndex = 2;
                    break;
                case 4: // Sadece Eğitmen Kamerası
                    ChkShowScreen.IsChecked = false;
                    ChkShowWebcam.IsChecked = true;
                    SldCamScale.Value = 38;
                    break;
            }
        }

        private void CmbCameras_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            if (CmbCameras.SelectedItem is DirectShowDevice dev)
            {
                var fallback = _videoDevices.FirstOrDefault(d => !d.IsNvidiaBroadcast && !d.IsVirtual) ?? _videoDevices.Skip(1).FirstOrDefault();
                _engine.SwitchCamera(dev, fallback);
                TxtStatusBanner.Text = $"Kamera bağlandı: {dev.FriendlyName}";
            }
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            var (vids, auds) = HardwareEncoderService.EnumerateDirectShowDevices();
            _videoDevices = vids;
            _audioDevices = auds;
            PopulateCameraCombo();
            TxtStatusBanner.Text = $"{_videoDevices.Count} kamera ve {_audioDevices.Count} mikrofon algılandı (Varsayılan: NVIDIA Broadcast).";
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
                RefreshCapCutFolderLists();

                string fileToPreview = File.Exists(proj.Horizontal16x9VideoPath)
                    ? proj.Horizontal16x9VideoPath
                    : proj.Vertical9x16VideoPath;

                if (!string.IsNullOrWhiteSpace(fileToPreview) && File.Exists(fileToPreview))
                {
                    LoadVideoIntoCapCutEditor(fileToPreview, isVertical: !File.Exists(proj.Horizontal16x9VideoPath), preserveCurrentProject: true);
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

                var proj = _engine.StartDualRecording("DikeyReels", verticalOnly: true);
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
            RefreshCapCutFolderLists();
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

        private void BtnShowShortcuts_Click(object sender, RoutedEventArgs e)
        {
            string info =
                "CIRCLE MIX RECORD — KISAYOLLAR & HAKKINDA\n" +
                "by thezencirs (https://github.com/thezencirs)\n" +
                "MIT License — Copyright (c) 2026 thezencirs\n" +
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
            RefreshCapCutFolderLists();
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
                    "Circle Mix Record", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outFile = await TimelineRenderService.RenderNonDestructiveUncrop9x16Async(
                _currentProject,
                SldUncrop.Value,
                SldPanX.Value / 100.0,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(outFile) && File.Exists(outFile))
            {
                RefreshCapCutFolderLists();
                LoadVideoIntoCapCutEditor(outFile, isVertical: true, preserveCurrentProject: true);
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

        // ======================= SEKME 3: CAPCUT & INSTAGRAM EDITS KURGU, MEDYA, MASKELEME & ALTYAZI =======================

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
            _currentProject.ColorFilter = (ColorLutPreset)Math.Clamp(CmbColorFilter.SelectedIndex, 0, 3);
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
            RedrawTimeline();
            TxtStatusBanner.Text = $"{_currentProject.Subtitles.Count} adet konuşma senkronlu altyazı bloğu üretildi.";
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
                    "Circle Mix Record", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentProject.EnableNoiseSuppression = ChkAudioNoiseSuppress.IsChecked == true;
            _currentProject.EnableLufsNormalization = ChkAudioLufsNorm.IsChecked == true;

            var (out16, out9, outSrt) = await TimelineRenderService.ExportEditedSocialMediaPackageAsync(
                _currentProject,
                ChkRecord4K.IsChecked == true,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(out16)) _lastExported16x9Path = out16;
            if (!string.IsNullOrWhiteSpace(out9)) _lastExported9x16Path = out9;

            SetStudioViewMode("Social");

            System.Windows.MessageBox.Show(
                $"Sosyal Medya Kurgu Paketi Hazır!\n\n" +
                $"📺 YouTube (16:9): {out16}\n" +
                $"📱 Reels/Shorts (9:16): {out9}\n" +
                $"📝 Altyazı (.SRT): {outSrt ?? "Yok"}\n\n" +
                $"Şimdi sağdaki butonlardan tek tıkla YouTube, Instagram Reels, TikTok veya WhatsApp'a gönderebilirsiniz!",
                "Circle Mix Record — Sosyal Medyaya Hazır",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ======================= SOSYAL MEDYAYA SAL (PAYLAŞIM MERKEZİ) =======================

        private void RegenerateSocialMediaMetadata()
        {
            if (TxtSocialTitle == null || TxtSocialCaption == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🎓 Bu eğitim videosunda adım adım uygulamalı teknikleri, kısayolları ve profesyonel iş akışını inceledik.");
            sb.AppendLine();
            sb.AppendLine("⏱️ BÖLÜMLER (YOUTUBE CHAPTERS):");
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
            sb.AppendLine("🔥 #mimarimodel #içmimarlık #3bmodelleme #tasarım #eğitim #render #keşfet #reels #shorts");

            TxtSocialCaption.Text = sb.ToString();
        }

        private void BtnRegenerateSocialMeta_Click(object sender, RoutedEventArgs e)
        {
            RegenerateSocialMediaMetadata();
            TxtStatusBanner.Text = "Sosyal medya başlığı, YouTube bölüm zaman damgaları ve etiketler yenilendi.";
        }

        private void BtnCopySocialText_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string full = $"{TxtSocialTitle.Text}\n\n{TxtSocialCaption.Text}";
                System.Windows.Clipboard.SetText(full);
                TxtStatusBanner.Text = "Başlık, açıklama ve #hashtag'ler panoya kopyalandı (Ctrl+V ile yapıştırabilirsiniz).";
            }
            catch { }
        }

        private async void BtnShareYouTube_Click(object sender, RoutedEventArgs e)
        {
            await ShareToSocialPlatformAsync(
                platformName: "YouTube Studio (16:9 Yatay + Chapters)",
                uploadUrl: "https://studio.youtube.com/",
                preferVertical9x16: false);
        }

        private async void BtnShareInstagram_Click(object sender, RoutedEventArgs e)
        {
            await ShareToSocialPlatformAsync(
                platformName: "Instagram Reels (9:16 Dikey)",
                uploadUrl: "https://www.instagram.com/",
                preferVertical9x16: true);
        }

        private async void BtnShareTikTok_Click(object sender, RoutedEventArgs e)
        {
            await ShareToSocialPlatformAsync(
                platformName: "TikTok Studio (9:16 Dikey)",
                uploadUrl: "https://www.tiktok.com/tiktokstudio/upload",
                preferVertical9x16: true);
        }

        private async void BtnShareWhatsApp_Click(object sender, RoutedEventArgs e)
        {
            await ShareToSocialPlatformAsync(
                platformName: "WhatsApp Web / Masaüstü",
                uploadUrl: "https://web.whatsapp.com/",
                preferVertical9x16: true);
        }

        private async Task ShareToSocialPlatformAsync(string platformName, string uploadUrl, bool preferVertical9x16)
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

            // 4. Platformun yükleme sayfasını aç
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uploadUrl,
                    UseShellExecute = true
                });
            }
            catch { }

            TxtStatusBanner.Text = $"🚀 {platformName} açıldı! Başlık & etiketler panoya kopyalandı (Ctrl+V), video dosyası klasörde seçildi.";
        }

        // ======================= SEKME 4: CANLI YAYIN (RTMP), CIRCLE MEET & ANYDESK KODLA BAĞLANTI =======================

        private void BtnCopyRoomCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_meetServer.RoomCode);
                TxtStatusBanner.Text = $"🔑 6 Haneli Bağlantı Kodunuz panoya kopyalandı: {_meetServer.RoomCode} — Arkadaşınıza bu kodu gönderebilirsiniz!";
            }
            catch { }
        }

        private void ChkAllowRemoteControl_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            bool allow = ChkAllowRemoteControl.IsChecked == true;
            _meetServer.AllowRemoteControl = allow;
            TxtStatusBanner.Text = allow
                ? "🖱️ Circle Uzaktan Fare & Klavye Kontrolü AÇILDI. Bağlanan kişi ekranınızı yönetebilir."
                : "🔒 Circle Uzaktan Fare & Klavye Kontrolü KAPATILDI (Sadece izleme ve kameralı toplantı aktif).";
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
                    "Circle Mix Record — Kodla Bağlan", MessageBoxButton.OK, MessageBoxImage.Information);
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
                string url = await _meetServer.StartServerAsync();
                TxtMeetUrl.Text = url;
                BtnToggleMeet.Content = "● Canlı Odayı Kapat";
                BtnToggleMeet.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
                TxtStatusBanner.Text = $"Circle Meet & Uzaktan Bağlantı Odası aktif (Kod: {_meetServer.RoomCode}): {url}";
            }
            else
            {
                await _meetServer.StopServerAsync();
                BtnToggleMeet.Content = "● Canlı Odayı Aç";
                BtnToggleMeet.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
                TxtStatusBanner.Text = "Circle Meet Canlı Ders Sınıfı kapatıldı.";
            }
        }

        private void BtnOpenMeetBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = TxtMeetUrl.Text,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnCopyMeetLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(TxtMeetUrl.Text);
                TxtStatusBanner.Text = $"Canlı sınıf davet linki panoya kopyalandı: {TxtMeetUrl.Text}";
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

        private void BtnToggleRtmp_Click(object sender, RoutedEventArgs e)
        {
            if (!_engine.IsStreamingRtmp)
            {
                bool ok = _engine.StartRtmpBroadcast(TxtRtmpUrl.Text?.Trim());
                if (ok)
                {
                    BtnToggleRtmp.Content = "⏹️ RTMP Canlı Yayını Durdur";
                    TxtStatusBanner.Text = $"RTMP Canlı Yayın başlatıldı: {TxtRtmpUrl.Text}";
                }
                else
                {
                    TxtStatusBanner.Text = "RTMP sunucusuna bağlanılamadı. Lütfen yayın anahtarınızı kontrol edin.";
                }
            }
            else
            {
                _engine.StopRtmpBroadcast();
                BtnToggleRtmp.Content = "📡 RTMP Canlı Yayını Başlat";
                TxtStatusBanner.Text = "RTMP Canlı Yayın durduruldu.";
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

        private void RefreshCapCutFolderLists()
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

        private void BtnRefreshCapCutFolders_Click(object sender, RoutedEventArgs e)
        {
            RefreshCapCutFolderLists();
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

        private void BtnCapCutNewBlank_Click(object sender, RoutedEventArgs e)
        {
            PauseCapCutPlayer();
            CapCutMediaPlayer.Source = null;
            CapCutMediaPlayer.Visibility = Visibility.Collapsed;
            BdrCapCutSpeedBadge.Visibility = Visibility.Collapsed;
            BdrCapCutEmptyState.Visibility = Visibility.Visible;
            _activeCapCutVideoPath = "";
            _activeTimelapseSpeed = 1.0;
            _undoStack.Clear();

            InitializeStarterTimelineProject();
            TxtCapCutActiveFile.Text = "Video seçin veya sürükleyin";
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
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
            }
        }

        private void LstVertFolderFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstVertFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
            }
        }

        private void LstHorizFolderFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstHorizFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
                PlayCapCutPlayer();
            }
        }

        private void LstVertFolderFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstVertFolderFiles.SelectedItem is FolderMediaItem item && File.Exists(item.FilePath))
            {
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
                PlayCapCutPlayer();
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

        private void CapCutDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
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

        private void CapCutDropZone_Drop(object sender, System.Windows.DragEventArgs e)
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
                    if (string.IsNullOrWhiteSpace(_activeCapCutVideoPath))
                    {
                        LoadVideoIntoCapCutEditor(file, isVert, preserveCurrentProject: false);
                    }
                    else if (!string.Equals(_activeCapCutVideoPath, file, StringComparison.OrdinalIgnoreCase))
                    {
                        // Halihazırda açık bir video varsa hem oynatıcıya yükle hem de birleştirme listesine ekle
                        LoadVideoIntoCapCutEditor(file, isVert, preserveCurrentProject: false);
                    }
                }
            }
        }

        private void LoadVideoIntoCapCutEditor(string videoPath, bool isVertical, bool preserveCurrentProject)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return;

            _activeCapCutVideoPath = videoPath;
            _isCapCutShowingVertical = isVertical;
            TxtCapCutActiveFile.Text = $"{System.IO.Path.GetFileName(videoPath)} ({(isVertical ? "9:16 Dikey" : "16:9 Yatay")})";

            var activeBtnBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));
            var idleBtnBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));
            if (BtnCapCutView16x9 != null) BtnCapCutView16x9.Background = !isVertical ? activeBtnBg : idleBtnBg;
            if (BtnCapCutView9x16 != null) BtnCapCutView9x16.Background = isVertical ? activeBtnBg : idleBtnBg;

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
                        baseName.Replace("_Dikey_Reels_9x16", "_Yatay_16x9") + ".mp4");
                    if (File.Exists(hMatch)) horizCandidate = hMatch;
                }
                else
                {
                    string vMatch = System.IO.Path.Combine(
                        HardwareEncoderService.GetVerticalRecordingsDir(),
                        baseName.Replace("_Yatay_16x9", "_Dikey_Reels_9x16") + ".mp4");
                    if (File.Exists(vMatch)) vertCandidate = vMatch;
                }

                _currentProject = new CmrProject
                {
                    ProjectName = baseName.Replace("_Yatay_16x9", "").Replace("_Dikey_Reels_9x16", ""),
                    ProjectDirectory = HardwareEncoderService.GetBaseStorageDir(),
                    MasterCleanVideoPath = horizCandidate,
                    Horizontal16x9VideoPath = horizCandidate,
                    Vertical9x16VideoPath = vertCandidate,
                    TotalDurationSec = 15.0
                };
                _undoStack.Clear();
            }

            BdrCapCutEmptyState.Visibility = Visibility.Collapsed;
            CapCutMediaPlayer.Visibility = Visibility.Visible;
            BdrCapCutSpeedBadge.Visibility = Visibility.Visible;

            try
            {
                CapCutMediaPlayer.Stop();
                CapCutMediaPlayer.Source = new Uri(videoPath, UriKind.Absolute);
                CapCutMediaPlayer.SpeedRatio = Math.Clamp(_activeTimelapseSpeed, 0.25, 8.0);
                CapCutMediaPlayer.Play();
                _isPlayerPlaying = true;
                UpdatePlayPauseButtonLabels();
            }
            catch { }

            TxtStatusBanner.Text = $"🎬 Circle Edit Kurguya Yüklendi: {System.IO.Path.GetFileName(videoPath)} — Space ile Oynat/Duraklat, S ile Kes, Del ile Sil, 2x-30x ile Timelapse yapın.";
        }

        private void CapCutMediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (CapCutMediaPlayer.NaturalDuration.HasTimeSpan)
            {
                double realDur = Math.Max(0.5, CapCutMediaPlayer.NaturalDuration.TimeSpan.TotalSeconds);
                _currentProject.TotalDurationSec = realDur;

                if (_currentProject.Clips.Count == 0 ||
                    (_currentProject.Clips.Count == 1 && Math.Abs(_currentProject.Clips[0].SourceEndSec - realDur) > 0.5))
                {
                    _currentProject.Clips.Clear();
                    _currentProject.Clips.Add(new TimelineClip
                    {
                        Label = System.IO.Path.GetFileNameWithoutExtension(_activeCapCutVideoPath),
                        SourceStartSec = 0.0,
                        SourceEndSec = Math.Round(realDur, 2),
                        SpeedMultiplier = _activeTimelapseSpeed,
                        IsDeleted = false,
                        IsSilentSegment = false
                    });

                    // Gerçekçi ses dalga formu oluştur (eğer harici MP4 yüklendiyse)
                    if (_currentProject.WaveformPeaks == null || _currentProject.WaveformPeaks.Count == 0)
                    {
                        _currentProject.WaveformPeaks = new List<float>();
                        var rng = new Random(base.GetHashCode());
                        for (int i = 0; i < 480; i++)
                        {
                            _currentProject.WaveformPeaks.Add((float)(0.18 + rng.NextDouble() * 0.65));
                        }
                    }
                }

                _playheadSec = 0.0;
                _selectedClipIndex = 0;
                TxtTimelineTimecode.Text = $"{FormatShortTime(0)} / {FormatShortTime(realDur)}";
                RedrawTimeline();
            }
        }

        private void CapCutMediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            CapCutMediaPlayer.Position = TimeSpan.Zero;
            _playheadSec = 0.0;
            PauseCapCutPlayer();
            RedrawTimeline();
        }

        private void CapCutMediaPlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BtnPlayerPlayPause_Click(sender, e);
        }

        private void PlayCapCutPlayer()
        {
            if (CapCutMediaPlayer.Source == null) return;
            try
            {
                CapCutMediaPlayer.Play();
                _isPlayerPlaying = true;
                UpdatePlayPauseButtonLabels();
            }
            catch { }
        }

        private void PauseCapCutPlayer()
        {
            try
            {
                CapCutMediaPlayer.Pause();
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
            if (CapCutMediaPlayer.Source == null)
            {
                // Oynatıcı boşsa Yatay veya Dikey klasördeki en son videoyu otomatik yükle
                if (LstHorizFolderFiles.Items.Count > 0 && LstHorizFolderFiles.Items[0] is FolderMediaItem hItem)
                {
                    LoadVideoIntoCapCutEditor(hItem.FilePath, isVertical: false, preserveCurrentProject: false);
                    return;
                }
                if (LstVertFolderFiles.Items.Count > 0 && LstVertFolderFiles.Items[0] is FolderMediaItem vItem)
                {
                    LoadVideoIntoCapCutEditor(vItem.FilePath, isVertical: true, preserveCurrentProject: false);
                    return;
                }
                TxtStatusBanner.Text = "Soldaki medya listesinden bir video seçin veya yeni kayıt alın";
                return;
            }

            if (_isPlayerPlaying)
                PauseCapCutPlayer();
            else
                PlayCapCutPlayer();
        }

        private void BtnPlayerStart_Click(object sender, RoutedEventArgs e)
        {
            SeekCapCutPlayer(0.0);
        }

        private void BtnPlayerBack_Click(object sender, RoutedEventArgs e)
        {
            SeekCapCutPlayer(Math.Max(0.0, _playheadSec - 3.0));
        }

        private void BtnPlayerForward_Click(object sender, RoutedEventArgs e)
        {
            double maxDur = Math.Max(1.0, _currentProject?.TotalDurationSec ?? 10.0);
            SeekCapCutPlayer(Math.Min(maxDur, _playheadSec + 3.0));
        }

        private void SeekCapCutPlayer(double targetSec)
        {
            _playheadSec = Math.Max(0.0, targetSec);
            if (CapCutMediaPlayer.Source != null)
            {
                try
                {
                    CapCutMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec);
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
            CapCutMediaPlayer.IsMuted = _isPlayerMuted;
            if (BtnTogglePlayerMute != null)
            {
                BtnTogglePlayerMute.Content = _isPlayerMuted ? "Ses: Kapalı" : "Ses";
            }
        }

        private void BtnCapCutSwitch16x9_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentProject?.Horizontal16x9VideoPath) && File.Exists(_currentProject.Horizontal16x9VideoPath))
            {
                LoadVideoIntoCapCutEditor(_currentProject.Horizontal16x9VideoPath, isVertical: false, preserveCurrentProject: true);
            }
            else if (LstHorizFolderFiles.Items.Count > 0 && LstHorizFolderFiles.Items[0] is FolderMediaItem item)
            {
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: false, preserveCurrentProject: false);
            }
        }

        private void BtnCapCutSwitch9x16_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentProject?.Vertical9x16VideoPath) && File.Exists(_currentProject.Vertical9x16VideoPath))
            {
                LoadVideoIntoCapCutEditor(_currentProject.Vertical9x16VideoPath, isVertical: true, preserveCurrentProject: true);
            }
            else if (LstVertFolderFiles.Items.Count > 0 && LstVertFolderFiles.Items[0] is FolderMediaItem item)
            {
                LoadVideoIntoCapCutEditor(item.FilePath, isVertical: true, preserveCurrentProject: false);
            }
        }

        // ======================= TIMELAPSE (HIZLANDIRMA: 0.5x - 30x) & 60 FPS TIMELAPSE KAYDET =======================

        private void ApplyTimelapseSpeed(double speed)
        {
            PushUndoSnapshot();
            _activeTimelapseSpeed = speed;

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

            if (CapCutMediaPlayer.Source != null)
            {
                try
                {
                    CapCutMediaPlayer.SpeedRatio = Math.Clamp(speed, 0.25, 8.0);
                }
                catch { }
            }

            if (TxtCapCutSpeedBadge != null)
            {
                TxtCapCutSpeedBadge.Text = $"{speed:0.#}x";
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
            string srcVideo = !string.IsNullOrWhiteSpace(_activeCapCutVideoPath) && File.Exists(_activeCapCutVideoPath)
                ? _activeCapCutVideoPath
                : (_isCapCutShowingVertical ? _currentProject?.Vertical9x16VideoPath : _currentProject?.Horizontal16x9VideoPath);

            if (string.IsNullOrWhiteSpace(srcVideo) || !File.Exists(srcVideo))
            {
                System.Windows.MessageBox.Show(
                    "Önce soldaki Yatay veya Dikey klasörden bir video seçin veya yeni bir kayıt alın.",
                    "Timelapse Üretici", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double spd = _activeTimelapseSpeed <= 1.05 ? 4.0 : _activeTimelapseSpeed;
            PauseCapCutPlayer();

            string outFile = await TimelineRenderService.RenderInstantTimelapseAsync(
                srcVideo,
                _isCapCutShowingVertical,
                spd,
                muteAudio: _isPlayerMuted || spd >= 8.0,
                msg => Dispatcher.Invoke(() => TxtStatusBanner.Text = msg));

            if (!string.IsNullOrWhiteSpace(outFile) && File.Exists(outFile))
            {
                RefreshCapCutFolderLists();
                LoadVideoIntoCapCutEditor(outFile, _isCapCutShowingVertical, preserveCurrentProject: false);
                _activeTimelapseSpeed = 1.0;
                TxtStatusBanner.Text = $"✅ 60 FPS Timelapse ({spd:0.#}x) videosu üretildi ve klasöre eklendi: {System.IO.Path.GetFileName(outFile)}";
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
                Title = "Circle Mix Record Projesi (.cmrproj) veya Video (.mp4/.mkv) Seçin",
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
                        LoadVideoIntoCapCutEditor(vPath, isVertical: false, preserveCurrentProject: true);
                }
                else
                {
                    bool isVert = dlg.FileName.Contains("Dikey", StringComparison.OrdinalIgnoreCase) || dlg.FileName.Contains("9x16", StringComparison.OrdinalIgnoreCase);
                    LoadVideoIntoCapCutEditor(dlg.FileName, isVert, preserveCurrentProject: false);
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

        // ======================= ÇOK KATMANLI TIMELINE VE SES DALGA FORMU =======================

        private void BtnSplitClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _currentProject.Clips.Count == 0) return;

            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                if (_playheadSec > c.SourceStartSec + 0.15 && _playheadSec < c.SourceEndSec - 0.15)
                {
                    PushUndoSnapshot();
                    double origEnd = c.SourceEndSec;
                    c.SourceEndSec = Math.Round(_playheadSec, 2);

                    var secondHalf = new TimelineClip
                    {
                        Label = c.Label + " (Parça 2)",
                        SourceStartSec = Math.Round(_playheadSec, 2),
                        SourceEndSec = origEnd,
                        SpeedMultiplier = c.SpeedMultiplier,
                        IsDeleted = c.IsDeleted,
                        IsSilentSegment = c.IsSilentSegment
                    };
                    _currentProject.Clips.Insert(i + 1, secondHalf);
                    _selectedClipIndex = i + 1;
                    TxtStatusBanner.Text = $"✂️ Klip {_playheadSec:0.00}. saniyeden ikiye bölündü (Silmek için Del tuşuna basın).";
                    RedrawTimeline();
                    return;
                }
            }
            TxtStatusBanner.Text = "✂️ Bölmek için kırmızı çizgiyi (Playhead) klibin ortasında bir noktaya getirin ve S tuşuna basın.";
        }

        private void BtnToggleDeleteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject?.Clips == null || _selectedClipIndex < 0 || _selectedClipIndex >= _currentProject.Clips.Count) return;
            PushUndoSnapshot();
            var clip = _currentProject.Clips[_selectedClipIndex];
            clip.IsDeleted = !clip.IsDeleted;
            UpdateSilenceSummaryText();
            RedrawTimeline();
            TxtStatusBanner.Text = clip.IsDeleted
                ? $"🗑️ '{clip.Label}' silindi (Oynatırken ve çıktıda otomatik atlanır, Ctrl+Z ile geri alınabilir)."
                : $"↩️ '{clip.Label}' geri getirildi.";
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

        private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

        private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = true;
            TimelineCanvas.CaptureMouse();
            UpdatePlayheadFromMouse(e.GetPosition(TimelineCanvas).X);
        }

        private void TimelineCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                UpdatePlayheadFromMouse(e.GetPosition(TimelineCanvas).X);
            }
        }

        private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                _isDraggingPlayhead = false;
                TimelineCanvas.ReleaseMouseCapture();
            }
        }

        private void UpdatePlayheadFromMouse(double mouseX)
        {
            double w = Math.Max(100, TimelineCanvas.ActualWidth - 110);
            double totalDur = Math.Max(1.0, _currentProject.TotalDurationSec);
            double norm = Math.Clamp((mouseX - 105) / w, 0.0, 1.0);
            _playheadSec = norm * totalDur;

            // Tıklanan yerdeki klibi seç
            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                if (_playheadSec >= c.SourceStartSec && _playheadSec <= c.SourceEndSec)
                {
                    _selectedClipIndex = i;
                    break;
                }
            }

            if (CapCutMediaPlayer != null && CapCutMediaPlayer.Source != null)
            {
                try
                {
                    CapCutMediaPlayer.Position = TimeSpan.FromSeconds(_playheadSec);
                }
                catch { }
            }

            TxtTimelineTimecode.Text = $"{FormatShortTime(_playheadSec)} / {FormatShortTime(totalDur)}";
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

            TimelineCanvas.Children.Clear();

            double canvasW = TimelineCanvas.ActualWidth;
            double canvasH = TimelineCanvas.ActualHeight;
            const double headerW = 105;
            double trackAreaW = Math.Max(100, canvasW - headerW - 10);
            double totalDur = Math.Max(1.0, _currentProject.TotalDurationSec);

            // 4 Katman Yüksekliği: Ruler (20px), V1 (34px), V2 (26px), T1 (24px), A1 Waveform (Kalan)
            double[] rowTops = { 0, 22, 60, 90, 118 };
            double[] rowHeights = { 20, 34, 26, 24, Math.Max(36, canvasH - 122) };
            string[] rowLabels = { "ZAMAN", "V1 • 16:9", "V2 • 9:16", "T1 • METİN", "A1 • SES" };

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

            // 1. Zaman Cetveli (Ruler) & Marker Bayrakları
            int tickCount = 10;
            for (int i = 0; i <= tickCount; i++)
            {
                double t = (totalDur * i) / tickCount;
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

            foreach (var m in _currentProject.Markers)
            {
                double mx = headerW + (m.TimeSec / totalDur) * trackAreaW;
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

            // 2. V1 Katmanı: Video Klipleri (Konuşma / Timelapse Hızlı / Kesilen Sessizlik)
            for (int i = 0; i < _currentProject.Clips.Count; i++)
            {
                var c = _currentProject.Clips[i];
                double x1 = headerW + (c.SourceStartSec / totalDur) * trackAreaW;
                double x2 = headerW + (c.SourceEndSec / totalDur) * trackAreaW;
                double w = Math.Max(4, x2 - x1 - 2);

                System.Windows.Media.Color fillCol = c.IsDeleted
                    ? System.Windows.Media.Color.FromArgb(110, 38, 26, 102)
                    : (c.SpeedMultiplier > 1.2
                        ? System.Windows.Media.Color.FromRgb(239, 95, 24)
                        : System.Windows.Media.Color.FromRgb(38, 26, 102));

                var bdr = new Border
                {
                    Width = w,
                    Height = rowHeights[1] - 4,
                    Background = new SolidColorBrush(fillCol),
                    BorderBrush = new SolidColorBrush(i == _selectedClipIndex
                        ? System.Windows.Media.Color.FromRgb(255, 255, 255)
                        : System.Windows.Media.Color.FromRgb(52, 37, 128)),
                    BorderThickness = new Thickness(i == _selectedClipIndex ? 1.5 : 1),
                    CornerRadius = new CornerRadius(4),
                    ClipToBounds = true,
                    Child = new TextBlock
                    {
                        Text = c.IsDeleted ? $"[Silindi] {c.Label}" : $"{c.Label} • {c.SpeedMultiplier:0.#}x",
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(6, 6, 2, 0)
                    }
                };
                Canvas.SetLeft(bdr, x1);
                Canvas.SetTop(bdr, rowTops[1] + 2);
                TimelineCanvas.Children.Add(bdr);
            }

            // 3. V2 Katmanı: Circle Portrait 9:16 Odak & Tıklama Keyframe Elmasları
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
                    Text = $"9:16 Odak • {_currentProject.CameraKeyframes.Count} Keyframe • Uncrop %{_currentProject.UncropPercent:0}",
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
                double kx = headerW + (kf.TimeSec / totalDur) * trackAreaW;
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

            // 4. T1 Katmanı: Otomatik Altyazı Blokları
            foreach (var sub in _currentProject.Subtitles)
            {
                double sx1 = headerW + (sub.StartSec / totalDur) * trackAreaW;
                double sx2 = headerW + (sub.EndSec / totalDur) * trackAreaW;
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

            // 5. A1 Katmanı: Mikrofon Ses Dalga Formu (Waveform) + Turuncu Sessizlik Bölgeleri
            double wfTop = rowTops[4];
            double wfH = rowHeights[4];

            foreach (var sil in _currentProject.DetectedSilences)
            {
                double sx1 = headerW + (sil.StartSec / totalDur) * trackAreaW;
                double sx2 = headerW + (sil.EndSec / totalDur) * trackAreaW;
                var silRect = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, sx2 - sx1),
                    Height = wfH,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 239, 95, 24))
                };
                Canvas.SetLeft(silRect, sx1);
                Canvas.SetTop(silRect, wfTop);
                TimelineCanvas.Children.Add(silRect);
            }

            var peaks = _currentProject.WaveformPeaks;
            if (peaks != null && peaks.Count > 0)
            {
                int barCount = Math.Min(peaks.Count, (int)(trackAreaW / 3));
                double barStep = trackAreaW / Math.Max(1, barCount);
                for (int i = 0; i < barCount; i++)
                {
                    int srcIdx = (i * peaks.Count) / barCount;
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
                    Canvas.SetLeft(line, headerW + i * barStep);
                    Canvas.SetTop(line, wfTop + (wfH - bh) / 2);
                    TimelineCanvas.Children.Add(line);
                }
            }

            // 6. Turuncu Playhead Çizgisi
            double px = headerW + (_playheadSec / totalDur) * trackAreaW;
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
                SeekCapCutPlayer(Math.Max(0.0, _playheadSec - 1.0));
                e.Handled = true;
            }
            else if (e.Key == Key.Right && _activeStudioMode == "Edit")
            {
                SeekCapCutPlayer(Math.Min(_currentProject?.TotalDurationSec ?? 10.0, _playheadSec + 1.0));
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
                        HK_SCENE_1, HK_SCENE_2, HK_SCENE_3, HK_SCENE_4
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
