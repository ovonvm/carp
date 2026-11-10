using System;
using System.Windows;
using System.Windows.Threading;
using Carp.Gui.Services;

namespace Carp.Gui;

public partial class App : Application
{
    /// <summary>保留 --minimized 参数的解析（旧快捷方式兼容），当前不做托盘，仅记录。</summary>
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
        base.OnStartup(e);
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
