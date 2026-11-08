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
                string.IsNullOrWhiteSpace(project.ProjectDirectory) ? AppDomain.CurrentDomain.BaseDirectory : project.ProjectDirectory,
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
                dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kayitlar_ve_Projeler");
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

                RunFfmpegBlocking(args, 120000);
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
        /// Tek Tıkla Sosyal Medya Çıktısı (YouTube 16:9 + Reels/Shorts 9:16):
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

            onProgress?.Invoke("1/2: YouTube (16:9) kurgulu video, efektler ve -14 LUFS stüdyo sesi hazırlanıyor...");
            if (File.Exists(project.Horizontal16x9VideoPath))
            {
                await Task.Run(() => RenderTimelineClipsToFile(
                    project,
                    project.Horizontal16x9VideoPath,
                    activeClips,
                    export4K16x9 ? 3840 : 1920,
                    export4K16x9 ? 2160 : 1080,
                    project.EnableNoiseSuppression,
                    project.EnableLufsNormalization,
                    out16));

                if (File.Exists(out16))
                {
                    try { File.Copy(out16, Path.Combine(outDir, Path.GetFileName(out16)), true); } catch { }
                }
            }

            onProgress?.Invoke("2/2: Reels / Shorts (9:16 Dikey) kurgulu video, maskeleme ve müzik hazırlanıyor...");
            if (File.Exists(project.Vertical9x16VideoPath))
            {
                await Task.Run(() => RenderTimelineClipsToFile(
                    project,
                    project.Vertical9x16VideoPath,
                    activeClips,
                    1080,
                    1920,
                    project.EnableNoiseSuppression,
                    project.EnableLufsNormalization,
                    out9));

                if (File.Exists(out9))
                {
                    try { File.Copy(out9, Path.Combine(outDir, Path.GetFileName(out9)), true); } catch { }
                }
            }

            onProgress?.Invoke("Tamamlandı! YouTube (16:9) + Reels/Shorts (9:16) videoları hazır.");
            return (out16, out9, srtPath);
        }

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

                if (!RunFfmpegBlocking(args, 180000) || !File.Exists(outMp4))
                {
                    string fallbackArgs = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" -vf \"setpts={ptsFactor}*PTS,fps=60,format=yuv420p\" -an -c:v {enc.CodecName} {enc.PresetArgs} -movflags +faststart \"{outMp4}\"";
                    RunFfmpegBlocking(fallbackArgs, 180000);
                }
            });

            return File.Exists(outMp4) ? outMp4 : null;
        }

        private static void RenderTimelineClipsToFile(
            CmrProject project,
            string inputMp4,
            List<TimelineClip> activeClips,
            int targetW,
            int targetH,
            bool noiseSuppress,
            bool lufsNorm,
            string outputMp4)
        {
            var enc = HardwareEncoderService.DetectBestEncoder();

            // En fazla 24 segmenti tek filter_complex içinde birleştir (performans ve komut satırı limiti koruması)
            var clipsToUse = activeClips.Take(24).ToList();
            var fc = new StringBuilder();

            for (int i = 0; i < clipsToUse.Count; i++)
            {
                var c = clipsToUse[i];
                string st = c.SourceStartSec.ToString("0.000", CultureInfo.InvariantCulture);
                string en = c.SourceEndSec.ToString("0.000", CultureInfo.InvariantCulture);
                double spd = Math.Clamp(c.SpeedMultiplier, 0.25, 30.0);
                string ptsFactor = (1.0 / spd).ToString("0.0000", CultureInfo.InvariantCulture);

                fc.Append($"[0:v]trim=start={st}:end={en},setpts={ptsFactor}*(PTS-STARTPTS)[v{i}];");
                fc.Append($"[0:a]atrim=start={st}:end={en},asetpts=PTS-STARTPTS,{BuildAtempoChain(spd)}[a{i}];");
            }

            for (int i = 0; i < clipsToUse.Count; i++)
            {
                fc.Append($"[v{i}][a{i}]");
            }
            fc.Append($"concat=n={clipsToUse.Count}:v=1:a=1[vcat][acat];");

            // Circle Edit & Studio Renk & Keskinlik Filtresi
            string colorFilter = project?.ColorFilter switch
            {
                ColorLutPreset.CinematicEdits => ",eq=contrast=1.12:saturation=1.18:brightness=0.02,unsharp=5:5:0.6",
                ColorLutPreset.WarmGolden => ",eq=contrast=1.08:saturation=1.22,colorbalance=rs=0.06:gs=0.02:bs=-0.05",
                ColorLutPreset.CrispCad3D => ",unsharp=5:5:1.1,eq=contrast=1.06",
                _ => ""
            };

            string extraInputArgs = "";
            int nextInputIdx = 1;
            string currentVideoPad = "[vbase]";
            fc.Append($"[vcat]scale={targetW}:{targetH}:flags=lanczos{colorFilter},format=yuv420p{currentVideoPad};");

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

            // Altyazıları Videoya Göm (Burn-In Subtitles - FFmpeg drawtext zinciri)
            if (project != null && project.BurnInSubtitles && project.Subtitles != null && project.Subtitles.Count > 0)
            {
                var subsToBurn = project.Subtitles.OrderBy(s => s.StartSec).Take(20).ToList();
                int fontSize = targetH >= 1800 ? 42 : 30;
                var dtChain = new List<string>();

                foreach (var sub in subsToBurn)
                {
                    string safeText = EscapeDrawtext(sub.Text);
                    if (string.IsNullOrWhiteSpace(safeText)) continue;
                    string st = sub.StartSec.ToString("0.00", CultureInfo.InvariantCulture);
                    string en = sub.EndSec.ToString("0.00", CultureInfo.InvariantCulture);
                    dtChain.Add($"drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='{safeText}':fontcolor=white:fontsize={fontSize}:box=1:boxcolor=0x140E38@0.82:boxborderw=12:x=(w-text_w)/2:y=h-th-64:enable='between(t,{st},{en})'");
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

            string tempMainOut = (project?.ExternalMergedVideoPaths != null && project.ExternalMergedVideoPaths.Count > 0)
                ? Path.Combine(Path.GetDirectoryName(outputMp4) ?? "", "tmp_main_" + Path.GetFileName(outputMp4))
                : outputMp4;

            string args = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" {extraInputArgs}" +
                          $"-filter_complex \"{fc}\" -map \"[vfinal]\" -map \"[afinal]\" " +
                          $"-c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{tempMainOut}\"";

            bool ok = RunFfmpegBlocking(args, 180000);
            if (!ok || !File.Exists(tempMainOut))
            {
                string simpleArgs = $"-y -hide_banner -loglevel error -i \"{inputMp4}\" -vf \"scale={targetW}:{targetH}:flags=lanczos{colorFilter}\" -c:v {enc.CodecName} {enc.PresetArgs} -c:a copy \"{tempMainOut}\"";
                RunFfmpegBlocking(simpleArgs, 120000);
            }

            // Dışarıdan Eklenen Videolar Varsa Birleştir (Concat)
            if (project?.ExternalMergedVideoPaths != null && project.ExternalMergedVideoPaths.Count > 0 && File.Exists(tempMainOut))
            {
                MergeVideosWithExternalClips(tempMainOut, project.ExternalMergedVideoPaths, targetW, targetH, enc, outputMp4);
                try { if (tempMainOut != outputMp4) File.Delete(tempMainOut); } catch { }
            }
        }

        private static void MergeVideosWithExternalClips(
            string mainVideoPath,
            List<string> externalVideoPaths,
            int targetW,
            int targetH,
            HardwareEncoderInfo enc,
            string finalOutputMp4)
        {
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
                return;
            }

            var inSb = new StringBuilder();
            var fcSb = new StringBuilder();
            for (int i = 0; i < validFiles.Count; i++)
            {
                inSb.Append($"-i \"{validFiles[i]}\" ");
                fcSb.Append($"[{i}:v]scale={targetW}:{targetH}:force_original_aspect_ratio=decrease,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[mv{i}];");
            }
            for (int i = 0; i < validFiles.Count; i++)
            {
                fcSb.Append($"[mv{i}][{i}:a]");
            }
            fcSb.Append($"concat=n={validFiles.Count}:v=1:a=1[vmerged][amerged]");

            string args = $"-y -hide_banner -loglevel error {inSb}-filter_complex \"{fcSb}\" -map \"[vmerged]\" -map \"[amerged]\" -c:v {enc.CodecName} {enc.PresetArgs} -c:a aac -b:a 192k -movflags +faststart \"{finalOutputMp4}\"";
            if (!RunFfmpegBlocking(args, 180000) || !File.Exists(finalOutputMp4))
            {
                if (mainVideoPath != finalOutputMp4)
                    File.Copy(mainVideoPath, finalOutputMp4, true);
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
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(timeoutMs);
                return p.HasExited && p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
