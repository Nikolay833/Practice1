using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// The island's shell: a <see cref="PseudoConsole"/> running PowerShell or Command Prompt (Settings), and the
/// <see cref="VtScreen"/> it draws into. Output is batched onto the UI thread; <see cref="Changed"/> says when to repaint.
/// The shell starts the first time the terminal is shown, keeps running while the island is closed (so a long job, or
/// claude, carries on), and can be started again with Enter after it ends.
/// </summary>
internal sealed class TerminalSession : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<string> _pending = new();
    private PseudoConsole? _console;
    private int _drainScheduled;
    private bool _ended;

    public TerminalSession(SettingsService settings)
    {
        _settings = settings;
        Screen.Reply += reply => _console?.Write(reply); // cursor position and device queries go back to the program
    }

    public VtScreen Screen { get; } = new(80, 24);

    /// <summary>The shell is running.</summary>
    public bool IsRunning => _console != null && !_ended;

    /// <summary>The shell ended and Enter starts it again.</summary>
    public bool HasEnded => _ended;

    /// <summary>New output to draw (UI thread).</summary>
    public event Action? Changed;

    private string ShellName => _settings.Current.TerminalShell == TerminalShell.CommandPrompt ? "Command Prompt" : "PowerShell";

    /// <summary>Starts the shell if it is not running yet (the first time the terminal is shown).</summary>
    public void EnsureStarted(int columns, int rows)
    {
        Screen.Resize(columns, rows);
        if (_console == null)
        {
            Start();
        }
    }

    public void Restart()
    {
        StopShell();
        Screen.Feed("\x1b[2J\x1b[3J\x1b[H");
        Start();
    }

    private void Start()
    {
        _ended = false;
        string saved = _settings.Current.TerminalDirectory;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = saved.Length > 0 && Directory.Exists(saved) ? saved : home;
        string command = _settings.Current.TerminalShell == TerminalShell.CommandPrompt ? "cmd.exe" : "powershell.exe -NoLogo";

        // Tell programs this terminal does colours (claude and other node tools look at these).
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "Dropshelf");
        RefreshPath();

        var console = new PseudoConsole();
        console.Output += OnOutput;
        console.Exited += () => _dispatcher.BeginInvoke(new Action(() => OnExited(console)));
        try
        {
            console.Start(command, folder, Screen.Columns, Screen.Rows);
            _console = console;
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException or IOException)
        {
            Log.Error("Could not start the terminal", ex);
            console.Dispose();
            _ended = true;
            Screen.Feed($"\x1b[91mCould not start {ShellName}: {ex.Message}\x1b[0m\r\n");
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Dropshelf's own PATH is a copy taken when it started, so a tool installed since then (claude, node, git) would be
    /// "not recognized". Rebuild it from the saved system and user PATH, as a new Windows Terminal tab would, keeping any
    /// entries only this process has.
    /// </summary>
    private static void RefreshPath()
    {
        try
        {
            var entries = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? source in new[]
                     {
                         Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
                         Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
                         Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process),
                     })
            {
                foreach (string entry in (source ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (seen.Add(entry.TrimEnd('\\')))
                    {
                        entries.Add(entry);
                    }
                }
            }

            Environment.SetEnvironmentVariable("Path", string.Join(';', entries));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Info($"Could not refresh PATH for the terminal: {ex.Message}");
        }
    }

    private void OnOutput(string text)
    {
        _pending.Enqueue(text);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));
        }
    }

    /// <summary>Feeds everything that arrived since the last frame in one go, then asks for one repaint.</summary>
    private void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        bool any = false;
        while (_pending.TryDequeue(out string? text))
        {
            Screen.Feed(text);
            any = true;
        }

        if (any)
        {
            Changed?.Invoke();
        }
    }

    private void OnExited(PseudoConsole console)
    {
        if (!ReferenceEquals(console, _console))
        {
            return;
        }

        Drain();
        _ended = true;
        Screen.Feed($"\r\n\x1b[90m[{ShellName} ended. Press Enter to start it again.]\x1b[0m\r\n");
        Changed?.Invoke();
    }

    /// <summary>Keyboard input, as the VT sequences a terminal sends.</summary>
    public void Send(string text)
    {
        if (_ended)
        {
            if (text == "\r")
            {
                Restart();
            }

            return;
        }

        _console?.Write(text);
    }

    public void Resize(int columns, int rows)
    {
        Screen.Resize(columns, rows);
        _console?.Resize(columns, rows);
        Changed?.Invoke();
    }

    /// <summary>The shell setting changed: the next start uses the new shell (a running one is left alone).</summary>
    public void ShellChanged()
    {
        if (_ended || _console == null)
        {
            return;
        }

        Restart();
    }

    private void StopShell()
    {
        PseudoConsole? console = _console;
        _console = null;
        if (console != null)
        {
            console.Output -= OnOutput;
            // Closing can wait for the shell to wind down: do it off the UI thread.
            ThreadPool.QueueUserWorkItem(_ => console.Dispose());
        }
    }

    public void Dispose()
    {
        PseudoConsole? console = _console;
        _console = null;
        console?.Dispose();
    }
}
