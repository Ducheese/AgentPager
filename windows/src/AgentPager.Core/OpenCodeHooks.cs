using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentPager.Core;

/// <summary>
/// opencode 插件通过 TCP Hook 通道转发的统一载荷。event 为 opencode 总线事件名
/// （或插件合成的 chat.message / plugin.dispose），properties 为裁剪后的原始属性。
/// </summary>
public sealed record OpenCodeHookPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("properties")] JsonElement? Properties,
    /// <summary>
    /// "挂住等手机"的帧（2026-10-11 起）：插件在收到权限询问/提问时，除了照常转发展示，
    /// 还会**再挂一条连接**给桥 —— 桥攥着它直到手机作答，回包走这条连接。
    /// 非空即为挂起帧（"approval" / "question"）。
    /// </summary>
    [property: JsonPropertyName("hold")] string? Hold = null,
    /// <summary>提问的 requestID（opencode 的作答端点按它定位；审批用 properties.id）。</summary>
    [property: JsonPropertyName("request_id")] string? RequestID = null);

/// <summary>
/// 写回 opencode 插件的应答（走挂住的那条连接，一行 JSON）。
/// 形状由插件自己消费，与 Claude 那套 hook stdout 协议无关。
/// </summary>
public static class OpenCodeHookOutput
{
    public static byte[] Decision(bool allow) =>
        Encoding.UTF8.GetBytes($"{{\"decision\":\"{(allow ? "allow" : "deny")}\"}}\n");

    /// <summary>
    /// 作答：opencode 的答复是"每题一个已选 label 数组"（源码 routes/question.ts 原文：
    /// "User answers in order of questions (each answer is an array of selected labels)"）。
    /// 现在只走单题单选的形态 → 一个数组包一个 label；多选题以后再扩。
    /// </summary>
    public static byte[] Answers(IReadOnlyList<string> labels)
    {
        var array = new JsonArray(labels.Select(label => (JsonNode)JsonValue.Create(label)!).ToArray());
        var root = new JsonObject { ["answers"] = new JsonArray(array) };
        return Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
    }
}

public enum OpenCodePluginStatus { NotInstalled, Installed, Outdated }

/// <summary>
/// 安装/卸载 &lt;opencode 配置目录&gt;/plugins/agentpager-opencode.js。
/// 插件内容来自嵌入资源，安装幂等（内容一致不重写），覆盖用户文件前自动备份，
/// 卸载只删除带托管标记的文件，绝不动用户自己的插件。
/// 三态识别：未安装 / 已写入且与内置一致 / 已写入但与内置不一致（待更新）。
/// </summary>
public sealed class OpenCodePluginConfiguration(string? configDirectory = null)
{
    public const string ManagedMarker = "Managed by AgentPager (OpenCode)";
    public const string PluginFileName = "agentpager-opencode.js";
    private const string ResourceName = "AgentPager.Core.Resources.agentpager-opencode.js";

    // 插件首行版本头：// agentpager-opencode v1
    private static readonly Regex VersionPattern = new(
        @"^//\s*agentpager-opencode\s+v(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Lazy<string> EmbeddedContent = new(LoadEmbedded);

    public string ConfigDirectory { get; } = configDirectory ?? OpenCodePaths.ConfigDirectory;

    public string PluginsDirectory => Path.Combine(ConfigDirectory, "plugins");

    public string PluginPath => Path.Combine(PluginsDirectory, PluginFileName);

    /// <summary>内置（嵌入资源）插件的版本号。</summary>
    public static int? EmbeddedVersion => ParseVersion(EmbeddedContent.Value);

    /// <summary>已安装插件的版本号；文件不存在或没有版本头时为 null。</summary>
    public int? InstalledVersion
    {
        get
        {
            try { return File.Exists(PluginPath) ? ParseVersion(File.ReadAllText(PluginPath)) : null; }
            catch { return null; }
        }
    }

    /// <summary>未安装 / 已写入且与内置一致 / 已写入但与内置不一致。</summary>
    public OpenCodePluginStatus Status
    {
        get
        {
            if (!File.Exists(PluginPath)) return OpenCodePluginStatus.NotInstalled;
            try
            {
                return Normalize(File.ReadAllText(PluginPath)) == Normalize(EmbeddedContent.Value)
                    ? OpenCodePluginStatus.Installed
                    : OpenCodePluginStatus.Outdated;
            }
            catch { return OpenCodePluginStatus.Outdated; }
        }
    }

    public bool IsInstalled() => Status != OpenCodePluginStatus.NotInstalled;

    public HookConfigurationChange Install()
    {
        var content = EmbeddedContent.Value;
        var existing = File.Exists(PluginPath) ? File.ReadAllText(PluginPath) : null;
        if (existing is not null && Normalize(existing) == Normalize(content)) return new(false);

        Directory.CreateDirectory(PluginsDirectory);
        var backup = existing is not null && !IsManaged(existing) ? Backup(existing) : null;
        File.WriteAllText(PluginPath, content);
        return new(true, backup);
    }

    public HookConfigurationChange Uninstall()
    {
        if (!File.Exists(PluginPath)) return new(false);
        var existing = File.ReadAllText(PluginPath);
        if (!IsManaged(existing)) return new(false);
        File.Delete(PluginPath);
        return new(true);
    }

    private string Backup(string contents)
    {
        var stem = $"{PluginPath}.agentpager-backup-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var path = stem;
        for (var index = 1; File.Exists(path); index++) path = $"{stem}-{index}";
        File.WriteAllText(path, contents);
        return path;
    }

    private static string LoadEmbedded()
    {
        using var stream = typeof(OpenCodePluginConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("缺少 OpenCode 插件嵌入资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static bool IsManaged(string contents) =>
        contents.Contains(ManagedMarker, StringComparison.Ordinal);

    private static int? ParseVersion(string contents)
    {
        var match = VersionPattern.Match(contents);
        return match.Success && int.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
}

public static class OpenCodePaths
{
    /// <summary>
    /// 与 opencode 自身的路径规则对齐：OPENCODE_CONFIG_DIR &gt; XDG_CONFIG_HOME/opencode
    /// &gt; ~/.config/opencode（Windows 上同样落在用户目录的 .config 下）。
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } configured)
                return configured;
            if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg)
                return Path.Combine(xdg, "opencode");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode");
        }
    }
}
