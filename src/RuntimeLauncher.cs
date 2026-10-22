using System.Diagnostics;
using System.Text;

namespace WinApkLauncher;

/// <summary>Holds a started runtime process plus everything needed to report on it.</summary>
public sealed class LaunchedProcess
{
    public required Process Process { get; init; }
    public required string LogPath { get; init; }
    public required string CommandLine { get; init; }
    public required IReadOnlyList<(string Key, string Value)> Env { get; init; }
    public required Profile Profile { get; init; }

    /// <summary>The per-app proxy adapter child, when this launch has a proxy; else null.</summary>
    public Process? ProxyAdapter { get; init; }
    public int ProxyPort { get; init; }

    private StreamWriter? _log;

    internal void AttachLog(StreamWriter log) => _log = log;
    internal void CloseLog()
    {
        try { _log?.Flush(); _log?.Dispose(); } catch { }
        _log = null;
    }
}

/// <summary>
/// Turns a <see cref="Profile"/> into a winapk-runtime.exe invocation: the
/// --apk/--data command line plus the env vars set for that child process only
/// (WINAPK_PHONE_SIZE, MACAPK_SDK_INT, MACAPK_JVM_OPTS, MACAPK_LOG_LEVEL,
/// WINAPK_DENSITY, WINAPK_ALLOW_SECOND_INSTANCE, WINAPK_PROXY). stdout+stderr are
/// streamed to a per-run log file in <see cref="AppPaths.LogsDir"/>.
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
        if (socksPort != 0) env.Add(("WINAPK_PROXY", WinApkProxyValue(socksPort)));
        return env;
    }

    public static string BuildCommandLine(string? runtimeExe, Profile p) =>
        $"\"{runtimeExe}\" --apk \"{p.ApkPath}\" --data \"{p.DataRoot}\"";

    /// <summary>Validate a profile before launch; returns an error message or null.</summary>
    public static string? Validate(string? runtimeExe, Profile p)
    {
        if (string.IsNullOrWhiteSpace(runtimeExe))
            return "winapk-runtime.exe was not found.\nUse \"Runtime path…\" to locate it.";
        if (!File.Exists(runtimeExe))
            return $"winapk-runtime.exe not found:\n{runtimeExe}";
        if (string.IsNullOrWhiteSpace(p.ApkPath) || !File.Exists(p.ApkPath))
            return $"APK not found:\n{p.ApkPath}";
        if (string.IsNullOrWhiteSpace(p.DataRoot))
            return "This profile has no data root.";
        return null;
    }

    /// <summary>
    /// Starts the runtime for <paramref name="p"/>. Creates the data root and the
    /// log file, sets the env on the child only, and streams its output to the log.
    /// Throws on a validation failure or if the process cannot start.
    /// </summary>
    public static LaunchedProcess Start(string? runtimeExe, Profile p)
    {
        if (LaunchBlocked) throw new InvalidOperationException("Launching is disabled in this mode (--screenshot).");
        var err = Validate(runtimeExe, p);
        if (err != null || runtimeExe == null) throw new InvalidOperationException(err ?? "no runtime");

        Directory.CreateDirectory(p.DataRoot);
        Directory.CreateDirectory(LogsDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safe = MakeFileSafe(p.Name);
        var logPath = Path.Combine(LogsDir, $"{safe}-{stamp}.log");

        // A proxy profile gets its local adapter first, so we can point the app at its port.
        Process? adapter = null;
        int socksPort = 0;
        if (p.HasProxy)
        {
            try { (adapter, socksPort) = ProxyAdapter.StartChild(p.Id); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start the network proxy adapter for ‘{p.Name}’.\n{ex.Message}\n\n" +
                    "The app was NOT launched (it would otherwise run without the configured proxy).");
            }
        }

        var env = BuildEnv(p, socksPort);
        var psi = new ProcessStartInfo
        {
            FileName = runtimeExe,
            UseShellExecute = false,
            CreateNoWindow = true,          // no console window; the APP opens its own window
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeExe) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--apk");
        psi.ArgumentList.Add(p.ApkPath);
        psi.ArgumentList.Add("--data");
        psi.ArgumentList.Add(p.DataRoot);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        log.WriteLine($"# {AppInfo.ProductName} {AppInfo.Version}");
        log.WriteLine($"# {DateTime.Now:O}");
        log.WriteLine($"# profile: {p.Name}");
        log.WriteLine($"# command: {BuildCommandLine(runtimeExe, p)}");
        log.WriteLine($"# env:");
        foreach (var (k, v) in env) log.WriteLine($"#   {k}={v}");
        if (adapter != null) log.WriteLine($"# proxy: {p.ProxyLabel} via local adapter 127.0.0.1:{socksPort} (pid {adapter.Id})");
        log.WriteLine($"# runtime data/logs: {RuntimeLogDir(p)}");
        log.WriteLine("# ----------------------------------------------------------------");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var launched = new LaunchedProcess
        {
            Process = proc,
            LogPath = logPath,
            CommandLine = BuildCommandLine(runtimeExe, p),
            Env = env,
            Profile = p,
            ProxyAdapter = adapter,
            ProxyPort = socksPort,
        };
        launched.AttachLog(log);

        var gate = new object();
        void Write(string? line)
        {
            if (line == null) return;
            lock (gate) { try { log.WriteLine(line); } catch { } }
        }
        proc.OutputDataReceived += (_, e) => Write(e.Data);
        proc.ErrorDataReceived += (_, e) => Write(e.Data);
        proc.Exited += (_, _) =>
        {
            lock (gate) { try { log.WriteLine($"# ---- exited, code {SafeExitCode(proc)} ----"); } catch { } }
            launched.CloseLog();
        };

        if (!proc.Start())
        {
            try { adapter?.Kill(); } catch { }
            throw new InvalidOperationException("CreateProcess failed (winapk-runtime did not start).");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Hand the runtime PID to the adapter so it can shut down when the app exits.
        if (adapter != null)
        {
            try { adapter.StandardInput.WriteLine(proc.Id); adapter.StandardInput.Flush(); }
            catch { /* adapter self-exits after 60 s even if this fails */ }
        }
        return launched;
    }

    public static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    /// <summary>Last N lines of a log file, for an error dialog.</summary>
    public static string TailLog(string path, int lines = 25)
    {
        try
        {
            var all = File.ReadAllLines(path);
            var take = all.Length <= lines ? all : all[^lines..];
            return string.Join(Environment.NewLine, take);
        }
        catch (Exception ex) { return $"(could not read log: {ex.Message})"; }
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
