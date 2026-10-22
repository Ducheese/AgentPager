namespace AgentPager.Core;

/// <summary>
/// 手机远程 prompt 附图暂存区：落盘 + TTL 清扫。
/// 根目录 %LOCALAPPDATA%\AgentPager\attachments\{yyyyMMdd}\att_{unixms}_{8hex}.ext。
/// 只存二进制、只返本地绝对路径；prompt 文本本身走原控制通道透传（零协议改动）。
/// </summary>
public static class AttachmentStore
{
    /// <summary>单图上限 512KB（手机端压缩后的硬上限，超了 App 侧就会判失败）。</summary>
    public const long MaxFileBytes = 512 * 1024;

    /// <summary>暂存保留期 7 天（与 DailyFileLog 日志保留期对齐）。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string DefaultRoot =>
        Path.Combine(AgentPagerPaths.DataRoot, "attachments");

    public sealed record SavedAttachment(string Id, string LocalPath, string FileName, long Size);

    /// <summary>
    /// 边读边写盘：超限立即中断并删除半截文件，不把整个 body 先缓冲成 byte[]。
    /// 时间戳与日期目录统一用 UTC，避免 23:30 上传时目录名与文件名跨天对不上。
    /// </summary>
    public static SavedAttachment Save(
        Stream content,
        string? fileName = null,
        string? root = null,
        DateTimeOffset? now = null,
        string? meta = null)
    {
        var targetRoot = root ?? DefaultRoot;
        var instant = now ?? DateTimeOffset.UtcNow;
        var dir = Path.Combine(targetRoot, instant.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);

        var ext = NormalizeExtension(fileName);
        var stamp = instant.ToUnixTimeMilliseconds();
        var rand = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var id = $"att_{stamp}_{rand}";
        var suffix = NormalizeMeta(meta);
        var name = id + (suffix is null ? "" : "_" + suffix) + ext;
        var path = Path.Combine(dir, name);

        long total = 0;
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxFileBytes)
                        throw new InvalidDataException($"附件超过上限（{MaxFileBytes} 字节）");
                    output.Write(buffer, 0, read);
                }
            }
            if (total == 0)
                throw new InvalidDataException("附件内容为空");
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
        return new(id, path, name, total);
    }

    public static SavedAttachment Save(
        byte[] bytes,
        string? fileName = null,
        string? root = null,
        DateTimeOffset? now = null,
        string? meta = null)
    {
        if (bytes is null || bytes.Length == 0)
            throw new InvalidDataException("附件内容为空");
        using var stream = new MemoryStream(bytes, writable: false);
        return Save(stream, fileName, root, now, meta);
    }

    /// <summary>
    /// 清掉 LastWriteTimeUtc 早于 now-maxAge 的暂存文件，顺手摘空目录。返回删除数。
    /// 按文件 mtime 而不是日期目录名判定：目录名只是分桶，mtime 才是真相。
    /// </summary>
    public static int SweepOlderThan(
        TimeSpan maxAge,
        string? root = null,
        DateTimeOffset? now = null,
        Action<string, string>? log = null)
    {
        var targetRoot = root ?? SweepRootOverride ?? DefaultRoot;
        if (!Directory.Exists(targetRoot)) return 0;
        var current = now ?? DateTimeOffset.UtcNow;
        var cutoff = current - maxAge;
        var deleted = 0;
        // 枚举本身也会抛（权限、目录被删、路径过长）；先物化再删，
        // 否则一个坏目录会把 TaskCatalog.Maintain 整条维护链打断。
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("WARN", $"[附件] 枚举暂存目录失败，跳过本轮：{targetRoot}（{error.Message}）");
            return 0;
        }
        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("WARN", $"[附件] 删除过期暂存失败：{file}（{error.Message}）");
            }
        }
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(targetRoot).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("WARN", $"[附件] 枚举日期目录失败，跳过清理：{targetRoot}（{error.Message}）");
            return deleted;
        }
        var today = current.ToString("yyyyMMdd");
        foreach (var dir in dirs)
        {
            // 当天的目录可能正好处在 CreateDirectory 与首次写入之间，不动它。
            if (Path.GetFileName(dir) == today) continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("WARN", $"[附件] 删除空目录失败：{dir}（{error.Message}）");
            }
        }
        return deleted;
    }

    /// <summary>
    /// 清扫根目录的测试注入点（null = DefaultRoot）。
    /// Core 测试会跑 Maintain，不隔离就会删真实用户的暂存文件。
    /// </summary>
    public static string? SweepRootOverride { get; set; }

    /// <summary>
    /// 压缩参数白名单：[0-9a-z_]，最长 24。非法字符直接滤掉，滤空就当没有——
    /// 这段来自手机端表单，绝不允许把路径分隔符或点带进文件名。
    /// </summary>
    internal static string? NormalizeMeta(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta)) return null;
        var cleaned = new string(meta.ToLowerInvariant()
            .Where(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '_')
            .ToArray());
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= 24 ? cleaned : cleaned[..24];
    }

    internal static string NormalizeExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".png" => ".png",
            ".webp" => ".webp",
            _ => ".jpg",
        };
    }
}
