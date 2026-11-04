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
        CircleMask = 1,            // Daire Maske (Circle Edit Stil)
        ChromaKeyGreen = 2,        // Yeşil Perde Silici (Chroma Key)
        FullScreenWatermark = 3    // Yarı Saydam Tam Ekran / Filigran Bindirme
    }

    public enum ColorLutPreset
    {
        NaturalStudio = 0,         // Doğal Stüdyo
        CinematicEdits = 1,        // Circle Sinematik Kontrast
        WarmGolden = 2,            // Sıcak Altın Saat (Warm LUT)
        CrispCad3D = 3             // 3B CAD/BIM Keskin & Net Çizgi Modu
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
        public string ColorHex { get; set; } = "#F59E0B";
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
        public SmartPortraitLayout DefaultPortraitLayout { get; set; } = SmartPortraitLayout.FullFill9x16;
    }

    public class CmrProject
    {
        public string FormatVersion { get; set; } = "CircleMixRecord-v1.0";
        public string ProjectName { get; set; } = "Yeni_Ders_Kaydi";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string ProjectDirectory { get; set; } = "";
        public string ProjectFilePath { get; set; } = "";

        // Medya Dosyaları (Master Kaynaklar Korunur -> Non-Destructive Kurgu)
        public string MasterCleanVideoPath { get; set; } = "";     // Kırpılmamış 16:9 Master (.mkv)
        public string Horizontal16x9VideoPath { get; set; } = "";  // 16:9 Yatay Ders Çıktısı (.mp4)
        public string Vertical9x16VideoPath { get; set; } = "";    // 9:16 Dikey Reels Çıktısı (.mp4)
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

        // Circle Edit & Studio: Dışarıdan Medya Birleştirme, Bindirme (PIP), Maskeleme & Müzik
        public List<string> ExternalMergedVideoPaths { get; set; } = new();
        public string PipOverlayMediaPath { get; set; } = "";
        public OverlayMaskShape PipMaskShape { get; set; } = OverlayMaskShape.RoundedBox;
        public int PipPositionIndex { get; set; } = 0;             // 0: Sol Üst, 1: Sağ Üst, 2: Sol Alt, 3: Sağ Alt, 4: Merkez
        public double PipScalePercent { get; set; } = 28.0;
        public string ExternalAudioTrackPath { get; set; } = "";
        public double ExternalAudioVolumePercent { get; set; } = 18.0;
        public ColorLutPreset ColorFilter { get; set; } = ColorLutPreset.NaturalStudio;

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
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kayitlar_ve_Projeler")
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
            sb.AppendLine("-- Circle Mix Record Non-Destructive Project SQLite Schema & Data");
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
