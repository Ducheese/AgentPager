using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentPager.Core;
using QRCoder;

namespace AgentPager.Bridge;

public partial class MainWindow : Window
{
    private readonly BridgeRuntime _runtime;
    private bool _refreshing;
    private static readonly System.Windows.Media.Brush TunnelBrushRunning = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly System.Windows.Media.Brush TunnelBrushStarting = CreateFrozenBrush(0xFD, 0xBA, 0x4A);
    private static readonly System.Windows.Media.Brush TunnelBrushDefault = CreateFrozenBrush(0x7F, 0x8A, 0x9A);

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 隧道域名的延迟落盘。挂 LostFocus 靠不住 —— 实测用户输完没点开别处、桥就被
    /// 换了进程，那次输入直接丢掉。改成打字停下就存，键盘焦点去哪都不影响。
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _tunnelCommitTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600),
    };

    public MainWindow(BridgeRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        _tunnelCommitTimer.Tick += (_, _) =>
        {
            _tunnelCommitTimer.Stop();
            _runtime.SetTunnelHost(TunnelHostBox.Text);
        };
        Refresh();
    }

    public event Action? HideRequested;

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }
        _refreshing = true;
        var state = _runtime.State;
        ServiceStatusText.Text = state.ServiceStatus;
        PhoneCountText.Text = state.PhoneCount.ToString();
        HookStatusText.Text = state.HookInstalled ? "已写入" : "未安装";
        ClaudeHookStatusText.Text = state.ClaudeHookText;
        ClaudeHookStatusText.Foreground = state.ClaudeHookState switch
        {
            ClaudeHookStatus.Installed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0x8B, 0xFA)),
            ClaudeHookStatus.Outdated => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xBA, 0x4A)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x8A, 0x9A)),
        };
        OpenCodeStatusText.Text = state.OpenCodePluginText;
        OpenCodeStatusText.Foreground = state.OpenCodePluginState switch
        {
            OpenCodePluginStatus.Installed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80)),
            OpenCodePluginStatus.Outdated => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xBA, 0x4A)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x8A, 0x9A)),
        };
        AgyStatusText.Text = state.AgyHookText;
        AgyStatusText.Foreground = state.AgyHookState switch
        {
            AntigravityPluginStatus.Installed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8)),
            AntigravityPluginStatus.Outdated => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xBA, 0x4A)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x8A, 0x9A)),
        };
        LastHookText.Text = state.LastHookAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未收到";
        if (PairingTextBox.Text != state.PairingText)
        {
            PairingTextBox.Text = state.PairingText;
            PairingQrImage.Source = QrCode(state.PairingText);
        }
        NetworkCombo.ItemsSource = state.Networks;
        NetworkCombo.SelectedValue = state.SelectedAddress;
        StartupCheckBox.IsChecked = _runtime.Settings.LaunchAtLogin;
        EnableNgrokCheckBox.IsChecked = _runtime.Settings.EnableNgrokTunnel;
        TunnelStatusText.Text = state.TunnelStatusText;
        TunnelStatusText.Foreground = state.TunnelStatus switch
        {
            NgrokTunnelStatus.Running => TunnelBrushRunning,
            NgrokTunnelStatus.Starting => TunnelBrushStarting,
            NgrokTunnelStatus.Failed or NgrokTunnelStatus.NotFound => System.Windows.Media.Brushes.OrangeRed,
            _ => TunnelBrushDefault,
        };
        if (!TunnelHostBox.IsKeyboardFocusWithin)
        {
            var hostText = _runtime.Settings.TunnelHost;
            if (string.IsNullOrEmpty(hostText) && _runtime.Settings.EnableNgrokTunnel)
            {
                hostText = state.ActiveTunnelHost;
            }
            TunnelHostBox.Text = hostText ?? "";
        }
        DiagnosticText.Text = state.LastError is null
            ? $"49361/49362 · {state.TaskCount} 个任务 · 数据目录 {AgentPagerPaths.DataRoot}"
            : state.LastError;
        DiagnosticText.Foreground = state.LastError is null
            ? (System.Windows.Media.Brush)FindResource("Muted")
            : System.Windows.Media.Brushes.OrangeRed;
        _refreshing = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        HideRequested?.Invoke();
    }

    private void InstallHook_Click(object sender, RoutedEventArgs e) => _runtime.InstallHook();
    private void UninstallHook_Click(object sender, RoutedEventArgs e) => _runtime.UninstallHook();
    private void InstallClaudeHook_Click(object sender, RoutedEventArgs e) => _runtime.InstallClaudeHook();
    private void UninstallClaudeHook_Click(object sender, RoutedEventArgs e) => _runtime.UninstallClaudeHook();
    private void InstallOpenCode_Click(object sender, RoutedEventArgs e) => _runtime.InstallOpenCodePlugin();
    private void UninstallOpenCode_Click(object sender, RoutedEventArgs e) => _runtime.UninstallOpenCodePlugin();
    private void InstallAgy_Click(object sender, RoutedEventArgs e) => _runtime.InstallAgyHook();
    private void UninstallAgy_Click(object sender, RoutedEventArgs e) => _runtime.UninstallAgyHook();
    private void RefreshNetworks_Click(object sender, RoutedEventArgs e) => _runtime.RefreshNetworks();

    private void NetworkCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_refreshing && NetworkCombo.SelectedValue is string address)
            _runtime.SelectAddress(address);
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) _runtime.SetLaunchAtLogin(StartupCheckBox.IsChecked == true);
    }

    private void EnableNgrokCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) _runtime.SetEnableNgrokTunnel(EnableNgrokCheckBox.IsChecked == true);
    }

    /// <summary>
    /// 打字停顿 600ms 再提交。不能每次按键都提交：SetTunnelHost 会触发 PublishState →
    /// Refresh，而 Refresh 每次都重画二维码，逐键提交会卡得打不动字。
    /// </summary>
    private void TunnelHostBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_refreshing) return;
        _tunnelCommitTimer.Stop();
        _tunnelCommitTimer.Start();
    }

    private void TunnelHostBox_Changed(object sender, RoutedEventArgs e) => CommitTunnelHost();

    private void TunnelHostBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) CommitTunnelHost();
    }

    private void CommitTunnelHost()
    {
        _tunnelCommitTimer.Stop();
        if (!_refreshing) _runtime.SetTunnelHost(TunnelHostBox.Text);
    }

    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(PairingTextBox.Text))
            System.Windows.Clipboard.SetText(PairingTextBox.Text);
    }

    private static BitmapImage QrCode(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var bytes = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using var memory = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AgentPagerPaths.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AgentPagerPaths.LogsDirectory)
        {
            UseShellExecute = true,
        });
    }
}
