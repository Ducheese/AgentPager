namespace AgentPager.Core;

/// <summary>内置的外网隧道提供方。None = 只走局域网，不拉任何隧道进程。</summary>
public enum TunnelProvider
{
    None,
    Ngrok,
    Cloudflared,
}

public static class TunnelProviders
{
    /// <summary>
    /// 老设置文件里只有 <c>EnableNgrokTunnel</c>（2026-10 之前的格式）。没写 provider 时按它推断：
    /// 开过 ngrok 的继续走 ngrok，没开过的就是 None —— 不能因为升级把用户的隧道偷偷换掉。
    /// </summary>
    public static TunnelProvider Resolve(TunnelProvider? stored, bool legacyEnableNgrok) =>
        stored ?? (legacyEnableNgrok ? TunnelProvider.Ngrok : TunnelProvider.None);
}
