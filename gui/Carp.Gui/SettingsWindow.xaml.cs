using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Carp.Gui.Services;

namespace Carp.Gui;

/// <summary>高级设置：端口、profile、日志级别、开机自启、cookie-secure 与只读路径信息。</summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] LogLevels = { "debug", "info", "warn", "error" };

    private readonly GuiConfig _config;
    private readonly int _originalPort;
    private readonly string _originalProfile;
    private readonly string _originalLogLevel;
    private readonly bool _originalCookieSecure;

    /// <summary>端口 / profile / 日志级别 / cookie-secure 变化后需要重启核心。</summary>
    public bool RequiresCoreRestart { get; private set; }

    public bool Saved { get; private set; }

    public SettingsWindow(GuiConfig config, string corePath)
    {
        InitializeComponent();
        _config = config;
        _originalPort = config.Port;
        _originalProfile = config.Profile;
        _originalLogLevel = config.LogLevel;
        _originalCookieSecure = config.CookieSecure;

        PortBox.Text = config.Port.ToString();
        ProfileBox.SelectedIndex = string.Equals(config.Profile, "high", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var logIndex = Array.IndexOf(LogLevels, config.LogLevel);
        LogLevelBox.SelectedIndex = logIndex >= 0 ? logIndex : 1;
        CookieSecureCheck.IsChecked = config.CookieSecure;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        DataDirBox.Text = config.DataDir;
        DataDirBox.ToolTip = config.DataDir;
        CorePathBox.Text = corePath;
        CorePathBox.ToolTip = corePath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "端口必须是 1–65535 之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            PortBox.SelectAll();
            return;
        }

        if (port != _config.Port && SystemProbe.IsPortInUse(port))
        {
            var proceed = ChoiceDialog.Ask(
                this,
                "端口已被占用",
                $"端口 {port} 当前已被其他程序占用，保存后核心可能无法启动。仍要保存吗？",
                okText: "仍然保存");
            if (!proceed)
            {
                return;
            }
        }

        _config.Port = port;
        _config.Profile = ProfileBox.SelectedIndex == 1 ? "high" : "low";
        _config.LogLevel = LogLevels[Math.Max(0, Math.Min(LogLevels.Length - 1, LogLevelBox.SelectedIndex))];
        _config.CookieSecure = CookieSecureCheck.IsChecked == true;
        _config.AutoStart = AutoStartCheck.IsChecked == true;
        _config.Normalize();
        _config.Save();

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            AutoStart.SetEnabled(_config.AutoStart, exePath);
        }

        RequiresCoreRestart = _config.Port != _originalPort ||
                              !string.Equals(_config.Profile, _originalProfile, StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(_config.LogLevel, _originalLogLevel, StringComparison.OrdinalIgnoreCase) ||
                              _config.CookieSecure != _originalCookieSecure;
        Saved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_config.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_config.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InfoDialog.ShowMessage(this, "无法打开目录", ex.Message, _config.DataDir);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
        {
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 忽略拖动边界情况。
        }
    }
}
