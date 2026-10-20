using System;
using System.Windows;

namespace Dropshelf.Activities.Notes;

/// <summary>
/// Quick notes: the island's second tab, next to the terminal and drawn the same way (light grey monospaced text, nothing
/// else). Nothing on the closed island. Saved encrypted to notes.json.
/// </summary>
internal sealed class NotesActivity : PillActivityBase, IFocusableTab, IDisposable
{
    private NotesView? _view;

    public NoteStore Store { get; } = new();

    public override string Id => "notes";
    public override int Priority => 14;
    public override string? TabTitle => "Notes";
    public override int TabOrder => -50; // after the terminal
    public override bool HasExpanded => true;

    public override FrameworkElement? CreateExpanded() => _view = new NotesView(Store);

    public void FocusInput() => _view?.FocusEditor();

    public void Dispose() => Store.Flush();
}
