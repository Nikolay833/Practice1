using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// The Terminal tab: Enter runs the line, Up / Down walk the history, Ctrl+C stops a running command (or copies a selection),
/// Ctrl+L clears, Esc gives the keyboard back to the app you were in.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly TerminalSession _session;
    private readonly SettingsService _settings;
    private int _historyIndex = -1;   // -1 = typing a new line
    private string _draft = string.Empty;

    internal TerminalView(TerminalSession session, SettingsService settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        OutputBox.Text = session.Output;
        session.Appended += OnAppended;
        session.Reset += OnReset;
        session.StateChanged += UpdateState;
        settings.Changed += name =>
        {
            if (name == nameof(AppSettings.TerminalShell))
            {
                UpdateState();
            }
        };
        UpdateState();
        Loaded += (_, _) => OutputBox.ScrollToEnd();
    }

    /// <summary>Esc: the island should let go of the keyboard.</summary>
    public event Action? ReleaseRequested;

    /// <summary>Puts the caret in the prompt (the tab must be showing).</summary>
    public void FocusInput() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
        }));

    private void OnAppended(string text)
    {
        OutputBox.AppendText(text);
        OutputBox.ScrollToEnd();
        Hint.Visibility = Visibility.Collapsed;
    }

    private void OnReset()
    {
        OutputBox.Text = _session.Output;
        OutputBox.ScrollToEnd();
        Hint.Visibility = OutputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateState()
    {
        bool running = _session.IsRunning;
        PromptText.Text = running ? "running…" : _session.Prompt;
        Input.IsReadOnly = running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        bool powerShell = _settings.Current.TerminalShell == TerminalShell.PowerShell;
        PowerShellOption.IsChecked = powerShell;
        CmdOption.IsChecked = !powerShell;
        PowerShellOption.IsEnabled = CmdOption.IsEnabled = !running;
        Hint.Visibility = OutputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Enter when !_session.IsRunning:
                string line = Input.Text;
                Input.Clear();
                _historyIndex = -1;
                _draft = string.Empty;
                _session.Run(line);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true; // nothing can be typed into a running command
                break;
            case Key.Up:
                Recall(-1);
                e.Handled = true;
                break;
            case Key.Down:
                Recall(+1);
                e.Handled = true;
                break;
            case Key.C when ctrl && _session.IsRunning && Input.SelectionLength == 0:
                _session.Stop();
                e.Handled = true;
                break;
            case Key.L when ctrl:
                _session.Clear();
                e.Handled = true;
                break;
            case Key.Escape:
                ReleaseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void Output_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ReleaseRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _session.IsRunning && OutputBox.SelectionLength == 0)
        {
            _session.Stop();
            e.Handled = true;
        }
    }

    /// <summary>Up / Down: older / newer commands; past the newest is the line that was being typed.</summary>
    private void Recall(int step)
    {
        var history = _session.History;
        if (history.Count == 0 || _session.IsRunning)
        {
            return;
        }

        if (_historyIndex == -1)
        {
            if (step > 0)
            {
                return;
            }

            _draft = Input.Text;
            _historyIndex = history.Count - 1;
        }
        else
        {
            _historyIndex += step;
            if (_historyIndex < 0)
            {
                _historyIndex = 0;
            }
            else if (_historyIndex >= history.Count)
            {
                _historyIndex = -1;
                Input.Text = _draft;
                Input.CaretIndex = Input.Text.Length;
                return;
            }
        }

        Input.Text = history[_historyIndex];
        Input.CaretIndex = Input.Text.Length;
    }

    private void Shell_Click(object sender, RoutedEventArgs e)
    {
        _settings.Current.TerminalShell = ReferenceEquals(sender, CmdOption) ? TerminalShell.CommandPrompt : TerminalShell.PowerShell;
        UpdateState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _session.Clear();

    private void Stop_Click(object sender, RoutedEventArgs e) => _session.Stop();
}
