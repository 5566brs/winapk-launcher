using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinApkLauncher;

/// <summary>A winapk-runtime process that serves a profile's app, and its app window if it has one.</summary>
public sealed record RuntimeProcess(int Pid, IntPtr Window, string WindowTitle);

/// <summary>What a launch of a profile would run into.</summary>
public enum InstanceState
{
    /// <summary>Nothing serves this profile's app.</summary>
    NotRunning,
    /// <summary>A runtime serves it but has no window yet (installing, converting, booting).</summary>
    Starting,
    /// <summary>A runtime serves it and has its window.</summary>
    Running,
}

/// <summary>The result of <see cref="RuntimeInstance.Probe"/>.</summary>
public sealed record InstanceProbe(InstanceState State, bool LockHeld, IReadOnlyList<RuntimeProcess> Processes, string MutexName)
{
    /// <summary>The first app window among <see cref="Processes"/>, or zero.</summary>
    public IntPtr Window => Processes.FirstOrDefault(p => p.Window != IntPtr.Zero)?.Window ?? IntPtr.Zero;
}

/// <summary>
/// Finds a profile's running runtime the way the runtime itself does. winapk-runtime.exe allows one
/// runtime per app folder (<c>claim_single_instance</c> in windows/host/runtime_main.cpp): a named
/// mutex <c>Local\WinApk-app-%016llx</c>, held for the life of the process, whose value is FNV-1a 64
/// over the UTF-16 units of the app folder <c>&lt;data&gt;\apps\&lt;APK file name without its
/// extension&gt;</c>, made absolute, lexically normalized, with <c>/</c> turned into <c>\</c>, trailing
/// separators dropped and ASCII letters lower-cased. A second runtime for the same folder exits with
/// code 3. <see cref="MutexName(string, string, string?)"/> mirrors that name exactly, and
/// <see cref="SelfTest"/> checks it against values computed from the runtime's C++.
/// </summary>
internal static class RuntimeInstance
{
    /// <summary>The exit code of a runtime that found its app already running.</summary>
    public const int ExitAlreadyRunning = 3;

    /// <summary>The runtime's own executable name; a custom runtime path adds its file name.</summary>
    public const string RuntimeProcessName = "winapk-runtime";

    // ----- the runtime's lock name -----

    /// <summary>The runtime's per-app folder: <c>&lt;data&gt;\apps\&lt;stem of the APK&gt;</c>, as
    /// <c>data / "apps" / apk.stem()</c> builds it.</summary>
    public static string AppDir(string dataRoot, string apkPath) => Join(Join(dataRoot, "apps"), Stem(apkPath));

    /// <summary><c>std::filesystem::path::stem()</c> of a path's file name: "pf-notes.apk" is
    /// "pf-notes", "a.b.xapk" is "a.b", and a leading dot is not an extension (".apk" stays ".apk").</summary>
    public static string Stem(string path)
    {
        var name = Path.GetFileName(path) ?? "";
        if (name is "." or "..") return name;
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>
    /// The lock key for an app folder: <c>fs::absolute</c> (libc++: an absolute path is kept as it is,
    /// a relative one is put under the runtime's working directory, which the launcher sets to the
    /// runtime's folder), <c>lexically_normal</c>, trailing separators dropped, <c>/</c> to <c>\</c>,
    /// and <c>towlower</c> in the C locale, which changes ASCII letters only.
    /// </summary>
    public static string Key(string appDir, string? workingDir)
    {
        var key = LexicallyNormal(Absolute(appDir, workingDir)).TrimEnd('\\', '/');
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            var c = ch == '/' ? '\\' : ch;
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c);
        }
        return sb.ToString();
    }

    // The runtime's constants, kept exactly: its offset basis is 1469598103934665603, not the
    // textbook FNV-1a 64 basis (14695981039346656037), and it hashes whole UTF-16 code units.
    private const ulong FnvOffset = 1469598103934665603UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Fnv1a(string key)
    {
        ulong h = FnvOffset;
        foreach (char c in key) h = unchecked((h ^ c) * FnvPrime);
        return h;
    }

    public static string MutexNameForKey(string key) => $@"Local\WinApk-app-{Fnv1a(key):x16}";

    /// <summary>The runtime's lock name for a --data / --apk pair started by <paramref name="runtimeExe"/>.</summary>
    public static string MutexName(string dataRoot, string apkPath, string? runtimeExe) =>
        MutexNameForKey(Key(AppDir(dataRoot, apkPath), RuntimeWorkingDir(runtimeExe)));

    /// <summary>The working directory <see cref="RuntimeLauncher.Start"/> gives the runtime.</summary>
    public static string? RuntimeWorkingDir(string? runtimeExe) =>
        string.IsNullOrWhiteSpace(runtimeExe) ? null : Path.GetDirectoryName(runtimeExe);

    private static bool IsSep(char c) => c is '\\' or '/';

    /// <summary>Splits off a root name: "C:" or a UNC "\\server".</summary>
    private static (string Root, string Tail) SplitRootName(string p)
    {
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) return (p[..2], p[2..]);
        if (p.Length >= 3 && IsSep(p[0]) && IsSep(p[1]) && !IsSep(p[2]))
        {
            int j = 2;
            while (j < p.Length && !IsSep(p[j])) j++;
            return (p[..j], p[j..]);
        }
        return ("", p);
    }

    /// <summary><c>path / name</c> for a relative <paramref name="name"/> without a root.</summary>
    private static string Join(string a, string name)
    {
        if (a.Length == 0) return name;
        if (IsSep(a[^1])) return a + name;
        var (root, rest) = SplitRootName(a);
        if (rest.Length == 0 && root.Length == 2) return a + name; // "C:" / "x" is "C:x"
        return a + "\\" + name;
    }

    /// <summary>libc++ <c>fs::absolute</c>: an absolute path as it is; else <c>current_path() / p</c>.</summary>
    private static string Absolute(string p, string? cwd)
    {
        var (root, rest) = SplitRootName(p);
        bool absolute = root.Length > 0 && rest.Length > 0 && IsSep(rest[0]);
        if (absolute || string.IsNullOrEmpty(cwd)) return p;
        var (cwdRoot, _) = SplitRootName(cwd);
        if (root.Length > 0 && !string.Equals(root, cwdRoot, StringComparison.OrdinalIgnoreCase)) return p;
        if (rest.Length > 0 && IsSep(rest[0])) return cwdRoot + rest;
        return Join(cwd, rest);
    }

    /// <summary><c>std::filesystem::path::lexically_normal</c> on Windows: one separator between
    /// parts, "." removed, "name\.." removed, ".." right after the root dropped.</summary>
    private static string LexicallyNormal(string p)
    {
        if (p.Length == 0) return p;
        var (root, rest) = SplitRootName(p);
        bool rootDir = rest.Length > 0 && IsSep(rest[0]);
        var parts = new List<string>();
        foreach (var part in rest.Split('\\', '/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
                else if (!rootDir) parts.Add("..");
                continue;
            }
            parts.Add(part);
        }
        var s = root.Replace('/', '\\') + (rootDir ? "\\" : "") + string.Join('\\', parts);
        return s.Length == 0 ? "." : s;
    }

    // ----- what is running -----

    /// <summary>
    /// Whether the runtime's lock for this profile is held and which runtime processes serve its app
    /// (found by their --apk/--data arguments), with their windows. Never starts or stops anything.
    /// A profile that allows multiple instances runs without the lock, so only its processes count.
    /// </summary>
    public static InstanceProbe Probe(Profile p, string? runtimeExe)
    {
        var name = MutexName(p.DataRoot, p.ApkPath, runtimeExe);
        bool held = !p.AllowMultipleInstances && MutexExists(name);
        var procs = FindProcesses(p, runtimeExe);
        var state = procs.Any(x => x.Window != IntPtr.Zero) ? InstanceState.Running
            : held || procs.Count > 0 ? InstanceState.Starting
            : InstanceState.NotRunning;
        return new InstanceProbe(state, held, procs, name);
    }

    /// <summary>True when a mutex of that name exists (OpenMutexW with SYNCHRONIZE access only).</summary>
    public static bool MutexExists(string name)
    {
        var h = OpenMutexW(SYNCHRONIZE, false, name);
        if (h != IntPtr.Zero) { CloseHandle(h); return true; }
        return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED; // there, but not ours to open
    }

    /// <summary>
    /// Runtime processes whose command line names this profile's app folder: each
    /// winapk-runtime.exe (and the configured runtime's file name) is read with
    /// NtQueryInformationProcess(ProcessCommandLineInformation) and its --apk/--data arguments are
    /// keyed like the runtime keys its lock. Processes that cannot be read are skipped.
    /// </summary>
    public static List<RuntimeProcess> FindProcesses(Profile p, string? runtimeExe)
    {
        var result = new List<RuntimeProcess>();
        if (string.IsNullOrWhiteSpace(p.ApkPath)) return result;
        var want = Key(AppDir(p.DataRoot, p.ApkPath), RuntimeWorkingDir(runtimeExe));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RuntimeProcessName };
        if (!string.IsNullOrWhiteSpace(runtimeExe)) names.Add(Path.GetFileNameWithoutExtension(runtimeExe));
        var seen = new HashSet<int>();
        foreach (var exeName in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(exeName); } catch { continue; }
            foreach (var proc in procs)
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == Environment.ProcessId || !seen.Add(proc.Id)) continue;
                        var cmd = CommandLine(proc.Id);
                        if (cmd == null || !TryParseRuntimeArgs(cmd, out var apk, out var data, out var exe)) continue;
                        if (Key(AppDir(data, apk), RuntimeWorkingDir(exe ?? runtimeExe)) != want) continue;
                        var w = FindAppWindow(proc.Id, includeDialogs: true);
                        result.Add(new RuntimeProcess(proc.Id, w, w != IntPtr.Zero ? WindowText(w) : ""));
                    }
                    catch { }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The runtime's own argument rules (parse_args, resolve_paths): --apk and --data take the next
    /// argument, --jdk/--lib too, --install-only stands alone, anything else is a usage error; a
    /// missing --data means %LOCALAPPDATA%\Win.apk.
    /// </summary>
    internal static bool TryParseRuntimeArgs(string commandLine, out string apk, out string data, out string? exe)
    {
        apk = data = "";
        exe = null;
        var argv = SplitCommandLine(commandLine);
        if (argv.Length == 0) return false;
        exe = argv[0];
        for (int i = 1; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a is "--apk" or "--data" or "--jdk" or "--lib")
            {
                if (i + 1 >= argv.Length) return false;
                var v = argv[++i];
                if (a == "--apk") apk = v;
                else if (a == "--data") data = v;
            }
            else if (a != "--install-only") return false;
        }
        if (apk.Length == 0) return false;
        if (data.Length == 0)
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) return false;
            data = Path.Combine(local, "Win.apk");
        }
        return true;
    }

    /// <summary>The command line of another process, or null if it cannot be read.</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int size = 1024;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                buf = Marshal.AllocHGlobal(size);
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                if (status == 0)
                {
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } at the start.
                    int bytes = (ushort)Marshal.ReadInt16(buf);
                    var text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                    return bytes == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
                }
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                if (status != STATUS_INFO_LENGTH_MISMATCH && status != STATUS_BUFFER_TOO_SMALL) return null;
                size = Math.Max(needed, size * 2);
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(argv); }
    }

    // ----- windows -----

    /// <summary>
    /// The process's app window: a visible, unowned top-level window that is not a tool window.
    /// Message boxes (class #32770) count only with <paramref name="includeDialogs"/>, and only when
    /// there is no other window.
    /// </summary>
    public static IntPtr FindAppWindow(int pid, bool includeDialogs = false)
    {
        IntPtr found = IntPtr.Zero, dialog = IntPtr.Zero;
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != (uint)pid || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (!GetWindowRect(h, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
            cls.Clear();
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() == "#32770")
            {
                if (dialog == IntPtr.Zero) dialog = h;
                return true;
            }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : includeDialogs ? dialog : IntPtr.Zero;
    }

    public static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Restores the window if it is minimized and brings it to the front. When Windows refuses the
    /// foreground change (this process is not the one the user is working in), it flashes instead.
    /// </summary>
    public static bool BringToFront(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (SetForegroundWindow(h)) return true;
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = h,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 3,
        };
        FlashWindowEx(ref fi);
        return false;
    }

    /// <summary>Ends a runtime process and the processes it started (the converter's java.exe).</summary>
    public static bool Stop(int pid, int waitMs = 5000)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            return proc.WaitForExit(waitMs);
        }
        catch (ArgumentException) { return true; } // already gone
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return true; }
    }

    // ----- self-check -----

    /// <summary>
    /// Checks the lock-name mirror against values computed from the runtime's C++ (the code of
    /// claim_single_instance, built with the runtime's toolchain: clang, libc++, UCRT) and
    /// cross-checked with an independent FNV-1a script. Returns the failures; empty means OK.
    /// </summary>
    public static List<string> SelfTest()
    {
        var failures = new List<string>();
        void Expect(string what, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                failures.Add($"{what}: got \"{actual}\", expected \"{expected}\"");
        }
        void Sample(string data, string apk, string key, string hex)
        {
            var k = Key(AppDir(data, apk), @"C:\runtime\bin");
            Expect($"key({data} | {apk})", k, key);
            Expect($"name({data} | {apk})", MutexName(data, apk, @"C:\runtime\bin\winapk-runtime.exe"), @"Local\WinApk-app-" + hex);
        }

        Sample(@"E:\Home\Example\AppData\Local\WinApk Launcher\apps\WhatsApp", @"C:\Apps\WhatsApp.apk",
            @"e:\home\example\appdata\local\winapk launcher\apps\whatsapp\apps\whatsapp", "f301b9535d7eee31");
        Sample(@"D:/Portable/WinApk Launcher/apps/PF Notes/", @"D:\pkgs\pf-notes.apk",
            @"d:\portable\winapk launcher\apps\pf notes\apps\pf-notes", "fd700cae349af571");
        Sample(@"E:\Data\.\x\..\Notes\\", "org.secuso.notes_12.xapk",
            @"e:\data\notes\apps\org.secuso.notes_12", "676b5576c96a7e6e");
        // towlower in the C locale lowers ASCII only: the non-ASCII capitals stay.
        Sample("C:\\Data\\\u00C4rger\\\u03A9mega", "\u00DCn\u00EF.APK",
            "c:\\data\\\u00C4rger\\\u03A9mega\\apps\\\u00DCn\u00EF", "4ed6104570c3e836");
        Sample(@"\\server\share\Apps", "a.b.apk", @"\\server\share\apps\apps\a.b", "55e39641edb5b124");
        // libc++ keeps an absolute path as it is: no trailing-dot/space trimming, and ".apk" is a stem.
        Sample(@"C:\Apps\Foo. ", ".apk", @"c:\apps\foo. \apps\.apk", "baaf9970f1b43dec");
        Sample(@"C:\x\..\..\y\", @"C:\noext", @"c:\y\apps\noext", "d3f5065d0799c33d");

        // A relative data root is resolved against the runtime's working directory.
        Expect("relative key", Key(AppDir(@"rel\data", "x.apk"), @"C:\runtime\bin"), @"c:\runtime\bin\rel\data\apps\x");
        Expect("stem pf-notes.apk", Stem(@"C:\a\pf-notes.apk"), "pf-notes");
        Expect("stem a.b.c.xapk", Stem("a.b.c.xapk"), "a.b.c");
        Expect("stem noext", Stem("noext"), "noext");

        // The command line the launcher builds parses back to the same app folder.
        var cmd = RuntimeLauncher.BuildCommandLine(@"C:\runtime\bin\winapk-runtime.exe",
            new Profile { ApkPath = @"C:\Apps\pf-notes.apk", DataRoot = @"D:\Data\PF Notes" });
        if (!TryParseRuntimeArgs(cmd, out var apk2, out var data2, out _)) failures.Add("could not parse " + cmd);
        else
        {
            Expect("parsed --apk", apk2, @"C:\Apps\pf-notes.apk");
            Expect("parsed --data", data2, @"D:\Data\PF Notes");
        }
        if (TryParseRuntimeArgs(@"""C:\rt\winapk-runtime.exe"" --apk a.apk --bogus", out _, out _, out _))
            failures.Add("an unknown runtime option must not parse (the runtime exits with a usage error)");
        return failures;
    }

    // ----- Win32 -----

    private const uint SYNCHRONIZE = 0x00100000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
