using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Household profiles: member details, medicines with their supply, jobs and what to do when off sick.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class HouseholdTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-household-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public HouseholdTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    private static async Task AddMemberAsync(MainViewModel vm, string name, Action? fill = null)
    {
        vm.NewMemberCommand.Execute(null);
        vm.MemberFormName = name;
        fill?.Invoke();
        await vm.SavePersonCommand.ExecuteAsync(null);
        Assert.Null(vm.PersonError);
    }

    [Fact]
    public void AMedicineCountsDownItsSupply()
    {
        var m = new Medicine { Name = "Euthyrox", Dose = 1, Morning = true, Evening = true, Stock = 20, CountedOn = new DateOnly(2026, 11, 1) };

        Assert.Equal(2, m.PerDay);
        Assert.Equal(14, m.StockOn(new DateOnly(2026, 11, 4)));
        // 20 tablets at 2 a day: ten full days, 1 to 10 November.
        Assert.Equal(new DateOnly(2026, 11, 10), m.RunsOutOn);
        Assert.Equal(0, m.StockOn(new DateOnly(2026, 12, 1)));
        Assert.Equal("Morning and evening", m.TimesText);

        m.AsNeeded = true;
        Assert.Null(m.RunsOutOn);
        Assert.Equal("As needed", m.TimesText);
    }

    [Fact]
    public void TheBernScaleGrowsWithYearsOfService()
    {
        var start = new DateOnly(2020, 3, 1);
        Assert.Null(SwissSickPay.BernScale(start, new DateOnly(2020, 5, 1)));    // first three months
        Assert.Equal((3, 0), SwissSickPay.BernScale(start, new DateOnly(2020, 9, 1)));
        Assert.Equal((0, 1), SwissSickPay.BernScale(start, new DateOnly(2021, 6, 1)));
        Assert.Equal((0, 2), SwissSickPay.BernScale(start, new DateOnly(2023, 6, 1)));
        Assert.Equal((0, 3), SwissSickPay.BernScale(start, new DateOnly(2025, 6, 1)));
        Assert.Equal((0, 4), SwissSickPay.BernScale(start, new DateOnly(2030, 6, 1)));
    }

    [Fact]
    public async Task AMembersDetailsAreSavedAndShownOnTheirProfile()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Anna", () =>
        {
            vm.PersonBirthDate = new DateTime(1990, 6, 15);
            vm.PersonPhone = "079 000 00 01";
            vm.PersonAllergies = "Penicillin";
            vm.PersonBloodTypeIndex = MainViewModel.BloodTypes.ToList().IndexOf("A+");
            vm.PersonDoctorName = "Dr. Keller";
            vm.PersonDoctorPhone = "061 000 00 02";
            vm.PersonEmergencyName = "Lukas Meier";
            vm.PersonEmergencyRelation = "Partner";
            vm.PersonEmergencyPhone = "079 000 00 03";
        });

        var reloaded = await LoadAsync();
        var anna = reloaded.People.Single();
        Assert.Equal(new DateOnly(1990, 6, 15), anna.BirthDate);
        Assert.Equal(1990, anna.BirthYear); // the retirement plan counts from it
        Assert.Equal("A+", anna.BloodType);

        Assert.Equal("Anna", reloaded.MemberName);
        Assert.Contains(reloaded.MemberContactLines, l => l.Value == "079 000 00 01" && l.CanCopy);
        Assert.Contains(reloaded.MemberHealthLines, l => l.Value == "Penicillin" && l.IsWarning);
        Assert.Contains(reloaded.MemberEmergencyLines, l => l.Label == "Partner" && l.Value.EndsWith("079 000 00 03"));

        var summary = reloaded.HealthSummaryText(anna);
        Assert.Contains("Allergies: Penicillin", summary);
        Assert.Contains("Dr. Keller", summary);
    }

    [Fact]
    public async Task AMedicineRunningOutIsShownRemindedAndRefilled()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Lukas");

        vm.Medicines.NewMedicineCommand.Execute(null);
        vm.Medicines.MedicineName = "Euthyrox";
        vm.Medicines.MedicineStrength = "50 µg";
        vm.Medicines.MedicineMorning = true;
        vm.Medicines.MedicineStock = 3;
        vm.Medicines.MedicinePackSize = 100;
        vm.Medicines.MedicinePrescribedBy = "Dr. Keller";
        await vm.Medicines.SaveMedicineCommand.ExecuteAsync(null);
        Assert.Null(vm.Medicines.MedicineError);

        var row = vm.Medicines.MedicineRows.Single();
        Assert.Equal("Euthyrox 50 µg", row.Name);
        Assert.Equal("Runs out in 2 days", row.Alert); // today, tomorrow and the day after
        Assert.True(vm.MemberTabs.Single().HasAlert);
        Assert.Contains(vm.AttentionRows, a => a.Kind == AttentionKind.Medicine && a.Text.Contains("Euthyrox"));
        Assert.Contains(vm.PlanReminders(DateTime.Today.AddDays(-10)), r => r.Id.StartsWith($"medicine-{row.Medicine.Id}-refill-"));
        Assert.Contains("Euthyrox 50 µg, 1 tablet Morning", vm.HealthSummaryText(vm.People.Single()));

        await vm.Medicines.RefillMedicineCommand.ExecuteAsync(row.Medicine);
        Assert.Equal(103, row.Medicine.Stock);
        Assert.Null(vm.Medicines.MedicineRows.Single().Alert);
        Assert.DoesNotContain(vm.AttentionRows, a => a.Kind == AttentionKind.Medicine);

        // A changed count is counted from today; reopening shows today's count.
        vm.Medicines.EditMedicineCommand.Execute(row.Medicine);
        Assert.Equal(103, vm.Medicines.MedicineStock);
    }

    [Fact]
    public async Task AMedicineNeedsATimeOrAsNeeded()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Sofia");
        vm.Medicines.NewMedicineCommand.Execute(null);
        vm.Medicines.MedicineName = "Ventolin";
        vm.Medicines.MedicineMorning = false;
        await vm.Medicines.SaveMedicineCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Medicines.MedicineError);

        vm.Medicines.MedicineAsNeeded = true;
        await vm.Medicines.SaveMedicineCommand.ExecuteAsync(null);
        Assert.Null(vm.Medicines.MedicineError);
        Assert.Equal("1 tablet when needed", vm.Medicines.MedicineRows.Single().DetailText);
    }

    [Fact]
    public async Task OffSickShowsWhomToTellAndWhenACertificateIsDue()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Anna");

        vm.Work.NewEmploymentCommand.Execute(null);
        vm.Work.EmploymentEmployer = "Spital Muster";
        vm.Work.EmploymentRole = "Nurse";
        vm.Work.EmploymentWorkload = 80;
        vm.Work.EmploymentStartedOn = Today.AddYears(-6).ToDateTime(TimeOnly.MinValue);
        vm.Work.EmploymentManagerName = "Martin Frei";
        vm.Work.EmploymentManagerPhone = "061 000 00 10";
        vm.Work.EmploymentCertificateDay = 3;
        await vm.Work.SaveEmploymentCommand.ExecuteAsync(null);
        Assert.Null(vm.Work.EmploymentError);

        var job = vm.Work.EmploymentRows.Single();
        Assert.Equal("Call Martin Frei before 08:00 on the first day", job.SickRuleText);
        Assert.Equal("Doctor’s certificate from day 3", job.CertificateText);
        Assert.Contains("3 months", job.SickPayText); // Bern scale in the 7th year of service
        Assert.Contains(job.Contacts, c => c.Value == "061 000 00 10" && c.CanCopy);

        await vm.Work.ReportSickCommand.ExecuteAsync("illness");
        Assert.True(vm.Work.IsMemberOffSick);
        Assert.Equal("Anna is off sick · day 1", vm.Work.SickBannerTitle);
        var tell = vm.Work.SickSteps[0];
        Assert.Equal("061 000 00 10", tell.CopyText);
        Assert.True(tell.IsDue);
        Assert.Contains(vm.AttentionRows, a => a.Kind == AttentionKind.SickLeave && a.Detail.Contains("tell work"));

        var leave = vm.Work.CurrentSickLeave!.Leave;
        Assert.Equal(Today.AddDays(2), leave.CertificateDueOn(3));
        Assert.Contains(vm.PlanReminders(DateTime.Today.AddDays(-1)), r => r.Id == $"sick-{leave.Id}-certificate-{Today.AddDays(2):yyyyMMdd}");

        var message = vm.Work.SickMessage(leave);
        Assert.StartsWith("Hello Martin", message);
        Assert.Contains("from day 3", message);
        Assert.EndsWith("Anna", message);

        await vm.Work.ToggleSickStepCommand.ExecuteAsync(tell);
        Assert.True(leave.Informed);
        Assert.True(vm.Work.SickSteps[0].IsDone);

        await vm.Work.BackAtWorkCommand.ExecuteAsync(null);
        Assert.False(vm.Work.IsMemberOffSick);
        Assert.Equal(Today, leave.To); // reported and back on the same day
        Assert.Equal("1 sick day in " + Today.Year, vm.Work.SickDaysText);

        // Saved: a fresh load shows the same.
        var reloaded = await LoadAsync();
        Assert.Single(reloaded.Work.SickLeaveRows);
        Assert.Equal("Spital Muster", reloaded.Work.SickLeaveRows.Single().EmployerName);
    }

    [Fact]
    public async Task EarlierSickDaysCountPerYearAndNeedACertificateWhenLong()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Lukas");
        vm.Work.NewSickLeaveCommand.Execute(null);
        vm.Work.SickLeaveFrom = new DateTime(Today.Year - 1, 2, 2);
        vm.Work.SickLeaveTo = new DateTime(Today.Year - 1, 2, 6);
        vm.Work.SickLeaveReason = "Flu";
        vm.Work.SickLeaveCertificateSent = false;
        await vm.Work.SaveSickLeaveCommand.ExecuteAsync(null);
        Assert.Null(vm.Work.SickLeaveError);

        var row = vm.Work.SickLeaveRows.Single();
        Assert.Equal("5 days", row.DaysText);
        Assert.True(row.CertificateMissing);
        Assert.Equal($"5 sick days in {Today.Year - 1}", vm.Work.SickDaysText);
        Assert.False(vm.Work.IsMemberOffSick);
    }

    [Fact]
    public async Task DeletingAMemberTakesTheirMedicinesJobsAndSickDays()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Sofia");
        vm.Medicines.NewMedicineCommand.Execute(null);
        vm.Medicines.MedicineName = "Vitamin D";
        await vm.Medicines.SaveMedicineCommand.ExecuteAsync(null);
        vm.Work.NewEmploymentCommand.Execute(null);
        vm.Work.EmploymentEmployer = "Bäckerei Muster";
        await vm.Work.SaveEmploymentCommand.ExecuteAsync(null);
        await vm.Work.ReportSickCommand.ExecuteAsync("illness");

        await vm.DeletePersonCommand.ExecuteAsync(vm.People.Single());
        Assert.False(vm.HasMembers);
        Assert.Empty(vm.Medicines.MedicineRows);

        var reloaded = await LoadAsync();
        Assert.Empty(await _repository.GetAllAsync<Medicine>());
        Assert.Empty(await _repository.GetAllAsync<Employment>());
        Assert.Empty(await _repository.GetAllAsync<SickLeave>());
        Assert.DoesNotContain(reloaded.AttentionRows, a => a.Kind is AttentionKind.SickLeave or AttentionKind.Medicine);
    }

    [Fact]
    public async Task GermanSickRulesAreWholeSentences()
    {
        var vm = await LoadAsync();
        await AddMemberAsync(vm, "Anna");
        vm.Work.NewEmploymentCommand.Execute(null);
        vm.Work.EmploymentEmployer = "Spital Muster";
        vm.Work.EmploymentHrName = "Personaldienst";
        vm.Work.EmploymentHrEmail = "hr@example.com";
        vm.Work.EmploymentReportByIndex = (int)SickReportChannel.Email;
        vm.Work.EmploymentReportTimeIndex = 0; // no set time
        await vm.Work.SaveEmploymentCommand.ExecuteAsync(null);
        Assert.Equal("Email Personaldienst on the first day", vm.Work.EmploymentRows.Single().SickRuleText);

        Loc.Instance.SetLanguage("de");
        try
        {
            vm.SelectMemberCommand.Execute(vm.MemberTabs.Single());
            Assert.Equal("Schreib am ersten Tag eine E-Mail an Personaldienst", vm.Work.EmploymentRows.Single().SickRuleText);
        }
        finally
        {
            Loc.Instance.SetLanguage("en");
        }
    }
}
