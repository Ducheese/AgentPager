namespace AgentPager.Bridge;

/// <summary>隧道进程的公共状态。两个提供方（ngrok / cloudflared）共用一套 UI 语义。</summary>
public enum TunnelStatus
{
    Stopped,
    Starting,
    Running,
    Failed,
    NotFound,
}

/// <summary>
/// 桥运行时只认这个接口：拉起、停止、当前域名、当前状态。进程细节（stdout JSON / stderr
/// 日志、pid 文件、Job Object）留在各自实现里。
/// </summary>
public interface ITunnelService : IAsyncDisposable
{
    TunnelStatus Status { get; }
    string? ActiveHost { get; }
    Task StartAsync();
    Task StopAsync();
}
