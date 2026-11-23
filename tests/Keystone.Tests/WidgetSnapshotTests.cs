using System.Text.Json;
using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>The summary the desktop widget shows: open tasks, bills to pay and the next appointments.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class WidgetSnapshotTests : IDisposable
{
    private static readonly DateTime Now = DateTime.Today.AddHours(10);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-widget-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public WidgetSnapshotTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "keystone.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private async Task<MainViewModel> LoadAsync()
    {
        await _repository.AddTaskAsync(new TaskItem { Title = "Renew passport", DueDate = Today.AddDays(-2), CreatedAt = Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Sort out the cellar", CreatedAt = Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Done already", DueDate = Today, CompletedOn = Today, CreatedAt = Now });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Premium December", Sender = "Helsana", Kind = DocumentKind.Bill, Amount = 1080, DueDate = Today.AddDays(8), Date = Today, AddedAt = Now,
        });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Electricity", Sender = "IWB", Kind = DocumentKind.Bill, Amount = 190, DueDate = Today.AddDays(-1), Date = Today, AddedAt = Now,
        });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Paid", Kind = DocumentKind.Bill, Amount = 50, PaidOn = Today, Date = Today, AddedAt = Now,
        });
        foreach (var (title, days) in new[] { ("Dentist check-up", 1), ("Yesterday's meeting", -1), ("Far away", 40) })
            await _repository.SaveAsync(new Appointment
            {
                Title = title, Location = "Basel", Start = Now.AddDays(days), End = Now.AddDays(days).AddHours(1), CreatedAt = Now, UpdatedAt = Now,
            }, isNew: true);

        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    [Fact]
    public async Task ShowsWhatIsOpenSoonestFirst()
    {
        var vm = await LoadAsync();
        var snapshot = vm.BuildWidgetSnapshot(Now, hideDetails: false);

        Assert.Equal((2, 1), (snapshot.Tasks.Count, snapshot.Tasks.Overdue));
        Assert.Equal(["Renew passport", "Sort out the cellar"], snapshot.Tasks.Items.Select(i => i.Title));

        Assert.Equal((2, 1, Format.Money(1270)), (snapshot.Bills.Count, snapshot.Bills.Overdue, snapshot.Bills.Total));
        Assert.Equal(["IWB", "Helsana"], snapshot.Bills.Items.Select(i => i.Title));
        Assert.Equal(Format.Money(190), snapshot.Bills.Items[0].Detail);

        // Past ones and those more than two weeks away are left out.
        var appointment = Assert.Single(snapshot.Appointments.Items);
        Assert.Equal(("Dentist check-up", "Basel", Today.AddDays(1)), (appointment.Title, appointment.Detail, appointment.Date!.Value));
        Assert.Equal("Bills to pay", snapshot.Labels["bills"]);
    }

    [Fact]
    public async Task OnlyCountsWhileTheAppLockIsOn()
    {
        var vm = await LoadAsync();
        var snapshot = vm.BuildWidgetSnapshot(Now, hideDetails: true);

        Assert.True(snapshot.DetailsHidden);
        Assert.Equal(2, snapshot.Tasks.Count);
        Assert.All(snapshot.Tasks.Items.Concat(snapshot.Bills.Items).Concat(snapshot.Appointments.Items), i =>
        {
            Assert.Equal("", i.Title);
            Assert.Null(i.Detail);
        });
        Assert.DoesNotContain("Helsana", snapshot.ToJson());
    }

    [Fact]
    public async Task IsWrittenNextToTheDatabaseInTheWidgetsFormat()
    {
        var vm = await LoadAsync();
        var path = Path.Combine(_folder, "widget.json");
        for (var i = 0; i < 100 && !File.Exists(path); i++)
            await Task.Delay(50); // written with the reminders, a moment after loading

        Assert.True(File.Exists(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = json.RootElement;
        Assert.Equal(WidgetSnapshot.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal(2, root.GetProperty("tasks").GetProperty("count").GetInt32());
        Assert.Equal(Today.AddDays(-2).ToString("yyyy-MM-dd"), root.GetProperty("tasks").GetProperty("items")[0].GetProperty("date").GetString());
        Assert.Equal("+{0} more", root.GetProperty("labels").GetProperty("more").GetString());
    }

    [Fact]
    public void TheDebugBuildWritesItsOwnFile()
    {
        Assert.Equal("widget.json", Path.GetFileName(WidgetSnapshot.PathFor("/data/keystone.db")));
        Assert.Equal("widget-dev.json", Path.GetFileName(WidgetSnapshot.PathFor("/data/keystone-dev.db")));
    }
}
