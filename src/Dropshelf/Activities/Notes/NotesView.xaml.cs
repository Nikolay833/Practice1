using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Dropshelf.Activities.Notes;

/// <summary>
/// The "Notes" tab. The editor is a plain TextBox; every change goes to the note at once and the store writes the file 500 ms
/// after the last keystroke. The shelf window lets itself be activated when the editor is clicked (it is a no-activate window
/// otherwise) and stays open while the editor has the keyboard.
/// </summary>
public partial class NotesView : UserControl
{
    private readonly NoteStore _store;
    private readonly DispatcherTimer _deleteTimer;
    private bool _loading;
    private bool _confirmDelete;

    internal NotesView(NoteStore store)
    {
        InitializeComponent();
        _store = store;
        NoteList.ItemsSource = store.Notes;

        _deleteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _deleteTimer.Tick += (_, _) => DisarmDelete();

        _store.CurrentChanged += OnCurrentChanged;
        Unloaded += (_, _) => _deleteTimer.Stop();
        OnCurrentChanged();
    }

    /// <summary>Esc in the editor: give the keyboard back and let the shelf close.</summary>
    internal event Action? ReleaseRequested;

    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Editor.Focus();
            Keyboard.Focus(Editor);
            Editor.CaretIndex = Editor.Text.Length;
        }));
    }

    private void OnCurrentChanged()
    {
        _loading = true;
        try
        {
            NoteList.SelectedItem = _store.Current;
            if (Editor.Text != _store.Current.Text)
            {
                Editor.Text = _store.Current.Text;
            }

            DisarmDelete();
        }
        finally
        {
            _loading = false;
        }
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NoteList.SelectedItem is not Note note)
        {
            return;
        }

        _store.Current = note;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _store.Current.Text = Editor.Text;
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ReleaseRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            New_Click(sender, e);
            e.Handled = true;
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _store.Add();
        FocusEditor();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Note note = _store.Current;
        if (!note.IsEmpty && !_confirmDelete)
        {
            // A note with text needs a second click within a couple of seconds.
            _confirmDelete = true;
            DeleteHint.Text = "Click again to delete";
            _deleteTimer.Stop();
            _deleteTimer.Start();
            return;
        }

        DisarmDelete();
        _store.Remove(note);
    }

    private void DisarmDelete()
    {
        _deleteTimer.Stop();
        _confirmDelete = false;
        DeleteHint.Text = string.Empty;
    }
}
