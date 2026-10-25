using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinApkLauncher;

/// <summary>The paths one launch hands the runtime (see <see cref="PathSafety"/>).</summary>
/// <param name="RuntimeExe">The winapk-runtime.exe to start.</param>
/// <param name="ApkPath">The --apk argument.</param>
/// <param name="DataRoot">The --data argument.</param>
/// <param name="TempDir">A folder for TMP, TEMP and java.io.tmpdir, or null to keep the user's.</param>
/// <param name="Notes">One line per path that was moved, for the run log and the console.</param>
public sealed record LaunchPaths(string RuntimeExe, string ApkPath, string DataRoot, string? TempDir,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when any path differs from the profile's own.</summary>
    public bool Moved => Notes.Count > 0;
}

/// <summary>
/// Plain-letter paths for a runtime that reads paths in the ANSI code page.
///
/// The JVM inside winapk-runtime.exe takes file names in the ANSI code page ("Language for
/// non-Unicode programs"). A path with a character that code page cannot hold, such as a user name
/// in Hebrew on an English system, then reaches it as "?", and every app fails. The runtime from
/// bundle/v0.1.3 on sets its own code page to UTF-8 in its manifest, which Windows 10 1903 and
/// later honors, so there every path works. Before 1903, or with an older runtime, the launcher
/// moves each such path to a plain-letter folder under <c>C:\WinApk Launcher</c>:
///   data root -&gt; C:\WinApk Launcher\apps\&lt;name&gt;-&lt;profile id&gt;
///   TEMP      -&gt; C:\WinApk Launcher\temp  (TMP, TEMP and -Djava.io.tmpdir)
///   APK       -&gt; a copy in C:\WinApk Launcher\apks\&lt;profile id&gt;\, refreshed when the APK changes
///   runtime   -&gt; a directory junction C:\WinApk Launcher\runtime-&lt;hash&gt; to its folder (an 8.3
///                short name is not enough: Java expands it again before it loads its DLLs)
/// A path the code page can hold is never moved. Nothing needs configuring.
///
/// Test switches: WINAPK_ANSI_PATHS=1 moves paths whatever the system (=0 never),
/// WINAPK_LAUNCHER_TEST_ACP=&lt;code page&gt; stands in for the system's ANSI code page, and
/// WINAPK_LAUNCHER_SAFE_ROOT=&lt;folder&gt; replaces C:\WinApk Launcher.
/// </summary>
internal static class PathSafety
{
    public const string EnvMode = "WINAPK_ANSI_PATHS";
    public const string EnvTestAcp = "WINAPK_LAUNCHER_TEST_ACP";
    public const string EnvSafeRoot = "WINAPK_LAUNCHER_SAFE_ROOT";

    /// <summary>Windows 10 1903, the first to honor activeCodePage in a manifest.</summary>
    public const int Utf8ManifestBuild = 18362;

    private const int CpUtf8 = 65001;

    /// <summary>The runtime <see cref="LauncherStore.ResolveRuntime()"/> found last (null: the bundled one).</summary>
    public static string? DefaultRuntime { get; set; }

    /// <summary>The ANSI code page paths are checked against: the system's, or the test switch's.</summary>
    public static int AnsiCodePage =>
        int.TryParse(Environment.GetEnvironmentVariable(EnvTestAcp), out var cp) && cp > 0 ? cp : (int)GetACP();

    /// <summary>The folder moved paths go to: <c>C:\WinApk Launcher</c> on the system drive.</summary>
    public static string SafeRoot
    {
        get
        {
            var o = Environment.GetEnvironmentVariable(EnvSafeRoot);
            if (!string.IsNullOrWhiteSpace(o)) return Path.GetFullPath(o.Trim());
            var drive = Path.GetPathRoot(Environment.SystemDirectory);
            return Path.Combine(string.IsNullOrEmpty(drive) ? @"C:\" : drive, AppInfo.ProductName);
        }
    }

    /// <summary>True when every character of <paramref name="s"/> survives a round trip through the
    /// code page (WideCharToMultiByte without best-fit, then MultiByteToWideChar). UTF-8 holds all.</summary>
    public static bool Survives(string s, int codePage)
    {
        if (codePage == CpUtf8 || s.Length == 0) return true;
        bool ascii = true;
        foreach (var c in s) ascii &= c < 0x80;
        if (ascii) return true;
        int n = WideCharToMultiByte((uint)codePage, WC_NO_BEST_FIT_CHARS, s, s.Length, null, 0, IntPtr.Zero, out int lossy);
        if (n <= 0 || lossy != 0) return false;
        var bytes = new byte[n];
        if (WideCharToMultiByte((uint)codePage, WC_NO_BEST_FIT_CHARS, s, s.Length, bytes, n, IntPtr.Zero, out lossy) != n
            || lossy != 0) return false;
        int m = MultiByteToWideChar((uint)codePage, 0, bytes, n, null, 0);
        if (m <= 0) return false;
        var back = new char[m];
        MultiByteToWideChar((uint)codePage, 0, bytes, n, back, m);
        return new string(back) == s;
    }

    /// <summary>True when this Windows honors the runtime's UTF-8 manifest and the runtime has one.</summary>
    public static bool RuntimeTakesAnyPath(string runtimeExe) =>
        Environment.OSVersion.Version.Build >= Utf8ManifestBuild && HasUtf8Manifest(runtimeExe);

    /// <summary>Whether launches with this runtime need plain-letter paths (the test switch, else
    /// <see cref="RuntimeTakesAnyPath"/>).</summary>
    public static bool Needed(string? runtimeExe)
    {
        var mode = Environment.GetEnvironmentVariable(EnvMode)?.Trim();
        if (mode == "1") return true;
        if (mode == "0") return false;
        var exe = runtimeExe ?? DefaultRuntime ?? AppPaths.BundledRuntimeExe;
        return !RuntimeTakesAnyPath(exe);
    }

    /// <summary>The paths a launch of <paramref name="p"/> hands the runtime. With
    /// <paramref name="prepare"/> the moved ones are made ready (folders, the APK copy, the runtime
    /// junction); without it nothing on disk changes (dry runs, the running-app probe).</summary>
    public static LaunchPaths Resolve(Profile p, string? runtimeExe = null, bool prepare = false)
    {
        var exe = runtimeExe ?? DefaultRuntime ?? AppPaths.BundledRuntimeExe;
        if (!Needed(exe)) return new LaunchPaths(exe, p.ApkPath, p.DataRoot, null, Array.Empty<string>());
        return Resolve(p, exe, prepare, AnsiCodePage, SafeRoot, Path.GetTempPath());
    }

    /// <summary>The same with every input given (the self-test uses it with a fake code page).</summary>
    internal static LaunchPaths Resolve(Profile p, string exe, bool prepare, int cp, string root, string systemTemp)
    {
        var notes = new List<string>();
        if (prepare)
        {
            try { Directory.CreateDirectory(root); }
            catch (Exception ex)
            {
                notes.Add($"could not create {root} ({ex.Message}); the paths stay as they are");
                return new LaunchPaths(exe, p.ApkPath, p.DataRoot, null, notes);
            }
        }
        var id = string.IsNullOrEmpty(p.Id) ? "profile" : p.Id[..Math.Min(8, p.Id.Length)];

        var data = p.DataRoot;
        if (!string.IsNullOrWhiteSpace(data) && !Survives(Full(data), cp))
        {
            data = Path.Combine(root, "apps", SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(Full(data))), cp)
                                              + "-" + id);
            notes.Add($"data root {p.DataRoot} -> {data}");
            if (prepare) TryCreate(data, notes);
        }

        string? temp = null;
        if (!Survives(systemTemp, cp))
        {
            temp = Path.Combine(root, "temp");
            notes.Add($"TEMP {systemTemp} -> {temp}");
            if (prepare) TryCreate(temp, notes);
        }

        var apk = p.ApkPath;
        if (!string.IsNullOrWhiteSpace(apk) && !Survives(Full(apk), cp))
        {
            apk = Path.Combine(root, "apks", id, SafeFileName(Path.GetFileName(apk), cp));
            notes.Add($"APK {p.ApkPath} -> copy {apk}");
            if (prepare && !StageApk(p.ApkPath, apk, notes)) apk = p.ApkPath;
        }

        var runtime = exe;
        var bin = Path.GetDirectoryName(Full(exe));
        var runtimeRoot = bin == null ? null : Path.GetDirectoryName(bin);
        if (runtimeRoot != null && !Survives(Full(exe), cp))
        {
            var link = Path.Combine(root, $"runtime-{(uint)RuntimeInstance.Fnv1a(runtimeRoot.ToLowerInvariant()):x8}");
            runtime = Path.Combine(link, Path.GetFileName(bin!), Path.GetFileName(exe));
            notes.Add($"runtime {runtimeRoot} -> junction {link}");
            if (prepare && !EnsureJunction(link, runtimeRoot, notes)) runtime = exe;
        }
        return new LaunchPaths(runtime, apk, data, temp, notes);
    }

    /// <summary>-Djava.io.tmpdir for the runtime's MACAPK_JVM_OPTS, which it splits at spaces: the
    /// folder's 8.3 form when its name has a space, or null when there is none (TMP and TEMP, which
    /// Java's default java.io.tmpdir comes from, are set either way).</summary>
    public static string? TmpDirOption(string tempDir)
    {
        var dir = tempDir;
        if (dir.Contains(' '))
        {
            var sb = new StringBuilder(1024);
            var n = GetShortPathNameW(dir, sb, (uint)sb.Capacity);
            dir = n > 0 && n < sb.Capacity ? sb.ToString() : "";
        }
        return dir.Length == 0 || dir.Contains(' ') ? null : "-Djava.io.tmpdir=" + dir;
    }

    /// <summary>A file or folder name with only characters the code page holds; '_' for the rest,
    /// "app" when nothing readable is left.</summary>
    public static string SafeName(string name, int cp)
    {
        var sb = new StringBuilder();
        foreach (var ch in RuntimeLauncher.MakeFileSafe(name))
            sb.Append(Survives(ch.ToString(), cp) ? ch : '_');
        var s = sb.ToString().Trim();
        return s.Any(char.IsLetterOrDigit) ? s : "app";
    }

    /// <summary>The APK copy's file name: the same when the code page holds it, else a safe stem
    /// plus a hash of the original name (the runtime names the app's folder after the stem).</summary>
    private static string SafeFileName(string fileName, int cp)
    {
        if (Survives(fileName, cp)) return fileName;
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return SafeName(stem, cp) + $"-{(uint)RuntimeInstance.Fnv1a(fileName):x8}" + (Survives(ext, cp) ? ext : ".apk");
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private static void TryCreate(string dir, List<string> notes)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { notes.Add($"could not create {dir}: {ex.Message}"); }
    }

    /// <summary>Copies the APK unless the copy is current (same size and time).</summary>
    private static bool StageApk(string source, string copy, List<string> notes)
    {
        try
        {
            var src = new FileInfo(source);
            var dst = new FileInfo(copy);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            var part = copy + ".part";
            File.Copy(source, part, overwrite: true);
            File.SetLastWriteTimeUtc(part, src.LastWriteTimeUtc);
            File.Move(part, copy, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            notes.Add($"could not copy the APK: {ex.Message}; using {source}");
            return false;
        }
    }

    /// <summary>A directory junction <paramref name="link"/> to <paramref name="target"/> (mklink /J
    /// needs no administrator rights). An existing junction to the same target is kept; a folder that
    /// is not a junction is never touched.</summary>
    private static bool EnsureJunction(string link, string target, List<string> notes)
    {
        try
        {
            var di = new DirectoryInfo(link);
            if (di.Exists)
            {
                var to = di.LinkTarget;
                if (to == null)
                {
                    notes.Add($"{link} is a folder, not a junction; using the runtime where it is");
                    return false;
                }
                if (SamePath(to, target)) return true;
                Directory.Delete(link); // the junction only, never what it points to
            }
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            di.Refresh();
            if (di.Exists && di.LinkTarget != null) return true;
            notes.Add($"could not make the junction {link}: {err.Trim()}; using the runtime where it is");
            return false;
        }
        catch (Exception ex)
        {
            notes.Add($"could not make the junction {link}: {ex.Message}; using the runtime where it is");
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        static string N(string s) => Path.TrimEndingDirectorySeparator(s.StartsWith(@"\??\") ? s[4..] : s);
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    // ----- the runtime's manifest -----

    private static readonly Dictionary<string, bool> ManifestCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the exe's manifest sets activeCodePage to UTF-8.</summary>
    public static bool HasUtf8Manifest(string exe)
    {
        string key;
        try
        {
            var fi = new FileInfo(exe);
            if (!fi.Exists) return false;
            key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return false; }
        lock (ManifestCache)
        {
            if (ManifestCache.TryGetValue(key, out var known)) return known;
            var has = ManifestSaysUtf8(ReadManifest(exe));
            ManifestCache[key] = has;
            return has;
        }
    }

    internal static bool ManifestSaysUtf8(string? xml) =>
        xml != null && System.Text.RegularExpressions.Regex.IsMatch(xml,
            @"<activeCodePage\s+xmlns=""http://schemas\.microsoft\.com/SMI/2019/WindowsSettings""\s*>\s*UTF-8\s*</activeCodePage>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? ReadManifest(string exe)
    {
        var module = LoadLibraryExW(exe, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero) return null;
        try
        {
            var res = FindResourceW(module, (IntPtr)1, (IntPtr)24); // CREATEPROCESS_MANIFEST_RESOURCE_ID, RT_MANIFEST
            if (res == IntPtr.Zero) return null;
            var size = SizeofResource(module, res);
            var data = LoadResource(module, res);
            var ptr = data == IntPtr.Zero ? IntPtr.Zero : LockResource(data);
            if (ptr == IntPtr.Zero || size == 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, (int)size);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { FreeLibrary(module); }
    }

    // ----- self-test -----

    /// <summary>The code-page check against fixed code pages, and the moves for a fake one.</summary>
    public static List<string> SelfTest()
    {
        var failures = new List<string>();
        void Expect(string what, bool got, bool want) { if (got != want) failures.Add($"PathSafety {what}: got {got}, want {want}"); }
        void Same(string what, string? got, string? want) { if (got != want) failures.Add($"PathSafety {what}: got {got ?? "null"}, want {want ?? "null"}"); }

        // Sample profile paths on a placeholder drive: the release's leak check rejects any
        // "C:\Users\" string in the binaries, even a made-up one.
        const string cyr = "X:\\Profiles\\\u041A\u0438\u0440\u0438\u043B\u043B\\AppData\\Local";   // Кирилл
        const string heb = "X:\\Profiles\\\u05DE\u05E9\u05EA\u05DE\u05E9\\AppData\\Local";           // משתמש ("user")
        Expect("ascii in 1252", Survives(@"X:\Profiles\user\AppData\Local", 1252), true);
        Expect("Cyrillic in 1255", Survives(cyr, 1255), false);
        Expect("Cyrillic in 1251", Survives(cyr, 1251), true);
        Expect("Hebrew in 1255", Survives(heb, 1255), true);
        Expect("Hebrew in 1252", Survives(heb, 1252), false);
        Expect("Hebrew in UTF-8", Survives(heb, 65001), true);
        // Best fit would turn \u011C into G; the check must not accept that.
        Expect("best fit refused", Survives("C:\\\u011Ctest", 1252), false);
        Expect("UTF-8 manifest", ManifestSaysUtf8(
            "<activeCodePage xmlns=\"http://schemas.microsoft.com/SMI/2019/WindowsSettings\">UTF-8</activeCodePage>"), true);
        Expect("no manifest", ManifestSaysUtf8(null), false);
        Expect("other namespace", ManifestSaysUtf8(
            "<activeCodePage xmlns=\"http://schemas.microsoft.com/SMI/2019/WindowsSettingX\">UTF-8</activeCodePage>"), false);

        // A profile under a Hebrew user name, on an English system (code page 1252).
        var p = new Profile
        {
            Id = "0123456789abcdef0123456789abcdef",
            Name = "Notes",
            ApkPath = "X:\\Profiles\\\u05DE\u05E9\u05EA\u05DE\u05E9\\Downloads\\\u05E4\u05EA\u05E7\u05D9\u05DD.apk",  // פתקים.apk
            DataRoot = heb + "\\WinApk Launcher\\apps\\Notes",
        };
        var r = Resolve(p, heb + "\\Programs\\WinApk Launcher\\runtime\\bin\\winapk-runtime.exe", prepare: false,
            cp: 1252, root: @"C:\WinApk Launcher", systemTemp: heb + "\\Temp\\");
        Same("moved data root", r.DataRoot, @"C:\WinApk Launcher\apps\Notes-01234567");
        Same("moved temp", r.TempDir, @"C:\WinApk Launcher\temp");
        Expect("APK copy under apks", r.ApkPath.StartsWith(@"C:\WinApk Launcher\apks\01234567\", StringComparison.Ordinal), true);
        Expect("APK copy name holds in 1252", Survives(r.ApkPath, 1252), true);
        Expect("APK copy is an .apk", r.ApkPath.EndsWith(".apk", StringComparison.Ordinal), true);
        Expect("runtime through a junction", r.RuntimeExe.StartsWith(@"C:\WinApk Launcher\runtime-", StringComparison.Ordinal)
                                           && r.RuntimeExe.EndsWith(@"\bin\winapk-runtime.exe", StringComparison.Ordinal), true);
        Expect("four notes", r.Notes.Count == 4, true);
        // The same profile on a Hebrew system (1255): nothing moves.
        var same = Resolve(p, heb + "\\Programs\\WinApk Launcher\\runtime\\bin\\winapk-runtime.exe", prepare: false,
            cp: 1255, root: @"C:\WinApk Launcher", systemTemp: heb + "\\Temp\\");
        Expect("nothing moves in 1255", same.Moved, false);
        Same("data root kept", same.DataRoot, p.DataRoot);
        Same("APK kept", same.ApkPath, p.ApkPath);
        Same("temp kept", same.TempDir, null);
        Same("safe name", SafeName("\u05E4\u05EA\u05E7\u05D9\u05DD", 1252), "app");
        Same("safe name mixed", SafeName("Notes \u05D0", 1252), "Notes _");
        return failures;
    }

    // ----- Win32 -----

    private const uint WC_NO_BEST_FIT_CHARS = 0x400;
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    private static extern int WideCharToMultiByte(uint codePage, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string src,
        int srcLen, byte[]? dst, int dstLen, IntPtr defaultChar, out int usedDefaultChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] // char[] as UTF-16
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] src, int srcLen,
        [Out] char[]? dst, int dstLen);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr res);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr res);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr data);
}
