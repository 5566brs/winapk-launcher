using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinApkLauncher;

/// <summary>
/// Starts a child process that inherits exactly three handles, its stdin, stdout and stderr, and
/// nothing else: CreateProcessW with STARTF_USESTDHANDLES and PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
/// A child started this way never holds the launcher's own std handles (for example the pipe of a
/// caller running <c>WinApkLauncher.exe --launch X | tail</c>), so the caller is never kept waiting
/// and the child does not depend on the launcher staying alive.
/// </summary>
internal static class NativeProcess
{
    /// <summary>
    /// Starts <paramref name="exe"/> suspended, hands the <see cref="Process"/> to
    /// <paramref name="beforeResume"/> (attach Exited handlers there, so a quick exit is not missed),
    /// then resumes it. <paramref name="env"/> is merged over this process's environment; null
    /// inherits it unchanged. The child gets its own hidden console (CREATE_NO_WINDOW).
    /// </summary>
    public static Process Start(string exe, IEnumerable<string> args, IReadOnlyList<(string Key, string Value)>? env,
        string workingDir, SafeHandle stdin, SafeHandle stdout, SafeHandle stderr, Action<Process>? beforeResume = null)
    {
        var cmd = new StringBuilder();
        AppendArgument(cmd, exe, alwaysQuote: true);
        foreach (var a in args) { cmd.Append(' '); AppendArgument(cmd, a); }
        var commandLine = cmd.ToString();

        var self = GetCurrentProcess();
        var copies = new Dictionary<IntPtr, IntPtr>(); // original -> inheritable duplicate
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero, envBlock = IntPtr.Zero;
        try
        {
            // Inheritable duplicates that exist only for this CreateProcess call; the originals keep
            // their own (normally non-inheritable) flags, so no other child can pick them up.
            IntPtr Inheritable(SafeHandle h)
            {
                var raw = h.DangerousGetHandle();
                if (!copies.TryGetValue(raw, out var dup))
                {
                    if (!DuplicateHandle(self, raw, self, out dup, 0, true, DUPLICATE_SAME_ACCESS))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle failed");
                    copies[raw] = dup;
                }
                return dup;
            }
            var hIn = Inheritable(stdin);
            var hOut = Inheritable(stdout);
            var hErr = Inheritable(stderr);
            var inherit = copies.Values.ToArray(); // distinct: the handle list rejects duplicates

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            }
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * inherit.Length);
            for (int i = 0; i < inherit.Length; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, inherit[i]);
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray,
                    (IntPtr)(IntPtr.Size * inherit.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            if (env != null) envBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(env));

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = hIn;
            si.StartupInfo.hStdOutput = hOut;
            si.StartupInfo.hStdError = hErr;
            si.lpAttributeList = attrList;

            const uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_SUSPENDED;
            // Leave the caller's job object when it allows that, so closing the caller's terminal or
            // tool does not take the app down; otherwise start inside it.
            bool ok = CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                flags | CREATE_BREAKAWAY_FROM_JOB, envBlock, workingDir, ref si, out var pi);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error == ERROR_ACCESS_DENIED)
            {
                ok = CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    flags, envBlock, workingDir, ref si, out pi);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            if (!ok) throw new Win32Exception(error, $"CreateProcess failed for {exe}");

            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                _ = proc.SafeHandle;          // keep a handle, so HasExited/ExitCode stay reliable
                beforeResume?.Invoke(proc);
                if (ResumeThread(pi.hThread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
                return proc;
            }
            catch
            {
                TerminateProcess(pi.hProcess, 1);
                throw;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            foreach (var dup in copies.Values) CloseHandle(dup);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    /// <summary>This process's environment with <paramref name="overrides"/> applied, as a sorted
    /// "k=v\0k=v\0\0" block for CREATE_UNICODE_ENVIRONMENT.</summary>
    private static string EnvironmentBlock(IReadOnlyList<(string Key, string Value)> overrides)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.Length > 0) vars[k] = e.Value as string ?? "";
        foreach (var (k, v) in overrides) vars[k] = v;
        var sb = new StringBuilder();
        foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0'); // StringToHGlobalUni adds the final terminator
        return sb.ToString();
    }

    /// <summary>Quotes one argument the way CommandLineToArgvW / the C runtime parse it.</summary>
    private static void AppendArgument(StringBuilder sb, string arg, bool alwaysQuote = false)
    {
        if (!alwaysQuote && arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    // ----- files a child can write to -----

    /// <summary>
    /// Opens (or creates) <paramref name="path"/> for appending: FILE_APPEND_DATA access, so every
    /// write by any holder lands at the current end, shared for read, write and delete so the log
    /// can be read (and even replaced) while the app writes to it.
    /// </summary>
    public static SafeFileHandle OpenAppend(string path)
    {
        var h = CreateFileW(path, FILE_APPEND_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"cannot open {path}");
        return h;
    }

    /// <summary>The NUL device, readable and writable (an empty stdin, a discarded stderr).</summary>
    public static SafeFileHandle OpenNul()
    {
        var h = CreateFileW("NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot open NUL");
        return h;
    }

    /// <summary>Appends UTF-8 text through a handle from <see cref="OpenAppend"/>. Never throws.</summary>
    public static void Append(SafeFileHandle h, string text)
    {
        try
        {
            if (h.IsInvalid || h.IsClosed) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteFile(h, bytes, bytes.Length, out _, IntPtr.Zero);
        }
        catch { }
    }

    // ----- Win32 -----

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
    private const int ERROR_ACCESS_DENIED = 5;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_APPEND_DATA = 0x00000004;
    private const uint FILE_READ_ATTRIBUTES = 0x00000080;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3, OPEN_ALWAYS = 4;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcess, IntPtr hSource, IntPtr hTargetProcess,
        out IntPtr lpTarget, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, IntPtr lpOverlapped);
}
