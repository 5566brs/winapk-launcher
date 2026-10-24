using System.Runtime.InteropServices;
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

/// <summary>Whether a starting app still shows signs of work; see <see cref="StartupWatch.Stall"/>.</summary>
public enum StallState
{
    /// <summary>The run log grew recently.</summary>
    None,
    /// <summary>The log has been quiet for a while, but the runtime or its children use the CPU.</summary>
    Busy,
    /// <summary>Neither the log nor the CPU time has moved for a while: the app may be stuck.</summary>
    Stalled,
}

/// <summary>
/// Follows one launch from Start until its first window: reads the lines the runtime appends to the
/// run log the launcher owns and keeps the current <see cref="StartupPhase"/>, the latest useful log
/// line, when the log last grew and when the runtime's process tree last used the CPU. Only three
/// runtime lines change the phase: "[win.apk] installing ...", "[win.apk] converting ..." and the
/// converter's "[macapk-dex] N classes -> ... in X ms". The parsing works on the English log
/// lines; the texts it produces come from <see cref="L"/>. Not thread-safe; the GUI polls it on its
/// timer.
/// </summary>
public sealed class StartupWatch
{
    private static readonly Regex InstallingLine = new(@"^\[win\.apk\] installing\b(?!.*\bfailed\b)", RegexOptions.CultureInvariant);
    private static readonly Regex ConvertingLine = new(@"^\[win\.apk\] converting\b", RegexOptions.CultureInvariant);
    private static readonly Regex ConvertedLine = new(@"^\[macapk-dex\] (\d+) classes\b.*\bin (\d+) ms", RegexOptions.CultureInvariant);

    /// <summary>Lines from the runtime's own steps; the last of these is shown before any other line.</summary>
    private static readonly string[] StepPrefixes = { "[win.apk]", "[install]", "[macapk-dex]" };

    private const int MaxReadPerPoll = 1 << 20;

    // A capped log (see CappedLog) is rewritten in place at the same size: its last bytes tell
    // whether it changed.
    private const int TailWindow = 16 * 1024;
    private byte[]? _lastWindow;

    /// <summary>
    /// How long both the log and the CPU must be idle before the startup window says the app may be
    /// stuck: 3 minutes. WINAPKLAUNCHER_STALL_SECONDS overrides it, for tests.
    /// </summary>
    public static TimeSpan StallAfter { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("WINAPKLAUNCHER_STALL_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(3);

    // The CPU counts as busy while the process tree used at least 5% of one core over the last 30 s.
    private static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(30);
    private const double BusyShare = 0.05;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _partial = new();
    private long _offset;
    private string? _lastStep, _lastAny;
    private (long Classes, long Ms)? _converted;

    private ProcessTreeCpu? _cpu;
    private bool _cpuKnown;
    private readonly Queue<(DateTime At, TimeSpan Cpu)> _cpuSamples = new();
    private DateTime _nextCpuSample;

    /// <summary>Follows a launch of this window.</summary>
    public StartupWatch(LaunchedProcess launch) : this(launch.Profile, launch.LogPath, launch)
    {
        try { AttachProcess(launch.Process.Id); } catch { }
    }

    /// <summary>Follows any runtime of <paramref name="profile"/>, through its run log if there is one.</summary>
    public StartupWatch(Profile profile, string? logPath, LaunchedProcess? launch = null)
    {
        Profile = profile;
        LogPath = logPath;
        Launch = launch;
        StartedUtc = launch != null ? DateTime.UtcNow : StartTimeOf(logPath) ?? DateTime.UtcNow;
        LastLogGrowthUtc = LastCpuBusyUtc = DateTime.UtcNow;
    }

    /// <summary>The launch this window started; null for a runtime started elsewhere.</summary>
    public LaunchedProcess? Launch { get; }
    public Profile Profile { get; }
    public string? LogPath { get; }
    public DateTime StartedUtc { get; private set; }

    /// <summary>When the run log last grew (or when this watch began).</summary>
    public DateTime LastLogGrowthUtc { get; private set; }

    /// <summary>When the runtime's process tree last used the CPU (or when this watch began).</summary>
    public DateTime LastCpuBusyUtc { get; private set; }

    /// <summary>When a run log was created: the launch time of a runtime started elsewhere.</summary>
    private static DateTime? StartTimeOf(string? logPath)
    {
        try { return logPath != null && File.Exists(logPath) ? File.GetCreationTimeUtc(logPath) : null; }
        catch { return null; }
    }
    public StartupPhase Phase { get; private set; } = StartupPhase.Starting;

    /// <summary>"1,234 classes in 3.0 s" once the code conversion has finished in this launch.</summary>
    public string? ConvertedSummary => _converted is (long classes, long ms) ? L.ConvertedSummary(classes, ms) : null;

    /// <summary>
    /// The newest line of the runtime's own steps ("[win.apk] ...", "[install] ...", "[macapk-dex] ..."),
    /// or else the newest non-empty line; the launcher's "# ..." header lines are skipped. Null before
    /// the runtime wrote anything.
    /// </summary>
    public string? LastLogLine => _lastStep ?? _lastAny;

    /// <summary>The step as the status bar shows it.</summary>
    public string PhaseText => PhaseLabel(Phase);

    /// <summary>The step as the startup window shows it.</summary>
    public string SplashPhaseText => Phase switch
    {
        StartupPhase.Installing => L.PhaseInstalling,
        StartupPhase.Converting => L.PhaseConvertingLong,
        _ => ConvertedSummary is string s ? L.PhaseStartingConverted(s) : L.PhaseStarting,
    };

    public static string PhaseLabel(StartupPhase phase) => phase switch
    {
        StartupPhase.Installing => L.PhaseInstalling,
        StartupPhase.Converting => L.PhaseConverting,
        _ => L.PhaseStarting,
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
        L.StatusLine(Profile.Name,
            PhaseText + (Phase == StartupPhase.Starting && ConvertedSummary is string s ? L.CodeConverted(s) : ""),
            ElapsedText);

    /// <summary>Also watch the CPU time of this runtime process and the processes it starts.</summary>
    public void AttachProcess(int pid) => _cpu = new ProcessTreeCpu(pid);

    /// <summary>
    /// Reads what the runtime appended since the last call and samples the CPU time (every 2 s).
    /// Returns true when the phase changed.
    /// </summary>
    public bool Poll()
    {
        SampleCpu();
        if (LogPath == null) return false;
        var before = (Phase, _converted);
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (fs.Length <= _offset)
            {
                ReadRewrittenTail(fs);
                return before != (Phase, _converted);
            }
            _lastWindow = null;
            LastLogGrowthUtc = DateTime.UtcNow;
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
        return before != (Phase, _converted);
    }

    /// <summary>
    /// The file did not get longer: unchanged, or rewritten in place by the log keeper once it reached
    /// its limit. Compares its last bytes with the last look; when they differ, the log moved on, and
    /// the whole lines among them are applied.
    /// </summary>
    private void ReadRewrittenTail(FileStream fs)
    {
        long len = fs.Length;
        int n = (int)Math.Min(len, TailWindow);
        var window = new byte[n];
        fs.Seek(len - n, SeekOrigin.Begin);
        fs.ReadExactly(window);
        if (_lastWindow == null && len == _offset) { _lastWindow = window; return; } // the baseline after reading it all
        if (_lastWindow != null && window.AsSpan().SequenceEqual(_lastWindow)) return;
        _lastWindow = window;
        _offset = len;
        _partial.Clear();
        _decoder.Reset();
        LastLogGrowthUtc = DateTime.UtcNow;
        var text = Encoding.UTF8.GetString(window);
        int start = n < len ? text.IndexOf('\n') + 1 : 0; // the first line may be cut
        int end = text.LastIndexOf('\n');
        if (start <= 0 && n < len || end < start) return;
        foreach (var line in text[start..end].Split('\n')) Feed(line.TrimEnd('\r'));
    }

    /// <summary>Applies one complete log line.</summary>
    public void Feed(string line)
    {
        var text = line.Trim();
        if (text.Length > 0 && text[0] != '#')
        {
            _lastAny = text;
            if (StepPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))) _lastStep = text;
        }
        var (phase, converted) = ParseLine(line);
        if (phase == null) return;
        Phase = phase.Value;
        if (converted != null) _converted = converted;
    }

    /// <summary>The phase a runtime log line starts, with the conversion result; (null, null) for other lines.</summary>
    public static (StartupPhase? Phase, (long Classes, long Ms)? Converted) ParseLine(string line)
    {
        if (InstallingLine.IsMatch(line)) return (StartupPhase.Installing, null);
        if (ConvertingLine.IsMatch(line)) return (StartupPhase.Converting, null);
        var m = ConvertedLine.Match(line);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var classes) && long.TryParse(m.Groups[2].Value, out var ms))
            return (StartupPhase.Starting, (classes, ms));
        return (null, null);
    }

    private void SampleCpu()
    {
        if (_cpu == null) return;
        var now = DateTime.UtcNow;
        if (now < _nextCpuSample) return;
        _nextCpuSample = now.AddSeconds(2);
        if (_cpu.Sample() is not TimeSpan cpu) return;
        _cpuKnown = true;
        _cpuSamples.Enqueue((now, cpu));
        while (_cpuSamples.Count > 2 && now - _cpuSamples.Peek().At > CpuWindow) _cpuSamples.Dequeue();
        var (at, first) = _cpuSamples.Peek();
        double span = (now - at).TotalSeconds;
        if (span >= 4 && (cpu - first).TotalSeconds >= BusyShare * span) LastCpuBusyUtc = now;
    }

    /// <summary>
    /// <see cref="StallState.Stalled"/> with the idle minutes when the run log has not grown for
    /// <see cref="StallAfter"/> and the process tree has not used the CPU for as long either;
    /// <see cref="StallState.Busy"/> when only the log is quiet. A long code conversion writes
    /// nothing for minutes while it keeps a core busy.
    /// </summary>
    public (StallState State, int Minutes) Stall()
    {
        var now = DateTime.UtcNow;
        var logIdle = now - LastLogGrowthUtc;
        if (logIdle < StallAfter) return (StallState.None, 0);
        if (!_cpuKnown) return (StallState.Stalled, (int)logIdle.TotalMinutes);
        var cpuIdle = now - LastCpuBusyUtc;
        if (cpuIdle < StallAfter) return (StallState.Busy, 0);
        return (StallState.Stalled, (int)Math.Min(logIdle.TotalMinutes, cpuIdle.TotalMinutes));
    }

    /// <summary>For --screenshot: a sample state without a runtime behind it.</summary>
    internal void SetShotState(TimeSpan elapsed, TimeSpan logIdle, bool cpuBusy)
    {
        var now = DateTime.UtcNow;
        StartedUtc = now - elapsed;
        LastLogGrowthUtc = now - logIdle;
        _cpuKnown = true;
        LastCpuBusyUtc = cpuBusy ? now : now - logIdle;
    }

    /// <summary>Checks <see cref="ParseLine"/> on the runtime's real lines, the last-line filter and the CPU sampler. Returns the failures.</summary>
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
        if (ParseLine("[macapk-dex] 1234 classes -> x in 3012 ms").Converted is not (1234, 3012))
            failures.Add("conversion result of \"[macapk-dex] 1234 classes -> x in 3012 ms\"");

        var w = new StartupWatch(new Profile { Name = "x" }, null);
        void Last(string line, string? expected)
        {
            w.Feed(line);
            if (w.LastLogLine != expected) failures.Add($"last log line after \"{line}\": got \"{w.LastLogLine}\", expected \"{expected}\"");
        }
        Last("# ---- the launcher's header ----", null);
        Last("I/ActivityThread: boot", "I/ActivityThread: boot");
        Last("[install] extracting lib/arm64-v8a", "[install] extracting lib/arm64-v8a");
        Last("D/Chatty: something else", "[install] extracting lib/arm64-v8a");
        Last("   ", "[install] extracting lib/arm64-v8a");
        Last("[macapk-dex] 10 classes -> x in 5 ms", "[macapk-dex] 10 classes -> x in 5 ms");

        if (new ProcessTreeCpu(Environment.ProcessId).Sample() is not TimeSpan own || own <= TimeSpan.Zero)
            failures.Add("process tree CPU time of this process");
        return failures;
    }
}

/// <summary>
/// The CPU time of a process and every process it started (the runtime's converter runs in a child
/// java.exe). Each process is counted with the last CPU time seen for it, so a child that has exited
/// keeps counting and the total never goes back.
/// </summary>
internal sealed class ProcessTreeCpu
{
    private readonly int _root;
    private long _rootCreated = -1;
    private readonly Dictionary<(int Pid, long Created), long> _seen = new(); // CPU time in 100 ns units

    public ProcessTreeCpu(int rootPid) => _root = rootPid;

    /// <summary>The total CPU time so far, or null when the process could never be read.</summary>
    public TimeSpan? Sample()
    {
        var root = Times(_root);
        if (root is not (long rootCreated, long rootCpu) || (_rootCreated >= 0 && rootCreated != _rootCreated))
            return _seen.Count > 0 ? Total() : null; // exited, or its PID now belongs to another process
        _rootCreated = rootCreated;
        Record(_root, rootCreated, rootCpu);

        // Descendants: a process whose parent is in the tree and that was created after that parent
        // (an older one only inherited a reused parent PID).
        var parents = Snapshot();
        var tree = new Dictionary<int, long> { [_root] = rootCreated };
        for (bool added = true; added;)
        {
            added = false;
            foreach (var (pid, parent) in parents)
            {
                if (tree.ContainsKey(pid) || !tree.TryGetValue(parent, out var parentCreated)) continue;
                if (Times(pid) is not (long created, long cpu) || created < parentCreated) continue;
                tree[pid] = created;
                Record(pid, created, cpu);
                added = true;
            }
        }
        return Total();
    }

    private void Record(int pid, long created, long cpu) =>
        _seen[(pid, created)] = Math.Max(cpu, _seen.GetValueOrDefault((pid, created)));

    private TimeSpan Total() => TimeSpan.FromTicks(_seen.Values.Sum());

    /// <summary>A process's creation time and its kernel + user CPU time, or null.</summary>
    private static (long Created, long Cpu)? Times(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(h, out var created, out _, out var kernel, out var user)) return null;
            return (created, kernel + user);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>(PID, parent PID) of every process.</summary>
    private static List<(int Pid, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
}
