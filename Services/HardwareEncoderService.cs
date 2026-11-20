// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleMixRecord.Services
{
    public class DirectShowDevice
    {
        public string FriendlyName { get; set; } = "";
        public string AlternativeName { get; set; } = "";
        public bool IsVideo { get; set; }
        public bool IsWirelessStudioMic => !IsVideo && (
            FriendlyName.Contains("Wireless Mic", StringComparison.OrdinalIgnoreCase) ||
            FriendlyName.Contains("Wireless", StringComparison.OrdinalIgnoreCase));
        public bool IsStudioBroadcast => FriendlyName.Contains("Broadcast", StringComparison.OrdinalIgnoreCase);
        public bool IsVirtual => FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        public int SortPriority => IsWirelessStudioMic ? -1 : (IsStudioBroadcast ? 0 : (IsVirtual ? 2 : 1));

        public string FfmpegInputIdentifier => !string.IsNullOrWhiteSpace(AlternativeName) ? AlternativeName : FriendlyName;
        public override string ToString() => IsWirelessStudioMic
            ? $"🎙️ {FriendlyName} (Kablosuz Stüdyo • #1)"
            : (IsStudioBroadcast ? $"⭐ {FriendlyName} (Varsayılan)" : FriendlyName);
    }

    public class HardwareEncoderInfo
    {
        public string CodecName { get; set; } = "h264_mf";
        public string DisplayName { get; set; } = "Windows Media Foundation H.264";
        public string PresetArgs { get; set; } = "-b:v 12M";
        public bool IsHardware { get; set; } = false;
    }

    public static class HardwareEncoderService
    {
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder packageFullName);

        private static string _cachedFfmpegPath;
        private static string _cachedBaseStorageDir;
        private static HardwareEncoderInfo _cachedEncoder;

        public static bool IsRunningAsMsixPackage()
        {
            try
            {
                int length = 0;
                int rc = GetCurrentPackageFullName(ref length, null);
                return rc != APPMODEL_ERROR_NO_PACKAGE && length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDirectoryWritable(string dirPath)
        {
            try
            {
                Directory.CreateDirectory(dirPath);
                string probe = Path.Combine(dirPath, $".cmr_write_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string GetFfmpegPath()
        {
            if (!string.IsNullOrWhiteSpace(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
                return _cachedFfmpegPath;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(appDir, "bin", "ffmpeg.exe"),
                Path.Combine(appDir, "ffmpeg.exe"),
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
            if (!string.IsNullOrWhiteSpace(_cachedBaseStorageDir))
            {
                Directory.CreateDirectory(_cachedBaseStorageDir);
                return _cachedBaseStorageDir;
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            bool inProtectedInstallDir = IsRunningAsMsixPackage()
                || appDir.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(progFiles) && appDir.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(progFilesX86) && appDir.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase));

            if (!inProtectedInstallDir)
            {
                string portableDir = Path.Combine(appDir, "Kayitlar_ve_Projeler");
                if (IsDirectoryWritable(portableDir))
                {
                    _cachedBaseStorageDir = portableDir;
                    return _cachedBaseStorageDir;
                }
            }

            string videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videosRoot))
                videosRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            _cachedBaseStorageDir = Path.Combine(videosRoot, "CircleMixRecord");
            Directory.CreateDirectory(_cachedBaseStorageDir);
            return _cachedBaseStorageDir;
        }

        public static string GetHorizontalRecordingsDir()
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Yatay_16x9_Kayitlar");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetVerticalRecordingsDir()
        {
            string dir = Path.Combine(GetBaseStorageDir(), "Dikey_9x16_Kayitlar");
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
                    DisplayName = "Circle GPU Ultra HQ (CQP 16 • 0% CPU)",
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
                },
                new HardwareEncoderInfo
                {
                    CodecName = "h264_mf",
                    DisplayName = "Windows Media Foundation H.264 HQ",
                    PresetArgs = "-b:v 12M",
                    IsHardware = false
                },
                new HardwareEncoderInfo
                {
                    CodecName = "libopenh264",
                    DisplayName = "Cisco OpenH264 HQ (CPU)",
                    PresetArgs = "-b:v 10M -maxrate 16M",
                    IsHardware = false
                },
                new HardwareEncoderInfo
                {
                    CodecName = "libx264",
                    DisplayName = "CPU x264 HQ (Çok Çekirdekli Yedek)",
                    PresetArgs = "-preset superfast -crf 17",
                    IsHardware = false
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
                CodecName = "mpeg4",
                DisplayName = "CPU MPEG-4 (Evrensel Yedek)",
                PresetArgs = "-qscale:v 3",
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

            // Circle Broadcast her zaman 1. sırada (Varsayılan), ardından fiziksel kameralar/mikrofonlar gelsin
            videos = videos.OrderBy(v => v.SortPriority).ToList();
            audios = audios.OrderBy(a => a.SortPriority).ToList();
            return (videos, audios);
        }
    }
}

