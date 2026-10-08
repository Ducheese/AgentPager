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

    public MainWindow(BridgeRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
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
        PairingTextBox.Text = state.PairingText;
        PairingQrImage.Source = QrCode(state.PairingText);
        NetworkCombo.ItemsSource = state.Networks;
        NetworkCombo.SelectedValue = state.SelectedAddress;
        StartupCheckBox.IsChecked = _runtime.Settings.LaunchAtLogin;
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
