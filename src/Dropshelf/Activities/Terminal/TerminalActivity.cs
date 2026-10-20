using System;
using System.Windows;
using System.Windows.Controls;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// The island's first tab: a real PowerShell or Command Prompt (Settings) in a pseudo console, drawn straight on the
/// transparent island. Anything that runs in a console runs here, including interactive tools such as claude. The shell
/// starts the first time the island opens and keeps running while it is closed.
/// </summary>
internal sealed class TerminalActivity : PillActivityBase, IFocusableTab, IDisposable
{
    private readonly SettingsService _settings;
    private TerminalScreen? _screen;

    public TerminalActivity(SettingsService settings)
    {
        _settings = settings;
        Session = new TerminalSession(settings);
    }

    public TerminalSession Session { get; }

    public override string Id => "terminal";
    public override int Priority => 15;
    public override string? TabTitle => "Terminal";
    public override int TabOrder => -60;
    public override bool HasExpanded => true;

    /// <summary>The shell setting changed: a running shell is replaced by the chosen one.</summary>
    public void ApplySettings() => Session.ShellChanged();

    public override FrameworkElement? CreateExpanded()
    {
        _screen = new TerminalScreen(Session);
        return new Grid { Children = { _screen } };
    }

    /// <summary>Puts the keyboard in the terminal (the island must already be allowed to take it).</summary>
    public void FocusInput()
    {
        if (_screen != null)
        {
            _screen.Focus();
            System.Windows.Input.Keyboard.Focus(_screen);
        }
    }

    public void Dispose() => Session.Dispose();
}
