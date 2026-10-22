using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed class LanBridgeServer(
    Func<string> pairingProvider,
    Func<string?, bool> authorize,
    Func<string> snapshotProvider,
    Func<string, Task> messageHandler,
    Action<int> countChanged,
    DailyFileLog log,
    Func<string>? rotateProvider = null) : IAsyncDisposable
{
    /// <summary>
    /// 手机在 WebSocket 握手上带配对密钥的请求头。密钥就是配对文本里那个 Base64 串。
    /// </summary>
    public const string SecretHeader = "X-AgentGrid-Secret";

    private sealed class Client(WebSocket socket) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;

        public async Task Send(string text, CancellationToken cancellationToken = default)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally { _sendLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bridge shutdown", CancellationToken.None); }
                catch (WebSocketException) { }
            Socket.Dispose();
            _sendLock.Dispose();
        }
    }

    /// <summary>手机侧（局域网/隧道）接入端口。ngrok 隧道也转发到这里，两处必须同源。</summary>
    public const int PhonePort = 49362;

    private readonly ConcurrentDictionary<Guid, Client> _clients = [];
    private WebApplication? _application;

    public int ClientCount => _clients.Count;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = new WebApplicationOptions { Args = [], ApplicationName = typeof(LanBridgeServer).Assembly.FullName };
        var builder = WebApplication.CreateSlimBuilder(options);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{PhonePort}");
        builder.Services.AddRouting();
        // multipart 在 ReadFormAsync 里就会缓冲：把上限压到 1MB + 头部余量，
        // 让超限请求在缓冲阶段就被拒，而不是先吃下 30MB 再检查。
        builder.Services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = AttachmentStore.MaxFileBytes + 64 * 1024);
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/pairing", async context =>
        {
            if (!IsLoopbackRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(pairingProvider());
        });

        app.MapPost("/pairing/rotate", async context =>
        {
            if (rotateProvider is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!IsLoopbackRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(rotateProvider());
        });

        app.Map("/agentgrid", HandleWebSocket);

        app.MapPost("/api/attachments/upload", async context =>
        {
            var provided = context.Request.Headers[SecretHeader].ToString();
            if (!authorize(string.IsNullOrEmpty(provided) ? null : provided))
            {
                log.Write("WARN", $"附件上传被拒：配对密钥无效（{context.Connection.RemoteIpAddress}）");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!context.Request.HasFormContentType)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("需要 multipart/form-data（字段名 file）");
                return;
            }
            // Content-Length 能提前判的就在缓冲前判掉（Kestrel 的 multipart 上限是第二道闸）。
            var maxRequestBytes = AttachmentStore.MaxFileBytes + 64 * 1024;
            if (context.Request.ContentLength is long declared && declared > maxRequestBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsync($"文件超过上限（{AttachmentStore.MaxFileBytes} 字节）");
                return;
            }
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsync($"文件超过上限（{AttachmentStore.MaxFileBytes} 字节）");
                return;
            }
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("没有收到文件");
                return;
            }
            if (file.Length > AttachmentStore.MaxFileBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsync($"文件超过上限（{AttachmentStore.MaxFileBytes} 字节）");
                return;
            }
            AttachmentStore.SavedAttachment saved;
            try
            {
                // 直接把上传流写盘：不再 MemoryStream + ToArray 复制两份。
                // meta 是手机端的压缩参数（q60_1440x810），Bridge 过滤后拼进文件名便于排查。
                await using var source = file.OpenReadStream();
                saved = AttachmentStore.Save(source, file.FileName, meta: form["meta"].ToString());
            }
            catch (InvalidDataException error)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(error.Message);
                return;
            }
            log.Write("INFO", $"[附件] 已暂存 {saved.FileName}（{saved.Size} 字节）");
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(saved, WireJson.Options));
        });
        await app.StartAsync(cancellationToken);
        _application = app;
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        // 严防反向代理/公网隧道访问本地配对密钥接口：
        // 1. Host 必须为 127.0.0.1 或 localhost 回环；
        // 2. RemoteIpAddress 必须为 Loopback 本地回环；
        // 3. 任何带有反向代理/公网穿透请求头的（X-Forwarded-*, Forwarded, X-Real-IP, ngrok-trace-id 等），直接拒绝。
        // 避免 ngrok 或任何公网反代在本地端口转发时把配对文本（含 secret）裸露给公网。
        var hostHeader = context.Request.Host.Host;
        var isLoopbackHost = hostHeader.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                             hostHeader.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        return isLoopbackHost &&
               context.Connection.RemoteIpAddress is not null &&
               (IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
                (context.Connection.RemoteIpAddress.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(context.Connection.RemoteIpAddress.MapToIPv4()))) &&
               !context.Request.Headers.ContainsKey("Origin") &&
               !context.Request.Headers.ContainsKey("X-Forwarded-For") &&
               !context.Request.Headers.ContainsKey("X-Forwarded-Proto") &&
               !context.Request.Headers.ContainsKey("X-Forwarded-Host") &&
               !context.Request.Headers.ContainsKey("Forwarded") &&
               !context.Request.Headers.ContainsKey("X-Real-IP") &&
               !context.Request.Headers.ContainsKey("ngrok-trace-id");
    }

    public async Task DisconnectAllAsync(string reason = "Secret rotated")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (var pair in _clients.ToList())
        {
            try
            {
                if (pair.Value.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await pair.Value.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
            }
            catch (Exception) { }
            await Remove(pair.Key);
        }
    }

    public async Task BroadcastAsync(string text)
    {
        foreach (var pair in _clients.ToList())
        {
            try { await pair.Value.Send(text); }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
                await Remove(pair.Key);
            }
        }
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // 握手必须验密钥。这个端口本来是局域网专用的，但一旦经隧道挂到公网（ngrok 之类），
        // 不验就等于谁拿到域名谁就能连上收走全部快照（任务标题、正文、额度）。
        // 控制消息本来就有 HMAC 签名挡着，敞开的只有"读" —— 而读恰恰是泄露面最大的那半边。
        var provided = context.Request.Headers[SecretHeader].ToString();
        if (!authorize(string.IsNullOrEmpty(provided) ? null : provided))
        {
            log.Write("WARN", $"WebSocket 握手上配对密钥无效，已拒绝：{context.Connection.RemoteIpAddress}");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        countChanged(_clients.Count);
        try
        {
            await client.Send(snapshotProvider(), context.RequestAborted);
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                await message.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted);
                if (!result.EndOfMessage) continue;
                if (message.Length > 1024 * 1024) break;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                await messageHandler(text);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException)
        {
            log.Write("WARN", $"WebSocket 关闭：{error.Message}");
        }
        finally { await Remove(id); }
    }

    private async Task Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var client))
        {
            await client.DisposeAsync();
            countChanged(_clients.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _clients.Keys.ToList()) await Remove(id);
        if (_application is not null)
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
