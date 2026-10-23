using System.Runtime.InteropServices;

namespace WinApkLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Hidden child process: the per-app network proxy adapter. No GUI, no console, no seeding.
        if (args.Length >= 2 && args[0].Equals("--proxy-adapter", StringComparison.OrdinalIgnoreCase))
            return ProxyAdapter.Run(args[1]);

        // Hidden child process: the startup window of a --launch (see SplashProcess).
        if (args.Length >= 3 && args[0].Equals("--splash", StringComparison.OrdinalIgnoreCase))
            return SplashProcess.Run(args);

        if (args.Length > 0 && !IsGuiStart(args[0]))
            return Cli.Run(args);

        // GUI options: --theme light|dark (override the Windows app theme),
        //              --lang auto|en|he (this run's language instead of the saved setting; not saved),
        //              --screenshot <dir> (save window captures for a UI check, then exit; never launches),
        //              --add <package> or a bare package path (Explorer's "Open with WinApk Launcher"):
        //              open the Add dialog prefilled with that file.
        string? theme = null, shots = null, add = null, lang = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            bool hasValue = i + 1 < args.Length;
            if (a.Equals("--theme", StringComparison.OrdinalIgnoreCase) && hasValue) theme = args[++i];
            else if (a.Equals("--lang", StringComparison.OrdinalIgnoreCase) && hasValue) lang = args[++i];
            else if (a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && hasValue) shots = args[++i];
            else if (a.Equals("--add", StringComparison.OrdinalIgnoreCase) && hasValue) add = args[++i];
            else if (!a.StartsWith('-')) add ??= a;
        }
        if (add != null) { try { add = Path.GetFullPath(add.Trim().Trim('"')); } catch { } }
        return RunGui(theme, shots, add, lang);
    }

    /// <summary>The GUI flags, or a bare file path; everything else is a console command.</summary>
    private static bool IsGuiStart(string first) =>
        first.ToLowerInvariant() is "--theme" or "--lang" or "--screenshot" or "--add" ||
        (!first.StartsWith('-') && first != "/?");

    private static int RunGui(string? theme, string? shots, string? add, string? lang)
    {
        bool primary = false;
        if (shots != null)
        {
            // The screenshot harness must never start the runtime, whatever the UI does.
            RuntimeLauncher.LaunchBlocked = true;
        }
        else
        {
            primary = SingleInstance.TryBecomePrimary();
            // Already open: hand the package over (or just bring that window forward) and exit.
            if (!primary && SingleInstance.SendToPrimary(add)) return 0;
        }

        ApplicationConfiguration.Initialize();
        Theme.Initialize(theme);
        // Screenshot file names say what was forced: tiles-he-dark.png.
        var suffix = shots == null ? "" :
            (lang != null ? "-" + L.NormalizeSetting(lang) : "") + (theme != null ? "-" + theme.ToLowerInvariant() : "");
        var form = new MainForm(shots, suffix, add, lang);
        if (primary) SingleInstance.Listen(form.HandleExternalRequest);
        Application.Run(form);
        return 0;
    }
}

/// <summary>
/// A tiny console surface so the launch code path can be driven without the GUI
/// (handy for scripted smoke tests). The app is a WinExe, so we attach to the
/// parent console first.
/// </summary>
internal static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    private const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;

    /// <summary>False when started without a console, for example by a desktop shortcut.</summary>
    private static bool _hasConsole;

    /// <summary>True when stdout goes to a file or a pipe.</summary>
    private static bool _redirected;

    public static int Run(string[] args)
    {
        // cmd.exe hands "> file" and "| x" to a GUI program by handle inheritance, not with
        // STARTF_USESTDHANDLES, and AttachConsole then swaps such handles for the console's, so the
        // output never reached the file. Keep a redirected stdout/stderr as it is.
        var outFile = Redirected(STD_OUTPUT_HANDLE);
        var errFile = Redirected(STD_ERROR_HANDLE);
        _hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
        if (outFile != IntPtr.Zero)
        {
            SetStdHandle(STD_OUTPUT_HANDLE, outFile);
            Console.SetOut(Writer(Console.OpenStandardOutput()));
        }
        if (errFile != IntPtr.Zero)
        {
            SetStdHandle(STD_ERROR_HANDLE, errFile);
            Console.SetError(Writer(Console.OpenStandardError()));
        }
        _redirected = outFile != IntPtr.Zero;
        var store = LauncherStore.Load();

        switch (args[0].ToLowerInvariant())
        {
            case "--version":
            case "-v":
                Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
                return 0;

            case "--list":
            case "-l":
                Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}  ({AppPaths.ModeLabel})");
                Console.WriteLine($"runtime: {store.DescribeRuntime()}");
                Console.WriteLine($"store:   {LauncherStore.StorePath}");
                foreach (var p in store.Profiles)
                    Console.WriteLine($"  [{p.Name}] apk={Path.GetFileName(p.ApkPath)} " +
                                      $"data={p.DataRoot} size={p.PhoneWidthDp}x{p.PhoneHeightDp} " +
                                      $"sdk={(p.SdkInt > 0 ? p.SdkInt : 29)} multi={p.AllowMultipleInstances}");
                return 0;

            case "--launch":
                if (args.Length < 2) { Console.Error.WriteLine("usage: --launch <name|id> [-s N] [--no-splash]"); return 2; }
                int seconds = -1; // -1 = launch like the button and leave it running
                bool noSplash = false;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] is "--seconds" or "-s" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { seconds = n; i++; }
                    else if (args[i].Equals("--no-splash", StringComparison.OrdinalIgnoreCase)) noSplash = true;
                }
                return Launch(store, args[1], seconds, noSplash || seconds >= 0);

            case "--dry-run":
                if (args.Length < 2) { Console.Error.WriteLine("usage: --dry-run <name|id>"); return 2; }
                return DryRun(store, args[1]);

            case "--test-proxy":
                if (args.Length < 2) { Console.Error.WriteLine("usage: --test-proxy <name|id>"); return 2; }
                return TestProxy(store, args[1]);

            case "--status":
                if (args.Length < 2) { Console.Error.WriteLine("usage: --status <name|id>"); return 2; }
                return Status(store, args[1]);

            case "--self-test":
                return SelfTest();

            case "--icon":
                if (args.Length < 2) { Console.Error.WriteLine("usage: --icon <apk|xapk> [out.png] [--xml]"); return 2; }
                IconExtractor.PreferXml = args.Any(a => a.Equals("--xml", StringComparison.OrdinalIgnoreCase));
                var rest = args.Skip(2).Where(a => !a.StartsWith("--")).ToArray();
                return Icon(args[1], rest.Length > 0 ? rest[0] : null);

            default:
                Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
                Console.WriteLine("  (no args)                 open the GUI");
                Console.WriteLine("  <package>                 open the GUI with the Add dialog prefilled (.apk/.xapk/.apkm/.apks)");
                Console.WriteLine("  --add <package>           the same");
                Console.WriteLine("  --theme light|dark        open the GUI in a forced theme");
                Console.WriteLine("  --lang auto|en|he         open the GUI in that language (not saved)");
                Console.WriteLine("  --screenshot <dir>        open the GUI, save captures of its windows, exit (never launches)");
                Console.WriteLine("  --list                    list profiles");
                Console.WriteLine("  --launch <name>           launch a profile (proxy adapter included) and leave it running,");
                Console.WriteLine("                            with a startup window until the app's window opens;");
                Console.WriteLine("                            if it already runs or is starting: start nothing, exit code 3");
                Console.WriteLine("  --launch <name> --no-splash   the same without the startup window");
                Console.WriteLine("  --launch <name> -s N      launch, watch N seconds, then close it (smoke test)");
                Console.WriteLine("  --dry-run <name>          print the exe, args and env Launch would use (starts nothing)");
                Console.WriteLine("  --test-proxy <name>       test the profile's proxy (same check as the Edit dialog)");
                Console.WriteLine("  --status <name>           is the app running or still starting? (starts nothing)");
                Console.WriteLine("  --self-test               run the launcher's built-in checks");
                Console.WriteLine("  --icon <apk> [out.png]    trace the icon extraction for a package (no cache)");
                Console.WriteLine("  --version                 print the version");
                Console.WriteLine();
                Console.WriteLine($"mode:    {AppPaths.ModeLabel}");
                Console.WriteLine($"store:   {LauncherStore.StorePath}");
                Console.WriteLine($"runtime: {store.DescribeRuntime()}");
                return 0;
        }
    }

    /// <summary>The std handle if it is a file or a pipe, else zero.</summary>
    private static IntPtr Redirected(int which)
    {
        var h = GetStdHandle(which);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return IntPtr.Zero;
        var type = GetFileType(h);
        return type is FILE_TYPE_DISK or FILE_TYPE_PIPE ? h : IntPtr.Zero;
    }

    private static TextWriter Writer(Stream s)
    {
        var enc = Console.OutputEncoding;
        if (enc.CodePage == 65001) enc = new System.Text.UTF8Encoding(false);
        return new StreamWriter(s, enc) { AutoFlush = true };
    }

    private static int Icon(string apk, string? outPng)
    {
        Console.WriteLine($"package: {apk}");
        IconExtractor.Trace = Console.Out;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Bitmap? bmp = null;
        try { bmp = IconExtractor.Extract(apk); }
        catch (Exception ex) { Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}"); }
        finally { IconExtractor.Trace = null; }
        using (bmp)
        {
            if (bmp == null)
            {
                Console.WriteLine($"RESULT: no icon ({sw.ElapsedMilliseconds} ms) -> the GUI shows a letter tile");
                return 1;
            }
            Console.WriteLine($"RESULT: {bmp.Width}x{bmp.Height} icon in {sw.ElapsedMilliseconds} ms");
            if (outPng != null) { bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png); Console.WriteLine($"saved: {outPng}"); }
        }
        return 0;
    }

    private static int Launch(LauncherStore store, string nameOrId, int seconds, bool noSplash)
    {
        var p = store.Find(nameOrId);
        if (p == null) { Console.Error.WriteLine($"no such profile: {nameOrId}"); return 2; }

        var runtime = store.ResolveRuntime();
        var err = RuntimeLauncher.Validate(runtime, p);
        if (err != null) { Console.Error.WriteLine(err); return 2; }

        // Never a second copy: a second runtime on the same app folder only exits with code 3.
        var probe = RuntimeInstance.Probe(p, runtime);
        bool another = p.AllowMultipleInstances && probe.State == InstanceState.Running;
        if (probe.State != InstanceState.NotRunning && !another)
        {
            var pids = string.Join(", ", probe.Processes.Select(x => x.Pid));
            if (probe.State == InstanceState.Running)
            {
                RuntimeInstance.BringToFront(probe.Window);
                Console.WriteLine($"RESULT: {p.Name} is already running (pid {pids}); brought its window to the front. Nothing was started.");
            }
            else
            {
                const string why = "The first launch prepares the app and can take several minutes for large apps.";
                // Its startup window, or a new one that follows it; the plain notice only when the
                // runtime process cannot be found (just its lock).
                string how = "";
                bool notice = false;
                if (SplashForm.TryBringToFront(SplashForm.KeyFor(p, runtime))) how = " Brought its startup window to the front.";
                else if (!noSplash && probe.Processes.Count > 0 && SplashProcess.SpawnAttached(p, probe.Processes[0].Pid))
                    how = " Opened a startup window for it.";
                else notice = !_hasConsole && !_redirected;
                Console.WriteLine($"RESULT: {p.Name} is still starting{(pids.Length > 0 ? $" (pid {pids})" : "")}. {why}{how} Nothing was started.");
                if (notice) ShowNotice(store, p.Name);
            }
            return RuntimeInstance.ExitAlreadyRunning;
        }

        Console.WriteLine($"command: {RuntimeLauncher.BuildCommandLine(runtime, p)}");
        if (p.HasProxy) Console.WriteLine($"proxy:   {p.ProxyLabel}{(p.ResolveProxy()!.HasAuth ? " (auth)" : "")} via a local SOCKS5 adapter");

        var lp = RuntimeLauncher.Start(runtime, p);
        Console.WriteLine($"started pid {lp.Process.Id}");
        if (lp.ProxyAdapter != null) Console.WriteLine($"proxy adapter: pid {lp.ProxyAdapter.Id} on 127.0.0.1:{lp.ProxyPort}");
        Console.WriteLine("env:");
        foreach (var (k, v) in lp.Env) Console.WriteLine($"  {k}={v}");
        Console.WriteLine($"log: {lp.LogPath}");
        if (Directory.Exists(RuntimeLauncher.RuntimeLogDir(p)))
            Console.WriteLine($"runtime data/logs: {RuntimeLauncher.RuntimeLogDir(p)}");

        if (seconds < 0)
        {
            // The startup window runs in its own detached process, so this one returns at once.
            bool splash = !noSplash && SplashProcess.Spawn(p, lp.Process.SafeHandle, lp.LogPath);
            Console.WriteLine(splash ? "RESULT: launched; leaving it running (a startup window shows its progress)."
                                     : "RESULT: launched; leaving it running.");
            return 0;
        }

        // Watch for an early crash and for the app window appearing.
        bool windowSeen = false;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (lp.Process.HasExited) break;
            try
            {
                lp.Process.Refresh();
                if (!windowSeen && lp.Process.MainWindowHandle != IntPtr.Zero)
                {
                    windowSeen = true;
                    Console.WriteLine($"window opened: handle=0x{lp.Process.MainWindowHandle.ToInt64():X} " +
                                      $"title=\"{lp.Process.MainWindowTitle}\"");
                }
            }
            catch { }
            Thread.Sleep(250);
        }

        if (lp.Process.HasExited)
        {
            var code = RuntimeLauncher.SafeExitCode(lp.Process);
            Console.WriteLine($"process exited early, code {code} -> " + (code == 0 ? "clean" : "CRASH"));
            Console.WriteLine("--- log tail ---");
            Console.WriteLine(RuntimeLauncher.TailLog(lp.LogPath));
            return code == 0 ? 0 : 1;
        }

        Console.WriteLine(windowSeen
            ? "RESULT: window is open and the process is alive."
            : "RESULT: process alive but no top-level window detected yet.");
        Console.WriteLine("closing it now...");
        try { if (!lp.Process.CloseMainWindow()) { } } catch { }
        try { if (!lp.Process.WaitForExit(3000)) lp.Process.Kill(entireProcessTree: true); } catch { }
        lp.CloseLog();
        Console.WriteLine("closed.");
        return windowSeen ? 0 : 1;
    }

    /// <summary>Print the exact command, args and env Launch would use, with the proxy password masked. Starts nothing.</summary>
    private static int DryRun(LauncherStore store, string nameOrId)
    {
        var p = store.Find(nameOrId);
        if (p == null) { Console.Error.WriteLine($"no such profile: {nameOrId}"); return 2; }

        var runtime = store.ResolveRuntime();
        Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}  ({AppPaths.ModeLabel})");
        Console.WriteLine($"store:   {LauncherStore.StorePath}");
        Console.WriteLine($"runtime: {store.DescribeRuntime()}");
        Console.WriteLine($"profile: {p.Name}  [{p.Id}]");
        Console.WriteLine($"exe:  {runtime ?? "(none)"}");
        Console.WriteLine($"args: --apk \"{p.ApkPath}\" --data \"{p.DataRoot}\"");
        if (p.HasProxy)
        {
            var cfg = p.ResolveProxy()!;
            var masked = string.IsNullOrEmpty(cfg.Password) ? "" : " pass=" + new string('*', Math.Max(3, cfg.Password.Length));
            var user = cfg.HasAuth ? " user=" + cfg.User : "";
            Console.WriteLine($"proxy: {p.ProxyLabel}{user}{masked}  (local SOCKS5 adapter -> this upstream)");
        }
        else Console.WriteLine("proxy: none");
        Console.WriteLine("env:");
        // socksPort = -1 shows the placeholder flags the adapter would fill in at launch.
        foreach (var (k, v) in RuntimeLauncher.BuildEnv(p, p.HasProxy ? -1 : 0))
            Console.WriteLine($"  {k}={v}");
        var val = RuntimeLauncher.Validate(runtime, p);
        Console.WriteLine(val == null ? "validation: OK (would launch)" : "validation: " + val.Replace("\n", " "));
        return 0;
    }

    /// <summary>The "still starting" notice for a launch without a console (a desktop shortcut), in the GUI's language.</summary>
    private static void ShowNotice(LauncherStore store, string name)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Theme.Initialize(null);
            L.Apply(store.Language);
            using var dlg = new LauncherDialog(AppInfo.ProductName, L.StillStartingHeading(name), L.StillStartingBody,
                new[] { new DialogButton("ok", L.Ok, Accent: true) })
            {
                TopMost = true,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
            };
            Application.Run(dlg);
        }
        catch { }
    }

    /// <summary>Whether the profile's app runs or is starting, by the runtime's lock and processes. Starts nothing.</summary>
    private static int Status(LauncherStore store, string nameOrId)
    {
        var p = store.Find(nameOrId);
        if (p == null) { Console.Error.WriteLine($"no such profile: {nameOrId}"); return 2; }
        var runtime = store.ResolveRuntime();
        var appDir = RuntimeInstance.AppDir(p.DataRoot, p.ApkPath);
        var probe = RuntimeInstance.Probe(p, runtime);
        Console.WriteLine($"profile: {p.Name}  [{p.Id}]");
        Console.WriteLine($"app folder: {appDir}");
        Console.WriteLine($"lock: {probe.MutexName} ({(p.AllowMultipleInstances ? "not used: multiple instances allowed" : probe.LockHeld ? "held" : "free")})");
        foreach (var x in probe.Processes)
            Console.WriteLine($"  pid {x.Pid}: " + (x.Window != IntPtr.Zero ? $"window \"{x.WindowTitle}\"" : "no window yet"));
        Console.WriteLine("state: " + probe.State switch
        {
            InstanceState.Running => "running",
            InstanceState.Starting => "starting (no window yet)",
            _ => "not running",
        });
        return 0;
    }

    /// <summary>The built-in checks: the runtime lock name mirror, the startup log parser and the string table.</summary>
    private static int SelfTest()
    {
        var failures = RuntimeInstance.SelfTest();
        failures.AddRange(StartupWatch.SelfTest());
        failures.AddRange(L.SelfTest());
        foreach (var f in failures) Console.WriteLine("FAIL: " + f);
        Console.WriteLine(failures.Count == 0 ? "self-test: OK" : $"self-test: {failures.Count} failure(s)");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Run the Edit dialog's proxy test from the console.</summary>
    private static int TestProxy(LauncherStore store, string nameOrId)
    {
        var p = store.Find(nameOrId);
        if (p == null) { Console.Error.WriteLine($"no such profile: {nameOrId}"); return 2; }
        var cfg = p.ResolveProxy();
        if (cfg == null) { Console.Error.WriteLine("profile has no proxy configured"); return 2; }
        Console.WriteLine($"testing {p.ProxyLabel}{(cfg.HasAuth ? " (auth)" : "")} ...");
        var r = ProxyUpstream.TestAsync(cfg).GetAwaiter().GetResult();
        Console.WriteLine((r.Ok ? "OK: " : "FAIL: ") + r.Message);
        return r.Ok ? 0 : 1;
    }
}
