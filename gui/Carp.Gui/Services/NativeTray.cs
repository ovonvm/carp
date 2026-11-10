using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Carp.Gui.Services;

/// <summary>
/// 原生托盘图标（Shell_NotifyIcon），消息接收窗口是一个专用的隐藏 HwndSource。
/// 说明：主窗口的 HWND 在这里注册会被 Shell 拒绝（E_FAIL），所以单独开一个
/// 1×1 的隐藏窗口当托盘图标的 owner；不依赖 WinForms 的 NotifyIcon。
/// </summary>
public sealed class NativeTray : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 21;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint TRAY_ICON_ID = 1;

    private readonly Icon _iconRunning;
    private readonly Icon _iconIdle;
    private readonly ContextMenu _menu;
    private readonly MenuItem _toggleItem;
    private readonly HwndSource _sink;

    private bool _added;
    private bool _hintShown;
    private bool _disposed;
    private bool _running;
    private bool _readOnly = true;
    private bool _canControl = true;

    public event Action? OpenRequested;
    public event Action? OpenWebRequested;
    public event Action? ToggleServiceRequested;
    public event Action? ExitRequested;

    public NativeTray()
    {
        _iconRunning = LoadIcon("Carp-running");
        _iconIdle = LoadIcon("Carp-idle");

        _toggleItem = new MenuItem { Header = "停止服务" };
        _toggleItem.Click += (_, _) => ToggleServiceRequested?.Invoke();

        var open = new MenuItem { Header = "打开面板", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => OpenRequested?.Invoke();
        var web = new MenuItem { Header = "打开网盘" };
        web.Click += (_, _) => OpenWebRequested?.Invoke();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new ContextMenu();
        _menu.Items.Add(open);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(web);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);
        _menu.Closed += MenuClosed;

        var parameters = new HwndSourceParameters("CarpTray")
        {
            Width = 1,
            Height = 1,
            WindowStyle = 0x800000,          // WS_POPUP
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
        };
        _sink = new HwndSource(parameters);
        _sink.AddHook(WndProc);
        Register();
    }

    private void Register()
    {
        _added = false;
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _iconIdle.Handle;
        data.szTip = "Carp 控制面板";
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            AppLog.Error($"[GUI] 托盘图标注册失败，Shell 错误码 {Marshal.GetLastWin32Error()}");
            return;
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
        AppLog.Info("[GUI] 托盘图标已注册");
        Update(_running, _readOnly, string.Empty, _canControl);
    }

    /// <summary>同步状态：图标、提示、菜单文字。</summary>
    public void Update(bool running, bool readOnly, string address, bool canControl = true)
    {
        _running = running;
        _readOnly = readOnly;
        _canControl = canControl;

        _toggleItem.Header = running ? "停止服务" : "启动服务";
        _toggleItem.IsEnabled = !readOnly && canControl;

        if (!_added || _disposed)
        {
            return;
        }
        var state = readOnly ? "只读（外部核心）" : running ? "服务运行中" : "服务已停止";
        var data = CreateData();
        data.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.hIcon = (running ? _iconRunning : _iconIdle).Handle;
        data.szTip = Truncate($"Carp · {state}{(string.IsNullOrEmpty(address) ? string.Empty : " · " + address)}", 127);
        if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
        {
            AppLog.Error($"[GUI] 托盘图标更新失败，Shell 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>第一次收进托盘时提示一次。</summary>
    public void ShowHideHint()
    {
        if (_hintShown || !_added || _disposed)
        {
            return;
        }
        _hintShown = true;
        var data = CreateData();
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = NIIF_INFO;
        data.szInfoTitle = "Carp 仍在后台运行";
        data.szInfo = "网盘服务没有停止。右键托盘图标可以停止服务或退出。";
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            // Explorer 重启后托盘区被清空，需要重新注册图标。
            Register();
            return IntPtr.Zero;
        }
        if (msg != WM_TRAYICON)
        {
            return IntPtr.Zero;
        }
        var notification = (int)(lParam.ToInt64() & 0xFFFF);
        switch (notification)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke();
                handled = true;
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // 先把前台窗口抢过来，否则首次点击只会激活菜单而不会触发菜单项。
        SetForegroundWindow(_sink.Handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        _menu.Focus();
    }

    private void MenuClosed(object? sender, EventArgs e)
    {
        // 菜单关闭后把前台还给系统，否则托盘区会短暂失去响应。
        PostMessage(_sink.Handle, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _sink.Handle,
        uID = TRAY_ICON_ID,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static Icon LoadIcon(string name)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{name}.ico", UriKind.Absolute);
        var stream = Application.GetResourceStream(uri)?.Stream
                     ?? throw new InvalidOperationException($"找不到图标资源：{name}.ico");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _menu.IsOpen = false;
        _sink.RemoveHook(WndProc);
        _sink.Dispose();
        _iconRunning.Dispose();
        _iconIdle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
