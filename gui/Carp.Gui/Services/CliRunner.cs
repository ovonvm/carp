using System.Diagnostics;
using System.IO;
using System.Text;

namespace Carp.Gui.Services;

/// <summary>命令行执行结果；<see cref="Output"/> 合并了 stdout 与 stderr。</summary>
public readonly record struct CliResult(bool Ok, string Output);

/// <summary>以 UTF-8 运行 carp 命令行子命令（backup / reset-admin-password / doctor / version）。</summary>
public static class CliRunner
{
    public static async Task<CliResult> RunAsync(
        string exe,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutSeconds = 90)
    {
        var output = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };

        // StandardInputEncoding 仅在重定向 stdin 时才允许设置。
        if (stdin != null)
        {
            psi.StandardInputEncoding = new UTF8Encoding(false);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new CliResult(false, "无法启动进程：" + exe);
            }
        }
        catch (Exception ex)
        {
            return new CliResult(false, "无法启动进程：" + ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (stdin != null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
                // stdin 写入失败时继续等待退出码。
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略。
            }
            // 进程已被杀掉；把超时前读到的 stdout/stderr 一并带回去，否则错误
            // 消息里永远是空的。
            var partial = output.ToString();
            try
            {
                if (await Task.WhenAny(stdout, Task.Delay(1500)).ConfigureAwait(false) == stdout)
                {
                    partial += await stdout.ConfigureAwait(false);
                }
            }
            catch
            {
                // 忽略读取失败。
            }
            try
            {
                if (await Task.WhenAny(stderr, Task.Delay(1500)).ConfigureAwait(false) == stderr)
                {
                    partial += await stderr.ConfigureAwait(false);
                }
            }
            catch
            {
                // 忽略读取失败。
            }
            return new CliResult(false, $"命令执行超时（{timeoutSeconds} 秒）。\n{partial}");
        }

        output.Append(await stdout.ConfigureAwait(false));
        var err = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(err))
        {
            output.Append(err);
        }
        var text = output.ToString().Trim();
        return new CliResult(process.ExitCode == 0, text);
    }
}
