using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// A shell (PowerShell or Command Prompt) running in a Windows pseudo console (ConPTY, Windows 10 1809+), the same mechanism
/// Windows Terminal uses. The program believes it has a real console, so interactive tools (claude, vim, ssh, prompts with
/// colours and arrow keys) work. Output arrives as text with VT escape sequences on a reader thread
/// (<see cref="Output"/>); keyboard input is written as VT sequences with <see cref="Write"/>.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new(0x00020016);

    private readonly object _writeGate = new();
    private IntPtr _console;
    private IntPtr _attributes;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _input;
    private IntPtr _process;
    private IntPtr _thread;
    private Thread? _reader;
    private volatile bool _disposed;

    /// <summary>Decoded output text (on the reader thread).</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended (on a worker thread).</summary>
    public event Action? Exited;

    public bool IsRunning => _process != IntPtr.Zero && !_disposed;

    /// <summary>Starts <paramref name="commandLine"/> in a <paramref name="columns"/> x <paramref name="rows"/> console.</summary>
    public void Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0)
            || !CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the console pipes");
        }

        int hr = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _console);
        inputRead.Dispose();   // the console has its own copies now
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "Could not create the pseudo console (Windows 10 1809 or newer is needed)");
        }

        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _input = new FileStream(inputWrite, FileAccess.Write, 1, false);

        // A process attribute list that hands the pseudo console to the new process.
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributes = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributes, 1, 0, ref size)
            || !UpdateProcThreadAttribute(_attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the pseudo console");
        }

        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() }, lpAttributeList = _attributes };
        if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workingDirectory, ref startup, out ProcessInformation info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start " + commandLine);
        }

        _process = info.hProcess;
        _thread = info.hThread;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Dropshelf terminal output" };
        _reader.Start();

        var waiter = new Thread(() =>
        {
            WaitForSingleObject(_process, 0xFFFFFFFF);
            if (!_disposed)
            {
                Exited?.Invoke();
            }
        }) { IsBackground = true, Name = "Dropshelf terminal wait" };
        waiter.Start();
    }

    /// <summary>Must keep draining: a pseudo console whose output is not read stops (and closing it can hang).</summary>
    private void ReadLoop()
    {
        Decoder decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024 + 4];
        try
        {
            using var stream = new FileStream(_outputRead!, FileAccess.Read, 1, false);
            while (!_disposed)
            {
                int read = stream.Read(bytes, 0, bytes.Length);
                if (read <= 0)
                {
                    break;
                }

                int count = decoder.GetChars(bytes, 0, read, chars, 0);
                if (count > 0)
                {
                    Output?.Invoke(new string(chars, 0, count));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The console closed.
        }
    }

    /// <summary>Sends keyboard input (VT sequences, UTF-8).</summary>
    public void Write(string text)
    {
        if (_disposed || _input == null || text.Length == 0)
        {
            return;
        }

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            lock (_writeGate)
            {
                _input.Write(data, 0, data.Length);
                _input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Info($"Terminal input dropped: {ex.Message}");
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_console != IntPtr.Zero && !_disposed)
        {
            ResizePseudoConsole(_console, new Coord(columns, rows));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_console != IntPtr.Zero)
            {
                ClosePseudoConsole(_console); // ends the shell; the reader thread drains the last output meanwhile
                _console = IntPtr.Zero;
            }

            if (_process != IntPtr.Zero)
            {
                TerminateProcess(_process, 0); // in case something ignored the close
            }
        }
        catch (Exception ex)
        {
            Log.Error("Closing the terminal failed", ex);
        }

        _input?.Dispose();
        _inputWrite?.Dispose();
        _outputRead?.Dispose();
        if (_attributes != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributes);
            Marshal.FreeHGlobal(_attributes);
            _attributes = IntPtr.Zero;
        }

        if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }
    }

    // ---- Interop -------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public Coord(int x, int y)
        {
            X = (short)Math.Clamp(x, 1, short.MaxValue);
            Y = (short)Math.Clamp(y, 1, short.MaxValue);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
