namespace WinApkLauncher;

/// <summary>
/// Edits a single <see cref="Profile"/>. Returns the edited copy in <see cref="Result"/>. Mirrored in
/// a right-to-left language (labels on the right, fields to their left); the fields for paths,
/// hosts, ports, credentials and JVM options stay left to right.
/// </summary>
public sealed class ProfileForm : Form
{
    public Profile Result { get; }

    private readonly Theme _t = Theme.Current;
    private readonly TextBox _name = new();
    private readonly TextBox _apk = new();
    private readonly TextBox _data = new();
    private readonly ComboBox _sizePreset = new();
    private readonly NumericUpDown _w = new();
    private readonly NumericUpDown _h = new();
    private readonly ComboBox _sdk = new();
    private readonly NumericUpDown _density = new();
    private readonly TextBox _jvm = new();
    private readonly ComboBox _log = new();
    private readonly CheckBox _multi = new();

    // Network proxy section.
    private readonly ComboBox _proxyType = new();
    private readonly TextBox _proxyHost = new();
    private readonly NumericUpDown _proxyPort = new();
    private readonly TextBox _proxyUser = new();
    private readonly TextBox _proxyPass = new();
    private readonly ThemedButton _proxyTest = new();
    private readonly Label _proxyResult = new();
    private readonly List<Control> _proxyFields = new();
    private CancellationTokenSource? _testCts;

    private static (string Label, ProxyKind Kind)[] ProxyTypes => new[]
    {
        (L.ProxyNone, ProxyKind.None),
        ("SOCKS5", ProxyKind.Socks5),
        ("SOCKS4 / 4a", ProxyKind.Socks4),
        ("HTTP CONNECT", ProxyKind.Http),
    };

    // Header: the app's icon, the dialog title and the APK file name; refreshed as you type.
    private readonly IconBox _icon = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly System.Windows.Forms.Timer _headerTimer = new() { Interval = 350 };

    // (label, width, height) presets the task asked for.
    private static (string Label, int W, int H)[] Presets => new[]
    {
        (L.PresetDefault, 360, 800),
        (L.Ltr("412 x 915"), 412, 915),
        (L.Ltr("393 x 873"), 393, 873),
        (L.PresetCustom, 0, 0),
    };

    private static (string Label, int Level)[] LogLevels => new[]
    {
        (L.LogDefault, 0), (L.LogVerbose, 2), (L.LogDebug, 3),
        (L.LogInfo, 4), (L.LogWarn, 5), (L.LogError, 6),
    };

    // New profiles: while the data root is still the suggested one, it follows the Name field.
    private readonly Func<string, string>? _suggestDataRoot;
    private string? _suggestedDataRoot;

    /// <param name="suggestDataRoot">For a new profile: the default data root for a name. The Data root
    /// field follows the Name field until the user edits it.</param>
    public ProfileForm(Profile p, bool isNew, Func<string, string>? suggestDataRoot = null)
    {
        Result = p;
        _suggestDataRoot = isNew ? suggestDataRoot : null;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = isNew ? L.AddProfileTitle : L.EditProfileTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        L.ApplyDirection(this); // RTL: the form mirrors the positions below

        const int labelX = 20, fieldX = 156, fieldW = 330, browseW = 84, wideW = fieldW + 8 + browseW;
        const int row = 36;

        // ----- header -----
        _icon.SetBounds(labelX, 16, 44, 44);
        Controls.Add(_icon);
        _title.Text = Text;
        _title.Font = _t.UiFont(12.5f, semibold: true);
        _title.AutoSize = false;
        _title.SetBounds(76, 14, wideW, 26);
        Controls.Add(_title);
        _subtitle.AutoSize = false;
        _subtitle.AutoEllipsis = true;
        _subtitle.ForeColor = _t.SubText;
        _subtitle.SetBounds(77, 40, fieldX + wideW - 77, 20);
        Controls.Add(_subtitle);
        Controls.Add(new Label { BackColor = _t.Border, Left = 0, Top = 76, Width = fieldX + wideW + 20, Height = 1 });

        int y = 94;

        Label Lab(string t, int yy)
        {
            var l = new Label { Text = t, Left = labelX, Top = yy + 4, Width = fieldX - labelX - 6, AutoSize = false };
            Controls.Add(l);
            return l;
        }
        Label Hint(string t, int x, int yy, int w, int h = 20)
        {
            var l = new Label { Text = t, Left = x, Top = yy, Width = w, Height = h, ForeColor = _t.SubText };
            Controls.Add(l);
            return l;
        }
        ThemedButton Browse(int yy, Action onClick)
        {
            var b = new ThemedButton { Text = L.Browse, Left = fieldX + fieldW + 8, Top = yy - 2, Width = browseW, Height = 28 };
            b.Click += (_, _) => onClick();
            Controls.Add(b);
            return b;
        }

        // Name
        Lab(L.FieldName, y);
        _name.SetBounds(fieldX, y, wideW, 24);
        _name.Text = p.Name;
        // The name box reads in the name's own direction (English left to right, Hebrew right to left).
        if (!string.IsNullOrWhiteSpace(p.Name)) _name.RightToLeft = L.StartsRtl(p.Name) ? RightToLeft.Yes : RightToLeft.No;
        Controls.Add(_name);
        y += row;

        // APK
        Lab(L.FieldApk, y);
        _apk.SetBounds(fieldX, y, fieldW, 24);
        _apk.Text = p.ApkPath;
        Controls.Add(_apk);
        Browse(y, () => BrowseFile(_apk, L.ApkFilter));
        y += row;

        // Data root
        Lab(L.FieldDataRoot, y);
        _data.SetBounds(fieldX, y, fieldW, 24);
        _data.Text = p.DataRoot;
        Controls.Add(_data);
        if (_suggestDataRoot != null && p.DataRoot == _suggestDataRoot(p.Name)) _suggestedDataRoot = p.DataRoot;
        _name.TextChanged += (_, _) =>
        {
            if (_suggestDataRoot == null || _suggestedDataRoot == null || _data.Text != _suggestedDataRoot) return;
            _data.Text = _suggestedDataRoot = _suggestDataRoot(_name.Text);
        };
        Browse(y, () => BrowseFolder(_data));
        y += 30;
        Hint(L.HintDataRoot, fieldX, y, wideW, 34);
        y += 44;

        // Phone size
        Lab(L.FieldPhoneSize, y);
        _sizePreset.DropDownStyle = ComboBoxStyle.DropDownList;
        _sizePreset.SetBounds(fieldX, y, 180, 24);
        foreach (var pr in Presets) _sizePreset.Items.Add(pr.Label);
        Controls.Add(_sizePreset);
        _w.SetBounds(fieldX + 190, y, 64, 24);
        _w.Minimum = 200; _w.Maximum = 4000; _w.Value = Clamp(p.PhoneWidthDp, 200, 4000);
        Controls.Add(_w);
        Controls.Add(new Label { Text = "x", Left = fieldX + 259, Top = y + 4, Width = 12, ForeColor = _t.SubText });
        _h.SetBounds(fieldX + 274, y, 64, 24);
        _h.Minimum = 200; _h.Maximum = 4000; _h.Value = Clamp(p.PhoneHeightDp, 200, 4000);
        Controls.Add(_h);
        _sizePreset.SelectedIndexChanged += (_, _) => ApplyPreset();
        SelectPresetFor(p.PhoneWidthDp, p.PhoneHeightDp);
        y += row;

        // API level
        Lab(L.FieldApi, y);
        _sdk.DropDownStyle = ComboBoxStyle.DropDownList;
        _sdk.SetBounds(fieldX, y, 180, 24);
        _sdk.Items.Add(L.ApiDefault);
        for (int lvl = 21; lvl <= 37; lvl++) _sdk.Items.Add($"API {lvl}");
        _sdk.SelectedIndex = p.SdkInt is >= 21 and <= 37 ? p.SdkInt - 21 + 1 : 0;
        Controls.Add(_sdk);
        Hint("MACAPK_SDK_INT", fieldX + 190, y + 4, 200);
        y += row;

        // Density
        Lab(L.FieldDensity, y);
        _density.SetBounds(fieldX, y, 80, 24);
        _density.Minimum = 0; _density.Maximum = 640; _density.Value = Clamp(p.DensityDpi, 0, 640);
        Controls.Add(_density);
        Hint(L.HintDensity, fieldX + 90, y + 4, 320);
        y += row;

        // JVM opts
        Lab(L.FieldJvm, y);
        _jvm.SetBounds(fieldX, y, wideW, 24);
        _jvm.Text = p.JvmOpts;
        Controls.Add(_jvm);
        y += 30;
        Hint(L.HintJvm, fieldX, y, wideW);
        y += 30;

        // Log level
        Lab(L.FieldLogLevel, y);
        _log.DropDownStyle = ComboBoxStyle.DropDownList;
        _log.SetBounds(fieldX, y, 180, 24);
        foreach (var ll in LogLevels) _log.Items.Add(ll.Label);
        _log.SelectedIndex = Math.Max(0, Array.FindIndex(LogLevels, l => l.Level == p.LogLevel));
        Controls.Add(_log);
        y += row;

        // Multi instance
        _multi.Text = L.FieldMulti;
        _multi.SetBounds(fieldX, y, wideW, 24);
        _multi.Checked = p.AllowMultipleInstances;
        Controls.Add(_multi);
        y += 38;

        // ----- Network proxy -----
        Controls.Add(new Label { BackColor = _t.Border, Left = labelX, Top = y, Width = fieldX + wideW - labelX, Height = 1 });
        y += 12;
        Controls.Add(new Label
        {
            Text = L.SectionProxy, Left = labelX, Top = y, Width = 200, Height = 20,
            Font = _t.UiFont(9.5f, semibold: true),
        });
        y += 28;

        // Type
        Lab(L.FieldProxyType, y);
        _proxyType.DropDownStyle = ComboBoxStyle.DropDownList;
        _proxyType.SetBounds(fieldX, y, 180, 24);
        foreach (var pt in ProxyTypes) _proxyType.Items.Add(pt.Label);
        _proxyType.SelectedIndex = Math.Max(0, Array.FindIndex(ProxyTypes, t => t.Kind == p.ProxyKind));
        _proxyType.SelectedIndexChanged += (_, _) => UpdateProxyEnabled();
        Controls.Add(_proxyType);
        y += row;

        // Host + port
        Lab(L.FieldHost, y);
        _proxyHost.SetBounds(fieldX, y, fieldW - 108, 24);
        _proxyHost.Text = p.ProxyHost;
        Controls.Add(_proxyHost);
        Controls.Add(new Label { Text = L.FieldPort, Left = fieldX + fieldW - 96, Top = y + 4, Width = 32, ForeColor = _t.SubText });
        _proxyPort.SetBounds(fieldX + fieldW - 60, y, 70, 24);
        _proxyPort.Minimum = 1; _proxyPort.Maximum = 65535;
        _proxyPort.Value = Clamp(p.ProxyPort <= 0 ? 1080 : p.ProxyPort, 1, 65535);
        Controls.Add(_proxyPort);
        y += row;

        // User
        Lab(L.FieldUser, y);
        _proxyUser.SetBounds(fieldX, y, 220, 24);
        _proxyUser.Text = p.ProxyUser;
        Controls.Add(_proxyUser);
        Controls.Add(new Label { Text = L.FieldOptional, Left = fieldX + 230, Top = y + 4, Width = 90, ForeColor = _t.SubText });
        y += row;

        // Password + Test
        Lab(L.FieldPassword, y);
        _proxyPass.SetBounds(fieldX, y, 220, 24);
        _proxyPass.UseSystemPasswordChar = true;
        _proxyPass.Text = p.GetProxyPassword();
        Controls.Add(_proxyPass);
        _proxyTest.Text = L.ProxyTest;
        _proxyTest.SetBounds(fieldX + 230, y - 2, 92, 28);
        _proxyTest.Click += (_, _) => RunProxyTest();
        Controls.Add(_proxyTest);
        y += 30;

        // Result line + scope note
        _proxyResult.SetBounds(fieldX, y, wideW, 20);
        _proxyResult.ForeColor = _t.SubText;
        _proxyResult.AutoEllipsis = true;
        Controls.Add(_proxyResult);
        y += 24;
        Hint(L.HintProxy, labelX, y, fieldX + wideW - labelX, 34);
        y += 42;

        _proxyFields.AddRange(new Control[] { _proxyHost, _proxyPort, _proxyUser, _proxyPass, _proxyTest });
        UpdateProxyEnabled();

        // Paths, hosts, ports, credentials and JVM options read left to right in any language.
        foreach (var c in new Control[] { _apk, _data, _jvm, _proxyHost, _proxyPort, _proxyUser, _proxyPass }) L.KeepLtr(c);

        // ----- footer: OK / Cancel on the chrome color, like a Windows 11 dialog -----
        // The panel does not mirror its children, so in RTL the buttons are placed from the left.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = _t.Back };
        footer.Paint += (_, e) => { using var pen = new Pen(_t.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        int right = fieldX + wideW, width = right + 20;
        int X(int x, int w) => L.Rtl ? width - x - w : x;
        var cancel = new ThemedButton { Text = L.Cancel, DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        var ok = new ThemedButton { Text = L.Ok, Accent = true, DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        cancel.SetBounds(X(right - 100, 100), 16, 100, 32);
        ok.SetBounds(X(right - 208, 100), 16, 100, 32);
        ok.Click += (_, _) => { if (Collect()) DialogResult = DialogResult.OK; else DialogResult = DialogResult.None; };
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;

        // Dark mode: the 3D edit border renders bright white; a single-line border reads as Windows 11.
        if (_t.Dark)
        {
            foreach (var tb in new[] { _name, _apk, _data, _jvm, _proxyHost, _proxyUser, _proxyPass }) tb.BorderStyle = BorderStyle.FixedSingle;
            foreach (var nu in new[] { _w, _h, _density, _proxyPort }) nu.BorderStyle = BorderStyle.FixedSingle;
        }

        ClientSize = new Size(width, y + footer.Height);
        ResumeLayout(false);
        PerformLayout();

        // Header follows the Name / APK fields (debounced; icons are extracted in the background).
        _headerTimer.Tick += (_, _) => { _headerTimer.Stop(); UpdateHeader(); };
        _apk.TextChanged += (_, _) => { _headerTimer.Stop(); _headerTimer.Start(); };
        _name.TextChanged += (_, _) => { _headerTimer.Stop(); _headerTimer.Start(); };
        AppIcons.IconReady += OnIconReady;
        UpdateHeader();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Derived from the (DPI-scaled) form font, so the header matches on any monitor.
        _title.Font = new Font(_t.SemiboldFamily, Font.SizeInPoints * 12.5f / 9f);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppIcons.IconReady -= OnIconReady;
        _headerTimer.Stop();
        try { _testCts?.Cancel(); } catch { }
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _headerTimer.Dispose(); _testCts?.Dispose(); }
        base.Dispose(disposing);
    }

    private void OnIconReady()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) UpdateHeader(); }); } catch { }
    }

    private void UpdateHeader()
    {
        var apk = _apk.Text.Trim();
        var name = string.IsNullOrWhiteSpace(_name.Text) ? "?" : _name.Text.Trim();
        _title.Text = string.IsNullOrWhiteSpace(_name.Text) ? Text : L.Iso(name);
        if (string.IsNullOrWhiteSpace(apk)) _subtitle.Text = L.NoApkSelected + "  •  " + Text;
        else if (!File.Exists(apk)) _subtitle.Text = $"{L.Ltr(Path.GetFileName(apk))}  •  {L.FileNotFound}";
        else _subtitle.Text = $"{L.Ltr(Path.GetFileName(apk))}  •  {Text}";
        _icon.SetIcon(AppIcons.Get(apk), name);
    }

    private void ApplyPreset()
    {
        var idx = _sizePreset.SelectedIndex;
        if (idx < 0) return;
        var pr = Presets[idx];
        bool custom = pr.W == 0;
        _w.Enabled = _h.Enabled = custom;
        if (!custom) { _w.Value = pr.W; _h.Value = pr.H; }
    }

    private void SelectPresetFor(int w, int h)
    {
        for (int i = 0; i < Presets.Length; i++)
            if (Presets[i].W == w && Presets[i].H == h) { _sizePreset.SelectedIndex = i; return; }
        _sizePreset.SelectedIndex = Presets.Length - 1; // Custom
        _w.Enabled = _h.Enabled = true;
    }

    private bool Collect()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        { Msg.Show(this, L.GiveName, L.MissingName); return false; }
        if (string.IsNullOrWhiteSpace(_data.Text))
        { Msg.Show(this, L.GiveDataRoot, L.MissingDataRoot); return false; }

        var kind = ProxyTypes[Math.Max(0, _proxyType.SelectedIndex)].Kind;
        if (kind != ProxyKind.None && string.IsNullOrWhiteSpace(_proxyHost.Text))
        { Msg.Show(this, L.GiveProxyHost, L.MissingProxyHost); return false; }

        Result.Name = _name.Text.Trim();
        Result.ApkPath = _apk.Text.Trim();
        Result.DataRoot = _data.Text.Trim();
        Result.PhoneWidthDp = (int)_w.Value;
        Result.PhoneHeightDp = (int)_h.Value;
        Result.SdkInt = _sdk.SelectedIndex <= 0 ? 0 : 21 + (_sdk.SelectedIndex - 1);
        Result.DensityDpi = (int)_density.Value;
        Result.JvmOpts = _jvm.Text.Trim();
        Result.LogLevel = _log.SelectedIndex >= 0 ? LogLevels[_log.SelectedIndex].Level : 0;
        Result.AllowMultipleInstances = _multi.Checked;

        Result.ProxyType = kind.ToString();
        Result.ProxyHost = _proxyHost.Text.Trim();
        Result.ProxyPort = (int)_proxyPort.Value;
        Result.ProxyUser = _proxyUser.Text.Trim();
        Result.SetProxyPassword(_proxyPass.Text); // DPAPI-encrypted; never plaintext on disk
        return true;
    }

    private void UpdateProxyEnabled()
    {
        bool on = ProxyTypes[Math.Max(0, _proxyType.SelectedIndex)].Kind != ProxyKind.None;
        foreach (var c in _proxyFields) c.Enabled = on;
        if (!on) _proxyResult.Text = "";
    }

    /// <summary>The proxy config from the dialog's current (unsaved) values, or null if type is None.</summary>
    private ProxyConfig? CurrentProxyConfig()
    {
        var kind = ProxyTypes[Math.Max(0, _proxyType.SelectedIndex)].Kind;
        if (kind == ProxyKind.None || string.IsNullOrWhiteSpace(_proxyHost.Text)) return null;
        return new ProxyConfig(kind, _proxyHost.Text.Trim(), (int)_proxyPort.Value, _proxyUser.Text.Trim(), _proxyPass.Text);
    }

    private async void RunProxyTest()
    {
        var cfg = CurrentProxyConfig();
        if (cfg == null) { _proxyResult.ForeColor = _t.SubText; _proxyResult.Text = L.ProxyChooseFirst; return; }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _proxyTest.Enabled = false;
        _proxyResult.ForeColor = _t.SubText;
        _proxyResult.Text = L.ProxyTesting;
        try
        {
            var r = await ProxyUpstream.TestAsync(cfg, _testCts.Token);
            if (IsDisposed) return;
            _proxyResult.ForeColor = r.Ok ? (_t.Dark ? Color.FromArgb(0x6C, 0xCB, 0x5A) : Color.FromArgb(0x0E, 0x70, 0x0E))
                                          : (_t.Dark ? Color.FromArgb(0xFF, 0x80, 0x7A) : Color.FromArgb(0xC4, 0x2B, 0x1C));
            _proxyResult.Text = r.Message;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { _proxyResult.ForeColor = _t.SubText; _proxyResult.Text = ex.Message; }
        }
        finally
        {
            if (!IsDisposed) _proxyTest.Enabled = ProxyTypes[Math.Max(0, _proxyType.SelectedIndex)].Kind != ProxyKind.None;
        }
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (!string.IsNullOrWhiteSpace(target.Text) && File.Exists(target.Text))
        { dlg.InitialDirectory = Path.GetDirectoryName(target.Text); dlg.FileName = Path.GetFileName(target.Text); }
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
    }

    private void BrowseFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { ShowNewFolderButton = true };
        if (!string.IsNullOrWhiteSpace(target.Text)) dlg.SelectedPath = target.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Draws an app icon (or the letter-tile fallback) crisply at the control's pixel size.</summary>
    private sealed class IconBox : Control
    {
        private Image? _src;
        private string _name = "?";
        private Bitmap? _cache;

        public IconBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void SetIcon(Image? src, string name)
        {
            if (ReferenceEquals(src, _src) && name == _name && _cache != null) return;
            _src = src;
            _name = name;
            _cache?.Dispose();
            _cache = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int px = Math.Min(Width, Height);
            if (px <= 0) return;
            if (_cache == null || _cache.Width != px)
            {
                _cache?.Dispose();
                _cache = _src != null ? AppIcons.Scaled(_src, px) : AppIcons.LetterTile(_name, px, Theme.Current.SemiboldFamily);
            }
            e.Graphics.DrawImageUnscaled(_cache, (Width - px) / 2, (Height - px) / 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cache?.Dispose();
            base.Dispose(disposing);
        }
    }
}
