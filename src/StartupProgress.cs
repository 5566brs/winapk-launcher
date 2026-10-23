using System.Text;
using System.Text.RegularExpressions;

namespace WinApkLauncher;

/// <summary>The steps a launch goes through before the app's window opens.</summary>
public enum StartupPhase
{
    /// <summary>Started, or the app's code is ready and the app is booting.</summary>
    Starting,
    /// <summary>"[win.apk] installing the app (once per install)...": the first launch of a package.</summary>
    Installing,
    /// <summary>"[win.apk] converting the app's code (once per install)...": DEX to Java classes.</summary>
    Converting,
}

/// <summary>
/// Follows one launch from Start until its first window: reads the lines the runtime appends to the
/// run log the launcher owns and keeps the current <see cref="StartupPhase"/>. Only three runtime
/// lines are recognized: "[win.apk] installing ...", "[win.apk] converting ..." and the converter's
/// "[macapk-dex] N classes -> ... in X ms". Not thread-safe; the GUI polls it on its timer.
/// </summary>
public sealed class StartupWatch
{
    private static readonly Regex InstallingLine = new(@"^\[win\.apk\] installing\b(?!.*\bfailed\b)", RegexOptions.CultureInvariant);
    private static readonly Regex ConvertingLine = new(@"^\[win\.apk\] converting\b", RegexOptions.CultureInvariant);
    private static readonly Regex ConvertedLine = new(@"^\[macapk-dex\] (\d+) classes\b.*\bin (\d+) ms", RegexOptions.CultureInvariant);

    private const int MaxReadPerPoll = 1 << 20;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _partial = new();
    private long _offset;

    /// <summary>Follows a launch of this window.</summary>
    public StartupWatch(LaunchedProcess launch) : this(launch.Profile, launch.LogPath, launch) { }

    /// <summary>Follows any runtime of <paramref name="profile"/>, through its run log if there is one.</summary>
    public StartupWatch(Profile profile, string? logPath, LaunchedProcess? launch = null)
    {
        Profile = profile;
        LogPath = logPath;
        Launch = launch;
        StartedUtc = launch != null ? DateTime.UtcNow : StartTimeOf(logPath) ?? DateTime.UtcNow;
    }

    /// <summary>The launch this window started; null for a runtime started elsewhere.</summary>
    public LaunchedProcess? Launch { get; }
    public Profile Profile { get; }
    public string? LogPath { get; }
    public DateTime StartedUtc { get; }

    /// <summary>When a run log was created: the launch time of a runtime started elsewhere.</summary>
    private static DateTime? StartTimeOf(string? logPath)
    {
        try { return logPath != null && File.Exists(logPath) ? File.GetCreationTimeUtc(logPath) : null; }
        catch { return null; }
    }
    public StartupPhase Phase { get; private set; } = StartupPhase.Starting;

    /// <summary>"1,234 classes in 3.0 s" once the code conversion has finished in this launch.</summary>
    public string? ConvertedSummary { get; private set; }

    /// <summary>The step as the status bar shows it.</summary>
    public string PhaseText => PhaseLabel(Phase);

    /// <summary>The step as the startup window shows it.</summary>
    public string SplashPhaseText => Phase switch
    {
        StartupPhase.Installing => "Installing the app…",
        StartupPhase.Converting => "Converting the app's code — first launch only, large apps take a few minutes…",
        _ => ConvertedSummary != null ? $"Starting… (code converted: {ConvertedSummary})" : "Starting…",
    };

    public static string PhaseLabel(StartupPhase phase) => phase switch
    {
        StartupPhase.Installing => "Installing the app…",
        StartupPhase.Converting => "Converting the app's code (first launch only)…",
        _ => "Starting…",
    };

    /// <summary>Time since Launch, as m:ss.</summary>
    public string ElapsedText
    {
        get
        {
            var t = DateTime.UtcNow - StartedUtc;
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
    }

    /// <summary>One line for the status bar: name, step, conversion result, elapsed time.</summary>
    public string StatusLine =>
        $"‘{Profile.Name}’: {PhaseText}" +
        (Phase == StartupPhase.Starting && ConvertedSummary != null ? $" (code converted: {ConvertedSummary})" : "") +
        $"  •  {ElapsedText}";

    /// <summary>Reads what the runtime appended since the last call. Returns true when the phase changed.</summary>
    public bool Poll()
    {
        if (LogPath == null) return false;
        var before = (Phase, ConvertedSummary);
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (fs.Length < _offset) { _offset = 0; _partial.Clear(); } // replaced: start over
            if (fs.Length == _offset) return false;
            fs.Seek(_offset, SeekOrigin.Begin);
            var bytes = new byte[(int)Math.Min(MaxReadPerPoll, fs.Length - _offset)];
            int n = fs.Read(bytes, 0, bytes.Length);
            _offset += n;
            var chars = new char[_decoder.GetCharCount(bytes, 0, n)];
            _decoder.GetChars(bytes, 0, n, chars, 0);
            foreach (var c in chars)
            {
                if (c == '\n') { Feed(_partial.ToString().TrimEnd('\r')); _partial.Clear(); }
                else if (_partial.Length < 4096) _partial.Append(c); // a runaway line is cut, not kept
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return before != (Phase, ConvertedSummary);
    }

    /// <summary>Applies one complete log line.</summary>
    public void Feed(string line)
    {
        var (phase, summary) = ParseLine(line);
        if (phase == null) return;
        Phase = phase.Value;
        if (summary != null) ConvertedSummary = summary;
    }

    /// <summary>The phase a runtime log line starts, with the conversion summary; (null, null) for other lines.</summary>
    public static (StartupPhase? Phase, string? Summary) ParseLine(string line)
    {
        if (InstallingLine.IsMatch(line)) return (StartupPhase.Installing, null);
        if (ConvertingLine.IsMatch(line)) return (StartupPhase.Converting, null);
        var m = ConvertedLine.Match(line);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var classes) && long.TryParse(m.Groups[2].Value, out var ms))
            return (StartupPhase.Starting, $"{classes:N0} classes in {ms / 1000.0:0.0} s");
        return (null, null);
    }

    /// <summary>Checks <see cref="ParseLine"/> on the runtime's real lines. Returns the failures.</summary>
    public static List<string> SelfTest()
    {
        var failures = new List<string>();
        void Expect(string line, StartupPhase? phase)
        {
            var got = ParseLine(line).Phase;
            if (got != phase) failures.Add($"line \"{line}\": got {got?.ToString() ?? "none"}, expected {phase?.ToString() ?? "none"}");
        }
        Expect("[win.apk] installing the app (once per install)...", StartupPhase.Installing);
        Expect("[win.apk] converting the app's code (once per install)...", StartupPhase.Converting);
        Expect(@"[macapk-dex] 18342 classes -> C:\d\apps\WhatsApp\classes.jar.tmp in 151234 ms", StartupPhase.Starting);
        Expect("[win.apk] installing the app failed (exit code 1)", null);
        Expect("# command: \"C:\\rt\\winapk-runtime.exe\" --apk \"a.apk\" --data \"d\"", null);
        Expect("[win.apk] WhatsApp is already running; not starting a second copy", null);
        var summary = ParseLine("[macapk-dex] 1234 classes -> x in 3012 ms").Summary;
        if (summary == null || !summary.Contains("classes in 3", StringComparison.Ordinal))
            failures.Add($"conversion summary: got \"{summary}\"");
        return failures;
    }
}
