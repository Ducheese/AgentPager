using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// OpenCode Go 订阅额度（rolling 5h / weekly / monthly）。
///
/// 官方公开端点（2026-08 起生产可用）：<c>GET https://opencode.ai/zen/go/v1/usage</c>，
/// 用 <c>Authorization: Bearer &lt;opencode-go key&gt;</c>。响应：
/// <code>
/// {"usage":{
///   "rolling":{"status":"ok","percent":1,"resetsAt":"2026-08-15T12:34:47.397Z"},
///   "weekly":{"status":"ok","percent":18,"resetsAt":"..."},
///   "monthly":{"status":"ok","percent":11,"resetsAt":"..."}}}
/// </code>
/// percent 是 0..100 的百分数（不是 0..1）。`status:"rate-limited"` 时百分数可能只有 99，
/// 按已用尽处理。没有 Go 订阅时端点回 403 EntitlementError；404 是端点漂移——两者都只记日志，
/// 沿用上一次成功缓存。
///
/// 凭证读 OpenCode 自己写的那份：<c>~/.local/share/opencode/auth.json</c> 的
/// <c>"opencode-go": { "type": "api", "key": "..." }</c>；环境变量 OPENCODE_API_KEY 兜底。
/// </summary>
public sealed class OpenCodeGoUsageLoader
{
    /// <summary>可注入，供测试替换（默认读 auth.json）。</summary>
    public static Func<string?>? AuthJsonProviderOverride { get; set; }

    /// <summary>可注入，供测试替换。</summary>
    public static Func<string, string?>? HttpGetOverride { get; set; }

    public static string ApiBaseUrl { get; set; } = "https://opencode.ai";

    public const string PlanType = "OpenCode Go";

    private const int RollingMinutes = 300;
    private const int WeeklyMinutes = 10080;
    private const int MonthlyMinutes = 43200;

    public Action<string, string>? Log { get; set; }

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private (DateTimeOffset CapturedAt, UsageSnapshot Snapshot)? _cache;

    public UsageSnapshot? Load(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        if (_cache is { } cached && current - cached.CapturedAt < CacheTtl)
            return cached.Snapshot;

        var json = Get("/zen/go/v1/usage");
        if (json is null) return _cache?.Snapshot;

        var windows = ParseWindows(json);
        if (windows.Count == 0)
        {
            // HTTP 成功但字段全缺：这是未知，不是请求失败；返回空窗口快照让 UI 画占位。
            Log?.Invoke("WARN", "[OpenCodeGo] usage 响应里没有可用的窗口");
        }

        // usage 接口不返回套餐档位，LimitID 只能硬编码；App 会显示成 "OPENCODE GO 订阅"。
        var snapshot = new UsageSnapshot(current, PlanType, "OpenCode Go", windows, []);
        _cache = (current, snapshot);
        return snapshot;
    }

    private string? Get(string path)
    {
        var url = ApiBaseUrl + path;
        try
        {
            if (HttpGetOverride is { } stub) return stub(url);

            var key = ReadApiKey();
            if (string.IsNullOrEmpty(key))
            {
                Log?.Invoke("WARN", "[OpenCodeGo] 读不到 auth.json 的 opencode-go key（未登录？）");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = Client.Send(request);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                Log?.Invoke("WARN", status switch
                {
                    403 => "[OpenCodeGo] 403 EntitlementError：这个 key 没有 Go 订阅",
                    404 => "[OpenCodeGo] 404：usage 端点已变更（opencode 版本漂移）",
                    _ => $"[OpenCodeGo] {path} 返回 {status}",
                });
                return null;
            }
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Log?.Invoke("WARN", $"[OpenCodeGo] {path} 请求失败：{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private static string? ReadApiKey()
    {
        if (AuthJsonProviderOverride is { } stub) return ExtractKey(stub());
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode", "auth.json");
            if (!File.Exists(path))
                return Environment.GetEnvironmentVariable("OPENCODE_API_KEY");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ExtractKey(reader.ReadToEnd())
                ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("opencode-go", out var provider)) return null;
            return provider.ValueKind == JsonValueKind.Object &&
                   provider.TryGetProperty("key", out var key) &&
                   key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 usage 响应，返回 5 小时 / 周 / 月三个窗口；无法解析返回空列表。</summary>
    public static List<UsageWindow> ParseWindows(string json)
    {
        var windows = new List<UsageWindow>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
                return windows;

            AddWindow(usage, "rolling", "oc-rolling", "5 小时", RollingMinutes);
            AddWindow(usage, "weekly", "oc-weekly", "周额度", WeeklyMinutes);
            AddWindow(usage, "monthly", "oc-monthly", "月额度", MonthlyMinutes);
        }
        catch
        {
            // 解析失败当作没有窗口处理，由调用方沿用缓存。
        }
        return windows;

        void AddWindow(JsonElement root, string name, string key, string label, int minutes)
        {
            if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;

            var status = window.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString()
                : null;

            double? used = null;
            if (window.TryGetProperty("percent", out var percent) && percent.ValueKind == JsonValueKind.Number)
            {
                used = percent.GetDouble();
            }
            else if (window.TryGetProperty("used", out var usedValue) && usedValue.ValueKind == JsonValueKind.Number &&
                     window.TryGetProperty("limit", out var limitValue) && limitValue.ValueKind == JsonValueKind.Number &&
                     limitValue.GetDouble() > 0)
            {
                used = usedValue.GetDouble() / limitValue.GetDouble() * 100.0;
            }

            // rate-limited 的 percent 可能是 99（向下取整），语义上是已用尽；没有 percent 也算用尽。
            if (string.Equals(status, "rate-limited", StringComparison.OrdinalIgnoreCase)) used = 100;
            if (used is not { } spent || !double.IsFinite(spent))
            {
                // 没有百分比也没有 used/limit：不知道用了多少，宁可少一个窗口，
                // 也不能按 0 已用显示成 100% 剩余。
                return;
            }

            spent = Math.Clamp(spent, 0, 100);
            windows.Add(new(key, label, spent, 100 - spent, minutes, ResetAt(window)));
        }
    }

    private static DateTimeOffset? ResetAt(JsonElement window)
    {
        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        // 少数镜像/旧版用 resetInSec（秒后重置）。
        if (window.TryGetProperty("resetInSec", out var seconds) && seconds.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.UtcNow.AddSeconds(seconds.GetDouble());

        return null;
    }
}
