using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPager.Core;

public enum AgentSource { CodexDesktop, CodexCLI, ClaudeCode, OpenCode, Antigravity }
public enum AgentLifecycle { Offline, Idle, Starting, Running, WaitingApproval, WaitingAnswer, Succeeded, Interrupted }
public enum AgentActivity { Thinking, Reading, Searching, Editing, Executing, Testing, Browsing, Delegating }
public enum TaskCapability { Approve, Deny, Answer, Interrupt, Retry, Prompt }
public enum PendingRequestKind { Approval, Question }
public enum ControlAction { Approve, Deny, Answer, Interrupt, Retry, Mute, MarkRead, Pin, Prompt, Shutdown }
public enum ControlResult { Accepted, Rejected, Stale, Unsupported }

public sealed record TokenUsage(
    int Input = 0,
    int CachedInput = 0,
    int Output = 0,
    int ReasoningOutput = 0,
    int Total = 0);

public sealed record SubagentSnapshot(
    string Id,
    string Path,
    string DisplayName,
    AgentLifecycle Lifecycle,
    AgentActivity? Activity,
    string? LatestStep,
    TokenUsage? TokenUsage,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt)
{
    [JsonIgnore]
    public bool IsTerminal => Lifecycle is AgentLifecycle.Succeeded or AgentLifecycle.Interrupted;

    public static string NameFromPath(string path)
    {
        var raw = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrEmpty(raw)
            ? "Codex 子代理"
            : char.ToUpperInvariant(raw[0]) + raw[1..];
    }
}

public sealed record TaskSnapshot(
    string Id,
    AgentSource Source,
    string ProjectName,
    string Title,
    string? UserPrompt,
    string? LatestStep,
    TokenUsage? TokenUsage,
    List<SubagentSnapshot> Subagents,
    AgentLifecycle Lifecycle,
    AgentActivity? Activity,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool IsUnread,
    bool IsPinned,
    bool IsMuted,
    HashSet<TaskCapability> Capabilities,
    string? ModelName = null,
    bool PromptQueued = false,
    string? ResponseBody = null)
{
    [JsonIgnore]
    public bool IsTerminal => Lifecycle is AgentLifecycle.Succeeded or AgentLifecycle.Interrupted or AgentLifecycle.Offline;

    [JsonIgnore]
    public int EffectivePriority =>
        (Lifecycle switch
        {
            AgentLifecycle.WaitingApproval => 600,
            AgentLifecycle.WaitingAnswer => 500,
            AgentLifecycle.Succeeded => 300,
            AgentLifecycle.Starting or AgentLifecycle.Running => 200,
            _ => 100,
        }) + (IsUnread && Lifecycle == AgentLifecycle.Succeeded ? 50 : 0) + (IsPinned ? 25 : 0);
}

public sealed record UsageWindow(
    string Key,
    string Label,
    double UsedPercentage,
    double RemainingPercentage,
    int WindowMinutes,
    DateTimeOffset? ResetsAt,
    /// <summary>
    /// 只显示一行文字、不画进度条的窗口（例如 Command Code 的月额度是美元余额，没有百分比可言）。
    /// App 见到它有值就走文字行。为空则照旧按百分比画条。
    /// </summary>
    string? Detail = null);

public sealed record DailyUsagePoint(
    string Date,
    long InputTokens = 0,
    long CachedInputTokens = 0,
    long OutputTokens = 0,
    long ReasoningOutputTokens = 0,
    long TotalTokens = 0,
    double? EstimatedCostUSD = null);

public sealed record UsageSnapshot(
    DateTimeOffset? CapturedAt,
    string? PlanType,
    string? LimitID,
    List<UsageWindow> Windows,
    List<DailyUsagePoint> DailyUsage);

public sealed record PendingRequest(
    string TaskID,
    PendingRequestKind Kind,
    string? Summary,
    List<string> Options,
    /// <summary>
    /// 这道题可否多选（Claude 的 `multiSelect` / opencode 的 `multiple`）。
    /// true 时 App 把选项画成可反复点选的开关，回传走 `ControlPayload.Values`（多值）；
    /// false 保持老交互（点一下即作答，回传单值 `Value`）。
    /// </summary>
    bool MultiSelect = false);

public sealed record StateSnapshotPayload(
    List<TaskSnapshot> Tasks,
    UsageSnapshot? Usage,
    string? FocusedTaskID,
    List<PendingRequest> PendingRequests,
    List<UsageSnapshot>? Usages = null);

public sealed record PairingPayload(
    int Version,
    string ServiceID,
    string Host,
    ushort Port,
    string Secret,
    /// <summary>
    /// 外网隧道域名（ngrok 之类），手机在局域网地址连不上时改用它走 wss。
    /// 只填域名，不带协议、不带端口 —— 隧道一律按 443/wss。留空即不启用外网。
    /// </summary>
    string? TunnelHost = null)
{
    /// <summary>
    /// 隧道域名清洗：整段粘 https://xxx.ngrok-free.dev/ 进来是常态，统一洗成裸域名，
    /// 免得手机那边拼出 wss://https://... 这种废 URL。空白或洗不出东西返回 null。
    /// </summary>
    public static string? NormalizeTunnelHost(string? host)
    {
        var value = host?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) value = value[(scheme + 3)..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[..colon];
        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}

public sealed record ControlPayload(
    string TaskID,
    ControlAction Action,
    string? Value,
    /// <summary>
    /// 多选题的作答：选中的选项文本（可多个，按点选顺序）。
    /// 单选与自由文本仍走 <see cref="Value"/>；两处可以并存（打字内容算附加的一项）。
    /// </summary>
    List<string>? Values = null);

public sealed record SignedControlEnvelope(
    int Version,
    Guid MessageId,
    string Type,
    long SentAt,
    string DeviceId,
    ulong Sequence,
    string Nonce,
    ControlPayload Payload,
    string Signature);

public sealed record ControlAckPayload(Guid RequestID, ControlResult Result, string? Reason);

public sealed record MessageEnvelope<T>(
    int Version,
    Guid MessageId,
    string Type,
    long SentAt,
    T Payload)
{
    public static MessageEnvelope<T> Create(string type, T payload) =>
        new(1, Guid.NewGuid(), type, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload);
}

/// <summary>
/// 配对密钥的比对规则。桥在 WebSocket 握手上验它，手机把配对文本里的 Base64 串原样带过来。
/// </summary>
public static class PairingSecret
{
    /// <summary>
    /// 定长比对，避免用比较耗时反推密钥。长度不同直接判否（FixedTimeEquals 不会抛）。
    /// </summary>
    public static bool Matches(byte[] secret, string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(Convert.ToBase64String(secret)));
    }
}

public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new CodexHookEventNameConverter());
        options.Converters.Add(new WireEnumConverterFactory());
        options.Converters.Add(new UnixMillisecondsConverter());
        options.Converters.Add(new NullableUnixMillisecondsConverter());
        return options;
    }
}

internal sealed class CodexHookEventNameConverter : JsonConverter<CodexHookEventName>
{
    public override CodexHookEventName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<CodexHookEventName>(reader.GetString(), ignoreCase: false, out var result)
            ? result
            : throw new JsonException("未知的 Codex Hook 事件。");

    public override void Write(Utf8JsonWriter writer, CodexHookEventName value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class UnixMillisecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
}

internal sealed class NullableUnixMillisecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value.ToUnixTimeMilliseconds());
    }
}

internal sealed class WireEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(WireEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class WireEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException();
        foreach (var candidate in Enum.GetValues<T>())
            if (WireName(candidate).Equals(value, StringComparison.Ordinal))
                return candidate;
        throw new JsonException($"未知的 {typeof(T).Name} 值：{value}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(WireName(value));

    private static string WireName(T value)
    {
        var name = value.ToString();
        if (typeof(T) == typeof(AgentSource))
        {
            if (name == nameof(AgentSource.CodexCLI)) return "codexCLI";
            if (name == nameof(AgentSource.ClaudeCode)) return "claudeCode";
            if (name == nameof(AgentSource.OpenCode)) return "opencode";
        }
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
