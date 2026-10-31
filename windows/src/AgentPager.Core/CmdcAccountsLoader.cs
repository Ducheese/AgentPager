using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// 多 Command Code 账号额度：读取桌面 ccgo.txt 中各账号（邮箱 + JSON apiKey），
/// 逐个调用 api.commandcode.ai 端点拉取额度，每个账号生成一个 UsageSnapshot。
/// </summary>
public sealed class CmdcAccountsLoader
{
    public Action<string, string>? Log { get; set; }

    /// <summary>可注入，供测试替换（默认读取桌面 ccgo.txt）。</summary>
    public static Func<string?>? AccountsFileProviderOverride { get; set; }

    /// <summary>可注入，供测试替换（默认直调 API）。</summary>
    public static Func<string, string, string?>? HttpGetOverride { get; set; }

    private (DateTimeOffset CapturedAt, List<UsageSnapshot> Snapshots)? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public List<UsageSnapshot> Load(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var current = now ?? DateTimeOffset.Now;
        if (_cache is { } cached && current - cached.CapturedAt < CacheTtl)
            return cached.Snapshots;

        var result = new System.Collections.Concurrent.ConcurrentBag<UsageSnapshot>();
        var accounts = ReadAccounts();
        if (accounts.Count == 0)
            Log?.Invoke("WARN", "[CMDC-多号] ccgo.txt 为空或读不到，不拉多号");

        try
        {
            Parallel.ForEach(accounts, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, accounts.Count),
            }, account =>
            {
                var snapshot = LoadOne(account.Email, account.ApiKey, current);
                if (snapshot is not null) result.Add(snapshot);
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Log?.Invoke("WARN", $"[CMDC-多号] 并行拉取失败：{error.GetType().Name}: {error.Message}");
        }

        var ordered = accounts
            .Select(a => result.FirstOrDefault(s => s.LimitID == a.Email))
            .Where(s => s is not null)
            .Cast<UsageSnapshot>()
            .ToList();

        // 全成功才更新缓存：部分失败不拿残表覆盖上次好的缓存；全失败沿用旧缓存，从没成功过才返回空。
        if (ordered.Count == accounts.Count && ordered.Count > 0) _cache = (current, ordered);
        if (_cache is { } good && ordered.Count < accounts.Count) return good.Snapshots;
        return ordered.Count > 0 ? ordered : _cache?.Snapshots ?? ordered;
    }

    private UsageSnapshot? LoadOne(string email, string apiKey, DateTimeOffset now)
    {
        var creditsJson = Get(apiKey, "/alpha/billing/credits");
        if (creditsJson is null) return null;
        var subscriptionJson = Get(apiKey, "/alpha/billing/subscriptions");
        var summaryJson = Get(apiKey, "/alpha/usage/summary");

        var windows = CommandCodeUsageLoader.ParseWindows(creditsJson, subscriptionJson, now);
        // credits 成功但字段全缺：返回空窗口快照，让多号面板显示未知，而不是丢掉这个号。
        var daily = CommandCodeUsageLoader.ParseDaily(summaryJson, now);

        // LimitID 直接使用账号邮箱，供 App 侧多账号面板展示
        return new(now, CommandCodeUsageLoader.PlanType, email, windows, daily);
    }

    private string? Get(string apiKey, string path)
    {
        var url = CommandCodeUsageLoader.ApiBaseUrl + path;
        try
        {
            if (HttpGetOverride is { } stub) return stub(apiKey, url);
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                Log?.Invoke("WARN", $"[CMDC-多号] {path} 返回 {(int)response.StatusCode}");
                return null;
            }
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Log?.Invoke("WARN", $"[CMDC-多号] {path} 请求失败：{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>解析 ccgo.txt：邮箱行 + 多行 JSON 块（含 apiKey），空行分隔。</summary>
    public static List<(string Email, string ApiKey)> ReadAccounts()
    {
        var text = AccountsFileProviderOverride is { } stub ? stub() : ReadDesktopFile();
        var accounts = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return accounts;

        try
        {
            string? pendingEmail = null;
            string? jsonBlock = null;
            var depth = 0;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;

                if (jsonBlock is null)
                {
                    if (line.Contains('@') && !line.StartsWith('{'))
                    {
                        pendingEmail = line;
                    }
                    else if (line.StartsWith('{'))
                    {
                        jsonBlock = line;
                        depth = line.Count(c => c == '{') - line.Count(c => c == '}');
                        if (depth <= 0)
                        {
                            if (pendingEmail is not null && ExtractApiKey(jsonBlock) is { } key)
                                accounts.Add((pendingEmail, key));
                            pendingEmail = null;
                            jsonBlock = null;
                        }
                    }
                }
                else
                {
                    jsonBlock += "\n" + line;
                    depth += line.Count(c => c == '{') - line.Count(c => c == '}');
                    if (depth > 0) continue;

                    if (pendingEmail is not null && ExtractApiKey(jsonBlock) is { } key)
                        accounts.Add((pendingEmail, key));
                    pendingEmail = null;
                    jsonBlock = null;
                }
            }
        }
        catch
        {
            // ignore
        }
        return accounts;
    }

    private static string? ExtractApiKey(string json)
    {
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

    private static string? ReadDesktopFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ccgo.txt");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
