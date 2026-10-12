using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Activities.Terminal;

/// <summary>
/// The "Terminal" tab: a one-line command prompt in the island (PowerShell or Command Prompt). While a command runs the
/// collapsed island shows it (a terminal glyph and the command); when it ends with the island closed, a short "Done" or
/// "Failed" HUD says so.
/// </summary>
internal sealed class TerminalActivity : PillActivityBase, IReleasesKeyboard, IDisposable
{
    private readonly SettingsService _settings;
    private TerminalView? _view;

    public TerminalActivity(SettingsService settings)
    {
        _settings = settings;
        Session = new TerminalSession(settings);
        Session.StateChanged += RaiseStateChanged;
        Session.Finished += RequestHud; // the host drops it while the island is open (the tab shows the result)
    }

    public TerminalSession Session { get; }

    public override string Id => "terminal";
    public override int Priority => 15;
    public override string? TabTitle => "Terminal";
    public override int TabOrder => -60;
    public override bool HasExpanded => _settings.Current.TerminalEnabled;
    public override bool IsActive => Session.IsRunning;
    public override TimeSpan HudDuration => TimeSpan.FromMilliseconds(2600);

    public event Action? ReleaseRequested;

    public void ApplySettings() => RaiseStateChanged();

    public override FrameworkElement? CreateExpanded()
    {
        _view = new TerminalView(Session, _settings);
        _view.ReleaseRequested += () => ReleaseRequested?.Invoke();
        return _view;
    }

    public void FocusInput() => _view?.FocusInput();

    public override CompactContent? CreateCompact()
    {
        if (Session.IsRunning)
        {
            var glyph = HudParts.Glyph("", "SuccessBrush");
            var command = new TextBlock
            {
                Text = Trim(Session.LastCommand, 18),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            command.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return CompactContent.Of(glyph, command);
        }

        bool ok = Session.LastExitCode == 0;
        var mark = HudParts.Glyph(ok ? "" : "", ok ? "SuccessBrush" : "DangerBrush");
        var label = HudParts.Label(ok ? "Done" : $"Failed ({Session.LastExitCode})");
        return CompactContent.Of(mark, label);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose() => Session.Dispose();
}
