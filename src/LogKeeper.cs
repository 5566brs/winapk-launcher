using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinApkLauncher;

/// <summary>
/// A run log that never grows past a size limit. The first <see cref="HeadLimit"/> bytes (the
/// install, the code conversion and an early crash) stay as they were written; once the log is full,
/// a marker line "[… N MB of log omitted …]" follows them, then the newest output, rewritten in place.
/// The file stays readable (shared for read, write and delete) the whole time.
/// </summary>
internal sealed class CappedLog : IDisposable
{
    public const int HeadLimit = 256 * 1024;
    private const int MarkerReserve = 128;

    private readonly FileStream _file;
    private readonly long _max;
    private readonly int _headLimit;
    private long _length;          // bytes in the file while it is below the limit
    private long _lastHeadNewline; // end of the last whole line inside the head
    private long _headEnd = -1;    // where the head ends once it is full
    private long _total;           // bytes received
    private bool _capped;
    private byte[] _ring = Array.Empty<byte>(); // the newest bytes after the head, once capped
    private int _ringStart, _ringCount;
    private bool _dirty;

    public CappedLog(string path, long maxBytes)
    {
        _max = Math.Max(64 * 1024, maxBytes);
        _headLimit = (int)Math.Min(HeadLimit, _max / 4);
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>True when part of the output has been left out (the log is at its limit).</summary>
    public bool Capped => _capped;

    public long FileLength => _file.Length;

    public void Write(ReadOnlySpan<byte> data)
    {
        _total += data.Length;
        if (!_capped)
        {
            int room = (int)Math.Min(data.Length, _max - _length);
            if (room > 0)
            {
                var part = data[..room];
                for (int i = 0; i < part.Length && _length + i < _headLimit; i++)
                    if (part[i] == (byte)'\n') _lastHeadNewline = _length + i + 1;
                _file.Write(part);
                _length += room;
                if (_headEnd < 0 && _length >= _headLimit) _headEnd = _lastHeadNewline > 0 ? _lastHeadNewline : _headLimit;
                data = data[room..];
            }
            if (data.IsEmpty) { _file.Flush(); return; }
            StartCapping();
        }
        Push(data);
        _dirty = true;
    }

    /// <summary>From now on the tail is kept in memory and rewritten after the head; seeds it with what is on disk.</summary>
    private void StartCapping()
    {
        _capped = true;
        if (_headEnd < 0) _headEnd = _lastHeadNewline > 0 ? _lastHeadNewline : Math.Min(_headLimit, _length);
        _ring = new byte[(int)(_max - _headEnd - MarkerReserve)];
        _file.Flush();
        long after = _length - _headEnd;
        int keep = (int)Math.Min(after, _ring.Length);
        var buf = new byte[keep];
        _file.Seek(_length - keep, SeekOrigin.Begin);
        _file.ReadExactly(buf);
        Push(buf);
    }

    private void Push(ReadOnlySpan<byte> data)
    {
        int cap = _ring.Length;
        if (data.Length >= cap)
        {
            data[^cap..].CopyTo(_ring);
            _ringStart = 0;
            _ringCount = cap;
            return;
        }
        foreach (var b in data)
        {
            int end = (_ringStart + _ringCount) % cap;
            _ring[end] = b;
            if (_ringCount < cap) _ringCount++;
            else _ringStart = (_ringStart + 1) % cap;
        }
    }

    /// <summary>Writes the marker and the newest output after the head (only when capped and changed).</summary>
    public void Flush()
    {
        if (!_capped || !_dirty) { _file.Flush(); return; }
        _dirty = false;
        var tail = new byte[_ringCount];
        int first = Math.Min(_ringCount, _ring.Length - _ringStart);
        Array.Copy(_ring, _ringStart, tail, 0, first);
        Array.Copy(_ring, 0, tail, first, _ringCount - first);
        // Start the tail at a whole line.
        int skip = 0;
        if (_ringCount == _ring.Length)
        {
            int nl = Array.IndexOf(tail, (byte)'\n');
            if (nl >= 0 && nl < tail.Length - 1) skip = nl + 1;
        }
        long omitted = _total - _headEnd - (tail.Length - skip);
        bool headEndsLine = _headEnd == 0 || _headEnd == _lastHeadNewline;
        var marker = Encoding.UTF8.GetBytes((headEndsLine ? "" : "\n") +
            $"[… {omitted / 1048576.0:0.0} MB of log omitted …]\n");
        _file.Seek(_headEnd, SeekOrigin.Begin);
        _file.Write(marker);
        _file.Write(tail, skip, tail.Length - skip);
        _file.SetLength(_headEnd + marker.Length + tail.Length - skip);
        _file.Flush();
    }

    public void Dispose()
    {
        try { Flush(); } catch { }
        _file.Dispose();
    }

    /// <summary>Writes 5 MB of numbered lines through a 1 MB log and checks the head, the marker, the tail and the size.</summary>
    public static List<string> SelfTest()
    {
        var failures = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"winapklauncher-selftest-{Environment.ProcessId}.log");
        try
        {
            using (var log = new CappedLog(path, 1 << 20))
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 100_000; i++)
                {
                    sb.Append("[I] line ").Append(i).Append(" of the self-test, some padding to make it longer\n");
                    if (sb.Length > 30_000 || i == 99_999)
                    {
                        log.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                        sb.Clear();
                        if (i % 7 == 0) log.Flush();
                    }
                }
            }
            var size = new FileInfo(path).Length;
            var text = File.ReadAllText(path);
            if (size > 1 << 20) failures.Add($"capped log: {size} bytes, over the 1 MB limit");
            if (!text.StartsWith("[I] line 0 ", StringComparison.Ordinal)) failures.Add("capped log: the head is missing");
            if (!text.Contains("MB of log omitted …]\n[I] line ", StringComparison.Ordinal)) failures.Add("capped log: no marker before a whole tail line");
            if (!text.EndsWith("[I] line 99999 of the self-test, some padding to make it longer\n", StringComparison.Ordinal))
                failures.Add("capped log: the tail does not end with the last line");
        }
        catch (Exception ex) { failures.Add("capped log: " + ex.Message); }
        finally { try { File.Delete(path); } catch { } }
        return failures;
    }
}

/// <summary>
/// The hidden <c>WinApkLauncher.exe --log-keeper &lt;log&gt; &lt;max bytes&gt;</c> process: reads the
/// runtime's stdout and stderr from its stdin (one pipe) and writes them to a <see cref="CappedLog"/>.
/// It runs on its own, so the log keeps growing after the launcher exits, and it exits when the pipe
/// closes: when the runtime, the processes it started and the launcher have all let go of it.
/// </summary>
internal static class LogKeeper
{
    public static int Run(string path, string maxText)
    {
        if (!long.TryParse(maxText, out var max) || max <= 0) return 2;
        using var input = Console.OpenStandardInput();
        using var log = new CappedLog(path, max);
        var gate = new object();
        bool done = false;
        // The tail is rewritten at most twice a second while the output keeps coming.
        var flusher = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(500);
                lock (gate)
                {
                    if (done) return;
                    try { log.Flush(); } catch { }
                }
            }
        }) { IsBackground = true, Name = "log-flush" };
        flusher.Start();
        var buf = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                lock (gate) log.Write(buf.AsSpan(0, n));
        }
        catch (IOException) { } // the pipe broke: same as closed
        lock (gate) done = true;
        return 0;
    }

    /// <summary>
    /// Starts a keeper for <paramref name="logPath"/> and returns it with the write end of its pipe,
    /// which becomes the runtime's stdout and stderr. The keeper inherits only the read end (and NUL).
    /// Throws when it cannot start, so the caller falls back to a plain log file.
    /// </summary>
    public static (Process Keeper, SafeFileHandle Pipe) StartChild(string logPath, long maxBytes)
    {
        if (RuntimeLauncher.LaunchBlocked) throw new InvalidOperationException(L.LaunchBlocked);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 1 << 20))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        try
        {
            using var nul = NativeProcess.OpenNul();
            var keeper = NativeProcess.Start(exe, new[] { "--log-keeper", logPath, maxBytes.ToString() }, env: null,
                AppContext.BaseDirectory, stdin: read, stdout: nul, stderr: nul);
            read.Dispose();
            return (keeper, write);
        }
        catch
        {
            read.Dispose();
            write.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
}
