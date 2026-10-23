namespace WinApkLauncher;

/// <summary>One button of a <see cref="LauncherDialog"/>.</summary>
internal sealed record DialogButton(string Id, string Text, bool Accent = false, bool Enabled = true, string? Tip = null);

/// <summary>
/// A message window in the launcher's theme: a glyph, a heading, body text, an optional block of
/// log lines and a row of buttons. <see cref="ShowDialog(IWin32Window)"/> for a question, or
/// <see cref="Form.Show(IWin32Window)"/> for a notice that does not block the launcher.
/// <see cref="Result"/> is the id of the button that closed it (null: closed another way).
/// </summary>
internal sealed class LauncherDialog : Form
{
    public enum Kind { Info, Warning, Error }

    private readonly Theme _t = Theme.Current;
    private readonly Kind _kind;
    private readonly PictureBox _glyph = new();
    private readonly Label _heading = new();
    private readonly Label _body = new();
    private readonly TextBox? _details;
    private readonly FlowLayoutPanel _buttons = new();
    private readonly Dictionary<string, ThemedButton> _byId = new();
    private const int TextWidth = 460;

    public string? Result { get; private set; }

    public LauncherDialog(string title, string heading, string body, IEnumerable<DialogButton> buttons,
        Kind kind = Kind.Info, string? details = null)
    {
        _kind = kind;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = _t.Content,
        };

        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(20, 20, 24, 16),
            BackColor = _t.Content,
        };
        _glyph.Size = new Size(32, 32);
        _glyph.SizeMode = PictureBoxSizeMode.CenterImage;
        _glyph.Margin = new Padding(0, 0, 14, 0);
        content.Controls.Add(_glyph, 0, 0);
        content.SetRowSpan(_glyph, 3);

        _heading.AutoSize = true;
        _heading.MaximumSize = new Size(TextWidth, 0);
        _heading.Font = new Font(_t.SemiboldFamily, 11f);
        _heading.ForeColor = _t.Text;
        _heading.Text = heading;
        _heading.Margin = new Padding(0, 4, 0, 8);
        _heading.UseMnemonic = false;
        content.Controls.Add(_heading, 1, 0);

        _body.AutoSize = true;
        _body.MaximumSize = new Size(TextWidth, 0);
        _body.ForeColor = _t.SubText;
        _body.Text = body;
        _body.Margin = new Padding(0, 0, 0, 4);
        _body.UseMnemonic = false;
        _body.Visible = !string.IsNullOrEmpty(_body.Text);
        content.Controls.Add(_body, 1, 1);

        if (details != null)
        {
            _details = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = _t.Back,
                ForeColor = _t.Text,
                Font = new Font(MonoFamily(), 8.5f),
                Size = new Size(TextWidth + 120, 220),
                Margin = new Padding(0, 10, 0, 0),
                Text = details.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                TabStop = false,
            };
            _details.HandleCreated += (_, _) => _t.ApplyControlTheme(_details);
            content.Controls.Add(_details, 1, 2);
        }
        root.Controls.Add(content, 0, 0);

        // Footer on the chrome color, buttons on the right, like the Edit dialog.
        var footer = new TableLayoutPanel
        {
            BackColor = _t.Back,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(16, 14, 16, 14),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Paint += (_, e) => { using var pen = new Pen(_t.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        _buttons.FlowDirection = FlowDirection.LeftToRight;
        _buttons.AutoSize = true;
        _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _buttons.Anchor = AnchorStyles.Right;
        _buttons.WrapContents = false;
        _buttons.BackColor = _t.Back;
        _buttons.Margin = Padding.Empty;
        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        foreach (var spec in buttons)
        {
            var b = new ThemedButton
            {
                Text = spec.Text,
                Accent = spec.Accent,
                Enabled = spec.Enabled,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(96, 32),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(8, 0, 0, 0),
            };
            if (spec.Tip != null) tips.SetToolTip(b, spec.Tip);
            b.Click += (_, _) => Choose(spec.Id);
            _byId[spec.Id] = b;
            _buttons.Controls.Add(b);
            if (spec.Accent && AcceptButton == null) AcceptButton = b;
        }
        footer.Controls.Add(_buttons, 1, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Result = null; Close(); e.Handled = true; }
        };
    }

    /// <summary>Replace the heading and body of an open notice (another launch of the same app).</summary>
    public void UpdateText(string heading, string body)
    {
        _heading.Text = heading;
        _body.Text = body;
        _body.Visible = body.Length > 0;
    }

    public void SetButtonEnabled(string id, bool enabled)
    {
        if (_byId.TryGetValue(id, out var b)) b.Enabled = enabled;
    }

    private void Choose(string id)
    {
        Result = id;
        if (Modal) DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int px = LogicalToDeviceUnits(28);
        var (glyph, color) = _kind switch
        {
            Kind.Warning => ('', Color.FromArgb(0xE0, 0x9B, 0x00)),
            Kind.Error => ('', _t.Dark ? Color.FromArgb(0xFF, 0x99, 0xA4) : Color.FromArgb(0xC4, 0x2B, 0x1C)),
            _ => ('', _t.Accent),
        };
        var old = _glyph.Image;
        _glyph.Image = _t.Glyph(glyph, px, color);
        old?.Dispose();
        // The run log ends at the bottom: show its last lines.
        if (_details != null)
            BeginInvoke(() =>
            {
                _details.SelectionStart = _details.TextLength;
                _details.ScrollToCaret();
            });
        if (Owner == null) CenterToScreen();
        else if (!Modal)
        {
            // A modeless form does not honor CenterParent: center it on the launcher by hand.
            var o = Owner.Bounds;
            var wa = Screen.FromControl(Owner).WorkingArea;
            int x = o.Left + (o.Width - Width) / 2, y = o.Top + (o.Height - Height) / 2;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
                                 Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _glyph.Image?.Dispose();
        base.Dispose(disposing);
    }

    private static string MonoFamily()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas" })
        {
            try
            {
                using var f = new FontFamily(name);
                return f.Name;
            }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericMonospace.Name;
    }
}
