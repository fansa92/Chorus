using System.Diagnostics;
using Chorus.Core.Interop;

namespace Chorus.Core;

/// <summary>sing-box.exe 核心进程管理：配置校验、启动、停止、日志转发、退出事件。</summary>
public sealed class SingBoxCoreManager : IDisposable
{
    private Process? _process;
    private IntPtr _jobHandle;

    public string CorePath { get; set; } = "";

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? LogReceived;
    public event Action<int?>? Exited;

    public async Task CheckAsync(string configPath, CancellationToken ct = default)
    {
        var (code, output) = await RunCoreAsync($"check -c \"{configPath}\"", ct);
        if (code != 0)
            throw new InvalidOperationException("配置校验失败：\n" + output.Trim());
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var (_, output) = await RunCoreAsync("version", ct);
        return output.Trim();
    }

    public void Start(string configPath, string workDir)
    {
        if (IsRunning) throw new InvalidOperationException("核心已在运行");
        if (!File.Exists(CorePath)) throw new FileNotFoundException("未找到 sing-box.exe", CorePath);

        _jobHandle = JobObjectInterop.CreateKillOnCloseJob();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = CorePath,
            Arguments = $"run -c \"{configPath}\" -D \"{workDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        _process = process;
        try
        {
            JobObjectInterop.AssignToJob(_jobHandle, process.Handle);
        }
        catch
        {
            // 绑定 Job 失败不影响运行，只是失去“随宿主退出”的保底
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var code = SafeExitCode();
            Cleanup();
            Exited?.Invoke(code);
        };
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
        catch (SystemException)
        {
        }
        Cleanup();
    }

    private async Task<(int Code, string Output)> RunCoreAsync(string arguments, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(CorePath) || !File.Exists(CorePath))
            throw new FileNotFoundException("未找到 sing-box.exe", CorePath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CorePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    private int? SafeExitCode()
    {
        try { return _process is { HasExited: true } ? _process.ExitCode : null; }
        catch { return null; }
    }

    private void Cleanup()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            JobObjectInterop.CloseJob(_jobHandle); // 关闭 Job 句柄会连带终止核心进程
            _jobHandle = IntPtr.Zero;
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
