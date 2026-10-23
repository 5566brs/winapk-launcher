using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WinApkLauncher;

public sealed class TileMenuEventArgs : EventArgs
{
    public TileMenuEventArgs(Profile? profile, bool isAddTile, Point screenLocation)
    {
        Profile = profile;
        IsAddTile = isAddTile;
        ScreenLocation = screenLocation;
    }

    /// <summary>The tile under the request, or null for empty space / the Add tile.</summary>
    public Profile? Profile { get; }
    public bool IsAddTile { get; }
    public Point ScreenLocation { get; }
}

/// <summary>
/// Desktop-style grid of app tiles (large icon + name) ending with an "Add app" tile.
/// Owner-drawn. Click selects, double-click / Enter activates, right-click / Apps key /
/// Shift+F10 asks for a context menu, arrows / Home / End / PgUp / PgDn move, typing
/// jumps to a matching name.
/// </summary>
internal sealed class TileView : ScrollableControl
{
    private List<Profile> _items = new();
    private int _selected = -1;          // index; _items.Count is the Add tile
    private int _hover = -1;
    private int _pressedAdd = -1;
    private bool _keyboardCues;
    private readonly ToolTip _tip = new() { InitialDelay = 700, ReshowDelay = 250, AutoPopDelay = 10000 };
    private int _tipFor = -2;
    private string _typeAhead = "";
    private DateTime _typeAt;
    private readonly Dictionary<(Image, int), Bitmap> _scaled = new();
    private readonly Dictionary<(string, int), Bitmap> _letters = new();
    private Font? _headerFont;
    private int _lineHeight;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Profile, Image?>? IconProvider { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Profile, bool>? IsRunning { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Profile, bool>? IsStarting { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Profile, bool>? HasProxy { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Profile, string>? TooltipProvider { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Header { get; set; } = "Apps";

    public event EventHandler? SelectionChanged;
    public event EventHandler<Profile>? Activated;
    public event EventHandler? AddRequested;
    public event EventHandler<TileMenuEventArgs>? MenuRequested;

    public TileView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick |
                 ControlStyles.StandardDoubleClick, true);
        TabStop = true;
        AutoScroll = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Apps";
    }

    // ----- data -----

    public Profile? SelectedProfile =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public bool AddTileSelected => _selected == _items.Count;

    public void SetItems(IEnumerable<Profile> items, string? selectId)
    {
        int old = _selected;
        _items = items.ToList();
        int idx = selectId != null ? _items.FindIndex(p => p.Id == selectId) : -1;
        if (idx < 0) idx = _items.Count == 0 ? 0 : Math.Clamp(old, 0, _items.Count - 1);
        _selected = idx;
        _hover = -1;
        _tipFor = -2;
        PruneCaches();
        UpdateScroll();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectById(string? id)
    {
        int idx = id != null ? _items.FindIndex(p => p.Id == id) : -1;
        if (idx >= 0) Select(idx, ensureVisible: true);
    }

    /// <summary>Repaint after icons / running state changed.</summary>
    public void RefreshTiles() { PruneCaches(); Invalidate(); }

    /// <summary>The label under a starting app's name.</summary>
    public const string StartingLabel = "Starting…";

    private int _spinFrame;

    /// <summary>Advance the "starting" spinner one step and repaint only the starting tiles.</summary>
    public void AdvanceSpinner()
    {
        if (IsStarting == null) return;
        _spinFrame = (_spinFrame + 12) % 360;
        for (int i = 0; i < _items.Count; i++)
            if (IsStarting(_items[i])) Invalidate(SpinnerRect(TileRect(i)));
    }

    private Rectangle IconRect(Rectangle tile) => new(tile.X + (tile.Width - IconPx) / 2, tile.Y + S(16), IconPx, IconPx);

    /// <summary>The spinner badge: the icon's bottom-right corner, like the proxy badge at the top right.</summary>
    private Rectangle SpinnerRect(Rectangle tile)
    {
        var ir = IconRect(tile);
        int bd = S(22);
        var r = new Rectangle(ir.Right - bd + S(4), ir.Bottom - bd + S(4), bd, bd);
        r.Inflate(S(2), S(2));
        return r;
    }

    /// <summary>For --screenshot: show a hover highlight on a tile without a mouse.</summary>
    internal void SimulateHover(int index) { _hover = index; Invalidate(); }

    /// <summary>For --screenshot: a key press delivered to this control only.</summary>
    internal void SimulateKey(Keys key) => OnKeyDown(new KeyEventArgs(key));

    private int Count => _items.Count + 1;

    // ----- metrics (device pixels) -----

    private int S(int v) => LogicalToDeviceUnits(v);
    private int IconPx => S(64);
    private int LineHeight => _lineHeight > 0 ? _lineHeight : _lineHeight = TextRenderer.MeasureText("Ag", Font).Height;
    private int TileW => S(124);
    private int TileH => S(16) + IconPx + S(10) + 2 * LineHeight + S(12);
    private int Gap => S(8);
    private int PadX => S(20);
    private int PadTop => S(52);
    private int PadBottom => S(20);

    private int Columns => Math.Max(1, (ClientSize.Width - 2 * PadX + Gap) / (TileW + Gap));

    private Font HeaderFont => _headerFont ??= new Font(Theme.Current.SemiboldFamily, Font.SizeInPoints * 13f / 9f);

    private Rectangle TileRect(int i, bool scrolled = true)
    {
        int cols = Columns;
        int x = PadX + i % cols * (TileW + Gap);
        int y = PadTop + i / cols * (TileH + Gap) + (scrolled ? AutoScrollPosition.Y : 0);
        return new Rectangle(x, y, TileW, TileH);
    }

    private int HitTest(Point p)
    {
        int cols = Columns;
        int x = p.X - PadX, y = p.Y - AutoScrollPosition.Y - PadTop;
        if (x < 0 || y < 0) return -1;
        int col = x / (TileW + Gap), row = y / (TileH + Gap);
        if (col >= cols || x % (TileW + Gap) >= TileW || y % (TileH + Gap) >= TileH) return -1;
        int i = row * cols + col;
        return i < Count ? i : -1;
    }

    private void UpdateScroll()
    {
        int rows = (Count + Columns - 1) / Columns;
        int h = PadTop + rows * (TileH + Gap) - Gap + PadBottom;
        if (AutoScrollMinSize.Height != h) AutoScrollMinSize = new Size(0, h);
    }

    private void EnsureVisible(int i)
    {
        if (i < 0) return;
        var r = TileRect(i, scrolled: false);
        int top = -AutoScrollPosition.Y, bottom = top + ClientSize.Height;
        if (r.Top - Gap < top) AutoScrollPosition = new Point(0, i < Columns ? 0 : r.Top - Gap);
        else if (r.Bottom + Gap > bottom) AutoScrollPosition = new Point(0, r.Bottom + Gap - ClientSize.Height);
        Invalidate();
    }

    private void Select(int i, bool ensureVisible)
    {
        if (i < 0 || i >= Count) return;
        if (i != _selected)
        {
            InvalidateTile(_selected);
            _selected = i;
            InvalidateTile(_selected);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (ensureVisible) EnsureVisible(i);
    }

    private void InvalidateTile(int i)
    {
        if (i < 0 || i >= Count) return;
        var r = TileRect(i);
        r.Inflate(S(4), S(4));
        Invalidate(r);
    }

    // ----- painting -----

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(t.Content);

        int y0 = AutoScrollPosition.Y;
        var hp = new Point(PadX, y0 + S(16));
        TextRenderer.DrawText(g, Header, HeaderFont, hp, t.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var hs = TextRenderer.MeasureText(g, Header, HeaderFont, Size.Empty, TextFormatFlags.NoPadding);
        var count = _items.Count == 1 ? "1 app" : $"{_items.Count} apps";
        var cs = TextRenderer.MeasureText(g, count, Font, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, count, Font, new Point(hp.X + hs.Width + S(10), hp.Y + hs.Height - cs.Height - S(1)),
            t.SubText, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        for (int i = 0; i < Count; i++)
        {
            var r = TileRect(i);
            var hit = r;
            hit.Inflate(S(4), S(4));
            if (!hit.IntersectsWith(e.ClipRectangle)) continue;
            DrawTile(g, i, r, t);
        }

        if (_items.Count == 0) DrawEmptyState(g, t);
    }

    /// <summary>Shown under the Add tile while there are no profiles yet.</summary>
    public const string EmptyTitle = "No apps yet";
    public const string EmptyHint = "Drag an .apk or .xapk here, or click Add app.";

    private void DrawEmptyState(Graphics g, Theme t)
    {
        int top = TileRect(0).Bottom + S(24);
        int areaH = ClientSize.Height - top - PadBottom;
        using var titleFont = new Font(t.SemiboldFamily, Font.SizeInPoints * 12f / 9f);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var ts = TextRenderer.MeasureText(g, EmptyTitle, titleFont, Size.Empty, flags);
        var hs = TextRenderer.MeasureText(g, EmptyHint, Font, Size.Empty, flags);
        int glyphPx = S(48);
        int blockH = glyphPx + S(16) + ts.Height + S(6) + hs.Height;
        int y = top + Math.Max(0, (areaH - blockH) / 2);
        int cx = ClientSize.Width / 2;

        using (var glyph = t.Glyph('', glyphPx, Theme.Blend(t.SubText, t.Content, 0.35f))) // Package
            g.DrawImage(glyph, cx - glyphPx / 2, y);
        y += glyphPx + S(16);
        TextRenderer.DrawText(g, EmptyTitle, titleFont, new Point(cx - ts.Width / 2, y), t.Text, flags);
        y += ts.Height + S(6);
        TextRenderer.DrawText(g, EmptyHint, Font, new Point(cx - hs.Width / 2, y), t.SubText, flags);
    }

    private void DrawTile(Graphics g, int i, Rectangle r, Theme t)
    {
        bool isAdd = i == _items.Count;
        bool sel = i == _selected, hov = i == _hover;
        float radius = S(8);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
        Color? fill = sel ? (Focused ? t.Selection : t.SelectionInactive) : hov ? t.Hover : null;
        if (sel && hov) fill = Theme.Blend(fill!.Value, t.Dark ? Color.White : Color.Black, 0.04f);
        if (fill != null)
        {
            using var path = Theme.RoundRect(rf, radius);
            using var b = new SolidBrush(fill.Value);
            g.FillPath(b, path);
            if (sel && Focused)
            {
                using var pen = new Pen(Theme.Blend(t.Selection, t.Accent, 0.55f));
                g.DrawPath(pen, path);
            }
        }
        if (sel && Focused && _keyboardCues)
        {
            var fr = RectangleF.Inflate(rf, S(2), S(2));
            using var path = Theme.RoundRect(fr, radius + S(2));
            using var pen = new Pen(t.Text, Math.Max(1.5f, S(2) * 0.85f));
            g.DrawPath(pen, path);
        }

        var ir = IconRect(r);
        string label;
        bool starting = false;
        Color textColor = t.Text;
        if (isAdd)
        {
            label = "Add app";
            textColor = hov || sel ? t.Text : t.SubText;
            var ar = new RectangleF(ir.X + S(4) + 0.5f, ir.Y + S(4) + 0.5f, ir.Width - S(8) - 1f, ir.Height - S(8) - 1f);
            using (var path = Theme.RoundRect(ar, ir.Width * 0.2f))
            {
                using var pen = new Pen(hov || sel ? t.Accent : t.ControlBorder, Math.Max(1f, S(1) * 1.5f)) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, path);
            }
            int gp = S(26);
            using var plus = t.Glyph('\uE710', gp, t.Accent);
            g.DrawImage(plus, ir.X + (ir.Width - gp) / 2, ir.Y + (ir.Height - gp) / 2);
        }
        else
        {
            var p = _items[i];
            label = p.Name;
            Image? icon = null;
            try { icon = IconProvider?.Invoke(p); } catch { }
            var bmp = icon != null ? ScaledIcon(icon, IconPx) : LetterIcon(p.Name, IconPx);
            g.DrawImageUnscaled(bmp, ir.X, ir.Y);

            if (HasProxy?.Invoke(p) == true)
            {
                // A small accent badge with a globe glyph at the icon's top-right corner.
                int bd = S(22);
                var br = new Rectangle(ir.Right - bd + S(4), ir.Top - S(4), bd, bd);
                using (var path = Theme.RoundRect(br, bd / 2f))
                {
                    using var halo = new SolidBrush(t.Content);
                    g.FillPath(halo, Theme.RoundRect(RectangleF.Inflate(br, 1.5f, 1.5f), bd / 2f + 1.5f));
                    using var b = new SolidBrush(t.Accent);
                    g.FillPath(b, path);
                }
                using var glyph = t.Glyph('\uE774', bd - S(8), t.AccentText); // Globe
                g.DrawImage(glyph, br.X + (br.Width - glyph.Width) / 2, br.Y + (br.Height - glyph.Height) / 2);
            }

            starting = IsStarting?.Invoke(p) == true;
            if (starting) DrawSpinner(g, t, SpinnerRect(r));
            else if (IsRunning?.Invoke(p) == true)
            {
                float w = S(16), h = S(4);
                var pill = new RectangleF(r.X + (r.Width - w) / 2f, r.Bottom - h - S(5), w, h);
                using var path = Theme.RoundRect(pill, h / 2);
                using var b = new SolidBrush(t.Accent);
                g.FillPath(b, path);
            }
        }
        g.SmoothingMode = SmoothingMode.Default;

        var tr = new Rectangle(r.X + S(6), ir.Bottom + S(10), r.Width - S(12), 2 * LineHeight + S(2));
        if (starting)
        {
            // One line of name, then "Starting…".
            const TextFormatFlags one = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine |
                                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, label, Font, new Rectangle(tr.X, tr.Y, tr.Width, LineHeight), textColor, one);
            TextRenderer.DrawText(g, StartingLabel, Font, new Rectangle(tr.X, tr.Y + LineHeight, tr.Width, LineHeight + S(2)),
                t.Dark ? Theme.Blend(t.Accent, Color.White, 0.35f) : t.Accent, one);
            return;
        }
        TextRenderer.DrawText(g, label, Font, tr, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak |
            TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
    }

    /// <summary>A small badge with a faint ring and a rotating accent arc.</summary>
    private void DrawSpinner(Graphics g, Theme t, Rectangle area)
    {
        var br = Rectangle.Inflate(area, -S(2), -S(2));
        using (var halo = new SolidBrush(t.Content))
            g.FillEllipse(halo, RectangleF.Inflate(br, 1.5f, 1.5f));
        using (var disc = new SolidBrush(t.Dark ? Theme.Blend(t.Content, Color.White, 0.08f) : Color.White))
            g.FillEllipse(disc, br);
        float w = Math.Max(2f, S(2) * 1.25f);
        var ring = RectangleF.Inflate(br, -w * 1.4f, -w * 1.4f);
        using (var track = new Pen(Theme.Blend(t.Content, t.Accent, 0.25f), w))
            g.DrawEllipse(track, ring);
        using var arc = new Pen(t.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, ring, _spinFrame * 1f - 90f, 100f);
    }

    private Bitmap ScaledIcon(Image src, int px)
    {
        if (!_scaled.TryGetValue((src, px), out var b))
            _scaled[(src, px)] = b = AppIcons.Scaled(src, px);
        return b;
    }

    private Bitmap LetterIcon(string name, int px)
    {
        if (!_letters.TryGetValue((name, px), out var b))
            _letters[(name, px)] = b = AppIcons.LetterTile(name, px, Theme.Current.SemiboldFamily);
        return b;
    }

    private void PruneCaches()
    {
        // Drop scaled copies whose source icon is no longer in use (icons replaced / profiles removed).
        var live = new HashSet<Image>();
        foreach (var p in _items)
        {
            try { if (IconProvider?.Invoke(p) is Image img) live.Add(img); } catch { }
        }
        foreach (var k in _scaled.Keys.Where(k => !live.Contains(k.Item1) || k.Item2 != IconPx).ToList())
        {
            _scaled[k].Dispose();
            _scaled.Remove(k);
        }
        var names = new HashSet<string>(_items.Select(p => p.Name));
        foreach (var k in _letters.Keys.Where(k => !names.Contains(k.Item1) || k.Item2 != IconPx).ToList())
        {
            _letters[k].Dispose();
            _letters.Remove(k);
        }
    }

    // ----- input -----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused) Focus();
        _keyboardCues = false;
        int i = HitTest(e.Location);
        if (i >= 0 && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)) Select(i, ensureVisible: false);
        _pressedAdd = e.Button == MouseButtons.Left && i == _items.Count ? i : -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && _pressedAdd >= 0 && i == _pressedAdd)
        {
            // The Add tile behaves like a button: a single click is enough.
            _pressedAdd = -1;
            AddRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _pressedAdd = -1;
        if (e.Button == MouseButtons.Right)
        {
            var p = i >= 0 && i < _items.Count ? _items[i] : null;
            MenuRequested?.Invoke(this, new TileMenuEventArgs(p, i == _items.Count, PointToScreen(e.Location)));
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Location);
        if (i >= 0 && i < _items.Count) Activated?.Invoke(this, _items[i]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    private void SetHover(int i)
    {
        if (i == _hover) return;
        InvalidateTile(_hover);
        _hover = i;
        InvalidateTile(_hover);
        if (_tipFor != i)
        {
            _tipFor = i;
            string? text = i >= 0 && i < _items.Count ? TooltipProvider?.Invoke(_items[i]) ?? _items[i].Name
                : i == _items.Count ? "Add an app (or drop an .apk / .xapk here)" : null;
            _tip.SetToolTip(this, text);
        }
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
        SetHover(HitTest(PointToClient(Cursor.Position)));
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                return true;
            case Keys.Enter:
                return (keyData & Keys.Modifiers) == 0;
        }
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int cols = Columns;
        int visibleRows = Math.Max(1, ClientSize.Height / (TileH + Gap));
        int cur = _selected < 0 ? 0 : _selected;
        int target = -1;
        switch (e.KeyCode)
        {
            case Keys.Left: target = cur - 1; break;
            case Keys.Right: target = cur + 1; break;
            case Keys.Up: target = cur - cols; break;
            case Keys.Down: target = cur + cols; break;
            case Keys.Home: target = 0; break;
            case Keys.End: target = Count - 1; break;
            case Keys.PageUp: target = Math.Max(cur % cols, cur - cols * visibleRows); break;
            case Keys.PageDown: target = cur + cols * visibleRows; break;
            case Keys.Enter:
                if (e.Modifiers != 0) return;
                if (SelectedProfile is Profile p) Activated?.Invoke(this, p);
                else if (AddTileSelected) AddRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                ShowMenuForSelection();
                e.Handled = true;
                return;
            default:
                return;
        }
        e.Handled = true;
        _keyboardCues = true;
        if (target < 0) target = e.KeyCode == Keys.Left ? 0 : cur;
        if (target >= Count)
            target = cur / cols < (Count - 1) / cols ? Count - 1 : cur; // move into a short last row
        Select(target, ensureVisible: true);
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar) || _items.Count == 0) return;
        if (DateTime.UtcNow - _typeAt > TimeSpan.FromSeconds(1)) _typeAhead = "";
        _typeAt = DateTime.UtcNow;
        _typeAhead += e.KeyChar;
        bool single = _typeAhead.Length == 1;
        int start = single ? _selected + 1 : Math.Max(0, _selected);
        for (int k = 0; k < _items.Count; k++)
        {
            int i = (start + k) % _items.Count;
            if (_items[i].Name.StartsWith(_typeAhead, StringComparison.CurrentCultureIgnoreCase))
            {
                _keyboardCues = true;
                Select(i, ensureVisible: true);
                break;
            }
        }
        e.Handled = true;
    }

    public void ShowMenuForSelection()
    {
        if (_selected < 0) return;
        var r = TileRect(_selected);
        var pt = PointToScreen(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        MenuRequested?.Invoke(this, new TileMenuEventArgs(SelectedProfile, AddTileSelected, pt));
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ResetMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ResetMetrics();
    }

    private void ResetMetrics()
    {
        _headerFont?.Dispose();
        _headerFont = null;
        _lineHeight = 0;
        PruneCaches();
        UpdateScroll();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Current.ApplyControlTheme(this); // themed (dark) scroll bar
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _headerFont?.Dispose();
            foreach (var b in _scaled.Values) b.Dispose();
            foreach (var b in _letters.Values) b.Dispose();
            _scaled.Clear();
            _letters.Clear();
        }
        base.Dispose(disposing);
    }
}
