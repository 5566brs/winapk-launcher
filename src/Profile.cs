using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApkLauncher;

/// <summary>Upstream proxy kinds a profile can route its Java traffic through.</summary>
public enum ProxyKind { None, Socks5, Socks4, Http }

/// <summary>A resolved upstream proxy (plaintext password), used by the adapter and the Test button.</summary>
public sealed record ProxyConfig(ProxyKind Kind, string Host, int Port, string User, string Password)
{
    public bool HasAuth => !string.IsNullOrEmpty(User);
}

/// <summary>
/// A launch profile = an APK plus a settings bundle (its own data root and the
/// env/flags the runtime reads). Several profiles may point at the same APK with
/// different data roots; that is how you run independent instances / accounts.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";

    /// <summary>Absolute path to the .apk/.xapk/.apkm/.apks passed as --apk.</summary>
    public string ApkPath { get; set; } = "";

    /// <summary>Per-profile data root passed as --data (library, apps, android, logs live here).</summary>
    public string DataRoot { get; set; } = "";

    // Emulated phone size in dp -> WINAPK_PHONE_SIZE=<w>x<h>. Runtime default is 360x800.
    public int PhoneWidthDp { get; set; } = 360;
    public int PhoneHeightDp { get; set; } = 800;

    /// <summary>Android API level -> MACAPK_SDK_INT. 0 means leave unset (runtime default, API 29).</summary>
    public int SdkInt { get; set; } = 0;

    /// <summary>Optional display density dpi -> WINAPK_DENSITY. 0 means auto (derive from the monitor).</summary>
    public int DensityDpi { get; set; } = 0;

    /// <summary>Extra JVM options, space-separated -> MACAPK_JVM_OPTS. Empty leaves it unset.</summary>
    public string JvmOpts { get; set; } = "";

    /// <summary>MACAPK_LOG_LEVEL: 0 = unset (all), 2=V 3=D 4=I 5=W 6=E. The runtime's own default is 0.</summary>
    public int LogLevel { get; set; } = 0;

    /// <summary>
    /// Allow more than one live instance. The runtime runs one copy per app folder and refuses a
    /// second with exit code 3; this sets WINAPK_ALLOW_SECOND_INSTANCE=1, which turns that off.
    /// Launch then starts another copy once the running one has its window (never while it is
    /// still starting). Independent accounts still need distinct data roots.
    /// </summary>
    public bool AllowMultipleInstances { get; set; } = false;

    // ----- per-app network proxy (routes the app's Java traffic through a local adapter) -----

    /// <summary>"None", "Socks5", "Socks4" or "Http". Unknown/missing = None (backward compatible).</summary>
    public string ProxyType { get; set; } = "None";

    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyUser { get; set; } = "";

    /// <summary>
    /// The proxy password, encrypted with Windows DPAPI (CurrentUser) and base64-encoded.
    /// NEVER plaintext on disk. Use <see cref="GetProxyPassword"/> / <see cref="SetProxyPassword"/>.
    /// </summary>
    public string ProxyPassword { get; set; } = "";

    [JsonIgnore]
    public ProxyKind ProxyKind => ProxyType?.Trim().ToLowerInvariant() switch
    {
        "socks5" => ProxyKind.Socks5,
        "socks4" => ProxyKind.Socks4,
        "http" => ProxyKind.Http,
        _ => ProxyKind.None,
    };

    [JsonIgnore]
    public bool HasProxy => ProxyKind != ProxyKind.None && !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort is > 0 and < 65536;

    /// <summary>Decrypt the stored password; returns "" if unset or undecryptable (e.g. copied from another user).</summary>
    public string GetProxyPassword() => Dpapi.Unprotect(ProxyPassword);

    /// <summary>Encrypt and store a plaintext password (empty clears it).</summary>
    public void SetProxyPassword(string plain) => ProxyPassword = Dpapi.Protect(plain);

    /// <summary>The upstream config the adapter/Test should use, or null when no usable proxy is set.</summary>
    public ProxyConfig? ResolveProxy()
    {
        if (!HasProxy) return null;
        return new ProxyConfig(ProxyKind, ProxyHost.Trim(), ProxyPort, ProxyUser?.Trim() ?? "", GetProxyPassword());
    }

    /// <summary>Short label for UI ("socks5 host:1080") or "" when no proxy.</summary>
    public string ProxyLabel => HasProxy ? $"{ProxyKind.ToString().ToLowerInvariant()} {ProxyHost.Trim()}:{ProxyPort}" : "";
}

/// <summary>Windows DPAPI (CurrentUser) string protection via crypt32.dll. No external packages.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pIn, string? desc, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pIn, IntPtr desc, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>Plaintext -> base64 of a DPAPI blob. "" stays "".</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = handle.AddrOfPinnedObject();
            if (!CryptProtectData(ref inBlob, "WinApkLauncherProxy", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return "";
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(outBytes);
        }
        catch { return ""; }
        finally
        {
            if (handle.IsAllocated) handle.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>base64 DPAPI blob -> plaintext. "" or anything undecryptable -> "".</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        byte[] data;
        try { data = Convert.FromBase64String(stored); } catch { return ""; }
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = handle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return "";
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        catch { return ""; }
        finally
        {
            if (handle.IsAllocated) handle.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}

/// <summary>Where <see cref="LauncherStore.ResolveRuntime"/> found winapk-runtime.exe.</summary>
public enum RuntimeSource { None, Settings, Bundled }

/// <summary>Everything persisted to profiles.json (see <see cref="AppPaths"/> for where it lives).</summary>
public sealed class LauncherStore
{
    /// <summary>
    /// A user-chosen winapk-runtime.exe ("Runtime path..."). Empty means "use the runtime bundled
    /// next to the launcher"; see <see cref="ResolveRuntime"/>.
    /// </summary>
    public string RuntimeExe { get; set; } = "";

    public List<Profile> Profiles { get; set; } = new();

    /// <summary>
    /// Main window layout: "Tiles" (icon grid, the default) or "List" (detail rows).
    /// Older files without this field open in Tiles.
    /// </summary>
    public string ViewMode { get; set; } = ViewTiles;

    public const string ViewTiles = "Tiles";
    public const string ViewList = "List";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string StorePath => AppPaths.StorePath;

    /// <summary>
    /// The runtime to launch: the RuntimeExe from the settings if it is set and exists, else the
    /// runtime bundled at &lt;launcher dir&gt;\runtime\bin\winapk-runtime.exe, else null (the GUI then
    /// asks for it with the "Runtime path..." dialog).
    /// </summary>
    public string? ResolveRuntime() => ResolveRuntime(out _);

    public string? ResolveRuntime(out RuntimeSource source)
    {
        if (!string.IsNullOrWhiteSpace(RuntimeExe) && File.Exists(RuntimeExe.Trim()))
        {
            source = RuntimeSource.Settings;
            return RuntimeExe.Trim();
        }
        if (File.Exists(AppPaths.BundledRuntimeExe))
        {
            source = RuntimeSource.Bundled;
            return AppPaths.BundledRuntimeExe;
        }
        source = RuntimeSource.None;
        return null;
    }

    /// <summary>One line for --list / --dry-run / About: the runtime path and how it was found.</summary>
    public string DescribeRuntime()
    {
        var exe = ResolveRuntime(out var src);
        return src switch
        {
            RuntimeSource.Settings => $"{exe}  (from settings)",
            RuntimeSource.Bundled => $"{exe}  (bundled next to the launcher)",
            _ => "NOT FOUND" +
                 (string.IsNullOrWhiteSpace(RuntimeExe) ? "" : $" (settings: {RuntimeExe}, missing)") +
                 $"; looked for {AppPaths.BundledRuntimeExe}",
        };
    }

    public static LauncherStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                var store = JsonSerializer.Deserialize<LauncherStore>(json, JsonOpts);
                if (store != null)
                {
                    store.Profiles ??= new();
                    store.RuntimeExe ??= "";
                    store.ViewMode = string.Equals(store.ViewMode, ViewList, StringComparison.OrdinalIgnoreCase)
                        ? ViewList : ViewTiles;
                    return store;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file should not wipe anything silently; back it up and start fresh.
            try { File.Copy(StorePath, StorePath + ".bad", overwrite: true); } catch { }
            Console.Error.WriteLine($"[winapk-launcher] could not read {StorePath}: {ex.Message}");
        }
        return new LauncherStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, StorePath, overwrite: true);
    }

    public Profile? Find(string nameOrId) =>
        Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, nameOrId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The default data root for a new profile: &lt;apps dir&gt;\&lt;sanitized name&gt;, made unique among
    /// the existing profiles (each profile needs its own data root). <paramref name="exceptId"/> skips
    /// the profile being edited.
    /// </summary>
    public string SuggestDataRoot(string name, string? exceptId = null)
    {
        var baseName = RuntimeLauncher.MakeFileSafe(string.IsNullOrWhiteSpace(name) ? "app" : name.Trim());
        var taken = new HashSet<string>(
            Profiles.Where(p => p.Id != exceptId && !string.IsNullOrWhiteSpace(p.DataRoot))
                    .Select(p => Path.TrimEndingDirectorySeparator(p.DataRoot.Trim())),
            StringComparer.OrdinalIgnoreCase);
        var candidate = Path.Combine(AppPaths.AppsDir, baseName);
        for (int n = 2; taken.Contains(candidate); n++)
            candidate = Path.Combine(AppPaths.AppsDir, $"{baseName} ({n})");
        return candidate;
    }
}
