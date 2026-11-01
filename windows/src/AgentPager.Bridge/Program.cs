using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using AgentPager.Core;

namespace AgentPager.Bridge;

public static class Program
{
    private const string MutexName = "Local\\AgentPager.Bridge.Singleton";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static void EnablePerMonitorDpi()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(ConsoleKeystrokes.WorkerSwitch, StringComparer.OrdinalIgnoreCase))
            return ConsoleKeystrokes.RunWorker(args);
        if (args.Contains("--hook", StringComparer.OrdinalIgnoreCase))
            return HookClient.RunAsync(args).GetAwaiter().GetResult();
        if (args.Contains("--uninstall-hook", StringComparer.OrdinalIgnoreCase))
            return Maintenance.Uninstall();

        EnablePerMonitorDpi();
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            SingleInstanceSignal.Send();
            return 0;
        }

        var app = new App(
            args.Contains("--first-run", StringComparer.OrdinalIgnoreCase),
            args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        return app.Run();
    }
}

internal static class HookClient
{
    public static async Task<int> RunAsync(string[] args)
    {
        var source = ResolveSource(args);
        try
        {
            using var input = Console.OpenStandardInput();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            var payloadData = memory.ToArray();

            // 按来源解码载荷，仅用于判断事件类型以设置等待时长；解析失败保持 fail-open。
            var isPermission = source == HookSource.Claude
                ? IsClaudePermission(payloadData)
                : IsCodexPermission(payloadData);

            var envelope = WrapInEnvelope(payloadData, source);
            using var client = new TcpClient();
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync("127.0.0.1", 49361, connectTimeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(envelope);
            await stream.WriteAsync("\n"u8.ToArray());
            await stream.FlushAsync();

            // 权限请求可能等用户在手机上操作很久；与 settings.json 的 hook timeout 对齐。
            var wait = isPermission
                ? (source == HookSource.Claude ? TimeSpan.FromHours(24) : TimeSpan.FromHours(1))
                : TimeSpan.FromSeconds(5);
            using var readTimeout = new CancellationTokenSource(wait);
            var response = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, readTimeout.Token);
                if (count == 0) break;
                await response.WriteAsync(buffer.AsMemory(0, count), readTimeout.Token);
                if (buffer.AsSpan(0, count).Contains((byte)'\n')) break;
            }
            if (response.Length > 0)
            {
                response.Position = 0;
                await response.CopyToAsync(Console.OpenStandardOutput());
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or JsonException)
        {
            // Hook 必须 fail-open，Bridge 不可用不能阻塞 Agent。
        }
        return 0;
    }

    private static HookSource ResolveSource(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--source", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<HookSource>(args[index + 1], ignoreCase: true, out var source))
                return source;
        }
        return HookSource.Codex;
    }

    private static bool IsCodexPermission(byte[] payloadData)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CodexHookPayload>(payloadData, WireJson.Options);
            return payload?.HookEventName == CodexHookEventName.PermissionRequest;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsClaudePermission(byte[] payloadData)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ClaudeHookPayload>(payloadData, WireJson.Options);
            return payload?.HookEventName == "PermissionRequest";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>把原始 Hook 载荷包进 {hook_source, payload} 信封。</summary>
    private static byte[] WrapInEnvelope(byte[] payloadData, HookSource source)
    {
        try
        {
            var payloadNode = JsonNode.Parse(payloadData);
            var envelope = new JsonObject
            {
                ["hook_source"] = source.ToString().ToLowerInvariant(),
                ["payload"] = payloadNode,
            };
            return System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());
        }
        catch (JsonException)
        {
            // 极端情况下无法解析时退化为原样发送，服务器仍按 codex 兼容。
            return payloadData;
        }
    }
}

internal static class Maintenance
{
    public static int Uninstall()
    {
        var ok = 0;
        try { _ = new CodexHookConfiguration().Uninstall(); }
        catch { ok = 1; }
        try { _ = new ClaudeHookConfiguration().Uninstall(); }
        catch { ok = 1; }
        try { _ = new OpenCodePluginConfiguration().Uninstall(); }
        catch { ok = 1; }
        try { _ = new AgyHookConfiguration().Uninstall(); }
        catch { ok = 1; }
        try { StartupRegistration.Disable(); }
        catch { ok = 1; }
        return ok;
    }
}

internal static class SingleInstanceSignal
{
    private const string PipeName = "AgentPager.Bridge.Activate";

    public static void Send()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(200);
            pipe.WriteByte(1);
        }
        catch (Exception error) when (error is IOException or TimeoutException) { }
    }

    public static async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = pipe.ReadByte();
                activate();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }
}

public sealed class App : System.Windows.Application
{
    private readonly bool _firstRun;
    private readonly bool _background;
    /// <summary>
    /// 退出流程只允许跑一次。托盘菜单点下后还要一两秒才彻底消失，没有这道闸用户会连点，
    /// 而并发 Dispose 会把 CTS 和监听器拆两遍（第二次直接抛 ObjectDisposedException）。
    /// </summary>
    private bool _exiting;
    private BridgeRuntime? _runtime;
    private TrayController? _tray;
    private MainWindow? _window;
    private CancellationTokenSource? _cancellation;
    /// <summary>退出清理的总预算；到点直接 Shutdown，隧道子进程由 Job Object 收尸。</summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(2);

    public App(bool firstRun, bool background)
    {
        _firstRun = firstRun;
        _background = background;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _cancellation = new();
        _runtime = new BridgeRuntime();
        _window = new MainWindow(_runtime);
        _tray = new TrayController(_runtime, ShowWindow, ExitApplication);
        if (!_background) _runtime.Settings.Save();
        _runtime.StateChanged += () => Dispatcher.Invoke(() =>
        {
            // 退出期间隧道会回调 Stopped；此时托盘可能已经撤掉，再刷新会抛 ObjectDisposedException。
            if (_exiting) return;
            _window?.Refresh();
            _tray?.Refresh();
        });
        _window.HideRequested += () => _window.Hide();
        _ = SingleInstanceSignal.ListenAsync(() => Dispatcher.Invoke(ShowWindow), _cancellation.Token);
        await _runtime.StartAsync(_cancellation.Token);
        if (_firstRun || !_runtime.Settings.HasCompletedFirstRun)
        {
            _runtime.Settings.HasCompletedFirstRun = true;
            _runtime.Settings.Save();
        }
        if (!_background) ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        // 第一次点击就撤掉托盘图标：用户立刻有反馈，也不会以为没点上而反复点。
        _tray?.Hide();
        _window?.Hide();
        // 离开 WinForms 菜单的点击栈再 Dispose，避免在菜单自己的事件里拆 ContextMenuStrip。
        await Task.Yield();
        _cancellation?.Cancel();
        if (_runtime is not null)
        {
            Task? dispose = null;
            try { dispose = _runtime.DisposeAsync().AsTask(); }
            catch (Exception error) { Debug.WriteLine($"退出清理启动失败：{error}"); }
            if (dispose is not null)
                await Task.WhenAny(dispose, Task.Delay(ShutdownBudget));
        }
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }
}
