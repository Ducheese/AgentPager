using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed record NetworkAddressOption(string ID, string Label, string Address);

public static class NetworkAddresses
{
    public static List<NetworkAddressOption> Available()
    {
        var result = new List<(NetworkAddressOption Option, bool Gateway)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(value =>
                value.Address.AddressFamily == AddressFamily.InterNetwork &&
                !value.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicast.Address)) continue;
                result.Add((new(adapter.Id, $"{adapter.Name} · {unicast.Address}", unicast.Address.ToString()), hasGateway));
            }
        }
        return result.OrderByDescending(value => value.Gateway).ThenBy(value => value.Option.Label)
            .Select(value => value.Option).ToList();
    }
}

public sealed class BridgeSettings
{
    public string? SelectedNetworkAddress { get; set; }
    /// <summary>
    /// 外网隧道域名（ngrok 之类），只存域名本身。手机连不上局域网地址时改走
    /// wss://这个域名/agentgrid —— 见 PairingPayload.TunnelHost。
    /// </summary>
    public string? TunnelHost { get; set; }

    /// <summary>
    /// 是否开启内置 ngrok 隧道守护。默认 true（开箱即用，随桥自动静默启动与回收）。
    /// </summary>
    public bool EnableNgrokTunnel { get; set; } = true;

    public bool LaunchAtLogin { get; set; } = true;
    public bool HasCompletedFirstRun { get; set; }

    public static BridgeSettings Load()
    {
        try
        {
            if (File.Exists(AgentPagerPaths.SettingsFile))
                return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(AgentPagerPaths.SettingsFile)) ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        WriteFile();
        if (LaunchAtLogin) StartupRegistration.Enable();
        else StartupRegistration.Disable();
    }

    /// <summary>
    /// 只落盘，不碰注册表。输入框边打字边存走这条 —— 每敲一个键都去写一遍 Run 键没有道理。
    /// </summary>
    public void SaveQuietly() => WriteFile();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private void WriteFile()
    {
        Directory.CreateDirectory(AgentPagerPaths.DataRoot);
        File.WriteAllText(AgentPagerPaths.SettingsFile,
            JsonSerializer.Serialize(this, WriteOptions));
    }
}

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentPager";

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public sealed class PairingSecretStore
{
    private readonly string _path = Path.Combine(AgentPagerPaths.DataRoot, "pairing-secret.bin");

    public byte[] LoadOrCreate()
    {
        Directory.CreateDirectory(AgentPagerPaths.DataRoot);
        if (File.Exists(_path))
        {
            try { return Unprotect(File.ReadAllBytes(_path)); }
            catch { /* 若 DPAPI 解密失败（如跨机器或损坏），自动轮换刷新 */ }
        }
        return Rotate();
    }

    public byte[] Rotate()
    {
        Directory.CreateDirectory(AgentPagerPaths.DataRoot);
        var value = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_path, Protect(value));
        return value;
    }

    private static byte[] Protect(byte[] value) => Crypt(value, protect: true);
    private static byte[] Unprotect(byte[] value) => Crypt(value, protect: false);

    private static byte[] Crypt(byte[] value, bool protect)
    {
        var input = new DataBlob(value);
        var output = new DataBlob();
        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            input.Dispose();
            output.DisposeLocal();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
        public DataBlob(byte[] value)
        {
            Length = value.Length;
            Data = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, Data, value.Length);
        }
        public void Dispose()
        {
            if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
        }
        public void DisposeLocal()
        {
            if (Data != IntPtr.Zero) LocalFree(Data);
            Data = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class DailyFileLog
{
    private readonly object _lock = new();

    public DailyFileLog()
    {
        Directory.CreateDirectory(AgentPagerPaths.LogsDirectory);
        foreach (var path in Directory.EnumerateFiles(AgentPagerPaths.LogsDirectory, "agentpager-*.log"))
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                try { File.Delete(path); } catch (IOException) { }
    }

    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        var path = Path.Combine(AgentPagerPaths.LogsDirectory, $"agentpager-{DateTime.Now:yyyy-MM-dd}.log");
        lock (_lock) File.AppendAllText(path, line);
    }
}
