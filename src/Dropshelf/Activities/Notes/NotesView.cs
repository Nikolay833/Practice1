using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dropshelf.Activities.Notes;

/// <summary>
/// The "Notes" tab, drawn like the terminal next to it: no boxes, lines or backgrounds, just light grey monospaced text on the
/// transparent island. Note titles down the left (the current one bright, the rest dim), the note itself on the right. Every
/// keystroke goes to the note and the store writes the file 500 ms after the last one. Ctrl+N starts a note, Esc lets go of
/// the keyboard so the island can close.
/// </summary>
internal sealed class NotesView : Grid, IWantsKeyboard // a click anywhere on it (a title, "+ new note") lets the island take the keyboard
{
    private const double TextSize = 13;
    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    private readonly NoteStore _store;
    private readonly StackPanel _list = new();
    private readonly TextBox _editor;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _edited;
    private readonly TextBlock _delete;
    private readonly DispatcherTimer _deleteTimer;
    private bool _loading;
    private bool _confirmDelete;

    public NotesView(NoteStore store)
    {
        _store = store;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Left: "+ new note" and the titles ----
        var left = new DockPanel { Margin = new Thickness(0, 0, 22, 0) };
        TextBlock add = Link("+ new note", "New note (Ctrl+N)", () =>
        {
            _store.Add();
            FocusEditor();
        });
        add.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(add, Dock.Top);
        left.Children.Add(add);
        left.Children.Add(new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        Children.Add(left);

        // ---- Right: date and delete, then the text ----
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetColumn(right, 1);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _delete = Link("delete", "Delete this note", OnDelete);
        DockPanel.SetDock(_delete, Dock.Right);
        _edited = Text(dim: true);
        header.Children.Add(_delete);
        header.Children.Add(_edited);
        right.Children.Add(header);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = Mono,
            FontSize = TextSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            SelectionOpacity = 0.3,
            FocusVisualStyle = null,
            Template = BareTextBox(),
        };
        _editor.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _editor.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
        _editor.TextChanged += OnTextChanged;
        _editor.PreviewKeyDown += OnEditorKeyDown;
        SetRow(_editor, 1);
        right.Children.Add(_editor);

        _placeholder = Text(dim: true);
        _placeholder.Text = "type a note…";
        _placeholder.IsHitTestVisible = false;
        SetRow(_placeholder, 1);
        right.Children.Add(_placeholder);
        Children.Add(right);

        _deleteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _deleteTimer.Tick += (_, _) => DisarmDelete();

        _store.Notes.CollectionChanged += OnNotesChanged;
        foreach (Note note in _store.Notes)
        {
            note.PropertyChanged += OnNotePropertyChanged;
        }

        _store.CurrentChanged += OnCurrentChanged;
        Unloaded += (_, _) => _deleteTimer.Stop();
        OnCurrentChanged();
    }

    /// <summary>Puts the caret at the end of the current note (the tab must be showing).</summary>
    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _editor.Focus();
            Keyboard.Focus(_editor);
            _editor.CaretIndex = _editor.Text.Length;
        }));
    }

    private void OnCurrentChanged()
    {
        _loading = true;
        try
        {
            if (_editor.Text != _store.Current.Text)
            {
                _editor.Text = _store.Current.Text;
            }

            UpdateInfo();
            RebuildList();
            DisarmDelete();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _store.Current.Text = _editor.Text;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        _edited.Text = _store.Current.IsEmpty ? string.Empty : _store.Current.Edited;
        _placeholder.Visibility = _editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNotesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (Note note in e.OldItems ?? Array.Empty<Note>())
        {
            note.PropertyChanged -= OnNotePropertyChanged;
        }

        foreach (Note note in e.NewItems ?? Array.Empty<Note>())
        {
            note.PropertyChanged += OnNotePropertyChanged;
        }

        RebuildList();
    }

    private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Note.Title))
        {
            RebuildList();
        }
    }

    /// <summary>One line per note: its title, bright for the open one and dim for the rest.</summary>
    private void RebuildList()
    {
        _list.Children.Clear();
        foreach (Note note in _store.Notes)
        {
            Note target = note;
            bool current = ReferenceEquals(note, _store.Current);
            TextBlock row = Text(dim: !current);
            row.Text = note.Title;
            row.Margin = new Thickness(0, 0, 0, 6);
            row.Cursor = Cursors.Hand;
            row.Background = Brushes.Transparent; // the whole line takes the click, not just the letters
            row.MouseEnter += (_, _) => row.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.MouseLeave += (_, _) => row.SetResourceReference(TextBlock.ForegroundProperty, current ? "TextBrush" : "TextSecondaryBrush");
            row.MouseLeftButtonUp += (_, e) =>
            {
                _store.Current = target;
                FocusEditor();
                e.Handled = true;
            };
            _list.Children.Add(row);
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus(); // the island closes once the mouse leaves
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _store.Add();
            FocusEditor();
            e.Handled = true;
        }
    }

    private void OnDelete()
    {
        Note note = _store.Current;
        if (!note.IsEmpty && !_confirmDelete)
        {
            // A note with text needs a second click within a couple of seconds.
            _confirmDelete = true;
            _delete.Text = "click again to delete";
            _delete.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
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
        _delete.Text = "delete";
        _delete.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
    }

    private static TextBlock Text(bool dim)
    {
        var text = new TextBlock { FontFamily = Mono, FontSize = TextSize, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextSecondaryBrush" : "TextBrush");
        return text;
    }

    /// <summary>Dim text that brightens under the mouse and runs <paramref name="click"/>.</summary>
    private static TextBlock Link(string label, string tooltip, Action click)
    {
        TextBlock link = Text(dim: true);
        link.Text = label;
        link.ToolTip = tooltip;
        link.Cursor = Cursors.Hand;
        link.Background = Brushes.Transparent;
        link.MouseEnter += (_, _) =>
        {
            if (link.Text != "click again to delete")
            {
                link.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }
        };
        link.MouseLeave += (_, _) =>
        {
            if (link.Text != "click again to delete")
            {
                link.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
        };
        link.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return link;
    }

    /// <summary>A text box with nothing around the text: no border, background, focus ring or hover highlight.</summary>
    private static ControlTemplate BareTextBox()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
    }
}
