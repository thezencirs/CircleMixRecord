// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CircleMixRecord.Services
{
    public class MeetChatMessage
    {
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public bool IsInstructor { get; set; }
        public bool IsHandRaise { get; set; }
    }

    public class MeetParticipantInfo
    {
        public string Name { get; set; } = "";
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public byte[] LatestCameraJpeg { get; set; }
        public bool HasRaisedHand { get; set; }
        public bool IsSharingScreen { get; set; }
    }

    /// <summary>
    /// Circle Meet yerel toplantı sunucusu.
    /// GÜVENLİK MODELİ (Faz 1A):
    ///  - Sunucu/tünel uygulama açılışında ASLA başlamaz; yalnızca kullanıcı "Canlı Odayı Aç" dediğinde başlar.
    ///  - Her oturumda rastgele 6 haneli PIN + 24 karakterlik rastgele erişim anahtarı (token) üretilir.
    ///  - Davet linki token içerir (?k=...). Token'sız gelen her istek reddedilir; izleyici sayfası token yoksa PIN sorar.
    ///  - Tüm karşılaştırmalar sabit zamanlıdır (CryptographicOperations.FixedTimeEquals).
    ///  - ntfy.sh'ye yalnızca token'SIZ tünel/LAN adresi yayınlanır: kodu bilen biri adresi bulabilir ama PIN olmadan
    ///    hiçbir içeriğe (ekran, sohbet, kamera, uzaktan kontrol) erişemez. PIN deneme sayısı oturum başına sınırlıdır.
    ///  - Uzaktan kontrol: host kutucuğu (varsayılan KAPALI) + her katılımcı için host'un ayrıca "İzin ver" onayı gerekir.
    /// </summary>
    public class CircleMeetServer : IDisposable
    {
        private WebApplication _app;
        private Process _sshTunnelProcess;
        private readonly DualCanvasEngine _engine;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        public bool IsRunning { get; private set; }
        public int Port { get; set; } = 5880;
        /// <summary>false ise yalnızca yerel ağ (LAN) — internet tüneli ve kod yayını yapılmaz.</summary>
        public bool EnablePublicRelay { get; set; } = true;

        // 6 Haneli Hızlı Bağlantı Kodu (Örn: 482-915) — yalnızca adres bulmak içindir, erişim için PIN gerekir
        public string ShortCodeDigits { get; private set; }
        public string RoomCode { get; private set; }

        // Oturum bazlı güvenlik bilgileri (her StartServerAsync çağrısında yeniden üretilir)
        public string RoomPin { get; private set; } = "";
        public string SessionToken { get; private set; } = "";
        private int _failedPinAttempts;
        private const int MaxFailedPinAttempts = 20;

        /// <summary>Token'sız LAN adresi (yalnızca dahili kullanım / ntfy yayını)</summary>
        public string LanBaseUrl { get; private set; } = "http://localhost:5880/meet";
        /// <summary>Token'sız genel internet (tünel) adresi</summary>
        public string PublicBaseUrl { get; private set; } = "";

        /// <summary>LAN davet linki (token içerir)</summary>
        public string JoinUrl => string.IsNullOrEmpty(SessionToken) ? LanBaseUrl : $"{LanBaseUrl}?k={SessionToken}";
        /// <summary>İnternet davet linki (token içerir), tünel hazır değilse boş</summary>
        public string PublicInternetUrl => string.IsNullOrWhiteSpace(PublicBaseUrl) ? "" : $"{PublicBaseUrl}?k={SessionToken}";
        /// <summary>Paylaşılacak en iyi davet linki</summary>
        public string BestInviteUrl => !string.IsNullOrWhiteSpace(PublicBaseUrl) ? PublicInternetUrl : JoinUrl;

        // Circle Remote Uzaktan Fare & Klavye Kontrol İzni (host kutucuğu, varsayılan KAPALI)
        private volatile bool _allowRemoteControl;
        public bool AllowRemoteControl
        {
            get => _allowRemoteControl;
            set
            {
                _allowRemoteControl = value;
                if (!value) RevokeAllRemoteControl();
            }
        }

        // Katılımcı (istemci kimliği → ad) uzaktan kontrol onay durumları
        private readonly ConcurrentDictionary<string, string> _remoteApproved = new();
        private readonly ConcurrentDictionary<string, string> _remotePending = new();
        private readonly ConcurrentDictionary<string, string> _remoteDenied = new();

        public ConcurrentQueue<MeetChatMessage> Messages { get; } = new();
        public ConcurrentDictionary<string, MeetParticipantInfo> Participants { get; } = new();

        public event Action<MeetChatMessage> OnNewMessage;
        public event Action OnParticipantsUpdated;
        /// <summary>Genel internet davet linki (token dahil) hazır olduğunda tetiklenir.</summary>
        public event Action<string> OnTunnelReady;
        /// <summary>Bir katılımcı uzaktan kontrol istediğinde tetiklenir: (istemciId, katılımcıAdı)</summary>
        public event Action<string, string> OnRemoteControlRequested;

        public CircleMeetServer(DualCanvasEngine engine)
        {
            _engine = engine;
            int p1 = RandomNumberGenerator.GetInt32(100, 1000);
            int p2 = RandomNumberGenerator.GetInt32(100, 1000);
            ShortCodeDigits = $"{p1}{p2}";
            RoomCode = $"{p1}-{p2}";
        }

        private static string GenerateToken(int bytes = 18)
        {
            byte[] b = RandomNumberGenerator.GetBytes(bytes);
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool FixedTimeEqualsString(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] ba = Encoding.UTF8.GetBytes(a);
            byte[] bb = Encoding.UTF8.GetBytes(b);
            // Uzunluk farklıysa da sabit zamanlı karşılaştırma yap (yalnızca uzunluk bilgisi sızabilir)
            if (ba.Length != bb.Length)
            {
                CryptographicOperations.FixedTimeEquals(bb, bb);
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(ba, bb);
        }

        private bool IsAuthorized(HttpContext ctx)
        {
            if (string.IsNullOrEmpty(SessionToken)) return false;
            string k = ctx.Request.Query["k"].ToString();
            if (string.IsNullOrEmpty(k)) k = ctx.Request.Headers["X-Circle-Key"].ToString();
            return FixedTimeEqualsString(k, SessionToken);
        }

        private static string SanitizeName(string s, int maxLen = 40)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Katılımcı";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= maxLen) break;
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "Katılımcı" : r;
        }

        private static string SanitizeText(string s, int maxLen = 500)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsControl(c) && c != '\n') continue;
                sb.Append(c);
                if (sb.Length >= maxLen) break;
            }
            return sb.ToString().Trim();
        }

        private static string SanitizeClientId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string r = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').Take(48).ToArray());
            return r.Length >= 8 ? r : "";
        }

        public async Task<string> StartServerAsync()
        {
            if (IsRunning) return BestInviteUrl;

            try
            {
                // Her oturumda yeni PIN + erişim anahtarı
                RoomPin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                SessionToken = GenerateToken(18); // 24 karakter base64url
                _failedPinAttempts = 0;
                PublicBaseUrl = "";
                _remoteApproved.Clear();
                _remotePending.Clear();
                _remoteDenied.Clear();

                string localIp = GetLocalIpAddress();
                LanBaseUrl = $"http://{localIp}:{Port}/meet";

                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
                builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_000_000);

                _app = builder.Build();

                // Token kapısı: /, /meet ve /api/auth dışındaki HER istek geçerli token ister
                _app.Use(async (ctx, next) =>
                {
                    string path = ctx.Request.Path.Value ?? "/";
                    bool open = path == "/" || path.Equals("/meet", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/api/auth", StringComparison.OrdinalIgnoreCase);
                    if (!open && !IsAuthorized(ctx))
                    {
                        ctx.Response.StatusCode = 401;
                        await ctx.Response.WriteAsync("unauthorized");
                        return;
                    }
                    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                    await next();
                });

                _app.MapGet("/", ctx =>
                {
                    ctx.Response.Redirect("/meet");
                    return Task.CompletedTask;
                });

                _app.MapGet("/meet", async ctx =>
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    if (IsAuthorized(ctx))
                        await ctx.Response.WriteAsync(BuildMeetHtml(RoomCode));
                    else
                        await ctx.Response.WriteAsync(BuildPinHtml(RoomCode));
                });

                // PIN → token takası (oturum başına sınırlı deneme)
                _app.MapPost("/api/auth", async ctx =>
                {
                    try
                    {
                        if (Volatile.Read(ref _failedPinAttempts) >= MaxFailedPinAttempts)
                        {
                            ctx.Response.StatusCode = 429;
                            await ctx.Response.WriteAsync("locked");
                            return;
                        }
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        string body = await sr.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        string pin = doc.RootElement.TryGetProperty("pin", out var p) ? (p.GetString() ?? "") : "";
                        pin = new string(pin.Where(char.IsDigit).ToArray());

                        if (FixedTimeEqualsString(pin, RoomPin))
                        {
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { token = SessionToken }));
                            return;
                        }

                        Interlocked.Increment(ref _failedPinAttempts);
                        await Task.Delay(800); // kaba kuvvet yavaşlatma
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("wrong");
                    }
                    catch
                    {
                        ctx.Response.StatusCode = 400;
                    }
                });

                _app.MapGet("/api/frame.jpg", async ctx =>
                {
                    byte[] jpg = _engine?.LatestJpeg16x9;
                    if (jpg == null || jpg.Length == 0)
                    {
                        ctx.Response.StatusCode = 204;
                        return;
                    }
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                    await ctx.Response.Body.WriteAsync(jpg, 0, jpg.Length);
                });

                _app.MapGet("/api/participant-frame.jpg", async ctx =>
                {
                    string user = ctx.Request.Query["user"].ToString();
                    if (!string.IsNullOrWhiteSpace(user) &&
                        Participants.TryGetValue(SanitizeName(user), out var pInfo) &&
                        pInfo.LatestCameraJpeg != null && pInfo.LatestCameraJpeg.Length > 0)
                    {
                        ctx.Response.ContentType = "image/jpeg";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                        await ctx.Response.Body.WriteAsync(pInfo.LatestCameraJpeg, 0, pInfo.LatestCameraJpeg.Length);
                        return;
                    }
                    ctx.Response.StatusCode = 204;
                });

                _app.MapPost("/api/participant-frame", async ctx =>
                {
                    string user = SanitizeName(ctx.Request.Query["user"].ToString());
                    bool isScreen = ctx.Request.Query["screen"] == "1";

                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms);
                    byte[] bytes = ms.ToArray();

                    // Yalnızca gerçek JPEG kabul et (FF D8 başlığı)
                    if (bytes.Length > 100 && bytes.Length < 900_000 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    {
                        var info = Participants.GetOrAdd(user, u => new MeetParticipantInfo { Name = u });
                        info.LastSeenUtc = DateTime.UtcNow;
                        info.LatestCameraJpeg = bytes;
                        info.IsSharingScreen = isScreen;
                        SyncParticipantsToCompositor();
                    }
                    ctx.Response.StatusCode = 200;
                });

                // Katılımcı uzaktan kontrol talebi → host onayı
                _app.MapPost("/api/remote-request", async ctx =>
                {
                    try
                    {
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        string body = await sr.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        string cid = SanitizeClientId(doc.RootElement.TryGetProperty("cid", out var c) ? c.GetString() : "");
                        string name = SanitizeName(doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "");
                        if (string.IsNullOrEmpty(cid)) { ctx.Response.StatusCode = 400; return; }

                        string status;
                        if (!AllowRemoteControl) status = "disabled";
                        else if (_remoteApproved.ContainsKey(cid)) status = "approved";
                        else if (_remotePending.ContainsKey(cid)) status = "pending";
                        else
                        {
                            _remoteDenied.TryRemove(cid, out _);
                            _remotePending[cid] = name;
                            status = "pending";
                            try { OnRemoteControlRequested?.Invoke(cid, name); } catch { }
                        }
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { status }));
                    }
                    catch
                    {
                        ctx.Response.StatusCode = 400;
                    }
                });

                // Circle Remote Uzaktan Fare ve Klavye Kontrol Endpoint'i (yalnızca host'un onayladığı katılımcılar)
                _app.MapPost("/api/remote-input", async ctx =>
                {
                    try
                    {
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        string body = await sr.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;

                        string cid = SanitizeClientId(root.TryGetProperty("cid", out var cProp) ? cProp.GetString() : "");
                        if (!AllowRemoteControl || string.IsNullOrEmpty(cid) || !_remoteApproved.ContainsKey(cid))
                        {
                            ctx.Response.StatusCode = 403;
                            await ctx.Response.WriteAsync("not-approved");
                            return;
                        }

                        var cmd = new RemoteInputCommand
                        {
                            Type = root.TryGetProperty("type", out var tProp) ? (tProp.GetString() ?? "") : "",
                            X = root.TryGetProperty("x", out var xProp) && xProp.ValueKind == JsonValueKind.Number ? xProp.GetDouble() : 0.5,
                            Y = root.TryGetProperty("y", out var yProp) && yProp.ValueKind == JsonValueKind.Number ? yProp.GetDouble() : 0.5,
                            Button = root.TryGetProperty("button", out var bProp) && bProp.ValueKind == JsonValueKind.Number ? bProp.GetInt32() : 0,
                            Delta = root.TryGetProperty("delta", out var dProp) && dProp.ValueKind == JsonValueKind.Number ? dProp.GetInt32() : 0,
                            Key = root.TryGetProperty("key", out var kProp) ? (kProp.GetString() ?? "") : "",
                            Ctrl = root.TryGetProperty("ctrl", out var ctl) && ctl.ValueKind == JsonValueKind.True,
                            Alt = root.TryGetProperty("alt", out var alt) && alt.ValueKind == JsonValueKind.True,
                            Shift = root.TryGetProperty("shift", out var sh) && sh.ValueKind == JsonValueKind.True,
                            Meta = root.TryGetProperty("meta", out var mt) && mt.ValueKind == JsonValueKind.True
                        };

                        ExecuteRemoteInputOnHost(cmd);
                        ctx.Response.StatusCode = 200;
                    }
                    catch
                    {
                        ctx.Response.StatusCode = 400;
                    }
                });

                _app.MapGet("/api/state", async ctx =>
                {
                    string user = ctx.Request.Query["user"].ToString();
                    string cid = SanitizeClientId(ctx.Request.Query["cid"].ToString());
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        string uName = SanitizeName(user);
                        var info = Participants.GetOrAdd(uName, u => new MeetParticipantInfo { Name = u });
                        info.LastSeenUtc = DateTime.UtcNow;
                    }

                    var cutoff = DateTime.UtcNow.AddSeconds(-25);
                    bool removedAny = false;
                    foreach (var kv in Participants.ToArray())
                    {
                        if (kv.Value.LastSeenUtc < cutoff)
                        {
                            Participants.TryRemove(kv.Key, out _);
                            removedAny = true;
                        }
                    }
                    if (removedAny) SyncParticipantsToCompositor();

                    string remoteStatus = !AllowRemoteControl ? "disabled"
                        : string.IsNullOrEmpty(cid) ? "none"
                        : _remoteApproved.ContainsKey(cid) ? "approved"
                        : _remotePending.ContainsKey(cid) ? "pending"
                        : _remoteDenied.ContainsKey(cid) ? "denied"
                        : "none";

                    var payload = new
                    {
                        room = RoomCode,
                        allowRemote = AllowRemoteControl,
                        remoteStatus,
                        participants = Participants.Values.OrderBy(x => x.Name).Select(x => new
                        {
                            name = x.Name,
                            hasCam = x.LatestCameraJpeg != null && x.LatestCameraJpeg.Length > 0,
                            hand = x.HasRaisedHand,
                            screen = x.IsSharingScreen
                        }).ToArray(),
                        messages = Messages.ToArray()
                    };
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
                });

                _app.MapPost("/api/chat", async ctx =>
                {
                    try
                    {
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        string body = await sr.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        string sender = SanitizeName(doc.RootElement.TryGetProperty("sender", out var s) ? s.GetString() : "Katılımcı");
                        string text = SanitizeText(doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : "");
                        bool hand = doc.RootElement.TryGetProperty("hand", out var h) && h.ValueKind == JsonValueKind.True;

                        var info = Participants.GetOrAdd(sender, u => new MeetParticipantInfo { Name = u });
                        info.LastSeenUtc = DateTime.UtcNow;
                        if (hand) info.HasRaisedHand = true;
                        SyncParticipantsToCompositor();

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var msg = new MeetChatMessage
                            {
                                Sender = sender,
                                Text = text,
                                Time = DateTime.Now.ToString("HH:mm:ss"),
                                IsInstructor = false,
                                IsHandRaise = hand
                            };
                            Messages.Enqueue(msg);
                            while (Messages.Count > 100) Messages.TryDequeue(out _);
                            OnNewMessage?.Invoke(msg);
                        }
                        ctx.Response.StatusCode = 200;
                    }
                    catch
                    {
                        ctx.Response.StatusCode = 400;
                    }
                });

                await _app.StartAsync();
                IsRunning = true;

                // Önce LAN adresini 6 haneli koda kaydet (token'sız), ardından genel internet tünelini başlat
                if (EnablePublicRelay)
                {
                    _ = PublishRoomCodeMappingAsync();
                    _ = StartPublicInternetTunnelAsync();
                }

                SendInstructorMessage($"🎓 Circle Meet & Uzaktan Yardım Odası (Kod: {RoomCode}) aktif!");
                return JoinUrl;
            }
            catch
            {
                IsRunning = false;
                return JoinUrl;
            }
        }

        private async Task StartPublicInternetTunnelAsync()
        {
            try
            {
                StopTunnelProcess();
                var psi = new ProcessStartInfo
                {
                    FileName = "ssh.exe",
                    Arguments = $"-T -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ServerAliveInterval=30 -R 80:127.0.0.1:{Port} nokey@localhost.run",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _sshTunnelProcess = Process.Start(psi);
                if (_sshTunnelProcess == null) return;

                while (!_sshTunnelProcess.HasExited && IsRunning)
                {
                    string line = await _sshTunnelProcess.StandardOutput.ReadLineAsync();
                    if (line == null) break;

                    var m = Regex.Match(line, @"https://[a-zA-Z0-9\-]+\.lhr\.life");
                    if (m.Success)
                    {
                        PublicBaseUrl = m.Value.TrimEnd('/') + "/meet";
                        await PublishRoomCodeMappingAsync();
                        OnTunnelReady?.Invoke(PublicInternetUrl);
                        break;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// "Kodla bağlan" özelliği için 6 haneli kodu ntfy.sh üzerinde adrese eşler.
        /// GÜVENLİK: Yayınlanan yük yalnızca TOKEN'SIZ adresleri içerir. Bu adresler PIN sorma sayfasından
        /// başka hiçbir şey döndürmez; ekran, sohbet ve uzaktan kontrol PIN/token olmadan erişilemez.
        /// </summary>
        private async Task PublishRoomCodeMappingAsync()
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    code = ShortCodeDigits,
                    room = RoomCode,
                    lanUrl = LanBaseUrl,          // token YOK
                    publicUrl = PublicBaseUrl      // token YOK
                });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _http.PostAsync($"https://ntfy.sh/circlemix_code_{ShortCodeDigits}", content);
            }
            catch { }
        }

        /// <summary>
        /// Kullanıcının girdiği 6 haneli kodu (Örn: 482-915) veya linki çözer ve bağlanılacak URL'yi döndürür.
        /// Uzak odalar için dönen adres token içermez; izleyici sayfası PIN ister.
        /// </summary>
        public async Task<string> ResolveCodeToUrlAsync(string codeOrUrl)
        {
            if (string.IsNullOrWhiteSpace(codeOrUrl)) return null;
            string trimmed = codeOrUrl.Trim();

            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            string digits = new string(trimmed.Where(char.IsDigit).ToArray());
            if (digits == ShortCodeDigits && IsRunning)
            {
                return BestInviteUrl;
            }

            if (digits.Length >= 4)
            {
                try
                {
                    string resp = await _http.GetStringAsync($"https://ntfy.sh/circlemix_code_{digits}/json?poll=1&since=12h");
                    string[] lines = resp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        using var outer = JsonDocument.Parse(lines[i]);
                        if (outer.RootElement.TryGetProperty("message", out var msgProp))
                        {
                            string innerJson = msgProp.GetString();
                            if (!string.IsNullOrWhiteSpace(innerJson))
                            {
                                using var inner = JsonDocument.Parse(innerJson);
                                string pubUrl = inner.RootElement.TryGetProperty("publicUrl", out var p) ? p.GetString() : "";
                                string lanUrl = inner.RootElement.TryGetProperty("lanUrl", out var l) ? l.GetString() : "";

                                // Yalnızca http(s) adreslerini kabul et
                                if (IsSafeHttpUrl(pubUrl)) return pubUrl;
                                if (IsSafeHttpUrl(lanUrl)) return lanUrl;
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool IsSafeHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        // ======================= UZAKTAN KONTROL ONAY YÖNETİMİ =======================

        /// <summary>Host'un "İzin ver / Reddet" kararını uygular.</summary>
        public void SetRemoteApproval(string clientId, bool approved)
        {
            if (string.IsNullOrEmpty(clientId)) return;
            _remotePending.TryRemove(clientId, out string name);
            if (approved && AllowRemoteControl)
            {
                _remoteDenied.TryRemove(clientId, out _);
                _remoteApproved[clientId] = name ?? "Katılımcı";
            }
            else
            {
                _remoteApproved.TryRemove(clientId, out _);
                _remoteDenied[clientId] = name ?? "Katılımcı";
            }
        }

        public void RevokeAllRemoteControl()
        {
            _remoteApproved.Clear();
            _remotePending.Clear();
        }

        public IReadOnlyCollection<string> ApprovedRemoteNames => _remoteApproved.Values.ToArray();

        private sealed class RemoteInputCommand
        {
            public string Type = "";
            public double X = 0.5, Y = 0.5;
            public int Button;
            public int Delta;
            public string Key = "";
            public bool Ctrl, Alt, Shift, Meta;
        }

        private void ExecuteRemoteInputOnHost(RemoteInputCommand cmd)
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                int x = bounds.Left + Math.Clamp((int)(Math.Clamp(cmd.X, 0, 1) * bounds.Width), 0, bounds.Width - 1);
                int y = bounds.Top + Math.Clamp((int)(Math.Clamp(cmd.Y, 0, 1) * bounds.Height), 0, bounds.Height - 1);

                switch (cmd.Type)
                {
                    case "move":
                        SetCursorPos(x, y);
                        break;

                    case "mousedown":
                        SetCursorPos(x, y);
                        mouse_event(cmd.Button == 2 ? MOUSEEVENTF_RIGHTDOWN : (cmd.Button == 1 ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_LEFTDOWN), 0, 0, 0, UIntPtr.Zero);
                        break;

                    case "mouseup":
                        SetCursorPos(x, y);
                        mouse_event(cmd.Button == 2 ? MOUSEEVENTF_RIGHTUP : (cmd.Button == 1 ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_LEFTUP), 0, 0, 0, UIntPtr.Zero);
                        break;

                    case "click":
                        SetCursorPos(x, y);
                        if (cmd.Button == 2)
                        {
                            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                        }
                        else
                        {
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        }
                        break;

                    case "wheel":
                        {
                            int delta = Math.Clamp(cmd.Delta, -1200, 1200);
                            if (delta != 0)
                            {
                                SetCursorPos(x, y);
                                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                            }
                        }
                        break;

                    case "key":
                        SendRemoteKey(cmd);
                        break;
                }
            }
            catch { }
        }

        private static ushort MapKeyToVk(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            switch (key)
            {
                case "Enter": return 0x0D;
                case "Backspace": return 0x08;
                case "Tab": return 0x09;
                case "Escape": return 0x1B;
                case "Delete": return 0x2E;
                case "Insert": return 0x2D;
                case "Home": return 0x24;
                case "End": return 0x23;
                case "PageUp": return 0x21;
                case "PageDown": return 0x22;
                case "ArrowUp": return 0x26;
                case "ArrowDown": return 0x28;
                case "ArrowLeft": return 0x25;
                case "ArrowRight": return 0x27;
                case " ": return 0x20;
            }
            if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out int fn) && fn >= 1 && fn <= 12)
                return (ushort)(0x70 + fn - 1);
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c;
            }
            return 0;
        }

        private static void SendRemoteKey(RemoteInputCommand cmd)
        {
            string key = cmd.Key ?? "";
            if (key.Length == 0 || key.Length > 16) return;
            // Modifier tuşlarının kendisi tek başına gönderilmez (kombinasyon olarak işlenir)
            if (key is "Control" or "Shift" or "Alt" or "Meta" or "AltGraph" or "CapsLock" or "OS") return;

            ushort vk = MapKeyToVk(key);
            bool combo = cmd.Ctrl || cmd.Alt || cmd.Meta;

            var inputs = new List<INPUT>();
            if (combo && vk != 0)
            {
                if (cmd.Ctrl) inputs.Add(KeyInput(VK_CONTROL, false));
                if (cmd.Alt) inputs.Add(KeyInput(VK_MENU, false));
                if (cmd.Shift) inputs.Add(KeyInput(VK_SHIFT, false));
                if (cmd.Meta) inputs.Add(KeyInput(VK_LWIN, false));
                inputs.Add(KeyInput(vk, false));
                inputs.Add(KeyInput(vk, true));
                if (cmd.Meta) inputs.Add(KeyInput(VK_LWIN, true));
                if (cmd.Shift) inputs.Add(KeyInput(VK_SHIFT, true));
                if (cmd.Alt) inputs.Add(KeyInput(VK_MENU, true));
                if (cmd.Ctrl) inputs.Add(KeyInput(VK_CONTROL, true));
            }
            else if (key.Length == 1)
            {
                // Yazdırılabilir karakter (Türkçe karakterler dahil) → Unicode olarak gönder
                char ch = key[0];
                inputs.Add(UnicodeInput(ch, false));
                inputs.Add(UnicodeInput(ch, true));
            }
            else if (vk != 0)
            {
                if (cmd.Shift) inputs.Add(KeyInput(VK_SHIFT, false));
                inputs.Add(KeyInput(vk, false));
                inputs.Add(KeyInput(vk, true));
                if (cmd.Shift) inputs.Add(KeyInput(VK_SHIFT, true));
            }

            if (inputs.Count > 0)
            {
                var arr = inputs.ToArray();
                SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
            }
        }

        private static INPUT KeyInput(ushort vk, bool up)
        {
            uint flags = up ? KEYEVENTF_KEYUP : 0u;
            if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E)
                flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } }
            };
        }

        private static INPUT UnicodeInput(char ch, bool up)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0u), time = 0, dwExtraInfo = IntPtr.Zero } }
            };
        }

        private void SyncParticipantsToCompositor()
        {
            try
            {
                if (_engine != null)
                {
                    _engine.UpdateMeetingParticipants(Participants.Values.ToList());
                }
                OnParticipantsUpdated?.Invoke();
            }
            catch { }
        }

        public void SendInstructorMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var msg = new MeetChatMessage
            {
                Sender = "Eğitmen (Stüdyo)",
                Text = SanitizeText(text, 1000),
                Time = DateTime.Now.ToString("HH:mm:ss"),
                IsInstructor = true,
                IsHandRaise = false
            };
            Messages.Enqueue(msg);
            while (Messages.Count > 100) Messages.TryDequeue(out _);
            OnNewMessage?.Invoke(msg);
        }

        private void StopTunnelProcess()
        {
            try
            {
                if (_sshTunnelProcess != null && !_sshTunnelProcess.HasExited)
                {
                    _sshTunnelProcess.Kill(true);
                }
            }
            catch { }
            _sshTunnelProcess = null;
        }

        public async Task StopServerAsync()
        {
            StopTunnelProcess();
            RevokeAllRemoteControl();
            _remoteDenied.Clear();
            // Oturum anahtarlarını geçersiz kıl (eski linkler/PIN bir sonraki oturumda çalışmaz)
            SessionToken = "";
            RoomPin = "";
            PublicBaseUrl = "";
            if (!IsRunning || _app == null) return;
            IsRunning = false;
            try
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            catch { }
            _app = null;
            Participants.Clear();
            SyncParticipantsToCompositor();
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint ep)
                    return ep.Address.ToString();
            }
            catch { }
            return "localhost";
        }

        private static string BuildPinHtml(string roomCode)
        {
            const string html = """
<!DOCTYPE html>
<html lang='tr'>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>Circle Meet — Oda Girişi</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0;font-family:'Segoe UI',system-ui,sans-serif;}
  body{background:#140E38;color:#FFFFFF;display:flex;align-items:center;justify-content:center;height:100vh;}
  .card{background:#1C134C;border:1px solid #3B2A94;border-radius:14px;padding:28px 30px;width:340px;text-align:center;}
  .badge{display:inline-block;background:#EF5F18;color:#FFFFFF;padding:3px 10px;border-radius:99px;font-size:11.5px;font-weight:700;margin-bottom:14px;}
  h1{font-size:17px;margin-bottom:6px;}
  p{font-size:12.5px;color:#C4B5FD;margin-bottom:16px;}
  input{background:#140E38;border:1px solid #3B2A94;color:#fff;padding:10px;border-radius:8px;font-size:22px;letter-spacing:6px;text-align:center;width:100%;margin-bottom:12px;}
  button{background:#EF5F18;color:#FFFFFF;border:1px solid #EF5F18;padding:10px 14px;border-radius:8px;cursor:pointer;font-weight:700;font-size:13px;width:100%;}
  #err{color:#FFFFFF;font-size:12px;margin-top:10px;min-height:16px;}
</style>
</head>
<body>
<div class='card'>
  <span class='badge'>● CIRCLE MEET</span>
  <h1>Oda {{ROOM}}</h1>
  <p>Odaya katılmak için davet edenden aldığınız 6 haneli PIN kodunu girin.</p>
  <input id='pin' maxlength='6' inputmode='numeric' autocomplete='one-time-code' placeholder='••••••'/>
  <button id='go'>Odaya Katıl</button>
  <div id='err'></div>
</div>
<script>
  const pinEl = document.getElementById('pin');
  const errEl = document.getElementById('err');
  async function join(){
    const pin = (pinEl.value || '').replace(/\D/g,'');
    if(pin.length !== 6){ errEl.textContent = 'PIN 6 haneli olmalıdır.'; return; }
    try{
      const r = await fetch('/api/auth', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ pin }) });
      if(r.status === 429){ errEl.textContent = 'Çok fazla hatalı deneme. Davet edenden yeni link isteyin.'; return; }
      if(!r.ok){ errEl.textContent = 'PIN hatalı.'; return; }
      const d = await r.json();
      location.replace('/meet?k=' + encodeURIComponent(d.token));
    }catch(e){ errEl.textContent = 'Bağlantı hatası.'; }
  }
  document.getElementById('go').addEventListener('click', join);
  pinEl.addEventListener('keydown', e => { if(e.key === 'Enter') join(); });
  pinEl.focus();
</script>
</body>
</html>
""";
            return html.Replace("{{ROOM}}", WebUtility.HtmlEncode(roomCode ?? ""));
        }

        private static string BuildMeetHtml(string roomCode)
        {
            const string html = """
<!DOCTYPE html>
<html lang='tr'>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>Circle Meet & Uzaktan Bağlantı — Kod: {{ROOM}}</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0;font-family:'Segoe UI',system-ui,sans-serif;}
  body{background:#140E38;color:#FFFFFF;display:flex;flex-direction:column;height:100vh;overflow:hidden;}
  header{height:54px;background:#1C134C;border-bottom:1px solid #3B2A94;display:flex;align-items:center;justify-content:space-between;padding:0 20px;}
  .brand{display:flex;align-items:center;gap:12px;font-weight:700;font-size:15px;color:#FFFFFF;}
  .badge{background:#EF5F18;color:#FFFFFF;padding:3px 10px;border-radius:99px;font-size:11.5px;font-weight:700;}
  .room{background:#261A66;border:1px solid #3B2A94;color:#EF5F18;padding:4px 12px;border-radius:6px;font-size:13px;font-weight:700;}
  main{flex:1;display:grid;grid-template-columns:1fr 330px;gap:12px;padding:12px;overflow:hidden;}
  .stage{background:#1C134C;border:1px solid #3B2A94;border-radius:12px;display:flex;flex-direction:column;overflow:hidden;}
  .video-wrap{flex:1;display:flex;align-items:center;justify-content:center;background:#0D0926;position:relative;overflow:hidden;outline:none;}
  #liveStream{max-width:100%;max-height:100%;object-fit:contain;user-select:none;}
  .tiles-strip{position:absolute;bottom:14px;left:14px;display:flex;gap:10px;z-index:5;}
  #selfVideo{width:180px;height:102px;background:#261A66;border:2px solid #EF5F18;border-radius:10px;object-fit:cover;display:none;}
  .controls{min-height:60px;background:#1C134C;border-top:1px solid #3B2A94;display:flex;align-items:center;justify-content:center;gap:10px;flex-wrap:wrap;padding:6px;}
  button{background:#261A66;color:#FFFFFF;border:1px solid #3B2A94;padding:9px 14px;border-radius:8px;cursor:pointer;font-weight:600;font-size:12.5px;transition:.15s;}
  button:hover{background:#3B2A94;}
  button.primary{background:#EF5F18;border-color:#EF5F18;}
  button.active{background:#EF5F18;border-color:#FFFFFF;}
  button.hand{background:#EF5F18;border-color:#FFFFFF;}
  .sidebar{background:#1C134C;border:1px solid #3B2A94;border-radius:12px;display:flex;flex-direction:column;overflow:hidden;}
  .side-hdr{padding:11px 14px;border-bottom:1px solid #3B2A94;font-weight:700;font-size:12.5px;color:#FFFFFF;display:flex;justify-content:space-between;}
  #chatBox{flex:1;padding:12px;overflow-y:auto;display:flex;flex-direction:column;gap:8px;font-size:13px;}
  .msg{background:#261A66;padding:8px 11px;border-radius:8px;line-height:1.4;word-wrap:break-word;white-space:pre-wrap;}
  .msg.inst{background:#261A66;border-left:3px solid #EF5F18;}
  .msg.hand{background:#261A66;border-left:3px solid #FFFFFF;}
  .meta{font-size:11px;color:#C4B5FD;margin-bottom:3px;display:flex;justify-content:space-between;gap:8px;}
  .chat-in{padding:10px;border-top:1px solid #3B2A94;display:flex;gap:8px;}
  input{background:#140E38;border:1px solid #3B2A94;color:#fff;padding:8px 10px;border-radius:6px;font-size:13px;flex:1;}
  #remoteNote{font-size:11.5px;color:#C4B5FD;width:100%;text-align:center;}
</style>
</head>
<body>
<header>
  <div class='brand'>
    <span class='badge'>● CIRCLE CANLI ODA</span>
    <span>CIRCLE MEET & REMOTE</span>
  </div>
  <div style='display:flex;gap:10px;align-items:center;'>
    <input id='userName' maxlength='40' style='width:145px;flex:none;' title='Adınız'/>
    <span class='room'>Circle Kodu: {{ROOM}}</span>
  </div>
</header>
<main>
  <div class='stage'>
    <div class='video-wrap' id='stageWrap' tabindex='0'>
      <img id='liveStream' alt='Canlı Ekran Yükleniyor...' draggable='false'/>
      <div class='tiles-strip'>
        <video id='selfVideo' autoplay muted playsinline></video>
      </div>
      <canvas id='sendCanvas' width='320' height='180' style='display:none;'></canvas>
    </div>
    <div class='controls'>
      <button id='btnCam'>📷 Kameramı Aç</button>
      <button id='btnScreen'>🖥️ Ekranımı Paylaş</button>
      <button id='btnRemote'>🖱️ Circle Uzaktan Kontrol İste</button>
      <button id='btnHand' class='hand'>✋ Söz İste</button>
      <button id='btnFull'>⛶ Tam Ekran</button>
      <div id='remoteNote'></div>
    </div>
  </div>
  <div class='sidebar'>
    <div class='side-hdr'>
      <span>💬 CIRCLE SOHBET & KATILIMCILAR</span>
      <span id='partCount'>1 Kişi</span>
    </div>
    <div id='chatBox'></div>
    <div class='chat-in'>
      <input id='msgInput' maxlength='500' placeholder='Mesaj veya soru yazın...'/>
      <button class='primary' id='btnSend'>Gönder</button>
    </div>
  </div>
</main>
<script>
  const K = new URLSearchParams(location.search).get('k') || '';
  const KQ = 'k=' + encodeURIComponent(K);
  function api(path){ return path + (path.indexOf('?') >= 0 ? '&' : '?') + KQ; }

  // İstemci kimliği (uzaktan kontrol onayı bu kimliğe verilir)
  let CID = sessionStorage.getItem('circle_cid');
  if(!CID){
    const a = new Uint8Array(16); crypto.getRandomValues(a);
    CID = Array.from(a, b => b.toString(16).padStart(2,'0')).join('');
    sessionStorage.setItem('circle_cid', CID);
  }

  const nameEl = document.getElementById('userName');
  nameEl.value = sessionStorage.getItem('circle_name') || ('Katılımcı_' + (10 + Math.floor(Math.random()*89)));
  nameEl.addEventListener('change', () => sessionStorage.setItem('circle_name', nameEl.value));
  function myName(){ return (nameEl.value || 'Katılımcı').trim().slice(0,40) || 'Katılımcı'; }

  const img = document.getElementById('liveStream');
  const selfVideo = document.getElementById('selfVideo');
  const sendCanvas = document.getElementById('sendCanvas');
  const ctx2d = sendCanvas.getContext('2d');
  const stageWrap = document.getElementById('stageWrap');
  const remoteNote = document.getElementById('remoteNote');
  let activeStream = null;
  let isScreen = false;
  let remoteControlActive = false;
  let remoteStatus = 'disabled';

  setInterval(() => { img.src = api('/api/frame.jpg?t=' + Date.now()); }, 130);

  setInterval(async () => {
    if (!activeStream || selfVideo.readyState < 2) return;
    try {
      ctx2d.drawImage(selfVideo, 0, 0, 320, 180);
      sendCanvas.toBlob(async (blob) => {
        if (!blob) return;
        const u = encodeURIComponent(myName());
        await fetch(api('/api/participant-frame?user=' + u + '&screen=' + (isScreen ? '1' : '0')), { method: 'POST', body: blob });
      }, 'image/jpeg', 0.72);
    } catch(e){}
  }, 280);

  function getNormCoords(e){
    const rect = img.getBoundingClientRect();
    const x = Math.max(0, Math.min(1, (e.clientX - rect.left) / Math.max(1, rect.width)));
    const y = Math.max(0, Math.min(1, (e.clientY - rect.top) / Math.max(1, rect.height)));
    return { x, y };
  }

  async function sendRemote(payload){
    if(!remoteControlActive || remoteStatus !== 'approved') return;
    payload.cid = CID;
    try{ await fetch(api('/api/remote-input'), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) }); }catch(e){}
  }

  let buttonsDown = 0;
  img.addEventListener('mousedown', (e) => {
    if(!remoteControlActive) return;
    e.preventDefault();
    stageWrap.focus();
    buttonsDown++;
    const c = getNormCoords(e);
    sendRemote({ type:'mousedown', x:c.x, y:c.y, button:e.button });
  });
  window.addEventListener('mouseup', (e) => {
    if(!remoteControlActive || buttonsDown <= 0) return;
    buttonsDown = Math.max(0, buttonsDown - 1);
    const c = getNormCoords(e);
    sendRemote({ type:'mouseup', x:c.x, y:c.y, button:e.button });
  });
  img.addEventListener('contextmenu', (e) => { if(remoteControlActive) e.preventDefault(); });

  let lastMove = 0;
  img.addEventListener('mousemove', (e) => {
    if(!remoteControlActive) return;
    const now = Date.now();
    if(now - lastMove < (buttonsDown > 0 ? 35 : 65)) return; // sürüklemede daha sık güncelle
    lastMove = now;
    const c = getNormCoords(e);
    sendRemote({ type:'move', x:c.x, y:c.y });
  });

  let wheelAcc = 0, wheelTimer = null, wheelPos = {x:0.5,y:0.5};
  img.addEventListener('wheel', (e) => {
    if(!remoteControlActive) return;
    e.preventDefault();
    wheelPos = getNormCoords(e);
    wheelAcc += (e.deltaY > 0 ? -120 : 120);
    if(!wheelTimer){
      wheelTimer = setTimeout(() => {
        const d = wheelAcc; wheelAcc = 0; wheelTimer = null;
        if(d !== 0) sendRemote({ type:'wheel', x:wheelPos.x, y:wheelPos.y, delta:d });
      }, 50);
    }
  }, { passive:false });

  stageWrap.addEventListener('keydown', (e) => {
    if(!remoteControlActive) return;
    e.preventDefault();
    sendRemote({ type:'key', key:e.key, ctrl:e.ctrlKey, alt:e.altKey, shift:e.shiftKey, meta:e.metaKey });
  });

  function updateRemoteButton(){
    const btn = document.getElementById('btnRemote');
    if(remoteStatus === 'approved'){
      btn.className = remoteControlActive ? 'active' : '';
      btn.textContent = remoteControlActive ? '🖱️ Circle Uzaktan Kontrol: AKTİF (Ekrana Tıkla/Yaz)' : '🖱️ Circle Uzaktan Kontrol: Onaylandı (Başlat)';
      remoteNote.textContent = '';
    } else {
      remoteControlActive = false;
      btn.className = '';
      btn.textContent = '🖱️ Circle Uzaktan Kontrol İste';
      remoteNote.textContent =
        remoteStatus === 'pending' ? 'Uzaktan kontrol isteğiniz oda sahibinin onayını bekliyor...' :
        remoteStatus === 'denied' ? 'Uzaktan kontrol isteğiniz reddedildi.' :
        remoteStatus === 'disabled' ? '' : '';
    }
  }

  async function toggleRemoteControl(){
    if(remoteStatus === 'approved'){
      remoteControlActive = !remoteControlActive;
      updateRemoteButton();
      if(remoteControlActive) stageWrap.focus();
      return;
    }
    try{
      const r = await fetch(api('/api/remote-request'), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ cid: CID, name: myName() }) });
      const d = await r.json();
      remoteStatus = d.status;
      if(remoteStatus === 'disabled'){
        alert('Oda sahibi uzaktan kontrolü kapalı tutuyor. Lütfen karşı taraftan izni açmasını isteyin.');
      }
      updateRemoteButton();
    }catch(e){}
  }

  function renderMessages(list){
    const box = document.getElementById('chatBox');
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
    box.replaceChildren();
    for(const m of list){
      const div = document.createElement('div');
      div.className = 'msg' + (m.IsInstructor ? ' inst' : (m.IsHandRaise ? ' hand' : ''));
      const meta = document.createElement('div');
      meta.className = 'meta';
      const b = document.createElement('b'); b.textContent = String(m.Sender || '');
      const t = document.createElement('span'); t.textContent = String(m.Time || '');
      meta.append(b, t);
      const body = document.createElement('div'); body.textContent = String(m.Text || '');
      div.append(meta, body);
      box.appendChild(div);
    }
    if(atBottom) box.scrollTop = box.scrollHeight;
  }

  async function pollState(){
    try{
      const r = await fetch(api('/api/state?user=' + encodeURIComponent(myName()) + '&cid=' + CID));
      if(r.status === 401){ location.replace('/meet'); return; }
      const d = await r.json();
      remoteStatus = d.remoteStatus || 'disabled';
      updateRemoteButton();
      document.getElementById('partCount').textContent = (d.participants.length + 1) + ' Katılımcı';
      renderMessages(d.messages || []);
    }catch(e){}
  }
  setInterval(pollState, 1000);
  pollState();

  async function sendMsg(){
    const inp = document.getElementById('msgInput');
    const txt = inp.value.trim();
    if(!txt) return;
    inp.value = '';
    await fetch(api('/api/chat'), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ sender: myName(), text: txt, hand: false }) });
    pollState();
  }

  async function raiseHand(){
    await fetch(api('/api/chat'), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ sender: myName(), text: '✋ Söz istiyor! (El kaldırdı)', hand: true }) });
    pollState();
  }

  function stopStream(){
    if(activeStream){ activeStream.getTracks().forEach(t=>t.stop()); activeStream = null; }
    selfVideo.style.display = 'none';
    document.getElementById('btnCam').className = '';
    document.getElementById('btnScreen').className = '';
  }

  async function toggleSelfCam(){
    if(activeStream){ stopStream(); return; }
    try{
      activeStream = await navigator.mediaDevices.getUserMedia({video:true,audio:false});
      isScreen = false;
      selfVideo.srcObject = activeStream;
      selfVideo.style.display = 'block';
      document.getElementById('btnCam').className = 'active';
    }catch(e){ alert('Kamera izni alınamadı.'); }
  }

  async function toggleScreenShare(){
    stopStream();
    try{
      activeStream = await navigator.mediaDevices.getDisplayMedia({video:true});
      isScreen = true;
      selfVideo.srcObject = activeStream;
      selfVideo.style.display = 'block';
      document.getElementById('btnScreen').className = 'active';
    }catch(e){}
  }

  function toggleFullScreen(){
    if(!document.fullscreenElement) stageWrap.requestFullscreen();
    else document.exitFullscreen();
  }

  document.getElementById('btnCam').addEventListener('click', toggleSelfCam);
  document.getElementById('btnScreen').addEventListener('click', toggleScreenShare);
  document.getElementById('btnRemote').addEventListener('click', toggleRemoteControl);
  document.getElementById('btnHand').addEventListener('click', raiseHand);
  document.getElementById('btnFull').addEventListener('click', toggleFullScreen);
  document.getElementById('btnSend').addEventListener('click', sendMsg);
  document.getElementById('msgInput').addEventListener('keydown', e => { if(e.key === 'Enter') sendMsg(); });
</script>
</body>
</html>
""";
            return html.Replace("{{ROOM}}", WebUtility.HtmlEncode(roomCode ?? ""));
        }

        public void Dispose()
        {
            StopTunnelProcess();
            try { StopServerAsync().Wait(1500); } catch { }
        }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }
}

