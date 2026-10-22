using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BloodborneLauncher.Services;

/// <summary>
/// run.bat and everything it starts, in a Win32 job object: Stop terminates the job, and
/// the job dies with the launcher (KILL_ON_JOB_CLOSE). The process is created suspended
/// and joined to the job before it runs, so no child can escape. stdout and stderr share
/// one pipe, read on a background thread; stdin is NUL; no console window.
/// </summary>
internal sealed unsafe partial class GameProcess : IDisposable
{
    private readonly IntPtr _job;
    private readonly IntPtr _process;
    private readonly SafeFileHandle _readPipe;
    private int _disposed;

    /// <summary>A decoded output line (reader thread).</summary>
    public event Action<string>? Line;
    /// <summary>The exit code, after the last line (reader thread).</summary>
    public event Action<uint>? Exited;

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    private GameProcess(IntPtr job, IntPtr process, SafeFileHandle readPipe)
    {
        _job = job;
        _process = process;
        _readPipe = readPipe;
    }

    public static GameProcess Start(string commandLine, string workingDir, IDictionary<string, string> environment)
    {
        var job = CreateJobObjectW(null, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception("CreateJobObject");
        }
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            CloseHandle(job);
            throw new Win32Exception("SetInformationJobObject");
        }

        var sa = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        if (!CreatePipe(out var readEnd, out var writeEnd, &sa, 0))
        {
            CloseHandle(job);
            throw new Win32Exception("CreatePipe");
        }
        SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);
        var nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, IntPtr.Zero);

        var si = new STARTUPINFOW
        {
            cb = (uint)sizeof(STARTUPINFOW),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = nul,
            hStdOutput = writeEnd,
            hStdError = writeEnd,
        };
        // CreateProcess wants the block sorted by name, case-insensitively.
        var block = new StringBuilder();
        foreach (var kv in environment.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        block.Append('\0');
        var envChars = block.ToString();
        var cmd = (commandLine + "\0").ToCharArray();
        PROCESS_INFORMATION pi;
        bool ok;
        fixed (char* pCmd = cmd)
        fixed (char* pEnv = envChars)
        fixed (char* pDir = workingDir)
        {
            ok = CreateProcessW(null, pCmd, null, null, 1,
                CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, pEnv, pDir, &si, &pi);
        }
        var error = Marshal.GetLastPInvokeError();
        CloseHandle(writeEnd);
        if (nul != IntPtr.Zero && nul != new IntPtr(-1))
        {
            CloseHandle(nul);
        }
        if (!ok)
        {
            CloseHandle(readEnd);
            CloseHandle(job);
            throw new Win32Exception("CreateProcess", error);
        }
        if (!AssignProcessToJobObject(job, pi.hProcess))
        {
            var assignError = Marshal.GetLastPInvokeError();
            TerminateProcess(pi.hProcess, 1);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            CloseHandle(readEnd);
            CloseHandle(job);
            throw new Win32Exception("AssignProcessToJobObject", assignError);
        }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        return new GameProcess(job, pi.hProcess, new SafeFileHandle(readEnd, ownsHandle: true));
    }

    /// <summary>Starts the reader thread; call after subscribing to the events.</summary>
    public void BeginRead()
    {
        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "run.bat output" };
        thread.Start();
    }

    /// <summary>Kills run.bat and every process it started.</summary>
    public void Kill()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            TerminateJobObject(_job, 1);
        }
    }

    private void ReadLoop()
    {
        var pending = new List<byte>(512);
        var buffer = new byte[8192];
        try
        {
            using var stream = new FileStream(_readPipe, FileAccess.Read, 1, false);
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        Emit(pending);
                    }
                    else
                    {
                        pending.Add(buffer[i]);
                    }
                }
            }
        }
        catch (IOException)
        {
            // Broken pipe: every writer has exited.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Line?.Invoke($"Launcher read error: {ex.Message}");
        }
        if (pending.Count > 0)
        {
            Emit(pending);
        }
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out var code);
        Exited?.Invoke(code);
    }

    private void Emit(List<byte> pending)
    {
        var bytes = CollectionsMarshal.AsSpan(pending);
        Line?.Invoke(Decode(bytes).TrimEnd('\r', ' ', '\t'));
        pending.Clear();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    });

    /// <summary>UTF-8 first, then the system ANSI code page (launcher.py decode_line).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Ansi.Value.GetString(bytes);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CloseHandle(_job); // KILL_ON_JOB_CLOSE: anything still running goes with it.
            CloseHandle(_process);
        }
    }

    // ---------------------------------------------------------------- Win32

    internal sealed class Win32Exception(string api, int error) : Exception($"{api} failed: {new System.ComponentModel.Win32Exception(error).Message} ({error})")
    {
        public Win32Exception(string api) : this(api, Marshal.GetLastPInvokeError()) { }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint HANDLE_FLAG_INHERIT = 1;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint STARTF_USESTDHANDLES = 0x100;
    private const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public void* lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public char* lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public byte* lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(SECURITY_ATTRIBUTES* attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(IntPtr job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out IntPtr read, out IntPtr write, SECURITY_ATTRIBUTES* attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(string name, uint access, uint share, SECURITY_ATTRIBUTES* attributes,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, SECURITY_ATTRIBUTES* processAttributes,
        SECURITY_ATTRIBUTES* threadAttributes, int inheritHandles, uint flags, void* environment, char* currentDirectory,
        STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
