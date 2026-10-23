using System.Diagnostics;

namespace WinApkLauncher;

/// <summary>
/// The launcher window: app tiles (default) or a detail list of profiles, a toolbar
/// with every action, context menus, and a status bar. Its strings come from <see cref="L"/>;
/// choosing another language in the "…" menu rebuilds the window in place.
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

    // Launches that have no window yet: the tile shows "Starting…" and the status bar the step.
    private readonly List<StartupWatch> _starting = new();
    private readonly System.Windows.Forms.Timer _startTimer = new() { Interval = 50 };
    private int _startTicks;
    private LauncherDialog? _notice; // the non-modal "still starting" notice, while it is open
    private readonly HashSet<int> _stoppedPids = new(); // ended with Stop it: their exit is not a crash
    private readonly Dictionary<StartupWatch, SplashForm> _splashes = new(); // startup windows of _starting
    private readonly List<(ToolStripItem Item, char Glyph, bool OnAccent)> _glyphItems = new();

    private ToolStripButton _launchBtn = null!, _addBtn = null!, _editBtn = null!, _dupBtn = null!, _delBtn = null!;
    private ToolStripButton _logBtn = null!, _folderBtn = null!, _runtimeBtn = null!, _tilesBtn = null!, _listBtn = null!;
    private ToolStripButton _proxyLogBtn = null!;
    private ToolStripMenuItem _langMenu = null!;
    private ToolStripMenuItem _miTiles = null!, _miList = null!, _miProxyLogItem = null!;
    private readonly List<ToolStripItem> _needsSelection = new();

    private string? _selectedId;
    private bool _tilesMode;
    private bool _syncing;
    private Icon? _windowIcon;

    // --screenshot <dir>: capture the views, the Edit dialog, two messages and the startup window,
    // then exit. Nothing is saved and nothing is launched.
    private readonly string? _shotDir;
    private readonly string _shotSuffix;
    private Action<Form>? _dialogShownHook;

    private static readonly string[] PackageExtensions = { ".apk", ".xapk", ".apkm", ".apks" };

    // Packages to add that arrived while a dialog was open (another start, Explorer "Open with").
    private readonly Queue<string> _pendingAdds = new();
    private readonly System.Windows.Forms.Timer _pendingTimer = new() { Interval = 400 };
    private readonly Label _listEmpty = new();

    /// <param name="addPath">A package to open the Add dialog with once the window is shown.</param>
    /// <param name="language">--lang: a language for this run instead of the saved setting (not saved).</param>
    public MainForm(string? screenshotDir = null, string shotSuffix = "", string? addPath = null, string? language = null)
    {
        _shotDir = screenshotDir;
        _shotSuffix = shotSuffix;
        _store = LauncherStore.Load();
        L.Apply(language ?? _store.Language);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.MainTitle;
        ClientSize = new Size(980, 600);
        MinimumSize = new Size(700, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        AllowDrop = true;
        L.ApplyDirection(this);

        SetUpToolbar();
        SetUpMenus();
        BuildToolbarItems();
        BuildMenuItems();
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
        _startTimer.Tick += (_, _) => OnStartTick();

        RefreshAll();
        ApplyViewMode(focus: false);
    }

    // =====================================================================
    // UI construction
    // =====================================================================

    private void SetUpToolbar()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Renderer = new ThemedRenderer(_t);
        _toolbar.BackColor = _t.Back;
        _toolbar.ForeColor = _t.Text;
        _toolbar.Font = Font;
        _toolbar.ShowItemToolTips = true;
        _toolbar.CanOverflow = true;
        _toolbar.Stretch = true;
    }

    /// <summary>The toolbar's buttons, in the current language. Runs again after a language change.</summary>
    private void BuildToolbarItems()
    {
        foreach (var old in _toolbar.Items.Cast<ToolStripItem>().ToList())
        {
            old.Image?.Dispose();
            old.Dispose();
        }
        _toolbar.Items.Clear();
        _needsSelection.Clear();
        _toolbar.AccessibleName = L.ToolbarName;
        _toolbar.OverflowButton.AccessibleName = _toolbar.OverflowButton.ToolTipText = L.MoreOptions;

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

        // In a right-to-left language the ToolStrip mirrors itself: "Left" items start at the right.
        _launchBtn = Btn(L.Launch, '\uE768', L.LaunchTip, LaunchSelected, accent: true);
        Sep();
        _addBtn = Btn(L.AddApp, '\uE710', L.AddAppTip, () => AddProfile());
        _editBtn = Btn(L.Edit, '\uE70F', L.EditTip, EditSelected);
        _dupBtn = Btn(L.Duplicate, '\uE8C8', L.DuplicateTip, DuplicateSelected);
        _delBtn = Btn(L.Delete, '\uE74D', L.DeleteTip, DeleteSelected);
        Sep();
        _logBtn = Btn(L.RunLog, '\uE9F9', L.RunLogTip, OpenLastLog);
        _folderBtn = Btn(L.DataFolder, '\uE838', L.DataFolderTip, OpenDataFolder);
        _proxyLogBtn = Btn(L.ProxyLog, '\uE774', L.ProxyLogTip, OpenProxyLog);

        // Right-aligned items are laid out from the far edge in collection order.
        _listBtn = Btn(L.ViewList, '\uE8FD', L.ViewListTip, () => SetViewMode(tiles: false), align: ToolStripItemAlignment.Right);
        _tilesBtn = Btn(L.ViewTiles, '\uECA5', L.ViewTilesTip, () => SetViewMode(tiles: true), align: ToolStripItemAlignment.Right);
        Sep(ToolStripItemAlignment.Right);
        _runtimeBtn = Btn(L.RuntimePath, '\uE713', RuntimeTooltip(), () => EditRuntimePath(),
            align: ToolStripItemAlignment.Right);
        // Always in the "…" overflow menu.
        var logs = Btn(L.OpenLogsFolder, '\uE8B7', L.OpenLogsFolderTip(RuntimeLauncher.LogsDir), () => OpenLogsFolder());
        logs.Overflow = ToolStripItemOverflow.Always;
        BuildLanguageMenu();
        var about = Btn(L.About, '\uE946', L.MainTitle, ShowAbout);
        about.Overflow = ToolStripItemOverflow.Always;

        // When the window is narrow, the secondary buttons go to the "…" overflow first.
        _launchBtn.Overflow = _tilesBtn.Overflow = _listBtn.Overflow = ToolStripItemOverflow.Never;

        _needsSelection.AddRange(new ToolStripItem[] { _launchBtn, _editBtn, _dupBtn, _delBtn, _folderBtn, _proxyLogBtn });
    }

    /// <summary>"Language / שפה" in the "…" menu: Automatic, then each language by its own name.</summary>
    private void BuildLanguageMenu()
    {
        _langMenu = new ToolStripMenuItem(L.LanguageMenu) { Overflow = ToolStripItemOverflow.Always };
        var current = L.NormalizeSetting(_store.Language);
        var choices = new List<(string Setting, string Text)> { (L.Auto, L.LanguageAuto) };
        choices.AddRange(L.Languages.Select(l => (l.Code, l.NativeName)));
        foreach (var (setting, text) in choices)
        {
            var mi = new ToolStripMenuItem(text) { Checked = setting == current };
            // After the menu has closed: the rebuild disposes this item.
            mi.Click += (_, _) => BeginInvoke(() => SetLanguage(setting));
            _langMenu.DropDownItems.Add(mi);
        }
        var dd = _langMenu.DropDown;
        dd.Renderer = new ThemedRenderer(_t);
        dd.BackColor = _t.MenuBack;
        dd.ForeColor = _t.Text;
        dd.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        dd.HandleCreated += (s, _) => Theme.RoundCorners(((Control)s!).Handle);
        _toolbar.Items.Add(_langMenu);
        _glyphItems.Add((_langMenu, '\uF2B7', false)); // LocaleLanguage
    }

    private void SetUpMenus()
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
    }

    /// <summary>The context menus' items, in the current language. Runs again after a language change.</summary>
    private void BuildMenuItems()
    {
        foreach (var m in new[] { _itemMenu, _spaceMenu })
        {
            foreach (var old in m.Items.Cast<ToolStripItem>().ToList())
            {
                old.Image?.Dispose();
                old.Dispose();
            }
            m.Items.Clear();
            m.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        }

        ToolStripMenuItem Item(ContextMenuStrip menu, string text, char glyph, string? keys, Action onClick, bool bold = false)
        {
            var mi = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Tag = bold ? "bold" : null };
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
            _glyphItems.Add((mi, glyph, false));
            return mi;
        }

        Item(_itemMenu, L.MenuLaunch, '\uE768', "Enter", LaunchSelected, bold: true);
        _itemMenu.Items.Add(new ToolStripSeparator());
        Item(_itemMenu, L.MenuEdit, '\uE70F', "F2", EditSelected);
        Item(_itemMenu, L.MenuDuplicate, '\uE8C8', "Ctrl+D", DuplicateSelected);
        Item(_itemMenu, L.MenuDelete, '\uE74D', "Del", DeleteSelected);
        _itemMenu.Items.Add(new ToolStripSeparator());
        Item(_itemMenu, L.MenuOpenRunLog, '\uE9F9', "Ctrl+L", OpenLastLog);
        _miProxyLogItem = Item(_itemMenu, L.MenuOpenProxyLog, '\uE774', null, OpenProxyLog);
        Item(_itemMenu, L.MenuOpenDataFolder, '\uE838', null, OpenDataFolder);
        _itemMenu.Items.Add(new ToolStripSeparator());
        // The shortcut glyph points out of the window, so it turns with the reading direction.
        Item(_itemMenu, L.MenuCreateShortcut, '\uE8A7', null, CreateDesktopShortcut);

        Item(_spaceMenu, L.MenuAddApp, '\uE710', "Ctrl+N", () => AddProfile());
        _spaceMenu.Items.Add(new ToolStripSeparator());
        _miTiles = Item(_spaceMenu, L.MenuTilesView, '\uECA5', "Ctrl+1", () => SetViewMode(tiles: true));
        _miList = Item(_spaceMenu, L.MenuListView, '\uE8FD', "Ctrl+2", () => SetViewMode(tiles: false));
        _spaceMenu.Items.Add(new ToolStripSeparator());
        Item(_spaceMenu, L.RuntimePath, '\uE713', null, () => EditRuntimePath());
    }

    /// <summary>Glyphs that show a direction (a list's bullets, an arrow out of the window): mirrored in RTL.</summary>
    private static bool IsDirectional(char glyph) => glyph is '\uE8FD' or '\uE8A7';

    private void BuildTiles()
    {
        _tiles.Dock = DockStyle.Fill;
        _tiles.BackColor = _t.Content;
        _tiles.ForeColor = _t.Text;
        _tiles.AllowDrop = true;
        _tiles.IconProvider = p => AppIcons.Get(p.ApkPath);
        _tiles.IsRunning = IsRunning;
        _tiles.IsStarting = IsStarting;
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
        foreach (var w in new[] { 220, 170, 260, 80, 50, 55, 150, 80 }) _list.Columns.Add("", w);
        ApplyListTexts();
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

    /// <summary>The list's column headers and empty text in the current language; its columns run from the right in RTL.</summary>
    private void ApplyListTexts()
    {
        var names = new[] { L.ColProfile, L.ColApk, L.ColDataRoot, L.ColSize, L.ColApi, L.ColMulti, L.ColProxy, L.ColStatus };
        for (int i = 0; i < names.Length && i < _list.Columns.Count; i++) _list.Columns[i].Text = names[i];
        _list.RightToLeftLayout = L.Rtl;
        _listEmpty.Text = L.EmptyTitle + "\n\n" + L.EmptyHint;
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
        _langMenu.DropDown.Font = Font;
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

        foreach (var m in new ToolStripDropDown[] { _itemMenu, _spaceMenu, _langMenu.DropDown })
        {
            m.ImageScalingSize = new Size(S(16), S(16));
            m.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in m.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(S(2), S(4), S(2), S(4));
        }

        foreach (var (item, glyph, onAccent) in _glyphItems)
        {
            var old = item.Image;
            var img = _t.Glyph(glyph, S(16), onAccent ? _t.AccentText : _t.Text);
            if (L.Rtl && IsDirectional(glyph)) img.RotateFlip(RotateFlipType.RotateNoneFlipX);
            item.Image = img;
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
            if (_windowIcon == null)
            {
                using var s = typeof(MainForm).Assembly.GetManifestResourceStream("WinApkLauncher.ico");
                if (s != null) _windowIcon = new Icon(s);
                else
                {
                    using var bmp = WindowIconBitmap(LogicalToDeviceUnits(32));
                    _windowIcon = Icon.FromHandle(bmp.GetHicon());
                }
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
    // Language
    // =====================================================================

    /// <summary>The Language menu: save the setting, then rebuild the window in that language.</summary>
    private void SetLanguage(string setting)
    {
        _store.Language = L.NormalizeSetting(setting);
        if (_shotDir == null) SaveStore();
        L.Apply(_store.Language);
        RebuildUi();
    }

    /// <summary>
    /// Every string and the reading direction again, in place: the toolbar and the menus are built
    /// anew, the window is mirrored (or not) and the open startup windows are opened again. Running
    /// and starting apps are kept.
    /// </summary>
    private void RebuildUi()
    {
        if (_notice is { IsDisposed: false }) _notice.Close();
        SuspendLayout();
        Text = L.MainTitle;
        L.ApplyDirection(this);
        _glyphItems.Clear();
        BuildToolbarItems();
        BuildMenuItems();
        ApplyListTexts();
        _tiles.AccessibleName = L.AppsHeader;
        ResumeLayout(true);
        ApplyMetrics();
        RefreshAll();
        ApplyViewMode(focus: true);
        foreach (var w in _splashes.Keys.ToList())
        {
            CloseSplash(w);
            ShowSplash(w);
        }
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
                var it = new ListViewItem(L.Iso(p.Name)) { Tag = p, ImageKey = p.Id, UseItemStyleForSubItems = false };
                it.SubItems.Add(L.Ltr(Path.GetFileName(p.ApkPath)), _t.SubText, _t.Content, Font);
                it.SubItems.Add(L.Ltr(p.DataRoot), _t.SubText, _t.Content, Font);
                it.SubItems.Add(L.Ltr($"{p.PhoneWidthDp}x{p.PhoneHeightDp}"));
                it.SubItems.Add(L.Ltr(p.SdkInt > 0 ? p.SdkInt.ToString() : "29*"));
                it.SubItems.Add(p.AllowMultipleInstances ? L.ListYes : "");
                it.SubItems.Add(L.Ltr(p.ProxyLabel), p.HasProxy ? _t.Text : _t.SubText, _t.Content, Font);
                bool running = IsRunning(p), starting = IsStarting(p);
                it.SubItems.Add(starting ? L.ListStarting : running ? L.ListRunning : "", running ? _t.Accent : _t.Text, _t.Content, Font);
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

        int starting = _store.Profiles.Count(IsStarting);
        int running = _store.Profiles.Count(x => IsRunning(x) && !IsStarting(x));
        _summary.Text = L.AppCount(_store.Profiles.Count) +
                        (starting > 0 ? "  •  " + L.SummaryStarting(starting) : "") +
                        (running > 0 ? "  •  " + L.SummaryRunning(running) : "");
        _runtimeBtn.ToolTipText = RuntimeTooltip();
        if (_store.ResolveRuntime() == null)
        {
            _status.Text = L.StatusNoRuntime;
            return;
        }
        // While an app starts, the status bar shows its step: the selected app's, else the latest.
        var watch = (p != null ? _starting.LastOrDefault(w => w.Profile.Id == p.Id) : null) ?? _starting.LastOrDefault();
        if (watch != null) { _status.Text = watch.StatusLine; return; }
        if (p == null) { _status.Text = L.StatusProfiles(_store.Profiles.Count, LauncherStore.StorePath); return; }
        var lp = _running.LastOrDefault(r => r.Profile.Id == p.Id);
        var runtimeLogs = RuntimeLauncher.RuntimeLogDir(p);
        _status.Text = lp != null ? L.StatusLastLog(lp.LogPath)
                     : Directory.Exists(runtimeLogs) ? L.StatusRuntimeLogs(runtimeLogs)
                     : L.StatusRunLogs(RuntimeLauncher.LogsDir);
    }

    private bool IsRunning(Profile p) =>
        _running.Any(r => r.Profile.Id == p.Id && !SafeExited(r.Process));

    /// <summary>Launched by this window and still without its first window.</summary>
    private bool IsStarting(Profile p) => _starting.Any(w => w.Profile.Id == p.Id);

    private static bool SafeExited(Process pr) { try { return pr.HasExited; } catch { return true; } }

    private string TileTooltip(Profile p)
    {
        var apk = string.IsNullOrWhiteSpace(p.ApkPath) ? L.TipNoApk : L.Ltr(Path.GetFileName(p.ApkPath));
        var api = p.SdkInt > 0 ? p.SdkInt.ToString() : L.TipApiDefault;
        var lines = new List<string>
        {
            L.Iso(p.Name),
            $"{apk}  •  {L.Ltr($"{p.PhoneWidthDp}x{p.PhoneHeightDp} dp")}  •  API {api}",
            L.Ltr(p.DataRoot),
        };
        if (p.HasProxy) lines.Add(L.TipProxy(p.ProxyLabel));
        var watch = _starting.LastOrDefault(w => w.Profile.Id == p.Id);
        if (watch != null) lines.Add($"{watch.PhaseText}  ({watch.ElapsedText})");
        else if (IsRunning(p)) lines.Add(L.TipRunning);
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
            Msg.Show(this, L.CouldNotSave(LauncherStore.StorePath, ex.Message), L.SaveFailed, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    // Actions
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
            using var ask = new LauncherDialog(L.RuntimeNotFoundTitle, L.RuntimeNotFoundHeading,
                L.RuntimeNotFoundBody(AppPaths.BundledRuntimeExe),
                new[] { new DialogButton("locate", L.Locate, Accent: true), new DialogButton("cancel", L.Cancel) },
                LauncherDialog.Kind.Warning);
            if (ShowLauncherDialog(ask) != "locate" || !EditRuntimePath()) return;
            runtime = _store.ResolveRuntime();
        }
        var err = RuntimeLauncher.Validate(runtime, p);
        if (err != null) { Msg.Show(this, err, L.CannotLaunch, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        // Never a second copy: a second runtime on the same app folder only exits with code 3.
        if (HandleAlreadyRunning(p, runtime!)) return;

        LaunchedProcess lp;
        try { lp = RuntimeLauncher.Start(runtime, p); }
        catch (Exception ex)
        {
            Msg.Show(this, ex.Message, L.LaunchFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _running.Add(lp);
        var watch = new StartupWatch(lp);
        _starting.Add(watch);
        _startTimer.Start();

        lp.WhenExited(() => OnProcessExited(lp));
        RefreshAll();
        _status.Text = L.StatusLaunched(p.Name, lp.LogPath);
        ShowSplash(watch);
    }

    /// <summary>
    /// True when this profile already runs or is still starting, so Launch must not start it again.
    /// Checks this window's own launches first, then the runtime's lock and its processes (an app
    /// started by a desktop shortcut, a script or another launcher). A running app comes to the
    /// front; one without a window yet gets its startup window brought forward (or opened again
    /// after Hide). A profile that allows multiple instances may start another copy once the
    /// running one has its window.
    /// </summary>
    private bool HandleAlreadyRunning(Profile p, string runtime)
    {
        var watch = _starting.LastOrDefault(w => w.Profile.Id == p.Id);
        if (watch != null)
        {
            ShowSplash(watch);
            _status.Text = L.StatusStillStarting(p.Name);
            return true;
        }

        var mine = _running.Where(r => r.Profile.Id == p.Id && !SafeExited(r.Process)).ToList();
        var probe = RuntimeInstance.Probe(p, runtime);
        var window = probe.Window;
        foreach (var r in mine)
        {
            if (window != IntPtr.Zero) break;
            window = RuntimeInstance.FindAppWindow(r.Process.Id);
        }
        bool running = mine.Count > 0 || probe.State != InstanceState.NotRunning;
        if (!running) return false;
        if (window != IntPtr.Zero)
        {
            if (p.AllowMultipleInstances) return false;
            RuntimeInstance.BringToFront(window);
            _status.Text = L.StatusBroughtToFront(p.Name);
            return true;
        }
        // Started elsewhere and still starting: its startup window, or a new one that follows it.
        _status.Text = L.StatusStillStarting(p.Name);
        if (SplashForm.TryBringToFront(SplashForm.KeyFor(p, runtime), waitMs: 1500)) return true;
        var target = probe.Processes.FirstOrDefault();
        if (target != null && SplashProcess.SpawnAttached(p, target.Pid)) return true;
        ShowStillStarting(p);
        return true;
    }

    /// <summary>Opens the startup window of one of this window's launches, or brings it to the front.</summary>
    private void ShowSplash(StartupWatch watch)
    {
        if (_shotDir != null) return;
        if (_splashes.TryGetValue(watch, out var open) && !open.IsDisposed)
        {
            RuntimeInstance.BringToFront(open.Handle);
            return;
        }
        var splash = new SplashForm(watch.Profile, watch, SplashForm.KeyFor(watch.Profile, _store.ResolveRuntime()),
            anchor: WindowState == FormWindowState.Minimized ? null : Bounds, inTaskbar: false);
        // Its Stop link ends the runtime the way Stop it does; the exit then closes the window.
        splash.StopProcess = () =>
        {
            var lp = watch.Launch;
            if (lp == null || SafeExited(lp.Process)) return true;
            int pid;
            try { pid = lp.Process.Id; } catch { return true; }
            _stoppedPids.Add(pid);
            return RuntimeInstance.Stop(pid);
        };
        splash.FormClosed += (_, _) =>
        {
            if (_splashes.TryGetValue(watch, out var s) && s == splash) _splashes.Remove(watch);
        };
        _splashes[watch] = splash;
        splash.Show();
    }

    private void CloseSplash(StartupWatch watch)
    {
        if (_splashes.Remove(watch, out var s) && !s.IsDisposed) s.Close();
    }

    /// <summary>
    /// The non-modal notice for a Launch while an app started elsewhere has no window yet and its
    /// runtime process cannot be followed (only its lock is visible).
    /// </summary>
    private void ShowStillStarting(Profile p)
    {
        var heading = L.StillStartingHeading(p.Name);
        var body = L.StillStartingBody;
        if (_notice is { IsDisposed: false })
        {
            _notice.UpdateText(heading, body);
            _notice.Activate();
            return;
        }
        _notice = new LauncherDialog(L.StillStartingTitle, heading, body,
            new[] { new DialogButton("ok", L.Ok, Accent: true) });
        _notice.FormClosed += (_, _) => _notice = null;
        _notice.Show(this);
    }

    /// <summary>Animates the starting tiles; every 500 ms reads the logs and looks for the app windows.</summary>
    private void OnStartTick()
    {
        if (_starting.Count == 0) { _startTimer.Stop(); return; }
        if (_tilesMode) _tiles.AdvanceSpinner();
        if (++_startTicks % 10 != 0) return;

        StartupWatch? opened = null;
        bool changed = false;
        foreach (var w in _starting.ToList())
        {
            var proc = w.Launch!.Process;
            if (SafeExited(proc)) { _starting.Remove(w); changed = true; continue; } // OnProcessExited reports
            w.Poll();
            if (_splashes.TryGetValue(w, out var splash)) splash.RefreshPhase();
            if (RuntimeInstance.FindAppWindow(proc.Id) != IntPtr.Zero)
            {
                _starting.Remove(w);
                CloseSplash(w);
                opened = w;
                changed = true;
            }
        }
        if (changed) RefreshAll();
        else UpdateUiState();
        if (opened != null) _status.Text = L.StatusRunning(opened.Profile.Name, opened.Launch!.LogPath);
        if (_starting.Count == 0) _startTimer.Stop();
    }

    private void OnProcessExited(LaunchedProcess lp)
    {
        if (InvokeRequired) { try { BeginInvoke(() => OnProcessExited(lp)); } catch { } return; }
        var code = RuntimeLauncher.SafeExitCode(lp.Process);
        _starting.RemoveAll(w => w.Launch == lp);
        // The startup window, if it is still open: the runtime exited before its first window.
        var entry = _splashes.FirstOrDefault(kv => kv.Key.Launch == lp);
        var splash = entry.Value;
        if (splash != null) _splashes.Remove(entry.Key);
        RefreshAll();
        bool stopped = false;
        try { stopped = _stoppedPids.Remove(lp.Process.Id); } catch { }
        bool showError = splash is { IsDisposed: false } && !stopped && code != 0 && code != RuntimeInstance.ExitAlreadyRunning;
        if (showError) splash!.ShowExited(code, lp.LogPath);
        else if (splash is { IsDisposed: false }) splash.Close();

        if (stopped) _status.Text = L.StatusStopped(lp.Profile.Name);
        else if (code == RuntimeInstance.ExitAlreadyRunning) ShowAlreadyRunning(lp);
        // Any other non-zero exit is treated as a crashed launch; before its first window, the
        // startup window says so (it has the log and the logs folder), else the crash message.
        else if (code != 0 && !showError) ShowCrashed(lp, code);
    }

    /// <summary>Exit code 3: another runtime holds this app's folder. Offers Wait, Show it and Stop it.</summary>
    private void ShowAlreadyRunning(LaunchedProcess lp)
    {
        var p = lp.Profile;
        var others = RunningCopies(p, except: lp);
        var window = others.Select(x => x.Window).FirstOrDefault(h => h != IntPtr.Zero);
        using var dlg = AlreadyRunningDialog(p, lp.LogPath, hasWindow: window != IntPtr.Zero, hasProcess: others.Count > 0);
        switch (ShowLauncherDialog(dlg))
        {
            case "show":
                RuntimeInstance.BringToFront(window);
                break;
            case "stop":
                StopApp(p, except: lp);
                break;
        }
    }

    private static LauncherDialog AlreadyRunningDialog(Profile p, string logPath, bool hasWindow, bool hasProcess) =>
        new(L.AlreadyRunningTitle, L.AlreadyRunningHeading(p.Name), L.AlreadyRunningBody(logPath),
            new[]
            {
                new DialogButton("wait", L.Wait, Accent: true, Tip: L.WaitTip),
                new DialogButton("show", L.ShowIt, Enabled: hasWindow, Tip: hasWindow ? L.ShowItTip : L.ShowItNoWindow),
                new DialogButton("stop", L.StopIt, Enabled: hasProcess, Tip: hasProcess ? L.StopItTip : L.StopItNone),
            });

    /// <summary>The runtime processes serving this profile's app: this window's launches and any other.</summary>
    private List<RuntimeProcess> RunningCopies(Profile p, LaunchedProcess? except)
    {
        int skip = -1;
        try { if (except != null) skip = except.Process.Id; } catch { }
        var list = RuntimeInstance.FindProcesses(p, _store.ResolveRuntime()).Where(x => x.Pid != skip).ToList();
        foreach (var r in _running)
        {
            if (r == except || r.Profile.Id != p.Id || SafeExited(r.Process)) continue;
            int pid;
            try { pid = r.Process.Id; } catch { continue; }
            if (list.All(x => x.Pid != pid))
            {
                var w = RuntimeInstance.FindAppWindow(pid, includeDialogs: true);
                list.Add(new RuntimeProcess(pid, w, w != IntPtr.Zero ? RuntimeInstance.WindowText(w) : ""));
            }
        }
        return list;
    }

    /// <summary>Asks, then ends the runtime processes that serve this profile's app.</summary>
    private void StopApp(Profile p, LaunchedProcess? except)
    {
        var copies = RunningCopies(p, except);
        if (copies.Count == 0)
        {
            Msg.Show(this, L.NoRunningCopy(p.Name), L.StopTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (var confirm = LauncherDialog.StopConfirm(p.Name, copies.Count))
        {
            if (ShowLauncherDialog(confirm) != "stop") return;
        }
        int failed = 0;
        foreach (var c in copies)
        {
            _stoppedPids.Add(c.Pid);
            if (!RuntimeInstance.Stop(c.Pid)) failed++;
        }
        RefreshAll();
        _status.Text = failed == 0 ? L.StatusStopped(p.Name) : L.StatusStopFailed(failed, p.Name);
        if (failed > 0)
            Msg.Show(this, L.CouldNotStop(failed, p.Name), L.StopTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>A non-zero exit other than 3: the log's last lines, the log and its folder.</summary>
    private void ShowCrashed(LaunchedProcess lp, int code)
    {
        using var dlg = CrashDialog(lp.Profile, code, lp.LogPath, RuntimeLauncher.TailLog(lp.LogPath));
        switch (ShowLauncherDialog(dlg))
        {
            case "log": OpenInNotepad(lp.LogPath); break;
            case "folder": OpenLogsFolder(lp.LogPath); break;
        }
    }

    private static LauncherDialog CrashDialog(Profile p, int code, string logPath, string details)
    {
        var runtimeLogs = RuntimeLauncher.RuntimeLogDir(p);
        var body = L.LogLine(logPath) + (Directory.Exists(runtimeLogs) ? "\n" + L.RuntimeLogsLine(runtimeLogs) : "");
        return new LauncherDialog(L.CrashTitle, L.CrashHeading(p.Name, code), body,
            new[]
            {
                new DialogButton("log", L.OpenLog, Accent: true),
                new DialogButton("folder", L.OpenLogsFolder, Tip: L.OpenLogsFolderTipSelected),
                new DialogButton("close", L.Close),
            }, LauncherDialog.Kind.Error, details: details);
    }

    private string? ShowLauncherDialog(LauncherDialog dlg)
    {
        if (_dialogShownHook != null) { var hook = _dialogShownHook; dlg.Shown += (_, _) => hook(dlg); }
        dlg.ShowDialog(this);
        return dlg.Result;
    }

    private void AddProfile(string? apkPath = null)
    {
        var name = L.NewProfileName;
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
        copy.Name = p.Name + L.CopySuffix;
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
        using (var confirm = new LauncherDialog(L.DeleteTitle, L.DeleteHeading(p.Name), L.DeleteBody,
                   new[] { new DialogButton("delete", L.Delete, Accent: true), new DialogButton("cancel", L.Cancel) },
                   LauncherDialog.Kind.Warning))
        {
            if (ShowLauncherDialog(confirm) != "delete") return;
        }
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
        // After a restart of the launcher: the newest log of this profile on disk.
        var path = lp?.LogPath ?? (p != null ? RuntimeLauncher.LatestLog(p) : null);
        if (path == null)
        {
            Msg.Show(this, L.NoRunLog(RuntimeLauncher.LogsDir), L.LogCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenInNotepad(path);
    }

    /// <summary>Opens the run logs folder in Explorer, with <paramref name="select"/> selected if it exists.</summary>
    private void OpenLogsFolder(string? select = null)
    {
        try { ShellOpen.LogsFolder(select); }
        catch (Exception ex) { Msg.Show(this, ex.Message, L.OpenLogsFolder, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
        catch (Exception ex) { Msg.Show(this, ex.Message, L.OpenFolderCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenProxyLog()
    {
        var p = Selected;
        if (p == null) return;
        if (!p.HasProxy)
        {
            Msg.Show(this, L.NoProxyConfigured, L.ProxyLogCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var path = Path.Combine(RuntimeLauncher.RuntimeLogDir(p), "proxy.log");
        if (!File.Exists(path))
        {
            Msg.Show(this, L.NoProxyLog(p.Name, path), L.ProxyLogCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                ?? throw new InvalidOperationException(L.ShortcutNoShell);
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = exe;
            link.Arguments = $"--launch {p.Id}";
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = L.ShortcutDescription(p.Name);
            if (iconPath != null) link.IconLocation = iconPath + ",0";
            link.Save();

            _status.Text = L.StatusShortcut(linkPath);
            Msg.Show(this, L.ShortcutCreated(linkPath), L.ShortcutCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Msg.Show(this, L.ShortcutFailed(ex.Message), L.ShortcutCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Title = L.LocateRuntimeTitle,
            Filter = L.RuntimeFilter,
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
        return L.RuntimeTip(exe, src);
    }

    private void ShowAbout()
    {
        var source = new List<string>();
        try
        {
            if (File.Exists(AppPaths.BundledRuntimeSourceFile))
                source.AddRange(File.ReadLines(AppPaths.BundledRuntimeSourceFile)
                    .Where(l => l.StartsWith("Source", StringComparison.OrdinalIgnoreCase) ||
                                l.StartsWith("Commit", StringComparison.OrdinalIgnoreCase))
                    .Take(3).Select(l => l.Trim()));
        }
        catch { }
        var text = L.AboutText(_store.DescribeRuntime(), source, LauncherStore.StorePath, AppPaths.LocalDir, AppPaths.ModeLabel);
        Msg.Show(this, text, L.About, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (!IsHandleCreated || !IsWindowEnabled(Handle) || OwnedForms.Any(f => f != _notice && f is not SplashForm)) { _pendingTimer.Start(); return; }
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

    private static void OpenInNotepad(string path) => ShellOpen.InNotepad(path);

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
            _startTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // =====================================================================
    // --screenshot: capture the window's own bounds only (PrintWindow), then exit
    // =====================================================================

    /// <summary>
    /// Saves tiles, keyboard focus, narrow window, list, Edit dialog, the exit-code-3 and crash
    /// messages and the startup window (starting, stalled, busy, failed), then the tiles and the list
    /// again after a live switch to the other language; then closes. It never opens a context menu,
    /// never presses Enter, never launches and never saves: a context menu opened programmatically
    /// once led to a real launch. <see cref="RuntimeLauncher.LaunchBlocked"/> is set for this process
    /// as a second guard. The dialogs are built here with sample values and closed without a choice.
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
            _tiles.SimulateKey(_tiles.NextKey); // keyboard focus ring on the next tile (arrow keys only)
            await Task.Delay(300);
            WindowShot.Capture(this, Path.Combine(dir, $"tiles-keyboard{_shotSuffix}.png"));
            _tiles.SimulateKey(_tiles.PreviousKey);

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
                CaptureNextDialog(Path.Combine(dir, $"edit{_shotSuffix}.png"));
                EditSelected();
                _dialogShownHook = null;
            }

            var p = Selected ?? _store.Profiles.FirstOrDefault();
            if (p != null)
            {
                var log = Path.Combine(RuntimeLauncher.LogsDir, RuntimeLauncher.MakeFileSafe(p.Name) + "-20261023-101500.log");
                CaptureNextDialog(Path.Combine(dir, $"already-running{_shotSuffix}.png"));
                using (var dlg = AlreadyRunningDialog(p, log, hasWindow: false, hasProcess: true)) ShowLauncherDialog(dlg);
                CaptureNextDialog(Path.Combine(dir, $"crash{_shotSuffix}.png"));
                using (var dlg = CrashDialog(p, 1, log, SampleLogTail)) ShowLauncherDialog(dlg);
                _dialogShownHook = null;

                await CaptureSplash(p, log, "splash", SplashShot.Converting);
                await CaptureSplash(p, log, "splash-stalled", SplashShot.Stalled);
                await CaptureSplash(p, log, "splash-busy", SplashShot.Busy);
                await CaptureSplash(p, log, "splash-error", SplashShot.Exited);
            }

            // The Language menu's own path, without the menu: switch, then capture the rebuilt window.
            SetLanguage(L.Current.Code == "he" ? "en" : "he");
            SetViewMode(tiles: true);
            await Task.Delay(600);
            WindowShot.Capture(this, Path.Combine(dir, $"switched-tiles{_shotSuffix}.png"));
            SetViewMode(tiles: false);
            await Task.Delay(500);
            WindowShot.Capture(this, Path.Combine(dir, $"switched-list{_shotSuffix}.png"));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "screenshot-error.txt"), ex.ToString()); } catch { }
        }
        Close();
    }

    /// <summary>The next modal dialog is captured once it has painted, then closed without a choice.</summary>
    private void CaptureNextDialog(string path)
    {
        _dialogShownHook = async dlg =>
        {
            await Task.Delay(700);
            try { WindowShot.Capture(dlg, path); } catch { }
            dlg.DialogResult = DialogResult.Cancel;
        };
    }

    private enum SplashShot { Converting, Stalled, Busy, Exited }

    /// <summary>A startup window with a sample state: no runtime behind it, key 0, closed after the capture.</summary>
    private async Task CaptureSplash(Profile p, string log, string name, SplashShot state)
    {
        var watch = new StartupWatch(p, log);
        watch.Feed("[win.apk] installing the app (once per install)...");
        watch.Feed("[install] extracting lib/arm64-v8a (12 files)");
        if (state != SplashShot.Exited) watch.Feed("[win.apk] converting the app's code (once per install)...");
        watch.SetShotState(elapsed: TimeSpan.FromSeconds(state == SplashShot.Converting ? 43 : 251),
            logIdle: state is SplashShot.Stalled or SplashShot.Busy ? TimeSpan.FromMinutes(4) : TimeSpan.Zero,
            cpuBusy: state != SplashShot.Stalled);
        using var splash = new SplashForm(p, watch, key: 0, anchor: Bounds, inTaskbar: false);
        splash.Show();
        if (state == SplashShot.Exited) splash.ShowExited(1, log);
        else splash.RefreshPhase();
        await Task.Delay(700);
        WindowShot.Capture(splash, Path.Combine(_shotDir!, $"{name}{_shotSuffix}.png"));
        splash.Close();
    }

    private const string SampleLogTail =
        "# WinApk Launcher 0.1.1\n# command: \"W:\\runtime\\bin\\winapk-runtime.exe\" --apk \"W:\\pkgs\\app.apk\" --data \"W:\\apps\\app\"\n" +
        "----------------------------------------------------------------\n" +
        "[win.apk] installing the app (once per install)...\n" +
        "[install] extracting lib/arm64-v8a (12 files)\n" +
        "[win.apk] converting the app's code (once per install)...\n" +
        "Exception in thread \"main\" java.lang.OutOfMemoryError: Java heap space\n" +
        "\tat com.android.dx.Converter.run(Converter.java:118)\n" +
        "# ---- exited, code 1 ----";
}

/// <summary>
/// Captures one window (its own pixels only, via PrintWindow) to a PNG. PrintWindow returns a
/// mirrored (right-to-left layout) window that has a title bar flipped, so that image is flipped
/// back to what the screen shows; a borderless one (the startup window) comes back as it is.
/// </summary>
internal static class WindowShot
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

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
        bool mirrored = (GetWindowLongPtrW(h, -20 /* GWL_EXSTYLE */).ToInt64() & 0x00400000 /* WS_EX_LAYOUTRTL */) != 0;
        bool caption = (GetWindowLongPtrW(h, -16 /* GWL_STYLE */).ToInt64() & 0x00C00000 /* WS_CAPTION */) == 0x00C00000;
        if (mirrored && caption) full.RotateFlip(RotateFlipType.RotateNoneFlipX);
        // Drop the invisible resize borders: crop to the visible frame.
        var crop = new Rectangle(0, 0, w, ht);
        if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var fr, 16) == 0)
            crop = Rectangle.Intersect(crop, new Rectangle(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top));
        using var outBmp = full.Clone(crop, full.PixelFormat);
        outBmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
