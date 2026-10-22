using System.Diagnostics;

namespace WinApkLauncher;

/// <summary>
/// The launcher window: app tiles (default) or a detail list of profiles, a toolbar
/// with every action, context menus, and a status bar.
/// </summary>
public sealed class MainForm : Form
{
    private readonly LauncherStore _store;
    private readonly Theme _t = Theme.Current;
    private readonly ToolStrip _toolbar = new();
    private readonly Panel _content = new();
    private readonly TileView _tiles = new();
    private readonly Panel _listHost = new();
    private readonly ListView _list = new();
    private readonly ImageList _listIcons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _summary = new();
    private readonly ContextMenuStrip _itemMenu = new();
    private readonly ContextMenuStrip _spaceMenu = new();
    private readonly List<LaunchedProcess> _running = new();
    private readonly List<(ToolStripItem Item, char Glyph, bool OnAccent)> _glyphItems = new();

    private ToolStripButton _launchBtn = null!, _addBtn = null!, _editBtn = null!, _dupBtn = null!, _delBtn = null!;
    private ToolStripButton _logBtn = null!, _folderBtn = null!, _runtimeBtn = null!, _tilesBtn = null!, _listBtn = null!;
    private ToolStripButton _proxyLogBtn = null!;
    private ToolStripMenuItem _miTiles = null!, _miList = null!, _miProxyLogItem = null!;
    private readonly List<ToolStripItem> _needsSelection = new();

    private string? _selectedId;
    private bool _tilesMode;
    private bool _syncing;
    private Icon? _windowIcon;

    // --screenshot <dir>: capture tiles, list and the Edit dialog, then exit. Nothing is saved.
    private readonly string? _shotDir;
    private readonly string _shotSuffix;
    private Action<Form>? _dialogShownHook;

    private static readonly string[] PackageExtensions = { ".apk", ".xapk", ".apkm", ".apks" };

    // Packages to add that arrived while a dialog was open (another start, Explorer "Open with").
    private readonly Queue<string> _pendingAdds = new();
    private readonly System.Windows.Forms.Timer _pendingTimer = new() { Interval = 400 };
    private readonly Label _listEmpty = new();

    /// <param name="addPath">A package to open the Add dialog with once the window is shown.</param>
    public MainForm(string? screenshotDir = null, string shotSuffix = "", string? addPath = null)
    {
        _shotDir = screenshotDir;
        _shotSuffix = shotSuffix;
        _store = LauncherStore.Load();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppInfo.ProductName;
        ClientSize = new Size(980, 600);
        MinimumSize = new Size(700, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        AllowDrop = true;

        BuildToolbar();
        BuildMenus();
        BuildTiles();
        BuildList();
        BuildStatusBar();

        _content.Dock = DockStyle.Fill;
        _content.BackColor = _t.Content;
        _content.Controls.Add(_tiles);
        _content.Controls.Add(_listHost);

        Controls.Add(_content);
        Controls.Add(_toolbar);
        Controls.Add(_statusStrip);
        ResumeLayout(false);
        PerformLayout();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _tilesMode = _store.ViewMode != LauncherStore.ViewList;
        _selectedId = _store.Profiles.FirstOrDefault()?.Id;
        AppIcons.IconReady += OnIconReady;
        FormClosing += (_, _) => { if (_shotDir == null) SaveStore(); };
        Load += (_, _) => ApplyMetrics();
        DpiChanged += (_, _) => BeginInvoke(ApplyMetrics);
        Shown += (_, _) =>
        {
            if (_shotDir != null) { RunScreenshots(); return; }
            if (!string.IsNullOrWhiteSpace(addPath)) QueueAdd(addPath);
        };
        _pendingTimer.Tick += (_, _) => ProcessPendingAdds();

        RefreshAll();
        ApplyViewMode(focus: false);
    }

    // =====================================================================
    // UI construction
    // =====================================================================

    private void BuildToolbar()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Renderer = new ThemedRenderer(_t);
        _toolbar.BackColor = _t.Back;
        _toolbar.ForeColor = _t.Text;
        _toolbar.Font = Font;
        _toolbar.ShowItemToolTips = true;
        _toolbar.CanOverflow = true;
        _toolbar.Stretch = true;
        _toolbar.AccessibleName = "Actions";

        ToolStripButton Btn(string text, char glyph, string tip, Action onClick,
            bool accent = false, ToolStripItemAlignment align = ToolStripItemAlignment.Left)
        {
            var b = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ImageScaling = ToolStripItemImageScaling.None,
                ToolTipText = tip,
                Alignment = align,
                AutoToolTip = false,
            };
            if (accent) b.Tag = ThemedRenderer.AccentTag;
            b.Click += (_, _) => onClick();
            _toolbar.Items.Add(b);
            _glyphItems.Add((b, glyph, accent));
            return b;
        }
        ToolStripSeparator Sep(ToolStripItemAlignment align = ToolStripItemAlignment.Left)
        {
            var s = new ToolStripSeparator { Alignment = align };
            _toolbar.Items.Add(s);
            return s;
        }

        _launchBtn = Btn("Launch", '\uE768', "Launch the selected app (Enter)", LaunchSelected, accent: true);
        Sep();
        _addBtn = Btn("Add app", '\uE710', "Add an app profile (Ctrl+N). You can also drop an .apk / .xapk on the window.", () => AddProfile());
        _editBtn = Btn("Edit", '\uE70F', "Edit the selected profile (F2)", EditSelected);
        _dupBtn = Btn("Duplicate", '\uE8C8', "Duplicate the selected profile (Ctrl+D)", DuplicateSelected);
        _delBtn = Btn("Delete", '\uE74D', "Delete the selected profile (Del). The data root on disk is kept.", DeleteSelected);
        Sep();
        _logBtn = Btn("Run log", '\uE9F9', "Open the last run log (Ctrl+L)", OpenLastLog);
        _folderBtn = Btn("Data folder", '\uE838', "Open the selected profile's data folder", OpenDataFolder);
        _proxyLogBtn = Btn("Proxy log", '\uE774', "Open the per-app network proxy log", OpenProxyLog);

        // Right-aligned items are laid out from the right edge in collection order.
        _listBtn = Btn("List", '\uE8FD', "List view (Ctrl+2)", () => SetViewMode(tiles: false), align: ToolStripItemAlignment.Right);
        _tilesBtn = Btn("Tiles", '\uECA5', "Tiles view (Ctrl+1)", () => SetViewMode(tiles: true), align: ToolStripItemAlignment.Right);
        Sep(ToolStripItemAlignment.Right);
        _runtimeBtn = Btn("Runtime path…", '\uE713', RuntimeTooltip(), () => EditRuntimePath(),
            align: ToolStripItemAlignment.Right);
        // Always in the "…" overflow menu.
        var about = Btn($"About {AppInfo.ProductName}", '\uE946', $"{AppInfo.ProductName} {AppInfo.Version}", ShowAbout);
        about.Overflow = ToolStripItemOverflow.Always;

        // When the window is narrow, the secondary buttons go to the "…" overflow first.
        _launchBtn.Overflow = _tilesBtn.Overflow = _listBtn.Overflow = ToolStripItemOverflow.Never;

        _needsSelection.AddRange(new ToolStripItem[] { _launchBtn, _editBtn, _dupBtn, _delBtn, _folderBtn, _proxyLogBtn });
    }

    private void BuildMenus()
    {
        foreach (var m in new[] { _itemMenu, _spaceMenu })
        {
            m.Renderer = new ThemedRenderer(_t);
            m.Font = Font;
            m.BackColor = _t.MenuBack;
            m.ForeColor = _t.Text;
            m.ShowImageMargin = true;
            m.HandleCreated += (s, _) => Theme.RoundCorners(((Control)s!).Handle);
        }

        ToolStripMenuItem Item(ContextMenuStrip menu, string text, char glyph, string? keys, Action onClick, bool bold = false)
        {
            var mi = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Tag = bold ? "bold" : null };
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
            _glyphItems.Add((mi, glyph, false));
            return mi;
        }

        var launch = Item(_itemMenu, "Launch", '\uE768', "Enter", LaunchSelected, bold: true);
        _itemMenu.Items.Add(new ToolStripSeparator());
        Item(_itemMenu, "Edit…", '\uE70F', "F2", EditSelected);
        Item(_itemMenu, "Duplicate", '\uE8C8', "Ctrl+D", DuplicateSelected);
        Item(_itemMenu, "Delete", '\uE74D', "Del", DeleteSelected);
        _itemMenu.Items.Add(new ToolStripSeparator());
        Item(_itemMenu, "Open run log", '\uE9F9', "Ctrl+L", OpenLastLog);
        _miProxyLogItem = Item(_itemMenu, "Open proxy log", '\uE774', null, OpenProxyLog);
        Item(_itemMenu, "Open data folder", '\uE838', null, OpenDataFolder);
        _itemMenu.Items.Add(new ToolStripSeparator());
        Item(_itemMenu, "Create desktop shortcut", '\uE8A7', null, CreateDesktopShortcut);
        _ = launch;

        Item(_spaceMenu, "Add app…", '\uE710', "Ctrl+N", () => AddProfile());
        _spaceMenu.Items.Add(new ToolStripSeparator());
        _miTiles = Item(_spaceMenu, "Tiles view", '\uECA5', "Ctrl+1", () => SetViewMode(tiles: true));
        _miList = Item(_spaceMenu, "List view", '\uE8FD', "Ctrl+2", () => SetViewMode(tiles: false));
        _spaceMenu.Items.Add(new ToolStripSeparator());
        Item(_spaceMenu, "Runtime path…", '\uE713', null, () => EditRuntimePath());
    }

    private void BuildTiles()
    {
        _tiles.Dock = DockStyle.Fill;
        _tiles.BackColor = _t.Content;
        _tiles.ForeColor = _t.Text;
        _tiles.AllowDrop = true;
        _tiles.IconProvider = p => AppIcons.Get(p.ApkPath);
        _tiles.IsRunning = IsRunning;
        _tiles.HasProxy = p => p.HasProxy;
        _tiles.TooltipProvider = TileTooltip;
        _tiles.SelectionChanged += (_, _) =>
        {
            if (_syncing || !_tilesMode) return;
            _selectedId = _tiles.SelectedProfile?.Id;
            UpdateUiState();
        };
        _tiles.Activated += (_, p) => { _selectedId = p.Id; LaunchSelected(); };
        _tiles.AddRequested += (_, _) => AddProfile();
        _tiles.MenuRequested += (_, e) =>
        {
            if (e.Profile != null) { _selectedId = e.Profile.Id; UpdateUiState(); ShowMenu(_itemMenu, e.ScreenLocation); }
            else ShowMenu(_spaceMenu, e.ScreenLocation);
        };
        _tiles.DragEnter += OnDragEnter;
        _tiles.DragDrop += OnDragDrop;
    }

    private void BuildList()
    {
        _listHost.Dock = DockStyle.Fill;
        _listHost.BackColor = _t.Content;
        _listHost.Padding = new Padding(12, 10, 12, 0);
        _listHost.Controls.Add(_list);

        // Empty state for the list view (the tiles view draws its own).
        _listEmpty.Dock = DockStyle.Fill;
        _listEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _listEmpty.ForeColor = _t.SubText;
        _listEmpty.BackColor = _t.Content;
        _listEmpty.Text = TileView.EmptyTitle + "\n\n" + TileView.EmptyHint;
        _listEmpty.Visible = false;
        _listEmpty.AllowDrop = true;
        _listEmpty.DragEnter += OnDragEnter;
        _listEmpty.DragDrop += OnDragDrop;
        _listEmpty.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(_spaceMenu, Cursor.Position); };
        _listHost.Controls.Add(_listEmpty);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.BackColor = _t.Content;
        _list.ForeColor = _t.Text;
        _list.AllowDrop = true;
        _list.SmallImageList = _listIcons;
        _list.Columns.Add("Profile", 220);
        _list.Columns.Add("APK", 170);
        _list.Columns.Add("Data root", 260);
        _list.Columns.Add("Size (dp)", 80);
        _list.Columns.Add("API", 50);
        _list.Columns.Add("Multi", 55);
        _list.Columns.Add("Proxy", 150);
        _list.Columns.Add("Status", 80);
        _list.DoubleClick += (_, _) => LaunchSelected();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _tilesMode) return;
            _selectedId = _list.SelectedItems.Count > 0 ? (_list.SelectedItems[0].Tag as Profile)?.Id : null;
            UpdateUiState();
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Modifiers == 0) { LaunchSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Apps || e.KeyCode == Keys.F10 && e.Shift)
            {
                var it = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
                var pt = it != null ? new Point(it.Bounds.Left + 24, it.Bounds.Bottom) : new Point(24, 24);
                ShowMenu(it != null ? _itemMenu : _spaceMenu, _list.PointToScreen(pt));
                e.Handled = true;
            }
        };
        _list.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location).Item;
            if (hit != null) { hit.Selected = true; ShowMenu(_itemMenu, _list.PointToScreen(e.Location)); }
            else ShowMenu(_spaceMenu, _list.PointToScreen(e.Location));
        };
        _list.Resize += (_, _) => FitListColumns();
        _list.HandleCreated += (_, _) => _t.ApplyControlTheme(_list, explorerInLight: true);
        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;
    }

    private void BuildStatusBar()
    {
        _statusStrip.Renderer = new ThemedRenderer(_t);
        _statusStrip.BackColor = _t.Back;
        _statusStrip.ForeColor = _t.SubText;
        _statusStrip.Font = Font;
        _statusStrip.SizingGrip = false;
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = _t.SubText;
        _summary.ForeColor = _t.SubText;
        _summary.TextAlign = ContentAlignment.MiddleRight;
        _statusStrip.Items.Add(_status);
        _statusStrip.Items.Add(_summary);
    }

    /// <summary>Device-pixel metrics that WinForms does not scale for us (toolbar items, glyphs, columns).</summary>
    private void ApplyMetrics()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        // ToolStrips don't inherit the form font; re-derive from it so a DPI change rescales them too.
        _toolbar.Font = Font;
        _statusStrip.Font = Font;
        _launchBtn.Font = new Font(_t.SemiboldFamily, Font.SizeInPoints);
        foreach (var m in new[] { _itemMenu, _spaceMenu })
        {
            m.Font = Font;
            foreach (ToolStripItem item in m.Items)
                if (Equals(item.Tag, "bold")) item.Font = new Font(_t.SemiboldFamily, Font.SizeInPoints);
        }
        _toolbar.SuspendLayout();
        _toolbar.Padding = new Padding(S(10), S(6), S(10), S(6));
        _toolbar.ImageScalingSize = new Size(S(16), S(16));
        foreach (ToolStripItem item in _toolbar.Items)
        {
            if (item is ToolStripSeparator) { item.Margin = new Padding(S(4), 0, S(4), 0); continue; }
            item.Padding = new Padding(S(8), S(5), S(8), S(5));
            item.Margin = new Padding(S(2), 0, S(2), 0);
        }
        _toolbar.ResumeLayout(true);

        foreach (var m in new[] { _itemMenu, _spaceMenu })
        {
            m.ImageScalingSize = new Size(S(16), S(16));
            m.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in m.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(S(2), S(4), S(2), S(4));
        }

        foreach (var (item, glyph, onAccent) in _glyphItems)
        {
            var old = item.Image;
            item.Image = _t.Glyph(glyph, S(16), onAccent ? _t.AccentText : _t.Text);
            old?.Dispose();
        }

        _statusStrip.Padding = new Padding(S(12), S(2), S(12), S(2));
        _listHost.Padding = new Padding(S(12), S(10), S(12), 0);

        int[] widths = { 220, 160, 240, 80, 50, 55, 150, 80 };
        for (int i = 0; i < _list.Columns.Count && i < widths.Length; i++) _list.Columns[i].Width = S(widths[i]);
        RebuildListIcons();
        FitListColumns();
        _tiles.RefreshTiles();
    }

    /// <summary>Let the "Data root" column absorb spare width so the list fills the window.</summary>
    private void FitListColumns()
    {
        if (_list.Columns.Count < 7 || _list.ClientSize.Width <= 0) return;
        int others = 0;
        for (int i = 0; i < _list.Columns.Count; i++) if (i != 2) others += _list.Columns[i].Width;
        int w = _list.ClientSize.Width - others - 2;
        _list.Columns[2].Width = Math.Max(LogicalToDeviceUnits(160), w);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
        try
        {
            // The app icon (assets\WinApkLauncher.ico, embedded), all sizes; a drawn fallback otherwise.
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("WinApkLauncher.ico");
            if (s != null) _windowIcon = new Icon(s);
            else
            {
                using var bmp = WindowIconBitmap(LogicalToDeviceUnits(32));
                _windowIcon = Icon.FromHandle(bmp.GetHicon());
            }
            Icon = _windowIcon;
        }
        catch { }
    }

    /// <summary>A small accent "app grid" icon for the title bar / taskbar.</summary>
    private Bitmap WindowIconBitmap(int px)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, px - 1f, px - 1f), px * 0.22f))
        using (var b = new SolidBrush(_t.Accent))
            g.FillPath(b, path);
        using var glyph = _t.Glyph('\uECA5', (int)(px * 0.6f), _t.AccentText);
        g.DrawImage(glyph, (px - glyph.Width) / 2, (px - glyph.Height) / 2);
        return bmp;
    }

    // =====================================================================
    // State
    // =====================================================================

    private Profile? Selected => _selectedId == null ? null : _store.Profiles.FirstOrDefault(p => p.Id == _selectedId);

    private void RefreshAll()
    {
        _syncing = true;
        try
        {
            if (_selectedId != null && _store.Profiles.All(p => p.Id != _selectedId)) _selectedId = null;
            _tiles.SetItems(_store.Profiles, _selectedId);
            if (_selectedId == null && _tilesMode) _selectedId = _tiles.SelectedProfile?.Id;

            RebuildListIcons();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in _store.Profiles)
            {
                var it = new ListViewItem(p.Name) { Tag = p, ImageKey = p.Id, UseItemStyleForSubItems = false };
                it.SubItems.Add(Path.GetFileName(p.ApkPath), _t.SubText, _t.Content, Font);
                it.SubItems.Add(p.DataRoot, _t.SubText, _t.Content, Font);
                it.SubItems.Add($"{p.PhoneWidthDp}x{p.PhoneHeightDp}");
                it.SubItems.Add(p.SdkInt > 0 ? p.SdkInt.ToString() : "29*");
                it.SubItems.Add(p.AllowMultipleInstances ? "yes" : "");
                it.SubItems.Add(p.ProxyLabel, p.HasProxy ? _t.Text : _t.SubText, _t.Content, Font);
                bool running = IsRunning(p);
                it.SubItems.Add(running ? "running" : "", running ? _t.Accent : _t.Text, _t.Content, Font);
                if (p.Id == _selectedId) it.Selected = true;
                _list.Items.Add(it);
            }
            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0 && !_tilesMode)
            {
                _list.Items[0].Selected = true;
                _selectedId = (_list.Items[0].Tag as Profile)?.Id;
            }
            _list.EndUpdate();
            FitListColumns();
            bool empty = _store.Profiles.Count == 0;
            _listEmpty.Visible = empty;
            _list.Visible = !empty;
        }
        finally { _syncing = false; }
        UpdateUiState();
    }

    private void RebuildListIcons()
    {
        // 22px icons in 28px cells: the image height sets the row height, so rows get some air.
        int cell = LogicalToDeviceUnits(28), px = LogicalToDeviceUnits(22);
        _listIcons.Images.Clear();
        if (_listIcons.ImageSize.Width != cell) _listIcons.ImageSize = new Size(cell, cell);
        foreach (var p in _store.Profiles)
        {
            var bmp = new Bitmap(cell, cell);
            try
            {
                var img = AppIcons.Get(p.ApkPath);
                using var icon = img != null ? AppIcons.Scaled(img, px) : AppIcons.LetterTile(p.Name, px, _t.SemiboldFamily);
                using var g = Graphics.FromImage(bmp);
                g.DrawImageUnscaled(icon, (cell - px) / 2, (cell - px) / 2);
            }
            catch { }
            _listIcons.Images.Add(p.Id, bmp); // the ImageList keeps the bitmap
        }
        _list.Invalidate();
    }

    private void OnIconReady()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _tiles.RefreshTiles();
                RebuildListIcons();
            });
        }
        catch { }
    }

    private void UpdateUiState()
    {
        var p = Selected;
        foreach (var item in _needsSelection) item.Enabled = p != null;
        foreach (ToolStripItem item in _itemMenu.Items) if (item is ToolStripMenuItem) item.Enabled = p != null;
        // The proxy log only exists once a profile has a proxy.
        _proxyLogBtn.Enabled = p?.HasProxy == true;
        _miProxyLogItem.Enabled = p?.HasProxy == true;

        int running = _store.Profiles.Count(IsRunning);
        _summary.Text = $"{_store.Profiles.Count} {(_store.Profiles.Count == 1 ? "app" : "apps")}" +
                        (running > 0 ? $"  •  {running} running" : "");
        _runtimeBtn.ToolTipText = RuntimeTooltip();
        if (_store.ResolveRuntime() == null)
        {
            _status.Text = "winapk-runtime.exe not found  •  use Runtime path… to locate it";
            return;
        }
        if (p == null) { _status.Text = $"{_store.Profiles.Count} profiles  •  settings: {LauncherStore.StorePath}"; return; }
        var lp = _running.LastOrDefault(r => r.Profile.Id == p.Id);
        _status.Text = lp != null ? $"last log: {lp.LogPath}"
                                  : $"runtime data/logs: {RuntimeLauncher.RuntimeLogDir(p)}";
    }

    private bool IsRunning(Profile p) =>
        _running.Any(r => r.Profile.Id == p.Id && !SafeExited(r.Process));

    private static bool SafeExited(Process pr) { try { return pr.HasExited; } catch { return true; } }

    private string TileTooltip(Profile p)
    {
        var lines = new List<string>
        {
            p.Name,
            $"{(string.IsNullOrWhiteSpace(p.ApkPath) ? "(no APK)" : Path.GetFileName(p.ApkPath))}  •  " +
            $"{p.PhoneWidthDp}x{p.PhoneHeightDp} dp  •  API {(p.SdkInt > 0 ? p.SdkInt.ToString() : "29 (default)")}",
            p.DataRoot,
        };
        if (p.HasProxy) lines.Add($"Proxy: {p.ProxyLabel}");
        if (IsRunning(p)) lines.Add("Running");
        return string.Join("\n", lines);
    }

    private void SetViewMode(bool tiles)
    {
        if (_tilesMode == tiles) { ApplyViewMode(focus: true); return; }
        _tilesMode = tiles;
        ApplyViewMode(focus: true);
        _store.ViewMode = tiles ? LauncherStore.ViewTiles : LauncherStore.ViewList;
        if (_shotDir == null) SaveStore();
    }

    private void ApplyViewMode(bool focus)
    {
        _syncing = true;
        try
        {
            _tiles.Visible = _tilesMode;
            _listHost.Visible = !_tilesMode;
            _tilesBtn.Checked = _tilesMode;
            _listBtn.Checked = !_tilesMode;
            _miTiles.Checked = _tilesMode;
            _miList.Checked = !_tilesMode;
            if (_tilesMode) _tiles.SelectById(_selectedId);
            else
            {
                foreach (ListViewItem it in _list.Items) it.Selected = (it.Tag as Profile)?.Id == _selectedId;
                if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
                {
                    _list.Items[0].Selected = true;
                    _selectedId = (_list.Items[0].Tag as Profile)?.Id;
                }
                if (_list.SelectedItems.Count > 0)
                {
                    _list.SelectedItems[0].Focused = true;
                    _list.SelectedItems[0].EnsureVisible();
                }
            }
        }
        finally { _syncing = false; }
        if (focus) { if (_tilesMode) _tiles.Focus(); else _list.Focus(); }
        UpdateUiState();
    }

    private void ShowMenu(ContextMenuStrip menu, Point screen)
    {
        UpdateUiState();
        menu.Show(screen);
    }

    private void SaveStore()
    {
        try { _store.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save {LauncherStore.StorePath}:\n{ex.Message}", "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N: AddProfile(); return true;
            case Keys.F2:
            case Keys.Control | Keys.E: EditSelected(); return true;
            case Keys.Control | Keys.D: DuplicateSelected(); return true;
            case Keys.Delete when Selected != null: DeleteSelected(); return true;
            case Keys.Control | Keys.L: OpenLastLog(); return true;
            case Keys.Control | Keys.D1: SetViewMode(tiles: true); return true;
            case Keys.Control | Keys.D2: SetViewMode(tiles: false); return true;
            case Keys.F5: RefreshAll(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =====================================================================
    // Actions (unchanged behaviour)
    // =====================================================================

    private void LaunchSelected()
    {
        var p = Selected;
        if (p == null) return;
        if (_shotDir != null || RuntimeLauncher.LaunchBlocked) return; // the --screenshot harness never starts the runtime

        // Runtime discovery: settings, then the bundled runtime, then ask.
        var runtime = _store.ResolveRuntime();
        if (runtime == null)
        {
            var ask = MessageBox.Show(this,
                "winapk-runtime.exe was not found.\n\n" +
                $"{AppInfo.ProductName} looks for the runtime in its settings and next to the launcher:\n" +
                $"{AppPaths.BundledRuntimeExe}\n\nLocate winapk-runtime.exe now?",
                "Runtime not found", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ask != DialogResult.OK || !EditRuntimePath()) return;
            runtime = _store.ResolveRuntime();
        }
        var err = RuntimeLauncher.Validate(runtime, p);
        if (err != null) { MessageBox.Show(this, err, "Cannot launch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        LaunchedProcess lp;
        try { lp = RuntimeLauncher.Start(runtime, p); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _running.Add(lp);

        lp.Process.Exited += (_, _) => OnProcessExited(lp);
        RefreshAll();
        _status.Text = $"launched ‘{p.Name}’ • log: {lp.LogPath}";
    }

    private void OnProcessExited(LaunchedProcess lp)
    {
        if (InvokeRequired) { try { BeginInvoke(() => OnProcessExited(lp)); } catch { } return; }
        var code = RuntimeLauncher.SafeExitCode(lp.Process);
        RefreshAll();
        // A quick non-zero exit is treated as a crashed launch.
        if (code != 0)
        {
            var tail = RuntimeLauncher.TailLog(lp.LogPath);
            var r = MessageBox.Show(this,
                $"‘{lp.Profile.Name}’ exited with code {code} (crashed launch).\n\n" +
                $"Log: {lp.LogPath}\n\n--- last lines ---\n{tail}\n\nOpen the full log?",
                "Launch crashed", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
            if (r == DialogResult.Yes) OpenInNotepad(lp.LogPath);
        }
    }

    private void AddProfile(string? apkPath = null)
    {
        var name = "New profile";
        if (!string.IsNullOrWhiteSpace(apkPath))
            name = Path.GetFileNameWithoutExtension(apkPath).Replace('_', ' ').Replace('-', ' ').Trim();
        var p = new Profile
        {
            Name = name,
            ApkPath = apkPath ?? "",
            PhoneWidthDp = 360,
            PhoneHeightDp = 800,
            DataRoot = _store.SuggestDataRoot(name),
        };
        using var dlg = new ProfileForm(p, isNew: true, n => _store.SuggestDataRoot(n));
        if (ShowProfileDialog(dlg) == DialogResult.OK)
        {
            _store.Profiles.Add(dlg.Result);
            _selectedId = dlg.Result.Id;
            SaveStore();
            RefreshAll();
            ApplyViewMode(focus: true);
        }
    }

    private void EditSelected()
    {
        var p = Selected;
        if (p == null) return;
        using var dlg = new ProfileForm(Clone(p), isNew: false);
        if (ShowProfileDialog(dlg) == DialogResult.OK)
        {
            var idx = _store.Profiles.FindIndex(x => x.Id == p.Id);
            if (idx >= 0) _store.Profiles[idx] = dlg.Result;
            SaveStore();
            RefreshAll();
        }
    }

    private void DuplicateSelected()
    {
        var p = Selected;
        if (p == null) return;
        var copy = Clone(p);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = p.Name + " (copy)";
        copy.DataRoot = _store.SuggestDataRoot(copy.Name);
        using var dlg = new ProfileForm(copy, isNew: true, n => _store.SuggestDataRoot(n));
        if (ShowProfileDialog(dlg) == DialogResult.OK)
        {
            _store.Profiles.Add(dlg.Result);
            _selectedId = dlg.Result.Id;
            SaveStore();
            RefreshAll();
            ApplyViewMode(focus: true);
        }
    }

    private DialogResult ShowProfileDialog(ProfileForm dlg)
    {
        if (_dialogShownHook != null) { var hook = _dialogShownHook; dlg.Shown += (_, _) => hook(dlg); }
        return dlg.ShowDialog(this);
    }

    private void DeleteSelected()
    {
        var p = Selected;
        if (p == null) return;
        if (MessageBox.Show(this, $"Delete profile ‘{p.Name}’?\n(The data root on disk is NOT deleted.)",
                "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int idx = _store.Profiles.FindIndex(x => x.Id == p.Id);
        _store.Profiles.RemoveAll(x => x.Id == p.Id);
        // Keep the selection on the neighbour, like Explorer does.
        _selectedId = _store.Profiles.Count > 0 ? _store.Profiles[Math.Clamp(idx, 0, _store.Profiles.Count - 1)].Id : null;
        SaveStore();
        RefreshAll();
        ApplyViewMode(focus: true);
    }

    private void OpenLastLog()
    {
        var p = Selected;
        var lp = p != null ? _running.LastOrDefault(r => r.Profile.Id == p.Id) : _running.LastOrDefault();
        if (lp == null) { MessageBox.Show(this, "No run log yet for this profile.", "Log", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        OpenInNotepad(lp.LogPath);
    }

    private void OpenDataFolder()
    {
        var p = Selected;
        if (p == null) return;
        try
        {
            Directory.CreateDirectory(p.DataRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.DataRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenProxyLog()
    {
        var p = Selected;
        if (p == null) return;
        if (!p.HasProxy)
        {
            MessageBox.Show(this, "This profile has no network proxy configured.", "Proxy log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var path = Path.Combine(RuntimeLauncher.RuntimeLogDir(p), "proxy.log");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"No proxy log yet.\nIt is written the first time ‘{p.Name}’ is launched with a proxy.\n\n{path}",
                "Proxy log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenInNotepad(path);
    }

    private void CreateDesktopShortcut()
    {
        var p = Selected;
        if (p == null) return;
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var linkPath = Path.Combine(desktop, RuntimeLauncher.MakeFileSafe(p.Name) + ".lnk");
            var iconPath = TryWriteShortcutIcon(p);

            // WScript.Shell via COM: a .lnk with no extra dependencies.
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = exe;
            link.Arguments = $"--launch {p.Id}";
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = $"Launch {p.Name} ({AppInfo.ProductName})";
            if (iconPath != null) link.IconLocation = iconPath + ",0";
            link.Save();

            _status.Text = $"desktop shortcut created: {linkPath}";
            MessageBox.Show(this, $"Shortcut created on the desktop:\n{linkPath}", "Desktop shortcut",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create the shortcut:\n" + ex.Message, "Desktop shortcut",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Write the profile's app icon as an .ico (PNG-in-ICO) into the icon cache; returns its path or null.</summary>
    private string? TryWriteShortcutIcon(Profile p)
    {
        try
        {
            var img = AppIcons.Get(p.ApkPath);
            using var square = img != null ? AppIcons.Scaled(img, 256) : AppIcons.LetterTile(p.Name, 256, _t.SemiboldFamily);
            var dir = AppIcons.CacheDir;
            Directory.CreateDirectory(dir);
            var icoPath = Path.Combine(dir, "shortcut-" + RuntimeLauncher.MakeFileSafe(p.Id) + ".ico");
            using var png = new MemoryStream();
            square.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            var pngBytes = png.ToArray();
            using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write((short)0); w.Write((short)1); w.Write((short)1);     // ICONDIR: reserved, type=icon, count=1
            w.Write((byte)0); w.Write((byte)0);                          // 0 = 256 px
            w.Write((byte)0); w.Write((byte)0);                          // colors, reserved
            w.Write((short)1); w.Write((short)32);                       // planes, bpp
            w.Write(pngBytes.Length);                                    // size of PNG
            w.Write(6 + 16);                                            // offset to PNG data
            w.Write(pngBytes);
            return icoPath;
        }
        catch { return null; }
    }

    /// <summary>The "Runtime path…" dialog. Returns true when a runtime was chosen.</summary>
    private bool EditRuntimePath()
    {
        var current = _store.ResolveRuntime();
        using var dlg = new OpenFileDialog
        {
            Title = "Locate winapk-runtime.exe",
            Filter = "winapk-runtime.exe|winapk-runtime.exe|Executables|*.exe|All files|*.*",
            FileName = current ?? "winapk-runtime.exe",
        };
        if (current != null) dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        // Choosing the bundled runtime stores nothing, so it keeps following the install folder.
        _store.RuntimeExe = string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(AppPaths.BundledRuntimeExe),
            StringComparison.OrdinalIgnoreCase) ? "" : dlg.FileName;
        SaveStore();
        UpdateUiState();
        return true;
    }

    private string RuntimeTooltip()
    {
        var exe = _store.ResolveRuntime(out var src);
        return "Locate winapk-runtime.exe\n" + src switch
        {
            RuntimeSource.Settings => exe + "  (chosen in settings)",
            RuntimeSource.Bundled => exe + "  (bundled)",
            _ => "Not found",
        };
    }

    private void ShowAbout()
    {
        var lines = new List<string>
        {
            $"{AppInfo.ProductName} {AppInfo.Version}",
            "A launcher and per-app settings manager for Win.apk, which runs Android apps on Windows.",
            "",
            $"Runtime:  {_store.DescribeRuntime()}",
        };
        try
        {
            if (File.Exists(AppPaths.BundledRuntimeSourceFile))
                lines.AddRange(File.ReadLines(AppPaths.BundledRuntimeSourceFile)
                    .Where(l => l.StartsWith("Source", StringComparison.OrdinalIgnoreCase) ||
                                l.StartsWith("Commit", StringComparison.OrdinalIgnoreCase))
                    .Take(3).Select(l => "  " + l.Trim()));
        }
        catch { }
        lines.AddRange(new[]
        {
            $"Settings: {LauncherStore.StorePath}",
            $"Data:     {AppPaths.LocalDir}",
            $"Mode:     {AppPaths.ModeLabel}",
            "",
            "License: GNU AGPL-3.0. Source: " + AppInfo.ProjectUrl,
            "Win.apk by israel2176, based on Mac.apk by kksimp.",
            "Not affiliated with Google, WhatsApp or Meta.",
        });
        MessageBox.Show(this, string.Join("\n", lines), $"About {AppInfo.ProductName}",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ----- packages handed over by Explorer ("Open with") or by a second start -----

    /// <summary>Called on a background thread by <see cref="SingleInstance"/>: "" = just show the window.</summary>
    public void HandleExternalRequest(string path)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                if (!string.IsNullOrWhiteSpace(path)) QueueAdd(path);
            });
        }
        catch { }
    }

    private void QueueAdd(string path)
    {
        if (_shotDir != null) return;
        _pendingAdds.Enqueue(path);
        ProcessPendingAdds();
    }

    /// <summary>Open the Add dialog for queued packages, one at a time, once no other dialog is open.</summary>
    private void ProcessPendingAdds()
    {
        // A modal dialog (Edit, a message box, a file picker) disables this window natively, without
        // changing Control.Enabled; wait until it closes.
        if (_pendingAdds.Count == 0) { _pendingTimer.Stop(); return; }
        if (!IsHandleCreated || !IsWindowEnabled(Handle) || OwnedForms.Length > 0) { _pendingTimer.Start(); return; }
        _pendingTimer.Stop();
        var path = _pendingAdds.Dequeue();
        AddProfile(path);
        if (_pendingAdds.Count > 0) _pendingTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    // ----- drag & drop an APK to add it -----

    private static string? DroppedPackage(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(f => PackageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
    }

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = DroppedPackage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var file = DroppedPackage(e);
        // Post it, so Explorer isn't blocked while our dialog is open.
        if (file != null) BeginInvoke(() => AddProfile(file));
    }

    // ----- helpers -----

    private static void OpenInNotepad(string path)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
    }

    private static Profile Clone(Profile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        ApkPath = p.ApkPath,
        DataRoot = p.DataRoot,
        PhoneWidthDp = p.PhoneWidthDp,
        PhoneHeightDp = p.PhoneHeightDp,
        SdkInt = p.SdkInt,
        DensityDpi = p.DensityDpi,
        JvmOpts = p.JvmOpts,
        LogLevel = p.LogLevel,
        AllowMultipleInstances = p.AllowMultipleInstances,
        ProxyType = p.ProxyType,
        ProxyHost = p.ProxyHost,
        ProxyPort = p.ProxyPort,
        ProxyUser = p.ProxyUser,
        ProxyPassword = p.ProxyPassword,
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppIcons.IconReady -= OnIconReady;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _itemMenu.Dispose();
            _spaceMenu.Dispose();
            _listIcons.Dispose();
            _windowIcon?.Dispose();
            _pendingTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // =====================================================================
    // --screenshot: capture the window's own bounds only (PrintWindow), then exit
    // =====================================================================

    /// <summary>
    /// Saves tiles, keyboard focus, narrow window, list and Edit-dialog captures, then closes.
    /// It never opens a context menu, never presses Enter and never saves: a context menu opened
    /// programmatically once led to a real launch. <see cref="RuntimeLauncher.LaunchBlocked"/> is
    /// set for this process as a second guard.
    /// </summary>
    private async void RunScreenshots()
    {
        var dir = _shotDir!;
        try
        {
            Directory.CreateDirectory(dir);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (AppIcons.PendingCount > 0 && DateTime.UtcNow < until) await Task.Delay(100);
            await Task.Delay(300);
            RefreshAll();

            SetViewMode(tiles: true);
            _tiles.SelectById(_store.Profiles.ElementAtOrDefault(1)?.Id ?? _store.Profiles.FirstOrDefault()?.Id);
            _tiles.Focus();
            if (_store.Profiles.Count > 2) _tiles.SimulateHover(2);
            await Task.Delay(500);
            WindowShot.Capture(this, Path.Combine(dir, $"tiles{_shotSuffix}.png"));

            _tiles.SimulateHover(-1);
            _tiles.SimulateKey(Keys.Right); // keyboard focus ring on the next tile (arrow keys only)
            await Task.Delay(300);
            WindowShot.Capture(this, Path.Combine(dir, $"tiles-keyboard{_shotSuffix}.png"));
            _tiles.SimulateKey(Keys.Left);

            var size = Size;
            Size = MinimumSize;
            await Task.Delay(400);
            WindowShot.Capture(this, Path.Combine(dir, $"narrow{_shotSuffix}.png"));
            Size = size;
            await Task.Delay(300);

            SetViewMode(tiles: false);
            await Task.Delay(500);
            WindowShot.Capture(this, Path.Combine(dir, $"list{_shotSuffix}.png"));

            if (Selected != null)
            {
                _dialogShownHook = async dlg =>
                {
                    await Task.Delay(700);
                    try { WindowShot.Capture(dlg, Path.Combine(dir, $"edit{_shotSuffix}.png")); } catch { }
                    dlg.DialogResult = DialogResult.Cancel;
                };
                EditSelected();
                _dialogShownHook = null;
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "screenshot-error.txt"), ex.ToString()); } catch { }
        }
        Close();
    }
}

/// <summary>Captures one window (its own pixels only, via PrintWindow) to a PNG.</summary>
internal static class WindowShot
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static void Capture(Control window, string path)
    {
        var h = window.Handle;
        if (!GetWindowRect(h, out var wr)) return;
        int w = wr.Right - wr.Left, ht = wr.Bottom - wr.Top;
        if (w <= 0 || ht <= 0) return;
        using var full = new Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
        }
        // Drop the invisible resize borders: crop to the visible frame.
        var crop = new Rectangle(0, 0, w, ht);
        if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var fr, 16) == 0)
            crop = Rectangle.Intersect(crop, new Rectangle(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top));
        using var outBmp = full.Clone(crop, full.PixelFormat);
        outBmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
