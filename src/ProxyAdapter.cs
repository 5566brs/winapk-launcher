using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinApkLauncher;

/// <summary>A clean, user-facing failure from the upstream proxy (shown by the Test button / logged).</summary>
public sealed class ProxyException : Exception
{
    public ProxyException(string message) : base(message) { }
}

/// <summary>
/// Opens a TCP tunnel to a target host:port through an upstream proxy: SOCKS5 (no-auth or
/// user/pass, RFC 1929), SOCKS4 / SOCKS4a, or HTTP CONNECT (with Basic auth). Shared by the
/// local adapter and the Edit dialog's "Test" button, so both exercise exactly the same path.
/// </summary>
public static class ProxyUpstream
{
    /// <summary>
    /// Connect through <paramref name="cfg"/> to <paramref name="host"/>:<paramref name="port"/> and
    /// return a connected socket already tunnelled to the target. Throws <see cref="ProxyException"/>
    /// with a clear message on any failure. <paramref name="host"/> may be a name or an IPv4/IPv6 literal.
    /// </summary>
    public static async Task<Socket> ConnectAsync(ProxyConfig cfg, string host, int port, CancellationToken ct)
    {
        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            try { await sock.ConnectAsync(cfg.Host, cfg.Port, ct); }
            catch (OperationCanceledException) { throw new ProxyException("timed out connecting to the proxy"); }
            catch (SocketException ex) { throw new ProxyException($"cannot reach proxy {cfg.Host}:{cfg.Port} ({ex.SocketErrorCode})"); }

            switch (cfg.Kind)
            {
                case ProxyKind.Socks5: await Socks5(sock, cfg, host, port, ct); break;
                case ProxyKind.Socks4: await Socks4(sock, cfg, host, port, ct); break;
                case ProxyKind.Http: await HttpConnect(sock, cfg, host, port, ct); break;
                default: throw new ProxyException("no proxy configured");
            }
            return sock;
        }
        catch
        {
            try { sock.Dispose(); } catch { }
            throw;
        }
    }

    // ----- SOCKS5 (RFC 1928 / 1929) -----

    private static async Task Socks5(Socket s, ProxyConfig cfg, string host, int port, CancellationToken ct)
    {
        // Greeting: offer no-auth, and user/pass when a user is set.
        var greet = cfg.HasAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
        await Send(s, greet, ct);
        var sel = await Recv(s, 2, ct);
        if (sel[0] != 0x05) throw new ProxyException("not a SOCKS5 proxy");
        byte method = sel[1];
        if (method == 0xFF) throw new ProxyException(cfg.HasAuth ? "proxy rejected the offered auth methods" : "proxy requires authentication");
        if (method == 0x02)
        {
            if (!cfg.HasAuth) throw new ProxyException("proxy requires a username and password");
            var u = Encoding.UTF8.GetBytes(cfg.User);
            var p = Encoding.UTF8.GetBytes(cfg.Password);
            if (u.Length > 255 || p.Length > 255) throw new ProxyException("username/password too long for SOCKS5");
            var auth = new byte[3 + u.Length + p.Length];
            int i = 0;
            auth[i++] = 0x01;
            auth[i++] = (byte)u.Length; Array.Copy(u, 0, auth, i, u.Length); i += u.Length;
            auth[i++] = (byte)p.Length; Array.Copy(p, 0, auth, i, p.Length);
            await Send(s, auth, ct);
            var ar = await Recv(s, 2, ct);
            if (ar[1] != 0x00) throw new ProxyException("proxy authentication failed (bad username or password)");
        }
        else if (method != 0x00)
            throw new ProxyException($"proxy chose an unsupported SOCKS5 auth method (0x{method:X2})");

        // CONNECT request. Send a literal address when host is an IP, else a domain name (proxy resolves).
        var req = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(host, out var ip))
        {
            bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            req.Add((byte)(v6 ? 0x04 : 0x01));
            req.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.UTF8.GetBytes(host);
            if (name.Length > 255) throw new ProxyException("host name too long for SOCKS5");
            req.Add(0x03);
            req.Add((byte)name.Length);
            req.AddRange(name);
        }
        req.Add((byte)(port >> 8));
        req.Add((byte)(port & 0xFF));
        await Send(s, req.ToArray(), ct);

        var head = await Recv(s, 4, ct);
        if (head[0] != 0x05) throw new ProxyException("malformed SOCKS5 reply");
        if (head[1] != 0x00) throw new ProxyException("SOCKS5 proxy refused: " + Socks5Error(head[1]));
        int skip = head[3] switch { 0x01 => 4, 0x04 => 16, 0x03 => 1 + (await Recv(s, 1, ct))[0], _ => throw new ProxyException("bad SOCKS5 reply address") };
        await Recv(s, skip + 2, ct); // bound address + port, discarded
    }

    private static string Socks5Error(byte b) => b switch
    {
        0x01 => "general failure",
        0x02 => "connection not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"code 0x{b:X2}",
    };

    // ----- SOCKS4 / SOCKS4a -----

    private static async Task Socks4(Socket s, ProxyConfig cfg, string host, int port, CancellationToken ct)
    {
        byte[] ipBytes;
        bool socks4a = false;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                throw new ProxyException("SOCKS4 cannot route an IPv6 address (use SOCKS5)");
            ipBytes = ip.GetAddressBytes();
        }
        else
        {
            // SOCKS4a: 0.0.0.x (x != 0) then the host name after the user id.
            ipBytes = new byte[] { 0, 0, 0, 1 };
            socks4a = true;
        }

        var user = Encoding.UTF8.GetBytes(cfg.User ?? "");
        var req = new List<byte> { 0x04, 0x01, (byte)(port >> 8), (byte)(port & 0xFF) };
        req.AddRange(ipBytes);
        req.AddRange(user);
        req.Add(0x00);
        if (socks4a)
        {
            req.AddRange(Encoding.UTF8.GetBytes(host));
            req.Add(0x00);
        }
        await Send(s, req.ToArray(), ct);

        var reply = await Recv(s, 8, ct);
        if (reply[1] != 0x5A)
            throw new ProxyException(reply[1] switch
            {
                0x5B => "SOCKS4 proxy rejected the request",
                0x5C => "SOCKS4 proxy could not reach the identd client",
                0x5D => "SOCKS4 proxy identd auth failed",
                _ => $"SOCKS4 proxy refused (code 0x{reply[1]:X2})",
            });
    }

    // ----- HTTP CONNECT -----

    private static async Task HttpConnect(Socket s, ProxyConfig cfg, string host, int port, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var target = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
        sb.Append($"CONNECT {target} HTTP/1.1\r\n");
        sb.Append($"Host: {target}\r\n");
        if (cfg.HasAuth)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.User}:{cfg.Password}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("Proxy-Connection: keep-alive\r\n\r\n");
        await Send(s, Encoding.ASCII.GetBytes(sb.ToString()), ct);

        var status = await ReadHttpStatus(s, ct);
        if (status.Code == 407) throw new ProxyException("HTTP proxy authentication required/failed (407)");
        if (status.Code is < 200 or > 299) throw new ProxyException($"HTTP proxy refused: {status.Code} {status.Reason}");
    }

    /// <summary>Read an HTTP status line plus headers up to the blank line; return the status.</summary>
    private static async Task<(int Code, string Reason)> ReadHttpStatus(Socket s, CancellationToken ct)
    {
        var raw = await ReadUntil(s, "\r\n\r\n", 64 * 1024, ct);
        var firstLine = raw.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/") || !int.TryParse(parts[1], out var code))
            throw new ProxyException("not an HTTP proxy (bad response)");
        return (code, parts.Length > 2 ? parts[2] : "");
    }

    // ----- Test button: a real end-to-end check through the upstream -----

    public readonly record struct TestResult(bool Ok, long LatencyMs, string Message);

    /// <summary>
    /// Connect through the upstream to www.gstatic.com:80 and GET /generate_204. Returns success with
    /// latency, or the exact failure (auth failed, connection refused, …). A 204/200 counts as success.
    /// Overridable host via WINAPKLAUNCHER_TEST_URL=host:port/path for offline testing.
    /// </summary>
    public static async Task<TestResult> TestAsync(ProxyConfig cfg, CancellationToken ct = default)
    {
        var (host, port, path) = TestTarget();
        var sw = Stopwatch.StartNew();
        try
        {
            using var sock = await ConnectAsync(cfg, host, port, ct);
            var req = $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: WinApkLauncher\r\nConnection: close\r\n\r\n";
            await Send(sock, Encoding.ASCII.GetBytes(req), ct);
            var status = await ReadHttpStatus(sock, ct);
            sw.Stop();
            if (status.Code is 204 or 200)
                return new TestResult(true, sw.ElapsedMilliseconds, $"OK — {cfg.Kind} proxy reachable, {host} responded {status.Code} in {sw.ElapsedMilliseconds} ms");
            return new TestResult(false, sw.ElapsedMilliseconds, $"Reached {host} through the proxy but it answered {status.Code} {status.Reason}");
        }
        catch (ProxyException ex) { return new TestResult(false, sw.ElapsedMilliseconds, ex.Message); }
        catch (OperationCanceledException) { return new TestResult(false, sw.ElapsedMilliseconds, "timed out"); }
        catch (Exception ex) { return new TestResult(false, sw.ElapsedMilliseconds, ex.Message); }
    }

    private static (string Host, int Port, string Path) TestTarget()
    {
        var o = Environment.GetEnvironmentVariable("WINAPKLAUNCHER_TEST_URL");
        if (!string.IsNullOrWhiteSpace(o))
        {
            var slash = o.IndexOf('/');
            var hostPort = slash >= 0 ? o[..slash] : o;
            var path = slash >= 0 ? o[slash..] : "/generate_204";
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var pp)) return (hostPort[..colon], pp, path);
            return (hostPort, 80, path);
        }
        return ("www.gstatic.com", 80, "/generate_204");
    }

    // ----- low-level socket helpers -----

    private static async Task Send(Socket s, byte[] data, CancellationToken ct)
    {
        int sent = 0;
        while (sent < data.Length)
            sent += await s.SendAsync(data.AsMemory(sent), SocketFlags.None, ct);
    }

    private static async Task<byte[]> Recv(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (r == 0) throw new ProxyException("proxy closed the connection unexpectedly");
            got += r;
        }
        return buf;
    }

    private static async Task<string> ReadUntil(Socket s, string marker, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < max)
        {
            int r = await s.ReceiveAsync(one.AsMemory(), SocketFlags.None, ct);
            if (r == 0) throw new ProxyException("proxy closed the connection unexpectedly");
            sb.Append((char)one[0]);
            if (sb.Length >= marker.Length && EndsWith(sb, marker)) return sb.ToString();
        }
        throw new ProxyException("proxy response too large / malformed");
    }

    private static bool EndsWith(StringBuilder sb, string s)
    {
        if (sb.Length < s.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (sb[sb.Length - s.Length + i] != s[i]) return false;
        return true;
    }
}

/// <summary>
/// The local adapter process (<c>--proxy-adapter &lt;profileId&gt;</c>): a loopback-only SOCKS5 server
/// (no auth) that forwards every connection through the profile's configured upstream proxy. The
/// launcher appends <c>-DsocksProxyHost=127.0.0.1 -DsocksProxyPort=&lt;port&gt;</c> to the app's JVM opts
/// and sets <c>WINAPK_PROXY=socks5://127.0.0.1:&lt;port&gt;</c> (read by the runtime's push client), so
/// the app's Java traffic flows through here. Prints its port on stdout, then reads the runtime PID
/// from stdin and exits when that process exits (or if no PID arrives within 60 s).
/// </summary>
public static class ProxyAdapter
{
    /// <summary>Entry point for the hidden child process. Returns a process exit code.</summary>
    public static int Run(string profileId)
    {
        var store = LauncherStore.Load();
        var profile = store.Find(profileId) ?? store.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) { Console.Error.WriteLine($"proxy-adapter: no profile {profileId}"); return 2; }
        var cfg = profile.ResolveProxy();
        if (cfg == null) { Console.Error.WriteLine("proxy-adapter: profile has no proxy"); return 2; }

        var logPath = Path.Combine(RuntimeLauncher.RuntimeLogDir(profile), "proxy.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }
        using var logger = new ConnLog(logPath);

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);  // loopback only, ephemeral port
            listener.Start();
        }
        catch (Exception ex) { Console.Error.WriteLine("proxy-adapter: cannot listen: " + ex.Message); return 2; }

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        logger.Line("adapter", $"listening 127.0.0.1:{port} -> {cfg.Kind} {cfg.Host}:{cfg.Port}" + (cfg.HasAuth ? " (auth)" : ""));
        // Stdout contract: the launcher reads this exact line to learn the port.
        Console.Out.WriteLine("PORT " + port);
        Console.Out.Flush();

        using var life = new CancellationTokenSource();
        WatchRuntimePid(life, logger);

        var serveTask = Serve(listener, cfg, logger, life.Token);
        try { serveTask.Wait(); } catch { }
        try { listener.Stop(); } catch { }
        logger.Line("adapter", "exit");
        return 0;
    }

    /// <summary>
    /// Read the runtime PID from stdin (one line) and exit when that process exits. If no PID arrives
    /// within 60 s, exit anyway so a stray adapter never lingers. The launcher window closing does not
    /// matter once the PID is known.
    /// </summary>
    private static void WatchRuntimePid(CancellationTokenSource life, ConnLog logger)
    {
        var thread = new Thread(() =>
        {
            int pid = -1;
            var gotPid = new ManualResetEventSlim(false);
            var reader = new Thread(() =>
            {
                try
                {
                    // The launcher writes one line: the runtime PID. Skip blank/garbage lines and read
                    // digits only, to be robust to any stray whitespace or BOM on the pipe.
                    string? line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        var digits = new string(line.Where(char.IsDigit).ToArray());
                        if (digits.Length > 0 && int.TryParse(digits, out var p)) { pid = p; break; }
                    }
                }
                catch { }
                gotPid.Set();
            }) { IsBackground = true };
            reader.Start();

            if (!gotPid.Wait(TimeSpan.FromSeconds(60)) || pid <= 0)
            {
                logger.Line("adapter", pid <= 0 ? "no runtime PID within 60 s, shutting down" : "stdin closed, shutting down");
                life.Cancel();
                return;
            }
            logger.Line("adapter", $"tracking runtime pid {pid}");
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.WaitForExit();
                logger.Line("adapter", $"runtime pid {pid} exited, shutting down");
            }
            catch { logger.Line("adapter", $"runtime pid {pid} not found, shutting down"); }
            life.Cancel();
        }) { IsBackground = true };
        thread.Start();
    }

    private static async Task Serve(TcpListener listener, ProxyConfig cfg, ConnLog logger, CancellationToken ct)
    {
        var active = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { break; }
            var task = HandleClient(client, cfg, logger, ct);
            lock (active) { active.Add(task); active.RemoveAll(t => t.IsCompleted); }
        }
    }

    private static async Task HandleClient(TcpClient client, ProxyConfig cfg, ConnLog logger, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        Socket? upstream = null;
        try
        {
            var c = client.Client;
            var (host, port) = await AcceptSocks5(c, ct);

            try { upstream = await ProxyUpstream.ConnectAsync(cfg, host, port, Timeout(ct, 20)); }
            catch (ProxyException ex)
            {
                logger.Line($"{host}:{port}", "FAIL " + ex.Message);
                await ReplySocks5(c, 0x05, ct); // connection refused by ruleset -> generic failure to the app
                return;
            }

            await ReplySocks5(c, 0x00, ct);
            logger.Line($"{host}:{port}", "OK");
            await Relay(c, upstream, ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException && ex is not ProxyException)
                logger.Line("-", "client error: " + ex.GetType().Name);
        }
        finally
        {
            try { upstream?.Dispose(); } catch { }
        }
    }

    /// <summary>Perform the server side of a no-auth SOCKS5 CONNECT; return the requested target.</summary>
    private static async Task<(string Host, int Port)> AcceptSocks5(Socket c, CancellationToken ct)
    {
        var hdr = await RecvExact(c, 2, ct);
        if (hdr[0] != 0x05) throw new ProxyException("client is not SOCKS5");
        int nMethods = hdr[1];
        await RecvExact(c, nMethods, ct);                // method list, ignored (we only do no-auth)
        await SendAll(c, new byte[] { 0x05, 0x00 }, ct); // choose no-auth

        var req = await RecvExact(c, 4, ct);
        if (req[0] != 0x05) throw new ProxyException("bad SOCKS5 request");
        if (req[1] != 0x01)                              // only CONNECT
        {
            await SendAll(c, new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            throw new ProxyException("unsupported SOCKS5 command");
        }
        string host = req[3] switch
        {
            0x01 => new IPAddress(await RecvExact(c, 4, ct)).ToString(),
            0x04 => new IPAddress(await RecvExact(c, 16, ct)).ToString(),
            0x03 => Encoding.UTF8.GetString(await RecvExact(c, (await RecvExact(c, 1, ct))[0], ct)),
            _ => throw new ProxyException("bad SOCKS5 address type"),
        };
        var pb = await RecvExact(c, 2, ct);
        int port = (pb[0] << 8) | pb[1];
        return (host, port);
    }

    private static Task ReplySocks5(Socket c, byte rep, CancellationToken ct) =>
        SendAll(c, new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);

    private static async Task Relay(Socket a, Socket b, CancellationToken ct)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = Pump(a, b, link);
        var t2 = Pump(b, a, link);
        await Task.WhenAny(t1, t2);
        link.Cancel();
        try { await Task.WhenAll(t1, t2); } catch { }
    }

    private static async Task Pump(Socket from, Socket to, CancellationTokenSource link)
    {
        var buf = new byte[32 * 1024];
        try
        {
            while (true)
            {
                int n = await from.ReceiveAsync(buf, SocketFlags.None, link.Token);
                if (n == 0) break;
                int sent = 0;
                while (sent < n) sent += await to.SendAsync(buf.AsMemory(sent, n - sent), SocketFlags.None, link.Token);
            }
        }
        catch { }
        finally
        {
            try { to.Shutdown(SocketShutdown.Send); } catch { }
            link.Cancel();
        }
    }

    private static CancellationToken Timeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts.Token;
    }

    private static async Task<byte[]> RecvExact(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (r == 0) throw new ProxyException("client closed the connection");
            got += r;
        }
        return buf;
    }

    private static async Task SendAll(Socket s, byte[] data, CancellationToken ct)
    {
        int sent = 0;
        while (sent < data.Length) sent += await s.SendAsync(data.AsMemory(sent), SocketFlags.None, ct);
    }

    // ----- launcher-side helper: start the adapter child and read its port -----

    /// <summary>
    /// Start <c>WinApkLauncher.exe --proxy-adapter &lt;profileId&gt;</c> as a hidden child and read the
    /// port it prints. The caller then launches the runtime with the SOCKS flags and writes the runtime
    /// PID to <see cref="Process.StandardInput"/>. Throws on failure so Launch can report it.
    /// </summary>
    public static (Process Process, int Port) StartChild(string profileId)
    {
        if (RuntimeLauncher.LaunchBlocked) throw new InvalidOperationException("Launching is disabled in this mode.");
        var exe = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--proxy-adapter");
        psi.ArgumentList.Add(profileId);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start the proxy adapter");
        try
        {
            // The adapter prints "PORT <n>" as its first stdout line, promptly.
            var readPort = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                    if (line.StartsWith("PORT ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(5).Trim(), out var p))
                        return p;
                return -1;
            });
            if (!readPort.Wait(TimeSpan.FromSeconds(10)) || readPort.Result <= 0)
                throw new InvalidOperationException("the proxy adapter did not report a port");
            return (proc, readPort.Result);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            throw;
        }
    }
}

/// <summary>Appends one line per connection to proxy.log, time-stamped. Never throws.</summary>
internal sealed class ConnLog : IDisposable
{
    private readonly StreamWriter? _w;
    private readonly object _gate = new();

    public ConnLog(string path)
    {
        try
        {
            _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            _w.WriteLine($"# ---- proxy adapter started {DateTime.Now:O} (pid {Environment.ProcessId}) ----");
        }
        catch { _w = null; }
    }

    public void Line(string target, string note)
    {
        lock (_gate)
        {
            try { _w?.WriteLine($"{DateTime.Now:HH:mm:ss}  {target,-28}  {note}"); } catch { }
        }
    }

    public void Dispose() { try { _w?.Dispose(); } catch { } }
}
