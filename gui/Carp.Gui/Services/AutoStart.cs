using System;
using System.IO;
using Microsoft.Win32;

namespace Carp.Gui.Services;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CarpGui";

    /// <summary>
    /// 只有 Run 键里的路径就是当前 exe 才算已启用：exe 被移动或改名后，
    /// 旧值会让登录启动一个不存在的程序，那种情况按未启用处理。
    /// </summary>
    public static bool IsEnabled(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return RegisteredPathMatches(key?.GetValue(ValueName) as string, exePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool RegisteredPathMatches(string? raw, string exePath)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        var value = raw.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
            {
                return false;
            }
            value = value[1..end];
        }
        else
        {
            var space = value.IndexOf(' ');
            if (space > 0)
            {
                value = value[..space];
            }
        }
        try
        {
            return string.Equals(Path.GetFullPath(value), Path.GetFullPath(exePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null)
            {
                return;
            }
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 忽略：某些策略禁止写 HKCU Run，此时仅本次会话生效。
        }
    }
}
