using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// The island terminal's engine. Each command runs in its own hidden PowerShell or cmd.exe process in the current folder; its
/// output (stdout and stderr) streams into <see cref="Output"/>. <c>cd</c>, <c>cls</c> / <c>clear</c> are handled here so the
/// folder survives from one command to the next. Ctrl+C (<see cref="Stop"/>) ends the command and everything it started.
/// Nothing runs unless the user types it and presses Enter.
/// </summary>
internal sealed partial class TerminalSession : IDisposable
{
    private const int MaxOutputChars = 60_000;   // older output is dropped beyond this
    private const int MaxHistory = 100;

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly StringBuilder _output = new();
    private readonly List<string> _history = new();
    private Process? _process;

    public TerminalSession(SettingsService settings)
    {
        _settings = settings;
        string saved = settings.Current.TerminalDirectory;
        Directory = saved.Length > 0 && System.IO.Directory.Exists(saved) ? saved : Home;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folder commands run in.</summary>
    public string Directory { get; private set; }

    /// <summary>The folder for the prompt: under the home folder it starts with "~".</summary>
    public string ShortDirectory
    {
        get
        {
            string home = Home.TrimEnd('\\');
            if (Directory.Equals(home, StringComparison.OrdinalIgnoreCase))
            {
                return "~";
            }

            return Directory.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + Directory[home.Length..] : Directory;
        }
    }

    public string Output => _output.ToString();

    public bool IsRunning => _process != null;

    /// <summary>The command that is running (or ran last).</summary>
    public string LastCommand { get; private set; } = string.Empty;

    public IReadOnlyList<string> History => _history;

    /// <summary>Text was added at the end of <see cref="Output"/> (UI thread).</summary>
    public event Action<string>? Appended;

    /// <summary><see cref="Output"/> was replaced (cleared or trimmed) (UI thread).</summary>
    public event Action? Reset;

    /// <summary><see cref="IsRunning"/> or <see cref="Directory"/> changed (UI thread).</summary>
    public event Action? StateChanged;

    public void Run(string line)
    {
        string command = line.Trim();
        if (IsRunning)
        {
            return;
        }

        Append($"{Prompt} {command}\n");
        if (command.Length == 0)
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != command)
        {
            _history.Add(command);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        LastCommand = command;
        if (TryBuiltIn(command))
        {
            return;
        }

        Start(command);
    }

    /// <summary>The prompt shown before a command: "PS ~\Desktop&gt;" or "~\Desktop&gt;".</summary>
    public string Prompt => (_settings.Current.TerminalShell == TerminalShell.PowerShell ? "PS " : string.Empty) + ShortDirectory + ">";

    public void Clear()
    {
        _output.Clear();
        Reset?.Invoke();
    }

    /// <summary>Ends the running command and every process it started.</summary>
    public void Stop()
    {
        Process? process = _process;
        if (process == null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Log.Error("Could not stop the terminal command", ex);
        }

        Append("^C\n");
    }

    // ---- Built-ins ---------------------------------------------------------------------------------

    /// <summary>cd / chdir / Set-Location, cls / clear. Returns true when the command was handled here.</summary>
    private bool TryBuiltIn(string command)
    {
        Match cd = CdPattern().Match(command);
        if (cd.Success)
        {
            ChangeDirectory(cd.Groups["path"].Value.Trim());
            return true;
        }

        if (command.Equals("cls", StringComparison.OrdinalIgnoreCase) || command.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || command.Equals("Clear-Host", StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return true;
        }

        return false;
    }

    private void ChangeDirectory(string argument)
    {
        // cmd's "cd /d X" switch, quotes and "~" are understood; "cd" alone goes home (like PowerShell).
        if (argument.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
        {
            argument = argument[3..].Trim();
        }

        argument = argument.Trim('"', '\'');
        if (argument.Length == 0 || argument == "~")
        {
            SetDirectory(Home);
            return;
        }

        if (argument.StartsWith("~\\", StringComparison.Ordinal) || argument.StartsWith("~/", StringComparison.Ordinal))
        {
            argument = Path.Combine(Home, argument[2..]);
        }

        try
        {
            string target = Path.GetFullPath(Path.Combine(Directory, Environment.ExpandEnvironmentVariables(argument)));
            if (System.IO.Directory.Exists(target))
            {
                SetDirectory(target);
            }
            else
            {
                Append($"The folder '{argument}' does not exist.\n");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            Append($"'{argument}' is not a valid folder.\n");
        }
    }

    private void SetDirectory(string path)
    {
        Directory = path.Length > 3 ? path.TrimEnd('\\') : path;
        _settings.Current.TerminalDirectory = Directory;
        StateChanged?.Invoke();
    }

    [GeneratedRegex(@"^\s*(?:cd|chdir|sl|set-location)(?:\s+(?<path>.*))?\s*$|^\s*cd(?<path>\.\.?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CdPattern();

    // ---- Running a command -------------------------------------------------------------------------

    private void Start(string command)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = System.IO.Directory.Exists(Directory) ? Directory : Home,
        };
        psi.Environment["NO_COLOR"] = "1"; // plain text: the output pane has no colours
        psi.Environment["TERM"] = "dumb";

        if (_settings.Current.TerminalShell == TerminalShell.PowerShell)
        {
            // Errors are merged into the output as text; progress bars are off (they would print as XML on a redirected stream).
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                                 + "& { " + command + "\n} 2>&1 | Out-String -Stream -Width 160");
        }
        else
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"chcp 65001>nul & " + command + "\"";
        }

        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(process, e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(process, e.Data);
            process.Exited += (_, _) => OnExited(process);
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("Could not start the terminal shell", ex);
            Append($"Could not start {psi.FileName}: {ex.Message}\n");
            return;
        }

        _process = process;
        StateChanged?.Invoke();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            // Nothing is typed into a running command: end of input right away, so "pause" or "Read-Host" cannot hang it.
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process already ended.
        }
    }

    private void OnLine(Process process, string? line)
    {
        if (line == null)
        {
            return;
        }

        string clean = AnsiPattern().Replace(line, string.Empty);
        _dispatcher.BeginInvoke(new Action(() => Append(clean + "\n")));
    }

    /// <summary>Runs on a worker thread when the shell exits.</summary>
    private void OnExited(Process process)
    {
        // Let the last output lines arrive first. Bounded: a program the command started (start notepad) can keep the pipes open.
        try
        {
            process.WaitForExit(1500);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Already disposed.
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ReferenceEquals(process, _process))
            {
                return;
            }

            int code = 0;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // Killed before it reported one.
            }

            process.Dispose();
            _process = null;
            LastExitCode = code;
            StateChanged?.Invoke();
            Finished?.Invoke();
        }));
    }

    /// <summary>Exit code of the last command (0 = success).</summary>
    public int LastExitCode { get; private set; }

    /// <summary>A command finished (UI thread).</summary>
    public event Action? Finished;

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiPattern();

    private void Append(string text)
    {
        _output.Append(text);
        if (_output.Length > MaxOutputChars)
        {
            int cut = _output.Length - MaxOutputChars * 3 / 4;
            string current = _output.ToString(cut, _output.Length - cut);
            int newline = current.IndexOf('\n');
            _output.Clear();
            _output.Append(newline >= 0 ? current[(newline + 1)..] : current);
            Reset?.Invoke();
            return;
        }

        Appended?.Invoke(text);
    }

    public void Dispose()
    {
        Process? process = _process;
        _process = null;
        if (process != null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
        }
    }
}
