using System.Reflection;

namespace WinApkLauncher;

/// <summary>Product name and version, as stamped on the assembly by the build (-p:Version=...).</summary>
internal static class AppInfo
{
    public const string ProductName = "WinApk Launcher";
    public const string ExeName = "WinApkLauncher.exe";
    public const string ProjectUrl = "https://github.com/5566brs/winapk-launcher";

    /// <summary>The InformationalVersion without any "+build" metadata, e.g. "0.1.0" or "0.0.0-dev".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var v = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) v = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}

/// <summary>
/// Where the launcher keeps its files.
///
/// Installed (the default):
///   settings  %APPDATA%\WinApk Launcher\profiles.json
///   cache     %LOCALAPPDATA%\WinApk Launcher\cache   (app icons, shortcut icons)
///   logs      %LOCALAPPDATA%\WinApk Launcher\logs    (one log per launch)
///   app data  %LOCALAPPDATA%\WinApk Launcher\apps\&lt;profile&gt;  (default data root of a new profile)
///
/// Portable (a file named portable.txt next to the exe): the same layout, all of it next to the exe.
/// </summary>
internal static class AppPaths
{
    /// <summary>The folder of WinApkLauncher.exe (with a trailing separator).</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>True when portable.txt sits next to the exe. Read once per process.</summary>
    public static bool Portable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    /// <summary>Roaming settings root (profiles.json).</summary>
    public static string SettingsDir => Portable
        ? Path.TrimEndingDirectorySeparator(ExeDir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.ProductName);

    /// <summary>Machine-local root (cache, logs, app data).</summary>
    public static string LocalDir => Portable
        ? Path.TrimEndingDirectorySeparator(ExeDir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ProductName);

    public static string StorePath => Path.Combine(SettingsDir, "profiles.json");
    public static string CacheDir => Path.Combine(LocalDir, "cache");
    public static string IconCacheDir => Path.Combine(CacheDir, "icons");
    public static string LogsDir => Path.Combine(LocalDir, "logs");
    public static string AppsDir => Path.Combine(LocalDir, "apps");

    /// <summary>The runtime the installer and the portable zip put next to the launcher.</summary>
    public static string BundledRuntimeExe => Path.Combine(ExeDir, "runtime", "bin", "winapk-runtime.exe");

    /// <summary>Where the bundled runtime came from (written by the release workflow), or null.</summary>
    public static string BundledRuntimeSourceFile => Path.Combine(ExeDir, "runtime", "SOURCE.txt");

    public static string ModeLabel => Portable ? L.ModePortable : L.ModeInstalled;
}
