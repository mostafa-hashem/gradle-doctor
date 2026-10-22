using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace GradleDoctor;

public record LockingProcess(int ProcessId, string ProcessName, string Description);

/// <summary>
/// Service to identify and terminate processes locking files or directories,
/// and to kill orphaned Gradle and Kotlin daemons on Windows.
/// </summary>
public static class FileLockService
{
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;

    enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        [In] string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        out uint lpdwRebootReasons);

    const int ERROR_MORE_DATA = 234;

    /// <summary>
    /// Uses Windows Restart Manager to find any processes holding open handles to the given paths.
    /// </summary>
    public static List<LockingProcess> FindLockingProcesses(params string[] paths)
    {
        var result = new List<LockingProcess>();
        if (paths == null || paths.Length == 0) return result;

        var validPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (validPaths.Length == 0) return result;

        int res = RmStartSession(out var sessionHandle, 0, Guid.NewGuid().ToString("N"));
        if (res != 0) return result;

        try
        {
            res = RmRegisterResources(sessionHandle, (uint)validPaths.Length, validPaths, 0, null, 0, null);
            if (res != 0) return result;

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;

            res = RmGetList(sessionHandle, out needed, ref count, null, out reasons);
            if (res == ERROR_MORE_DATA || needed > 0)
            {
                var processInfo = new RM_PROCESS_INFO[needed];
                count = needed;
                res = RmGetList(sessionHandle, out needed, ref count, processInfo, out reasons);
                if (res == 0)
                {
                    int currentPid = Environment.ProcessId;
                    for (int i = 0; i < count; i++)
                    {
                        int pid = processInfo[i].Process.dwProcessId;
                        if (pid <= 4 || pid == currentPid) continue;

                        string name = processInfo[i].strAppName;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            try { name = Process.GetProcessById(pid).ProcessName; }
                            catch { name = $"PID {pid}"; }
                        }

                        result.Add(new LockingProcess(pid, name, $"Locked resource: {string.Join(", ", validPaths)}"));
                    }
                }
            }
        }
        catch
        {
            // Ignore Restart Manager errors gracefully
        }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return result;
    }

    /// <summary>
    /// Safely terminates a process by PID, preventing suicide or killing system-critical processes.
    /// </summary>
    public static bool KillProcess(int pid, Action<string>? log = null)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            if (IsProtectedProcess(name))
            {
                log?.Invoke($"Skipped terminating protected system process: {name} (PID {pid})");
                return false;
            }

            log?.Invoke($"Terminating locking process: {name} (PID {pid})...");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(1500);
            return true;
        }
        catch (ArgumentException)
        {
            // Process already exited
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not terminate process PID {pid}: {ex.Message}");
            return false;
        }
    }

    static bool IsProtectedProcess(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is "csrss" or "lsass" or "smss" or "wininit" or "services" or "svchost" or "explorer" or "system";
    }

    /// <summary>
    /// Finds and terminates orphaned Gradle and Kotlin compile daemons running in background.
    /// </summary>
    public static int KillGradleAndKotlinDaemons(Action<string>? log = null)
    {
        int killed = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'java.exe' OR Name = 'javaw.exe'");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid <= 4 || pid == Environment.ProcessId) continue;

                    string cmd = obj["CommandLine"]?.ToString() ?? "";
                    bool isDaemon = cmd.Contains("GradleDaemon", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains("org.gradle.launcher", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains("org.jetbrains.kotlin.daemon", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains("kotlin-compiler", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains(".gradle", StringComparison.OrdinalIgnoreCase);

                    if (isDaemon)
                    {
                        log?.Invoke($"Terminating background daemon (PID {pid})...");
                        if (KillProcess(pid, log)) killed++;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not scan for Gradle/Kotlin daemons: {ex.Message}");
        }
        return killed;
    }

    /// <summary>
    /// Unlocks a path by terminating any locking processes found by Restart Manager
    /// and stopping Gradle/Kotlin daemons if the path is inside .gradle.
    /// </summary>
    public static void UnlockPath(string path, Action<string>? log = null)
    {
        try
        {
            var lockers = FindLockingProcesses(path);
            foreach (var locker in lockers)
            {
                log?.Invoke($"Closing locking application: {locker.ProcessName} (PID {locker.ProcessId})");
                KillProcess(locker.ProcessId, log);
            }

            if (path.Contains(".gradle", StringComparison.OrdinalIgnoreCase))
            {
                KillGradleAndKotlinDaemons(log);
            }
        }
        catch { }
    }
}
