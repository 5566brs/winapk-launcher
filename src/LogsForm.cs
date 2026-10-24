namespace WinApkLauncher;

/// <summary>
/// The "Logs…" dialog: the run-log limits (MaxRunLogMB, KeepRunLogs, MaxLogsFolderMB), the logs
/// folder's size now and "Delete old logs". Returns the edited limits in <see cref="Result"/>.
/// Mirrored in a right-to-left language.
/// </summary>
internal sealed class LogsForm : Form
{
    private readonly Theme _t = Theme.Current;
    private readonly NumericUpDown _maxLog = new();
    private readonly NumericUpDown _keep = new();
    private readonly NumericUpDown _maxFolder = new();
    private readonly Label _size = new();
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly string _dir;

    public LogLimits Result { get; private set; }

    public LogsForm(LogLimits current, IReadOnlyList<Profile> profiles, string logsDir)
    {
        Result = current;
        _profiles = profiles;
        _dir = logsDir;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.LogsTitle;
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        L.ApplyDirection(this);

        var root = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = _t.Content,
        };
        var grid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Margin = Padding.Empty, Padding = new Padding(20, 18, 24, 12), BackColor = _t.Content,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label Lab(string text) => new()
        {
            Text = text, AutoSize = true, Margin = new Padding(0, 6, 16, 6), UseMnemonic = false, ForeColor = _t.Text,
        };
        void Row(string label, Control value)
        {
            grid.Controls.Add(Lab(label));
            value.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(value);
        }
        NumericUpDown Number(NumericUpDown n, int min, int max, int value)
        {
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Clamp(value, min, max);
            n.Width = 90;
            if (_t.Dark) n.BorderStyle = BorderStyle.FixedSingle;
            return L.KeepLtr(n);
        }

        var folder = L.KeepLtr(new Label
        {
            Text = logsDir, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 6, 0, 6),
            ForeColor = _t.SubText, UseMnemonic = false,
        });
        Row(L.LogsFolder, folder);
        _size.AutoSize = true;
        _size.ForeColor = _t.SubText;
        _size.UseMnemonic = false;
        Row(L.LogsSizeNow, _size);
        Row(L.LogsMaxRun, Number(_maxLog, LogLimits.MaxRunLogMBMin, LogLimits.MaxRunLogMBMax, current.MaxRunLogMB));
        Row(L.LogsKeep, Number(_keep, LogLimits.KeepRunLogsMin, LogLimits.KeepRunLogsMax, current.KeepRunLogs));
        Row(L.LogsMaxFolder, Number(_maxFolder, LogLimits.MaxLogsFolderMBMin, LogLimits.MaxLogsFolderMBMax, current.MaxLogsFolderMB));
        var hint = new Label
        {
            Text = L.LogsHint, AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 12, 0, 0),
            ForeColor = _t.SubText, UseMnemonic = false,
        };
        grid.Controls.Add(hint);
        grid.SetColumnSpan(hint, 2);
        root.Controls.Add(grid);

        // Footer: Delete old logs at the start, OK and Cancel at the end (mirrored in RTL).
        var footer = new TableLayoutPanel
        {
            BackColor = _t.Back, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16, 14, 16, 14),
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Paint += (_, e) => { using var pen = new Pen(_t.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        ThemedButton Button(string text, bool accent = false) => new()
        {
            Text = text, Accent = accent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(96, 32), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(8, 0, 0, 0),
        };
        var delete = Button(L.LogsDeleteOld);
        delete.Margin = Padding.Empty;
        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        tips.SetToolTip(delete, L.LogsDeleteOldTip);
        delete.Click += (_, _) => DeleteOld();
        footer.Controls.Add(delete, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty,
            BackColor = _t.Back, Anchor = AnchorStyles.Right,
        };
        var ok = Button(L.Ok, accent: true);
        var cancel = Button(L.Cancel);
        ok.Click += (_, _) =>
        {
            Result = new LogLimits((int)_maxLog.Value, (int)_keep.Value, (int)_maxFolder.Value);
            DialogResult = DialogResult.OK;
        };
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        footer.Controls.Add(buttons, 2, 0);
        root.Controls.Add(footer);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
        ShowSize();
    }

    private void ShowSize(string? before = null)
    {
        var (bytes, files) = LogRetention.FolderSize(_dir);
        _size.Text = (before != null ? before + "  •  " : "") + L.LogsSizeValue(bytes, files);
    }

    private void DeleteOld()
    {
        var (files, bytes) = LogRetention.DeleteOld(_dir, _profiles);
        ShowSize(L.LogsDeleted(files, bytes));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
    }
}
