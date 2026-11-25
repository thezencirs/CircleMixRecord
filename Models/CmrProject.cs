// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleMixRecord.Models
{
    public enum SmartPortraitLayout
    {
        FullFill9x16 = 0,          // Tam Dikey 9:16 (İmleç & Odak Takipli Kırpma)
        UncropBlur16x9 = 1,        // Kayıpsız Uncrop (Sinematik Blur Arkaplan Üzerinde Yatay Ekran)
        SplitScreenCamBottom = 2   // Üstte Eğitim Ekranı + Altta Büyük Eğitmen Kamerası
    }

    public enum SilenceActionMode
    {
        KeepNormal = 0,
        RippleDelete = 1,          // Sessizliği Kes ve Birleştir
        SpeedUpDeadAir = 2         // 3B Modelleme Modu: Silmeden 8x Hızlandır
    }

    public enum OverlayMaskShape
    {
        RoundedBox = 0,            // Yuvarlatılmış Çerçeve
        CircleMask = 1,            // Daire Maske (Circle Studio Stil)
        ChromaKeyGreen = 2,        // Yeşil Perde Silici (Chroma Key)
        FullScreenWatermark = 3    // Yarı Saydam Tam Ekran / Filigran Bindirme
    }

    public enum ColorLutPreset
    {
        NaturalStudio = 0,         // 00 • Doğal Stüdyo
        LogToRec709 = 1,           // 01 • Kamera Log ➔ Rec.709 Düzeltme
        AmberWarm = 2,             // 02 • Amber Sıcak Altın
        BlueHour = 3,              // 03 • Blue Hour (Mavi Saat)
        CrispStudio = 4,           // 04 • Crisp (Kristal Netlik)
        EuphoriaCinema = 5,        // 05 • Euphoria (Sinematik Kontrast)
        FrostyCool = 6,            // 06 • Frosty (Soğuk Stüdyo)
        GlowSoft = 7,              // 07 • Glow (Yumuşak Işıltı)
        IndigoDeep = 8,            // 08 • Indigo (Derin Gece)
        JadeEmerald = 9,           // 09 • Jade (Zümrüt Ton)
        RedVelvet = 10,            // 10 • Red Velvet (Kadife Sıcak)
        VibrantPop = 11,           // 11 • Vibrant (Canlı Renkler)
        VintageWarm = 12,          // 12 • Vintage (Nostaljik Film)
        GreyscaleNoir = 13,        // 13 • Noir (Siyah Beyaz)
        CrispCad3D = 14            // 14 • 3B CAD/BIM Keskin Çizgi
    }

    public enum SpeedCurvePreset
    {
        Constant = 0,              // Sabit Hız (0.5x .. 30x)
        Montage = 1,               // Montage (0.9x ➔ 6.8x ➔ 0.3x ➔ 1.0x)
        Hero = 2,                  // Hero (1.0x ➔ 5.5x ➔ 0.5x ➔ 5.5x ➔ 1.0x)
        Bullet = 3,                // Bullet (5.2x ➔ 0.5x ➔ 5.2x)
        JumpCut = 4,               // Jump Cut (0.6x ➔ 6.0x ➔ 0.6x)
        FlashIn = 5,               // Flash In (5.2x ➔ 1.0x)
        FlashOut = 6,              // Flash Out (1.0x ➔ 5.2x)
        Ramp = 7,                  // Ramp (5.0x ➔ 1.0x ➔ 5.0x)
        Strobe = 8                 // Strobe (1.0x ⇄ 5.0x Ritim)
    }

    public enum ClipTransitionPreset
    {
        Fade = 0,                  // Yumuşak Geçiş (Dissolve)
        ZoomIn = 1,                // Hyperwarp Zoom In
        SlideLeft = 2,             // Sola Kaydır (Slide Left)
        SlideRight = 3,            // Sağa Kaydır (Slide Right)
        SlideUp = 4,               // Yukarı Kaydır (Slide Up)
        SlideDown = 5,             // Aşağı Kaydır (Slide Down)
        CircleOpen = 6,            // Daire Açılma (Circle In)
        CircleClose = 7,           // Daire Kapanma (Circle Out)
        RadialClock = 8,           // Saat Yönü (Clock Wipe)
        DiagTlBr = 9,              // Çapraz Silme (Diagonal Wipe)
        FadeWhite = 10,            // Işık Patlaması (Flash In-Out)
        HBlur = 11                 // Bulanık Geçiş (Stretch Blur)
    }

    public enum CaptionStylePreset
    {
        ClassicBox = 0,            // Klasik Stüdyo Kutu (#140E38)
        OrangeKaraoke = 1,         // Turuncu Vurgu (#EF5F18 Karaoke)
        MinimalCinema = 2,         // Minimal Sinema (Kutusuz)
        TopBanner = 3              // Üst Başlık Bandı (#261A66)
    }

    public sealed class SpeedCurvePoint
    {
        public double NormalizedProgress { get; set; } // 0 .. 100
        public double Speed { get; set; } = 1.0;       // 0.25 .. 10.0
    }

    public static class SpeedCurveCatalog
    {
        public static List<SpeedCurvePoint> GetPresetPoints(SpeedCurvePreset preset, double constantSpeed = 1.0)
        {
            return preset switch
            {
                SpeedCurvePreset.Montage => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 0.9 },
                    new() { NormalizedProgress = 10, Speed = 0.9 },
                    new() { NormalizedProgress = 50, Speed = 6.8 },
                    new() { NormalizedProgress = 65, Speed = 0.3 },
                    new() { NormalizedProgress = 80, Speed = 1.0 },
                    new() { NormalizedProgress = 100, Speed = 1.0 }
                },
                SpeedCurvePreset.Hero => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 1.0 },
                    new() { NormalizedProgress = 6, Speed = 1.0 },
                    new() { NormalizedProgress = 39, Speed = 5.5 },
                    new() { NormalizedProgress = 46, Speed = 0.5 },
                    new() { NormalizedProgress = 56, Speed = 0.5 },
                    new() { NormalizedProgress = 66, Speed = 5.5 },
                    new() { NormalizedProgress = 97, Speed = 1.0 },
                    new() { NormalizedProgress = 100, Speed = 1.0 }
                },
                SpeedCurvePreset.Bullet => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 5.2 },
                    new() { NormalizedProgress = 39, Speed = 5.2 },
                    new() { NormalizedProgress = 47, Speed = 0.5 },
                    new() { NormalizedProgress = 53, Speed = 0.5 },
                    new() { NormalizedProgress = 61, Speed = 5.2 },
                    new() { NormalizedProgress = 100, Speed = 5.2 }
                },
                SpeedCurvePreset.JumpCut => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 0.6 },
                    new() { NormalizedProgress = 43, Speed = 0.6 },
                    new() { NormalizedProgress = 50, Speed = 6.0 },
                    new() { NormalizedProgress = 57, Speed = 0.6 },
                    new() { NormalizedProgress = 100, Speed = 0.6 }
                },
                SpeedCurvePreset.FlashIn => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 5.2 },
                    new() { NormalizedProgress = 40, Speed = 5.2 },
                    new() { NormalizedProgress = 60, Speed = 1.0 },
                    new() { NormalizedProgress = 100, Speed = 1.0 }
                },
                SpeedCurvePreset.FlashOut => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 1.0 },
                    new() { NormalizedProgress = 40, Speed = 1.0 },
                    new() { NormalizedProgress = 60, Speed = 5.2 },
                    new() { NormalizedProgress = 100, Speed = 5.2 }
                },
                SpeedCurvePreset.Ramp => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 5.0 },
                    new() { NormalizedProgress = 20, Speed = 5.0 },
                    new() { NormalizedProgress = 25, Speed = 1.0 },
                    new() { NormalizedProgress = 60, Speed = 1.0 },
                    new() { NormalizedProgress = 65, Speed = 5.0 },
                    new() { NormalizedProgress = 100, Speed = 5.0 }
                },
                SpeedCurvePreset.Strobe => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = 1.0 },
                    new() { NormalizedProgress = 16.7, Speed = 5.0 },
                    new() { NormalizedProgress = 33.3, Speed = 1.0 },
                    new() { NormalizedProgress = 50, Speed = 5.0 },
                    new() { NormalizedProgress = 66.7, Speed = 1.0 },
                    new() { NormalizedProgress = 83.3, Speed = 5.0 },
                    new() { NormalizedProgress = 100, Speed = 1.0 }
                },
                _ => new List<SpeedCurvePoint>
                {
                    new() { NormalizedProgress = 0, Speed = constantSpeed },
                    new() { NormalizedProgress = 100, Speed = constantSpeed }
                }
            };
        }
    }

    public class CameraKeyframe
    {
        public double TimeSec { get; set; }
        public double NormX { get; set; } = 0.5;   // 0.0 .. 1.0 (Master tuval üzerinde odak merkezi X)
        public double NormY { get; set; } = 0.5;   // 0.0 .. 1.0 (Master tuval üzerinde odak merkezi Y)
        public double Zoom { get; set; } = 1.0;    // 1.0 .. 3.5
        public bool IsClickEvent { get; set; }
        public string ActiveWindowTitle { get; set; } = "";
    }

    public class SilenceRegion
    {
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public double DurationSec => Math.Max(0, EndSec - StartSec);
        public double AvgDb { get; set; } = -55.0;
        public bool IsSilent { get; set; } = true;
    }

    public class TimelineClip
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Label { get; set; } = "Klip";
        public double SourceStartSec { get; set; }
        public double SourceEndSec { get; set; }
        public double SourceDurationSec => Math.Max(0.01, SourceEndSec - SourceStartSec);
        public double SpeedMultiplier { get; set; } = 1.0;
        public bool IsDeleted { get; set; } = false;
        public bool IsSilentSegment { get; set; } = false;
        public double UncropOverride { get; set; } = -1.0; // -1 = Proje genel ayarını kullan, 0..100 = Özel Uncrop
        public double PanOffsetX { get; set; } = 0.0;      // -0.5 .. +0.5 manuel kadraj kaydırma
        public double OutputDurationSec => IsDeleted ? 0.0 : (SourceDurationSec / Math.Max(0.1, SpeedMultiplier));
    }

    public class TimelineMarker
    {
        public double TimeSec { get; set; }
        public string Title { get; set; } = "Önemli Nokta";
        public string ColorHex { get; set; } = "#EF5F18";
    }

    public class SubtitleItem
    {
        public int Index { get; set; }
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public string Text { get; set; } = "";
    }

    public class ScenePreset
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool ShowScreen { get; set; } = true;
        public bool ShowWebcam { get; set; } = true;
        public double WebcamScale { get; set; } = 0.22;
        public SmartPortraitLayout DefaultPortraitLayout { get; set; } = SmartPortraitLayout.SplitScreenCamBottom;
    }

    public class CmrProject
    {
        public string FormatVersion { get; set; } = "CircleStudio-v1.1";
        public string ProjectName { get; set; } = "Yeni_Ders_Kaydi";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string ProjectDirectory { get; set; } = "";
        public string ProjectFilePath { get; set; } = "";

        // Medya Dosyaları (Master Kaynaklar Korunur -> Non-Destructive Kurgu)
        public string MasterCleanVideoPath { get; set; } = "";     // Kırpılmamış 16:9 Master (.mkv)
        public string Horizontal16x9VideoPath { get; set; } = "";  // 16:9 Yatay Ders Çıktısı (.mp4)
        public string Vertical9x16VideoPath { get; set; } = "";    // 9:16 Dikey Video Çıktısı (.mp4)
        public string MicrophoneWavPath { get; set; } = "";        // Ayrı Mikrofon Kanalı (.wav)
        public string SystemAudioWavPath { get; set; } = "";       // Ayrı Sistem/Masaüstü Ses Kanalı (.wav)

        // Çözünürlük & Kaynak Bilgisi
        public int MasterWidth { get; set; } = 1920;
        public int MasterHeight { get; set; } = 1080;
        public int HorizontalOutputWidth { get; set; } = 3840;     // 4K UHD (3840x2160) veya 1920x1080
        public int HorizontalOutputHeight { get; set; } = 2160;
        public int VerticalOutputWidth { get; set; } = 1080;       // 9:16 Full HD Dikey (1080x1920)
        public int VerticalOutputHeight { get; set; } = 1920;
        public int Fps { get; set; } = 60;
        public double TotalDurationSec { get; set; } = 0.0;

        // Non-Destructive Smart Portrait & Uncrop Ayarları
        public SmartPortraitLayout PortraitLayout { get; set; } = SmartPortraitLayout.FullFill9x16;
        public double UncropPercent { get; set; } = 0.0;           // 0% = Tam 9:16 Zoom, 100% = Tam 16:9 Kayıpsız Uncrop
        public double ManualPanX { get; set; } = 0.0;              // -100% .. +100% yatay kadraj telafisi
        public double SmartZoomFactor16x9 { get; set; } = 2.0;
        public double SmartZoomFactor9x16 { get; set; } = 1.75;
        public bool AutoClickSmartZoom { get; set; } = true;

        // Akıllı Sessizlik Kaldırıcı Ayarları
        public double SilenceThresholdDb { get; set; } = -38.0;
        public double SilenceMinDurationSec { get; set; } = 0.55;
        public int SilencePaddingMs { get; set; } = 150;
        public double DeadAirSpeedMultiplier { get; set; } = 8.0;
        public SilenceActionMode LastSilenceMode { get; set; } = SilenceActionMode.KeepNormal;

        // Ses & Altyazı Ayarları
        public bool EnableNoiseSuppression { get; set; } = true;
        public bool EnableLufsNormalization { get; set; } = true;
        public double TargetLufs { get; set; } = -14.0;
        public bool BurnInSubtitles { get; set; } = true;
        public CaptionStylePreset CaptionStyle { get; set; } = CaptionStylePreset.ClassicBox;

        // Circle Studio Edit: Hız Eğrisi, Geçiş Kataloğu, Renk & LUT, İnce Ayarlar, Bindirme (PIP) & Müzik
        public SpeedCurvePreset ActiveSpeedCurve { get; set; } = SpeedCurvePreset.Constant;
        public ClipTransitionPreset TransitionPreset { get; set; } = ClipTransitionPreset.Fade;
        public double TransitionDurationSec { get; set; } = 0.60;

        public List<string> ExternalMergedVideoPaths { get; set; } = new();
        public string PipOverlayMediaPath { get; set; } = "";
        public OverlayMaskShape PipMaskShape { get; set; } = OverlayMaskShape.RoundedBox;
        public int PipPositionIndex { get; set; } = 0;             // 0: Sol Üst, 1: Sağ Üst, 2: Sol Alt, 3: Sağ Alt, 4: Merkez
        public double PipScalePercent { get; set; } = 28.0;
        public string ExternalAudioTrackPath { get; set; } = "";
        public double ExternalAudioVolumePercent { get; set; } = 18.0;

        public ColorLutPreset ColorFilter { get; set; } = ColorLutPreset.NaturalStudio;
        public double AdjustBrightness { get; set; } = 0.0;        // -50 .. +50
        public double AdjustContrast { get; set; } = 0.0;          // -50 .. +50
        public double AdjustSaturation { get; set; } = 0.0;        // -50 .. +50
        public double AdjustWarmth { get; set; } = 0.0;            // -50 .. +50
        public double AdjustSharpen { get; set; } = 0.0;           // 0 .. 100
        public double AdjustVignette { get; set; } = 0.0;          // 0 .. 100

        // Otomatik Yön (9:16 / 16:9), Döndürme (0°/90°/180°/270°) & Circle 4K Ultra AI Netleştirme / Upscale
        public int MetadataRotationCwDeg { get; set; } = 0;
        public int UserExtraRotationCwDeg { get; set; } = 0;
        public bool Enable4KUltraUpscale { get; set; } = true;

        // Zaman Çizelgesi Verileri
        public List<CameraKeyframe> CameraKeyframes { get; set; } = new();
        public List<SilenceRegion> DetectedSilences { get; set; } = new();
        public List<TimelineClip> Clips { get; set; } = new();
        public List<TimelineMarker> Markers { get; set; } = new();
        public List<SubtitleItem> Subtitles { get; set; } = new();
        public List<float> WaveformPeaks { get; set; } = new();

        public void Save(string path = null)
        {
            if (!string.IsNullOrWhiteSpace(path))
                ProjectFilePath = path;

            if (string.IsNullOrWhiteSpace(ProjectFilePath))
            {
                string dir = string.IsNullOrWhiteSpace(ProjectDirectory)
                    ? CircleMixRecord.Services.HardwareEncoderService.GetBaseStorageDir()
                    : ProjectDirectory;
                Directory.CreateDirectory(dir);
                ProjectFilePath = Path.Combine(dir, $"{ProjectName}.cmrproj");
            }

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            string json = JsonSerializer.Serialize(this, opts);
            File.WriteAllText(ProjectFilePath, json, new UTF8Encoding(false));

            // Ayrıca SQLite/SQL uyumlu kamera & timeline hareket dökümü (.cmrproj.sql) kaydet
            try
            {
                SaveSqlDump(ProjectFilePath + ".sql");
            }
            catch { }
        }

        private void SaveSqlDump(string sqlPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- Circle Studio Non-Destructive Project SQLite Schema & Data");
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("CREATE TABLE IF NOT EXISTS project_meta (key TEXT PRIMARY KEY, value TEXT);");
            sb.AppendLine("CREATE TABLE IF NOT EXISTS camera_keyframes (id INTEGER PRIMARY KEY AUTOINCREMENT, time_sec REAL, norm_x REAL, norm_y REAL, zoom REAL, is_click INTEGER);");
            sb.AppendLine("CREATE TABLE IF NOT EXISTS timeline_clips (id TEXT PRIMARY KEY, label TEXT, start_sec REAL, end_sec REAL, speed REAL, is_deleted INTEGER, is_silent INTEGER);");
            sb.AppendLine($"INSERT OR REPLACE INTO project_meta VALUES ('project_name', '{ProjectName.Replace("'", "''")}');");
            sb.AppendLine($"INSERT OR REPLACE INTO project_meta VALUES ('uncrop_percent', '{UncropPercent:F1}');");
            sb.AppendLine($"INSERT OR REPLACE INTO project_meta VALUES ('portrait_layout', '{PortraitLayout}');");

            int step = Math.Max(1, CameraKeyframes.Count / 500);
            for (int i = 0; i < CameraKeyframes.Count; i += step)
            {
                var k = CameraKeyframes[i];
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "INSERT INTO camera_keyframes (time_sec, norm_x, norm_y, zoom, is_click) VALUES ({0:F3}, {1:F4}, {2:F4}, {3:F2}, {4});",
                    k.TimeSec, k.NormX, k.NormY, k.Zoom, k.IsClickEvent ? 1 : 0));
            }

            foreach (var c in Clips)
            {
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "INSERT OR REPLACE INTO timeline_clips VALUES ('{0}', '{1}', {2:F3}, {3:F3}, {4:F2}, {5}, {6});",
                    c.Id, c.Label.Replace("'", "''"), c.SourceStartSec, c.SourceEndSec, c.SpeedMultiplier, c.IsDeleted ? 1 : 0, c.IsSilentSegment ? 1 : 0));
            }
            sb.AppendLine("COMMIT;");
            File.WriteAllText(sqlPath, sb.ToString(), new UTF8Encoding(false));
        }

        public static CmrProject Load(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var proj = JsonSerializer.Deserialize<CmrProject>(json) ?? new CmrProject();
            proj.ProjectFilePath = path;
            proj.ProjectDirectory = Path.GetDirectoryName(path) ?? "";
            return proj;
        }
    }
}
