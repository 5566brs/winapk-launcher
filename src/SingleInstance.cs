using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinApkLauncher;

/// <summary>
/// One launcher window per settings folder. A second start (for example a double-clicked .apk
/// while the launcher is open) hands its file to the running window over a named pipe and exits,
/// so two windows never overwrite each other's profiles.json. Installed and portable copies use
/// different settings folders and so run independently.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex; // held for the life of the process

    private static string Key { get; } = "WinApkLauncher-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.SettingsDir.ToLowerInvariant())))[..16];

    /// <summary>True when this is the first GUI instance for these settings.</summary>
    public static bool TryBecomePrimary()
    {
        try
        {
            var m = new Mutex(initiallyOwned: true, @"Local\" + Key, out bool created);
            if (created) { _mutex = m; return true; }
            m.Dispose();
            return false;
        }
        catch { return true; } // no mutex, no guard: behave like a normal start
    }

    /// <summary>Send a request (a package path, or "" to just show the window) to the first instance.</summary>
    public static bool SendToPrimary(string? path)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY); // let the running window come to the front
            using var client = new NamedPipeClientStream(".", Key, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            client.Write(Encoding.UTF8.GetBytes(path ?? ""));
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Serve requests from later instances; <paramref name="onRequest"/> runs on a background thread.</summary>
    public static void Listen(Action<string> onRequest)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Key, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var ms = new MemoryStream();
                    server.CopyTo(ms); // the client writes one request and closes
                    onRequest(Encoding.UTF8.GetString(ms.ToArray()).Trim());
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
