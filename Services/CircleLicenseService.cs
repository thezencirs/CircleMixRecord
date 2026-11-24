// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CircleMixRecord.Services
{
    public class ManagedLicenseEntry
    {
        public string Key { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Status { get; set; } = "active"; // "active" | "revoked"
        public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
        public string ExpiresAt { get; set; } = "2027-12-31";

        public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
        public string DisplayText => $"{(IsActive ? "✅ AKTİF  " : "⛔ İPTAL  ")}  |  {Key}  |  {Owner}  (Bitiş: {ExpiresAt})";
    }

    public class ManagedLicenseRegistry
    {
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("O");
        public List<ManagedLicenseEntry> Licenses { get; set; } = new();
    }

    public static class CircleLicenseService
    {
        private const string MasterSecret = "THEZENCIRS_CIRCLE_STUDIO_MASTER_2026_PRO_KEY";
        private const string RepoOwner = "thezencirs";
        private const string RepoName = "CircleMixRecord";
        private static readonly HttpClient Http = CreateHttpClient();

        public static string ActiveLicenseKey { get; private set; } = "";
        public static string ActiveLicenseStatus { get; private set; } = "PRO Lisans • Etkin";

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CircleStudio-LicenseEngine/1.1.0");
            client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            return client;
        }

        private static string GetLicenseFilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CircleStudio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "license.dat");
        }

        public static string GetLocalRegistryPath()
        {
            string repoPath = @"C:\Users\zenci\.gemini\antigravity\scratch\repos\CircleMixRecord\licenses.json";
            if (Directory.Exists(Path.GetDirectoryName(repoPath)))
                return repoPath;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CircleStudio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "licenses.json");
        }

        public static bool IsFounderMachine()
        {
            string user = Environment.UserName ?? "";
            return user.Equals("zenci", StringComparison.OrdinalIgnoreCase);
        }

        public static string GenerateLicenseKey(string seedSegment = null)
        {
            string seg = string.IsNullOrWhiteSpace(seedSegment)
                ? Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant()
                : Regex.Replace(seedSegment.ToUpperInvariant(), "[^A-Z0-9]", "").PadRight(4, 'X').Substring(0, 4);

            string sig = ComputeSignature(seg);
            return $"CST-2026-{seg}-{sig}";
        }

        private static string ComputeSignature(string segment4)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(MasterSecret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes("CST-2026-" + segment4.ToUpperInvariant()));
            const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
            var sb = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(alphabet[hash[i] % alphabet.Length]);
            }
            return sb.ToString();
        }

        public static bool ValidateKeyFormatAndSignature(string rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return false;
            string k = rawKey.Trim().ToUpperInvariant();

            if (k == "CST-2026-ZENC-PRO1")
                return true;

            var m = Regex.Match(k, @"^CST-2026-([A-Z0-9]{4})-([A-Z0-9]{4})$");
            if (m.Success)
            {
                string seg = m.Groups[1].Value;
                string sig = m.Groups[2].Value;
                return string.Equals(ComputeSignature(seg), sig, StringComparison.OrdinalIgnoreCase);
            }

            if (Regex.IsMatch(k, @"^CPM-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
                return true;

            return false;
        }

        /// <summary>
        /// Buluttaki (GitHub thezencirs/CircleMixRecord/licenses.json) canlı lisans durumunu sorgular.
        /// Eğer yönetici (thezencirs) lisansı iptal ettiyse ("revoked"), karşı tarafın uygulamasını anında kilitler.
        /// </summary>
        public static async Task<(bool allowed, string reason)> CheckOnlineLicenseStatusAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return (false, "Lisans anahtarı boş.");
            string k = key.Trim().ToUpperInvariant();
            if (!ValidateKeyFormatAndSignature(k))
                return (false, "Geçersiz lisans anahtarı imzası.");

            try
            {
                var reg = await FetchOnlineRegistryAsync();
                if (reg?.Licenses != null && reg.Licenses.Count > 0)
                {
                    var entry = reg.Licenses.FirstOrDefault(x => string.Equals(x.Key?.Trim(), k, StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        if (!entry.IsActive)
                            return (false, $"Bu lisans anahtarı ({k}) yönetici (thezencirs) tarafından iptal edilmiş veya askıya alınmıştır.");
                        if (DateTime.TryParse(entry.ExpiresAt, out var exp) && exp.Date < DateTime.UtcNow.Date)
                            return (false, $"Bu lisans anahtarının ({k}) kullanım süresi ({entry.ExpiresAt}) dolmuştur.");
                        return (true, $"PRO Lisans • {entry.Owner}");
                    }
                }
            }
            catch { }

            return (true, "Circle Studio PRO");
        }

        public static async Task<ManagedLicenseRegistry> FetchOnlineRegistryAsync()
        {
            // 1. Önce GitHub API üzerinden sıfır önbellekle (anlık canlı durum) çek
            try
            {
                string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/contents/licenses.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github.v3.raw");
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    var reg = JsonSerializer.Deserialize<ManagedLicenseRegistry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (reg != null) return reg;
                }
            }
            catch { }

            // 2. Yedek: raw.githubusercontent.com
            try
            {
                string rawUrl = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/main/licenses.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                string json = await Http.GetStringAsync(rawUrl);
                var reg = JsonSerializer.Deserialize<ManagedLicenseRegistry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (reg != null) return reg;
            }
            catch { }

            return LoadLocalRegistry();
        }

        public static ManagedLicenseRegistry LoadLocalRegistry()
        {
            string path = GetLocalRegistryPath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var reg = JsonSerializer.Deserialize<ManagedLicenseRegistry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (reg != null && reg.Licenses != null) return reg;
                }
                catch { }
            }

            var def = new ManagedLicenseRegistry
            {
                UpdatedAt = DateTime.UtcNow.ToString("O"),
                Licenses = new List<ManagedLicenseEntry>
                {
                    new ManagedLicenseEntry { Key = GenerateLicenseKey("8F4A"), Owner = "Kullanıcı #1 (PRO Lisans)", Status = "active", CreatedAt = "2026-11-24", ExpiresAt = "2027-11-24" },
                    new ManagedLicenseEntry { Key = GenerateLicenseKey("M7K9"), Owner = "Kullanıcı #2 (Yedek Lisans)", Status = "active", CreatedAt = "2026-11-24", ExpiresAt = "2027-11-24" },
                    new ManagedLicenseEntry { Key = GenerateLicenseKey("PRO1"), Owner = "VIP / Kurumsal PRO Lisans", Status = "active", CreatedAt = "2026-11-24", ExpiresAt = "2028-11-24" }
                }
            };
            SaveLocalRegistry(def);
            return def;
        }

        public static void SaveLocalRegistry(ManagedLicenseRegistry reg)
        {
            reg.UpdatedAt = DateTime.UtcNow.ToString("O");
            string json = JsonSerializer.Serialize(reg, new JsonSerializerOptions { WriteIndented = true });
            string path = GetLocalRegistryPath();
            File.WriteAllText(path, json, new UTF8Encoding(false));

            string scratchPath = @"C:\Users\zenci\.gemini\antigravity\scratch\CircleMixRecord\licenses.json";
            if (Directory.Exists(Path.GetDirectoryName(scratchPath)))
            {
                try { File.WriteAllText(scratchPath, json, new UTF8Encoding(false)); } catch { }
            }
        }

        /// <summary>
        /// Kurucu bilgisayarda yapılan tek tuşla Aktif Et / İptal Et / Yeni Lisans değişikliklerini
        /// doğrudan GitHub bulutuna (thezencirs/CircleMixRecord/licenses.json) anında senkronize eder.
        /// </summary>
        public static async Task<(bool ok, string msg)> PushRegistryToCloudAsync(ManagedLicenseRegistry reg)
        {
            SaveLocalRegistry(reg);
            try
            {
                string repoDir = @"C:\Users\zenci\.gemini\antigravity\scratch\repos\CircleMixRecord";
                if (!Directory.Exists(repoDir))
                    return (false, "Yerel GitHub deposu bulunamadı.");

                return await Task.Run(() =>
                {
                    RunGit(repoDir, "add licenses.json");
                    RunGit(repoDir, $"commit -m \"chore(license): update online license registry ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\"");
                    var (code, output) = RunGit(repoDir, "push origin main");
                    if (code == 0)
                        return (true, "Bulut lisans sunucusu (GitHub) anında güncellendi.");
                    return (false, "Yerel kaydedildi, bulut senkron uyarısı: " + output);
                });
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static (int code, string output) RunGit(string workDir, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git.exe",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "git başlatılamadı");
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return (p.ExitCode, (stdout + "\n" + stderr).Trim());
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        public static string GetHardwareFingerprint()
        {
            string raw = $"{Environment.MachineName}|{Environment.UserName}|{Environment.ProcessorCount}|{Environment.OSVersion}";
            using var sha = SHA256.Create();
            byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(b, 0, 6).Replace("-", "");
        }

        public static bool EnsureLicensed(Window owner = null)
        {
            if (IsFounderMachine())
            {
                ActiveLicenseKey = GenerateLicenseKey("ZENC");
                ActiveLicenseStatus = "Master Stüdyo Lisansı (thezencirs)";
                return true;
            }

            string licPath = GetLicenseFilePath();
            if (File.Exists(licPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(licPath, Encoding.UTF8);
                    if (lines.Length >= 1 && ValidateKeyFormatAndSignature(lines[0]))
                    {
                        string savedKey = lines[0].Trim().ToUpperInvariant();
                        var (allowed, reason) = Task.Run(() => CheckOnlineLicenseStatusAsync(savedKey)).GetAwaiter().GetResult();
                        if (allowed)
                        {
                            ActiveLicenseKey = savedKey;
                            ActiveLicenseStatus = $"{reason} ({ActiveLicenseKey})";
                            StartBackgroundRevocationMonitor(owner);
                            return true;
                        }
                        else
                        {
                            // Yönetici lisansı iptal ettiyse yerel kaydı sil ve uyarı göster!
                            try { File.Delete(licPath); } catch { }
                            System.Windows.MessageBox.Show(
                                reason + "\n\nLütfen geçerli bir lisans anahtarı girin veya thezencirs ile iletişime geçin.",
                                "Circle Studio — Lisans Durumu",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                    }
                }
                catch { }
            }

            bool activated = ShowActivationDialog(owner);
            if (activated)
            {
                StartBackgroundRevocationMonitor(owner);
            }
            return activated;
        }

        private static void StartBackgroundRevocationMonitor(Window owner)
        {
            if (IsFounderMachine()) return;
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(3));
                    if (string.IsNullOrWhiteSpace(ActiveLicenseKey)) break;
                    var (allowed, reason) = await CheckOnlineLicenseStatusAsync(ActiveLicenseKey);
                    if (!allowed)
                    {
                        try { File.Delete(GetLicenseFilePath()); } catch { }
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show(
                                reason + "\n\nUygulama şimdi kapatılacak.",
                                "Circle Studio — Lisans İptal Edildi",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            System.Windows.Application.Current.Shutdown();
                        });
                        break;
                    }
                }
            });
        }

        public static bool ShowActivationDialog(Window owner = null)
        {
            var bgCanvas = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 12, 49));   // #120C31
            var bgCard = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));    // #261A66
            var borderBr = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 37, 128));  // #342580
            var orangeBr = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));  // #EF5F18
            var whiteBr = System.Windows.Media.Brushes.White;                                     // #FFFFFF
            var mutedBr = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 255, 255, 255));

            var win = new Window
            {
                Title = "Circle Studio — Lisans Etkinleştirme (by thezencirs)",
                Width = 490,
                Height = 320,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = bgCanvas,
                Owner = owner
            };

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "◯  Circle Studio PRO — Online Lisans Doğrulama",
                Foreground = whiteBr,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(title, 0);

            var sub = new TextBlock
            {
                Text = $"by thezencirs • Donanım Kimliği (HWID): {GetHardwareFingerprint()}\nLütfen size iletilen Circle Studio PRO lisans anahtarını girin (otomatik kaydedilir):",
                Foreground = mutedBr,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(sub, 1);

            var txtKey = new System.Windows.Controls.TextBox
            {
                Text = "",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = whiteBr,
                Background = bgCard,
                BorderBrush = borderBr,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                CaretBrush = whiteBr
            };
            Grid.SetRow(txtKey, 2);

            var lblErr = new TextBlock
            {
                Text = "",
                Foreground = orangeBr,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(lblErr, 3);

            var btnPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var btnCancel = new System.Windows.Controls.Button
            {
                Content = "Çıkış",
                Width = 90,
                Height = 36,
                Margin = new Thickness(0, 0, 10, 0),
                Background = bgCard,
                Foreground = whiteBr,
                BorderBrush = borderBr,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnCancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };

            var btnOk = new System.Windows.Controls.Button
            {
                Content = "Lisansı Etkinleştir",
                Width = 165,
                Height = 36,
                Background = orangeBr,
                Foreground = whiteBr,
                BorderBrush = orangeBr,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = true
            };

            btnOk.Click += async (_, _) =>
            {
                string candidate = (txtKey.Text ?? "").Trim().ToUpperInvariant();
                if (!ValidateKeyFormatAndSignature(candidate))
                {
                    lblErr.Text = "Geçersiz lisans anahtarı. Lütfen CST-2026-XXXX-XXXX biçimindeki anahtarı kontrol edin.";
                    return;
                }

                btnOk.IsEnabled = false;
                btnOk.Content = "Doğrulanıyor...";
                var (allowed, reason) = await CheckOnlineLicenseStatusAsync(candidate);
                btnOk.IsEnabled = true;
                btnOk.Content = "Lisansı Etkinleştir";

                if (!allowed)
                {
                    lblErr.Text = reason;
                    return;
                }

                try
                {
                    File.WriteAllLines(GetLicenseFilePath(), new[]
                    {
                        candidate,
                        GetHardwareFingerprint(),
                        DateTime.UtcNow.ToString("O")
                    }, Encoding.UTF8);
                }
                catch { }

                ActiveLicenseKey = candidate;
                ActiveLicenseStatus = $"{reason} ({ActiveLicenseKey})";
                win.DialogResult = true;
                win.Close();
            };

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnOk);
            Grid.SetRow(btnPanel, 4);

            root.Children.Add(title);
            root.Children.Add(sub);
            root.Children.Add(txtKey);
            root.Children.Add(lblErr);
            root.Children.Add(btnPanel);
            win.Content = root;

            return win.ShowDialog() == true;
        }

        /// <summary>
        /// Yalnızca Kurucu Bilgisayarda (zenci) açılan Tek Tuşla Uzaktan Lisans Üretme, Aktif Etme ve İptal Etme Yönetim Paneli.
        /// </summary>
        public static void ShowFounderLicenseAdminWindow(Window owner)
        {
            if (!IsFounderMachine()) return;

            var bgCanvas = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 12, 49));   // #120C31
            var bgCard = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 26, 102));    // #261A66
            var borderBr = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 37, 128));  // #342580
            var orangeBr = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 95, 24));  // #EF5F18
            var whiteBr = System.Windows.Media.Brushes.White;                                     // #FFFFFF
            var mutedBr = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255));

            var reg = LoadLocalRegistry();

            var win = new Window
            {
                Title = "Circle Studio — Tek Tuş Lisans & Güncelleme Yönetim Merkezi (thezencirs)",
                Width = 720,
                Height = 520,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = bgCanvas,
                Owner = owner
            };

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "◯  Circle Studio PRO — Uzaktan Lisans & Güncelleme Kontrol Paneli",
                Foreground = whiteBr,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(header, 0);

            var subHeader = new TextBlock
            {
                Text = "Buradan ürettiğiniz, iptal ettiğiniz veya aktif ettiğiniz lisanslar tek tuşla buluta (GitHub) işlenir ve karşı tarafın bilgisayarında anında geçerli olur.",
                Foreground = mutedBr,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(subHeader, 1);

            var lst = new System.Windows.Controls.ListBox
            {
                Background = bgCard,
                BorderBrush = borderBr,
                Foreground = whiteBr,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(lst, 2);

            var lblStatus = new TextBlock
            {
                Text = $"Toplam {reg.Licenses.Count} kayıtlı lisans anahtarı • Canlı Bulut Senkronu Aktif",
                Foreground = orangeBr,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(lblStatus, 3);

            void RefreshList()
            {
                int sel = lst.SelectedIndex;
                lst.Items.Clear();
                foreach (var item in reg.Licenses)
                {
                    lst.Items.Add(item.DisplayText);
                }
                if (lst.Items.Count > 0)
                    lst.SelectedIndex = Math.Clamp(sel >= 0 ? sel : 0, 0, lst.Items.Count - 1);
            }
            RefreshList();

            var bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(bar, 4);

            var txtOwner = new System.Windows.Controls.TextBox
            {
                Text = "Yeni Kullanıcı / Müşteri",
                Background = bgCard,
                Foreground = whiteBr,
                BorderBrush = borderBr,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtOwner, 0);

            var btnNew = new System.Windows.Controls.Button
            {
                Content = "➕ Yeni Lisans Üret",
                Background = orangeBr,
                Foreground = whiteBr,
                BorderBrush = orangeBr,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(btnNew, 1);

            var btnActivate = new System.Windows.Controls.Button
            {
                Content = "✅ Aktif Et",
                Background = bgCard,
                Foreground = whiteBr,
                BorderBrush = borderBr,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(btnActivate, 2);

            var btnRevoke = new System.Windows.Controls.Button
            {
                Content = "⛔ İptal Et (Pasif)",
                Background = bgCard,
                Foreground = whiteBr,
                BorderBrush = orangeBr,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(btnRevoke, 3);

            var btnCopy = new System.Windows.Controls.Button
            {
                Content = "📋 Kopyala",
                Background = bgCard,
                Foreground = whiteBr,
                BorderBrush = borderBr,
                Padding = new Thickness(12, 7, 12, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(btnCopy, 4);

            async Task SyncNowAsync(string actionText)
            {
                RefreshList();
                lblStatus.Text = $"⏳ {actionText} — Buluta (GitHub) işleniyor...";
                var (ok, msg) = await PushRegistryToCloudAsync(reg);
                lblStatus.Text = ok ? $"✅ {actionText} • {msg}" : $"⚠️ {actionText} • {msg}";
            }

            btnNew.Click += async (_, _) =>
            {
                string ownerName = string.IsNullOrWhiteSpace(txtOwner.Text) ? "PRO Kullanıcı" : txtOwner.Text.Trim();
                string newKey = GenerateLicenseKey();
                reg.Licenses.Insert(0, new ManagedLicenseEntry
                {
                    Key = newKey,
                    Owner = ownerName,
                    Status = "active",
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd"),
                    ExpiresAt = DateTime.Now.AddYears(1).ToString("yyyy-MM-dd")
                });
                lst.SelectedIndex = 0;
                try { System.Windows.Clipboard.SetText(newKey); } catch { }
                await SyncNowAsync($"Yeni lisans üretildi ve panoya kopyalandı: {newKey}");
            };

            btnActivate.Click += async (_, _) =>
            {
                int idx = lst.SelectedIndex;
                if (idx < 0 || idx >= reg.Licenses.Count) return;
                reg.Licenses[idx].Status = "active";
                await SyncNowAsync($"{reg.Licenses[idx].Key} AKTİF edildi");
            };

            btnRevoke.Click += async (_, _) =>
            {
                int idx = lst.SelectedIndex;
                if (idx < 0 || idx >= reg.Licenses.Count) return;
                reg.Licenses[idx].Status = "revoked";
                await SyncNowAsync($"{reg.Licenses[idx].Key} İPTAL edildi (karşı tarafta kilitlendi)");
            };

            btnCopy.Click += (_, _) =>
            {
                int idx = lst.SelectedIndex;
                if (idx < 0 || idx >= reg.Licenses.Count) return;
                string key = reg.Licenses[idx].Key;
                try { System.Windows.Clipboard.SetText(key); } catch { }
                lblStatus.Text = $"📋 Lisans anahtarı panoya kopyalandı: {key}";
            };

            bar.Children.Add(txtOwner);
            bar.Children.Add(btnNew);
            bar.Children.Add(btnActivate);
            bar.Children.Add(btnRevoke);
            bar.Children.Add(btnCopy);

            root.Children.Add(header);
            root.Children.Add(subHeader);
            root.Children.Add(lst);
            root.Children.Add(lblStatus);
            root.Children.Add(bar);
            win.Content = root;
            win.ShowDialog();
        }
    }
}
