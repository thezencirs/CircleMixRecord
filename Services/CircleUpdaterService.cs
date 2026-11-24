// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace CircleMixRecord.Services
{
    public static class CircleUpdaterService
    {
        private const string Repo = "thezencirs/CircleMixRecord";
        private const string ExeName = "CircleStudio.exe";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CircleStudio-Updater/1.1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static void CleanupOldExecutable()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrWhiteSpace(currentExe))
                {
                    string old = currentExe + ".old";
                    if (File.Exists(old)) File.Delete(old);
                }
            }
            catch { }
        }

        /// <summary>
        /// Uygulama açılışında arka planda GitHub Releases (thezencirs/CircleMixRecord) üzerinden yeni sürüm kontrolü yapar.
        /// Yeni sürüm yayınlandığında karşı tarafa otomatik bildirim çıkarır ve tek tıkla SHA-256 doğrulamalı günceller.
        /// </summary>
        public static void CheckForUpdatesInBackground(Window owner, bool manual = false)
        {
            Task.Run(async () =>
            {
                if (!manual) await Task.Delay(3500);
                try
                {
                    string json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                    var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!tagMatch.Success) return;

                    string tag = tagMatch.Groups[1].Value.Trim();
                    if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latestVer)) return;
                    var normLatest = new Version(latestVer.Major, latestVer.Minor, Math.Max(0, latestVer.Build));

                    string exeUrl = null;
                    string sumsUrl = null;
                    foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                    {
                        string u = m.Groups[1].Value;
                        if (u.EndsWith("/CircleStudio.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = u;
                        else if (u.EndsWith("/SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = u;
                    }

                    if (normLatest > CurrentVersion && !string.IsNullOrEmpty(exeUrl))
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(async () =>
                        {
                            var res = System.Windows.MessageBox.Show(
                                owner,
                                $"Yeni bir Circle Studio güncellemesi yayınlandı!\n\n" +
                                $"• Mevcut Sürümünüz: v{CurrentVersion}\n" +
                                $"• Yeni Sürüm: v{normLatest}\n\n" +
                                "Kayıtlarınız ve lisansınız korunarak şimdi otomatik güncellensin mi?",
                                "Circle Studio — Otomatik Güncelleme (by thezencirs)",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Information);

                            if (res == MessageBoxResult.Yes)
                            {
                                var (ok, err) = await DownloadAndApplyUpdateAsync(exeUrl, sumsUrl);
                                if (ok)
                                {
                                    System.Windows.Application.Current.Shutdown();
                                }
                                else
                                {
                                    System.Windows.MessageBox.Show(owner, "Güncelleme uygulanamadı:\n" + err, "Circle Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                        });
                    }
                    else if (manual)
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show(owner, $"Circle Studio güncel (v{CurrentVersion}).", "Circle Studio — by thezencirs", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (manual)
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show(owner, "Güncelleme sunucusuna ulaşılamadı:\n" + ex.Message, "Circle Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
            });
        }

        private static async Task<(bool ok, string error)> DownloadAndApplyUpdateAsync(string exeUrl, string sumsUrl)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CircleStudio_Update_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                string newExe = Path.Combine(tempDir, ExeName);

                byte[] exeBytes = await Http.GetByteArrayAsync(exeUrl);
                if (exeBytes.Length < 100 * 1024)
                    return (false, "İndirilen güncelleme dosyası geçersiz boyutta.");
                await File.WriteAllBytesAsync(newExe, exeBytes);

                if (!string.IsNullOrEmpty(sumsUrl))
                {
                    string sumsText = await Http.GetStringAsync(sumsUrl);
                    string expectedHash = null;
                    foreach (string line in sumsText.Split('\n'))
                    {
                        var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                        if (m.Success && string.Equals(m.Groups[2].Value.Trim(), ExeName, StringComparison.OrdinalIgnoreCase))
                        {
                            expectedHash = m.Groups[1].Value.ToLowerInvariant();
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        using var sha = SHA256.Create();
                        string actualHash = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                            return (false, "SHA-256 güvenlik özeti doğrulaması başarısız oldu.");
                    }
                }

                string currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                    return (false, "Çalışan uygulama yolu belirlenemedi.");

                string oldExe = currentExe + ".old";
                if (File.Exists(oldExe)) File.Delete(oldExe);
                File.Move(currentExe, oldExe);
                try
                {
                    File.Copy(newExe, currentExe, overwrite: true);
                }
                catch
                {
                    File.Move(oldExe, currentExe);
                    throw;
                }

                Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = false });
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
