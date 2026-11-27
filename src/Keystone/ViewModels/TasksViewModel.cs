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
/// <remarks>Its own view model; shared things (people, notices, reminders) come from the main one.</remarks>
public sealed partial class TasksViewModel(MainViewModel main) : ViewModelBase
{
    /// <summary>Tasks due within this many days show on the Overview.</summary>
    private const int TaskReminderDays = 7;

    /// <summary>Every task, done ones too.</summary>
    internal List<TaskItem> All { get; } = [];

    public IReadOnlyList<string> TaskStatusFilters => MainViewModel.Translate("To do", "Done", "All tasks");

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

    public string TasksEmptyText => All.Count == 0
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
    internal void ShowTasksDue()
    {
        TaskSearch = "";
        TaskPersonFilter = PersonOption.Everyone;
        TaskStatusFilterIndex = 0;
        main.CurrentPage = AppPage.Tasks;
    }

    internal async Task LoadTasksAsync()
    {
        All.Clear();
        All.AddRange(await main.repository.GetTasksAsync());
        RefreshTaskRows();
    }

    internal void RefreshTaskRows()
    {
        var names = main.People.ToDictionary(p => p.Id, p => p.Name);
        var search = TaskSearch.Trim();
        var person = TaskPersonFilter ?? PersonOption.Everyone;

        TaskRows = main.Sorted("tasks", All
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
                t, t.PersonId is { } id && names.TryGetValue(id, out var name) ? name : PersonOption.Shared.ToString(), MainViewModel.Today)),
            r => r.Task.SortOrder,
            new SortKey<TaskRow>("title", r => r.Task.Title),
            new SortKey<TaskRow>("person", r => r.PersonName),
            new SortKey<TaskRow>("due", r => r.Task.DueDate));

        main.ScheduleReminders();
        var open = All.Count(t => !t.IsDone);
        TaskCountText = Loc.F("{0} to do", open);

        var due = All.Where(t => t.IsDueWithin(MainViewModel.Today, TaskReminderDays)).ToList();
        var overdue = due.Count(t => t.IsOverdue(MainViewModel.Today));
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
            await main.repository.AddTaskAsync(task);
        }
        catch (Exception ex)
        {
            NewTaskError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        All.Add(task);
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
        task.CompletedOn = task.IsDone ? null : MainViewModel.Today;
        try
        {
            await main.repository.UpdateTaskAsync(task);
            if (task.IsDone)
                await AddNextRepeatAsync(task);
        }
        catch (Exception ex)
        {
            task.CompletedOn = completedOn;
            main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }

        RefreshTaskRows();
    }

    /// <summary>
    /// A repeating task that is done comes back as a new task, due on its next date. A task done late doesn't
    /// pile up: the next date is the first one from today on.
    /// </summary>
    private async Task AddNextRepeatAsync(TaskItem done)
    {
        if (!done.Repeats)
            return;
        var due = done.DueDate ?? MainViewModel.Today;
        var next = TaskItem.NextDue(due, done.Repeat);
        while (next < MainViewModel.Today)
            next = TaskItem.NextDue(next, done.Repeat);

        // Don't add it twice, e.g. when a task is ticked, unticked and ticked again.
        if (All.Any(t => !t.IsDone && t.Title == done.Title && t.DueDate == next))
            return;

        var task = new TaskItem
        {
            Title = done.Title, Notes = done.Notes, PersonId = done.PersonId, Repeat = done.Repeat,
            DueDate = next, CreatedAt = DateTime.Now,
        };
        await main.repository.AddTaskAsync(task);
        All.Add(task);
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskItem task)
    {
        try
        {
            await main.repository.DeleteTaskAsync(task.Id);
        }
        catch (Exception ex)
        {
            main.ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        All.Remove(task);
        if (EditingTask == task)
            CloseTaskPanel();
        RefreshTaskRows();
    }

    // ── Task panel ──────────────────────────────────────────────────────────

    [ObservableProperty]
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

    /// <summary>Index into <see cref="TaskRepeatNames"/>, in <see cref="TaskRepeat"/> order.</summary>
    [ObservableProperty]
    public partial int TaskRepeatIndex { get; set; }

    public IReadOnlyList<string> TaskRepeatNames => MainViewModel.Translate("Never", "Every week", "Every month", "Every 3 months", "Every year");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskError))]
    public partial string? TaskError { get; set; }

    public bool HasTaskError => !string.IsNullOrEmpty(TaskError);

    [RelayCommand]
    private void EditTask(TaskItem task)
    {
        main.CloseOtherPanels();
        EditingTask = task;
        TaskTitle = task.Title;
        TaskDueDate = task.DueDate?.ToDateTime(TimeOnly.MinValue);
        TaskPerson = main.PersonOptions.FirstOrDefault(o => o.Id == task.PersonId) ?? PersonOption.Shared;
        TaskNotes = task.Notes;
        TaskIsDone = task.IsDone;
        TaskRepeatIndex = (int)task.Repeat;
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
            CompletedOn = TaskIsDone ? task.CompletedOn ?? MainViewModel.Today : null,
            Repeat = Enum.IsDefined((TaskRepeat)TaskRepeatIndex) ? (TaskRepeat)TaskRepeatIndex : TaskRepeat.Never,
            CreatedAt = task.CreatedAt,
        };
        try
        {
            await main.repository.UpdateTaskAsync(updated);
        }
        catch (Exception ex)
        {
            TaskError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        All[All.IndexOf(task)] = updated;
        if (!task.IsDone && updated.IsDone)
        {
            try
            {
                await AddNextRepeatAsync(updated);
            }
            catch (Exception ex)
            {
                main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            }
        }
        CloseTaskPanel();
        RefreshTaskRows();
    }

    internal void CloseTaskPanel()
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

    public bool Repeats => Task.Repeats;

    public string RepeatText => Task.Repeat switch
    {
        TaskRepeat.Weekly => Loc.T("Every week"),
        TaskRepeat.Monthly => Loc.T("Every month"),
        TaskRepeat.Quarterly => Loc.T("Every 3 months"),
        TaskRepeat.Yearly => Loc.T("Every year"),
        _ => "",
    };
}
