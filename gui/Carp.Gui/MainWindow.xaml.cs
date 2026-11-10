using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Carp.Gui.Effects;
using Carp.Gui.Services;
using Forms = System.Windows.Forms;

namespace Carp.Gui;

/// <summary>760×470 图标栏控制面板：常驻 core 的启动/停止、备份、自检、密码重置与日志。</summary>
public partial class MainWindow : Window
{
    private const string PasswordAlphabet =
        "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly SolidColorBrush SlateSoft = MakeBrush(0xF2, 0xEC, 0xE2);
    private static readonly SolidColorBrush SlateInk = MakeBrush(0x8A, 0x77, 0x62);

    private readonly GuiConfig _config;
    private readonly CoreController _core;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly string _guiVersion;

    private AppState _state = AppState.Stopped;
    private bool _readOnly;
    private bool _busy;
    private bool _shuttingDown;
    private bool _allowClose;
    private bool _initialAdminShown;
    private bool _auroraRunning;
    private bool _tileDriftRunning;
    private readonly NativeTray _tray;
    private bool _exitRequested;
    private bool _logPending;
    private string? _coreVersion;
    private string? _lastDetail;

    public MainWindow()
    {
        InitializeComponent();
        _guiVersion = ReadGuiVersion();
        _config = GuiConfig.Load();
        _core = new CoreController(_config);
        _core.StateChanged += OnCoreStateChanged;
        _core.LogChanged += OnCoreLogChanged;
        _core.CoreError += OnCoreError;

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateStatusText();

        // 托盘：关窗口只是收起来，服务继续跑；只有托盘菜单「退出」才真正退出。
        _tray = new NativeTray();
        _tray.OpenRequested += ShowFromTray;
        _tray.OpenWebRequested += OpenWebInBrowser;
        _tray.ToggleServiceRequested += () => _ = StartOrStopAsync();
        _tray.ExitRequested += ExitFromTray;
        Closed += (_, _) => _tray.Dispose();


        HeaderMeta.Text = $"{AddrText()} · v{_guiVersion}";
        UpdateBackupUi();
        ApplyState(AppState.Stopped);
        _core.AddLog($"[GUI] 控制面板 {_guiVersion} 启动；数据目录：{_config.DataDir}");
    }

    // ------------------------------------------------------------------ 启动流程

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        _uptimeTimer.Start();
        await BootstrapAsync();
    }

    /// <summary>固定的 860×540 在高 DPI 小屏上可能超出工作区，先夹进来再显示。</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var maxHeight = Math.Max(MinHeight, area.Height - 24);
        var maxWidth = Math.Max(MinWidth, area.Width - 24);
        if (Height > maxHeight)
        {
            MaxHeight = maxHeight;
            Height = maxHeight;
        }
        if (Width > maxWidth)
        {
            MaxWidth = maxWidth;
            Width = maxWidth;
        }
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    private async Task BootstrapAsync()
    {
        // 1) 数据目录必须可写：默认是 exe 旁边的 data\。
        var writable = SystemProbe.EnsureWritable(_config.DataDir);
        if (!writable.Ok)
        {
            var switchToLocal = ChoiceDialog.Ask(
                this,
                "数据目录不可写",
                "Carp 的数据目录默认是程序目录下的 data，当前无法写入。可以改用 %LOCALAPPDATA%\\Carp\\data 后继续。",
                $"数据目录：{_config.DataDir}\r\n{writable.Error}",
                "改用 %LOCALAPPDATA%\\Carp",
                "退出");
            if (!switchToLocal)
            {
                _allowClose = true;
                Close();
                return;
            }
            _config.DataDir = GuiConfig.LocalDataDir;
            _config.Save();
            var localWritable = SystemProbe.EnsureWritable(_config.DataDir);
            if (!localWritable.Ok)
            {
                InfoDialog.ShowMessage(this, "数据目录仍不可写", "无法创建或写入新的数据目录。", localWritable.Error);
                _allowClose = true;
                Close();
                return;
            }
            _core.AddLog("[GUI] 数据目录已切换为 " + _config.DataDir);
        }

        // 2) 准备核心程序并做版本匹配。
        var prepared = _core.EnsureCore();
        if (!prepared.Ok)
        {
            Fail(prepared.Error);
            return;
        }
        _coreVersion = ParseCoreVersion(await _core.GetCoreVersionAsync());
        HeaderMeta.Text = $"{AddrText()} · v{_guiVersion}";
        if (_coreVersion == null)
        {
            InfoDialog.ShowMessage(
                this,
                "无法读取核心版本",
                "core 的 version 命令没有返回可识别的版本信息，界面将进入只读模式。",
                $"核心路径：{SafeCorePath()}");
            SetReadOnly("无法读取核心版本");
            return;
        }
        if (!VersionsMatch(_guiVersion, _coreVersion))
        {
            InfoDialog.ShowMessage(
                this,
                "GUI 与核心版本不一致",
                "请使用完整安装包，避免控制协议不匹配。界面将进入只读模式。",
                $"GUI：{_guiVersion}\r\n核心：{_coreVersion}");
            SetReadOnly("版本不一致");
            return;
        }

        // 3) 已有核心：健康检查 -> 询问是否接管。
        if (await _core.HealthAsync())
        {
            var takeOver = ChoiceDialog.Ask(
                this,
                "检测到已在运行的 Carp 核心",
                $"端口 {_config.Port} 上已有一个 Carp 核心在运行。结束它并由本窗口接管，还是只看不改？",
                $"地址：http://127.0.0.1:{_config.Port}/",
                "结束并接管",
                "只读查看");
            if (!takeOver)
            {
                _readOnly = true;
                _state = AppState.Running;
                _core.AddLog("[GUI] 只读模式：保留已在运行的核心");
                ApplyState(AppState.Running);
                return;
            }
            if (!await TerminateExistingCoreAsync())
            {
                SetReadOnly("无法结束已有核心");
                return;
            }
        }
        else if (SystemProbe.IsPortInUse(_config.Port))
        {
            if (!await ResolvePortConflictAsync())
            {
                Fail($"端口 {_config.Port} 已被占用，无法启动 Carp。");
                return;
            }
        }

        // 4) 拉起常驻核心并自动开始服务。
        var launch = await _core.LaunchAsync();
        if (!launch.Ok)
        {
            Fail(launch.Message);
            return;
        }
        var started = await _core.StartAsync(TimeSpan.FromSeconds(25));
        if (!started.Ok)
        {
            Fail(started.Message);
            return;
        }
        await MaybeShowInitialAdminAsync();
        if (!_tray.Registered && !_readOnly)
        {
            _core.AddLog("[GUI] 托盘图标未注册，关闭窗口将直接退出服务");
        }
    }

    // ------------------------------------------------------------------ 核心事件

    private void OnCoreStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown)
            {
                return;
            }
            ApplyState(_core.State);
        }));
    }

    private void OnCoreLogChanged()
    {
        if (_logPending)
        {
            return;
        }
        _logPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _logPending = false;
            LogBox.Text = _core.SnapshotLog();
            LogBox.ScrollToEnd();
            LogCountText.Text = $"最近 {_core.LogCount} 行";
        }));
    }

    private void OnCoreError(string message)
    {
        Dispatcher.BeginInvoke(new Action(() => _ = HandleCoreErrorAsync(message)));
    }

    private async Task HandleCoreErrorAsync(string message)
    {
        if (_shuttingDown || _readOnly)
        {
            return;
        }
        _core.AddLog("[GUI] 核心报错：" + message);
        var lower = message.ToLowerInvariant();
        var portIssue = lower.Contains("address already in use") ||
                        lower.Contains("bind") ||
                        lower.Contains("listen") ||
                        message.Contains("占用");
        if (!portIssue)
        {
            if (!_busy)
            {
                InfoDialog.ShowMessage(this, "核心报错", message);
            }
            return;
        }
        if (_busy)
        {
            return;
        }
        if (!await ResolvePortConflictAsync())
        {
            return;
        }
        var launch = await _core.LaunchAsync();
        if (launch.Ok)
        {
            var start = await _core.StartAsync(TimeSpan.FromSeconds(25));
            if (!start.Ok)
            {
                Fail(start.Message);
            }
        }
        else
        {
            Fail(launch.Message);
        }
    }

    // ------------------------------------------------------------------ 状态显示

    private void ApplyState(AppState state, string? detail = null)
    {
        _state = state;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            _lastDetail = detail;
        }
        if (state == AppState.Running)
        {
            _lastDetail = null;
        }

        (string Glyph, string Title, Brush Host, Brush Icon) visual = state switch
        {
            AppState.Running => ("\uE73E", "服务运行中", (Brush)FindResource("GreenSoft"), (Brush)FindResource("Green")),
            AppState.Starting => ("\uE895", "服务启动中…", (Brush)FindResource("AmberSoft"), (Brush)FindResource("Amber")),
            AppState.Stopping => ("\uE895", "服务停止中…", (Brush)FindResource("AmberSoft"), (Brush)FindResource("Amber")),
            AppState.Error => ("\uE783", "服务出错", (Brush)FindResource("RedSoft"), (Brush)FindResource("Red")),
            _ => ("\uE71A", "服务已停止", SlateSoft, SlateInk),
        };

        StatusIcon.Text = visual.Glyph;
        StatusTitle.Text = visual.Title;
        StatusIconHost.Background = visual.Host;
        StatusIcon.Foreground = visual.Icon;

        SetAurora(state == AppState.Running);
        SetTileDrift(state == AppState.Running);
        UpdateStatusText();
        UpdateControls();
    }

    private void UpdateStatusText()
    {
        var addr = _state == AppState.Running && !_readOnly ? _core.Addr : AddrText();
        if (_readOnly)
        {
            StatusSub.Text = _state == AppState.Running
                ? $"{addr} · 外部核心（只读）"
                : $"{addr} · 只读模式";
            return;
        }
        StatusSub.Text = _state switch
        {
            AppState.Running => $"{addr} · 已运行 {Uptime(_core.RunningSince)}",
            AppState.Starting => $"{addr} · 正在启动服务，请稍候…",
            AppState.Stopping => $"{addr} · 正在停止服务，请稍候…",
            AppState.Error => _lastDetail ?? _core.LastError ?? "请查看运行日志",
            _ => $"{addr} · 未启动",
        };
    }

    private void UpdateControls()
    {
        var running = _state == AppState.Running;
        var transition = _state is AppState.Starting or AppState.Stopping;
        var canControl = !_readOnly && !_busy && !transition;

        TileStartStop.IsEnabled = canControl;
        TileDoctor.IsEnabled = canControl;
        TileResetPassword.IsEnabled = canControl;
        BackupButton.IsEnabled = canControl && _state is AppState.Running or AppState.Stopped or AppState.Error;
        OpenBrowserButton.IsEnabled = running;
        ReadOnlyBadge.Visibility = _readOnly ? Visibility.Visible : Visibility.Collapsed;

        var transitioning = _state is AppState.Starting or AppState.Stopping;
        _tray.Update(running, _readOnly, AddrText(), canControl && !transitioning);
    }

    private void SetReadOnly(string reason)
    {
        _readOnly = true;
        _core.AddLog("[GUI] 进入只读模式：" + reason);
        ApplyState(_state);
    }

    private void SetAurora(bool on)
    {
        if (on == _auroraRunning)
        {
            return;
        }
        _auroraRunning = on;
        if (on)
        {
            AuroraLayer.Visibility = Visibility.Visible;
            var animation = new DoubleAnimation(-280, 280, TimeSpan.FromSeconds(22))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            AuroraShift.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            AuroraShift.BeginAnimation(TranslateTransform.XProperty, null);
            AuroraLayer.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>运行中让「启动 / 停止」卡片的底色缓慢暖流（渐变沿横向来回漂移）。</summary>
    private void SetTileDrift(bool on)
    {
        if (on == _tileDriftRunning)
        {
            return;
        }
        _tileDriftRunning = on;
        if (on)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                RelativeTransform = new TranslateTransform(0, 0),
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFC, 0xF7, 0xED), 0.00));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEA, 0xF7, 0xEF), 0.30));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFD, 0xF1, 0xE3), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xF4, 0xFF), 0.80));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFC, 0xF7, 0xED), 1.00));

            if (brush.RelativeTransform is TranslateTransform shift)
            {
                shift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, 0.5, TimeSpan.FromSeconds(7))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    });
            }

            TileStartStop.Background = brush;
            TileStartStop.BorderBrush = new SolidColorBrush(Color.FromArgb(0x48, 0x2F, 0x9A, 0x4E));
        }
        else
        {
            TileStartStop.ClearValue(Control.BackgroundProperty);
            TileStartStop.ClearValue(Control.BorderBrushProperty);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        UpdateControls();
    }

    private void Fail(string message)
    {
        _core.AddLog("[GUI] " + message);
        _lastDetail = message;
        ApplyState(AppState.Error, message);
        InfoDialog.ShowMessage(this, "Carp 操作失败", message);
    }

    // ------------------------------------------------------------------ 启动 / 停止

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            RippleEffect.Play(button, Mouse.GetPosition(button));
        }
        await StartOrStopAsync();
    }

    private async Task StartOrStopAsync()
    {
        if (_readOnly || _busy || _state is AppState.Starting or AppState.Stopping)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (_state == AppState.Running)
            {
                var stopped = await _core.StopAsync(TimeSpan.FromSeconds(15));
                if (!stopped.Ok)
                {
                    InfoDialog.ShowMessage(this, "停止失败", stopped.Message);
                }
                return;
            }

            if (!_core.IsLaunched)
            {
                var launch = await _core.LaunchAsync();
                if (!launch.Ok)
                {
                    Fail(launch.Message);
                    return;
                }
            }
            var started = await _core.StartAsync(TimeSpan.FromSeconds(25));
            if (!started.Ok)
            {
                Fail(started.Message);
                return;
            }
            await MaybeShowInitialAdminAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> ResolvePortConflictAsync()
    {
        var owner = SystemProbe.DescribePortOwner(_config.Port);
        var details = owner?.Summary ?? "无法识别占用端口的进程（可能需要管理员权限）。";
        var kill = ChoiceDialog.Ask(
            this,
            "端口已被占用",
            $"端口 {_config.Port} 被其他程序占用，Carp 无法启动服务。可以结束该进程后重试。",
            details,
            "结束进程",
            "取消",
            danger: true);
        if (!kill || owner == null)
        {
            return false;
        }
        var confirm = ChoiceDialog.Ask(
            this,
            "确认结束进程",
            $"确定要结束 {owner.Name}（PID {owner.Pid}）吗？该程序未保存的数据可能丢失。",
            details,
            "结束进程",
            "取消",
            danger: true);
        if (!confirm)
        {
            return false;
        }
        var killed = SystemProbe.Kill(owner.Pid);
        if (!killed.Ok)
        {
            InfoDialog.ShowMessage(this, "无法结束进程", killed.Message, details);
            return false;
        }
        await SystemProbe.WaitPortFreeAsync(_config.Port, TimeSpan.FromSeconds(5));
        _core.AddLog($"[GUI] 已结束占用端口的进程 {owner.Name}（PID {owner.Pid}）");
        return true;
    }

    private async Task<bool> TerminateExistingCoreAsync()
    {
        var pid = _core.ReadPidFile();
        if (pid > 0)
        {
            // A stale pid file can point at a recycled PID; only trust it when the
            // process still looks like Carp and actually owns the port.
            var listenerPid = await Task.Run(() => SystemProbe.FindListenerPid(_config.Port));
            var fromFile = SystemProbe.Describe(pid);
            if (!SystemProbe.LooksLikeCarp(fromFile) || (listenerPid > 0 && listenerPid != pid))
            {
                pid = 0;
            }
        }
        if (pid <= 0)
        {
            pid = await Task.Run(() => SystemProbe.FindListenerPid(_config.Port));
        }
        if (pid <= 0)
        {
            InfoDialog.ShowMessage(this, "找不到占用进程", "端口上检测到 Carp 核心，但没有找到对应的进程。");
            return false;
        }
        var info = SystemProbe.Describe(pid);
        var summary = info?.Summary ?? $"PID：{pid}";
        var confirmed = ChoiceDialog.Ask(
            this,
            "结束已运行的 Carp 核心",
            $"将结束 {info?.Name ?? "carp"}（PID {pid}）并接管端口 {_config.Port}。",
            summary,
            "结束并接管",
            "取消",
            danger: true);
        if (!confirmed)
        {
            return false;
        }
        var killed = SystemProbe.Kill(pid);
        if (!killed.Ok)
        {
            InfoDialog.ShowMessage(this, "无法结束进程", killed.Message, summary);
            return false;
        }
        await SystemProbe.WaitPortFreeAsync(_config.Port, TimeSpan.FromSeconds(5));
        _core.AddLog($"[GUI] 已结束原有核心进程（PID {pid}）");
        return true;
    }

    // ------------------------------------------------------------------ 网盘 / 自检 / 备份 / 密码

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) => OpenWebInBrowser();

    private void OpenWebInBrowser()
    {
        var url = _core.AppUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _core.AddLog($"[GUI] 已打开网盘 {url}");
        }
        catch (Exception ex)
        {
            _core.AddLog($"[GUI] 打开网盘失败：{ex.Message}");
            InfoDialog.ShowMessage(this, "无法打开浏览器", ex.Message, url);
        }
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        OpenPath(_config.DataDir);
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        var file = AppLog.CurrentFile;
        if (File.Exists(file))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        else
        {
            OpenPath(AppLog.Directory);
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InfoDialog.ShowMessage(this, "无法打开目录", ex.Message, path);
        }
    }

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _readOnly)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var result = await _core.RunDoctorAsync();
            var output = string.IsNullOrWhiteSpace(result.Output) ? "（没有输出）" : Trim(result.Output, 4000);
            InfoDialog.ShowMessage(
                this,
                "运行自检",
                result.Ok ? "自检完成，输出如下。" : "自检未通过（退出码非 0）。",
                output);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _readOnly)
        {
            return;
        }

        var initial = !string.IsNullOrWhiteSpace(_config.LastBackupDir) && Directory.Exists(_config.LastBackupDir)
            ? _config.LastBackupDir
            : Directory.Exists(GuiConfig.BackupFallbackDir)
                ? GuiConfig.BackupFallbackDir
                : GuiConfig.ConfDir;

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择备份保存目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = initial,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }
        var outDir = dialog.SelectedPath;

        SetBusy(true);
        try
        {
            _core.AddLog("[GUI] 开始备份到 " + outDir);
            var result = await _core.BackupAsync(outDir);
            if (!result.Ok)
            {
                InfoDialog.ShowMessage(this, "备份失败", "备份没有完成。", result.Message);
                return;
            }
            _config.LastBackupDir = outDir;
            _config.LastBackupPath = result.Message;
            _config.LastBackupTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _config.Save();
            UpdateBackupUi();
            InfoDialog.ShowMessage(this, "备份完成", "备份文件：", result.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _readOnly)
        {
            return;
        }
        var confirmed = ChoiceDialog.Ask(
            this,
            "重置 root 密码",
            "只重置 root 账户，其他管理员不受影响。\r\n重置后会生成新的随机密码，且只显示一次。",
            $"数据目录：{_config.DataDir}",
            "重置密码",
            "取消",
            danger: true);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var password = RandomPassword(16);
            _core.AddLog("[GUI] 正在重置 root 密码");
            var result = await _core.ResetRootPasswordAsync(password);
            if (!result.Ok)
            {
                InfoDialog.ShowMessage(this, "重置失败", "无法重置密码。", result.Message);
                return;
            }
            new FirstRunDialog(
                password,
                hint: "密码只显示这一次，请立即复制并保存。",
                heading: "root 密码已重置",
                message: "请用新密码登录 root。其他管理员账号不受影响。",
                account: "root")
            {
                Owner = this,
            }.ShowDialog();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task MaybeShowInitialAdminAsync()
    {
        if (_initialAdminShown)
        {
            return;
        }
        var path = Path.Combine(_config.DataDir, "bootstrap", "initial-admin.txt");
        if (!File.Exists(path))
        {
            return;
        }
        _initialAdminShown = true;
        try
        {
            var text = (await File.ReadAllTextAsync(path)).Trim();
            var password = text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("password:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["password:".Length..].Trim())
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(password))
            {
                return;
            }
            new FirstRunDialog(
                password,
                hint: "密码只显示这一次，请立即复制并保存。之后可在数据目录的 bootstrap\\initial-admin.txt 查看（修改密码后自动删除）。")
            { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取初始管理员密码失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 设置 / 关于 / 窗口

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var dialog = new SettingsWindow(_config, SafeCorePath()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        HeaderMeta.Text = $"{AddrText()} · v{_guiVersion}";
        UpdateBackupUi();
        ApplyState(_state);
        if (dialog.RequiresCoreRestart && !_readOnly)
        {
            await RestartCoreAsync();
        }
    }

    private async Task RestartCoreAsync()
    {
        SetBusy(true);
        try
        {
            if (_core.IsLaunched)
            {
                if (_core.State == AppState.Running)
                {
                    await _core.StopAsync(TimeSpan.FromSeconds(10));
                }
                if (!await _core.ShutdownAsync(TimeSpan.FromSeconds(5)))
                {
                    _core.ForceKill();
                    await SystemProbe.WaitPortFreeAsync(_config.Port, TimeSpan.FromSeconds(3));
                }
            }
            var launch = await _core.LaunchAsync();
            if (!launch.Ok)
            {
                Fail(launch.Message);
                return;
            }
            var started = await _core.StartAsync(TimeSpan.FromSeconds(25));
            if (!started.Ok)
            {
                Fail(started.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        InfoDialog.ShowMessage(
            this,
            "关于 Carp 控制面板",
            "Carp 是自托管的个人网盘。本窗口负责启动、停止与维护本机核心。",
            $"GUI 版本：{_guiVersion}\r\n核心版本：{_coreVersion ?? "未知"}\r\n核心路径：{SafeCorePath()}\r\n数据目录：{_config.DataDir}\r\n配置：{GuiConfig.ConfigPath}");
    }

    private void RailHome_Click(object sender, RoutedEventArgs e)
    {
        if (RailHome.IsChecked != true)
        {
            RailHome.IsChecked = true;
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _core.ClearLog();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
            // 拖动过程中鼠标释放等边界情况，忽略。
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        e.Cancel = true;
        if (_shuttingDown)
        {
            return;
        }

        // 关窗口（X）= 收进托盘，服务继续跑；只有托盘菜单「退出」才走下面的停机流程。
        // 托盘注册失败时没有入口唤回窗口，所以此时关窗口按“退出”处理。
        if (!_exitRequested && _tray.Registered)
        {
            HideToTray();
            return;
        }

        _shuttingDown = true;
        _uptimeTimer.Stop();
        SetBusy(true);
        StatusTitle.Text = "正在退出…";
        StatusSub.Text = "正在停止服务并关闭核心";

        // Close() 不能在 Closing 处理过程中同步调用，交给消息循环下一轮执行。
        Dispatcher.BeginInvoke(new Action(() => _ = ShutdownAndCloseAsync()));
    }

    private void HideToTray()
    {
        Hide();
        _core.AddLog("[GUI] 窗口已收进托盘，服务继续运行");
        _tray.ShowHideHint();
    }

    /// <summary>从托盘点回来（也可能是第二个实例唤醒）。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>关机/注销时同步停核心，避免留下孤儿 carp-core.exe。</summary>
    public void ShutdownForSessionEnd()
    {
        try
        {
            _exitRequested = true;
            _allowClose = true;
            _uptimeTimer.Stop();
            if (_readOnly || !_core.IsLaunched)
            {
                return;
            }
            if (_core.State == AppState.Running)
            {
                _core.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            if (!_core.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult())
            {
                _core.ForceKill();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("关机时停止核心失败：" + ex.Message);
        }
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            if (!_readOnly && _core.IsLaunched)
            {
                if (_core.State is AppState.Running or AppState.Starting)
                {
                    await _core.StopAsync(TimeSpan.FromSeconds(10));
                }
                if (!await _core.ShutdownAsync(TimeSpan.FromSeconds(5)))
                {
                    var force = ChoiceDialog.Ask(
                        this,
                        "核心未退出",
                        "Carp 核心未在 5 秒内退出。可以强制结束进程后退出，或取消并继续使用界面。",
                        okText: "强制结束",
                        cancelText: "取消",
                        danger: true);
                    if (!force)
                    {
                        _shuttingDown = false;
                        SetBusy(false);
                        ApplyState(_state);
                        return;
                    }
                    _core.ForceKill();
                }
            }
            _core.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出时出错：" + ex);
        }
        _allowClose = true;
        Close();
    }

    // ------------------------------------------------------------------ 小工具

    private string AddrText() => $"127.0.0.1:{_config.Port}";

    private string SafeCorePath()
    {
        try
        {
            return _core.CoreExe;
        }
        catch
        {
            return "(未知)";
        }
    }

    private void UpdateBackupUi()
    {
        if (DateTime.TryParse(_config.LastBackupTime, out var time))
        {
            BackupTimeText.Text = time.Date == DateTime.Today
                ? "今天 " + time.ToString("HH:mm")
                : time.ToString("yyyy-MM-dd HH:mm");
        }
        else
        {
            BackupTimeText.Text = "暂无备份";
        }

        var location = !string.IsNullOrWhiteSpace(_config.LastBackupPath)
            ? _config.LastBackupPath
            : !string.IsNullOrWhiteSpace(_config.LastBackupDir)
                ? _config.LastBackupDir
                : GuiConfig.BackupFallbackDir;
        BackupPathText.Text = ShortenPath(location);
        BackupPathText.ToolTip = location;
    }

    private string ShortenPath(string path)
    {
        var conf = GuiConfig.ConfDir.TrimEnd(Path.DirectorySeparatorChar);
        if (path.StartsWith(conf, StringComparison.OrdinalIgnoreCase))
        {
            return "conf" + path[conf.Length..];
        }
        var data = _config.DataDir.TrimEnd(Path.DirectorySeparatorChar);
        if (path.StartsWith(data, StringComparison.OrdinalIgnoreCase))
        {
            return "data" + path[data.Length..];
        }
        return path;
    }

    private static string ReadGuiVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var value = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string? ParseCoreVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("carp ", StringComparison.OrdinalIgnoreCase));
        var tokens = (line ?? output.Trim()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }
        var version = tokens[1];
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static bool VersionsMatch(string left, string right)
    {
        return string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string version)
    {
        var v = version.Trim();
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V'))
        {
            v = v[1..];
        }
        var plus = v.IndexOf('+');
        if (plus >= 0)
        {
            v = v[..plus];
        }
        var dash = v.IndexOf('-');
        if (dash >= 0)
        {
            v = v[..dash];
        }
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        while (parts.Count < 3)
        {
            parts.Add("0");
        }
        return string.Join('.', parts).Trim();
    }

    private static string RandomPassword(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        }
        return new string(chars);
    }

    private static string Trim(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "\r\n…（输出已截断）";
    }

    private static string Uptime(DateTime? since)
    {
        if (since is null)
        {
            return "—";
        }
        var span = DateTime.Now - since.Value;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h{span.Minutes:00}m";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m{span.Seconds:00}s";
        }
        return $"{Math.Max(0, span.Seconds)}s";
    }

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
