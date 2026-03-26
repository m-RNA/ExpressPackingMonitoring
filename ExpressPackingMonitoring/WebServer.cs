#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// 内嵌轻量 HTTP 服务器，供局域网客户端搜索、播放和下载视频。
    /// 基于 HttpListener，无需额外 NuGet 依赖。
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly VideoDatabase _db;
        private readonly CancellationTokenSource _cts = new();
        private Task _listenTask;
        private bool _disposed;

        public int Port { get; }

        public WebServer(VideoDatabase db, int port = 5280)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            Port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{port}/");
        }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // URL ACL 未注册，尝试自动注册后重试
                RegisterUrlAcl(Port);
                _listener.Start();
            }
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        }

        /// <summary>
        /// 注册 URL ACL 和防火墙规则，需要管理员权限时会弹出 UAC 提示。
        /// </summary>
        private static void RegisterUrlAcl(int port)
        {
            string url = $"http://+:{port}/";
            RunElevatedCmd($"netsh http add urlacl url={url} user=Everyone");
            // 同时确保防火墙规则存在
            RunElevatedCmd($"netsh advfirewall firewall add rule name=\"快递打包监控 Web服务\" dir=in action=allow protocol=TCP localport={port}");
        }

        private static void RunElevatedCmd(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C {arguments}",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch { }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx), token);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                string method = ctx.Request.HttpMethod;

                // CORS
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

                switch (path)
                {
                    case "" or "/":
                        ServeIndexPage(ctx);
                        break;
                    case "/api/videos":
                        HandleSearchVideos(ctx);
                        break;
                    default:
                        if (path.StartsWith("/api/videos/") && path.EndsWith("/download"))
                            HandleDownload(ctx, path);
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                            HandlePlay(ctx, path);
                        else
                            SendJson(ctx, 404, new { error = "Not Found" });
                        break;
                }
            }
            catch (Exception ex)
            {
                try { SendJson(ctx, 500, new { error = ex.Message }); } catch { }
            }
        }

        // ───── API: 搜索视频 ─────
        private void HandleSearchVideos(HttpListenerContext ctx)
        {
            var qs = ctx.Request.QueryString;
            string keyword = qs["keyword"] ?? qs["q"] ?? "";

            if (!DateTime.TryParse(qs["start"], out var startDate))
                startDate = DateTime.Today.AddDays(-7);
            if (!DateTime.TryParse(qs["end"], out var endDate))
                endDate = DateTime.Today;

            int page = int.TryParse(qs["page"], out var p) ? Math.Max(1, p) : 1;
            int pageSize = int.TryParse(qs["size"], out var s) ? Math.Clamp(s, 1, 100) : 50;

            var allRecords = _db.QueryVideos(startDate, endDate, string.IsNullOrWhiteSpace(keyword) ? null : keyword);
            // 只返回未删除且文件存在的
            var valid = allRecords.Where(r => !r.IsDeleted).ToList();

            int total = valid.Count;
            var paged = valid.Skip((page - 1) * pageSize).Take(pageSize).Select(r => new
            {
                r.Id,
                r.OrderId,
                r.Mode,
                r.FileName,
                sizeMB = Math.Round(r.FileSizeBytes / 1048576.0, 1),
                startTime = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = Math.Round(r.DurationSeconds, 0),
                duration = TimeSpan.FromSeconds(r.DurationSeconds).ToString(@"mm\:ss"),
                exists = File.Exists(r.FilePath)
            });

            SendJson(ctx, 200, new { total, page, pageSize, data = paged });
        }

        // ───── API: 流式播放 (支持 Range) ─────
        private void HandlePlay(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/play");
            if (record == null || !File.Exists(record.FilePath))
            {
                SendJson(ctx, 404, new { error = "文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, record.FilePath, inline: true);
        }

        // ───── API: 下载 ─────
        private void HandleDownload(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/download");
            if (record == null || !File.Exists(record.FilePath))
            {
                SendJson(ctx, 404, new { error = "文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, record.FilePath, inline: false);
        }

        // ───── 文件传输 (支持 Range 请求实现拖拽播放) ─────
        private static void ServeFileWithRange(HttpListenerContext ctx, string filePath, bool inline)
        {
            var fi = new FileInfo(filePath);
            long fileLength = fi.Length;
            string ext = fi.Extension.ToLowerInvariant();
            string mime = ext switch { ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", _ => "application/octet-stream" };

            ctx.Response.ContentType = mime;
            if (!inline)
            {
                ctx.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fi.Name)}\"");
            }
            ctx.Response.Headers.Add("Accept-Ranges", "bytes");

            string rangeHeader = ctx.Request.Headers["Range"];
            long start = 0, end = fileLength - 1;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeValue = rangeHeader.Substring(6);
                var parts = rangeValue.Split('-');
                if (long.TryParse(parts[0], out long rs)) start = rs;
                if (parts.Length > 1 && long.TryParse(parts[1], out long re)) end = re;
                if (start < 0) start = 0;
                if (end >= fileLength) end = fileLength - 1;

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileLength}");
            }
            else
            {
                ctx.Response.StatusCode = 200;
            }

            long length = end - start + 1;
            ctx.Response.ContentLength64 = length;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[65536];
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = fs.Read(buffer, 0, toRead);
                if (read == 0) break;
                try { ctx.Response.OutputStream.Write(buffer, 0, read); }
                catch { break; } // 客户端断开
                remaining -= read;
            }
            ctx.Response.OutputStream.Close();
        }

        // ───── 根据 URL 中的 ID 查找记录 ─────
        private VideoRecord FindRecordFromPath(string path, string suffix)
        {
            string idStr = path.Replace("/api/videos/", "").Replace(suffix, "").Trim('/');
            if (!long.TryParse(idStr, out long id)) return null;
            return _db.GetVideoById(id);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // ───── JSON 响应 ─────
        private static void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // ───── 内嵌前端页面 ─────
        private static void ServeIndexPage(HttpListenerContext ctx)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] html = Encoding.UTF8.GetBytes(IndexHtml);
            ctx.Response.ContentLength64 = html.Length;
            ctx.Response.OutputStream.Write(html, 0, html.Length);
            ctx.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }

        // ═══════════════════════════════════════════════
        //  内嵌 HTML 单页应用
        // ═══════════════════════════════════════════════
        private const string IndexHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>快递打包录像回放</title>
<style>
  :root { --bg: #f0f2f5; --card: #fff; --primary: #1677ff; --text: #1f1f1f; --text2: #666; --border: #e8e8e8; --hover: #f5f5f5; }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: -apple-system, "Microsoft YaHei UI", sans-serif; background: var(--bg); color: var(--text); min-height: 100vh; }

  .header { background: linear-gradient(135deg, #1677ff 0%, #0958d9 100%); color: #fff; padding: 20px 0; text-align: center; }
  .header h1 { font-size: 22px; font-weight: 600; }
  .header p { font-size: 13px; opacity: .8; margin-top: 4px; }

  .container { max-width: 1000px; margin: 0 auto; padding: 20px; }

  .search-bar { background: var(--card); border-radius: 12px; padding: 18px; display: flex; flex-wrap: wrap; gap: 10px; align-items: center; box-shadow: 0 1px 3px rgba(0,0,0,.08); margin-bottom: 16px; }
  .search-bar label { font-size: 13px; color: var(--text2); }
  .search-bar input[type="date"], .search-bar input[type="text"] {
    height: 36px; border: 1px solid var(--border); border-radius: 6px; padding: 0 10px; font-size: 13px; outline: none; }
  .search-bar input:focus { border-color: var(--primary); }
  .search-bar input[type="text"] { flex: 1; min-width: 150px; }
  .btn { height: 36px; padding: 0 20px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; font-weight: 600; }
  .btn-primary { background: var(--primary); color: #fff; }
  .btn-primary:hover { background: #0958d9; }

  .results-info { font-size: 13px; color: var(--text2); margin-bottom: 12px; }

  .video-list { display: flex; flex-direction: column; gap: 8px; }
  .video-item {
    background: var(--card); border-radius: 10px; padding: 14px 18px; display: flex; align-items: center; justify-content: space-between;
    box-shadow: 0 1px 2px rgba(0,0,0,.05); transition: box-shadow .2s; cursor: default; }
  .video-item:hover { box-shadow: 0 2px 8px rgba(0,0,0,.1); }
  .video-info { flex: 1; }
  .video-info .order { font-size: 15px; font-weight: 600; }
  .video-info .meta { font-size: 12px; color: var(--text2); margin-top: 4px; display: flex; gap: 14px; flex-wrap: wrap; }
  .video-info .meta span { white-space: nowrap; }
  .video-actions { display: flex; gap: 8px; flex-shrink: 0; margin-left: 12px; }
  .btn-sm { height: 32px; padding: 0 14px; border-radius: 6px; font-size: 12px; border: 1px solid var(--border); background: var(--card); cursor: pointer; font-weight: 500; }
  .btn-sm:hover { background: var(--hover); }
  .btn-sm.play { border-color: var(--primary); color: var(--primary); }
  .btn-sm.play:hover { background: #e6f4ff; }
  .btn-sm.disabled { opacity: .4; pointer-events: none; }

  .pagination { display: flex; justify-content: center; gap: 8px; margin-top: 20px; }
  .pagination .btn-sm.active { background: var(--primary); color: #fff; border-color: var(--primary); }

  .player-overlay {
    display: none; position: fixed; inset: 0; background: rgba(0,0,0,.75); z-index: 100;
    justify-content: center; align-items: center; }
  .player-overlay.active { display: flex; }
  .player-box { background: #000; border-radius: 12px; overflow: hidden; max-width: 900px; width: 95%; position: relative; }
  .player-box video { width: 100%; max-height: 80vh; }
  .player-close { position: absolute; top: 10px; right: 14px; color: #fff; font-size: 28px; cursor: pointer; z-index: 2; line-height: 1; }
  .player-title { position: absolute; top: 12px; left: 16px; color: #fff; font-size: 14px; z-index: 2; text-shadow: 0 1px 3px rgba(0,0,0,.5); }

  .empty { text-align: center; padding: 60px 0; color: var(--text2); }
  .empty span { font-size: 48px; display: block; margin-bottom: 12px; }

  @media (max-width: 600px) {
    .video-item { flex-direction: column; align-items: flex-start; }
    .video-actions { margin: 10px 0 0; }
  }
</style>
</head>
<body>

<div class="header">
  <h1>📦 快递打包录像</h1>
  <p>局域网远程查看 · 搜索 · 下载</p>
</div>

<div class="container">
  <form class="search-bar" onsubmit="doSearch(); return false;">
    <label>开始</label>
    <input type="date" id="startDate">
    <label>结束</label>
    <input type="date" id="endDate">
    <input type="text" id="keyword" placeholder="输入单号搜索...">
    <button type="submit" class="btn btn-primary">🔍 搜索</button>
  </form>

  <div class="results-info" id="resultsInfo"></div>
  <div class="video-list" id="videoList"></div>
  <div class="pagination" id="pagination"></div>

  <div class="player-overlay" id="playerOverlay" onclick="closePlayer(event)">
    <div class="player-box" onclick="event.stopPropagation()">
      <span class="player-close" onclick="closePlayer()">&times;</span>
      <span class="player-title" id="playerTitle"></span>
      <video id="videoPlayer" controls></video>
    </div>
  </div>
</div>

<script>
let currentPage = 1;

(function init() {
  const today = new Date();
  const weekAgo = new Date(today);
  weekAgo.setDate(today.getDate() - 7);
  document.getElementById('startDate').value = fmt(weekAgo);
  document.getElementById('endDate').value = fmt(today);
  doSearch();
})();

function fmt(d) { return d.toISOString().slice(0, 10); }

function doSearch(page) {
  currentPage = page || 1;
  const params = new URLSearchParams({
    start: document.getElementById('startDate').value,
    end: document.getElementById('endDate').value,
    keyword: document.getElementById('keyword').value.trim(),
    page: currentPage,
    size: 20
  });
  fetch('/api/videos?' + params)
    .then(r => r.json())
    .then(render)
    .catch(e => {
      document.getElementById('videoList').innerHTML = '<div class="empty"><span>⚠️</span>请求失败: ' + e.message + '</div>';
    });
}

function render(res) {
  const list = document.getElementById('videoList');
  const info = document.getElementById('resultsInfo');
  const pagi = document.getElementById('pagination');

  if (!res.data || res.data.length === 0) {
    list.innerHTML = '<div class="empty"><span>📭</span>没有找到匹配的录像</div>';
    info.textContent = '';
    pagi.innerHTML = '';
    return;
  }

  info.textContent = '共 ' + res.total + ' 条记录，第 ' + res.page + ' 页';

  list.innerHTML = res.data.map(v => {
    const badge = v.mode === '退货' ? '🔄' : '📦';
    const sizeStr = v.sizeMB >= 1024 ? (v.sizeMB / 1024).toFixed(1) + ' GB' : v.sizeMB + ' MB';
    const disabled = !v.exists ? 'disabled' : '';
    const tip = !v.exists ? ' title="文件不存在"' : '';
    return '<div class="video-item">' +
      '<div class="video-info">' +
        '<div class="order">' + badge + ' ' + esc(v.orderId) + '</div>' +
        '<div class="meta">' +
          '<span>📅 ' + v.startTime + '</span>' +
          '<span>⏱ ' + v.duration + '</span>' +
          '<span>💾 ' + sizeStr + '</span>' +
          '<span>📄 ' + esc(v.fileName) + '</span>' +
        '</div>' +
      '</div>' +
      '<div class="video-actions">' +
        '<button class="btn-sm play ' + disabled + '"' + tip + ' onclick="playVideo(' + v.id + ',\'' + esc(v.orderId) + '\')">▶ 播放</button>' +
        '<button class="btn-sm ' + disabled + '"' + tip + ' onclick="downloadVideo(' + v.id + ')">⬇ 下载</button>' +
      '</div>' +
    '</div>';
  }).join('');

  // 分页
  const totalPages = Math.ceil(res.total / res.pageSize);
  if (totalPages <= 1) { pagi.innerHTML = ''; return; }
  let html = '';
  for (let i = 1; i <= totalPages && i <= 10; i++) {
    html += '<button class="btn-sm' + (i === res.page ? ' active' : '') + '" onclick="doSearch(' + i + ')">' + i + '</button>';
  }
  pagi.innerHTML = html;
}

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

function playVideo(id, title) {
  const player = document.getElementById('videoPlayer');
  player.src = '/api/videos/' + id + '/play';
  document.getElementById('playerTitle').textContent = title;
  document.getElementById('playerOverlay').classList.add('active');
  player.play();
}

function closePlayer(e) {
  if (e && e.target !== document.getElementById('playerOverlay')) return;
  const player = document.getElementById('videoPlayer');
  player.pause();
  player.src = '';
  document.getElementById('playerOverlay').classList.remove('active');
}

function downloadVideo(id) {
  window.open('/api/videos/' + id + '/download', '_blank');
}

document.addEventListener('keydown', e => { if (e.key === 'Escape') closePlayer(); });
</script>

</body>
</html>
""";
    }
}
