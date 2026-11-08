// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircleMixRecord.Models;

namespace CircleMixRecord.Services
{
    public static class AutoCutSilenceService
    {
        public static List<SilenceRegion> AnalyzeMicrophoneSilence(
            CmrProject project,
            double thresholdDb,
            double minSilenceDurationSec,
            int wordPaddingMs)
        {
            var silences = new List<SilenceRegion>();
            if (project == null) return silences;

            project.SilenceThresholdDb = thresholdDb;
            project.SilenceMinDurationSec = minSilenceDurationSec;
            project.SilencePaddingMs = wordPaddingMs;

            double padSec = Math.Max(0, wordPaddingMs) / 1000.0;

            // 1. Önce ayrık Master_Mic.wav varsa doğrudan PCM örneklerinden ultra-hızlı analiz yap
            if (!string.IsNullOrWhiteSpace(project.MicrophoneWavPath) && File.Exists(project.MicrophoneWavPath))
            {
                silences = AnalyzePcmWavFile(project.MicrophoneWavPath, thresholdDb, minSilenceDurationSec, padSec, out var peaks, out double wavDur);
                if (peaks.Count > 0)
                    project.WaveformPeaks = peaks;
                if (wavDur > 0.5)
                    project.TotalDurationSec = wavDur;
            }
            // 2. Harici bir video/ses dosyası yüklendiyse FFmpeg PCM ve silencedetect ile gerçek analiz yap
            else
            {
                string mediaSrc = !string.IsNullOrWhiteSpace(project.Horizontal16x9VideoPath) && File.Exists(project.Horizontal16x9VideoPath)
                    ? project.Horizontal16x9VideoPath
                    : project.Vertical9x16VideoPath;

                if (!string.IsNullOrWhiteSpace(mediaSrc) && File.Exists(mediaSrc))
                {
                    var realPeaks = ExtractRealWaveformPeaks(mediaSrc, out double realDur, 480);
                    if (realPeaks.Count > 0)
                        project.WaveformPeaks = realPeaks;
                    if (realDur > 0.5)
                        project.TotalDurationSec = realDur;

                    silences = AnalyzeViaFfmpegSilenceDetect(mediaSrc, thresholdDb, minSilenceDurationSec, padSec);
                }
            }

            project.DetectedSilences = silences;
            return silences;
        }

        public static void ApplySilenceActionToTimeline(
            CmrProject project,
            SilenceActionMode mode,
            double deadAirSpeed = 8.0)
        {
            if (project == null) return;
            project.LastSilenceMode = mode;
            project.DeadAirSpeedMultiplier = deadAirSpeed;

            double totalDur = Math.Max(1.0, project.TotalDurationSec);
            var clips = new List<TimelineClip>();

            if (mode == SilenceActionMode.KeepNormal || project.DetectedSilences == null || project.DetectedSilences.Count == 0)
            {
                clips.Add(new TimelineClip
                {
                    Label = "Tam Kayıt (Kurgusuz)",
                    SourceStartSec = 0,
                    SourceEndSec = totalDur,
                    SpeedMultiplier = 1.0,
                    IsDeleted = false,
                    IsSilentSegment = false
                });
                project.Clips = clips;
                return;
            }

            double cursor = 0.0;
            int speechIdx = 1;
            int silenceIdx = 1;

            foreach (var s in project.DetectedSilences.OrderBy(x => x.StartSec))
            {
                double sStart = Math.Clamp(s.StartSec, cursor, totalDur);
                double sEnd = Math.Clamp(s.EndSec, sStart, totalDur);

                // Sessizlikten önceki konuşma bloğu
                if (sStart - cursor >= 0.08)
                {
                    clips.Add(new TimelineClip
                    {
                        Label = $"Konuşma #{speechIdx++}",
                        SourceStartSec = cursor,
                        SourceEndSec = sStart,
                        SpeedMultiplier = 1.0,
                        IsDeleted = false,
                        IsSilentSegment = false
                    });
                }

                // Sessizlik bloğu (Ripple Delete veya 8x Hızlandırma)
                if (sEnd - sStart >= 0.10)
                {
                    clips.Add(new TimelineClip
                    {
                        Label = mode == SilenceActionMode.RippleDelete
                            ? $"Sessizlik #{silenceIdx++} (Kesildi)"
                            : $"3D İşlem #{silenceIdx++} ({deadAirSpeed:0.#}x Hızlı)",
                        SourceStartSec = sStart,
                        SourceEndSec = sEnd,
                        SpeedMultiplier = mode == SilenceActionMode.SpeedUpDeadAir ? deadAirSpeed : 1.0,
                        IsDeleted = (mode == SilenceActionMode.RippleDelete),
                        IsSilentSegment = true
                    });
                }

                cursor = sEnd;
            }

            // Son konuşma bloğu
            if (totalDur - cursor >= 0.08)
            {
                clips.Add(new TimelineClip
                {
                    Label = $"Konuşma #{speechIdx}",
                    SourceStartSec = cursor,
                    SourceEndSec = totalDur,
                    SpeedMultiplier = 1.0,
                    IsDeleted = false,
                    IsSilentSegment = false
                });
            }

            project.Clips = clips;
        }

        public static void ApplyTargetDurationCompression(CmrProject project, double targetSeconds)
        {
            if (project == null || project.Clips.Count == 0 || targetSeconds < 3.0) return;

            double currentActiveDuration = project.Clips.Where(c => !c.IsDeleted).Sum(c => c.SourceDurationSec);
            if (currentActiveDuration <= 0.1) return;

            double requiredSpeed = Math.Clamp(currentActiveDuration / targetSeconds, 0.5, 16.0);
            foreach (var c in project.Clips.Where(c => !c.IsDeleted))
            {
                c.SpeedMultiplier = Math.Round(requiredSpeed, 2);
                c.Label = $"{c.Label.Split('(')[0].Trim()} ({c.SpeedMultiplier:0.##}x)";
            }
        }

        private static List<SilenceRegion> AnalyzePcmWavFile(
            string wavPath,
            double thresholdDb,
            double minSilenceSec,
            double paddingSec,
            out List<float> waveformPeaks,
            out double totalDurationSec)
        {
            var regions = new List<SilenceRegion>();
            waveformPeaks = new List<float>();
            totalDurationSec = 0.0;

            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= 44) return regions;

            using var br = new BinaryReader(fs);
            br.ReadBytes(22);
            short channels = br.ReadInt16();
            int sampleRate = br.ReadInt32();
            br.ReadInt32(); // byteRate
            short blockAlign = br.ReadInt16();
            short bitsPerSample = br.ReadInt16();

            if (sampleRate <= 0) sampleRate = 48000;
            if (channels <= 0) channels = 2;

            fs.Seek(44, SeekOrigin.Begin);
            long dataBytes = fs.Length - 44;
            int bytesPerFrame = Math.Max(2, channels * (bitsPerSample / 8));
            long totalFrames = dataBytes / bytesPerFrame;
            totalDurationSec = (double)totalFrames / sampleRate;

            // 25 ms pencereler halinde RMS/Peak dB ölç
            int windowFrames = Math.Max(1, sampleRate / 40);
            int windowBytes = windowFrames * bytesPerFrame;
            byte[] buf = new byte[windowBytes];

            double linearThreshold = Math.Pow(10.0, thresholdDb / 20.0);
            bool inSilence = false;
            double silenceStart = 0.0;
            double dbSum = 0.0;
            int dbCount = 0;

            long frameCursor = 0;
            while (frameCursor < totalFrames)
            {
                int read = fs.Read(buf, 0, windowBytes);
                if (read <= 0) break;

                int framesInWin = read / bytesPerFrame;
                float peak = 0f;
                double sumSq = 0.0;

                for (int i = 0; i < framesInWin; i++)
                {
                    short s16 = BitConverter.ToInt16(buf, i * bytesPerFrame);
                    float norm = Math.Abs(s16 / 32768f);
                    if (norm > peak) peak = norm;
                    sumSq += norm * norm;
                }

                waveformPeaks.Add(peak);
                double rms = framesInWin > 0 ? Math.Sqrt(sumSq / framesInWin) : 0.0;
                double winDb = rms > 0.00001 ? 20.0 * Math.Log10(rms) : -65.0;

                double tStart = (double)frameCursor / sampleRate;
                double tEnd = (double)(frameCursor + framesInWin) / sampleRate;
                frameCursor += framesInWin;

                bool isWinSilent = (rms < linearThreshold);
                if (isWinSilent)
                {
                    if (!inSilence)
                    {
                        inSilence = true;
                        silenceStart = tStart;
                        dbSum = winDb;
                        dbCount = 1;
                    }
                    else
                    {
                        dbSum += winDb;
                        dbCount++;
                    }
                }
                else if (inSilence)
                {
                    inSilence = false;
                    double paddedStart = silenceStart + paddingSec;
                    double paddedEnd = tStart - paddingSec;
                    if (paddedEnd - paddedStart >= minSilenceSec)
                    {
                        regions.Add(new SilenceRegion
                        {
                            StartSec = Math.Round(paddedStart, 3),
                            EndSec = Math.Round(paddedEnd, 3),
                            AvgDb = dbCount > 0 ? Math.Round(dbSum / dbCount, 1) : -55.0,
                            IsSilent = true
                        });
                    }
                }
            }

            if (inSilence)
            {
                double paddedStart = silenceStart + paddingSec;
                double paddedEnd = totalDurationSec - paddingSec * 0.5;
                if (paddedEnd - paddedStart >= minSilenceSec)
                {
                    regions.Add(new SilenceRegion
                    {
                        StartSec = Math.Round(paddedStart, 3),
                        EndSec = Math.Round(paddedEnd, 3),
                        AvgDb = dbCount > 0 ? Math.Round(dbSum / dbCount, 1) : -55.0,
                        IsSilent = true
                    });
                }
            }

            return regions;
        }

        private static List<SilenceRegion> AnalyzeViaFfmpegSilenceDetect(
            string mediaPath,
            double thresholdDb,
            double minSilenceSec,
            double paddingSec)
        {
            var regions = new List<SilenceRegion>();
            try
            {
                string thStr = thresholdDb.ToString("0.0", CultureInfo.InvariantCulture);
                string durStr = minSilenceSec.ToString("0.00", CultureInfo.InvariantCulture);

                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-hide_banner -i \"{mediaPath}\" -af \"silencedetect=noise={thStr}dB:d={durStr}\" -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return regions;

                string output = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);

                var startRe = new Regex(@"silence_start:\s*([0-9\.]+)");
                var endRe = new Regex(@"silence_end:\s*([0-9\.]+)");

                double curStart = -1;
                foreach (var line in output.Split('\n'))
                {
                    var ms = startRe.Match(line);
                    if (ms.Success && double.TryParse(ms.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double st))
                    {
                        curStart = st;
                    }
                    var me = endRe.Match(line);
                    if (me.Success && curStart >= 0 && double.TryParse(me.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double en))
                    {
                        double pStart = curStart + paddingSec;
                        double pEnd = en - paddingSec;
                        if (pEnd - pStart >= minSilenceSec)
                        {
                            regions.Add(new SilenceRegion
                            {
                                StartSec = Math.Round(pStart, 3),
                                EndSec = Math.Round(pEnd, 3),
                                AvgDb = thresholdDb - 8.0,
                                IsSilent = true
                            });
                        }
                        curStart = -1;
                    }
                }
            }
            catch { }

            return regions;
        }

        /// <summary>
        /// Herhangi bir MP4 / MKV / MOV / WAV dosyasından FFmpeg aracılığıyla 16-bit PCM ses örneklerini
        /// belleğe okuyarak %100 gerçek ses dalga formu (Waveform) tepelerini çıkarır.
        /// </summary>
        public static List<float> ExtractRealWaveformPeaks(string mediaPath, out double durationSec, int targetBars = 480)
        {
            durationSec = 0.0;
            var peaks = new List<float>();
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return peaks;

            try
            {
                const int sampleRate = 1000; // 1000 Hz mono PCM -> 1ms çözünürlük, ultra hızlı
                var psi = new ProcessStartInfo
                {
                    FileName = HardwareEncoderService.GetFfmpegPath(),
                    Arguments = $"-hide_banner -loglevel error -i \"{mediaPath}\" -vn -ac 1 -ar {sampleRate} -f s16le -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return peaks;

                using var ms = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(ms);
                proc.WaitForExit(12000);

                byte[] raw = ms.ToArray();
                int totalSamples = raw.Length / 2;
                if (totalSamples <= 10) return peaks;

                durationSec = (double)totalSamples / sampleRate;
                int bars = Math.Clamp(targetBars, 64, 1200);
                int samplesPerBar = Math.Max(1, totalSamples / bars);

                for (int b = 0; b < bars; b++)
                {
                    int startSample = b * samplesPerBar;
                    if (startSample >= totalSamples) break;
                    int endSample = Math.Min(totalSamples, startSample + samplesPerBar);

                    float maxPeak = 0f;
                    for (int s = startSample; s < endSample; s++)
                    {
                        short val = BitConverter.ToInt16(raw, s * 2);
                        float norm = Math.Abs(val / 32768f);
                        if (norm > maxPeak) maxPeak = norm;
                    }
                    peaks.Add(Math.Clamp(maxPeak, 0.03f, 1.0f));
                }
            }
            catch { }

            return peaks;
        }
    }
}
