using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Dropshelf.Activities.Notes;

/// <summary>
/// The "Notes" tab, styled like Apple Notes: a note list (bold title, edit time and second line in grey) and a borderless
/// editor with the edit date above it and a yellow caret. The editor is a plain TextBox; every change goes to the note at once and the store writes the file 500 ms
/// after the last keystroke. The island window lets itself be activated when the editor is clicked (it is a no-activate window
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

    /// <summary>Esc in the editor: give the keyboard back and let the island close.</summary>
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

            UpdateEditorInfo();

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
        UpdateEditorInfo();
    }

    /// <summary>The date line above the editor and the "Start typing..." placeholder.</summary>
    private void UpdateEditorInfo()
    {
        EditedText.Text = _store.Current.IsEmpty ? string.Empty : _store.Current.Edited;
        Placeholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            DeleteButton.SetResourceReference(ForegroundProperty, "DangerBrush");
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
        DeleteButton.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
    }
}
