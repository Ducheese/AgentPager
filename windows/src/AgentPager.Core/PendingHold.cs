using System.Net.Sockets;

namespace AgentPager.Core;

/// <summary>
/// 等一条"攥在手里等手机裁决"的连接**被对端关掉**。
///
/// 为什么需要它：桥把 hook 进程的连接挂进待决表等手机答复，此前只在"回填 / 超时 / 别的事件宣布
/// 等待结束"时把它摘掉。hook 进程自己退出（脚本超时、被杀、Claude 退出）时 socket 先死，表里
/// 那条记录却一直留着——后续手机点"允许"会往一条死连接写（IOException），日志里也看不出原因。
///
/// 读 1 字节就够：对端在等到答复前不会再写；读到 0 表示它关了，抛异常同理（含我方主动 Dispose）。
/// 这只是"对端走了"的观测，不判断该不该判任务结束——那由调用方决定。
/// </summary>
public static class PendingHold
{
    public static async Task WaitForPeerCloseAsync(TcpClient client)
    {
        var buffer = new byte[1];
        try
        {
            var stream = client.GetStream();
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
                // 对端还在（理论上不会有数据）：继续等。
            }
        }
        catch (Exception error) when (
            error is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // 连接已被我方 Dispose（回填/取消/替换）或对端异常断开：都等于"这条挂起结束了"。
        }
    }
}
