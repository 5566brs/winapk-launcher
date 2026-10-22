using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinApkLauncher;

/// <summary>
/// App icons for profiles, extracted best-effort from the .apk/.xapk/.apkm/.apks and
/// cached as PNG in the launcher's cache folder (cache\icons). The cache key covers the
/// file's path, size and timestamp, so a replaced APK gets a fresh icon. Files with
/// no usable icon get a ".none" marker and the UI draws a letter tile instead.
/// Nothing here throws to the caller.
/// </summary>
public static class AppIcons
{
    private const string CacheVersion = "v2";
    public const int CachePx = 192;

    public static string CacheDir => AppPaths.IconCacheDir;

    private static readonly ConcurrentDictionary<string, Image?> Memory = new();
    private static readonly ConcurrentDictionary<string, byte> Loading = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Key)> Keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on a thread-pool thread whenever a background extraction finishes.</summary>
    public static event Action? IconReady;

    public static int PendingCount => Loading.Count;

    /// <summary>Cache key for the current version of the file, or null when it doesn't exist.</summary>
    public static string? KeyFor(string? apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath)) return null;
        var path = apkPath.Trim();
        if (Keys.TryGetValue(path, out var k) && DateTime.UtcNow - k.At < TimeSpan.FromSeconds(5)) return k.Key;
        string? key = null;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                var s = $"{CacheVersion}|{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20].ToLowerInvariant();
            }
        }
        catch { }
        Keys[path] = (DateTime.UtcNow, key);
        return key;
    }

    /// <summary>
    /// The icon for <paramref name="apkPath"/> if it is known (memory or disk cache).
    /// Otherwise returns null and, once per file version, extracts it in the background,
    /// then raises <see cref="IconReady"/>. Null also means "this file has no usable icon".
    /// </summary>
    public static Image? Get(string? apkPath)
    {
        var key = KeyFor(apkPath);
        if (key == null) return null;
        if (Memory.TryGetValue(key, out var img)) return img;
        if (Loading.ContainsKey(key)) return null;

        // A disk-cache hit is cheap enough to serve synchronously (no placeholder flash).
        var png = Path.Combine(CacheDir, key + ".png");
        try
        {
            if (File.Exists(png))
            {
                var cached = LoadPng(png);
                if (cached != null) { Memory[key] = cached; return cached; }
            }
            else if (File.Exists(Path.Combine(CacheDir, key + ".none")))
            {
                Memory[key] = null;
                return null;
            }
        }
        catch { }

        if (Loading.TryAdd(key, 0))
        {
            var path = apkPath!.Trim();
            Task.Run(() =>
            {
                Image? result = null;
                try { result = Extract(path, key); } catch { }
                Memory[key] = result;
                Loading.TryRemove(key, out _);
                try { IconReady?.Invoke(); } catch { }
            });
        }
        return null;
    }

    private static Bitmap? Extract(string path, string key)
    {
        Bitmap? bmp = null;
        try { bmp = IconExtractor.Extract(path); } catch { bmp = null; }
        try
        {
            Directory.CreateDirectory(CacheDir);
            var png = Path.Combine(CacheDir, key + ".png");
            if (bmp != null)
            {
                var tmp = png + ".tmp";
                bmp.Save(tmp, ImageFormat.Png);
                File.Move(tmp, png, overwrite: true);
            }
            else
            {
                File.WriteAllText(Path.Combine(CacheDir, key + ".none"), path);
            }
        }
        catch { }
        return bmp;
    }

    private static Bitmap? LoadPng(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img); // detached from the stream
        }
        catch { return null; }
    }

    // ----- letter tiles (fallback) -----

    private static readonly Color[] LetterColors =
    {
        Color.FromArgb(0x00, 0x78, 0xD4), Color.FromArgb(0x88, 0x17, 0x98), Color.FromArgb(0xC2, 0x39, 0xB3),
        Color.FromArgb(0xE3, 0x00, 0x8C), Color.FromArgb(0xD1, 0x34, 0x38), Color.FromArgb(0xCA, 0x50, 0x10),
        Color.FromArgb(0x98, 0x6F, 0x0B), Color.FromArgb(0x49, 0x82, 0x05), Color.FromArgb(0x10, 0x7C, 0x10),
        Color.FromArgb(0x00, 0x8B, 0x7A), Color.FromArgb(0x03, 0x83, 0x87), Color.FromArgb(0x00, 0x5B, 0x70),
        Color.FromArgb(0x4F, 0x6B, 0xED), Color.FromArgb(0x5C, 0x2E, 0x91), Color.FromArgb(0x69, 0x79, 0x7E),
        Color.FromArgb(0x7A, 0x75, 0x74),
    };

    /// <summary>A stable color for a name (FNV-1a; string.GetHashCode is randomised per process).</summary>
    public static Color ColorForName(string name)
    {
        uint h = 2166136261;
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        return LetterColors[h % (uint)LetterColors.Length];
    }

    public static string LetterFor(string name)
    {
        foreach (var ch in name ?? "")
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "?";
    }

    /// <summary>Rounded colored square with the name's first letter, drawn at <paramref name="px"/>.</summary>
    public static Bitmap LetterTile(string name, int px, string fontFamily)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var c = ColorForName(name);
        var r = new RectangleF(0, 0, px, px);
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, px - 1f, px - 1f), px * 0.22f))
        using (var br = new LinearGradientBrush(r, Theme.Blend(c, Color.White, 0.12f), Theme.Blend(c, Color.Black, 0.12f), 90f))
            g.FillPath(br, path);
        using var font = new Font(fontFamily, px * 0.46f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Color.White);
        g.DrawString(LetterFor(name), font, tb, new RectangleF(0, px * 0.02f, px, px), sf);
        return bmp;
    }

    /// <summary>High-quality downscale of an icon to a square of <paramref name="px"/>.</summary>
    public static Bitmap Scaled(Image src, int px)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        float s = Math.Min((float)px / src.Width, (float)px / src.Height);
        float w = src.Width * s, h = src.Height * s;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY); // no faded fringe at the edges
        g.DrawImage(src, new Rectangle((int)Math.Round((px - w) / 2), (int)Math.Round((px - h) / 2), (int)Math.Round(w), (int)Math.Round(h)),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return bmp;
    }
}

/// <summary>
/// Finds and renders the launcher icon inside an Android package:
/// AndroidManifest.xml (binary XML) → application@icon → resources.arsc →
/// best-density PNG/WebP, or an adaptive-icon / vector drawable rendered with GDI+.
/// Falls back to filename heuristics (res/mipmap-*/ic_launcher*.png).
/// </summary>
internal static class IconExtractor
{
    /// <summary>Diagnostics for the --icon CLI. Not used by the GUI.</summary>
    public static TextWriter? Trace;

    /// <summary>--icon ... --xml: try the adaptive / vector drawable before any bitmap (renderer check).</summary>
    public static bool PreferXml;
    private static void Log(string s) => Trace?.WriteLine("  " + s);

    private const long MaxNestedApk = 512L * 1024 * 1024;

    public static Bitmap? Extract(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        if (zip.GetEntry("AndroidManifest.xml") != null)
        {
            Log("plain APK");
            var set = new PackageSet();
            set.Add(zip, "apk");
            return FromPackage(set);
        }
        Log("bundle (XAPK/APKM/APKS)");
        return FromBundle(zip);
    }

    // ----- bundles -----

    private static Bitmap? FromBundle(ZipArchive zip)
    {
        // XAPK / APKM usually carry the store icon at the root.
        foreach (var e in zip.Entries)
        {
            if (e.FullName.Contains('/')) continue;
            var n = e.Name.ToLowerInvariant();
            if (n.StartsWith("icon") && (n.EndsWith(".png") || n.EndsWith(".webp") || n.EndsWith(".jpg")))
            {
                var img = Images.Decode(ReadAll(e, 32 * 1024 * 1024));
                if (img != null) { Log($"root icon {e.FullName} {img.Width}x{img.Height}"); return Images.Normalize(img); }
            }
        }

        var apks = zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (apks.Count == 0) { Log("no .apk inside"); return null; }

        string? baseName = null;
        try
        {
            var mf = zip.GetEntry("manifest.json");
            if (mf != null)
            {
                using var doc = JsonDocument.Parse(ReadAll(mf, 4 * 1024 * 1024) ?? Array.Empty<byte>());
                if (doc.RootElement.TryGetProperty("split_apks", out var splits) && splits.ValueKind == JsonValueKind.Array)
                    foreach (var s in splits.EnumerateArray())
                        if (s.TryGetProperty("id", out var id) && id.GetString() == "base" &&
                            s.TryGetProperty("file", out var f)) baseName = f.GetString();
            }
        }
        catch { }

        static bool IsConfigSplit(string n) =>
            Path.GetFileName(n).StartsWith("config.", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(n).StartsWith("split_config.", StringComparison.OrdinalIgnoreCase);

        var baseEntry = (baseName != null ? zip.GetEntry(baseName) : null)
            ?? apks.FirstOrDefault(e => e.Name.Equals("base.apk", StringComparison.OrdinalIgnoreCase))
            ?? apks.Where(e => !IsConfigSplit(e.FullName)).OrderByDescending(e => e.Length).FirstOrDefault()
            ?? apks.OrderByDescending(e => e.Length).First();
        Log($"base apk: {baseEntry.FullName} ({baseEntry.Length / 1024} KB)");

        var set = new PackageSet();
        try
        {
            var baseZip = OpenNested(baseEntry);
            if (baseZip == null) return null;
            set.Add(baseZip, baseEntry.FullName, owns: true);
            // Density splits (config.xxhdpi.apk) hold the high-res mipmaps in bundletool output.
            foreach (var e in apks)
            {
                if (e == baseEntry || !IsConfigSplit(e.FullName) || !e.Name.Contains("dpi", StringComparison.OrdinalIgnoreCase)) continue;
                var z = OpenNested(e);
                if (z != null) { set.Add(z, e.FullName, owns: true); Log($"density split: {e.FullName}"); }
            }
            return FromPackage(set);
        }
        finally { set.Dispose(); }
    }

    private static ZipArchive? OpenNested(ZipArchiveEntry e)
    {
        if (e.Length > MaxNestedApk) { Log($"skip {e.FullName}: too large"); return null; }
        var bytes = ReadAll(e, MaxNestedApk);
        if (bytes == null) return null;
        try { return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read); }
        catch { return null; }
    }

    internal static byte[]? ReadAll(ZipArchiveEntry e, long max)
    {
        try
        {
            if (e.Length > max) return null;
            using var s = e.Open();
            var ms = new MemoryStream((int)Math.Max(0, Math.Min(e.Length, int.MaxValue)));
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ----- one package (base + density splits) -----

    private static Bitmap? FromPackage(PackageSet set)
    {
        try
        {
            var manifestBytes = set.Read("AndroidManifest.xml");
            var manifest = manifestBytes != null ? Axml.Parse(manifestBytes) : null;
            if (manifest != null)
            {
                var res = new Resources(set);
                Log($"resource tables: {res.TableCount}");
                foreach (var id in IconIds(manifest))
                {
                    Log($"icon resource 0x{id:X8}");
                    var bmp = res.RenderIcon(id, AppIcons.CachePx);
                    if (bmp != null) return bmp;
                }
            }
            else Log("manifest unreadable");
        }
        catch (Exception ex) { Log("resource path failed: " + ex.Message); }

        return Heuristic(set);
    }

    /// <summary>application@icon, application@roundIcon, then the launcher activity's icon.</summary>
    private static IEnumerable<uint> IconIds(AxEl manifest)
    {
        var ids = new List<uint>();
        void Add(AxAttr? a) { if (a is { Type: Res.TypeReference or Res.TypeDynamicReference } && a.Data != 0 && !ids.Contains(a.Data)) ids.Add(a.Data); }
        var app = manifest.Descendants("application").FirstOrDefault();
        Add(app?.A("icon", Res.AttrIcon));
        Add(app?.A("roundIcon", Res.AttrRoundIcon));
        if (app != null)
            foreach (var act in app.Children.Where(c => c.Name is "activity" or "activity-alias"))
                if (act.Descendants("category").Any(c => c.A("name", Res.AttrName)?.Raw == "android.intent.category.LAUNCHER"))
                {
                    Add(act.A("icon", Res.AttrIcon));
                    Add(act.A("roundIcon", Res.AttrRoundIcon));
                }
        return ids;
    }

    private static readonly Regex IconName = new(
        @"^res/(mipmap|drawable)(-[^/]+)?/(ic_launcher|ic_launcher_round|app_icon|icon|launcher_icon|ic_app)(_foreground)?\.(png|webp)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>No manifest/resource path: largest well-known launcher bitmap by name.</summary>
    private static Bitmap? Heuristic(PackageSet set)
    {
        var cands = new List<(string Name, int Score, bool Foreground)>();
        foreach (var name in set.EntryNames)
        {
            var m = IconName.Match(name);
            if (!m.Success) continue;
            int dpi = DensityFromQualifiers(m.Groups[2].Value);
            bool fg = m.Groups[4].Success;
            bool round = m.Groups[3].Value.Contains("round", StringComparison.OrdinalIgnoreCase);
            cands.Add((name, dpi * 4 - (fg ? 2 : 0) - (round ? 1 : 0), fg));
        }
        foreach (var c in cands.OrderByDescending(c => c.Score))
        {
            var img = Images.Decode(set.Read(c.Name));
            if (img == null) continue;
            Log($"heuristic: {c.Name} {img.Width}x{img.Height}");
            if (!c.Foreground) return Images.Normalize(img);
            // An adaptive foreground alone: put it on a white rounded tile like a launcher would.
            using (img) return Adaptive.Compose(AppIcons.CachePx,
                (g, r) => { g.Clear(Color.White); return true; },
                (g, r) => { Images.Draw(g, img, r); return true; });
        }
        Log("heuristic: nothing");
        return null;
    }

    private static int DensityFromQualifiers(string q)
    {
        q = q.ToLowerInvariant();
        if (q.Contains("xxxhdpi")) return 640;
        if (q.Contains("xxhdpi")) return 480;
        if (q.Contains("xhdpi")) return 320;
        if (q.Contains("hdpi")) return 240;
        if (q.Contains("mdpi")) return 160;
        if (q.Contains("ldpi")) return 120;
        if (q.Contains("nodpi")) return 480;
        return 160;
    }

    // ===================================================================================
    // Resource resolution and drawable rendering
    // ===================================================================================

    private sealed class Resources
    {
        private readonly PackageSet _set;
        private readonly List<ResTable> _tables = new();
        private readonly Dictionary<string, AxEl?> _xml = new();

        public Resources(PackageSet set)
        {
            _set = set;
            foreach (var bytes in set.ReadAll("resources.arsc"))
            {
                try { _tables.Add(new ResTable(bytes)); } catch (Exception ex) { Log("arsc: " + ex.Message); }
            }
        }

        public int TableCount => _tables.Count;

        public List<ResEntry> Lookup(uint id)
        {
            var list = new List<ResEntry>();
            foreach (var t in _tables) list.AddRange(t.Lookup(id));
            return list;
        }

        /// <summary>Files / colors behind a resource id, following references.</summary>
        private void Collect(uint id, List<ResEntry> into, int depth)
        {
            if (depth > 8) return;
            foreach (var e in Lookup(id))
            {
                if (e.Type is Res.TypeReference or Res.TypeDynamicReference && e.Data != 0) Collect(e.Data, into, depth + 1);
                else into.Add(e);
            }
        }

        private static bool IsRaster(string p) =>
            p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);

        private static bool IsXml(string p) => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        public Bitmap? RenderIcon(uint id, int px)
        {
            var all = new List<ResEntry>();
            Collect(id, all, 0);
            var files = all.Where(e => e.Type == Res.TypeString && e.Str != null).ToList();
            foreach (var f in files) Log($"  candidate {f.Str} density={f.Config.Density} sdk={f.Config.Sdk}{(f.Config.Qualified ? " (qualified)" : "")}");

            Bitmap? raster = null;
            foreach (var f in files.Where(f => IsRaster(f.Str!)).OrderBy(f => f.Config.Qualified).ThenByDescending(f => f.Config.EffectiveDensity))
            {
                raster = Images.Decode(_set.Read(f.Str!));
                if (raster != null) { Log($"  raster {f.Str} {raster.Width}x{raster.Height}"); break; }
                Log($"  could not decode {f.Str}");
            }
            if (raster != null && Math.Min(raster.Width, raster.Height) >= 96 && !PreferXml) return Images.Normalize(raster);

            foreach (var f in files.Where(f => IsXml(f.Str!)).OrderBy(f => f.Config.Qualified).ThenByDescending(f => f.Config.Sdk))
            {
                var el = Xml(f.Str!);
                if (el == null) continue;
                Log($"  xml {f.Str} <{el.Name}>");
                Bitmap? bmp = null;
                try { bmp = RenderRoot(el, px); } catch (Exception ex) { Log("  render failed: " + ex.Message); }
                if (bmp != null && !Images.IsBlank(bmp)) { raster?.Dispose(); return bmp; }
                bmp?.Dispose();
            }
            return raster != null ? Images.Normalize(raster) : null;
        }

        private Bitmap? RenderRoot(AxEl el, int px)
        {
            if (el.Name is "adaptive-icon" or "maskable-icon")
            {
                var bg = el.Child("background");
                var fg = el.Child("foreground");
                if (fg == null) return null;
                return Adaptive.Compose(px,
                    (g, r) => bg != null && DrawLayer(g, r, bg, r.Width / 108f, 0),
                    (g, r) => DrawLayer(g, r, fg, r.Width / 108f, 0));
            }
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Images.Quality(g);
                if (!DrawElement(g, new RectangleF(0, 0, px, px), el, px / 48f, 0)) { bmp.Dispose(); return null; }
            }
            return bmp;
        }

        private bool DrawLayer(Graphics g, RectangleF r, AxEl layer, float dpPx, int depth)
        {
            var a = layer.A("drawable", Res.AttrDrawable);
            if (a != null) return DrawValue(g, r, a.Type, a.Data, a.Raw, dpPx, depth + 1);
            var child = layer.Children.FirstOrDefault();
            return child != null && DrawElement(g, r, child, dpPx, depth + 1);
        }

        private AxEl? Xml(string path)
        {
            if (_xml.TryGetValue(path, out var el)) return el;
            var b = _set.Read(path);
            el = b != null ? Axml.Parse(b) : null;
            _xml[path] = el;
            return el;
        }

        /// <summary>Best single entry: default (unqualified) config, highest density.</summary>
        private ResEntry? Best(uint id, bool preferXml)
        {
            var all = new List<ResEntry>();
            Collect(id, all, 0);
            return all
                .OrderBy(e => e.Config.Qualified)
                .ThenByDescending(e => preferXml && e.Str != null && IsXml(e.Str))
                .ThenByDescending(e => e.Config.EffectiveDensity)
                .FirstOrDefault();
        }

        private bool DrawValue(Graphics g, RectangleF r, byte type, uint data, string? raw, float dpPx, int depth)
        {
            if (depth > 12) return false;
            if (Res.IsColor(type)) { Fill(g, r, Res.ToColor(type, data)); return true; }
            if (type is Res.TypeReference or Res.TypeDynamicReference)
            {
                if (Res.Framework(data) is Color fc) { Fill(g, r, fc); return true; }
                var e = Best(data, preferXml: false);
                if (e == null) return false;
                return DrawValue(g, r, e.Type, e.Data, e.Str, dpPx, depth + 1);
            }
            if (type == Res.TypeString && raw != null)
            {
                if (raw.StartsWith('#') && Res.ParseHex(raw) is Color hc) { Fill(g, r, hc); return true; }
                return DrawFile(g, r, raw, dpPx, depth + 1);
            }
            return false;
        }

        private static void Fill(Graphics g, RectangleF r, Color c)
        {
            using var b = new SolidBrush(c);
            g.FillRectangle(b, r);
        }

        private bool DrawFile(Graphics g, RectangleF r, string path, float dpPx, int depth)
        {
            if (IsRaster(path))
            {
                using var img = Images.Decode(_set.Read(path));
                if (img == null) { Log($"  could not decode {path}"); return false; }
                Images.Draw(g, img, r);
                return true;
            }
            if (IsXml(path))
            {
                var el = Xml(path);
                return el != null && DrawElement(g, r, el, dpPx, depth + 1);
            }
            return false;
        }

        private bool DrawChildOrAttr(Graphics g, RectangleF r, AxEl el, float dpPx, int depth, string attr = "drawable", uint id = Res.AttrDrawable)
        {
            var a = el.A(attr, id);
            if (a != null && DrawValue(g, r, a.Type, a.Data, a.Raw, dpPx, depth + 1)) return true;
            foreach (var c in el.Children)
                if (DrawElement(g, r, c, dpPx, depth + 1)) return true;
            return false;
        }

        private bool DrawElement(Graphics g, RectangleF r, AxEl el, float dpPx, int depth)
        {
            if (depth > 12 || r.Width < 1 || r.Height < 1) return false;
            switch (el.Name)
            {
                case "vector":
                    return new VectorRenderer(this).Render(g, r, el);
                case "bitmap":
                case "nine-patch":
                    return DrawChildOrAttr(g, r, el, dpPx, depth, "src", Res.AttrSrc);
                case "inset":
                {
                    float all = Inset(el.A("inset"), r.Width, dpPx);
                    float l = Inset(el.A("insetLeft"), r.Width, dpPx, all), t = Inset(el.A("insetTop"), r.Height, dpPx, all);
                    float rr = Inset(el.A("insetRight"), r.Width, dpPx, all), b = Inset(el.A("insetBottom"), r.Height, dpPx, all);
                    var inner = RectangleF.FromLTRB(r.Left + l, r.Top + t, r.Right - rr, r.Bottom - b);
                    return DrawChildOrAttr(g, inner, el, dpPx, depth);
                }
                case "layer-list":
                {
                    bool any = false;
                    foreach (var item in el.Children.Where(c => c.Name == "item"))
                    {
                        float l = Inset(item.A("left"), r.Width, dpPx), t = Inset(item.A("top"), r.Height, dpPx);
                        float rr = Inset(item.A("right"), r.Width, dpPx), b = Inset(item.A("bottom"), r.Height, dpPx);
                        var ir = RectangleF.FromLTRB(r.Left + l, r.Top + t, r.Right - rr, r.Bottom - b);
                        var st = g.Save();
                        try { any |= DrawChildOrAttr(g, ir, item, dpPx, depth); }
                        finally { g.Restore(st); }
                    }
                    return any;
                }
                case "selector":
                case "level-list":
                case "animation-list":
                case "animated-selector":
                    foreach (var item in el.Children.Where(c => c.Name == "item"))
                        if (DrawChildOrAttr(g, r, item, dpPx, depth)) return true;
                    return false;
                case "animated-vector":
                case "rotate":
                case "scale":
                case "clip":
                case "ripple":
                    return DrawChildOrAttr(g, r, el, dpPx, depth);
                case "color":
                {
                    var c = ColorOf(el.A("color", Res.AttrColor), 0);
                    if (c == null) return false;
                    Fill(g, r, c.Value);
                    return true;
                }
                case "shape":
                    return DrawShape(g, r, el, dpPx);
                case "adaptive-icon":
                {
                    using var bmp = RenderRoot(el, Math.Max(1, (int)r.Width));
                    if (bmp == null) return false;
                    Images.Draw(g, bmp, r);
                    return true;
                }
                default:
                    Log($"  unsupported drawable <{el.Name}>");
                    return false;
            }
        }

        private float Inset(AxAttr? a, float extent, float dpPx, float dflt = 0)
        {
            if (a == null) return dflt;
            if (a.Type == Res.TypeFraction) return Res.Complex(a.Data) * extent;
            if (a.Type == Res.TypeDimension) return Res.Complex(a.Data) * dpPx;
            var f = FloatOf(a, 0);
            return f.HasValue ? f.Value * dpPx : dflt;
        }

        private bool DrawShape(Graphics g, RectangleF r, AxEl el, float dpPx)
        {
            int shape = (int)(FloatOf(el.A("shape"), 0) ?? 0);
            var solid = el.Child("solid");
            var grad = el.Child("gradient");
            float radius = 0;
            var corners = el.Child("corners");
            if (corners != null)
            {
                var ra = corners.A("radius");
                if (ra != null) radius = (ra.Type == Res.TypeDimension ? Res.Complex(ra.Data) : FloatOf(ra, 0) ?? 0) * dpPx;
            }
            using var path = new GraphicsPath();
            if (shape == 1) path.AddEllipse(r);
            else if (radius > 0) path.AddPath(Theme.RoundRect(r, radius), false);
            else path.AddRectangle(r);

            Brush? brush = null;
            try
            {
                if (solid != null && ColorOf(solid.A("color", Res.AttrColor), 0) is Color c) brush = new SolidBrush(c);
                else if (grad != null)
                {
                    var c0 = ColorOf(grad.A("startColor"), 0) ?? Color.Transparent;
                    var c1 = ColorOf(grad.A("endColor"), 0) ?? c0;
                    float angle = FloatOf(grad.A("angle"), 0) ?? 0;
                    brush = new LinearGradientBrush(r, c0, c1, -angle);
                }
                if (brush == null) return false;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                return true;
            }
            catch { return false; }
            finally { brush?.Dispose(); }
        }

        // ----- attribute values -----

        public float? FloatOf(AxAttr? a, int depth)
        {
            if (a == null) return null;
            return FloatOf(a.Type, a.Data, a.Raw, depth);
        }

        private float? FloatOf(byte type, uint data, string? raw, int depth)
        {
            switch (type)
            {
                case Res.TypeFloat: return BitConverter.Int32BitsToSingle((int)data);
                case Res.TypeDimension:
                case Res.TypeFraction: return Res.Complex(data);
                case Res.TypeIntDec:
                case Res.TypeIntHex: return (int)data;
                case Res.TypeIntBoolean: return data != 0 ? 1 : 0;
                case Res.TypeReference:
                case Res.TypeDynamicReference:
                    if (depth > 6) return null;
                    var e = Best(data, false);
                    return e == null ? null : FloatOf(e.Type, e.Data, e.Str, depth + 1);
                case Res.TypeString:
                    return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
            }
            return null;
        }

        public string? StringOf(AxAttr? a)
        {
            if (a == null) return null;
            if (a.Type == Res.TypeString || a.Raw != null && a.Type != Res.TypeReference) return a.Raw;
            if (a.Type is Res.TypeReference or Res.TypeDynamicReference)
                return Best(a.Data, false)?.Str;
            return a.Raw;
        }

        public Color? ColorOf(AxAttr? a, int depth)
        {
            if (a == null) return null;
            return ColorOf(a.Type, a.Data, a.Raw, depth);
        }

        private Color? ColorOf(byte type, uint data, string? raw, int depth)
        {
            if (depth > 6) return null;
            if (Res.IsColor(type)) return Res.ToColor(type, data);
            if (type is Res.TypeReference or Res.TypeDynamicReference)
            {
                if (Res.Framework(data) is Color fc) return fc;
                var e = Best(data, false);
                if (e == null) return null;
                return ColorOf(e.Type, e.Data, e.Str, depth + 1);
            }
            if (type == Res.TypeString && raw != null)
            {
                if (raw.StartsWith('#')) return Res.ParseHex(raw);
                if (IsXml(raw) && Xml(raw) is AxEl el)
                {
                    // <selector>: the last item is the default state; a <gradient> gets its start color.
                    if (el.Name == "selector")
                    {
                        var item = el.Children.LastOrDefault(c => c.Name == "item");
                        var c = ColorOf(item?.A("color", Res.AttrColor), depth + 1);
                        var alpha = FloatOf(item?.A("alpha", Res.AttrAlpha), depth + 1);
                        if (c.HasValue && alpha.HasValue) c = Color.FromArgb((int)Math.Clamp(c.Value.A * alpha.Value, 0, 255), c.Value);
                        return c;
                    }
                    if (el.Name == "gradient") return ColorOf(el.A("startColor"), depth + 1);
                }
            }
            return null;
        }

        /// <summary>A vector fill: either a solid color or a &lt;gradient&gt; element.</summary>
        public (Color? Solid, AxEl? Gradient) PaintOf(AxAttr? a)
        {
            if (a == null) return (null, null);
            if (a.Type is Res.TypeReference or Res.TypeDynamicReference && Res.Framework(a.Data) == null)
            {
                var e = Best(a.Data, true);
                if (e?.Str != null && IsXml(e.Str) && Xml(e.Str) is AxEl { Name: "gradient" } ge) return (null, ge);
            }
            return (ColorOf(a, 0), null);
        }
    }

    // ----- VectorDrawable -----

    private sealed class VectorRenderer
    {
        private readonly Resources _res;
        public VectorRenderer(Resources res) => _res = res;

        public bool Render(Graphics g, RectangleF r, AxEl v)
        {
            float vw = _res.FloatOf(v.A("viewportWidth", Res.AttrViewportWidth), 0) ?? 0;
            float vh = _res.FloatOf(v.A("viewportHeight", Res.AttrViewportHeight), 0) ?? 0;
            if (vw <= 0 || vh <= 0)
            {
                vw = _res.FloatOf(v.A("width"), 0) ?? 0;
                vh = _res.FloatOf(v.A("height"), 0) ?? 0;
            }
            if (vw <= 0 || vh <= 0) return false;
            float alpha = _res.FloatOf(v.A("alpha", Res.AttrAlpha), 0) ?? 1f;

            var state = g.Save();
            try
            {
                g.TranslateTransform(r.X, r.Y);
                g.ScaleTransform(r.Width / vw, r.Height / vh);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var m = new Matrix();
                return DrawChildren(g, v, m, alpha, 0) > 0;
            }
            finally { g.Restore(state); }
        }

        private int DrawChildren(Graphics g, AxEl el, Matrix parent, float alpha, int depth)
        {
            if (depth > 32) return 0;
            int drawn = 0;
            var state = g.Save(); // clip-paths are scoped to their group
            try
            {
                foreach (var c in el.Children)
                {
                    switch (c.Name)
                    {
                        case "group":
                        {
                            using var m = GroupMatrix(c);
                            m.Multiply(parent, MatrixOrder.Append);
                            drawn += DrawChildren(g, c, m, alpha, depth + 1);
                            break;
                        }
                        case "clip-path":
                        {
                            using var p = SvgPath.Parse(_res.StringOf(c.A("pathData", Res.AttrPathData)));
                            if (p != null) { p.Transform(parent); g.SetClip(p, CombineMode.Intersect); }
                            break;
                        }
                        case "path":
                            if (DrawPath(g, c, parent, alpha)) drawn++;
                            break;
                    }
                }
            }
            finally { g.Restore(state); }
            return drawn;
        }

        private Matrix GroupMatrix(AxEl gr)
        {
            float rot = _res.FloatOf(gr.A("rotation"), 0) ?? 0;
            float px = _res.FloatOf(gr.A("pivotX"), 0) ?? 0, py = _res.FloatOf(gr.A("pivotY"), 0) ?? 0;
            float sx = _res.FloatOf(gr.A("scaleX"), 0) ?? 1, sy = _res.FloatOf(gr.A("scaleY"), 0) ?? 1;
            float tx = _res.FloatOf(gr.A("translateX"), 0) ?? 0, ty = _res.FloatOf(gr.A("translateY"), 0) ?? 0;
            // Same order as android.graphics.drawable.VectorDrawable.VGroup.updateLocalMatrix.
            var m = new Matrix();
            m.Translate(-px, -py, MatrixOrder.Append);
            if (sx != 0 && sy != 0) m.Scale(sx, sy, MatrixOrder.Append);
            m.Rotate(rot, MatrixOrder.Append);
            m.Translate(tx + px, ty + py, MatrixOrder.Append);
            return m;
        }

        private bool DrawPath(Graphics g, AxEl p, Matrix m, float alpha)
        {
            using var path = SvgPath.Parse(_res.StringOf(p.A("pathData", Res.AttrPathData)));
            if (path == null || path.PointCount == 0) return false;
            int fillType = (int)(_res.FloatOf(p.A("fillType"), 0) ?? 0);
            path.FillMode = fillType == 1 ? FillMode.Alternate : FillMode.Winding;
            path.Transform(m);
            bool drew = false;

            var (fillColor, fillGrad) = _res.PaintOf(p.A("fillColor", Res.AttrFillColor));
            float fillAlpha = (_res.FloatOf(p.A("fillAlpha"), 0) ?? 1f) * alpha;
            using (var brush = MakeBrush(fillColor, fillGrad, fillAlpha, m, out var underlay))
            {
                if (brush != null)
                {
                    try
                    {
                        // Radial gradients: GDI+ paints nothing past the radius, Android clamps to the outer color.
                        if (underlay is Color u) { using var ub = new SolidBrush(u); g.FillPath(ub, path); }
                        g.FillPath(brush, path);
                        drew = true;
                    }
                    catch { }
                }
            }

            float sw = _res.FloatOf(p.A("strokeWidth"), 0) ?? 0;
            if (sw > 0)
            {
                var (strokeColor, strokeGrad) = _res.PaintOf(p.A("strokeColor"));
                float strokeAlpha = (_res.FloatOf(p.A("strokeAlpha"), 0) ?? 1f) * alpha;
                using var brush = MakeBrush(strokeColor, strokeGrad, strokeAlpha, m, out _);
                if (brush != null)
                {
                    var el = m.Elements;
                    float scale = (float)Math.Sqrt(Math.Abs(el[0] * el[3] - el[1] * el[2]));
                    using var pen = new Pen(brush, sw * (scale > 0 ? scale : 1));
                    int cap = (int)(_res.FloatOf(p.A("strokeLineCap"), 0) ?? 0);
                    int join = (int)(_res.FloatOf(p.A("strokeLineJoin"), 0) ?? 0);
                    pen.StartCap = pen.EndCap = cap == 1 ? LineCap.Round : cap == 2 ? LineCap.Square : LineCap.Flat;
                    pen.LineJoin = join == 1 ? LineJoin.Round : join == 2 ? LineJoin.Bevel : LineJoin.Miter;
                    try { g.DrawPath(pen, path); drew = true; } catch { }
                }
            }
            return drew;
        }

        private Brush? MakeBrush(Color? solid, AxEl? grad, float alpha, Matrix m, out Color? underlay)
        {
            underlay = null;
            alpha = Math.Clamp(alpha, 0, 1);
            if (solid is Color c)
            {
                if (c.A == 0 || alpha <= 0) return null;
                return new SolidBrush(Color.FromArgb((int)Math.Round(c.A * alpha), c));
            }
            if (grad == null) return null;
            try { return GradientBrush(grad, alpha, m, out underlay); } catch { return null; }
        }

        private Brush? GradientBrush(AxEl gr, float alpha, Matrix m, out Color? underlay)
        {
            underlay = null;
            Color A(Color c) => Color.FromArgb((int)Math.Round(c.A * alpha), c);
            var stops = new List<(float Pos, Color C)>();
            foreach (var item in gr.Children.Where(i => i.Name == "item"))
            {
                var col = _res.ColorOf(item.A("color", Res.AttrColor), 0);
                var off = _res.FloatOf(item.A("offset"), 0);
                if (col.HasValue) stops.Add((Math.Clamp(off ?? 0, 0, 1), A(col.Value)));
            }
            if (stops.Count == 0)
            {
                var c0 = _res.ColorOf(gr.A("startColor"), 0);
                var cm = _res.ColorOf(gr.A("centerColor"), 0);
                var c1 = _res.ColorOf(gr.A("endColor"), 0);
                if (c0 is Color s) stops.Add((0, A(s)));
                if (cm is Color mm) stops.Add((0.5f, A(mm)));
                if (c1 is Color e) stops.Add((1, A(e)));
            }
            if (stops.Count == 0) return null;
            stops.Sort((x, y) => x.Pos.CompareTo(y.Pos));
            if (stops.Count == 1) return new SolidBrush(stops[0].C);

            int type = (int)(_res.FloatOf(gr.A("type"), 0) ?? 0);
            if (type == 1)
            {
                float cx = _res.FloatOf(gr.A("centerX"), 0) ?? 0, cy = _res.FloatOf(gr.A("centerY"), 0) ?? 0;
                float rad = _res.FloatOf(gr.A("gradientRadius"), 0) ?? 0;
                if (rad <= 0) return new SolidBrush(stops[^1].C);
                using var ep = new GraphicsPath();
                ep.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
                ep.Transform(m);
                var pb = new PathGradientBrush(ep) { CenterColor = stops[0].C, SurroundColors = new[] { stops[^1].C } };
                var pts = new List<(float, Color)>(stops);
                if (pts[0].Item1 > 0) pts.Insert(0, (0, pts[0].Item2));
                if (pts[^1].Item1 < 1) pts.Add((1, pts[^1].Item2));
                // PathGradientBrush positions run from the edge (0) to the centre (1).
                pts.Reverse();
                pb.InterpolationColors = new ColorBlend
                {
                    Colors = pts.Select(p => p.Item2).ToArray(),
                    Positions = pts.Select(p => 1 - p.Item1).ToArray(),
                };
                underlay = stops[^1].C;
                return pb;
            }
            if (type == 2) return new SolidBrush(stops[stops.Count / 2].C); // sweep: approximate

            float sx = _res.FloatOf(gr.A("startX"), 0) ?? 0, sy = _res.FloatOf(gr.A("startY"), 0) ?? 0;
            float ex = _res.FloatOf(gr.A("endX"), 0) ?? 0, ey = _res.FloatOf(gr.A("endY"), 0) ?? 0;
            if (Math.Abs(ex - sx) < 1e-3 && Math.Abs(ey - sy) < 1e-3) return new SolidBrush(stops[^1].C);
            // GDI+ cannot clamp a linear gradient, so stretch the line far beyond both ends
            // and pad the blend with the end colors.
            const float k = 8f;
            float dx = ex - sx, dy = ey - sy;
            var p0 = new PointF(sx - dx * k, sy - dy * k);
            var p1 = new PointF(ex + dx * k, ey + dy * k);
            var lb = new LinearGradientBrush(p0, p1, stops[0].C, stops[^1].C);
            var cols = new List<Color> { stops[0].C };
            var pos = new List<float> { 0f };
            foreach (var s in stops) { cols.Add(s.C); pos.Add((k + s.Pos) / (1 + 2 * k)); }
            cols.Add(stops[^1].C); pos.Add(1f);
            lb.InterpolationColors = new ColorBlend { Colors = cols.ToArray(), Positions = pos.ToArray() };
            lb.MultiplyTransform(m, MatrixOrder.Append);
            return lb;
        }
    }

    // ----- SVG path data (android:pathData) -----

    internal static class SvgPath
    {
        public static GraphicsPath? Parse(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return null;
            var p = new GraphicsPath(FillMode.Winding);
            var r = new Reader(d);
            char cmd = '\0', prev = '\0';
            PointF cur = PointF.Empty, start = PointF.Empty, lastCubic = PointF.Empty, lastQuad = PointF.Empty;
            try
            {
                while (true)
                {
                    r.SkipSeparators();
                    if (r.End) break;
                    char c = r.Peek;
                    if (char.IsLetter(c)) { cmd = c; r.Advance(); }
                    else if (cmd == '\0') return Done(p);
                    else if (cmd is 'z' or 'Z') return Done(p); // numbers after Z: malformed

                    bool rel = char.IsLower(cmd);
                    PointF P(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);
                    switch (char.ToUpperInvariant(cmd))
                    {
                        case 'M':
                        {
                            if (!r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            cur = start = P(x, y);
                            p.StartFigure();
                            cmd = rel ? 'l' : 'L'; // further pairs are implicit line-tos
                            prev = 'M';
                            continue;
                        }
                        case 'L':
                        {
                            if (!r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var pt = P(x, y); p.AddLine(cur, pt); cur = pt; break;
                        }
                        case 'H':
                        {
                            if (!r.Num(out var x)) return Done(p);
                            var pt = new PointF(rel ? cur.X + x : x, cur.Y); p.AddLine(cur, pt); cur = pt; break;
                        }
                        case 'V':
                        {
                            if (!r.Num(out var y)) return Done(p);
                            var pt = new PointF(cur.X, rel ? cur.Y + y : y); p.AddLine(cur, pt); cur = pt; break;
                        }
                        case 'C':
                        {
                            if (!r.Num(out var x1) || !r.Num(out var y1) || !r.Num(out var x2) || !r.Num(out var y2) ||
                                !r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var c1 = P(x1, y1); var c2 = P(x2, y2); var pt = P(x, y);
                            p.AddBezier(cur, c1, c2, pt); lastCubic = c2; cur = pt; break;
                        }
                        case 'S':
                        {
                            if (!r.Num(out var x2) || !r.Num(out var y2) || !r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var c1 = prev is 'C' or 'S' ? new PointF(2 * cur.X - lastCubic.X, 2 * cur.Y - lastCubic.Y) : cur;
                            var c2 = P(x2, y2); var pt = P(x, y);
                            p.AddBezier(cur, c1, c2, pt); lastCubic = c2; cur = pt; break;
                        }
                        case 'Q':
                        {
                            if (!r.Num(out var x1) || !r.Num(out var y1) || !r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var q = P(x1, y1); var pt = P(x, y);
                            Quad(p, cur, q, pt); lastQuad = q; cur = pt; break;
                        }
                        case 'T':
                        {
                            if (!r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var q = prev is 'Q' or 'T' ? new PointF(2 * cur.X - lastQuad.X, 2 * cur.Y - lastQuad.Y) : cur;
                            var pt = P(x, y);
                            Quad(p, cur, q, pt); lastQuad = q; cur = pt; break;
                        }
                        case 'A':
                        {
                            if (!r.Num(out var rx) || !r.Num(out var ry) || !r.Num(out var rot) ||
                                !r.Flag(out var large) || !r.Flag(out var sweep) || !r.Num(out var x) || !r.Num(out var y)) return Done(p);
                            var pt = P(x, y);
                            Arc(p, cur, rx, ry, rot, large, sweep, pt); cur = pt; break;
                        }
                        case 'Z':
                            p.CloseFigure(); cur = start; break;
                        default:
                            return Done(p);
                    }
                    prev = char.ToUpperInvariant(cmd);
                }
            }
            catch { }
            return Done(p);
        }

        private static GraphicsPath? Done(GraphicsPath p)
        {
            if (p.PointCount > 0) return p;
            p.Dispose();
            return null;
        }

        private static void Quad(GraphicsPath p, PointF p0, PointF q, PointF p1)
        {
            var c1 = new PointF(p0.X + 2f / 3 * (q.X - p0.X), p0.Y + 2f / 3 * (q.Y - p0.Y));
            var c2 = new PointF(p1.X + 2f / 3 * (q.X - p1.X), p1.Y + 2f / 3 * (q.Y - p1.Y));
            p.AddBezier(p0, c1, c2, p1);
        }

        /// <summary>SVG elliptical arc → cubic Béziers (SVG 1.1 implementation notes F.6.5).</summary>
        private static void Arc(GraphicsPath p, PointF p0, double rx, double ry, double angle, bool large, bool sweep, PointF p1)
        {
            if (p0 == p1) return;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx < 1e-9 || ry < 1e-9) { p.AddLine(p0, p1); return; }
            double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx2 = (p0.X - p1.X) / 2.0, dy2 = (p0.Y - p1.Y) / 2.0;
            double x1 = cos * dx2 + sin * dy2, y1 = -sin * dx2 + cos * dy2;
            double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1) { var s = Math.Sqrt(lambda); rx *= s; ry *= s; }
            double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (large == sweep) coef = -coef;
            double cxp = coef * rx * y1 / ry, cyp = -coef * ry * x1 / rx;
            double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2.0;
            double cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2.0;
            static double Ang(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            double t1 = Ang(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            double dt = Ang((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && dt > 0) dt -= 2 * Math.PI;
            else if (sweep && dt < 0) dt += 2 * Math.PI;
            int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2) - 1e-9));
            double delta = dt / segs, t = 4.0 / 3.0 * Math.Tan(delta / 4);
            PointF Map(double ux, double uy) => new((float)(cx + rx * ux * cos - ry * uy * sin), (float)(cy + rx * ux * sin + ry * uy * cos));
            var from = p0;
            for (int i = 0; i < segs; i++)
            {
                double a1 = t1 + i * delta, a2 = a1 + delta;
                double c1x = Math.Cos(a1) - t * Math.Sin(a1), c1y = Math.Sin(a1) + t * Math.Cos(a1);
                double c2x = Math.Cos(a2) + t * Math.Sin(a2), c2y = Math.Sin(a2) - t * Math.Cos(a2);
                var to = i == segs - 1 ? p1 : Map(Math.Cos(a2), Math.Sin(a2));
                p.AddBezier(from, Map(c1x, c1y), Map(c2x, c2y), to);
                from = to;
            }
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;
            public Reader(string s) => _s = s;
            public bool End => _i >= _s.Length;
            public char Peek => _s[_i];
            public void Advance() => _i++;

            public void SkipSeparators()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
            }

            public bool Num(out float v)
            {
                v = 0;
                SkipSeparators();
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                bool digits = false, dot = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9') { digits = true; _i++; }
                    else if (c == '.' && !dot) { dot = true; _i++; }
                    else break;
                }
                if (digits && _i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int j = _i + 1;
                    if (j < _s.Length && (_s[j] == '+' || _s[j] == '-')) j++;
                    if (j < _s.Length && char.IsAsciiDigit(_s[j]))
                    {
                        _i = j;
                        while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
                    }
                }
                if (!digits) { _i = start; return false; }
                return float.TryParse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            }

            public bool Flag(out bool f)
            {
                f = false;
                SkipSeparators();
                if (_i < _s.Length && (_s[_i] == '0' || _s[_i] == '1')) { f = _s[_i] == '1'; _i++; return true; }
                return false;
            }
        }
    }
}

/// <summary>Adaptive icon composition: 108dp layers, centre 72dp shown through a rounded-square mask.</summary>
internal static class Adaptive
{
    public delegate bool Layer(Graphics g, RectangleF layerRect);

    public static Bitmap? Compose(int px, Layer background, Layer foreground)
    {
        float layer = px * 108f / 72f, off = -(layer - px) / 2f;
        var rect = new RectangleF(off, off, layer, layer);
        using var layers = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(layers))
        {
            Images.Quality(g);
            bool bg = false;
            try { bg = background(g, rect); } catch { }
            if (!bg) g.Clear(Color.White);
            bool fg = false;
            try { fg = foreground(g, rect); } catch { }
            if (!fg) return null;
        }
        var result = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var tb = new TextureBrush(layers);
            using var mask = Theme.RoundRect(new RectangleF(0, 0, px, px), px * 0.22f);
            g.FillPath(tb, mask);
        }
        return result;
    }
}

/// <summary>Image decoding: GDI+ first (PNG/JPEG/GIF/BMP), then WIC for WebP when a codec is installed.</summary>
internal static class Images
{
    public static void Quality(Graphics g)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    public static Bitmap? Decode(byte[]? data)
    {
        if (data == null || data.Length < 8) return null;
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
            return bmp;
        }
        catch { }
        try { return DecodeWic(data); } catch { return null; }
    }

    /// <summary>WebP etc. via Windows Imaging Component (needs the WebP Image Extension); kept
    /// in its own method so PresentationCore only loads when GDI+ couldn't decode.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Bitmap? DecodeWic(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        if (dec.Frames.Count == 0) return null;
        var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            dec.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    public static void Draw(Graphics g, Image img, RectangleF r)
    {
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        var st = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) };
        g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
        g.Restore(st);
    }

    /// <summary>Cap cached rasters at 256px; keep smaller ones as they are.</summary>
    public static Bitmap Normalize(Bitmap img)
    {
        int max = Math.Max(img.Width, img.Height);
        if (max <= 256) return img;
        int w = Math.Max(1, img.Width * 256 / max), h = Math.Max(1, img.Height * 256 / max);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Quality(g);
            Draw(g, img, new RectangleF(0, 0, w, h));
        }
        img.Dispose();
        return bmp;
    }

    public static bool IsBlank(Bitmap bmp)
    {
        try
        {
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                int visible = 0;
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                    for (int x = 3; x < row.Length; x += 4) if (row[x] > 16 && ++visible > 16) return false;
                }
                return true;
            }
            finally { bmp.UnlockBits(bd); }
        }
        catch { return false; }
    }
}

/// <summary>The base APK plus any density split APKs, searched in that order.</summary>
internal sealed class PackageSet : IDisposable
{
    private readonly List<(ZipArchive Zip, string Name, bool Owns)> _zips = new();

    public void Add(ZipArchive zip, string name, bool owns = false) => _zips.Add((zip, name, owns));

    public IEnumerable<string> EntryNames => _zips.SelectMany(z => z.Zip.Entries.Select(e => e.FullName));

    public byte[]? Read(string name)
    {
        foreach (var (zip, _, _) in _zips)
        {
            var e = zip.GetEntry(name);
            if (e != null) return IconExtractor.ReadAll(e, 64L * 1024 * 1024);
        }
        return null;
    }

    public IEnumerable<byte[]> ReadAll(string name)
    {
        foreach (var (zip, _, _) in _zips)
        {
            var e = zip.GetEntry(name);
            if (e == null) continue;
            var b = IconExtractor.ReadAll(e, 128L * 1024 * 1024);
            if (b != null) yield return b;
        }
    }

    public void Dispose()
    {
        foreach (var (zip, _, owns) in _zips) if (owns) try { zip.Dispose(); } catch { }
        _zips.Clear();
    }
}

// =======================================================================================
// Android binary formats: string pool, binary XML (AXML), resource table (ARSC)
// =======================================================================================

internal static class Res
{
    public const byte TypeNull = 0x00, TypeReference = 0x01, TypeAttribute = 0x02, TypeString = 0x03,
        TypeFloat = 0x04, TypeDimension = 0x05, TypeFraction = 0x06, TypeDynamicReference = 0x07,
        TypeIntDec = 0x10, TypeIntHex = 0x11, TypeIntBoolean = 0x12,
        TypeArgb8 = 0x1c, TypeRgb8 = 0x1d, TypeArgb4 = 0x1e, TypeRgb4 = 0x1f;

    // android.R.attr ids, used when attribute names were stripped from the binary XML.
    public const uint AttrIcon = 0x01010002, AttrName = 0x01010003, AttrRoundIcon = 0x0101052c,
        AttrDrawable = 0x01010199, AttrColor = 0x010101a5, AttrSrc = 0x01010119, AttrAlpha = 0x0101031f,
        AttrViewportWidth = 0x01010402, AttrViewportHeight = 0x01010403,
        AttrFillColor = 0x01010404, AttrPathData = 0x01010405;

    public static bool IsColor(byte t) => t is >= TypeArgb8 and <= TypeRgb4;

    public static Color ToColor(byte t, uint data)
    {
        // aapt stores all four color forms expanded to 0xAARRGGBB; RGB forms are opaque.
        uint argb = t is TypeRgb8 or TypeRgb4 ? data | 0xFF000000 : data;
        return Color.FromArgb(unchecked((int)argb));
    }

    /// <summary>The few framework colors launcher icons commonly reference.</summary>
    public static Color? Framework(uint id) => id switch
    {
        0x0106000b => Color.White,          // @android:color/white
        0x0106000c => Color.Black,          // @android:color/black
        0x0106000d => Color.Transparent,    // @android:color/transparent
        _ => null,
    };

    public static Color? ParseHex(string s)
    {
        s = s.Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return s.Length switch
        {
            3 => Color.FromArgb(255, (int)((v >> 8) & 0xF) * 17, (int)((v >> 4) & 0xF) * 17, (int)(v & 0xF) * 17),
            4 => Color.FromArgb((int)((v >> 12) & 0xF) * 17, (int)((v >> 8) & 0xF) * 17, (int)((v >> 4) & 0xF) * 17, (int)(v & 0xF) * 17),
            6 => Color.FromArgb(unchecked((int)(v | 0xFF000000))),
            8 => Color.FromArgb(unchecked((int)v)),
            _ => null,
        };
    }

    private static readonly float[] RadixMults =
        { 1f / (1 << 8), 1f / (1 << 7) / (1 << 8), 1f / (1 << 15) / (1 << 8), 1f / (1 << 23) / (1 << 8) };

    /// <summary>Value of a complex (dimension / fraction) number, ignoring its unit.</summary>
    public static float Complex(uint data) =>
        unchecked((int)(data & 0xFFFFFF00)) * RadixMults[(data >> 4) & 3];

    public static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    public static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
}

internal sealed class StringPool
{
    private readonly byte[] _b;
    private readonly int _count, _offsetsAt, _stringsAt;
    private readonly bool _utf8;
    private readonly string?[] _cache;

    public StringPool(byte[] b, int at)
    {
        _b = b;
        int headerSize = Res.U16(b, at + 2);
        _count = (int)Math.Min(Res.U32(b, at + 8), 4_000_000);
        uint flags = Res.U32(b, at + 16);
        _stringsAt = at + (int)Res.U32(b, at + 20);
        _utf8 = (flags & 0x100) != 0;
        _offsetsAt = at + headerSize;
        _cache = new string?[_count];
    }

    public string? Get(int i)
    {
        if ((uint)i >= (uint)_count) return null;
        if (_cache[i] != null) return _cache[i];
        try
        {
            int pos = _stringsAt + (int)Res.U32(_b, _offsetsAt + i * 4);
            string s;
            if (_utf8)
            {
                int n = _b[pos];
                pos += (n & 0x80) != 0 ? 2 : 1;            // UTF-16 length (unused)
                int len = _b[pos];
                if ((len & 0x80) != 0) { len = ((len & 0x7F) << 8) | _b[pos + 1]; pos += 2; } else pos += 1;
                s = Encoding.UTF8.GetString(_b, pos, len);
            }
            else
            {
                int len = Res.U16(_b, pos);
                if ((len & 0x8000) != 0) { len = ((len & 0x7FFF) << 16) | Res.U16(_b, pos + 2); pos += 4; } else pos += 2;
                s = Encoding.Unicode.GetString(_b, pos, len * 2);
            }
            return _cache[i] = s;
        }
        catch { return null; }
    }
}

internal sealed class AxAttr
{
    public string Name = "";
    public uint ResId;
    public byte Type;
    public uint Data;
    public string? Raw;
}

internal sealed class AxEl
{
    public string Name = "";
    public readonly List<AxAttr> Attrs = new();
    public readonly List<AxEl> Children = new();

    /// <summary>Attribute by framework id (if given and present) or by local name.</summary>
    public AxAttr? A(string name, uint id = 0)
    {
        if (id != 0)
            foreach (var a in Attrs) if (a.ResId == id) return a;
        foreach (var a in Attrs) if (a.Name == name) return a;
        return null;
    }

    public AxEl? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public IEnumerable<AxEl> Descendants(string name)
    {
        foreach (var c in Children)
        {
            if (c.Name == name) yield return c;
            foreach (var d in c.Descendants(name)) yield return d;
        }
    }
}

/// <summary>Android compiled ("binary") XML → a small element tree.</summary>
internal static class Axml
{
    public static AxEl? Parse(byte[] b)
    {
        try
        {
            if (b.Length < 8 || Res.U16(b, 0) != 0x0003) return null;
            int pos = Res.U16(b, 2);
            int end = (int)Math.Min(Res.U32(b, 4), (uint)b.Length);
            StringPool? pool = null;
            uint[] map = Array.Empty<uint>();
            var root = new AxEl { Name = "#document" };
            var stack = new Stack<AxEl>();
            stack.Push(root);
            int guard = 0;
            while (pos + 8 <= end && guard++ < 500_000)
            {
                int type = Res.U16(b, pos), hs = Res.U16(b, pos + 2);
                int size = (int)Res.U32(b, pos + 4);
                if (size < 8 || pos + size > end) break;
                switch (type)
                {
                    case 0x0001:
                        pool ??= new StringPool(b, pos);
                        break;
                    case 0x0180:
                    {
                        int n = (size - hs) / 4;
                        map = new uint[Math.Max(0, n)];
                        for (int i = 0; i < map.Length; i++) map[i] = Res.U32(b, pos + hs + i * 4);
                        break;
                    }
                    case 0x0102:
                    {
                        int ext = pos + hs;
                        var el = new AxEl { Name = pool?.Get((int)Res.U32(b, ext + 4)) ?? "" };
                        int attrStart = Res.U16(b, ext + 8), attrSize = Res.U16(b, ext + 10), count = Res.U16(b, ext + 12);
                        if (attrSize == 0) attrSize = 20;
                        for (int i = 0; i < count; i++)
                        {
                            int a = ext + attrStart + i * attrSize;
                            if (a + 20 > pos + size) break;
                            int nameIdx = (int)Res.U32(b, a + 4);
                            uint raw = Res.U32(b, a + 8);
                            byte vt = b[a + 15];
                            uint vd = Res.U32(b, a + 16);
                            el.Attrs.Add(new AxAttr
                            {
                                Name = pool?.Get(nameIdx) ?? "",
                                ResId = (uint)nameIdx < (uint)map.Length ? map[nameIdx] : 0,
                                Type = vt,
                                Data = vd,
                                Raw = raw != 0xFFFFFFFF ? pool?.Get((int)raw) : vt == Res.TypeString ? pool?.Get((int)vd) : null,
                            });
                        }
                        stack.Peek().Children.Add(el);
                        stack.Push(el);
                        break;
                    }
                    case 0x0103:
                        if (stack.Count > 1) stack.Pop();
                        break;
                }
                pos += size;
            }
            return root.Children.FirstOrDefault();
        }
        catch { return null; }
    }
}

internal readonly record struct ResConfig(int Density, int Sdk, bool Qualified)
{
    /// <summary>Density used for ranking: default = mdpi, nodpi ranks high, anydpi ranks lowest for rasters.</summary>
    public int EffectiveDensity => Density switch { 0 => 160, 0xFFFF => 480, 0xFFFE => 0, _ => Density };
}

internal sealed record ResEntry(byte Type, uint Data, string? Str, ResConfig Config);

/// <summary>resources.arsc: just enough to look up an entry's values across configurations.</summary>
internal sealed class ResTable
{
    private readonly byte[] _b;
    private readonly StringPool? _values;
    private readonly Dictionary<int, Dictionary<int, List<int>>> _types = new(); // pkg → type → chunk offsets

    public ResTable(byte[] b)
    {
        _b = b;
        if (b.Length < 12 || Res.U16(b, 0) != 0x0002) throw new InvalidDataException("not a resource table");
        int end = (int)Math.Min(Res.U32(b, 4), (uint)b.Length);
        int pos = Res.U16(b, 2);
        while (pos + 8 <= end)
        {
            int type = Res.U16(b, pos), hs = Res.U16(b, pos + 2);
            int size = (int)Res.U32(b, pos + 4);
            if (size < 8 || pos + size > end) break;
            if (type == 0x0001 && _values == null) _values = new StringPool(b, pos);
            else if (type == 0x0200) ParsePackage(pos, hs, size);
            pos += size;
        }
    }

    private void ParsePackage(int p, int hs, int size)
    {
        int id = (int)Res.U32(_b, p + 8);
        int typeIdOffset = hs >= 288 ? (int)Res.U32(_b, p + 284) : 0;
        if (!_types.TryGetValue(id, out var types)) _types[id] = types = new();
        int pos = p + hs, end = p + size;
        while (pos + 8 <= end)
        {
            int type = Res.U16(_b, pos);
            int csize = (int)Res.U32(_b, pos + 4);
            if (csize < 8 || pos + csize > end) break;
            if (type == 0x0201)
            {
                int tid = _b[pos + 8] + typeIdOffset;
                if (!types.TryGetValue(tid, out var list)) types[tid] = list = new();
                list.Add(pos);
            }
            pos += csize;
        }
    }

    public List<ResEntry> Lookup(uint resId)
    {
        var result = new List<ResEntry>();
        int pkgId = (int)(resId >> 24), typeId = (int)((resId >> 16) & 0xFF), idx = (int)(resId & 0xFFFF);
        if (!_types.TryGetValue(pkgId, out var types))
        {
            if (_types.Count == 1 && pkgId == 0x7F) types = _types.Values.First();
            else return result;
        }
        if (!types.TryGetValue(typeId, out var chunks)) return result;
        foreach (var c in chunks)
        {
            try
            {
                var e = ReadEntry(c, idx);
                if (e != null) result.Add(e);
            }
            catch { }
        }
        return result;
    }

    private ResEntry? ReadEntry(int c, int idx)
    {
        int hs = Res.U16(_b, c + 2);
        byte flags = _b[c + 9];
        int count = (int)Res.U32(_b, c + 12);
        int entriesStart = (int)Res.U32(_b, c + 16);
        int offsets = c + hs;
        int off = -1;
        if ((flags & 0x01) != 0) // FLAG_SPARSE: (u16 idx, u16 offset/4) pairs, sorted
        {
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int ei = Res.U16(_b, offsets + mid * 4);
                if (ei == idx) { off = Res.U16(_b, offsets + mid * 4 + 2) * 4; break; }
                if (ei < idx) lo = mid + 1; else hi = mid - 1;
            }
        }
        else
        {
            if (idx >= count) return null;
            if ((flags & 0x02) != 0) // FLAG_OFFSET16
            {
                int v = Res.U16(_b, offsets + idx * 2);
                if (v == 0xFFFF) return null;
                off = v * 4;
            }
            else
            {
                uint v = Res.U32(_b, offsets + idx * 4);
                if (v == 0xFFFFFFFF) return null;
                off = (int)v;
            }
        }
        if (off < 0) return null;

        int e = c + entriesStart + off;
        int esize = Res.U16(_b, e), eflags = Res.U16(_b, e + 2);
        byte vtype;
        uint data;
        if ((eflags & 0x0008) != 0) { vtype = (byte)(eflags >> 8); data = Res.U32(_b, e + 4); } // FLAG_COMPACT
        else if ((eflags & 0x0001) != 0) return null;                                               // bag (style/array)
        else { int v = e + esize; vtype = _b[v + 3]; data = Res.U32(_b, v + 4); }

        int cfg = c + 20;
        int cfgSize = (int)Res.U32(_b, cfg);
        int density = cfgSize >= 16 ? Res.U16(_b, cfg + 14) : 0;
        int sdk = cfgSize >= 28 ? Res.U16(_b, cfg + 24) : 0;
        int uiMode = cfgSize >= 30 ? _b[cfg + 29] : 0;
        bool qualified =
            Res.U32(_b, cfg + 4) != 0 ||                          // mcc / mnc
            Res.U32(_b, cfg + 8) != 0 ||                          // language / region
            (uiMode & 0x0F) > 1 || (uiMode & 0x30) == 0x20 ||     // car/tv/watch, night
            (cfgSize >= 32 && Res.U16(_b, cfg + 30) != 0) ||      // smallestScreenWidthDp
            (cfgSize >= 36 && Res.U32(_b, cfg + 32) != 0);        // screenWidthDp / screenHeightDp

        return new ResEntry(vtype, data, vtype == Res.TypeString ? _values?.Get((int)data) : null,
            new ResConfig(density, sdk, qualified));
    }
}
