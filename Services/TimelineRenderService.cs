// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CircleMixRecord.Models;

namespace CircleMixRecord.Services
{
    public static class TimelineRenderService
    {
        public static bool ImportSrtFile(CmrProject project, string srtPath)
        {
            if (project == null || string.IsNullOrWhiteSpace(srtPath) || !File.Exists(srtPath))
                return false;

            try
            {
                string raw = File.ReadAllText(srtPath, Encoding.UTF8);
                var blocks = raw.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                var parsed = new List<SubtitleItem>();
                int idx = 1;

                foreach (var block in blocks)
                {
                    var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    if (lines.Count < 2) continue;

                    int timeLineIdx = lines[0].Contains("-->") ? 0 : 1;
                    if (timeLineIdx >= lines.Count || !lines[timeLineIdx].Contains("-->")) continue;

                    var parts = lines[timeLineIdx].Split(new[] { "-->" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 &&
                        TryParseSrtTimestamp(parts[0].Trim(), out double st) &&
                        TryParseSrtTimestamp(parts[1].Trim(), out double en))
                    {
                        string text = string.Join(" ", lines.Skip(timeLineIdx + 1));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            parsed.Add(new SubtitleItem
                            {
                                Index = idx++,
                                StartSec = Math.Round(st, 2),
                                EndSec = Math.Round(Math.Max(st + 0.3, en), 2),
                                Text = text
                            });
                        }
                    }
                }

                if (parsed.Count > 0)
                {
                    project.Subtitles = parsed;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryParseSrtTimestamp(string s, out double seconds)
        {
            seconds = 0;
            s = s.Replace(',', '.');
            if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
            {
                seconds = ts.TotalSeconds;
                return true;
            }
            return false;
        }

        public static void GenerateAutoSubtitlesFromSpeechSegments(CmrProject project)
        {
            if (project == null) return;

            // 1. Proje veya video klasöründe hazır .srt varsa gerçek altyazıyı doğrudan yükle
            string candidateSrt = Path.Combine(
                string.IsNullOrWhiteSpace(project.ProjectDirectory) ? HardwareEncoderService.GetBaseStorageDir() : project.ProjectDirectory,
                $"{project.ProjectName}_Altyazi.srt");
            if (!File.Exists(candidateSrt) && !string.IsNullOrWhiteSpace(project.Horizontal16x9VideoPath))
            {
                candidateSrt = Path.ChangeExtension(project.Horizontal16x9VideoPath, ".srt");
            }
            if (File.Exists(candidateSrt) && ImportSrtFile(project, candidateSrt))
                return;

            project.Subtitles.Clear();

            // 2. Gerçek ses enerjisi / sessizlik analizi henüz yapılmadıysa otomatik çalıştır
            if ((project.DetectedSilences == null || project.DetectedSilences.Count == 0) &&
                (project.WaveformPeaks == null || project.WaveformPeaks.Count == 0))
            {
                AutoCutSilenceService.AnalyzeMicrophoneSilence(
                    project,
                    project.SilenceThresholdDb,
                    project.SilenceMinDurationSec,
                    project.SilencePaddingMs);
            }

            var activeSpeechClips = project.Clips
                .Where(c => !c.IsDeleted && !c.IsSilentSegment)
                .OrderBy(c => c.SourceStartSec)
                .ToList();

            double outTimeCursor = 0.0;
            int idx = 1;

            // Kayıt sırasında yakalanan gerçek aktif pencere başlıkları ve işaretçileri (Marker) kullan
            var windowTitles = project.CameraKeyframes
                .Where(k => !string.IsNullOrWhiteSpace(k.ActiveWindowTitle))
                .Select(k => k.ActiveWindowTitle.Trim())
                .Distinct()
                .ToList();
            string contextApp = windowTitles.FirstOrDefault() ?? project.ProjectName.Replace('_', ' ');

            if (activeSpeechClips.Count == 0)
            {
                double total = Math.Max(3.0, project.TotalDurationSec);
                double step = Math.Min(4.5, total / 3.0);
                for (double t = 0; t < total - 0.5; t += step)
                {
                    var marker = project.Markers.FirstOrDefault(m => Math.Abs(m.TimeSec - t) < step);
                    string lineText = marker != null
                        ? $"{marker.Title} ({contextApp})"
                        : $"{contextApp} — Bölüm {idx} ({FormatShortTimeLabel(t)})";

                    project.Subtitles.Add(new SubtitleItem
                    {
                        Index = idx,
                        StartSec = Math.Round(t, 2),
                        EndSec = Math.Round(Math.Min(total, t + step - 0.15), 2),
                        Text = lineText
                    });
                    idx++;
                }
                return;
            }

            foreach (var clip in activeSpeechClips)
            {
                double clipOutDur = clip.OutputDurationSec;
                double subStep = 4.0;
                double localPos = 0.0;
                while (localPos < clipOutDur - 0.25)
                {
                    double segLen = Math.Min(subStep, clipOutDur - localPos);
                    double srcTime = clip.SourceStartSec + localPos * clip.SpeedMultiplier;
                    var nearMarker = project.Markers.FirstOrDefault(m => Math.Abs(m.TimeSec - srcTime) <= 2.5);
                    var nearKf = project.CameraKeyframes.FirstOrDefault(k => Math.Abs(k.TimeSec - srcTime) <= 2.0 && !string.IsNullOrWhiteSpace(k.ActiveWindowTitle));

                    string labelText = nearMarker != null
                        ? nearMarker.Title
                        : (nearKf != null
                            ? $"{nearKf.ActiveWindowTitle} • {clip.Label}"
                            : $"{clip.Label} ({FormatShortTimeLabel(srcTime)})");

                    project.Subtitles.Add(new SubtitleItem
                    {
                        Index = idx,
                        StartSec = Math.Round(outTimeCursor + localPos, 2),
                        EndSec = Math.Round(outTimeCursor + localPos + segLen - 0.08, 2),
                        Text = labelText
                    });
                    idx++;
                    localPos += segLen;
                }
                outTimeCursor += clipOutDur;
            }
        }

        private static string FormatShortTimeLabel(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
        }

        public static string WriteSrtFile(CmrProject project)
        {
            string dir = project.ProjectDirectory;
            if (string.IsNullOrWhiteSpace(dir))
                dir = HardwareEncoderService.GetBaseStorageDir();
            Directory.CreateDirectory(dir);

            string srtPath = Path.Combine(dir, $"{project.ProjectName}_Altyazi.srt");
            var sb = new StringBuilder();

            int i = 1;
            foreach (var sub in project.Subtitles.OrderBy(s => s.StartSec))
            {
                sb.AppendLine((i++).ToString());
                sb.AppendLine($"{FormatSrtTime(sub.StartSec)} --> {FormatSrtTime(sub.EndSec)}");
                sb.AppendLine(sub.Text);
                sb.AppendLine();
            }

            File.WriteAllText(srtPath, sb.ToString(), new UTF8Encoding(false));
            return srtPath;
        }

        private static string FormatSrtTime(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", (int)ts.TotalHours, ts.Minutes, ts.Seconds, ts.Milliseconds);
        }

        /// <summary>
        /// Non-Destructive Uncrop: Korunan Master 16:9 kaynaktan (.mkv/.mp4) dikey 9:16 videoyu
        /// kullanıcının seçtiği Uncrop yüzdesi (0%..100%) ve PanX kadrajıyla kayıpsız yeniden üretir.
        /// </summary>
        public static async Task<string> RenderNonDestructiveUncrop9x16Async(
            CmrProject project,
            double uncropPercent,
            double panOffsetNorm,
            Action<string> onProgress = null)
        {
            if (project == null) return null;

            string masterSrc = File.Exists(project.MasterCleanVideoPath)
                ? project.MasterCleanVideoPath
                : project.Horizontal16x9VideoPath;

            if (string.IsNullOrWhiteSpace(masterSrc) || !File.Exists(masterSrc))
                return null;

            project.UncropPercent = uncropPercent;
            project.ManualPanX = panOffsetNorm;

            string outUncroppedPath = Path.Combine(
                HardwareEncoderService.GetVerticalRecordingsDir(),
                $"{project.ProjectName}_Dikey_Uncrop_{(int)uncropPercent}pct.mp4");

            var enc = HardwareEncoderService.DetectBestEncoder();
            double u = Math.Clamp(uncropPercent / 100.0, 0.0, 1.0);

            // Ortalama odak merkezini .cmrproj keyframe'lerinden hesapla
            double avgNormX = 0.5;
            if (project.CameraKeyframes != null && project.CameraKeyframes.Count > 0)
            {
                avgNormX = project.CameraKeyframes.Average(k => k.NormX);
            }
            avgNormX = Math.Clamp(avgNormX + panOffsetNorm, 0.0, 1.0);

            string vfFilter;
            if (u <= 0.03 && project.PortraitLayout == SmartPortraitLayout.FullFill9x16)
            {
                // Tam 9:16 Dikey Kesim (Odak noktası etrafında)
                string normXStr = avgNormX.ToString("0.000", CultureInfo.InvariantCulture);
                vfFilter = $"[0:v]scale=1920:1080,crop=608:1080:(iw-608)*{normXStr}:0,scale=1080:1920:flags=lanczos[vout]";
            }
            else
            {
                // Non-Destructive Uncrop: Arkaplanda 1080x1920 Sinematik Blur, önde Uncrop yüzdesine göre genişletilmiş net Master görüntü!
                int fgCropW = (int)Math.Round(608 + (1920 - 608) * u);
                fgCropW = Math.Clamp((fgCropW / 2) * 2, 608, 1920);
                int fgOutH = (int)Math.Round(1920 - (1920 - 608) * u);
                fgOutH = Math.Clamp((fgOutH / 2) * 2, 608, 1920);
                string normXStr = avgNormX.ToString("0.000", CultureInfo.InvariantCulture);

                vfFilter = $"[0:v]split=2[bg][fg];" +
                           $"[bg]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,boxblur=28:4,eq=brightness=-0.12:saturation=1.15[bgblur];" +
                           $"[fg]scale=1920:1080,crop={fgCropW}:1080:(iw-{fgCropW})*{normXStr}:0,scale=1080:{fgOutH}:flags=lanczos[fgscaled];" +
                           $"[bgblur][fgscaled]overlay=0:(H-h)/2[vout]";
            }

            onProgress?.Invoke($"Master kaynaktan Non-Destructive Uncrop (%{uncropPercent:0}) işleniyor...");

            await Task.Run(() =>
            {
                string audioInputArgs = "";
                string audioMapArgs = "";
                if (!string.IsNullOrWhiteSpace(project.Horizontal16x9VideoPath) && File.Exists(project.Horizontal16x9VideoPath))
                {
                    audioInputArgs = $"-i \"{project.Horizontal16x9VideoPath}\" ";
                    audioMapArgs = "-map 1:a:0? -c:a copy ";
                }

                string args = $"-y -hide_banner -loglevel error -i \"{masterSrc}\" {audioInputArgs}" +
                              $"-filter_complex \"{vfFilter}\" -map \"[vout]\" {audioMapArgs}" +
                              $"-c:v {enc.CodecName} {enc.PresetArgs} -movflags +faststart \"{outUncroppedPath}\"";

                RunFfmpegBlocking(args, ComputeRenderTimeoutMs(ProbeMediaInfo(masterSrc).DurationSec));
            });

            if (File.Exists(outUncroppedPath))
            {
                project.Vertical9x16VideoPath = outUncroppedPath;
                project.Save();
            }

            onProgress?.Invoke("Non-Destructive Uncrop tamamlandı!");
            return outUncroppedPath;
        }

        /// <summary>
        /// Tek Tıkla Sosyal Medya Çıktısı (Circle Yatay 16:9 + Circle Dikey 9:16):
        /// - Ripple Delete (Sessizlik kesimi) veya 8x Dead-Air Speed-Up (3D Modelleme hızlandırması)
        /// - Ses Gürültü Silme (afftdn) + -14 LUFS Broadcast Dengeleme (loudnorm)
        /// - Otomatik Altyazı (.srt) Dışa Aktarımı
        /// </summary>
        public static async Task<(string Out16x9, string Out9x16, string OutSrt)> ExportEditedSocialMediaPackageAsync(
            CmrProject project,
            bool export4K16x9,
            Action<string> onProgress = null)
        {
            if (project == null) return (null, null, null);

            string outDir = HardwareEncoderService.GetEditedExportsDir();
            string horizDir = HardwareEncoderService.GetHorizontalRecordingsDir();
            string vertDir = HardwareEncoderService.GetVerticalRecordingsDir();

            string srtPath = null;
            if (project.Subtitles != null && project.Subtitles.Count > 0)
            {
                srtPath = WriteSrtFile(project);
            }

            string stamp = DateTime.Now.ToString("HHmmss");
            string out16 = Path.Combine(horizDir, $"{project.ProjectName}_Kurgu_Yatay_16x9_{stamp}.mp4");
            string out9 = Path.Combine(vertDir, $"{project.ProjectName}_Kurgu_Dikey_9x16_{stamp}.mp4");

            var activeClips = project.Clips.Where(c => !c.IsDeleted && c.SourceDurationSec >= 0.08).ToList();
            if (activeClips.Count == 0)
            {
                activeClips.Add(new TimelineClip
                {
                    SourceStartSec = 0,
                    SourceEndSec = Math.Max(1.0, project.TotalDurationSec),
                    SpeedMultiplier = 1.0
                });
            }

            onProgress?.Invoke("1/2: Circle Yatay (16:9) kurgulu video, efektler ve -14 LUFS stüdyo sesi hazırlanıyor...");
            var errors = new List<string>();
            LastExportError = null;
            if (File.Exists(project.Horizontal16x9VideoPath))
            {
                string err16 = null;
                bool ok16 = await Task.Run(() => RenderTimelineClipsToFile(
                    project,
                    project.Horizontal16x9VideoPath,
                    activeClips,
                    export4K16x9 ? 3840 : 1920,
                    export4K16x9 ? 2160 : 1080,
                    project.EnableNoiseSuppression,
                    project.EnableLufsNormalization,
                    out16,
                    out err16));

                if (ok16 && File.Exists(out16))
                {
                    try { File.Copy(out16, Path.Combine(outDir, Path.GetFileName(out16)), true); } catch { }
                }
                else
                {
                    errors.Add(err16 ?? "16:9 çıktısı üretilemedi.");
                    out16 = null;
                }
            }
            else out16 = null;

            onProgress?.Invoke("2/2: Circle Dikey (9:16) kurgulu video, maskeleme ve müzik hazırlanıyor...");
            if (File.Exists(project.Vertical9x16VideoPath))
            {
                string err9 = null;
                bool ok9 = await Task.Run(() => RenderTimelineClipsToFile(
                    project,
                    project.Vertical9x16VideoPath,
                    activeClips,
                    1080,
                    1920,
                    project.EnableNoiseSuppression,
                    project.EnableLufsNormalization,
                    out9,
                    out err9));

                if (ok9 && File.Exists(out9))
                {
                    try { File.Copy(out9, Path.Combine(outDir, Path.GetFileName(out9)), true); } catch { }
                }
                else
                {
                    errors.Add(err9 ?? "9:16 çıktısı üretilemedi.");
                    out9 = null;
                }
            }
            else out9 = null;

            if (errors.Count > 0)
            {
                LastExportError = string.Join("\n\n", errors);
                onProgress?.Invoke("⚠️ Dışa aktarma sırasında hata oluştu — ayrıntılar için hata penceresine bakın.");
            }
            else
            {
                onProgress?.Invoke("Tamamlandı! Circle Yatay (16:9) + Circle Dikey (9:16) videoları hazır.");
            }
            return (out16, out9, srtPath);
        }

        /// <summary>Son dışa aktarma hatası (FFmpeg stderr son satırları dahil); başarılıysa null.</summary>
        public static string LastExportError { get; private set; }

        /// <summary>
        /// Tek Tıkla Timelapse (Hızlı Çekim) Üretici:
        /// Seçili videoyu 2x, 4x, 8x, 16x, 30x veya hedef süreye sıkıştırarak 60 FPS akıcı Timelapse MP4 üretir.
        /// </summary>
        public static async Task<string> RenderInstantTimelapseAsync(
            string inputMp4,
            bool isVertical,
            double speedMultiplier,
            bool muteAudio,
            Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(inputMp4) || !File.Exists(inputMp4))
                return null;

            double spd = Math.Clamp(speedMultiplier, 0.25, 30.0);
            string targetFolder = isVertical
                ? HardwareEncoderService.GetVerticalRecordingsDir()
                : HardwareEncoderService.GetHorizontalRecordingsDir();

            string baseName = Path.GetFileNameWithoutExtension(inputMp4);
            string stamp = DateTime.Now.ToString("HHmmss");
            string outMp4 = Path.Combine(targetFolder, $"{baseName}_Timelapse_{spd:0.#}x_{stamp}.mp4");

            var enc = HardwareEncoderService.DetectBestEncoder();
            string ptsFactor = (1.0 / spd).ToString("0.0000", CultureInfo.InvariantCulture);

            onProgress?.Invoke($"⚡ Timelapse ({spd:0.#}x Hız • 60 FPS) GPU ile oluşturuluyor...");

            await Task.Run(() =>
            {
                string args;
                if (muteAudio || spd > 8.0)
                {
                    args = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" -f lavfi -i anullsrc=r=44100:cl=stereo " +
                           $"-vf \"setpts={ptsFactor}*PTS,fps=60,format=yuv420p\" -map 0:v:0 -map 1:a:0 " +
                           $"-c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 128k -shortest -movflags +faststart \"{outMp4}\"";
                }
                else
                {
                    args = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" " +
                           $"-filter_complex \"[0:v]setpts={ptsFactor}*PTS,fps=60,format=yuv420p[v];[0:a]{BuildAtempoChain(spd)}[a]\" " +
                           $"-map \"[v]\" -map \"[a]\" -c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{outMp4}\"";
                }

                int tlTimeout = ComputeRenderTimeoutMs(ProbeMediaInfo(inputMp4).DurationSec);
                if (!RunFfmpegBlocking(args, tlTimeout) || !File.Exists(outMp4))
                {
                    string fallbackArgs = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" -vf \"setpts={ptsFactor}*PTS,fps=60,format=yuv420p\" -an -c:v {enc.CodecName} {enc.PresetArgs} -movflags +faststart \"{outMp4}\"";
                    RunFfmpegBlocking(fallbackArgs, tlTimeout);
                }
            });

            return File.Exists(outMp4) ? outMp4 : null;
        }

        /// <summary>
        /// Seçili klibi veya videoyu 8 dinamik Hız Eğrisi (Montage, Hero, Bullet, Jump Cut, Flash In, Flash Out, Ramp, Strobe)
        /// segmentlerine bölerek Timeline üzerine uygular.
        /// </summary>
        public static void ApplySpeedCurveToTimelineClip(CmrProject project, int clipIndex, SpeedCurvePreset preset, double constantSpeed = 1.0)
        {
            if (project?.Clips == null || project.Clips.Count == 0) return;
            project.ActiveSpeedCurve = preset;

            int idx = Math.Clamp(clipIndex, 0, project.Clips.Count - 1);
            var target = project.Clips[idx];
            double startSec = target.SourceStartSec;
            double endSec = target.SourceEndSec;
            double totalDur = Math.Max(0.2, endSec - startSec);

            if (preset == SpeedCurvePreset.Constant)
            {
                target.SpeedMultiplier = constantSpeed;
                return;
            }

            var points = SpeedCurveCatalog.GetPresetPoints(preset, constantSpeed);
            if (points.Count < 2) return;

            var newSegments = new List<TimelineClip>();
            string baseLabel = target.Label.Split('•')[0].Trim();

            for (int i = 0; i < points.Count - 1; i++)
            {
                double p0 = Math.Clamp(points[i].NormalizedProgress / 100.0, 0.0, 1.0);
                double p1 = Math.Clamp(points[i + 1].NormalizedProgress / 100.0, 0.0, 1.0);
                if (p1 - p0 < 0.01) continue;

                double segStart = Math.Round(startSec + totalDur * p0, 2);
                double segEnd = Math.Round(startSec + totalDur * p1, 2);
                if (segEnd - segStart < 0.08) continue;

                double avgSpd = Math.Round((points[i].Speed + points[i + 1].Speed) * 0.5, 2);
                avgSpd = Math.Clamp(avgSpd, 0.25, 16.0);

                newSegments.Add(new TimelineClip
                {
                    Label = $"{baseLabel} • {preset} #{i + 1}",
                    SourceStartSec = segStart,
                    SourceEndSec = segEnd,
                    SpeedMultiplier = avgSpd,
                    IsDeleted = false,
                    IsSilentSegment = false
                });
            }

            if (newSegments.Count > 0)
            {
                project.Clips.RemoveAt(idx);
                project.Clips.InsertRange(idx, newSegments);
            }
        }

        /// <summary>
        /// 8 Dinamik Hız Eğrisini (Montage, Hero, Bullet, Jump Cut, Flash In, Flash Out, Ramp, Strobe)
        /// veya Sabit Timelapse hızını doğrudan 60 FPS akıcı MP4 dosyası olarak üretir.
        /// </summary>
        public static async Task<string> RenderSpeedCurveVideoAsync(
            string inputMp4,
            bool isVertical,
            SpeedCurvePreset preset,
            double constantSpeed,
            bool muteAudio,
            Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(inputMp4) || !File.Exists(inputMp4))
                return null;

            if (preset == SpeedCurvePreset.Constant)
            {
                return await RenderInstantTimelapseAsync(inputMp4, isVertical, constantSpeed, muteAudio, onProgress);
            }

            string targetFolder = isVertical
                ? HardwareEncoderService.GetVerticalRecordingsDir()
                : HardwareEncoderService.GetHorizontalRecordingsDir();

            string baseName = Path.GetFileNameWithoutExtension(inputMp4);
            string stamp = DateTime.Now.ToString("HHmmss");
            string outMp4 = Path.Combine(targetFolder, $"{baseName}_Curve_{preset}_{stamp}.mp4");

            var enc = HardwareEncoderService.DetectBestEncoder();
            var info = ProbeMediaInfo(inputMp4);
            double dur = Math.Max(1.0, info.DurationSec > 0.2 ? info.DurationSec : ProbeVideoDurationSec(inputMp4));
            var points = SpeedCurveCatalog.GetPresetPoints(preset, constantSpeed);

            onProgress?.Invoke($"⚡ Hız Eğrisi ({preset} • 60 FPS) GPU ile işleniyor...");

            await Task.Run(() =>
            {
                var inv = CultureInfo.InvariantCulture;
                var fc = new StringBuilder();
                int segCount = 0;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    double p0 = Math.Clamp(points[i].NormalizedProgress / 100.0, 0.0, 1.0);
                    double p1 = Math.Clamp(points[i + 1].NormalizedProgress / 100.0, 0.0, 1.0);
                    double st = dur * p0;
                    double en = dur * p1;
                    if (en - st < 0.06) continue;

                    double spd = Math.Clamp((points[i].Speed + points[i + 1].Speed) * 0.5, 0.25, 16.0);
                    string stStr = st.ToString("0.000", inv);
                    string enStr = en.ToString("0.000", inv);
                    string ptsFactor = (1.0 / spd).ToString("0.0000", inv);
                    double outSegDur = Math.Max(0.04, (en - st) / spd);

                    fc.Append($"[0:v]trim=start={stStr}:end={enStr},setpts={ptsFactor}*(PTS-STARTPTS),fps=60,format=yuv420p[v{segCount}];");
                    if (!muteAudio && info.HasAudio)
                    {
                        fc.Append($"[0:a]atrim=start={stStr}:end={enStr},asetpts=PTS-STARTPTS,{BuildAtempoChain(spd)},aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo[a{segCount}];");
                    }
                    else
                    {
                        fc.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={outSegDur.ToString("0.000", inv)},asetpts=PTS-STARTPTS[a{segCount}];");
                    }
                    segCount++;
                }

                if (segCount == 0) return;

                for (int s = 0; s < segCount; s++)
                    fc.Append($"[v{s}][a{s}]");
                fc.Append($"concat=n={segCount}:v=1:a=1[vout][aout]");

                string fcArg = BuildFilterComplexArg(fc.ToString(), out string scriptPath);
                string args = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" {fcArg} -map \"[vout]\" -map \"[aout]\" " +
                              $"-c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{outMp4}\"";
                try
                {
                    RunFfmpegBlocking(args, ComputeRenderTimeoutMs(dur));
                }
                finally
                {
                    TryDelete(scriptPath);
                }
            });

            return File.Exists(outMp4) ? outMp4 : null;
        }

        /// <summary>
        /// Aktif videonun ses kanalını 48 kHz Stereo WAV olarak tek tıkla ayıklar.
        /// </summary>
        public static async Task<string> ExtractAudioToWavAsync(string inputVideoPath, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(inputVideoPath) || !File.Exists(inputVideoPath))
                return null;

            string dir = HardwareEncoderService.GetEditedExportsDir();
            string baseName = Path.GetFileNameWithoutExtension(inputVideoPath);
            string outWav = Path.Combine(dir, $"{baseName}_Ses_48kHz_{DateTime.Now:HHmmss}.wav");

            onProgress?.Invoke("🔊 Ses kanalı 48 kHz Stereo WAV olarak ayıklanıyor...");
            await Task.Run(() =>
            {
                string args = $"-y -hide_banner -loglevel error -i \"{inputVideoPath}\" -vn -acodec pcm_s16le -ar 48000 -ac 2 \"{outWav}\"";
                RunFfmpegBlocking(args, 120000);
            });

            return File.Exists(outWav) ? outWav : null;
        }

        /// <summary>
        /// Aktif videoyu 60 FPS geriye doğru (Reverse) çevirerek yeni MP4 üretir.
        /// </summary>
        public static async Task<string> RenderReversedClipAsync(string inputVideoPath, bool isVertical, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(inputVideoPath) || !File.Exists(inputVideoPath))
                return null;

            string targetFolder = isVertical
                ? HardwareEncoderService.GetVerticalRecordingsDir()
                : HardwareEncoderService.GetHorizontalRecordingsDir();
            string baseName = Path.GetFileNameWithoutExtension(inputVideoPath);
            string outMp4 = Path.Combine(targetFolder, $"{baseName}_TersCevir_{DateTime.Now:HHmmss}.mp4");

            var enc = HardwareEncoderService.DetectBestEncoder();
            var info = ProbeMediaInfo(inputVideoPath);

            onProgress?.Invoke("⏪ Video 60 FPS geriye doğru (Reverse) işleniyor...");
            await Task.Run(() =>
            {
                string audArgs = info.HasAudio ? "-af areverse -c:a aac -b:a 192k" : "-an";
                string args = $"-y -hide_banner -loglevel error -i \"{inputVideoPath}\" -vf \"reverse,fps=60,format=yuv420p\" {audArgs} -c:v {enc.CodecName} {enc.PresetArgs} -movflags +faststart \"{outMp4}\"";
                RunFfmpegBlocking(args, ComputeRenderTimeoutMs(info.DurationSec));
            });

            return File.Exists(outMp4) ? outMp4 : null;
        }

        public static string GetFfmpegXfadeTransitionName(ClipTransitionPreset preset)
        {
            return preset switch
            {
                ClipTransitionPreset.ZoomIn => "zoomin",
                ClipTransitionPreset.SlideLeft => "slideleft",
                ClipTransitionPreset.SlideRight => "slideright",
                ClipTransitionPreset.SlideUp => "slideup",
                ClipTransitionPreset.SlideDown => "slidedown",
                ClipTransitionPreset.CircleOpen => "circleopen",
                ClipTransitionPreset.CircleClose => "circleclose",
                ClipTransitionPreset.RadialClock => "radial",
                ClipTransitionPreset.DiagTlBr => "diagtl",
                ClipTransitionPreset.FadeWhite => "fadewhite",
                ClipTransitionPreset.HBlur => "hblur",
                _ => "fade"
            };
        }

        public static string BuildColorAndLutFilterChain(CmrProject project)
        {
            if (project == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var filters = new List<string>();

            // 1. 15 Stüdyo & Kamera Log LUT Ön Ayarı
            string lutFilter = project.ColorFilter switch
            {
                ColorLutPreset.LogToRec709 => "eq=contrast=1.28:saturation=1.24:gamma=0.92,unsharp=3:3:0.4",
                ColorLutPreset.AmberWarm => "colorbalance=rs=0.07:gs=0.02:bs=-0.06,eq=saturation=1.12",
                ColorLutPreset.BlueHour => "colorbalance=rs=-0.05:gs=-0.01:bs=0.08,eq=contrast=1.08:saturation=1.10",
                ColorLutPreset.CrispStudio => "eq=contrast=1.10:saturation=1.08,unsharp=5:5:0.75",
                ColorLutPreset.EuphoriaCinema => "colorbalance=rs=0.04:gs=-0.02:bs=0.05:rh=0.03:bh=-0.02,eq=contrast=1.14:saturation=1.15",
                ColorLutPreset.FrostyCool => "colorbalance=rs=-0.04:gs=0.02:bs=0.06,eq=brightness=0.02:saturation=0.94",
                ColorLutPreset.GlowSoft => "eq=brightness=0.03:contrast=1.05:saturation=1.08:gamma=1.05",
                ColorLutPreset.IndigoDeep => "colorbalance=rs=-0.03:gs=-0.03:bs=0.09,eq=contrast=1.16:brightness=-0.02",
                ColorLutPreset.JadeEmerald => "colorbalance=rs=-0.04:gs=0.05:bs=0.02,eq=contrast=1.10:saturation=1.06",
                ColorLutPreset.RedVelvet => "colorbalance=rs=0.08:gs=-0.02:bs=-0.02,eq=contrast=1.12:saturation=1.14",
                ColorLutPreset.VibrantPop => "eq=contrast=1.12:saturation=1.32,unsharp=3:3:0.45",
                ColorLutPreset.VintageWarm => "colorbalance=rs=0.06:gs=0.03:bs=-0.07,eq=contrast=0.96:saturation=0.88",
                ColorLutPreset.GreyscaleNoir => "hue=s=0,eq=contrast=1.22:brightness=-0.02",
                ColorLutPreset.CrispCad3D => "unsharp=5:5:1.15,eq=contrast=1.08",
                _ => ""
            };
            if (!string.IsNullOrEmpty(lutFilter))
                filters.Add(lutFilter);

            // 2. 6 Hassas Stüdyo Renk & Doku Ayarı (Parlaklık, Kontrast, Doygunluk, Sıcaklık, Keskinlik, Vignette)
            bool hasEq = Math.Abs(project.AdjustBrightness) > 0.5 ||
                         Math.Abs(project.AdjustContrast) > 0.5 ||
                         Math.Abs(project.AdjustSaturation) > 0.5;
            if (hasEq)
            {
                double br = Math.Clamp(project.AdjustBrightness / 100.0, -0.45, 0.45);
                double ct = Math.Clamp(1.0 + (project.AdjustContrast / 100.0), 0.5, 1.8);
                double st = Math.Clamp(1.0 + (project.AdjustSaturation / 50.0), 0.0, 2.5);
                filters.Add($"eq=brightness={br.ToString("0.000", inv)}:contrast={ct.ToString("0.000", inv)}:saturation={st.ToString("0.000", inv)}");
            }

            if (Math.Abs(project.AdjustWarmth) > 0.5)
            {
                double w = Math.Clamp(project.AdjustWarmth / 250.0, -0.20, 0.20);
                string rs = w.ToString("0.000", inv);
                string bs = (-w).ToString("0.000", inv);
                filters.Add($"colorbalance=rs={rs}:bs={bs}");
            }

            if (project.AdjustSharpen > 1.0)
            {
                double sh = Math.Clamp(project.AdjustSharpen / 55.0, 0.1, 1.8);
                filters.Add($"unsharp=5:5:{sh.ToString("0.00", inv)}");
            }

            if (project.AdjustVignette > 2.0)
            {
                filters.Add("vignette=PI/4");
            }

            return filters.Count > 0 ? "," + string.Join(",", filters) : "";
        }

        private static bool RenderTimelineClipsToFile(
            CmrProject project,
            string inputMp4,
            List<TimelineClip> activeClips,
            int targetW,
            int targetH,
            bool noiseSuppress,
            bool lufsNorm,
            string outputMp4,
            out string error)
        {
            error = null;
            var enc = HardwareEncoderService.DetectBestEncoder();
            var srcInfo = ProbeMediaInfo(inputMp4);

            // Tüm segmentler (sınır yok). Komut satırı çok uzarsa filtre grafiği dosyaya yazılır (-filter_complex_script).
            var clipsToUse = activeClips.ToList();
            var fc = new StringBuilder();
            double totalOutSec = 0;

            for (int i = 0; i < clipsToUse.Count; i++)
            {
                var c = clipsToUse[i];
                string st = c.SourceStartSec.ToString("0.000", CultureInfo.InvariantCulture);
                string en = c.SourceEndSec.ToString("0.000", CultureInfo.InvariantCulture);
                double spd = Math.Clamp(c.SpeedMultiplier, 0.25, 30.0);
                string ptsFactor = (1.0 / spd).ToString("0.0000", CultureInfo.InvariantCulture);
                double outDur = Math.Max(0.04, (c.SourceEndSec - c.SourceStartSec) / spd);
                totalOutSec += outDur;

                fc.Append($"[0:v]trim=start={st}:end={en},setpts={ptsFactor}*(PTS-STARTPTS)[v{i}];");
                if (srcInfo.HasAudio)
                {
                    fc.Append($"[0:a]atrim=start={st}:end={en},asetpts=PTS-STARTPTS,{BuildAtempoChain(spd)},aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
                }
                else
                {
                    // Kaynakta ses yoksa: segment süresi kadar sessiz stereo üret (concat a=1 için)
                    string d = outDur.ToString("0.000", CultureInfo.InvariantCulture);
                    fc.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={d},asetpts=PTS-STARTPTS[a{i}];");
                }
            }

            for (int i = 0; i < clipsToUse.Count; i++)
            {
                fc.Append($"[v{i}][a{i}]");
            }
            fc.Append($"concat=n={clipsToUse.Count}:v=1:a=1[vcat][acat];");

            // Circle Studio Renk, LUT & İnce Ayar Filtresi
            string colorFilter = BuildColorAndLutFilterChain(project);

            string extraInputArgs = "";
            int nextInputIdx = 1;
            string currentVideoPad = "[vbase]";
            fc.Append(BuildFitFilter("[vcat]", srcInfo.Width, srcInfo.Height, targetW, targetH, colorFilter, currentVideoPad, "fit"));

            // Görüntü / Video Bindirme (PIP Overlay) & Maskeleme
            if (project != null && !string.IsNullOrWhiteSpace(project.PipOverlayMediaPath) && File.Exists(project.PipOverlayMediaPath))
            {
                int pipIdx = nextInputIdx++;
                string ext = Path.GetExtension(project.PipOverlayMediaPath).ToLowerInvariant();
                bool isImg = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".webp";
                extraInputArgs += isImg
                    ? $"-loop 1 -i \"{project.PipOverlayMediaPath}\" "
                    : $"-stream_loop -1 -i \"{project.PipOverlayMediaPath}\" ";

                int pipW = Math.Max(96, (int)(targetW * Math.Clamp(project.PipScalePercent / 100.0, 0.12, 0.85)));
                pipW = (pipW / 2) * 2;

                string maskChain = project.PipMaskShape switch
                {
                    OverlayMaskShape.ChromaKeyGreen => $",colorkey=0x00FF00:0.28:0.12",
                    OverlayMaskShape.FullScreenWatermark => $",format=rgba,colorchannelmixer=aa=0.45",
                    _ => ""
                };

                string posExpr = project.PipPositionIndex switch
                {
                    0 => "24:24",
                    1 => "W-w-24:24",
                    2 => "24:H-h-24",
                    3 => "W-w-24:H-h-24",
                    _ => "(W-w)/2:(H-h)/2"
                };

                fc.Append($"[{pipIdx}:v]scale={pipW}:-2{maskChain}[pipv];");
                fc.Append($"{currentVideoPad}[pipv]overlay={posExpr}:shortest=1[vpip];");
                currentVideoPad = "[vpip]";
            }

            // Altyazıları Videoya Göm (Burn-In Subtitles - FFmpeg drawtext zinciri, 4 stil destekli)
            if (project != null && project.BurnInSubtitles && project.Subtitles != null && project.Subtitles.Count > 0)
            {
                var subsToBurn = project.Subtitles.OrderBy(s => s.StartSec).Take(32).ToList();
                int fontSize = targetH >= 1800 ? 44 : 32;
                var dtChain = new List<string>();

                string styleArgs = project.CaptionStyle switch
                {
                    CaptionStylePreset.OrangeKaraoke => $"fontcolor=white:fontsize={fontSize}:box=1:boxcolor=0xEF5F18@0.92:boxborderw=14:x=(w-text_w)/2:y=h-th-72",
                    CaptionStylePreset.MinimalCinema => $"fontcolor=white:fontsize={fontSize}:box=0:shadowcolor=0x000000@0.88:shadowx=2:shadowy=2:x=(w-text_w)/2:y=h-th-68",
                    CaptionStylePreset.TopBanner => $"fontcolor=white:fontsize={fontSize}:box=1:boxcolor=0x261A66@0.90:boxborderw=14:x=(w-text_w)/2:y=56",
                    _ => $"fontcolor=white:fontsize={fontSize}:box=1:boxcolor=0x140E38@0.85:boxborderw=12:x=(w-text_w)/2:y=h-th-64"
                };

                foreach (var sub in subsToBurn)
                {
                    string safeText = EscapeDrawtext(sub.Text);
                    if (string.IsNullOrWhiteSpace(safeText)) continue;
                    string st = sub.StartSec.ToString("0.00", CultureInfo.InvariantCulture);
                    string en = sub.EndSec.ToString("0.00", CultureInfo.InvariantCulture);
                    dtChain.Add($"drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='{safeText}':{styleArgs}:enable='between(t,{st},{en})'");
                }

                if (dtChain.Count > 0)
                {
                    fc.Append($"{currentVideoPad}{string.Join(",", dtChain)}[vsub];");
                    currentVideoPad = "[vsub]";
                }
            }

            fc.Append($"{currentVideoPad}format=yuv420p[vfinal];");

            var audioFilters = new List<string>();
            if (noiseSuppress)
                audioFilters.Add("highpass=f=80,afftdn=nf=-24");
            audioFilters.Add("acompressor=threshold=-18dB:ratio=3.2:makeup=2dB");
            if (lufsNorm)
                audioFilters.Add("loudnorm=I=-14:TP=-1.5:LRA=11");

            // Dışarıdan Arka Plan Müziği / Ses Ekleme (Auto-Mix)
            if (project != null && !string.IsNullOrWhiteSpace(project.ExternalAudioTrackPath) && File.Exists(project.ExternalAudioTrackPath))
            {
                int audIdx = nextInputIdx++;
                extraInputArgs += $"-stream_loop -1 -i \"{project.ExternalAudioTrackPath}\" ";
                string bgVol = Math.Clamp(project.ExternalAudioVolumePercent / 100.0, 0.02, 1.0).ToString("0.00", CultureInfo.InvariantCulture);
                fc.Append($"[acat]{string.Join(",", audioFilters)}[avoice];");
                fc.Append($"[{audIdx}:a]volume={bgVol}[abgm];");
                fc.Append($"[avoice][abgm]amix=inputs=2:duration=first:dropout_transition=2[afinal]");
            }
            else
            {
                fc.Append($"[acat]{string.Join(",", audioFilters)}[afinal]");
            }

            bool hasMerge = project?.ExternalMergedVideoPaths != null && project.ExternalMergedVideoPaths.Any(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f));
            string tempMainOut = hasMerge
                ? Path.Combine(Path.GetDirectoryName(outputMp4) ?? "", "tmp_main_" + Path.GetFileName(outputMp4))
                : outputMp4;

            string fcArg = BuildFilterComplexArg(fc.ToString(), out string scriptPath);
            string args = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" {extraInputArgs}" +
                          $"{fcArg} -map \"[vfinal]\" -map \"[afinal]\" " +
                          $"-c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{tempMainOut}\"";

            try
            {
                bool ok = RunFfmpeg(args, ComputeRenderTimeoutMs(totalOutSec), out string ffErr);
                if (!ok || !File.Exists(tempMainOut) || new FileInfo(tempMainOut).Length < 1024)
                {
                    // Sessiz "kesilmemiş video" yedeği YOK: hata kullanıcıya gösterilir.
                    TryDelete(tempMainOut);
                    error = $"{Path.GetFileName(outputMp4)}: Kurgu çıktısı üretilemedi.\n{ffErr}";
                    return false;
                }
            }
            finally
            {
                TryDelete(scriptPath);
            }

            // Dışarıdan Eklenen Videolar Varsa Birleştir (Concat)
            if (hasMerge)
            {
                bool merged = MergeVideosWithExternalClips(tempMainOut, project.ExternalMergedVideoPaths, targetW, targetH, enc, outputMp4, out string mergeErr);
                TryDelete(tempMainOut == outputMp4 ? null : tempMainOut);
                if (!merged)
                {
                    error = $"{Path.GetFileName(outputMp4)}: Ek videolar birleştirilemedi.\n{mergeErr}";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Kaynak en-boy oranı hedefe uyuyorsa (±%2) düz ölçekleme; uymuyorsa bulanık arka plan dolgulu "sığdır".
        /// (16:9 kaynağı 9:16 çıktıda asla sündürmez.)
        /// </summary>
        internal static string BuildFitFilter(string inPad, int srcW, int srcH, int targetW, int targetH, string colorFilter, string outPad, string tag)
        {
            double dstAr = (double)targetW / targetH;
            double srcAr = (srcW > 0 && srcH > 0) ? (double)srcW / srcH : dstAr;
            colorFilter ??= "";

            if (Math.Abs(srcAr - dstAr) / dstAr <= 0.02)
            {
                return $"{inPad}scale={targetW}:{targetH}:flags=lanczos,setsar=1{colorFilter},format=yuv420p{outPad};";
            }

            int bw = Math.Max(16, (targetW / 4) / 2 * 2);
            int bh = Math.Max(16, (targetH / 4) / 2 * 2);
            return $"{inPad}split=2[{tag}bg0][{tag}fg0];" +
                   $"[{tag}bg0]scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},boxblur=10:2,eq=brightness=-0.08:saturation=1.1,scale={targetW}:{targetH},setsar=1[{tag}bg1];" +
                   $"[{tag}fg0]scale={targetW}:{targetH}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,setsar=1[{tag}fg1];" +
                   $"[{tag}bg1][{tag}fg1]overlay=(W-w)/2:(H-h)/2{colorFilter},format=yuv420p{outPad};";
        }

        /// <summary>Uzun filtre grafiklerini (komut satırı sınırı ~32K) geçici dosyaya yazar.</summary>
        private static string BuildFilterComplexArg(string filterComplex, out string scriptPath)
        {
            scriptPath = null;
            if (filterComplex.Length <= 7000)
                return $"-filter_complex \"{filterComplex}\"";

            scriptPath = Path.Combine(Path.GetTempPath(), $"cmr_fc_{Guid.NewGuid():N}.txt");
            File.WriteAllText(scriptPath, filterComplex, new UTF8Encoding(false));
            return $"-filter_complex_script \"{scriptPath}\"";
        }

        /// <summary>Süreye orantılı cömert zaman aşımı: max(10 dk, 4 x süre).</summary>
        private static int ComputeRenderTimeoutMs(double durationSec)
        {
            double ms = Math.Max(600_000.0, durationSec * 4.0 * 1000.0);
            return (int)Math.Min(int.MaxValue - 1, ms);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public sealed class MediaProbeInfo
        {
            public int Width;
            public int Height;
            public double DurationSec;
            public double Fps;
            public bool HasAudio;
            public bool HasVideo;
        }

        /// <summary>ffmpeg -i çıktısından genişlik/yükseklik (döndürme dahil), süre, fps ve ses varlığını okur.</summary>
        public static MediaProbeInfo ProbeMediaInfo(string path)
        {
            var info = new MediaProbeInfo();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return info;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-hide_banner -i \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return info;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);

                var mDur = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (mDur.Success)
                {
                    info.DurationSec = int.Parse(mDur.Groups[1].Value) * 3600 +
                                       int.Parse(mDur.Groups[2].Value) * 60 +
                                       double.Parse(mDur.Groups[3].Value, CultureInfo.InvariantCulture);
                }

                foreach (var line in err.Split('\n'))
                {
                    if (!info.HasVideo && line.Contains("Video:"))
                    {
                        var mSize = System.Text.RegularExpressions.Regex.Match(line, @"[\s,](\d{2,5})x(\d{2,5})(?=[\s,\[]|$)");
                        if (mSize.Success)
                        {
                            info.Width = int.Parse(mSize.Groups[1].Value);
                            info.Height = int.Parse(mSize.Groups[2].Value);
                            info.HasVideo = true;
                        }
                        var mFps = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)\s+fps");
                        if (mFps.Success) info.Fps = double.Parse(mFps.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    if (line.Contains("Audio:")) info.HasAudio = true;
                }

                // Telefon videolarında döndürme (ffmpeg otomatik döndürür → genişlik/yükseklik yer değiştirir)
                var mRot = System.Text.RegularExpressions.Regex.Match(err, @"rotation of\s*(-?\d+(?:\.\d+)?)\s*degrees");
                if (mRot.Success)
                {
                    double rot = Math.Abs(double.Parse(mRot.Groups[1].Value, CultureInfo.InvariantCulture)) % 180.0;
                    if (Math.Abs(rot - 90.0) < 1.0) (info.Width, info.Height) = (info.Height, info.Width);
                }
            }
            catch { }
            return info;
        }

        /// <summary>
        /// Circle Studio Native Multi-Clip Ultra-SlowMo & 4K UHD Master Birleştirici:
        /// ExternalMergedVideoPaths listesindeki sahneleri (Google Flow 9:16 Dikey ve 16:9 Yatay Villa Sahneleri)
        /// 60 FPS akıcı optik kadans, 0.65s profesyonel xfade + acrossfade geçişleri ve 48kHz Stereo (-14 LUFS)
        /// ses kanalıyla doğrudan Circle Studio C# motoru üzerinden birleştirip 1080p ve 4K UHD üretir.
        /// </summary>
        public static async Task<(string Master1080pPath, string Master4KUhdPath)> RenderMultiClipSlowMoMasterAsync(
            CmrProject project,
            double slowMoStretchFactor = 1.18,
            double xfadeDurationSec = 0.65,
            Action<string> onProgress = null)
        {
            if (project == null || project.ExternalMergedVideoPaths == null || project.ExternalMergedVideoPaths.Count == 0)
                return (null, null);

            var clips = project.ExternalMergedVideoPaths.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList();
            if (clips.Count == 0)
                return (null, null);

            string horizDir = HardwareEncoderService.GetHorizontalRecordingsDir();
            string outDir = HardwareEncoderService.GetEditedExportsDir();
            string baseDir = HardwareEncoderService.GetBaseStorageDir();

            string outHoriz1080 = Path.Combine(horizDir, "CircleEdit_ELEGANCE_PALACE_1Dk_Elit_Nisan_Dugun_Filmi_Yatay_16x9_1080p_60fps.mp4");
            string outHoriz4K = Path.Combine(horizDir, "CircleEdit_ELEGANCE_PALACE_1Dk_Elit_Nisan_Dugun_Filmi_Yatay_16x9_4K_UHD_60fps.mp4");

            onProgress?.Invoke($"🎬 Circle Studio: {clips.Count} Sahnelik 1 Dakikalık Elit Nişan & Düğün Filmi 16:9 Yatay (60 FPS • 1080p & 4K UHD) birleştiriliyor...");

            await Task.Run(() =>
            {
                string bgScorePath = Path.Combine(baseDir, "Zencircle_Iskocya_BeatSync_Cello_Piano_SubDrop_48kHz.wav");
                string selectedTransition = GetFfmpegXfadeTransitionName(project.TransitionPreset);
                double selectedTransDur = Math.Clamp(project.TransitionDurationSec > 0.1 ? project.TransitionDurationSec : xfadeDurationSec, 0.20, 1.80);

                BuildProXfadeMaster(
                    clips,
                    isVertical: false,
                    xfadeDurationSec: selectedTransDur,
                    stretchFactor: 1.095,
                    bgScorePath: bgScorePath,
                    out1080Path: outHoriz1080,
                    out4KPath: outHoriz4K,
                    onProgress: onProgress,
                    xfadeTransitionName: selectedTransition);

                if (File.Exists(outHoriz4K))
                {
                    try { File.Copy(outHoriz4K, Path.Combine(outDir, Path.GetFileName(outHoriz4K)), true); } catch { }
                }
                if (File.Exists(outHoriz1080))
                {
                    try { File.Copy(outHoriz1080, Path.Combine(outDir, Path.GetFileName(outHoriz1080)), true); } catch { }
                }
            });

            onProgress?.Invoke("✅ Circle Studio: 1 Dakikalık (01:00) 16:9 Yatay 4K UHD & 1080p Elit Nişan & Düğün Filmi Hazır!");
            return (File.Exists(outHoriz1080) ? outHoriz1080 : null, File.Exists(outHoriz4K) ? outHoriz4K : null);
        }

        private static void BuildProXfadeMaster(
            List<string> clips,
            bool isVertical,
            double xfadeDurationSec,
            double stretchFactor,
            string bgScorePath,
            string out1080Path,
            string out4KPath,
            Action<string> onProgress,
            string xfadeTransitionName = "fade")
        {
            var inv = CultureInfo.InvariantCulture;
            int w1080 = isVertical ? 1080 : 1920;
            int h1080 = isVertical ? 1920 : 1080;
            int w4K = isVertical ? 2160 : 3840;
            int h4K = isVertical ? 3840 : 2160;

            var inArgs = new StringBuilder();
            var fc = new StringBuilder();
            var clipDurs = new List<double>();

            // Her sahnenin başındaki ve sonundaki mikro-duraksamayı kırpıp 60 FPS akıcı kadansa al
            for (int i = 0; i < clips.Count; i++)
            {
                inArgs.Append($"-i \"{clips[i]}\" ");
                var mInfo = ProbeMediaInfo(clips[i]);
                double rawDur = Math.Max(2.2, mInfo.DurationSec > 0.5 ? mInfo.DurationSec : ProbeVideoDurationSec(clips[i]));
                double trimStart = rawDur > 3.5 ? 0.08 : 0.04;
                double trimEnd = Math.Max(trimStart + 1.5, rawDur - 0.08);
                double trimmedDur = trimEnd - trimStart;
                double stretchedDur = Math.Round(trimmedDur * stretchFactor, 3);
                clipDurs.Add(stretchedDur);

                string tsStr = trimStart.ToString("0.000", inv);
                string teStr = trimEnd.ToString("0.000", inv);
                string sfStr = stretchFactor.ToString("0.0000", inv);
                string atempoStr = (1.0 / stretchFactor).ToString("0.0000", inv);
                string sdStr = stretchedDur.ToString("0.000", inv);

                string scaleExpr = (isVertical && mInfo.Width > mInfo.Height)
                    ? $"scale={w1080}:{h1080}:force_original_aspect_ratio=increase:flags=lanczos,crop={w1080}:{h1080},setsar=1"
                    : $"scale={w1080}:{h1080}:flags=lanczos,setsar=1";

                fc.Append($"[{i}:v]trim=start={tsStr}:end={teStr},setpts={sfStr}*(PTS-STARTPTS),fps=60,{scaleExpr},format=yuv420p[v{i}];");
                if (mInfo.HasAudio)
                {
                    fc.Append($"[{i}:a]atrim=start={tsStr}:end={teStr},asetpts=PTS-STARTPTS,atempo={atempoStr},aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
                }
                else
                {
                    fc.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={sdStr},asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a{i}];");
                }
            }

            // Sahneler arası profesyonel sinematik xfade (video) ve acrossfade (stereo ses) zinciri
            string[] transitions = new[] { string.IsNullOrWhiteSpace(xfadeTransitionName) ? "fade" : xfadeTransitionName };

            string prevV = "[v0]";
            string prevA = "[a0]";
            double cumulative = clipDurs[0];

            for (int i = 1; i < clips.Count; i++)
            {
                double offset = Math.Max(0.2, cumulative - xfadeDurationSec);
                string offStr = offset.ToString("0.000", inv);
                string xfStr = xfadeDurationSec.ToString("0.000", inv);
                string transName = transitions[(i - 1) % transitions.Length];

                string nextV = (i == clips.Count - 1) ? "[vxlast]" : $"[vx{i}]";
                string nextA = (i == clips.Count - 1) ? "[axlast]" : $"[ax{i}]";

                fc.Append($"{prevV}[v{i}]xfade=transition={transName}:duration={xfStr}:offset={offStr}{nextV};");
                fc.Append($"{prevA}[a{i}]acrossfade=d={xfStr}:c1=tri:c2=tri{nextA};");

                prevV = nextV;
                prevA = nextA;
                cumulative = offset + clipDurs[i];
            }

            fc.Append($"{prevV}colorbalance=rs=0.02:gs=0.01:bs=-0.01,unsharp=3:3:0.35:3:3:0.0,format=yuv420p[vout];");

            string totalStr = cumulative.ToString("0.000", inv);
            bool hasBgScore = !string.IsNullOrWhiteSpace(bgScorePath) && File.Exists(bgScorePath);
            if (hasBgScore)
            {
                int scoreIdx = clips.Count;
                inArgs.Append($"-stream_loop -1 -i \"{bgScorePath}\" ");
                // Sahnelerdeki yapay konuşma/uğultu seslerini tamamen sustur; yalnızca saf enstrümantal orkestra müziği kullan
                fc.Append($"{prevA}volume=0.0[amuted];");
                fc.Append($"[{scoreIdx}:a]atrim=duration={totalStr},asetpts=PTS-STARTPTS,volume=1.0,afade=t=in:st=0:d=1.5,afade=t=out:st={Math.Max(1.0, cumulative - 2.2).ToString("0.00", inv)}:d=2.2,aformat=sample_rates=48000:channel_layouts=stereo[abgm];");
                fc.Append($"[amuted][abgm]amix=inputs=2:duration=first:dropout_transition=2,loudnorm=I=-14:TP=-1.5:LRA=11[aout]");
            }
            else
            {
                fc.Append($"{prevA}highpass=f=60,acompressor=threshold=-16dB:ratio=2.5:makeup=2dB,loudnorm=I=-14:TP=-1.5:LRA=11[aout]");
            }

            var enc = HardwareEncoderService.DetectBestEncoder();
            string tag = isVertical ? "9x16" : "16x9";
            string out1080Tmp = Path.Combine(Path.GetTempPath(), $"cmr_master_{tag}_1080p_tmp.mp4");
            string out4KTmp = Path.Combine(Path.GetTempPath(), $"cmr_master_{tag}_4k_tmp.mp4");

            string args1080 = $"-y -hide_banner -loglevel error -threads 8 {inArgs}" +
                              $"-filter_complex \"{fc}\" -map \"[vout]\" -map \"[aout]\" " +
                              $"-t {totalStr} -c:v {enc.CodecName} {enc.PresetArgs} -pix_fmt yuv420p -c:a aac -b:a 320k -ar 48000 -movflags +faststart \"{out1080Tmp}\"";

            RunFfmpegBlocking(args1080, 3600000);
            try { if (File.Exists(out1080Tmp)) File.Copy(out1080Tmp, out1080Path, true); } catch { }

            string srcFor4K = File.Exists(out1080Tmp) ? out1080Tmp : out1080Path;
            if (File.Exists(srcFor4K))
            {
                onProgress?.Invoke($"✨ Circle Studio: 4K UHD ({w4K}×{h4K} • 60 FPS) Profesyonel Geçişli Master işleniyor...");
                string args4K = $"-y -hide_banner -loglevel error -threads 8 -i \"{srcFor4K}\" " +
                                $"-vf \"scale={w4K}:{h4K}:flags=lanczos,unsharp=5:5:0.55:3:3:0.0\" " +
                                $"-c:v {enc.CodecName} {enc.PresetArgs} -pix_fmt yuv420p -c:a copy -movflags +faststart \"{out4KTmp}\"";
                RunFfmpegBlocking(args4K, 3600000);
                try { if (File.Exists(out4KTmp)) File.Copy(out4KTmp, out4KPath, true); } catch { }
            }
        }

        public static double ProbeVideoDurationSec(string videoPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-i \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return 6.0;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (m.Success)
                {
                    return int.Parse(m.Groups[1].Value) * 3600 +
                           int.Parse(m.Groups[2].Value) * 60 +
                           double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return 6.0;
        }

        private static bool MergeVideosWithExternalClips(
            string mainVideoPath,
            List<string> externalVideoPaths,
            int targetW,
            int targetH,
            HardwareEncoderInfo enc,
            string finalOutputMp4,
            out string error)
        {
            error = null;
            var validFiles = new List<string> { mainVideoPath };
            foreach (var extVid in externalVideoPaths)
            {
                if (!string.IsNullOrWhiteSpace(extVid) && File.Exists(extVid))
                    validFiles.Add(extVid);
            }

            if (validFiles.Count <= 1)
            {
                if (mainVideoPath != finalOutputMp4)
                    File.Copy(mainVideoPath, finalOutputMp4, true);
                return true;
            }

            var infos = validFiles.Select(ProbeMediaInfo).ToList();
            // Hedef kare hızı: girişlerin en yükseği (en fazla 60), bilinmiyorsa 30
            double maxFps = infos.Where(i => i.Fps > 1).Select(i => i.Fps).DefaultIfEmpty(30).Max();
            int targetFps = (int)Math.Clamp(Math.Round(maxFps), 24, 60);
            var inv = CultureInfo.InvariantCulture;

            var inSb = new StringBuilder();
            var fcSb = new StringBuilder();
            double totalSec = 0;
            for (int i = 0; i < validFiles.Count; i++)
            {
                var info = infos[i];
                double dur = info.DurationSec > 0.05 ? info.DurationSec : 5.0;
                totalSec += dur;
                string dStr = dur.ToString("0.000", inv);
                inSb.Append($"-i \"{validFiles[i]}\" ");

                // Görüntü: hedefe sığdır + kenar dolgu, kare hızı ve SAR eşitle
                fcSb.Append($"[{i}:v]scale={targetW}:{targetH}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={targetFps},format=yuv420p,setpts=PTS-STARTPTS[mv{i}];");

                // Ses: 48 kHz stereo'ya normalize et; sesi olmayan girişler için sessiz stereo üret.
                // Her segmentin sesi görüntü süresine eşitlenir (apad + atrim) → kayma yok.
                if (info.HasAudio)
                    fcSb.Append($"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,atrim=duration={dStr},asetpts=PTS-STARTPTS[ma{i}];");
                else
                    fcSb.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={dStr},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS[ma{i}];");
            }
            for (int i = 0; i < validFiles.Count; i++)
            {
                fcSb.Append($"[mv{i}][ma{i}]");
            }
            fcSb.Append($"concat=n={validFiles.Count}:v=1:a=1[vmerged][amerged]");

            string fcArg = BuildFilterComplexArg(fcSb.ToString(), out string scriptPath);
            string args = $"-y -hide_banner -loglevel error {inSb}{fcArg} -map \"[vmerged]\" -map \"[amerged]\" -c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{finalOutputMp4}\"";
            try
            {
                bool ok = RunFfmpeg(args, ComputeRenderTimeoutMs(totalSec), out string ffErr);
                if (!ok || !File.Exists(finalOutputMp4) || new FileInfo(finalOutputMp4).Length < 1024)
                {
                    TryDelete(finalOutputMp4);
                    error = ffErr;
                    return false;
                }
                return true;
            }
            finally
            {
                TryDelete(scriptPath);
            }
        }

        private static string EscapeDrawtext(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return text
                .Replace("\\", "")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace(":", " - ")
                .Replace("%", " pct")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        public static bool ExtractSingleFramePng(string videoPath, double timeSec, string outputPngPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return false;
            string tStr = Math.Max(0, timeSec).ToString("0.000", CultureInfo.InvariantCulture);
            string args = $"-y -hide_banner -loglevel error -ss {tStr} -i \"{videoPath}\" -frames:v 1 -q:v 1 \"{outputPngPath}\"";
            return RunFfmpegBlocking(args, 15000) && File.Exists(outputPngPath);
        }

        private static string BuildAtempoChain(double speed)
        {
            if (Math.Abs(speed - 1.0) < 0.01) return "anull";
            var parts = new List<string>();
            double rem = speed;
            while (rem > 2.0)
            {
                parts.Add("atempo=2.0");
                rem /= 2.0;
            }
            while (rem < 0.5)
            {
                parts.Add("atempo=0.5");
                rem /= 0.5;
            }
            if (Math.Abs(rem - 1.0) >= 0.01)
            {
                parts.Add($"atempo={rem.ToString("0.000", CultureInfo.InvariantCulture)}");
            }
            return parts.Count > 0 ? string.Join(",", parts) : "anull";
        }

        private static bool RunFfmpegBlocking(string arguments, int timeoutMs)
        {
            return RunFfmpeg(arguments, timeoutMs, out _);
        }

        /// <summary>
        /// FFmpeg'i çalıştırır; stderr'in son satırlarını toplar. Zaman aşımında süreç ağacı ÖLDÜRÜLÜR
        /// (arka planda sahipsiz ffmpeg kalmaz).
        /// </summary>
        private static bool RunFfmpeg(string arguments, int timeoutMs, out string stderrTail)
        {
            var tail = new Queue<string>();
            var tailLock = new object();
            stderrTail = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (tailLock)
                    {
                        tail.Enqueue(e.Data.Trim());
                        while (tail.Count > 12) tail.Dequeue();
                    }
                };
                if (!p.Start()) { stderrTail = "FFmpeg başlatılamadı."; return false; }
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(Math.Max(1000, timeoutMs));
                if (!exited)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.WaitForExit(5000); } catch { }
                    lock (tailLock) stderrTail = $"FFmpeg {timeoutMs / 1000} sn içinde bitmedi ve durduruldu.\n" + string.Join("\n", tail);
                    return false;
                }
                p.WaitForExit(); // asenkron stderr okumasının bitmesini bekle
                lock (tailLock) stderrTail = string.Join("\n", tail);
                if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(stderrTail))
                    stderrTail = $"FFmpeg çıkış kodu: {p.ExitCode}";
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                stderrTail = ex.Message;
                return false;
            }
        }
    }
}


