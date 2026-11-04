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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public class CircleMeetServer : IDisposable
    {
        private WebApplication _app;
        private Process _sshTunnelProcess;
        private readonly DualCanvasEngine _engine;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        public bool IsRunning { get; private set; }
        public int Port { get; private set; } = 5880;

        // 6 Haneli Hızlı Bağlantı Kodu (Örn: 482-915)
        public string ShortCodeDigits { get; private set; }
        public string RoomCode { get; private set; }
        public string JoinUrl { get; private set; } = "http://localhost:5880/meet";
        public string PublicInternetUrl { get; private set; } = "";

        // Circle Remote Uzaktan Fare & Klavye Kontrol İzni
        public bool AllowRemoteControl { get; set; } = false;

        public ConcurrentQueue<MeetChatMessage> Messages { get; } = new();
        public ConcurrentDictionary<string, MeetParticipantInfo> Participants { get; } = new();

        public event Action<MeetChatMessage> OnNewMessage;
        public event Action OnParticipantsUpdated;
        public event Action<string> OnTunnelReady;

        public CircleMeetServer(DualCanvasEngine engine)
        {
            _engine = engine;
            int p1 = Random.Shared.Next(100, 999);
            int p2 = Random.Shared.Next(100, 999);
            ShortCodeDigits = $"{p1}{p2}";
            RoomCode = $"{p1}-{p2}";
        }

        public async Task<string> StartServerAsync()
        {
            if (IsRunning) return !string.IsNullOrWhiteSpace(PublicInternetUrl) ? PublicInternetUrl : JoinUrl;

            try
            {
                string localIp = GetLocalIpAddress();
                JoinUrl = $"http://{localIp}:{Port}/meet";

                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");

                _app = builder.Build();

                _app.MapGet("/", ctx =>
                {
                    ctx.Response.Redirect("/meet");
                    return Task.CompletedTask;
                });

                _app.MapGet("/meet", async ctx =>
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(BuildMeetHtml(RoomCode));
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
                        Participants.TryGetValue(user, out var pInfo) &&
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
                    string user = ctx.Request.Query["user"].ToString();
                    bool isScreen = ctx.Request.Query["screen"] == "1";
                    if (string.IsNullOrWhiteSpace(user)) user = "Katılımcı";

                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms);
                    byte[] bytes = ms.ToArray();

                    if (bytes.Length > 100 && bytes.Length < 900_000)
                    {
                        var info = Participants.GetOrAdd(user, u => new MeetParticipantInfo { Name = u });
                        info.LastSeenUtc = DateTime.UtcNow;
                        info.LatestCameraJpeg = bytes;
                        info.IsSharingScreen = isScreen;
                        SyncParticipantsToCompositor();
                    }
                    ctx.Response.StatusCode = 200;
                });

                // Circle Remote Uzaktan Fare ve Klavye Kontrol Endpoint'i
                _app.MapPost("/api/remote-input", async ctx =>
                {
                    if (!AllowRemoteControl)
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("disabled");
                        return;
                    }

                    try
                    {
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        string body = await sr.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;

                        string type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : "";
                        double normX = root.TryGetProperty("x", out var xProp) ? xProp.GetDouble() : 0.5;
                        double normY = root.TryGetProperty("y", out var yProp) ? yProp.GetDouble() : 0.5;
                        int btn = root.TryGetProperty("button", out var bProp) ? bProp.GetInt32() : 0;
                        string key = root.TryGetProperty("key", out var kProp) ? kProp.GetString() : "";

                        ExecuteRemoteInputOnHost(type, normX, normY, btn, key);
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
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        var info = Participants.GetOrAdd(user, u => new MeetParticipantInfo { Name = u });
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

                    var payload = new
                    {
                        room = RoomCode,
                        allowRemote = AllowRemoteControl,
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
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
                });

                _app.MapPost("/api/chat", async ctx =>
                {
                    using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    string body = await sr.ReadToEndAsync();
                    var doc = JsonDocument.Parse(body);
                    string sender = doc.RootElement.TryGetProperty("sender", out var s) ? s.GetString() : "Katılımcı";
                    string text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : "";
                    bool hand = doc.RootElement.TryGetProperty("hand", out var h) && h.GetBoolean();

                    if (!string.IsNullOrWhiteSpace(sender))
                    {
                        var info = Participants.GetOrAdd(sender, u => new MeetParticipantInfo { Name = u });
                        info.LastSeenUtc = DateTime.UtcNow;
                        if (hand) info.HasRaisedHand = true;
                        SyncParticipantsToCompositor();
                    }

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
                });

                await _app.StartAsync();
                IsRunning = true;

                // Önce yerel IP bilgisini 6 haneli koda kaydet, ardından genel internet tünelini başlat
                _ = PublishRoomCodeMappingAsync();
                _ = StartPublicInternetTunnelAsync();

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

                while (!_sshTunnelProcess.HasExited)
                {
                    string line = await _sshTunnelProcess.StandardOutput.ReadLineAsync();
                    if (line == null) break;

                    var m = Regex.Match(line, @"https://[a-zA-Z0-9\-]+\.lhr\.life");
                    if (m.Success)
                    {
                        PublicInternetUrl = m.Value.TrimEnd('/') + "/meet";
                        await PublishRoomCodeMappingAsync();
                        OnTunnelReady?.Invoke(PublicInternetUrl);
                        break;
                    }
                }
            }
            catch { }
        }

        private async Task PublishRoomCodeMappingAsync()
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    code = ShortCodeDigits,
                    room = RoomCode,
                    lanUrl = JoinUrl,
                    publicUrl = PublicInternetUrl
                });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _http.PostAsync($"https://ntfy.sh/circlemix_code_{ShortCodeDigits}", content);
            }
            catch { }
        }

        /// <summary>
        /// Kullanıcının girdiği 6 haneli kodu (Örn: 482-915) veya linki çözer ve bağlanılacak URL'yi döndürür.
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
            if (digits == ShortCodeDigits)
            {
                return !string.IsNullOrWhiteSpace(PublicInternetUrl) ? PublicInternetUrl : JoinUrl;
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

                                if (!string.IsNullOrWhiteSpace(pubUrl))
                                    return pubUrl;
                                if (!string.IsNullOrWhiteSpace(lanUrl))
                                    return lanUrl;
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private void ExecuteRemoteInputOnHost(string type, double normX, double normY, int button, string key)
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                int x = Math.Clamp((int)(normX * bounds.Width), 0, bounds.Width - 1);
                int y = Math.Clamp((int)(normY * bounds.Height), 0, bounds.Height - 1);

                switch (type)
                {
                    case "move":
                        SetCursorPos(x, y);
                        break;

                    case "click":
                        SetCursorPos(x, y);
                        if (button == 2)
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

                    case "dblclick":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        break;

                    case "mousedown":
                        SetCursorPos(x, y);
                        mouse_event(button == 2 ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        break;

                    case "mouseup":
                        SetCursorPos(x, y);
                        mouse_event(button == 2 ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        break;

                    case "key":
                        if (!string.IsNullOrEmpty(key))
                        {
                            string mapped = key switch
                            {
                                "Enter" => "{ENTER}",
                                "Backspace" => "{BACKSPACE}",
                                "Tab" => "{TAB}",
                                "Escape" => "{ESC}",
                                "Delete" => "{DEL}",
                                "ArrowUp" => "{UP}",
                                "ArrowDown" => "{DOWN}",
                                "ArrowLeft" => "{LEFT}",
                                "ArrowRight" => "{RIGHT}",
                                _ => key.Length == 1 ? key : ""
                            };
                            if (!string.IsNullOrEmpty(mapped))
                                SendKeys.SendWait(mapped);
                        }
                        break;
                }
            }
            catch { }
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
                Text = text,
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
            if (!IsRunning || _app == null) return;
            IsRunning = false;
            try
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            catch { }
            _app = null;
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

        private static string BuildMeetHtml(string roomCode)
        {
            return @"<!DOCTYPE html>
<html lang='tr'>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>Circle Meet & Uzaktan Bağlantı — Kod: " + roomCode + @"</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0;font-family:'Segoe UI',system-ui,sans-serif;}
  body{background:#140E38;color:#FFFFFF;display:flex;flex-direction:column;height:100vh;overflow:hidden;}
  header{height:54px;background:#1C134C;border-bottom:1px solid #3B2A94;display:flex;align-items:center;justify-content:space-between;padding:0 20px;}
  .brand{display:flex;align-items:center;gap:12px;font-weight:700;font-size:15px;color:#FFFFFF;}
  .badge{background:#EF5F18;color:#FFFFFF;padding:3px 10px;border-radius:99px;font-size:11.5px;font-weight:700;}
  .room{background:#261A66;border:1px solid #3B2A94;color:#EF5F18;padding:4px 12px;border-radius:6px;font-size:13px;font-weight:700;}
  main{flex:1;display:grid;grid-template-columns:1fr 330px;gap:12px;padding:12px;overflow:hidden;}
  .stage{background:#1C134C;border:1px solid #3B2A94;border-radius:12px;display:flex;flex-direction:column;overflow:hidden;}
  .video-wrap{flex:1;display:flex;align-items:center;justify-content:center;background:#0D0926;position:relative;overflow:hidden;}
  #liveStream{max-width:100%;max-height:100%;object-fit:contain;user-select:none;}
  .tiles-strip{position:absolute;bottom:14px;left:14px;display:flex;gap:10px;z-index:5;}
  #selfVideo{width:180px;height:102px;background:#261A66;border:2px solid #EF5F18;border-radius:10px;object-fit:cover;display:none;}
  .controls{height:60px;background:#1C134C;border-top:1px solid #3B2A94;display:flex;align-items:center;justify-content:center;gap:10px;flex-wrap:wrap;}
  button{background:#261A66;color:#FFFFFF;border:1px solid #3B2A94;padding:9px 14px;border-radius:8px;cursor:pointer;font-weight:600;font-size:12.5px;transition:.15s;}
  button:hover{background:#3B2A94;}
  button.primary{background:#EF5F18;border-color:#EF5F18;}
  button.active{background:#EF5F18;border-color:#FFFFFF;}
  button.hand{background:#EF5F18;border-color:#FFFFFF;}
  .sidebar{background:#1C134C;border:1px solid #3B2A94;border-radius:12px;display:flex;flex-direction:column;overflow:hidden;}
  .side-hdr{padding:11px 14px;border-bottom:1px solid #3B2A94;font-weight:700;font-size:12.5px;color:#FFFFFF;display:flex;justify-content:space-between;}
  #chatBox{flex:1;padding:12px;overflow-y:auto;display:flex;flex-direction:column;gap:8px;font-size:13px;}
  .msg{background:#261A66;padding:8px 11px;border-radius:8px;line-height:1.4;}
  .msg.inst{background:#261A66;border-left:3px solid #EF5F18;}
  .msg.hand{background:#261A66;border-left:3px solid #FFFFFF;}
  .meta{font-size:11px;color:#C4B5FD;margin-bottom:3px;display:flex;justify-content:space-between;}
  .chat-in{padding:10px;border-top:1px solid #3B2A94;display:flex;gap:8px;}
  input{background:#140E38;border:1px solid #3B2A94;color:#fff;padding:8px 10px;border-radius:6px;font-size:13px;flex:1;}
</style>
</head>
<body>
<header>
  <div class='brand'>
    <span class='badge'>● CIRCLE CANLI ODA</span>
    <span>CIRCLE MEET & REMOTE</span>
  </div>
  <div style='display:flex;gap:10px;align-items:center;'>
    <input id='userName' value='Katılımcı_" + Random.Shared.Next(10, 99) + @"' style='width:145px;flex:none;' title='Adınız'/>
    <span class='room'>Circle Kodu: " + roomCode + @"</span>
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
      <button id='btnCam' onclick='toggleSelfCam()'>📷 Kameramı Aç</button>
      <button id='btnScreen' onclick='toggleScreenShare()'>🖥️ Ekranımı Paylaş</button>
      <button id='btnRemote' onclick='toggleRemoteControl()'>🖱️ Circle Uzaktan Kontrol: KAPALI</button>
      <button class='hand' onclick='raiseHand()'>✋ Söz İste</button>
      <button onclick='toggleFullScreen()'>⛶ Tam Ekran</button>
    </div>
  </div>
  <div class='sidebar'>
    <div class='side-hdr'>
      <span>💬 CIRCLE SOHBET & KATILIMCILAR</span>
      <span id='partCount'>1 Kişi</span>
    </div>
    <div id='chatBox'></div>
    <div class='chat-in'>
      <input id='msgInput' placeholder='Mesaj veya soru yazın...' onkeydown='if(event.key===""Enter"")sendMsg()'/>
      <button class='primary' onclick='sendMsg()'>Gönder</button>
    </div>
  </div>
</main>
<script>
  const img = document.getElementById('liveStream');
  const selfVideo = document.getElementById('selfVideo');
  const sendCanvas = document.getElementById('sendCanvas');
  const ctx2d = sendCanvas.getContext('2d');
  let activeStream = null;
  let isScreen = false;
  let remoteControlActive = false;
  let hostAllowsRemote = false;

  setInterval(() => { img.src = '/api/frame.jpg?t=' + Date.now(); }, 130);

  setInterval(async () => {
    if (!activeStream || selfVideo.readyState < 2) return;
    try {
      ctx2d.drawImage(selfVideo, 0, 0, 320, 180);
      sendCanvas.toBlob(async (blob) => {
        if (!blob) return;
        const u = encodeURIComponent(document.getElementById('userName').value || 'Katılımcı');
        await fetch('/api/participant-frame?user=' + u + '&screen=' + (isScreen ? '1' : '0'), {
          method: 'POST',
          body: blob
        });
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
    if(!remoteControlActive) return;
    try{
      await fetch('/api/remote-input', { method:'POST', body: JSON.stringify(payload) });
    }catch(e){}
  }

  img.addEventListener('click', (e) => {
    if(!remoteControlActive) return;
    const c = getNormCoords(e);
    sendRemote({ type:'click', x:c.x, y:c.y, button:0 });
  });

  img.addEventListener('dblclick', (e) => {
    if(!remoteControlActive) return;
    const c = getNormCoords(e);
    sendRemote({ type:'dblclick', x:c.x, y:c.y, button:0 });
  });

  img.addEventListener('contextmenu', (e) => {
    if(!remoteControlActive) return;
    e.preventDefault();
    const c = getNormCoords(e);
    sendRemote({ type:'click', x:c.x, y:c.y, button:2 });
  });

  let lastMove = 0;
  img.addEventListener('mousemove', (e) => {
    if(!remoteControlActive) return;
    const now = Date.now();
    if(now - lastMove < 65) return;
    lastMove = now;
    const c = getNormCoords(e);
    sendRemote({ type:'move', x:c.x, y:c.y, button:0 });
  });

  document.getElementById('stageWrap').addEventListener('keydown', (e) => {
    if(!remoteControlActive) return;
    e.preventDefault();
    sendRemote({ type:'key', key:e.key });
  });

  function toggleRemoteControl(){
    if(!hostAllowsRemote){
      alert('Karşı bilgisayar henüz ""Uzaktan Fare & Klavye Kontrolüne İzin Ver"" kutucuğunu açmamış. Lütfen karşı taraftan izni açmasını isteyin.');
      return;
    }
    remoteControlActive = !remoteControlActive;
    const btn = document.getElementById('btnRemote');
    btn.className = remoteControlActive ? 'active' : '';
    btn.innerText = remoteControlActive
      ? '🖱️ Circle Uzaktan Kontrol: AKTİF (Ekrana Tıkla/Yaz)'
      : '🖱️ Circle Uzaktan Kontrol: KAPALI';
    if(remoteControlActive) document.getElementById('stageWrap').focus();
  }

  async function pollState(){
    try{
      const u = encodeURIComponent(document.getElementById('userName').value || 'Katılımcı');
      const r = await fetch('/api/state?user=' + u);
      const d = await r.json();
      hostAllowsRemote = !!d.allowRemote;
      document.getElementById('partCount').innerText = (d.participants.length + 1) + ' Katılımcı';
      const box = document.getElementById('chatBox');
      box.innerHTML = d.messages.map(m => `
        <div class='msg ${m.IsInstructor ? ""inst"" : (m.IsHandRaise ? ""hand"" : """")}'>
          <div class='meta'><b>${m.Sender}</b><span>${m.Time}</span></div>
          <div>${m.Text}</div>
        </div>`).join('');
      box.scrollTop = box.scrollHeight;
    }catch(e){}
  }
  setInterval(pollState, 1000);
  pollState();

  async function sendMsg(){
    const inp = document.getElementById('msgInput');
    const txt = inp.value.trim();
    if(!txt) return;
    inp.value = '';
    await fetch('/api/chat', {
      method:'POST',
      body: JSON.stringify({ sender: document.getElementById('userName').value || 'Katılımcı', text: txt, hand: false })
    });
    pollState();
  }

  async function raiseHand(){
    const name = document.getElementById('userName').value || 'Katılımcı';
    await fetch('/api/chat', {
      method:'POST',
      body: JSON.stringify({ sender: name, text: '✋ Söz istiyor! (El kaldırdı)', hand: true })
    });
    pollState();
  }

  async function toggleSelfCam(){
    if(activeStream){
      activeStream.getTracks().forEach(t=>t.stop());
      activeStream = null;
      selfVideo.style.display = 'none';
      document.getElementById('btnCam').className = '';
      document.getElementById('btnScreen').className = '';
      return;
    }
    try{
      activeStream = await navigator.mediaDevices.getUserMedia({video:true,audio:false});
      isScreen = false;
      selfVideo.srcObject = activeStream;
      selfVideo.style.display = 'block';
      document.getElementById('btnCam').className = 'active';
    }catch(e){ alert('Kamera izni alınamadı.'); }
  }

  async function toggleScreenShare(){
    if(activeStream){
      activeStream.getTracks().forEach(t=>t.stop());
      activeStream = null;
      selfVideo.style.display = 'none';
      document.getElementById('btnCam').className = '';
      document.getElementById('btnScreen').className = '';
    }
    try{
      activeStream = await navigator.mediaDevices.getDisplayMedia({video:true});
      isScreen = true;
      selfVideo.srcObject = activeStream;
      selfVideo.style.display = 'block';
      document.getElementById('btnScreen').className = 'active';
    }catch(e){}
  }

  function toggleFullScreen(){
    const wrap = document.querySelector('.video-wrap');
    if(!document.fullscreenElement) wrap.requestFullscreen();
    else document.exitFullscreen();
  }
</script>
</body>
</html>";
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

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }
}
