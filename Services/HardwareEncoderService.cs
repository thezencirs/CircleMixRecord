// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleMixRecord.Services
{
    public class DirectShowDevice
    {
        public string FriendlyName { get; set; } = "";
        public string AlternativeName { get; set; } = "";
        public bool IsVideo { get; set; }
        public bool IsNvidiaBroadcast => FriendlyName.Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase)
                                      || FriendlyName.Contains("Broadcast", StringComparison.OrdinalIgnoreCase);
        public bool IsVirtual => FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                              || FriendlyName.Contains("OBS", StringComparison.OrdinalIgnoreCase);

        public int SortPriority => IsNvidiaBroadcast ? 0 : (IsVirtual ? 2 : 1);

        public string FfmpegInputIdentifier => !string.IsNullOrWhiteSpace(AlternativeName) ? AlternativeName : FriendlyName;
        public override string ToString() => IsNvidiaBroadcast ? $"⭐ {FriendlyName} (Varsayılan)" : FriendlyName;
    }

    public class HardwareEncoderInfo
    {
        public string CodecName { get; set; } = "libx264";
        public string DisplayName { get; set; } = "CPU x264 (Ultrafast)";
        public string PresetArgs { get; set; } = "-preset ultrafast -crf 20";
        public bool IsHardware { get; set; } = false;
    }

    public static class HardwareEncoderService
    {
        private static string _cachedFfmpegPath;
        private static HardwareEncoderInfo _cachedEncoder;

        public static string GetFfmpegPath()
        {
            if (!string.IsNullOrWhiteSpace(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
                return _cachedFfmpegPath;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(appDir, "ffmpeg.exe"),
                Path.Combine(appDir, "bin", "ffmpeg.exe"),
                @"C:\Users\zenci\AppData\Local\CircleRender\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CircleRender\bin\ffmpeg.exe"),
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    _cachedFfmpegPath = c;
                    return c;
                }
            }

            _cachedFfmpegPath = "ffmpeg.exe";
            return _cachedFfmpegPath;
        }

        public static string GetBaseStorageDir()
        {
            string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kayitlar_ve_Projeler");
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        public static string GetHorizontalRecordingsDir()
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Yatay_16x9_Kayitlar");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetVerticalRecordingsDir()
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Dikey_9x16_Reels_Kayitlar");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetEditedExportsDir()
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Kurgu_Ciktilar_Export");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetProjectInternalsDir(string projName)
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Proje_Verileri", projName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static HardwareEncoderInfo DetectBestEncoder()
        {
            if (_cachedEncoder != null)
                return _cachedEncoder;

            var candidates = new List<HardwareEncoderInfo>
            {
                new HardwareEncoderInfo
                {
                    CodecName = "h264_nvenc",
                    DisplayName = "NVIDIA NVENC Ultra HQ (CQP 16 • 0% CPU)",
                    PresetArgs = "-preset p4 -tune hq -rc vbr -cq 16 -b:v 0 -maxrate 25M -bufsize 50M",
                    IsHardware = true
                },
                new HardwareEncoderInfo
                {
                    CodecName = "h264_qsv",
                    DisplayName = "Intel QuickSync QSV HQ (Donanım Hızlandırma)",
                    PresetArgs = "-global_quality 17 -look_ahead 0",
                    IsHardware = true
                },
                new HardwareEncoderInfo
                {
                    CodecName = "h264_amf",
                    DisplayName = "AMD AMF HQ (Donanım Hızlandırma)",
                    PresetArgs = "-quality quality -rc cqp -qp_i 16 -qp_p 16",
                    IsHardware = true
                }
            };

            foreach (var enc in candidates)
            {
                if (TestEncoderWorks(enc.CodecName))
                {
                    _cachedEncoder = enc;
                    return enc;
                }
            }

            _cachedEncoder = new HardwareEncoderInfo
            {
                CodecName = "libx264",
                DisplayName = "CPU x264 HQ (Çok Çekirdekli Yedek)",
                PresetArgs = "-preset superfast -crf 17",
                IsHardware = false
            };
            return _cachedEncoder;
        }

        private static bool TestEncoderWorks(string codec)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GetFfmpegPath(),
                    Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.05 -frames:v 1 -pix_fmt yuv420p -c:v {codec} -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(3500);
                return proc.HasExited && proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static (List<DirectShowDevice> VideoDevices, List<DirectShowDevice> AudioDevices) EnumerateDirectShowDevices()
        {
            var videos = new List<DirectShowDevice>();
            var audios = new List<DirectShowDevice>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GetFfmpegPath(),
                    Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (videos, audios);

                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(3000);

                DirectShowDevice lastDevice = null;
                var lines = err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var nameRegex = new Regex("\"([^\"]+)\"\\s+\\((video|audio)\\)", RegexOptions.IgnoreCase);
                var altRegex = new Regex("Alternative name\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

                foreach (var line in lines)
                {
                    var mName = nameRegex.Match(line);
                    if (mName.Success)
                    {
                        string friendly = mName.Groups[1].Value;
                        bool isVideo = mName.Groups[2].Value.Equals("video", StringComparison.OrdinalIgnoreCase);
                        lastDevice = new DirectShowDevice
                        {
                            FriendlyName = friendly,
                            IsVideo = isVideo
                        };
                        if (isVideo) videos.Add(lastDevice);
                        else audios.Add(lastDevice);
                        continue;
                    }

                    var mAlt = altRegex.Match(line);
                    if (mAlt.Success && lastDevice != null)
                    {
                        lastDevice.AlternativeName = mAlt.Groups[1].Value;
                    }
                }
            }
            catch { }

            // NVIDIA Broadcast her zaman 1. sırada (Varsayılan), ardından fiziksel kameralar/mikrofonlar gelsin
            videos = videos.OrderBy(v => v.SortPriority).ToList();
            audios = audios.OrderBy(a => a.SortPriority).ToList();
            return (videos, audios);
        }
    }
}
