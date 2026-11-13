using System.Diagnostics;

namespace Qaqh.Sdk.Discovery;

/// <summary>
/// 拉起 daemon。候选序与 qaqh-client 的 <c>daemon_executable</c> 一致：
/// QAQH_BACKEND_ROOT 的 target/debug → exe 旁 resources/ → exe 旁 →
/// PATH 兜底（仅 QAQH_DAEMON_ALLOW_PATH=1）。候选全空时报错，绝不静默退回裸名。
/// </summary>
public static class DaemonLauncher
{
    private const string Executable = "qaqh-daemon.exe";

    public static string ResolveExecutable()
    {
        var root = Environment.GetEnvironmentVariable("QAQH_BACKEND_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var dev = Path.Combine(root, "target", "debug", Executable);
            if (File.Exists(dev))
            {
                return dev;
            }
        }

        var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (exeDir is not null)
        {
            foreach (var baseDir in new[] { Path.Combine(exeDir, "resources"), exeDir })
            {
                var candidate = Path.Combine(baseDir, Executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var allowPath = Environment.GetEnvironmentVariable("QAQH_DAEMON_ALLOW_PATH");
        if (allowPath is "1" or "true" or "yes")
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in pathDirs)
            {
                var candidate = Path.Combine(dir, Executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw SdkException.Discovery($"QAQH_DAEMON_ALLOW_PATH=1 is set but '{Executable}' was not found on PATH");
        }

        throw SdkException.Discovery(
            "qaqh-daemon executable not found: install it next to the app (or in its resources/ directory), " +
            "set QAQH_BACKEND_ROOT for the dev layout, or set QAQH_DAEMON_ALLOW_PATH=1 to allow a PATH lookup");
    }

    /// <summary>
    /// 以脱离当前进程组与 Job 的方式拉起 <c>qaqh-daemon run</c>
    /// （CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW）。
    /// packaged 壳运行在 kill-on-close 的 Job 里，子进程不加 breakaway 会随壳
    /// 一同被收走并留下陈旧 discovery（BUG-2026-09-15-04 的 Windows 版）。
    /// </summary>
    public static void SpawnDetached(string executable)
    {
        var commandLine = $"\"{executable}\" run";
        var si = new STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW,
                IntPtr.Zero,
                null,
                ref si,
                out var pi))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw SdkException.Discovery($"failed to spawn daemon '{executable}': Win32 error {error}");
        }
        _ = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x0100_0000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x0000_0200;
    private const uint CREATE_NO_WINDOW = 0x0800_0000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        nint processAttributes,
        nint threadAttributes,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// 确保 daemon 在跑并返回其发现记录。已有存活 discovery 直接复用；
    /// lock 持有者存活（冷启动中）只等待不重复 spawn；否则拉起后轮询至超时。
    /// </summary>
    public static async Task<DaemonDiscovery> EnsureRunningAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var existing = DaemonLocator.TryRead();
        if (existing is not null && await DaemonLocator.EndpointReachable(existing).ConfigureAwait(false)
            && DaemonLocator.PidIsRunning(existing.Pid))
        {
            return existing;
        }

        if (!DaemonLocator.LockHolderAlive())
        {
            SpawnDetached(ResolveExecutable());
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovery = DaemonLocator.TryRead();
            if (discovery is not null
                && await DaemonLocator.EndpointReachable(discovery).ConfigureAwait(false)
                && DaemonLocator.PidIsRunning(discovery.Pid))
            {
                return discovery;
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw SdkException.Discovery("daemon did not publish live discovery in time");
            }
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
    }
}
