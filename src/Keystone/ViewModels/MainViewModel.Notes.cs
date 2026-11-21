using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>The Notes page: a list of notes and an editor that saves as you type.</summary>
public partial class MainViewModel
{
    private const int NoteSaveDelayMs = 600;

    private readonly List<Note> _notes = [];
    private CancellationTokenSource? _noteSaveDelay;
    private Note? _unsavedNote;

    /// <summary>Set while the editor is filled from a note, so that doesn't count as an edit.</summary>
    private bool _isLoadingNote;

    /// <summary>Set while the list is rebuilt, so the selection briefly clearing isn't "leaving" a note.</summary>
    private bool _isRebuildingNotes;

    public ObservableCollection<NoteItem> NoteItems { get; } = [];

    [ObservableProperty]
    public partial string NoteSearch { get; set; } = "";

    /// <summary>The project to show; <see cref="AllProjects"/> shows every note.</summary>
    [ObservableProperty]
    public partial string NoteProjectFilter { get; set; } = "";

    public string AllProjects => Loc.T("All projects");

    [ObservableProperty]
    public partial IReadOnlyList<string> NoteProjectFilters { get; set; } = [];

    /// <summary>Existing project names, suggested while typing a note's project.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> NoteProjects { get; set; } = [];

    public bool HasNoteItems => NoteItems.Count > 0;

    public string NotesEmptyText => _notes.Count == 0
        ? Loc.T("No notes yet. Click New note to write down an idea.")
        : Loc.T("No notes match these filters.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingNote))]
    public partial NoteItem? SelectedNoteItem { get; set; }

    public bool IsEditingNote => SelectedNoteItem is not null;

    [ObservableProperty]
    public partial string NoteTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NoteBody { get; set; } = "";

    [ObservableProperty]
    public partial string NoteProject { get; set; } = "";

    [ObservableProperty]
    public partial bool NoteIsPinned { get; set; }

    /// <summary>"Saved", or why saving failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoteSaveError))]
    public partial string NoteSaveStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool HasNoteSaveError { get; set; }

    /// <summary>An edit is waiting to be written to the database.</summary>
    public bool HasUnsavedNote => _unsavedNote is not null;

    partial void OnNoteSearchChanged(string value) => RefreshNoteItems();

    partial void OnNoteProjectFilterChanged(string value) => RefreshNoteItems();

    partial void OnSelectedNoteItemChanged(NoteItem? oldValue, NoteItem? newValue)
    {
        if (_isRebuildingNotes)
            return;

        if (oldValue is not null && oldValue != newValue)
            _ = LeaveNoteAsync(oldValue.Note);

        _isLoadingNote = true;
        NoteTitle = newValue?.Note.Title ?? "";
        NoteBody = newValue?.Note.Body ?? "";
        NoteProject = newValue?.Note.Project ?? "";
        NoteIsPinned = newValue?.Note.IsPinned ?? false;
        NoteSaveStatus = "";
        HasNoteSaveError = false;
        _isLoadingNote = false;
    }

    partial void OnNoteTitleChanged(string value) => EditNote(n => n.Title = value);

    partial void OnNoteBodyChanged(string value) => EditNote(n => n.Body = value);

    partial void OnNoteProjectChanged(string value) => EditNote(n => n.Project = value.Trim());

    partial void OnNoteIsPinnedChanged(bool value)
    {
        if (_isLoadingNote || SelectedNoteItem is null)
            return;
        EditNote(n => n.IsPinned = value);
        RefreshNoteItems(); // pinned notes move to the top
    }

    private void EditNote(Action<Note> change)
    {
        if (_isLoadingNote || SelectedNoteItem is not { } item)
            return;

        change(item.Note);
        item.Note.UpdatedAt = DateTime.Now;
        item.Refresh();
        ScheduleNoteSave(item.Note);
    }

    private void ScheduleNoteSave(Note note)
    {
        if (_unsavedNote is { } other && other != note)
            _ = SaveNoteNowAsync();

        _unsavedNote = note;
        _noteSaveDelay?.Cancel();
        var delay = _noteSaveDelay = new CancellationTokenSource();
        _ = SaveNoteLaterAsync(delay.Token);
    }

    private async Task SaveNoteLaterAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(NoteSaveDelayMs, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveNoteNowAsync();
    }

    /// <summary>Writes any waiting edit right away, e.g. before the window closes.</summary>
    public async Task SaveNoteNowAsync()
    {
        _noteSaveDelay?.Cancel();
        _noteSaveDelay = null;
        if (_unsavedNote is not { } note)
            return;
        _unsavedNote = null;

        try
        {
            await repository.UpdateNoteAsync(note);
            if (SelectedNoteItem?.Note == note)
            {
                NoteSaveStatus = Loc.T("Saved");
                HasNoteSaveError = false;
            }
        }
        catch (Exception ex)
        {
            NoteSaveStatus = Loc.F("Could not save: {0}", ex.Message);
            HasNoteSaveError = true;
        }

        RefreshNoteProjects();
    }

    /// <summary>Saves the note being left, or removes it if nothing was written in it.</summary>
    private async Task LeaveNoteAsync(Note note)
    {
        if (note.Title.Trim() != "" || note.Body.Trim() != "")
        {
            if (_unsavedNote == note)
                await SaveNoteNowAsync();
            return;
        }

        if (_unsavedNote == note)
        {
            _noteSaveDelay?.Cancel();
            _unsavedNote = null;
        }
        try
        {
            await repository.DeleteNoteAsync(note.Id);
        }
        catch
        {
            return; // an empty note left behind does no harm
        }
        _notes.Remove(note);
        RefreshNoteItems();
    }

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        CurrentPage = AppPage.Notes;
        if (SelectedNoteItem is { } current && current.Note.Title.Trim() == "" && current.Note.Body.Trim() == "")
            return; // already on an empty note

        var now = DateTime.Now;
        var note = new Note
        {
            CreatedAt = now,
            UpdatedAt = now,
            // A new note joins the project being looked at.
            Project = NoteProjectFilter != AllProjects ? NoteProjectFilter : "",
        };
        try
        {
            await repository.AddNoteAsync(note);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        _notes.Add(note);
        NoteSearch = "";
        RefreshNoteItems();
        SelectedNoteItem = NoteItems.FirstOrDefault(i => i.Note == note);
    }

    [RelayCommand]
    private void ToggleNotePin() => NoteIsPinned = !NoteIsPinned;

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNoteItem is not { } item)
            return;

        if (_unsavedNote == item.Note)
        {
            _noteSaveDelay?.Cancel();
            _unsavedNote = null;
        }
        try
        {
            await repository.DeleteNoteAsync(item.Note.Id);
        }
        catch (Exception ex)
        {
            NoteSaveStatus = Loc.F("Could not delete: {0}", ex.Message);
            HasNoteSaveError = true;
            return;
        }

        var index = NoteItems.IndexOf(item);
        _notes.Remove(item.Note);
        _isRebuildingNotes = true;
        SelectedNoteItem = null;
        _isRebuildingNotes = false;
        OnSelectedNoteItemChanged(null, null);
        RefreshNoteItems();
        RefreshNoteProjects();
        if (NoteItems.Count > 0)
            SelectedNoteItem = NoteItems[Math.Clamp(index, 0, NoteItems.Count - 1)];
    }

    private async Task LoadNotesAsync()
    {
        _notes.Clear();
        _notes.AddRange(await repository.GetNotesAsync());
        RefreshNoteProjects();
        RefreshNoteItems();
    }

    private void RefreshNoteProjects()
    {
        var projects = _notes.Select(n => n.Project.Trim()).Where(p => p != "")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var filter = NoteProjectFilter;
        NoteProjects = projects;
        NoteProjectFilters = [AllProjects, .. projects];
        NoteProjectFilter = NoteProjectFilters.Contains(filter) ? filter : AllProjects;
    }

    /// <summary>Rebuilds the list from the filters, keeping the selected note selected.</summary>
    private void RefreshNoteItems()
    {
        var search = NoteSearch.Trim();
        var project = NoteProjectFilter;
        var selected = SelectedNoteItem?.Note;

        var notes = _notes
            .Where(n => project == AllProjects || project == "" ||
                        n.Project.Equals(project, StringComparison.CurrentCultureIgnoreCase))
            .Where(n => search == "" ||
                        n.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        n.Body.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        n.Project.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.Id)
            .ToList();

        // The note being edited stays in the list even if it no longer matches, so it doesn't vanish mid-sentence.
        if (selected is not null && !notes.Contains(selected) && _notes.Contains(selected))
            notes.Insert(0, selected);

        _isRebuildingNotes = true;
        try
        {
            var existing = NoteItems.ToDictionary(i => i.Note);
            NoteItems.Clear();
            foreach (var note in notes)
                NoteItems.Add(existing.TryGetValue(note, out var item) ? item : new NoteItem(note));
            SelectedNoteItem = NoteItems.FirstOrDefault(i => i.Note == selected);
        }
        finally
        {
            _isRebuildingNotes = false;
        }

        if (selected is not null && SelectedNoteItem is null)
            OnSelectedNoteItemChanged(null, null);
        foreach (var item in NoteItems)
            item.Refresh();
        OnPropertyChanged(nameof(HasNoteItems));
        OnPropertyChanged(nameof(NotesEmptyText));
    }
}

/// <summary>One note in the list; updates as the note is edited.</summary>
public sealed class NoteItem(Note note) : ObservableObject
{
    private const int PreviewLength = 140;

    public Note Note { get; } = note;

    public string Title => Note.Title.Trim() != "" ? Note.Title.Trim() : Loc.T("Untitled note");

    public bool IsUntitled => Note.Title.Trim() == "";

    /// <summary>The start of the text on one line.</summary>
    public string Preview
    {
        get
        {
            var text = string.Join(" · ", Note.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return text.Length > PreviewLength ? text[..PreviewLength] + "…" : text;
        }
    }

    public bool HasPreview => Preview != "";

    public string Project => Note.Project;

    public bool HasProject => Note.Project != "";

    public bool IsPinned => Note.IsPinned;

    /// <summary>The time for today's edits, otherwise the date.</summary>
    public string UpdatedText => Note.UpdatedAt.Date == DateTime.Today
        ? Note.UpdatedAt.ToString("t", System.Globalization.CultureInfo.CurrentCulture)
        : Format.Date(DateOnly.FromDateTime(Note.UpdatedAt));

    public void Refresh() => OnPropertyChanged(string.Empty);
}
