using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinApkLauncher;

/// <summary>
/// The startup window, shown from Launch until the app's first window opens: the app's icon and
/// name, "Starting…" with an indeterminate bar, the step read from the run log and the time so
/// far. Borderless, in the launcher's theme and topmost while the app starts. If the runtime exits
/// first, it says so and offers Open log and Open logs folder. "Hide" closes only this window.
///
/// In the launcher window, MainForm's startup timer drives it (<see cref="RefreshPhase"/>,
/// <see cref="ShowExited"/>). For --launch and desktop shortcuts, a separate
/// <c>WinApkLauncher.exe --splash</c> process shows it and follows the runtime itself
/// (<see cref="Follow"/>). Any later launch of the same app finds it by a window property keyed
/// on the app folder (<see cref="TryBringToFront"/>) and brings it forward instead of starting
/// anything.
/// </summary>
internal sealed class SplashForm : Form
{
    private const string PropName = "WinApkLauncher.Splash";
    private const int TextWidth = 300;

    private readonly Theme _t = Theme.Current;
    private readonly Profile _profile;
    private readonly ulong _key;
    private readonly Rectangle? _anchor;
    private readonly Mutex? _marker;

    private readonly PictureBox _icon = new();
    private readonly Label _name = new();
    private readonly LinkLabel _hide = new();
    private readonly Label _state = new();
    private readonly Label _elapsed = new();
    private readonly IndeterminateBar _bar = new();
    private readonly Label _phase = new();
    private readonly FlowLayoutPanel _buttons = new();
    private bool _failed;
    private string? _logPath;

    private SafeProcessHandle? _process;
    private int _pid;
    private System.Windows.Forms.Timer? _follow;

    public StartupWatch Watch { get; }

    /// <param name="key">The app folder's key (<see cref="KeyFor"/>): how other launches find this window.</param>
    /// <param name="anchor">The launcher window to center on; null centers on the screen with the mouse.</param>
    public SplashForm(Profile profile, StartupWatch watch, ulong key, Rectangle? anchor, bool inTaskbar)
    {
        _profile = profile;
        Watch = watch;
        _key = key;
        _anchor = anchor;
        _logPath = watch.LogPath;
        try { _marker = new Mutex(false, MarkerName(key)); } catch { }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = inTaskbar;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = $"Starting {profile.Name}";
        Font = _t.UiFont(9f);
        BackColor = _t.Content;
        ForeColor = _t.Text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18, 16, 18, 16),
            Margin = Padding.Empty,
            BackColor = _t.Content,
        };
        _icon.Size = new Size(44, 44);
        _icon.SizeMode = PictureBoxSizeMode.CenterImage;
        _icon.Margin = new Padding(0, 2, 14, 0);
        root.Controls.Add(_icon, 0, 0);

        var text = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = _t.Content,
        };
        Label L(Label l, string s, Color c, Padding m)
        {
            l.AutoSize = true;
            l.Text = s;
            l.ForeColor = c;
            l.Margin = m;
            l.UseMnemonic = false;
            return l;
        }
        L(_name, profile.Name, _t.Text, new Padding(0, 0, 8, 2));
        _name.Font = new Font(_t.SemiboldFamily, 10.5f);
        _name.AutoEllipsis = true;
        _name.MaximumSize = new Size(TextWidth - 50, 0);
        text.Controls.Add(_name, 0, 0);

        _hide.AutoSize = true;
        _hide.Text = "Hide";
        _hide.LinkBehavior = LinkBehavior.HoverUnderline;
        _hide.LinkColor = _hide.ActiveLinkColor = _t.Dark ? Theme.Blend(_t.Accent, Color.White, 0.35f) : _t.Accent;
        _hide.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _hide.Margin = new Padding(0, 2, 0, 0);
        _hide.LinkClicked += (_, _) => Close();
        var tips = new ToolTip();
        tips.SetToolTip(_hide, "Close this window. The app keeps starting.");
        Disposed += (_, _) => tips.Dispose();
        text.Controls.Add(_hide, 1, 0);

        L(_state, "Starting…", _t.Text, new Padding(0, 2, 8, 6));
        text.Controls.Add(_state, 0, 1);
        L(_elapsed, "0:00", _t.SubText, new Padding(0, 2, 0, 6));
        _elapsed.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        text.Controls.Add(_elapsed, 1, 1);

        _bar.Size = new Size(TextWidth, 4);
        _bar.Margin = new Padding(0, 0, 0, 8);
        text.Controls.Add(_bar, 0, 2);
        text.SetColumnSpan(_bar, 2);

        L(_phase, watch.SplashPhaseText, _t.SubText, new Padding(0, 0, 0, 0));
        _phase.MaximumSize = new Size(TextWidth, 0);
        text.Controls.Add(_phase, 0, 3);
        text.SetColumnSpan(_phase, 2);

        _buttons.AutoSize = true;
        _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _buttons.WrapContents = false;
        _buttons.Margin = new Padding(0, 12, 0, 0);
        _buttons.BackColor = _t.Content;
        _buttons.Visible = false;
        ThemedButton B(string s, bool accent, Action click)
        {
            var b = new ThemedButton
            {
                Text = s,
                Accent = accent,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(80, 30),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(0, 0, 8, 0),
            };
            b.Click += (_, _) => click();
            _buttons.Controls.Add(b);
            return b;
        }
        B("Open log", true, () => { if (_logPath != null) ShellOpen.InNotepad(_logPath); });
        B("Open logs folder", false, () => ShellOpen.LogsFolder(_logPath));
        B("Close", false, Close);
        text.Controls.Add(_buttons, 0, 4);
        text.SetColumnSpan(_buttons, 2);

        root.Controls.Add(text, 1, 0);
        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        AppIcons.IconReady += OnIconReady;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    // ----- what the window shows -----

    /// <summary>Show the watch's current step and the time so far.</summary>
    public void RefreshPhase()
    {
        if (_failed || IsDisposed) return;
        _phase.Text = Watch.SplashPhaseText;
        _elapsed.Text = Watch.ElapsedText;
    }

    /// <summary>The runtime exited before its window opened: say so, with the log and its folder.</summary>
    public void ShowExited(int code, string? logPath = null)
    {
        if (IsDisposed) return;
        _failed = true;
        _logPath = logPath ?? _logPath;
        StopFollowing();
        TopMost = false;
        _bar.Visible = false;
        _elapsed.Visible = false;
        _hide.Visible = false;
        _state.Text = code == RuntimeInstance.ExitAlreadyRunning ? "Already running" : "Couldn't start";
        _state.ForeColor = code == RuntimeInstance.ExitAlreadyRunning ? _t.Text
            : _t.Dark ? Color.FromArgb(0xFF, 0x99, 0xA4) : Color.FromArgb(0xC4, 0x2B, 0x1C);
        _state.Font = new Font(_t.SemiboldFamily, 9f);
        _phase.Text = code == RuntimeInstance.ExitAlreadyRunning
            ? $"{_profile.Name} is already running, or still starting in the background."
            : $"{_profile.Name} exited with code {code} before its window opened." +
              (_logPath != null ? "\nThe run log has the details." : "");
        _buttons.Controls[0].Enabled = _logPath != null;
        _buttons.Visible = true;
        Text = $"{_profile.Name} couldn't start";
        PlaceOnScreen();
        try { Activate(); } catch { }
    }

    /// <summary>Follows a runtime by itself (the --splash process): log, exit and first window.</summary>
    public void Follow(SafeProcessHandle process, int pid)
    {
        _process = process;
        _pid = pid;
        _follow = new System.Windows.Forms.Timer { Interval = 500 };
        _follow.Tick += (_, _) => FollowTick();
        _follow.Start();
        FollowTick();
    }

    private void FollowTick()
    {
        if (_process == null || _failed) return;
        Watch.Poll();
        RefreshPhase();
        if (WaitForSingleObject(_process, 0) == 0)
        {
            int code = GetExitCodeProcess(_process, out uint c) ? unchecked((int)c) : -1;
            StopFollowing();
            if (code == 0) Close();
            else ShowExited(code);
            return;
        }
        if (RuntimeInstance.FindAppWindow(_pid) != IntPtr.Zero)
        {
            StopFollowing();
            Close();
        }
    }

    private void StopFollowing()
    {
        _follow?.Stop();
        _follow?.Dispose();
        _follow = null;
    }

    private void OnIconReady()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(SetIcon); } catch { }
    }

    private void SetIcon()
    {
        if (IsDisposed) return;
        int px = LogicalToDeviceUnits(40);
        var img = AppIcons.Get(_profile.ApkPath);
        var old = _icon.Image;
        _icon.Image = img != null ? AppIcons.Scaled(img, px) : AppIcons.LetterTile(_profile.Name, px, _t.SemiboldFamily);
        old?.Dispose();
    }

    // ----- window -----

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }
        catch { }
        SetPropW(Handle, PropName, unchecked((IntPtr)(long)_key));
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemovePropW(Handle, PropName);
        base.OnHandleDestroyed(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetIcon();
        RefreshPhase();
        PlaceOnScreen();
    }

    /// <summary>Centered on the launcher window, or on the screen with the mouse pointer.</summary>
    private void PlaceOnScreen()
    {
        var area = _anchor is Rectangle a && a.Width > 0
            ? a
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        var wa = Screen.FromRectangle(area).WorkingArea;
        int x = area.Left + (area.Width - Width) / 2, y = area.Top + (area.Height - Height) / 2;
        Location = new Point(Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
                             Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Windows 11 draws the border of a rounded window; Windows 10 gets a thin one here.
        if (Theme.IsWindows11) return;
        using var pen = new Pen(_t.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Drag the window by any empty part of it.
        if (m.Msg == 0x0084 /* WM_NCHITTEST */ && m.Result == (IntPtr)1 /* HTCLIENT */) m.Result = (IntPtr)2; // HTCAPTION
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppIcons.IconReady -= OnIconReady;
        StopFollowing();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Image?.Dispose();
            _process?.Dispose();
            _marker?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ----- finding an open startup window -----

    /// <summary>The key of a profile's app folder, as the runtime's lock name uses it.</summary>
    public static ulong KeyFor(Profile p, string? runtimeExe) =>
        RuntimeInstance.Fnv1a(RuntimeInstance.Key(RuntimeInstance.AppDir(p.DataRoot, p.ApkPath), RuntimeInstance.RuntimeWorkingDir(runtimeExe)));

    /// <summary>Held by every startup window and --splash process while it lives.</summary>
    public static string MarkerName(ulong key) => $@"Local\WinApkLauncher-splash-{key:x16}";

    /// <summary>The open startup window for this key in any launcher process, or zero.</summary>
    public static IntPtr Find(ulong key)
    {
        IntPtr found = IntPtr.Zero;
        var want = unchecked((IntPtr)(long)key);
        EnumWindows((h, _) =>
        {
            if (GetPropW(h, PropName) == want && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Brings this app's startup window to the front. A --splash process that has just started may
    /// not have its window yet: while its marker exists, wait up to <paramref name="waitMs"/>.
    /// </summary>
    public static bool TryBringToFront(ulong key, int waitMs = 3000)
    {
        var h = Find(key);
        if (h == IntPtr.Zero && RuntimeInstance.MutexExists(MarkerName(key)))
        {
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (h == IntPtr.Zero && DateTime.UtcNow < until)
            {
                Thread.Sleep(100);
                h = Find(key);
            }
        }
        if (h == IntPtr.Zero) return false;
        RuntimeInstance.BringToFront(h);
        return true;
    }

    // ----- Win32 -----

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetPropW(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetPropW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemovePropW(IntPtr hWnd, string lpString);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle h, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(SafeProcessHandle h, out uint code);
}

/// <summary>A thin bar with an accent segment sliding across it: progress of unknown length.</summary>
internal sealed class IndeterminateBar : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private float _pos;

    public IndeterminateBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => { _pos = (_pos + 0.012f) % 1.4f; Invalidate(); };
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Starting";
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _timer.Enabled = Visible;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _timer.Enabled = Visible;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Content);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Height, r = h / 2f;
        using (var track = new SolidBrush(Theme.Blend(t.Content, t.Text, t.Dark ? 0.16f : 0.10f)))
        using (var path = Theme.RoundRect(new RectangleF(0, 0, Width, h), r))
            g.FillPath(track, path);
        float segW = Width * 0.32f;
        float x = (_pos - 0.4f) * Width;
        var seg = RectangleF.Intersect(new RectangleF(x, 0, segW, h), new RectangleF(0, 0, Width, h));
        if (seg.Width > 0.5f)
        {
            using var b = new SolidBrush(t.Accent);
            using var path = Theme.RoundRect(seg, r);
            g.FillPath(b, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Opening a log or the logs folder for the user.</summary>
internal static class ShellOpen
{
    public static void InNotepad(string path)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
    }

    /// <summary>Opens the run logs folder in Explorer, with <paramref name="select"/> selected if it exists.</summary>
    public static void LogsFolder(string? select)
    {
        Directory.CreateDirectory(RuntimeLauncher.LogsDir);
        var args = select != null && File.Exists(select) ? $"/select,\"{select}\"" : $"\"{RuntimeLauncher.LogsDir}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }
}

/// <summary>
/// The startup window for launches outside the launcher window (--launch, desktop shortcuts):
/// <c>WinApkLauncher.exe --splash &lt;profile id&gt; &lt;process handle&gt; [&lt;run log&gt;]</c>, a detached
/// process that inherits only a handle to the runtime process. The launch itself returns at once,
/// so a caller such as <c>WinApkLauncher.exe --launch X | cat</c> is not kept waiting.
/// </summary>
internal static class SplashProcess
{
    public static int Run(string[] args)
    {
        if (args.Length < 3 || !long.TryParse(args[2], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var raw)) return 2;
        var process = new SafeProcessHandle(unchecked((IntPtr)raw), ownsHandle: true);
        int pid = GetProcessId(process);
        if (pid == 0) { process.Dispose(); return 2; }
        var store = LauncherStore.Load();
        var p = store.Find(args[1]) ?? new Profile { Id = args[1], Name = "App" };
        var log = args.Length > 3 && args[3].Length > 0 ? args[3] : null;

        ApplicationConfiguration.Initialize();
        Theme.Initialize(null);
        using var form = new SplashForm(p, new StartupWatch(p, log), SplashForm.KeyFor(p, store.ResolveRuntime()),
            anchor: null, inTaskbar: true);
        form.Follow(process, pid);
        Application.Run(form);
        return 0;
    }

    /// <summary>Starts a --splash process for a runtime this process holds a handle to.</summary>
    public static bool Spawn(Profile p, SafeHandle runtimeProcess, string? logPath)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || runtimeProcess.IsInvalid || runtimeProcess.IsClosed) return false;
        var me = GetCurrentProcess();
        if (!DuplicateHandle(me, runtimeProcess.DangerousGetHandle(), me, out var dup,
                SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, true, 0)) return false;
        try
        {
            using var nul = NativeProcess.OpenNul();
            using var child = NativeProcess.Start(self,
                new[] { "--splash", p.Id, dup.ToInt64().ToString("x"), logPath ?? "" }, env: null,
                AppContext.BaseDirectory, nul, nul, nul, alsoInherit: new[] { dup });
            AllowSetForegroundWindow(child.Id);
            return true;
        }
        catch { return false; }
        finally { CloseHandle(dup); }
    }

    /// <summary>
    /// Starts a --splash process for a runtime started elsewhere, by its PID. The profile's newest run
    /// log is used only if it was created for this runtime (not before the process started).
    /// </summary>
    public static bool SpawnAttached(Profile p, int pid)
    {
        var h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return false;
        using (h)
        {
            string? log = RuntimeLauncher.LatestLog(p);
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (log != null && File.GetCreationTimeUtc(log) < proc.StartTime.ToUniversalTime().AddSeconds(-5)) log = null;
            }
            catch { log = null; }
            return Spawn(p, h, log);
        }
    }

    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcess, IntPtr hSource, IntPtr hTargetProcess,
        out IntPtr lpTarget, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern int GetProcessId(SafeProcessHandle h);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
