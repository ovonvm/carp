using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Carp.Gui.Services;

/// <summary>占用某端口的进程信息。</summary>
public sealed record ProcessInfo(int Pid, string Name, string Path)
{
    public string Summary => $"进程：{Name}\r\nPID：{Pid}\r\n路径：{(string.IsNullOrWhiteSpace(Path) ? "（无权限读取）" : Path)}";
}

/// <summary>Win32 端口 / 进程探测与小工具，全部 P/Invoke + 系统自带命令，不引入新依赖。</summary>
public static class SystemProbe
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>端口上是否已有监听（无论监听者是哪个进程）。</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>找到监听指定端口的进程 PID，找不到返回 0。</summary>
    public static int FindListenerPid(int port)
    {
        var pid = FindListenerPidByTable(port);
        return pid > 0 ? pid : FindListenerPidByNetstat(port);
    }

    private static int FindListenerPidByTable(int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            return 0;
        }
        if (size <= 0)
        {
            return 0;
        }
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return 0;
            }
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                var rawPort = (ushort)(row.LocalPort & 0xFFFF);
                var localPort = (ushort)((rawPort >> 8) | ((rawPort & 0xFF) << 8));
                if (localPort == port && row.OwningPid != 0)
                {
                    return (int)row.OwningPid;
                }
            }
        }
        catch
        {
            // 忽略，交给 netstat 回退。
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return 0;
    }

    private static int FindListenerPidByNetstat(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return 0;
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var local = parts[1];
                var colon = local.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(local[(colon + 1)..], out var localPort) || localPort != port)
                {
                    continue;
                }
                if (int.TryParse(parts[4], out var pid) && pid > 0)
                {
                    return pid;
                }
            }
        }
        catch
        {
            // 忽略。
        }
        return 0;
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取进程名与可执行文件路径（无权限时路径为空）。</summary>
    public static ProcessInfo? Describe(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            var path = "";
            try
            {
                path = proc.MainModule?.FileName ?? "";
            }
            catch
            {
                // 访问被拒绝时拿不到路径。
            }
            return new ProcessInfo(pid, name, path);
        }
        catch
        {
            return null;
        }
    }

    public static ProcessInfo? DescribePortOwner(int port)
    {
        var pid = FindListenerPid(port);
        return pid > 0 ? Describe(pid) : null;
    }

    public static bool LooksLikeCarp(ProcessInfo? info)
    {
        if (info == null)
        {
            return false;
        }
        var name = info.Name ?? "";
        var path = info.Path ?? "";
        return name.Contains("carp", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("carp.exe", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("carp-core.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>结束进程；返回是否成功以及失败原因。</summary>
    public static (bool Ok, string Message) Kill(int pid)
    {
        if (pid <= 0)
        {
            return (false, "无效的进程号。");
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            if (!proc.WaitForExit(5000))
            {
                return (false, "已发送结束请求，但进程未在 5 秒内退出。");
            }
            return (true, "");
        }
        catch (ArgumentException)
        {
            return (true, ""); // 进程已退出。
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return (false, "权限不足，请以管理员身份运行 Carp 后再试。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>确认目录可写（必要时创建）。</summary>
    public static (bool Ok, string Error) EnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".carp-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>等待端口释放（最多 timeout），返回是否已释放。</summary>
    public static async Task<bool> WaitPortFreeAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortInUse(port))
            {
                return true;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return !IsPortInUse(port);
    }
}
