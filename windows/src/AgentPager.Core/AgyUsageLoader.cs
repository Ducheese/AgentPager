using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AgentPager.Core;

public static class AgyPaths
{
    public static string? BinaryPathOverride { get; set; }

    public static string ResolveBinary()
    {
        if (!string.IsNullOrWhiteSpace(BinaryPathOverride) && File.Exists(BinaryPathOverride))
            return BinaryPathOverride;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        if (File.Exists(defaultPath)) return defaultPath;

        return "agy";
    }
}

public sealed class AgyUsageLoader
{
    private const int HistoryDays = 90;
    private static readonly TimeSpan QuotaCacheTtl = TimeSpan.FromMinutes(5);

    private sealed record Raw(long Input, long Cached, long Output, long Total)
    {
        public static Raw Zero { get; } = new(0, 0, 0, 0);
        public bool IsEmpty => Input == 0 && Cached == 0 && Output == 0 && Total == 0;
        public static Raw operator +(Raw left, Raw right) =>
            new(left.Input + right.Input, left.Cached + right.Cached, left.Output + right.Output, left.Total + right.Total);
    }

    private sealed record CachedFileUsage(DateTimeOffset LastWriteTimeUtc, Dictionary<string, Raw> Daily);

    public static Func<string?>? QuotaJsonProviderOverride { get; set; }

    /// <summary>
    /// 诊断日志出口（桥在构造时注入 <c>DailyFileLog.Write</c>）。拉取失败以前是**完全静默**的：
    /// 调用点只看得到 null，分不清"超时 / 非零退出 / 没有可解析的 JSON"，排查只能猜——实测踩过
    /// （桥 spawn 61 次全空超时，没有一行日志能定位）。这里把每次拉取的耗时、退出码、输出长度落盘。
    /// </summary>
    public Action<string, string>? Log { get; set; }

    private (DateTimeOffset CapturedAt, List<UsageWindow> Windows, string? PlanType, string? LimitID)? _quotaCache;
    private readonly Dictionary<string, CachedFileUsage> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    public UsageSnapshot? Load(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var quota = GetOrFetchQuota(current);

        var brainRoot = AgyTranscriptReader.BrainRoot;
        if (!Directory.Exists(brainRoot) && quota is null) return null;

        var start = current.Date.AddDays(-(HistoryDays - 1));
        var buckets = AggregateDailyTokens(brainRoot, start);

        if (quota is null && buckets.Count == 0) return null;

        var daily = Enumerable.Range(0, HistoryDays).Select(offset =>
        {
            var date = start.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var usage = buckets.GetValueOrDefault(date, Raw.Zero);
            return new DailyUsagePoint(date, usage.Input, usage.Cached, usage.Output, 0, usage.Total);
        }).ToList();

        return new(quota?.CapturedAt ?? current, quota?.PlanType ?? "Antigravity", quota?.LimitID ?? "Google AI Pro",
            quota?.Windows ?? [], daily);
    }

    public (DateTimeOffset CapturedAt, List<UsageWindow> Windows, string? PlanType, string? LimitID)? GetOrFetchQuota(DateTimeOffset now)
    {
        if (_quotaCache is { } cached && (now - cached.CapturedAt) < QuotaCacheTtl)
            return cached;

        try
        {
            var json = QuotaJsonProviderOverride != null
                ? QuotaJsonProviderOverride()
                : ExecuteAgyUsage();

            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = ParseQuotaJson(json, now);
                if (parsed is not null)
                {
                    _quotaCache = parsed;
                    return parsed;
                }
            }
        }
        catch
        {
            // Fail-open: return cached quota if available
        }

        return _quotaCache;
    }

    public static (DateTimeOffset CapturedAt, List<UsageWindow> Windows, string? PlanType, string? LimitID)? ParseQuotaJson(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmd) ||
                !cmd.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
                return null;

            var windows = new List<UsageWindow>();
            UsageWindow? primaryGemini5h = null;
            UsageWindow? secondaryGeminiWeekly = null;
            UsageWindow? threeP5h = null;
            UsageWindow? threePWeekly = null;

            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("name", out var gNameProp) ? gNameProp.GetString() : "";
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;

                var isGemini = groupName?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    var id = bucket.TryGetProperty("id", out var idProp) ? idProp.GetString() : "";
                    var windowType = bucket.TryGetProperty("window", out var wProp) ? wProp.GetString() : "";
                    var fraction = bucket.TryGetProperty("remaining_fraction", out var fProp) ? fProp.GetDouble() : 1.0;
                    DateTimeOffset? resetTime = bucket.TryGetProperty("reset_time", out var rProp) &&
                                                DateTimeOffset.TryParse(rProp.GetString(), out var parsedReset)
                        ? parsedReset
                        : null;

                    var remaining = Math.Clamp(fraction * 100.0, 0.0, 100.0);
                    var used = Math.Max(0.0, 100.0 - remaining);

                    if (isGemini)
                    {
                        if (windowType == "5h" || id?.Contains("5h", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            primaryGemini5h = new("primary", "Gemini 5H", used, remaining, 300, resetTime);
                        }
                        else if (windowType == "weekly" || id?.Contains("weekly", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            secondaryGeminiWeekly = new("secondary", "Gemini 周额度", used, remaining, 10080, resetTime);
                        }
                    }
                    else
                    {
                        if (windowType == "5h" || id?.Contains("5h", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            threeP5h = new("3p-5h", "Claude/GPT 5H", used, remaining, 300, resetTime);
                        }
                        else if (windowType == "weekly" || id?.Contains("weekly", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            threePWeekly = new("3p-weekly", "Claude/GPT 周额度", used, remaining, 10080, resetTime);
                        }
                    }
                }
            }

            if (primaryGemini5h is not null) windows.Add(primaryGemini5h);
            if (secondaryGeminiWeekly is not null) windows.Add(secondaryGeminiWeekly);
            if (threeP5h is not null) windows.Add(threeP5h);
            if (threePWeekly is not null) windows.Add(threePWeekly);

            if (windows.Count == 0) return null;

            return (now, windows, "Antigravity", "Google AI Pro");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>我们自己等 agy 的上限；比 <c>--print-timeout</c> 宽 15 秒，留给它收尾和吐输出。</summary>
    private const int CommandTimeoutMs = 75_000;

    private string? ExecuteAgyUsage()
    {
        var binary = AgyPaths.ResolveBinary();
        // 先记一行"进来了"：否则一旦它在 spawn 之前就抛（被上层 fail-open 吞掉），日志会一片空白，
        // 只能靠"另一份额度照常有数据"反推——v94 就是这么排查的。
        Log?.Invoke("INFO", $"[AGY] 开始拉取配额：{binary}");
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            // --print-timeout：让 agy 自己到点收尾，而不是被我们硬杀——硬杀会在它那边留下楔住的实例，
            // 实测后果是"第一次失败之后每次都失败"。单位是 Go 风格时长，写 "60" 会直接被当成非法参数。
            Arguments = "-p \"/usage\" --output-format json --print-timeout 60s",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 必须显式接管 stdin：桥是 GUI 进程（没有控制台），子进程继承到的是**无效的 stdin 句柄**，
            // agy 的 print mode 在这种上下文里会一直阻塞——实测桥 spawn 的每一次都超时且 stdout 零字节
            // （partialOutLen=0，61 次 0 成功），而同一命令在控制台里 8~11 秒就返回。
            // 给一个"存在但立即 EOF"的 stdin 即可。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        // agy 拉额度要访问 googleapis，而**本机直连那条路是不通的**（实测 12 秒超时），必须走代理。
        // 桥是被双击启动的 GUI 进程，继承不到终端会话里的 HTTP_PROXY/HTTPS_PROXY（用户级与机器级
        // 环境变量里都没有），所以这里把 Windows 系统代理显式传给子进程——否则 agy 会一直卡在那个
        // HTTP 请求上：不输出、不报错，只能等我们超时杀它。这正是"手机端额度永远是空的"的根因。
        //
        // 整段必须自己 try/catch：它在 Process.Start **之前**，抛出去会被上层 fail-open 静默吞掉
        // （实测踩过——v94 里 agy 一次都没被启动过，日志一片空白，只能靠"Codex 那份照常有数据"倒推）。
        try
        {
            // 注意：ProcessStartInfo.Environment 的索引器在键不存在时**抛 KeyNotFoundException**，
            // 不是返回 null —— v94 正是死在这行（异常被上层 fail-open 吞掉，agy 一次都没被启动、
            // 日志一片空白）。必须用 TryGetValue。
            var inherited =
                (psi.Environment.TryGetValue("HTTP_PROXY", out var httpProxy) && !string.IsNullOrEmpty(httpProxy)) ||
                (psi.Environment.TryGetValue("HTTPS_PROXY", out var httpsProxy) && !string.IsNullOrEmpty(httpsProxy));
            if (inherited)
            {
                Log?.Invoke("INFO", "[AGY] 父进程已有代理环境变量，直接沿用");
            }
            else if (SystemProxyAddress() is { } proxy)
            {
                psi.Environment["HTTP_PROXY"] = proxy;
                psi.Environment["HTTPS_PROXY"] = proxy;
                Log?.Invoke("INFO", $"[AGY] 子进程使用系统代理 {proxy}");
            }
            else
            {
                Log?.Invoke("WARN", "[AGY] 系统代理读不到（ProxyEnable=0 或 ProxyServer 为空），" +
                                    "agy 将直连 googleapis —— 本机大概率连不通，会一直卡到超时");
            }
        }
        catch (Exception proxyError)
        {
            Log?.Invoke("WARN", $"[AGY] 代理处理失败，按原样启动：{proxyError.GetType().Name}: {proxyError.Message}");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                Log?.Invoke("WARN", $"[AGY] 配额拉取失败：进程未能启动（{binary}）");
                return null;
            }

            // 立刻关掉写端：子进程读到 EOF，不会挂在等 stdin 上。
            try { process.StandardInput.Close(); } catch { }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log?.Invoke("WARN",
                    $"[AGY] 配额命令超时（{CommandTimeoutMs / 1000}s）被中止，耗时 {watch.ElapsedMilliseconds}ms");
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            watch.Stop();

            if (process.ExitCode != 0)
            {
                Log?.Invoke("WARN",
                    $"[AGY] 配额命令非零退出码 {process.ExitCode}，耗时 {watch.ElapsedMilliseconds}ms，" +
                    $"outLen={output.Length}，err={Brief(error)}");
                return null;
            }

            // 成功也记一行：这是唯一能区分"命令跑通但没解析出额度"与"命令没跑通"的证据。
            Log?.Invoke("INFO",
                $"[AGY] 配额拉取完成，耗时 {watch.ElapsedMilliseconds}ms，outLen={output.Length}，" +
                $"{(ParseQuotaJson(output, DateTimeOffset.Now) is { } parsed ? $"解析出 {parsed.Windows.Count} 个窗口" : "未能解析出额度窗口")}");
            return output;
        }
        catch (Exception error)
        {
            Log?.Invoke("WARN", $"[AGY] 配额拉取异常（{watch.ElapsedMilliseconds}ms）：{error.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 Windows 系统代理设置取代理地址（Clash 之类的工具会写这里）。
    /// 取不到就返回 null —— 这时行为退回"让 agy 自己决定"，与改动前一致，不会更差。
    /// </summary>
    private static string? SystemProxyAddress()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0) return null;

            // 可能是 "host:port"，也可能是 "http=h:p;https=h:p" 这种按协议分开写的形式。
            var raw = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var segments = part.Split('=', 2);
                var value = (segments.Length == 2 ? segments[1] : segments[0]).Trim();
                if (value.Length == 0) continue;
                return value.Contains("://", StringComparison.Ordinal) ? value : $"http://{value}";
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>日志里只留一行短摘要：agy 的 stderr 可能很长，整段落盘会把有用的行冲掉。</summary>
    private static string Brief(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private Dictionary<string, Raw> AggregateDailyTokens(string brainRoot, DateTimeOffset start)
    {
        var totals = new Dictionary<string, Raw>();
        if (!Directory.Exists(brainRoot)) return totals;

        try
        {
            var directories = Directory.EnumerateDirectories(brainRoot);
            foreach (var dir in directories)
            {
                var transcriptPath = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(transcriptPath)) continue;

                var fileInfo = new FileInfo(transcriptPath);
                var lastWrite = fileInfo.LastWriteTimeUtc;
                if (lastWrite < start) continue;

                if (_fileCache.TryGetValue(transcriptPath, out var cached) && cached.LastWriteTimeUtc == lastWrite)
                {
                    Merge(totals, cached.Daily, start);
                    continue;
                }

                var fileDaily = ReadFileTokens(transcriptPath, start);
                _fileCache[transcriptPath] = new(lastWrite, fileDaily);
                Merge(totals, fileDaily, start);
            }
        }
        catch
        {
            // Fail-open
        }

        return totals;
    }

    private static Dictionary<string, Raw> ReadFileTokens(string transcriptPath, DateTimeOffset start)
    {
        var result = new Dictionary<string, Raw>();
        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.GetStringOrNull("type") != "PLANNER_RESPONSE") continue;

                    var createdAtStr = root.GetStringOrNull("created_at");
                    if (string.IsNullOrWhiteSpace(createdAtStr) ||
                        !DateTimeOffset.TryParse(createdAtStr, out var createdAt) ||
                        createdAt < start)
                        continue;

                    var date = createdAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    long input = 0, cached = 0, output = 0;
                    if (root.TryGetProperty("input_tokens", out var inProp)) input = inProp.GetInt64();
                    if (root.TryGetProperty("cache_read_tokens", out var cProp)) cached = cProp.GetInt64();
                    if (root.TryGetProperty("output_tokens", out var outProp)) output = outProp.GetInt64();

                    var total = input + cached + output;
                    var entry = new Raw(input, cached, output, total);

                    result[date] = result.TryGetValue(date, out var existing) ? existing + entry : entry;
                }
                catch (JsonException) { }
            }
        }
        catch
        {
            // Ignore I/O race
        }

        return result;
    }

    private static void Merge(Dictionary<string, Raw> target, Dictionary<string, Raw> source, DateTimeOffset start)
    {
        foreach (var (date, raw) in source)
        {
            target[date] = target.TryGetValue(date, out var existing) ? existing + raw : raw;
        }
    }
}
