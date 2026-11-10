using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Carp.Gui.Services;

namespace Carp.Gui;

public partial class App : Application
{
    // 单实例：第二次启动不会开第二个面板，而是把已经在跑的那个叫到前台。
    // 优先用 Global\ 作用域，这样「另一个用户会话」也不会再起一份抢同一个端口；
    // 没有权限时退到 Local\（同一会话内仍然有效）。
    private const string MutexBaseName = "Carp.Gui.Instance";
    private const string ShowEventBaseName = "Carp.Gui.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;
    private string _scope = @"Local\";

    /// <summary>--minimized：登录自启时直接收进托盘，不弹窗口。</summary>
    public bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = Array.Exists(e.Args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        // 让核心（含 backup/doctor 等子进程）把日志写进 conf\logs，而不是 data\logs。
        Environment.SetEnvironmentVariable("CARP_LOG_DIR", System.IO.Path.Combine(GuiConfig.ConfDir, "logs"));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error("未处理的异常：" + ex);
            }
        };

        if (!TryAcquireSingleInstance(out var isFirstInstance))
        {
            // 连互斥体都建不出来时，宁可让用户开第二份，也不要直接退出。
            isFirstInstance = true;
        }
        if (!isFirstInstance)
        {
            // 已有面板在跑：唤醒它，然后自己退出。
            AppLog.Info("[GUI] 检测到已有面板在运行，请求唤醒后退出");
            try
            {
                if (EventWaitHandle.TryOpenExisting(_scope + ShowEventBaseName, out var handle))
                {
                    handle.Set();
                    handle.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("唤醒已运行的面板失败：" + ex.Message);
            }
            Shutdown(0);
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _scope + ShowEventBaseName);
        }
        catch (Exception ex)
        {
            AppLog.Error("创建唤醒事件失败：" + ex.Message);
        }
        _showListener = new Thread(ShowListenerLoop) { IsBackground = true, Name = "Carp.ShowListener" };
        _showListener.Start();

        base.OnStartup(e);

        // 不再用 StartupUri：需要先决定是否直接进托盘。
        var window = new MainWindow();
        MainWindow = window;
        if (StartMinimized)
        {
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
        }
        window.Show();
        if (StartMinimized)
        {
            window.Hide();
        }
    }

    /// <summary>先试 Global\，没有权限再退回 Local\；两者都失败返回 false。</summary>
    private bool TryAcquireSingleInstance(out bool isFirstInstance)
    {
        isFirstInstance = true;
        foreach (var scope in new[] { @"Global\", @"Local\" })
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, scope + MutexBaseName, out var created);
                _scope = scope;
                _instanceMutex = mutex;
                isFirstInstance = created;
                if (!created)
                {
                    // 不是第一份，这个句柄留着也没用。
                    mutex.Dispose();
                    _instanceMutex = null;
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Global\ 需要权限，换 Local\ 再试。
            }
            catch (Exception ex)
            {
                AppLog.Error($"创建单实例互斥体失败（{scope}）：{ex.Message}");
            }
        }
        return false;
    }

    private void ShowListenerLoop()
    {
        var handle = _showEvent;
        if (handle == null)
        {
            return;
        }
        try
        {
            while (handle.WaitOne())
            {
                AppLog.Info("[GUI] 收到唤醒请求，显示窗口");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is MainWindow window)
                    {
                        window.ShowFromTray();
                    }
                }));
            }
        }
        catch (ObjectDisposedException)
        {
            // 退出时释放，正常结束。
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 关机/注销：先把核心停掉，避免留下孤儿进程。
        if (MainWindow is MainWindow window)
        {
            window.ShutdownForSessionEnd();
        }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _showEvent?.Dispose();
        }
        catch
        {
            // 退出阶段忽略
        }
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // 未持有互斥体时释放会抛异常，忽略。
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("界面异常：" + e.Exception);
        try
        {
            MessageBox.Show(
                "界面出现异常，但程序仍会继续运行。\r\n\r\n" + e.Exception.Message + "\r\n\r\n详细信息已写入日志：" + AppLog.CurrentFile,
                "Carp 控制面板",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 弹窗失败时只保留日志
        }
        e.Handled = true;
    }
}
