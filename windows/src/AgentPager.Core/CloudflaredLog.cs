using System.Text.RegularExpressions;

namespace AgentPager.Core;

/// <summary>
/// cloudflared 的日志解析。Quick Tunnel 的公网域名只打在 **stderr** 的日志行里
/// （ngrok 是 stdout JSON + 4040 API，两者没有可复用的地方），而且 cloudflared 没有
/// 类似 <c>/api/tunnels</c> 的本地查询接口，所以只能逐行读 stderr。
/// </summary>
public static class CloudflaredLog
{
    // 必须排除 api.trycloudflare.com：cloudflared 建 Quick Tunnel 时会先请求
    // `https://api.trycloudflare.com/tunnel`，那行日志里也带 trycloudflare 域名。
    // 旧正则把它当成公网域名报给手机，结果桥显示"在线：api.trycloudflare.com"、
    // 配对文本也写进这个假域名——隧道实际没建好（2026-10-31 开机自启实测）。
    private static readonly Regex QuickTunnelUrl = new(
        @"https://(?!api\.)([a-z0-9-]+\.trycloudflare\.com)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 从一行 cloudflared 日志里提取 Quick Tunnel 域名；不是域名行返回 null。
    /// 只认 trycloudflare.com —— 日志里还有 region1.v2.argotunnel.com 之类的连接目标，
    /// 它们不是给手机用的公网入口。
    /// </summary>
    public static string? TryParseQuickTunnelHost(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var match = QuickTunnelUrl.Match(line);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
}
