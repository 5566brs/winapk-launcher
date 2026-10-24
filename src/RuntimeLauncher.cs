using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WinApkLauncher;

/// <summary>Holds a started runtime process plus everything needed to report on it.</summary>
public sealed class LaunchedProcess
{
    public Process Process { get; internal set; } = null!;
    public required string LogPath { get; init; }
    public required string CommandLine { get; init; }
    public required IReadOnlyList<(string Key, string Value)> Env { get; init; }
    public required Profile Profile { get; init; }

    /// <summary>The per-app proxy adapter child, when this launch has a proxy; else null.</summary>
    public Process? ProxyAdapter { get; internal set; }
    public int ProxyPort { get; internal set; }

    /// <summary>The --log-keeper process that caps the run log; null when it could not start (a plain log file).</summary>
    public Process? LogKeeper { get; internal set; }

    // The launcher's own handle to the run log (the log keeper's pipe, or the file itself), kept to
    // add the exit line. The runtime has its own handle, so the log does not depend on this one (or
    // on the launcher).
    private SafeFileHandle? _log;
    private readonly object _gate = new();
    private bool _exited;
    private Action? _onExit;

    internal void AttachLog(SafeFileHandle log) => _log = log;

    internal void CloseLog()
    {
        lock (_gate)
        {
            try { _log?.Dispose(); } catch { }
            _log = null;
        }
    }

    /// <summary>Runs <paramref name="onExit"/> once the runtime has exited, at once if it already has.
    /// Called on a thread-pool thread.</summary>
    public void WhenExited(Action onExit)
    {
        bool now;
        lock (_gate)
        {
            now = _exited;
            if (!now) _onExit += onExit;
        }
        if (now) onExit();
    }

    internal void OnExited(Process proc)
    {
        Action? listeners;
        lock (_gate)
        {
            if (_exited) return;
            _exited = true;
            listeners = _onExit;
            _onExit = null;
            if (_log != null)
            {
                NativeProcess.Append(_log, $"# ---- exited, code {RuntimeLauncher.SafeExitCode(proc)} ----{Environment.NewLine}");
                try { _log.Dispose(); } catch { }
                _log = null;
            }
        }
        listeners?.Invoke();
    }
}

/// <summary>
/// Turns a <see cref="Profile"/> into a winapk-runtime.exe invocation: the
/// --apk/--data command line plus the env vars set for that child process only
/// (WINAPK_PHONE_SIZE, MACAPK_SDK_INT, MACAPK_JVM_OPTS, MACAPK_LOG_LEVEL, MACAPK_QUIET_CLASSLOAD,
/// WINAPK_DENSITY, WINAPK_ALLOW_SECOND_INSTANCE, WINAPK_PROXY). The runtime's stdout and
/// stderr go to a per-run log file in <see cref="AppPaths.LogsDir"/> through a hidden
/// <see cref="LogKeeper"/> process that caps its size (or, if that cannot start, straight to the
/// file), so the log keeps growing after the launcher exits.
/// </summary>
public static class RuntimeLauncher
{
    public static string LogsDir => AppPaths.LogsDir;

    /// <summary>
    /// Set by --screenshot: nothing may start the runtime or a proxy adapter in this process.
    /// <see cref="Start"/> refuses to run while it is set, whatever path the UI takes.
    /// </summary>
    public static bool LaunchBlocked { get; set; }

    /// <summary>The runtime's own crash/error files (hs_err, JVM ErrorFile) live here.</summary>
    public static string RuntimeLogDir(Profile p) => Path.Combine(p.DataRoot, "logs");

    /// <summary>The socks-proxy JVM flags appended for the adapter port; blank placeholder for --dry-run.</summary>
    public static string SocksJvmFlags(int socksPort) =>
        $"-DsocksProxyHost=127.0.0.1 -DsocksProxyPort={PortText(socksPort)}";

    /// <summary>
    /// WINAPK_PROXY for the adapter port. The runtime's built-in push client (Google push over MCS)
    /// reads it and deliberately ignores the JVM-wide socks properties, so both are set.
    /// </summary>
    public static string WinApkProxyValue(int socksPort) => $"socks5://127.0.0.1:{PortText(socksPort)}";

    private static string PortText(int socksPort) => socksPort > 0 ? socksPort.ToString() : "<assigned at launch>";

    /// <summary>
    /// The MACAPK_JVM_OPTS value for a profile: its own JvmOpts, plus the SOCKS flags when a proxy
    /// adapter is in use. <paramref name="socksPort"/> is the adapter's live port, or 0 (no proxy),
    /// or -1 to show a placeholder for --dry-run.
    /// </summary>
    public static string EffectiveJvmOpts(Profile p, int socksPort)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.JvmOpts)) parts.Add(p.JvmOpts.Trim());
        if (socksPort != 0) parts.Add(SocksJvmFlags(socksPort));
        return string.Join(' ', parts);
    }

    public static List<(string Key, string Value)> BuildEnv(Profile p, int socksPort = 0)
    {
        var env = new List<(string, string)>
        {
            // Always set: we always have a concrete phone size.
            ("WINAPK_PHONE_SIZE", $"{p.PhoneWidthDp}x{p.PhoneHeightDp}"),
        };
        if (p.SdkInt > 0) env.Add(("MACAPK_SDK_INT", p.SdkInt.ToString()));
        if (p.DensityDpi > 0) env.Add(("WINAPK_DENSITY", p.DensityDpi.ToString()));
        var jvm = EffectiveJvmOpts(p, socksPort);
        if (!string.IsNullOrWhiteSpace(jvm)) env.Add(("MACAPK_JVM_OPTS", jvm));
        if (p.LogLevel > 0) env.Add(("MACAPK_LOG_LEVEL", p.LogLevel.ToString()));
        if (p.AllowMultipleInstances) env.Add(("WINAPK_ALLOW_SECOND_INSTANCE", "1"));
        if (!p.DetailedClassLoadLog) env.Add(("MACAPK_QUIET_CLASSLOAD", "1"));
        if (socksPort != 0) env.Add(("WINAPK_PROXY", WinApkProxyValue(socksPort)));
        return env;
    }

    public static string BuildCommandLine(string? runtimeExe, Profile p) =>
        $"\"{runtimeExe}\" --apk \"{p.ApkPath}\" --data \"{p.DataRoot}\"";

    /// <summary>Validate a profile before launch; returns an error message or null.</summary>
    public static string? Validate(string? runtimeExe, Profile p)
    {
        if (string.IsNullOrWhiteSpace(runtimeExe))
            return L.ValidateNoRuntime;
        if (!File.Exists(runtimeExe))
            return L.ValidateRuntimeMissing(runtimeExe);
        if (string.IsNullOrWhiteSpace(p.ApkPath) || !File.Exists(p.ApkPath))
            return L.ValidateApkMissing(p.ApkPath);
        if (string.IsNullOrWhiteSpace(p.DataRoot))
            return L.ValidateNoDataRoot;
        return null;
    }

    /// <summary>
    /// Starts the runtime for <paramref name="p"/>. Creates the data root and the run log, starts the
    /// log keeper, writes the launcher's header lines, then starts the runtime with the keeper's pipe
    /// as its stdout and stderr (or the log file itself, if the keeper could not start) and NUL as its
    /// stdin, inheriting nothing else. The env is set on the child only. Throws on a validation
    /// failure or if the process cannot start.
    /// </summary>
    public static LaunchedProcess Start(string? runtimeExe, Profile p, LogLimits? limits = null)
    {
        limits ??= LogLimits.Default;
        if (LaunchBlocked) throw new InvalidOperationException(L.LaunchBlocked);
        var err = Validate(runtimeExe, p);
        if (err != null || runtimeExe == null) throw new InvalidOperationException(err ?? "no runtime");

        Directory.CreateDirectory(p.DataRoot);
        Directory.CreateDirectory(LogsDir);
        LogRetention.BeforeLaunch(p, limits); // the newest KeepRunLogs of this app stay, counting the new one
        var logPath = NewLogPath(p);

        // A proxy profile gets its local adapter first, so we can point the app at its port.
        AdapterChild? adapter = null;
        int socksPort = 0;
        if (p.HasProxy)
        {
            try { adapter = ProxyAdapter.StartChild(p.Id); socksPort = adapter.Port; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(L.AdapterFailed(p.Name, ex.Message));
            }
        }

        var env = BuildEnv(p, socksPort);
        var commandLine = BuildCommandLine(runtimeExe, p);
        SafeFileHandle? log = null;
        Process? keeper = null;
        try
        {
            // The log keeper caps the log; without it, the runtime writes the file directly.
            try { (keeper, log) = LogKeeper.StartChild(logPath, limits.MaxRunLogBytes); }
            catch { keeper = null; log = NativeProcess.OpenAppend(logPath); }
            var header = new StringBuilder();
            void H(string line) => header.Append("# ").Append(line).Append(Environment.NewLine);
            H($"{AppInfo.ProductName} {AppInfo.Version}");
            H($"{DateTime.Now:O}");
            H($"profile: {p.Name}");
            H($"command: {commandLine}");
            H("env:");
            foreach (var (k, v) in env) H($"  {k}={v}");
            if (adapter != null) H($"proxy: {p.ProxyLabel} via local adapter 127.0.0.1:{socksPort} (pid {adapter.Process.Id})");
            H(keeper != null ? $"log: kept under {limits.MaxRunLogMB} MB by the log keeper (pid {keeper.Id})"
                             : "log: not capped (the log keeper did not start)");
            // Only once the runtime has made it: on a first launch it does not exist yet.
            if (Directory.Exists(RuntimeLogDir(p))) H($"runtime data/logs: {RuntimeLogDir(p)}");
            H("----------------------------------------------------------------");
            NativeProcess.Append(log, header.ToString());

            var launched = new LaunchedProcess
            {
                LogPath = logPath,
                CommandLine = commandLine,
                Env = env,
                Profile = p,
                ProxyAdapter = adapter?.Process,
                ProxyPort = socksPort,
                LogKeeper = keeper,
            };
            launched.AttachLog(log);

            using var nul = NativeProcess.OpenNul();
            launched.Process = NativeProcess.Start(runtimeExe, new[] { "--apk", p.ApkPath, "--data", p.DataRoot }, env,
                Path.GetDirectoryName(runtimeExe) ?? AppContext.BaseDirectory, stdin: nul, stdout: log, stderr: log,
                beforeResume: proc =>
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, _) => launched.OnExited((Process)s!);
                });

            // Hand the runtime PID to the adapter so it can shut down when the app exits.
            if (adapter != null)
            {
                try { adapter.SendRuntimePid(launched.Process.Id); }
                catch { /* the adapter exits by itself after 60 s without a PID */ }
            }
            return launched;
        }
        catch (Exception ex)
        {
            try { log?.Dispose(); } catch { }
            try { if (adapter != null && !adapter.Process.HasExited) adapter.Process.Kill(); } catch { }
            if (ex is InvalidOperationException) throw;
            throw new InvalidOperationException(L.RuntimeDidNotStart(ex.Message), ex);
        }
        finally
        {
            adapter?.Dispose(); // closes our ends of the handshake pipes; the adapter keeps running
        }
    }

    /// <summary>logs\&lt;profile&gt;-&lt;yyyyMMdd-HHmmss&gt;.log, with -2, -3... if that name is taken.</summary>
    private static string NewLogPath(Profile p)
    {
        var stem = Path.Combine(LogsDir, $"{MakeFileSafe(p.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var path = stem + ".log";
        for (int n = 2; ; n++)
        {
            // Created at once, so a second launch in the same second picks the next name.
            try { using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } return path; }
            catch (IOException) when (File.Exists(path)) { path = $"{stem}-{n}.log"; }
        }
    }

    /// <summary>The newest run log of a profile in <see cref="LogsDir"/> (by name), or null.</summary>
    public static string? LatestLog(Profile p)
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return null;
            var safe = MakeFileSafe(p.Name);
            var pattern = new Regex("^" + Regex.Escape(safe) + @"-\d{8}-\d{6}(-\d+)?\.log$", RegexOptions.IgnoreCase);
            return new DirectoryInfo(LogsDir).EnumerateFiles(safe + "-*.log")
                .Where(f => pattern.IsMatch(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    public static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    /// <summary>Last N lines of a log file, for an error dialog. Reads while the app may still write.</summary>
    public static string TailLog(string path, int lines = 25)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            var take = all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
            return string.Join(Environment.NewLine, take);
        }
        catch (Exception ex) { return L.CouldNotReadLog(ex.Message); }
    }

    public static string MakeFileSafe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var r = sb.ToString().Trim().Trim('.');
        return string.IsNullOrEmpty(r) ? "profile" : r;
    }
}
