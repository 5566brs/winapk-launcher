using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinApkLauncher;

/// <summary>
/// Colors and fonts for the launcher. Follows the Windows *app* theme (light/dark,
/// read once at startup) and the user's accent color, Windows 11 style.
/// </summary>
internal sealed class Theme
{
    // Declared first: static initializers run in textual order and Build() needs the font list.
    private static readonly HashSet<string> Families = LoadFamilies();

    public static Theme Current { get; private set; } = Build(dark: false, ReadAccentPalette());

    public bool Dark { get; private init; }

    public Color Back { get; private init; }          // window chrome: toolbar, status bar, dialog footer
    public Color Content { get; private init; }       // tiles / list / dialog body
    public Color Text { get; private init; }
    public Color SubText { get; private init; }
    public Color Disabled { get; private init; }
    public Color Border { get; private init; }
    public Color Hover { get; private init; }          // subtle fill behind hovered items
    public Color Pressed { get; private init; }
    public Color Checked { get; private init; }        // toggled toolbar button
    public Color Accent { get; private init; }
    public Color AccentHover { get; private init; }
    public Color AccentPressed { get; private init; }
    public Color AccentText { get; private init; }     // text drawn ON an accent fill
    public Color Selection { get; private init; }      // selected tile, focused
    public Color SelectionInactive { get; private init; }
    public Color ControlFill { get; private init; }    // neutral buttons
    public Color ControlFillHover { get; private init; }
    public Color ControlBorder { get; private init; }
    public Color MenuBack { get; private init; }

    public string FontFamily { get; private init; } = "Segoe UI";
    public string SemiboldFamily { get; private init; } = "Segoe UI Semibold";
    public string IconFamily { get; private init; } = "Segoe MDL2 Assets";

    /// <summary>
    /// Decide light/dark once, before any window exists. <paramref name="forced"/> is
    /// "light"/"dark" from --theme, or null to follow
    /// HKCU\...\Themes\Personalize\AppsUseLightTheme.
    /// </summary>
    public static void Initialize(string? forced)
    {
        bool dark = forced?.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => !AppsUseLightTheme(),
        };
        Current = Build(dark, ReadAccentPalette());
#pragma warning disable WFO5001 // WinForms dark mode is still flagged experimental.
        try { Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic); } catch { }
#pragma warning restore WFO5001
    }

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { return true; }
    }

    /// <summary>
    /// The accent palette Windows 11 uses for app accents (light3..dark3).
    /// Null if unavailable, in which case the default Windows blue is used.
    /// </summary>
    private static Color[]? ReadAccentPalette()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] b && b.Length >= 28)
            {
                var c = new Color[7];
                for (int i = 0; i < 7; i++) c[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                return c;
            }
        }
        catch { }
        try
        {
            // Fallback: DWM accent (ABGR).
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
            {
                var a = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                return new[] { Blend(a, Color.White, .6f), Blend(a, Color.White, .4f), Blend(a, Color.White, .2f), a,
                               Blend(a, Color.Black, .15f), Blend(a, Color.Black, .35f), Blend(a, Color.Black, .6f) };
            }
        }
        catch { }
        return null;
    }

    private static Theme Build(bool dark, Color[]? pal)
    {
        // Windows 11 uses AccentDark1 for fills in light mode and AccentLight2 in dark mode.
        var accent = pal != null ? (dark ? pal[1] : pal[4])
                                 : (dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x67, 0xC0));
        var content = dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        var back = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
        var accentText = Luma(accent) > 0.55 ? Color.Black : Color.White;

        string family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        string semi = family == "Segoe UI Variable Text" && HasFont("Segoe UI Variable Text Semibold")
            ? "Segoe UI Variable Text Semibold"
            : HasFont("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";

        return new Theme
        {
            Dark = dark,
            Back = back,
            Content = content,
            Text = dark ? Color.White : Color.FromArgb(0x1A, 0x1A, 0x1A),
            SubText = dark ? Color.FromArgb(0xA8, 0xA8, 0xA8) : Color.FromArgb(0x60, 0x60, 0x60),
            Disabled = dark ? Color.FromArgb(0x6E, 0x6E, 0x6E) : Color.FromArgb(0xA0, 0xA0, 0xA0),
            Border = dark ? Color.FromArgb(0x38, 0x38, 0x38) : Color.FromArgb(0xE0, 0xE0, 0xE0),
            Hover = dark ? Color.FromArgb(0x2E, 0x2E, 0x2E) : Color.FromArgb(0xE9, 0xE9, 0xE9),
            Pressed = dark ? Color.FromArgb(0x26, 0x26, 0x26) : Color.FromArgb(0xE0, 0xE0, 0xE0),
            Checked = dark ? Color.FromArgb(0x33, 0x33, 0x33) : Color.FromArgb(0xFF, 0xFF, 0xFF),
            Accent = accent,
            AccentHover = Blend(accent, back, 0.12f),
            AccentPressed = Blend(accent, back, 0.25f),
            AccentText = accentText,
            Selection = Blend(content, accent, dark ? 0.22f : 0.13f),
            SelectionInactive = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xEE, 0xEE, 0xEE),
            ControlFill = dark ? Color.FromArgb(0x2D, 0x2D, 0x2D) : Color.FromArgb(0xFD, 0xFD, 0xFD),
            ControlFillHover = dark ? Color.FromArgb(0x36, 0x36, 0x36) : Color.FromArgb(0xF5, 0xF5, 0xF5),
            ControlBorder = dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD0, 0xD0, 0xD0),
            MenuBack = dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9),
            FontFamily = family,
            SemiboldFamily = semi,
            IconFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
        };
    }

    public Font UiFont(float pt = 9f, bool semibold = false) =>
        new(semibold ? SemiboldFamily : FontFamily, pt, FontStyle.Regular, GraphicsUnit.Point);

    // ----- helpers -----

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static HashSet<string> LoadFamilies()
    {
        try
        {
            using var fc = new InstalledFontCollection();
            return new HashSet<string>(fc.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
    private static bool HasFont(string name) => Families.Contains(name);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A Segoe Fluent Icons / MDL2 glyph rendered to a transparent bitmap.</summary>
    public Bitmap Glyph(char glyph, int px, Color color)
    {
        var bmp = new Bitmap(px, px);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var fam = new FontFamily(IconFamily);
            using var path = new GraphicsPath();
            path.AddString(glyph.ToString(), fam, 0, px, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width > 0 && b.Height > 0)
            {
                // Scale the glyph so its larger side is ~px*0.9, then centre it on whole pixels.
                float s = Math.Min(px * 0.92f / Math.Max(b.Width, b.Height), 1f);
                using var m = new Matrix();
                m.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2, MatrixOrder.Append);
                m.Scale(s, s, MatrixOrder.Append);
                m.Translate(px / 2f, px / 2f, MatrixOrder.Append);
                path.Transform(m);
            }
            using var br = new SolidBrush(color);
            g.FillPath(br, path);
        }
        catch { }
        return bmp;
    }

    // ----- window chrome (DWM) -----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Dark/light title bar to match the theme; on Windows 11 also a caption color matching the toolbar.</summary>
    public void ApplyWindowChrome(Form f)
    {
        try
        {
            int on = Dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(f.Handle, 19, ref on, 4);        // pre-20H1 value
            if (!AccentOnTitleBars())
            {
                int caption = ColorTranslator.ToWin32(Back);
                int text = ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR (Win11)
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);      // DWMWA_TEXT_COLOR
            }
        }
        catch { }
    }

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Windows 11 rounded corners (small radius) for popups such as context menus.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            int pref = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(hwnd, 33, ref pref, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch { }
    }

    /// <summary>Themed (dark or light) native scroll bars / selection visuals for a control.</summary>
    public void ApplyControlTheme(Control c, bool explorerInLight = true)
    {
        try
        {
            if (Dark) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else if (explorerInLight) SetWindowTheme(c.Handle, "Explorer", null);
        }
        catch { }
    }

    private static bool AccentOnTitleBars()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            return k?.GetValue("ColorPrevalence") is int v && v != 0;
        }
        catch { return false; }
    }
}

/// <summary>Flat, rounded ToolStrip / menu / status bar rendering in the theme colors.</summary>
internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    public const string AccentTag = "accent";
    private readonly Theme _t;

    public ThemedRenderer(Theme t) : base(new ThemedColors(t))
    {
        _t = t;
        RoundedEdges = false;
    }

    private static int S(ToolStripItem item, int v) =>
        item.Owner != null ? item.Owner.LogicalToDeviceUnits(v) : v;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var c = e.ToolStrip is ToolStripDropDown ? _t.MenuBack : _t.Back;
        using var b = new SolidBrush(c);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_t.Border);
        var r = e.ToolStrip.ClientRectangle;
        if (e.ToolStrip is ToolStripDropDown)
        {
            // On Windows 11 the rounded popup gets its border from DWM.
            if (!Theme.IsWindows11) e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        }
        else if (e.ToolStrip is StatusStrip)
            e.Graphics.DrawLine(pen, 0, 0, r.Width, 0);
        else
            e.Graphics.DrawLine(pen, 0, r.Height - 1, r.Width, r.Height - 1);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var g = e.Graphics;
        bool accent = Equals(item.Tag, AccentTag);
        bool isChecked = item is ToolStripButton { Checked: true };
        Color? fill = null, border = null;

        if (accent)
            fill = !item.Enabled ? _t.Hover : item.Pressed ? _t.AccentPressed : item.Selected ? _t.AccentHover : _t.Accent;
        else if (item.Enabled && item.Pressed) fill = _t.Pressed;
        else if (item.Enabled && item.Selected) fill = _t.Hover;
        else if (isChecked) { fill = _t.Checked; border = _t.Border; }

        if (fill == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, item.Width - 1f, item.Height - 1f);
        using (var path = Theme.RoundRect(r, S(item, 4)))
        {
            using var b = new SolidBrush(fill.Value);
            g.FillPath(b, path);
            if (border != null) { using var p = new Pen(border.Value); g.DrawPath(p, path); }
        }
        if (isChecked)
        {
            // Windows 11 style "selected" pill under the toggled view button.
            float w = S(item, 14), h = S(item, 3);
            var pill = new RectangleF((item.Width - w) / 2f, item.Height - h - S(item, 2), w, h);
            using var pp = Theme.RoundRect(pill, h / 2);
            using var ab = new SolidBrush(_t.Accent);
            g.FillPath(ab, pp);
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderButtonBackground(e);

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) { base.OnRenderMenuItemBackground(e); return; }
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(S(e.Item, 4), 1, e.Item.Width - S(e.Item, 8), e.Item.Height - 2);
        using var path = Theme.RoundRect(r, S(e.Item, 4));
        using var b = new SolidBrush(_t.Hover);
        g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? _t.Disabled
            : Equals(e.Item.Tag, AccentTag) ? _t.AccentText
            : _t.Text;
        // Shortcut column of menu items is drawn through here too; make it secondary.
        if (e.Item is ToolStripMenuItem mi && e.Text != mi.Text && e.Item.Enabled) e.TextColor = _t.SubText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_t.Border);
        var r = e.Item.ContentRectangle;
        if (e.Vertical)
        {
            int x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, S(e.Item, 6), x, e.Item.Height - S(e.Item, 6));
        }
        else
        {
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, S(e.Item, 10), y, e.Item.Width - S(e.Item, 10), y);
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* flat: same as menu back */ }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.ImageRectangle;
        using var glyph = _t.Glyph('\uE73E', Math.Max(8, Math.Min(r.Width, r.Height)), _t.Text); // CheckMark
        g.DrawImage(glyph, r.X + (r.Width - glyph.Width) / 2, r.Y + (r.Height - glyph.Height) / 2);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? _t.Disabled : _t.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { /* Windows 11 has none */ }

    protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var g = e.Graphics;
        if (item.Selected || item.Pressed)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, item.Width - 1f, item.Height - 1f), S(item, 4));
            using var b = new SolidBrush(item.Pressed ? _t.Pressed : _t.Hover);
            g.FillPath(b, path);
            g.SmoothingMode = SmoothingMode.Default;
        }
        int px = Math.Min(item.Width, item.Height) - S(item, 8);
        if (px < 6) return;
        using var glyph = _t.Glyph('\uE712', px, _t.Text); // More (…)
        g.DrawImage(glyph, (item.Width - px) / 2, (item.Height - px) / 2);
    }

    private sealed class ThemedColors : ProfessionalColorTable
    {
        private readonly Theme _t;
        public ThemedColors(Theme t) { _t = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => _t.MenuBack;
        public override Color ImageMarginGradientBegin => _t.MenuBack;
        public override Color ImageMarginGradientMiddle => _t.MenuBack;
        public override Color ImageMarginGradientEnd => _t.MenuBack;
        public override Color MenuBorder => _t.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _t.Hover;
        public override Color SeparatorDark => _t.Border;
        public override Color SeparatorLight => Color.Transparent;
        public override Color ToolStripBorder => _t.Border;
        public override Color StatusStripGradientBegin => _t.Back;
        public override Color StatusStripGradientEnd => _t.Back;
        public override Color OverflowButtonGradientBegin => _t.Back;
        public override Color OverflowButtonGradientMiddle => _t.Back;
        public override Color OverflowButtonGradientEnd => _t.Back;
    }
}

/// <summary>A flat, rounded push button in the theme colors (accent or neutral).</summary>
internal sealed class ThemedButton : Button
{
    private bool _hover, _down;

    /// <summary>Accent-filled (primary) button.</summary>
    [DefaultValue(false)]
    public bool Accent { get; set; }

    public ThemedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, text, border;
        if (Accent)
        {
            fill = !Enabled ? t.Hover : _down ? t.AccentPressed : _hover ? t.AccentHover : t.Accent;
            text = Enabled ? t.AccentText : t.Disabled;
            border = fill;
        }
        else
        {
            fill = _down ? t.Pressed : _hover ? t.ControlFillHover : t.ControlFill;
            text = Enabled ? t.Text : t.Disabled;
            border = t.ControlBorder;
        }

        float radius = LogicalToDeviceUnits(4);
        var r = new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f);
        using (var path = Theme.RoundRect(r, radius))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(border);
            g.DrawPath(p, path);
        }
        if (Focused && ShowFocusCues)
        {
            var fr = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using var path = Theme.RoundRect(fr, radius + 1);
            using var p = new Pen(t.Text, 1f);
            g.DrawPath(p, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft : 0));
    }
}
