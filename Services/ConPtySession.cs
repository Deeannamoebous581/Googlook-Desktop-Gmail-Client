using System;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
#endif

namespace Googlook.Services;

/// <summary>
/// A Windows pseudo-console (ConPTY) session. Spawns a child process attached to a
/// real PTY, streams its output, forwards typed input, and resizes with the view.
/// This is the plumbing behind the Gemini CLI tab. Windows 10 1809+ only.
///
/// Full VT/ANSI emulation (colour, cursor addressing, alternate screen) is NOT done
/// here — the view strips escape sequences for a readable line-oriented console,
/// which suits the CLI's auth + prompt flow. Swapping in a VT renderer later is the
/// remaining polish.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    /// <summary>Raised (off the UI thread) with each decoded chunk of console output.</summary>
    public event Action<string>? Output;

#if WINDOWS
    private const int  STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PSEUDOCONSOLE_ATTR = (IntPtr)0x00020016;

    private IntPtr _hPC = IntPtr.Zero;
    private SafeFileHandle? _inputReadSide;   // console reads user input here
    private SafeFileHandle? _outputWriteSide; // console writes output here
    private System.IO.FileStream? _writer;    // we write user input here
    private System.IO.FileStream? _reader;    // we read console output here
    private PROCESS_INFORMATION _proc;
    private IntPtr _attrList = IntPtr.Zero;
    private bool _disposed;

    public bool IsRunning => _proc.hProcess != IntPtr.Zero && !_disposed;

    public void Start(string commandLine, string workingDirectory, short cols, short rows)
    {
        // Two pipes: one for input to the console, one for its output.
        CreatePipe(out _inputReadSide, out var inputWriteSide, IntPtr.Zero, 0);
        CreatePipe(out var outputReadSide, out _outputWriteSide, IntPtr.Zero, 0);

        var size = new COORD { X = cols, Y = rows };
        if (CreatePseudoConsole(size, _inputReadSide!.DangerousGetHandle(),
                _outputWriteSide!.DangerousGetHandle(), 0, out _hPC) != 0)
            throw new InvalidOperationException("CreatePseudoConsole failed (needs Windows 10 1809+).");

        // We own the far ends; wrap them as streams (streams close these handles on dispose).
        _writer = new System.IO.FileStream(inputWriteSide, System.IO.FileAccess.Write);
        _reader = new System.IO.FileStream(outputReadSide, System.IO.FileAccess.Read);

        StartProcess(commandLine, workingDirectory);
        _ = PumpOutputAsync();
    }

    private void StartProcess(string commandLine, string workingDirectory)
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        // Attribute list carrying the pseudo-console handle.
        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        _attrList = Marshal.AllocHGlobal(lpSize);
        startupInfo.lpAttributeList = _attrList;

        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref lpSize))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed.");
        if (!UpdateProcThreadAttribute(_attrList, 0, PSEUDOCONSOLE_ATTR, _hPC,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed.");

        var secAttr = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        var wd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

        if (!CreateProcess(null, commandLine, ref secAttr, ref secAttr, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, wd, ref startupInfo, out _proc))
            throw new InvalidOperationException(
                "CreateProcess failed (" + Marshal.GetLastWin32Error() + ").");
    }

    private async Task PumpOutputAsync()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            while (!_disposed && _reader is not null)
            {
                int read = await _reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0) break;
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch { /* pipe closed on shutdown */ }
    }

    /// <summary>Sends text to the console as if typed (append "\r" for Enter).</summary>
    public void Write(string text)
    {
        if (_writer is null || _disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try { _writer.Write(bytes, 0, bytes.Length); _writer.Flush(); } catch { }
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC != IntPtr.Zero)
            ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

        try { if (_proc.hProcess != IntPtr.Zero) TerminateProcess(_proc.hProcess, 0); } catch { }
        if (_proc.hThread != IntPtr.Zero)  CloseHandle(_proc.hThread);
        if (_proc.hProcess != IntPtr.Zero) CloseHandle(_proc.hProcess);

        _reader?.Dispose();
        _writer?.Dispose();
        _inputReadSide?.Dispose();
        _outputWriteSide?.Dispose();

        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
    }

    // ---- native interop --------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX; public int dwY; public int dwXSize; public int dwYSize;
        public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags;
        public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
        public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes, ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
#else
    public bool IsRunning => false;
    public void Start(string commandLine, string workingDirectory, short cols, short rows) { }
    public void Write(string text) { }
    public void Resize(short cols, short rows) { }
    public void Dispose() { }
#endif
}
