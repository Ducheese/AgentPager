using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentPager.Core;

/// <summary>
/// Claude Code 通过 stdin 传给 Hook 的载荷。事件名以字符串保留，向前兼容
/// Claude 未来新增的事件。字段命名与官方 Hook 协议一致（snake_case）。
///
/// 字段依据：本机 2.1.236 的 bin/claude.exe 内嵌 JS 中每个事件的构造式
/// （docs/claude-code-state-mapping.md 第一节的取证方法）。缺字段会让
/// System.Text.Json 静默丢弃该键，因此"类型里有"必须等于"真机会传"——
/// 例如 task_description 只属于 TaskCreated/TaskCompleted，不属于 SubagentStart。
/// </summary>
public sealed record ClaudeHookPayload(
    string Cwd,
    [property: JsonPropertyName("hook_event_name")] string HookEventName,
    [property: JsonPropertyName("session_id")] string SessionID,
    [property: JsonPropertyName("transcript_path")] string? TranscriptPath = null,
    [property: JsonPropertyName("permission_mode")] string? PermissionMode = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null,
    [property: JsonPropertyName("tool_use_id")] string? ToolUseID = null,
    [property: JsonPropertyName("tool_input")] JsonElement? ToolInput = null,
    string? Prompt = null,
    string? Model = null,
    string? Source = null,
    string? Message = null,
    string? Title = null,
    [property: JsonPropertyName("notification_type")] string? NotificationType = null,
    string? Subtype = null,
    [property: JsonPropertyName("stop_hook_active")] bool? StopHookActive = null,
    [property: JsonPropertyName("last_assistant_message")] string? LastAssistantMessage = null,
    [property: JsonPropertyName("agent_id")] string? AgentID = null,
    [property: JsonPropertyName("agent_type")] string? AgentType = null,
    [property: JsonPropertyName("agent_transcript_path")] string? AgentTranscriptPath = null,
    [property: JsonPropertyName("task_description")] string? TaskDescription = null,
    [property: JsonPropertyName("session_title")] string? SessionTitle = null,
    // ---- 失败与中断（PostToolUseFailure / PermissionDenied / StopFailure）----
    string? Error = null,
    [property: JsonPropertyName("error_details")] string? ErrorDetails = null,
    /// <summary>工具失败是否由用户中断（Esc）引起——Claude 侧唯一及时的中断信号。</summary>
    [property: JsonPropertyName("is_interrupt")] bool? IsInterrupt = null,
    string? Reason = null)
{
    [JsonIgnore]
    public string ProjectName =>
        Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } value
            ? value
            : "Claude Code";

    /// <summary>事件是否发生在子代理内部：公共入参无条件带 agent_id，非空即为子代理。</summary>
    [JsonIgnore]
    public bool IsInsideSubagent => !string.IsNullOrWhiteSpace(AgentID);
}

public enum ClaudePermissionDecision { Allow, Deny }

/// <summary>与 OpenCode / Antigravity 两条来源同一口径的三态。</summary>
public enum ClaudeHookStatus { NotInstalled, Installed, Outdated }

/// <summary>
/// 生成 Claude Code 期望的 Hook stdout 响应。非权限事件不需要输出；
/// PermissionRequest 必须返回带 hookSpecificOutput 的 JSON。
/// 形状与 2.1.236 二进制里的 zod 联合体逐字对齐：
/// decision = { behavior: "allow", updatedInput?, updatedPermissions? } | { behavior: "deny", message?, interrupt? }。
/// </summary>
public static class ClaudeHookOutput
{
    public static byte[] Permission(ClaudePermissionDecision decision)
    {
        var value = new
        {
            @continue = true,
            suppressOutput = true,
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = new { behavior = decision == ClaudePermissionDecision.Allow ? "allow" : "deny" },
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
    }

    /// <summary>
    /// 把手机上的作答写回挂起的 `PermissionRequest`。
    ///
    /// 依据（本机 2.1.236 二进制实测，见 notes §八）：`AskUserQuestion` 是
    /// `requiresUserInteraction(){return!0}` 的工具，Claude Code 对该类工具的 hook 放行
    /// **只在带回 `updatedInput` 时才接受**（`if(!g.updatedInput && e.requiresUserInteraction?.()) return null;`），
    /// 然后以这份入参去跑工具；而工具自己的 `call()` 把入参里的答案原样透传成结果
    /// （`async call(e,t){let{questions:r,answers:n={},…}=e; return {data:{questions:r,answers:n,…}}}`）。
    /// 输入校验只查"问题文本唯一、每题选项 label 唯一"，不拦自带答案的入参。
    ///
    /// 所以这里**必须整份带回 `questions`**——这是"改写工具入参"，不是打补丁；
    /// 只塞答案会把问题本身抹掉。答案用 `response`（自由文本）承载，与 App 现有的输入框一对一。
    /// </summary>
    public static byte[] Answer(string rawToolInputJson, string answer)
    {
        var updated = ParseInput(rawToolInputJson);

        // 点选项 vs 打自由文本：两种都是工具官方支持的答案形态，只是落在不同字段。
        //   · 命中某个 label → `answers{问题全文: label}`（结构化选择）
        //   · 否则            → `response`（"用户手打的自由文本"）
        if (MatchOption(updated, answer) is { } selection)
            updated["answers"] = new JsonObject { [selection.Question] = selection.Label };
        else
            updated["response"] = answer;

        return Encode(updated);
    }

    /// <summary>
    /// 多题一次性回填：<paramref name="answers"/> 是"问题全文 → 答案"的整张表。
    /// 工具层的 `answers` 本来就是 map（多题各占一个 key），所以这里不需要逐题拆包。
    /// 桥端把多题拆成"手机上一题一屏"逐题收集，攒齐后走这一条。
    /// </summary>
    public static byte[] AnswerMap(string rawToolInputJson, IReadOnlyDictionary<string, string> answers)
    {
        var updated = ParseInput(rawToolInputJson);
        var map = new JsonObject();
        foreach (var (question, answer) in answers) map[question] = answer;
        updated["answers"] = map;
        return Encode(updated);
    }

    private static JsonObject ParseInput(string rawToolInputJson)
    {
        try
        {
            return JsonNode.Parse(rawToolInputJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            // 原始入参不可解析时不冒险改写：退回一份只带答案的对象，让 Claude 自己校验失败。
            return new JsonObject();
        }
    }

    private static byte[] Encode(JsonObject updated)
    {
        var value = new
        {
            @continue = true,
            suppressOutput = true,
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = new { behavior = "allow", updatedInput = updated },
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
    }

    /// <summary>
    /// 把答案与选项对表：命中就说明用户是**点选项**而不是打字。
    /// 用归一化后的值比对——按钮显示的是 `label（注解）`，App 回传的也是那一整串。
    /// </summary>
    private static (string Question, string Label)? MatchOption(JsonObject input, string answer)
    {
        if (input["questions"] is not JsonArray questions) return null;
        foreach (var node in questions)
        {
            if (node is not JsonObject question) continue;
            var labels = Labels(question);
            if (labels.Count == 0) continue;
            var normalized = NormalizeOptionLabel(answer, labels);
            if (!labels.Contains(normalized)) continue;
            var text = StringValue(question, "question") ?? StringValue(question, "header");
            if (!string.IsNullOrWhiteSpace(text)) return (text, normalized);
        }
        return null;
    }

    private static List<string> Labels(JsonObject question)
    {
        var labels = new List<string>();
        if (question["options"] is not JsonArray options) return labels;
        foreach (var option in options)
            if (option is JsonObject item && StringValue(item, "label") is { Length: > 0 } label)
                labels.Add(label);
        return labels;
    }

    /// <summary>
    /// 把回传的选项文本还原成**纯 label**。
    ///
    /// 手机上的选项按钮显示的是 `label（注解）`（用户要求"注解写在选项框里"），App 回传的也是这一整串；
    /// 而 `answers` 的 value 应当是纯 label。所以：**先原样对表**（label 自己就可能带括号，不能一上来就剥），
    /// 不中再剥掉尾部 `（…）` 对一次；都不中就原样返回（当自由文本）。
    /// </summary>
    public static string NormalizeOptionLabel(string candidate, IReadOnlyCollection<string> labels)
    {
        var trimmed = candidate.Trim();
        if (labels.Contains(trimmed)) return trimmed;
        var open = trimmed.LastIndexOf('（');
        if (open > 0 && trimmed.EndsWith('）'))
        {
            var bare = trimmed[..open].TrimEnd();
            if (labels.Contains(bare)) return bare;
        }
        return candidate;
    }

    private static string? StringValue(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public enum HookSource { Codex, Claude, OpenCode, Antigravity }

/// <summary>桥接服务器收到并已分类的 Hook 事件。</summary>
public abstract record HookEnvelope
{
    public sealed record Codex(CodexHookPayload Hook) : HookEnvelope;
    public sealed record Claude(ClaudeHookPayload Hook) : HookEnvelope;
    public sealed record OpenCode(OpenCodeHookPayload Hook) : HookEnvelope;
    public sealed record Antigravity(AgyHookPayload Hook) : HookEnvelope;
}

/// <summary>
/// Claude Code Hook 安装器：部署 <c>agentpager-claude.js</c> 轻量转发脚本，
/// 并把 <c>~/.claude/settings.json</c> 里的 14 组托管 Hook 指向它。
///
/// 通道形态与 Antigravity 一致（脚本 + TCP 49361），而**不是**早期版本的
/// <c>"&lt;bridge exe&gt;" --hook --source claude</c>——后者每次事件冷启一个约 220 MB 的
/// 自包含 .NET 进程（agy 判例 #1 实测 300~800 ms/次）。
///
/// Hook 命令用 **exec 形式**（<c>command: "node"</c> + <c>args: [脚本绝对路径]</c>）：
/// 2.1.236 的 schema 明确 "When present, command is resolved as an executable and
/// spawned directly with these arguments — no shell"，于是 Windows 下默认 shell
/// （有 Git Bash 用 bash、否则 PowerShell）的引号与反斜杠转义问题整体消失。
///
/// 结构仍与 CodexHookInstaller 对齐：保留用户既有 Hook、幂等替换管理项、卸载只删管理项。
/// </summary>
public sealed class ClaudeHookConfiguration(string? claudeConfigDir = null)
{
    public const string ManagedStatus = "Managed by AgentPager (Claude Code)";
    private const string LegacyManagedStatus = "Managed by AgentGrid (Claude Code)";
    public const string ScriptFileName = "agentpager-claude.js";
    public const string CommandExecutable = "node";
    private const string ResourceName = "AgentPager.Core.Resources.agentpager-claude.js";

    private static readonly Regex VersionPattern = new(
        @"^//\s*agentpager-claude\s+v(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Lazy<string> EmbeddedContent = new(LoadEmbedded);

    // 覆盖 Claude Code 全部生命周期事件；PermissionRequest 需要长等手机作答，故给足超时。
    private static readonly (string Name, string? Matcher, int? Timeout)[] Events =
    [
        ("SessionStart", null, null),
        ("SessionEnd", null, null),
        ("UserPromptSubmit", null, null),
        ("Stop", null, null),
        ("StopFailure", null, null),
        ("SubagentStart", null, null),
        ("SubagentStop", null, null),
        ("Notification", "*", null),
        ("PreToolUse", "*", null),
        ("PermissionRequest", "*", 86_400),
        ("PostToolUse", "*", null),
        ("PostToolUseFailure", "*", null),
        ("PermissionDenied", "*", null),
        ("PreCompact", null, null),
    ];

    private readonly string _settingsPath = Path.Combine(
        claudeConfigDir ?? ClaudeConfigDirectory.Home,
        "settings.json");

    private readonly string _configDirectory = claudeConfigDir ?? ClaudeConfigDirectory.Home;

    public string SettingsPath => _settingsPath;

    public string ScriptPath => Path.Combine(_configDirectory, ScriptFileName);

    /// <summary>内置（嵌入资源）脚本的版本号。</summary>
    public static int? EmbeddedVersion => ParseVersion(EmbeddedContent.Value);

    /// <summary>已部署脚本的版本号；文件不存在或没有版本头时为 null。</summary>
    public int? InstalledVersion
    {
        get
        {
            try { return File.Exists(ScriptPath) ? ParseVersion(File.ReadAllText(ScriptPath)) : null; }
            catch { return null; }
        }
    }

    /// <summary>
    /// 三态（与 OpenCode / Antigravity 同一口径）：
    /// 未安装 / 已写入且脚本与内置逐字一致 / 已写入但脚本缺失或陈旧（待更新）。
    /// **bool 表达不了"hooks 已注册、脚本却是旧版本"这一态**，只会把它误报成"未安装"——
    /// 而这一态从本轮起真实存在（hook 命令指向部署出去的 vN 脚本，升级桥后脚本会落后于内置版本）。
    /// </summary>
    public ClaudeHookStatus Status
    {
        get
        {
            if (!File.Exists(_settingsPath)) return ClaudeHookStatus.NotInstalled;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject;
                if (root?["hooks"] is not JsonObject hooks) return ClaudeHookStatus.NotInstalled;
                var registered = Events.All(spec =>
                    hooks[spec.Name] is JsonArray groups && groups.Any(node => IsManaged(node as JsonObject)));
                if (!registered) return ClaudeHookStatus.NotInstalled;
                return ScriptIsCurrent() ? ClaudeHookStatus.Installed : ClaudeHookStatus.Outdated;
            }
            catch { return ClaudeHookStatus.NotInstalled; }
        }
    }

    /// <summary>陈旧也算"装了"：hooks 已在注册、脚本还在，只是内容不是内置版本。</summary>
    public bool IsInstalled() => Status != ClaudeHookStatus.NotInstalled;

    public HookConfigurationChange Install()
    {
        var changed = DeployScript();
        var result = Mutate(root =>
        {
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            foreach (var spec in Events)
            {
                var groups = hooks[spec.Name] as JsonArray ?? [];
                var retained = new JsonArray(groups.Where(node => !IsManaged(node as JsonObject))
                    .Select(node => node?.DeepClone()).ToArray());
                var handler = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = CommandExecutable,
                    ["args"] = new JsonArray(ScriptPath.Replace('\\', '/')),
                    ["statusMessage"] = ManagedStatus,
                };
                if (spec.Timeout is not null) handler["timeout"] = spec.Timeout;
                var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
                if (spec.Matcher is not null) group["matcher"] = spec.Matcher;
                retained.Add(group);
                hooks[spec.Name] = retained;
            }
            return root;
        });
        return new(changed || result.Changed, result.BackupPath);
    }

    public HookConfigurationChange Uninstall()
    {
        var result = Mutate(root =>
        {
            if (root["hooks"] is not JsonObject hooks) return root;
            foreach (var spec in Events)
            {
                if (hooks[spec.Name] is not JsonArray groups) continue;
                var retained = new JsonArray(groups.Where(node => !IsManaged(node as JsonObject))
                    .Select(node => node?.DeepClone()).ToArray());
                if (retained.Count == 0) hooks.Remove(spec.Name);
                else hooks[spec.Name] = retained;
            }
            if (hooks.Count == 0) root.Remove("hooks");
            return root;
        }, deleteWhenEmpty: true);
        // 注意别写成 `result.Changed || RemoveScript()`——那会在 Changed 为 true 时短路，脚本删不掉。
        var scriptRemoved = RemoveScript();
        return new(result.Changed || scriptRemoved, result.BackupPath);
    }

    /// <summary>部署/更新转发脚本；内容一致时不重写（返回 false）。</summary>
    private bool DeployScript()
    {
        var content = EmbeddedContent.Value;
        try
        {
            if (File.Exists(ScriptPath) && Normalize(File.ReadAllText(ScriptPath)) == Normalize(content))
                return false;
        }
        catch
        {
            // 读不出来就重写
        }

        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(ScriptPath, content, new UTF8Encoding(false));
        return true;
    }

    /// <summary>卸载脚本：只删带托管标记的文件，绝不动用户自己的脚本。</summary>
    private bool RemoveScript()
    {
        try
        {
            if (!File.Exists(ScriptPath)) return false;
            if (!File.ReadAllText(ScriptPath).Contains("Managed by AgentPager (Claude Code)", StringComparison.Ordinal))
                return false;
            File.Delete(ScriptPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ScriptIsCurrent()
    {
        try
        {
            return File.Exists(ScriptPath) &&
                   Normalize(File.ReadAllText(ScriptPath)) == Normalize(EmbeddedContent.Value);
        }
        catch { return false; }
    }

    private HookConfigurationChange Mutate(Func<JsonObject, JsonObject> mutation, bool deleteWhenEmpty = false)
    {
        var original = File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : null;
        JsonObject root;
        try { root = original is null ? [] : JsonNode.Parse(original)?.AsObject() ?? []; }
        catch (Exception error) { throw new InvalidDataException("Claude settings.json 不是有效的 JSON。", error); }

        var result = mutation(root);
        var output = result.Count == 0 && deleteWhenEmpty
            ? null
            : result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Normalize(original) == Normalize(output)) return new(false);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var backup = original is null ? null : Backup(original);
        if (output is null) File.Delete(_settingsPath);
        else File.WriteAllText(_settingsPath, output + Environment.NewLine, new UTF8Encoding(false));
        return new(true, backup);
    }

    private string Backup(string contents)
    {
        var stem = $"{_settingsPath}.agentpager-claude-backup-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var path = stem;
        for (var index = 1; File.Exists(path); index++) path = $"{stem}-{index}";
        File.WriteAllText(path, contents);
        return path;
    }

    private static string? Normalize(string? value) => value?.Replace("\r\n", "\n").Trim();

    /// <summary>
    /// 托管项识别覆盖三代形态：
    /// ① 现行 exec 形式（args 指向 agentpager-claude.js）；
    /// ② statusMessage 标记（现行与 AgentGrid 旧标记）；
    /// ③ 更早的 <c>"&lt;bridge exe&gt;" --hook --source claude</c> 形态（命令里含 AgentPagerBridge）。
    /// </summary>
    private static bool IsManaged(JsonObject? group)
    {
        if (group?["hooks"] is not JsonArray handlers) return false;
        return handlers.Any(node =>
        {
            var handler = node as JsonObject;
            var status = handler?["statusMessage"]?.GetValue<string>();
            if (status is ManagedStatus or LegacyManagedStatus) return true;
            var args = handler?["args"] as JsonArray;
            if (args is not null && args.Any(value =>
                    value?.GetValue<string>()?.Contains(ScriptFileName, StringComparison.OrdinalIgnoreCase) == true))
                return true;
            var command = handler?["command"]?.GetValue<string>();
            return command?.Contains("AgentPagerBridge", StringComparison.OrdinalIgnoreCase) == true;
        });
    }

    private static string LoadEmbedded()
    {
        using var stream = typeof(ClaudeHookConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("缺少 Claude Code 转发脚本嵌入资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static int? ParseVersion(string contents)
    {
        var match = VersionPattern.Match(contents);
        return match.Success && int.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }
}

public static class ClaudeConfigDirectory
{
    public static string Home =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}

/// <summary>
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> 会话注册表：本机每个活跃 Claude 进程一份，
/// 正常退出时由 Claude Code 自己 unlink（实测源码：<c>process.on("exit", …unlinkSync)</c> 加异步兜底），
/// 崩溃残留由它的 sweep 清理——两种情况下"文件消失"都等于离线。
///
/// 用途只有两个，且都只当**正判**用：离线（见过才敢断）与排队（status）。
/// 注册并非在所有模式下都开（注册函数开头有条件早退），所以没见过就什么都不做。
/// </summary>
public static class ClaudeSessionRegistry
{
    public static string Directory =>
        Path.Combine(ClaudeConfigDirectory.Home, "sessions");

    public sealed record Entry(string SessionID, string? Name, string? NameSource, string? Status, long? StartedAt);

    /// <summary>扫描注册表，返回 session_id → 条目（文件数通常个位数，无需缓存）。</summary>
    public static Dictionary<string, Entry> Scan(string? directory = null)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var path = directory ?? Directory;
        try
        {
            if (!System.IO.Directory.Exists(path)) return result;
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    var sessionId = id.GetString();
                    if (string.IsNullOrWhiteSpace(sessionId)) continue;
                    result[sessionId] = new Entry(
                        sessionId,
                        Text(root, "name"),
                        Text(root, "nameSource"),
                        Text(root, "status"),
                        root.TryGetProperty("startedAt", out var started) && started.TryGetInt64(out var value) ? value : null);
                }
                catch
                {
                    // 正在写入或已损坏的条目：跳过。
                }
            }
        }
        catch
        {
            // 目录不可读：当作"没有正判信息"，绝不当成离线。
        }
        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
