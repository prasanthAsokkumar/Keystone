using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>The Tasks page: things to do, with due dates, for anyone in the household.</summary>
public partial class MainViewModel
{
    /// <summary>Tasks due within this many days show on the Overview.</summary>
    private const int TaskReminderDays = 7;

    private readonly List<TaskItem> _tasks = [];

    public IReadOnlyList<string> TaskStatusFilters => Translate("To do", "Done", "All tasks");

    [ObservableProperty]
    public partial string TaskSearch { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption TaskPersonFilter { get; set; } = PersonOption.Everyone;

    [ObservableProperty]
    public partial int TaskStatusFilterIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskRows), nameof(TasksEmptyText))]
    public partial IReadOnlyList<TaskRow> TaskRows { get; set; } = [];

    public bool HasTaskRows => TaskRows.Count > 0;

    public string TasksEmptyText => _tasks.Count == 0
        ? Loc.T("No tasks yet. Type one above and press Enter.")
        : TaskStatusFilterIndex == 0 && TaskSearch == "" && TaskPersonFilter is { IsEveryone: true }
            ? Loc.T("All done. Nothing left to do.")
            : Loc.T("No tasks match these filters.");

    [ObservableProperty]
    public partial string TaskCountText { get; set; } = "";

    /// <summary>Shown on the Overview, e.g. "2 tasks due this week · 1 overdue".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTasksDue))]
    public partial string TasksDueText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasOverdueTasks { get; set; }

    public bool HasTasksDue => TasksDueText != "";

    partial void OnTaskSearchChanged(string value) => RefreshTaskRows();

    partial void OnTaskPersonFilterChanged(PersonOption value) => RefreshTaskRows();

    partial void OnTaskStatusFilterIndexChanged(int value) => RefreshTaskRows();

    [RelayCommand]
    private void ShowTasksDue()
    {
        TaskSearch = "";
        TaskPersonFilter = PersonOption.Everyone;
        TaskStatusFilterIndex = 0;
        CurrentPage = AppPage.Tasks;
    }

    private async Task LoadTasksAsync()
    {
        _tasks.Clear();
        _tasks.AddRange(await repository.GetTasksAsync());
        RefreshTaskRows();
    }

    private void RefreshTaskRows()
    {
        var names = People.ToDictionary(p => p.Id, p => p.Name);
        var search = TaskSearch.Trim();
        var person = TaskPersonFilter ?? PersonOption.Everyone;

        TaskRows = _tasks
            .Where(t => person.IsEveryone || t.PersonId == person.Id)
            .Where(t => TaskStatusFilterIndex switch
            {
                0 => !t.IsDone,
                1 => t.IsDone,
                _ => true,
            })
            .Where(t => search == "" ||
                        t.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        t.Notes.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            // Open tasks first, soonest due first and undated last; then what was done most recently.
            .OrderBy(t => t.IsDone)
            .ThenBy(t => t.IsDone ? DateOnly.MaxValue : t.DueDate ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.CompletedOn ?? DateOnly.MinValue)
            .ThenBy(t => t.CreatedAt)
            .Select(t => new TaskRow(
                t, t.PersonId is { } id && names.TryGetValue(id, out var name) ? name : PersonOption.Shared.ToString(), Today))
            .ToList();

        var open = _tasks.Count(t => !t.IsDone);
        TaskCountText = Loc.F("{0} to do", open);

        var due = _tasks.Where(t => t.IsDueWithin(Today, TaskReminderDays)).ToList();
        var overdue = due.Count(t => t.IsOverdue(Today));
        HasOverdueTasks = overdue > 0;
        TasksDueText = due.Count == 0
            ? ""
            : Loc.Plural(due.Count, "{0} task due this week", "{0} tasks due this week") +
              (overdue > 0 ? Loc.F(" · {0} overdue", overdue) : "");
    }

    // ── Quick add ───────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string NewTaskTitle { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? NewTaskDueDate { get; set; }

    [ObservableProperty]
    public partial PersonOption NewTaskPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewTaskError))]
    public partial string? NewTaskError { get; set; }

    public bool HasNewTaskError => !string.IsNullOrEmpty(NewTaskError);

    [RelayCommand]
    private async Task AddTaskAsync()
    {
        NewTaskError = null;
        var title = NewTaskTitle.Trim();
        if (title == "")
        {
            NewTaskError = Loc.T("Enter what needs to be done.");
            return;
        }

        var task = new TaskItem
        {
            Title = title,
            DueDate = NewTaskDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            PersonId = NewTaskPerson.Id,
            CreatedAt = DateTime.Now,
        };
        try
        {
            await repository.AddTaskAsync(task);
        }
        catch (Exception ex)
        {
            NewTaskError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        _tasks.Add(task);
        // Keep the person for the next task; most tasks are added in a row for the same person.
        NewTaskTitle = "";
        NewTaskDueDate = null;
        if (TaskStatusFilterIndex == 1)
            TaskStatusFilterIndex = 0;
        RefreshTaskRows();
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(TaskItem task)
    {
        var completedOn = task.CompletedOn;
        task.CompletedOn = task.IsDone ? null : Today;
        try
        {
            await repository.UpdateTaskAsync(task);
        }
        catch (Exception ex)
        {
            task.CompletedOn = completedOn;
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }

        RefreshTaskRows();
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskItem task)
    {
        try
        {
            await repository.DeleteTaskAsync(task.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        _tasks.Remove(task);
        if (EditingTask == task)
            CloseTaskPanel();
        RefreshTaskRows();
    }

    // ── Task panel ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsTaskPanelOpen { get; set; }

    [ObservableProperty]
    public partial TaskItem? EditingTask { get; set; }

    [ObservableProperty]
    public partial string TaskTitle { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? TaskDueDate { get; set; }

    [ObservableProperty]
    public partial PersonOption TaskPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string TaskNotes { get; set; } = "";

    [ObservableProperty]
    public partial bool TaskIsDone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskError))]
    public partial string? TaskError { get; set; }

    public bool HasTaskError => !string.IsNullOrEmpty(TaskError);

    [RelayCommand]
    private void EditTask(TaskItem task)
    {
        CloseOtherPanels();
        EditingTask = task;
        TaskTitle = task.Title;
        TaskDueDate = task.DueDate?.ToDateTime(TimeOnly.MinValue);
        TaskPerson = PersonOptions.FirstOrDefault(o => o.Id == task.PersonId) ?? PersonOption.Shared;
        TaskNotes = task.Notes;
        TaskIsDone = task.IsDone;
        TaskError = null;
        IsTaskPanelOpen = true;
    }

    [RelayCommand]
    private void ClearTaskDueDate() => TaskDueDate = null;

    [RelayCommand]
    private async Task SaveTaskAsync()
    {
        if (EditingTask is not { } task)
            return;

        TaskError = null;
        if (string.IsNullOrWhiteSpace(TaskTitle))
        {
            TaskError = Loc.T("Enter what needs to be done.");
            return;
        }

        var updated = new TaskItem
        {
            Id = task.Id,
            Title = TaskTitle.Trim(),
            Notes = TaskNotes.Trim(),
            DueDate = TaskDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            PersonId = TaskPerson.Id,
            CompletedOn = TaskIsDone ? task.CompletedOn ?? Today : null,
            CreatedAt = task.CreatedAt,
        };
        try
        {
            await repository.UpdateTaskAsync(updated);
        }
        catch (Exception ex)
        {
            TaskError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        _tasks[_tasks.IndexOf(task)] = updated;
        CloseTaskPanel();
        RefreshTaskRows();
    }

    private void CloseTaskPanel()
    {
        IsTaskPanelOpen = false;
        EditingTask = null;
        TaskError = null;
    }
}

/// <summary>One row of the Tasks list.</summary>
public sealed record TaskRow(TaskItem Task, string PersonName, DateOnly Today)
{
    public string Title => Task.Title;

    public bool IsDone => Task.IsDone;

    public bool HasNotes => Task.Notes != "";

    /// <summary>The notes on one line.</summary>
    public string NotesPreview => string.Join(" ", Task.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public bool IsShared => Task.PersonId is null;

    public bool IsOverdue => Task.IsOverdue(Today);

    /// <summary>Due today or tomorrow.</summary>
    public bool IsDueSoon => !IsDone && !IsOverdue && Task.DueDate <= Today.AddDays(1);

    public string DueText => Task switch
    {
        { CompletedOn: { } done } => Loc.F("Done {0}", Format.Date(done)),
        { DueDate: null } => "",
        { DueDate: { } due } when due < Today => Loc.F("Overdue since {0}", Format.Date(due)),
        { DueDate: { } due } when due == Today => Loc.T("Due today"),
        { DueDate: { } due } when due == Today.AddDays(1) => Loc.T("Due tomorrow"),
        { DueDate: { } due } => Loc.F("Due {0}", Format.Date(due)),
    };

    public bool HasDueText => DueText != "";
}
