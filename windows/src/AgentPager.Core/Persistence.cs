using System.Text.Json;

namespace AgentPager.Core;

public static class AgentPagerPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentPager");
    public static string TasksFile => Path.Combine(DataRoot, "tasks.json");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogsDirectory => Path.Combine(DataRoot, "logs");
}

public sealed class TaskSnapshotPersistence(string? filePath = null)
{
    private readonly string _filePath = filePath ?? AgentPagerPaths.TasksFile;

    /// <summary>
    /// 读回上次的行。**非终态的行一律丢弃**（用户裁定，2026-10-14）：
    /// 重启意味着所有挂住的 hook 连接都没了，那些还写着 `running` 的行不可能再收到事件，
    /// 只会变成永远点不动的僵尸（实测三种来源：终端里按 Esc 打断、桥被关掉后该会话再无事件、
    /// headless 探针进程结束时没有 `Stop`）——宁可不要，也不要留着。
    /// **终态行（succeeded/interrupted/offline）保留**：那是历史，清空会让 app 每次重启都变白板。
    /// </summary>
    public List<TaskSnapshot> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            var tasks = JsonSerializer.Deserialize<List<TaskSnapshot>>(File.ReadAllText(_filePath), WireJson.Options) ?? [];
            return tasks.Where(task => task.IsTerminal).ToList();
        }
        catch { return []; }
    }

    public void Save(IEnumerable<TaskSnapshot> tasks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var values = tasks.Select(task => task with { LatestStep = null, Subagents = [] }).ToList();
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, WireJson.Options));
        File.Move(temporary, _filePath, true);
    }
}

public sealed class CodexSessionTitleReader(string? indexPath = null)
{
    private readonly string _indexPath = indexPath ?? Path.Combine(CodexPaths.Home, "session_index.jsonl");

    public Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, (string Title, string UpdatedAt)>();
        if (!File.Exists(_indexPath)) return [];
        foreach (var line in File.ReadLines(_indexPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString();
                var title = root.GetProperty("thread_name").GetString();
                var updated = root.GetProperty("updated_at").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                if (!result.TryGetValue(id, out var previous) ||
                    string.CompareOrdinal(previous.UpdatedAt, updated) <= 0)
                    result[id] = (title, updated);
            }
            catch (JsonException) { }
        }
        return result.ToDictionary(item => item.Key, item => item.Value.Title);
    }
}
