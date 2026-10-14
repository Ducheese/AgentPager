using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// Command Code（<c>cmdc</c>）的额度读取。
///
/// **不走 CLI**：`cmdc -p "/usage"` 要过额度门——账号月额度见底时直接报
/// "Insufficient credits"，等于"没额度就查不了额度"（实测）。但它内部就是打这几个端点，
/// 直接调反而更稳：
///   /alpha/billing/credits        → 5 小时与周窗口（used / cap / resetAt）+ 月额度余额
///   /alpha/billing/subscriptions  → 套餐与计费周期（"每月"的重置日）
///   /alpha/usage/summary          → 本周期逐日用量（喂 App 的每日柱状图）
///
/// 凭证取自 <c>~/.commandcode/auth.json</c> 的 <c>apiKey</c>，用 <c>Authorization: Bearer</c>。
/// 与 agy 不同：<c>api.commandcode.ai</c> **直连可达**（实测 HTTP 200），不需要代理注入。
/// </summary>
public sealed class CommandCodeUsageLoader
{
    /// <summary>可注入，供测试替换（默认读 %USERPROFILE%\.commandcode\auth.json）。</summary>
    public static Func<string?>? AuthJsonProviderOverride { get; set; }

    /// <summary>可注入，供测试替换。</summary>
    public static Func<string, string?>? HttpGetOverride { get; set; }

    public static string ApiBaseUrl { get; set; } = "https://api.commandcode.ai";

    /// <summary>诊断日志出口（桥注入 DailyFileLog.Write）。失败静默过一次，别再犯。</summary>
    public Action<string, string>? Log { get; set; }

    public const string PlanType = "Command Code";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    private const int FiveHourMinutes = 300;
    private const int WeeklyMinutes = 10080;
    private const int MonthlyMinutes = 43200;

    private (DateTimeOffset CapturedAt, string LimitId, List<UsageWindow> Windows, List<DailyUsagePoint> Daily)? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public UsageSnapshot? Load(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        if (_cache is { } cached && current - cached.CapturedAt < CacheTtl)
            return Snapshot(cached.CapturedAt, cached.LimitId, cached.Windows, cached.Daily);

        var creditsJson = Get("/alpha/billing/credits");
        if (creditsJson is null) return _cache is { } stale
            ? Snapshot(stale.CapturedAt, stale.LimitId, stale.Windows, stale.Daily)
            : null;

        // 套餐与逐日用量是锦上添花：拿不到也不该让整个窗口消失。
        var subscriptionJson = Get("/alpha/billing/subscriptions");
        var summaryJson = Get("/alpha/usage/summary");

        var windows = ParseWindows(creditsJson, subscriptionJson, current);
        if (windows.Count == 0) return null;
        var daily = ParseDaily(summaryJson, current);
        var limitId = PlanLabel(subscriptionJson);

        _cache = (current, limitId, windows, daily);
        return Snapshot(current, limitId, windows, daily);
    }

    private static UsageSnapshot Snapshot(
        DateTimeOffset capturedAt, string limitId, List<UsageWindow> windows, List<DailyUsagePoint> daily) =>
        new(capturedAt, PlanType, limitId, windows, daily);

    /// <summary>套餐显示名："individual-go-v1" → "Command Code Go"；认不出档位就退回 "Command Code"。</summary>
    public static string PlanLabel(string? subscriptionJson)
    {
        var planId = PlanId(subscriptionJson);
        if (string.IsNullOrWhiteSpace(planId)) return PlanType;

        var tier = planId.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => !part.All(char.IsDigit) && part is not ("individual" or "team" or "v"));
        return tier is null ? PlanType : $"{PlanType} {char.ToUpperInvariant(tier[0])}{tier[1..]}";
    }

    public static string? PlanId(string? subscriptionJson)
    {
        if (string.IsNullOrWhiteSpace(subscriptionJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(subscriptionJson);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            return data.TryGetProperty("planId", out var plan) && plan.ValueKind == JsonValueKind.String
                ? plan.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string? Get(string path)
    {
        var url = ApiBaseUrl + path;
        try
        {
            if (HttpGetOverride is { } stub) return stub(url);

            var auth = ReadAuthJson();
            if (auth is null)
            {
                Log?.Invoke("WARN", "[CMDC] 读不到 ~/.commandcode/auth.json（未登录？）");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = Client.Send(request);

            if (!response.IsSuccessStatusCode)
            {
                Log?.Invoke("WARN", $"[CMDC] {path} 返回 {(int)response.StatusCode}");
                return null;
            }
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Log?.Invoke("WARN", $"[CMDC] {path} 请求失败：{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private static string? ReadAuthJson()
    {
        if (AuthJsonProviderOverride is { } stub) return ExtractApiKey(stub());
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".commandcode", "auth.json");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ExtractApiKey(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractApiKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("apiKey", out var key) ? key.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 5 小时 / 周 / 月三个窗口。前两个按 used/cap 画百分比；月额度是美元余额，
    /// 没有上限可比，所以走 <see cref="UsageWindow.Detail"/> 只显示一行文字（App 侧不画条）。
    /// </summary>
    public static List<UsageWindow> ParseWindows(string creditsJson, string? subscriptionJson, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        try
        {
            using var doc = JsonDocument.Parse(creditsJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("windowLimits", out var limits))
            {
                AddWindow(limits, "fiveHour", "cc-5h", "5 小时", FiveHourMinutes);
                AddWindow(limits, "weekly", "cc-weekly", "周额度", WeeklyMinutes);
            }

            if (root.TryGetProperty("credits", out var credits) &&
                credits.TryGetProperty("monthlyCredits", out var monthly) &&
                monthly.ValueKind == JsonValueKind.Number)
            {
                var remaining = monthly.GetDouble();
                var resetsAt = SubscriptionPeriodEnd(subscriptionJson);
                var resetText = resetsAt?.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture);
                var detail = resetText is null
                    ? $"剩余 ${remaining:0.00}"
                    : $"剩余 ${remaining:0.00} · {resetText} 重置";
                windows.Add(new("cc-monthly", "月额度", 0, 0, MonthlyMinutes, resetsAt, detail));
            }
        }
        catch
        {
            // 解析失败当作没有窗口处理，由调用方决定是否沿用缓存。
        }
        return windows;

        void AddWindow(JsonElement limits, string name, string key, string label, int minutes)
        {
            if (!limits.TryGetProperty(name, out var window)) return;
            var used = window.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
            var cap = window.TryGetProperty("cap", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            if (cap <= 0) return;

            var usedPercent = Math.Clamp(used / cap * 100.0, 0, 100);
            windows.Add(new(key, label, usedPercent, 100 - usedPercent, minutes, ResetAt(window)));
        }
    }

    private static DateTimeOffset? ResetAt(JsonElement window) =>
        window.TryGetProperty("resetAt", out var r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64())
            : null;

    /// <summary>订阅的当前计费周期结束 = "每月"的重置日。</summary>
    public static DateTimeOffset? SubscriptionPeriodEnd(string? subscriptionJson)
    {
        if (string.IsNullOrWhiteSpace(subscriptionJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(subscriptionJson);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("currentPeriodEnd", out var end) || end.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本周期逐日用量（只给到 token 总数，没有输入/输出拆分）。</summary>
    public static List<DailyUsagePoint> ParseDaily(string? summaryJson, DateTimeOffset now)
    {
        var points = new List<DailyUsagePoint>();
        if (string.IsNullOrWhiteSpace(summaryJson)) return points;
        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            if (!doc.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Array)
                return points;

            foreach (var entry in daily.EnumerateArray())
            {
                var date = entry.TryGetProperty("date", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(date)) continue;
                var tokens = entry.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                points.Add(new(date, TotalTokens: tokens));
            }
        }
        catch
        {
            // ignore
        }
        return points;
    }
}
