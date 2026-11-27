using Keystone.Data;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Uses formatted texts, so it runs apart from the language tests.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class NotesAndTasksTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"runway-test-{Guid.NewGuid():N}.db");
    private readonly KeystoneRepository _repository;

    public NotesAndTasksTests()
    {
        _repository = new KeystoneRepository(_databasePath);
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Notes_RoundTrip()
    {
        var note = await _repository.AddNoteAsync(new Note
        {
            Title = "Garden ideas", Body = "Raised beds\nHerbs by the door", Project = "Garden",
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now,
        });
        note.IsPinned = true;
        await _repository.UpdateNoteAsync(note);

        var saved = Assert.Single(await _repository.GetNotesAsync());
        Assert.Equal("Raised beds\nHerbs by the door", saved.Body);
        Assert.Equal("Garden", saved.Project);
        Assert.True(saved.IsPinned);

        await _repository.DeleteNoteAsync(note.Id);
        Assert.Empty(await _repository.GetNotesAsync());
    }

    [Fact]
    public async Task Tasks_RoundTripAndBecomeSharedWhenTheirPersonIsRemoved()
    {
        var person = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var task = await _repository.AddTaskAsync(new TaskItem
        {
            Title = "Renew passport", DueDate = new DateOnly(2027, 1, 15), PersonId = person.Id, CreatedAt = DateTime.Now,
        });
        task.CompletedOn = new DateOnly(2027, 1, 10);
        await _repository.UpdateTaskAsync(task);
        await _repository.DeletePersonAsync(person.Id);

        var saved = Assert.Single(await _repository.GetTasksAsync());
        Assert.Equal("Renew passport", saved.Title);
        Assert.Equal(new DateOnly(2027, 1, 15), saved.DueDate);
        Assert.Equal(new DateOnly(2027, 1, 10), saved.CompletedOn);
        Assert.Null(saved.PersonId);
    }

    [Fact]
    public void TaskRow_SaysWhenItIsDue()
    {
        string Due(DateOnly? due, DateOnly? done = null) =>
            new TaskRow(new TaskItem { Title = "x", DueDate = due, CompletedOn = done }, "Shared", Today).DueText;

        Assert.Equal("Due today", Due(Today));
        Assert.Equal("Due tomorrow", Due(Today.AddDays(1)));
        Assert.StartsWith("Overdue since", Due(Today.AddDays(-2)));
        Assert.StartsWith("Due ", Due(Today.AddDays(5)));
        Assert.Equal("", Due(null));
        Assert.StartsWith("Done", Due(Today.AddDays(-2), Today));
    }

    [Fact]
    public async Task Overview_CountsTasksDueThisWeek()
    {
        await _repository.AddTaskAsync(new TaskItem { Title = "Overdue", DueDate = Today.AddDays(-1), CreatedAt = DateTime.Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Soon", DueDate = Today.AddDays(3), CreatedAt = DateTime.Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Later", DueDate = Today.AddDays(30), CreatedAt = DateTime.Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Whenever", CreatedAt = DateTime.Now });
        await _repository.AddTaskAsync(new TaskItem
        {
            Title = "Done", DueDate = Today.AddDays(-3), CompletedOn = Today, CreatedAt = DateTime.Now,
        });

        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        Assert.Equal("2 tasks due this week · 1 overdue", vm.Tasks.TasksDueText);
        Assert.True(vm.Tasks.HasOverdueTasks);
        // Open tasks, soonest first and undated last.
        Assert.Equal(["Overdue", "Soon", "Later", "Whenever"], vm.Tasks.TaskRows.Select(r => r.Title));
    }

    [Fact]
    public async Task AddingAndTickingTasks()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        vm.Tasks.NewTaskTitle = "  ";
        await vm.Tasks.AddTaskCommand.ExecuteAsync(null);
        Assert.True(vm.Tasks.HasNewTaskError);

        vm.Tasks.NewTaskTitle = "Call the landlord";
        vm.Tasks.NewTaskDueDate = DateTime.Today;
        await vm.Tasks.AddTaskCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Tasks.NewTaskTitle);
        var row = Assert.Single(vm.Tasks.TaskRows);

        await vm.Tasks.ToggleTaskCommand.ExecuteAsync(row.Task);
        Assert.Empty(vm.Tasks.TaskRows); // the To do list hides finished tasks
        Assert.Equal(Today, Assert.Single(await _repository.GetTasksAsync()).CompletedOn);
    }

    [Fact]
    public async Task NotesSaveAsYouTypeAndEmptyOnesAreDropped()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        await vm.Notes.NewNoteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Notes.SelectedNoteItem);
        vm.Notes.NoteTitle = "Holiday plans";
        vm.Notes.NoteBody = "Ticino in May";
        Assert.True(vm.Notes.HasUnsavedNote);
        await vm.Notes.SaveNoteNowAsync();
        Assert.Equal("Ticino in May", Assert.Single(await _repository.GetNotesAsync()).Body);

        // A second note left empty disappears once another note is picked.
        await vm.Notes.NewNoteCommand.ExecuteAsync(null);
        Assert.Equal(2, (await _repository.GetNotesAsync()).Count);
        vm.Notes.SelectedNoteItem = vm.Notes.NoteItems.First(i => i.Title == "Holiday plans");
        await Task.Delay(200);
        Assert.Single(await _repository.GetNotesAsync());
        Assert.Single(vm.Notes.NoteItems);
    }

    [Fact]
    public void Policies_HaveTheirOwnFolder()
    {
        Assert.Equal("Policies", HouseholdDocument.FolderName(DocumentKind.Policy));
        Assert.Equal("Bills", HouseholdDocument.FolderName(DocumentKind.Bill));
        Assert.Equal("Policy", HouseholdDocument.KindName(DocumentKind.Policy));
        Keystone.Localization.Loc.Instance.SetLanguage("de");
        try
        {
            Assert.Equal("Police", HouseholdDocument.KindName(DocumentKind.Policy));
            Assert.Equal("Policies", HouseholdDocument.FolderName(DocumentKind.Policy));
        }
        finally
        {
            Keystone.Localization.Loc.Instance.SetLanguage("en");
        }
    }
}
