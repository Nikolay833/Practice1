using System;
using System.Windows;
using Dropshelf.Services;

namespace Dropshelf.Activities.Notes;

/// <summary>An expanded-island tab that takes content dropped onto it (the Notes tab takes text).</summary>
internal interface IIslandDropTarget
{
    bool CanDrop(IDataObject data);

    bool Drop(IDataObject data);
}

/// <summary>
/// Quick notes: the "Notes" tab with a note list on the left and a plain-text editor. Nothing on the pill. Dropping text onto
/// the tab appends it to the current note; Ctrl+Shift+N opens the island on this tab with the editor focused.
/// </summary>
internal sealed class NotesActivity : PillActivityBase, IIslandDropTarget, IDisposable
{
    private NotesView? _view;

    public NotesActivity()
    {
        Store = new NoteStore();
    }

    public NoteStore Store { get; }

    public override string Id => "notes";
    public override int Priority => 20;
    public override string? TabTitle => "Notes";
    public override int TabOrder => -70;
    public override bool HasExpanded => true;

    /// <summary>Raised when the editor wants the island to let go of the keyboard (Esc).</summary>
    public event Action? ReleaseRequested;

    public override FrameworkElement? CreateExpanded()
    {
        _view = new NotesView(Store);
        _view.ReleaseRequested += () => ReleaseRequested?.Invoke();
        return _view;
    }

    /// <summary>Puts the caret in the editor (the tab must be showing).</summary>
    public void FocusEditor() => _view?.FocusEditor();

    public bool CanDrop(IDataObject data) =>
        !data.GetDataPresent(DataFormats.FileDrop)
        && (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text));

    public bool Drop(IDataObject data)
    {
        try
        {
            string? text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Store.Append(text);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error("Dropping text onto the notes failed", ex);
            return false;
        }
    }

    public void Dispose() => Store.Flush();
}
