using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Notes;

/// <summary>One quick note. The first non-empty line is its title.</summary>
public sealed class Note : NotifyBase
{
    private string _text = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    [JsonIgnore]
    public string Title
    {
        get
        {
            foreach (string line in _text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length <= 42 ? trimmed : trimmed[..41] + "…";
                }
            }

            return "New note";
        }
    }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(_text);
}

/// <summary>
/// The notes, saved to <c>notes.json</c> encrypted with DPAPI (like the clipboard history) 500 ms after the last edit. Newest
/// first. There is always at least one note.
/// </summary>
internal sealed class NoteStore
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _saveTimer;
    private Note? _current;

    public NoteStore()
    {
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        List<Note>? saved = JsonFile.LoadProtected<List<Note>>(AppPaths.NotesFile, out bool wasPlain);
        if (saved != null)
        {
            foreach (Note note in saved.OrderByDescending(n => n.CreatedUtc))
            {
                Attach(note);
                Notes.Add(note);
            }

            if (wasPlain)
            {
                ScheduleSave();
            }
        }

        if (Notes.Count == 0)
        {
            Add();
        }

        _current = Notes[0];
    }

    public ObservableCollection<Note> Notes { get; } = new();

    public Note Current
    {
        get => _current ?? Notes[0];
        set
        {
            if (!ReferenceEquals(_current, value) && Notes.Contains(value))
            {
                _current = value;
                CurrentChanged?.Invoke();
            }
        }
    }

    public event Action? CurrentChanged;

    /// <summary>Starts a new note and makes it current. An empty note that is already current is reused.</summary>
    public Note Add()
    {
        if (_current is { IsEmpty: true } && Notes.Contains(_current))
        {
            return _current;
        }

        var note = new Note();
        Attach(note);
        Notes.Insert(0, note);
        _current = note;
        CurrentChanged?.Invoke();
        ScheduleSave();
        return note;
    }

    public void Remove(Note note)
    {
        int index = Notes.IndexOf(note);
        if (index < 0)
        {
            return;
        }

        Notes.RemoveAt(index);
        if (Notes.Count == 0)
        {
            Add();
        }
        else if (ReferenceEquals(_current, note) || _current == null)
        {
            _current = Notes[Math.Min(index, Notes.Count - 1)];
            CurrentChanged?.Invoke();
        }

        ScheduleSave();
    }

    /// <summary>Appends dropped text to the current note (on its own line unless the note is empty).</summary>
    public void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Note note = Current;
        note.Text = note.IsEmpty ? text.Trim() : note.Text.TrimEnd() + "\n" + text.Trim();
        CurrentChanged?.Invoke(); // the editor reloads the text
    }

    private void Attach(Note note) => note.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(Note.Text))
        {
            note.UpdatedUtc = DateTime.UtcNow;
            ScheduleSave();
        }
    };

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Save() => JsonFile.SaveProtected(AppPaths.NotesFile, Notes.ToList());

    /// <summary>Writes a pending change now (on exit).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }
}
