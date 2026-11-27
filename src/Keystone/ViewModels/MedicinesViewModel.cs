using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>One medicine on a member's profile: what, how much and when, and how long the pack lasts.</summary>
public sealed record MedicineRow(Medicine Medicine, DateOnly Today, string? Alert)
{
    public string Name => $"{Medicine.Name} {Medicine.Strength}".Trim();

    /// <summary>"1 tablet · Morning and evening · Thyroid".</summary>
    public string DetailText => string.Join(" · ", new[]
    {
        Medicine.AsNeeded ? Loc.F("{0} when needed", Medicine.DoseText) : Medicine.DoseText,
        Medicine.AsNeeded ? "" : Medicine.TimesText,
        Medicine.Reason,
    }.Where(s => s != ""));

    public bool IsStopped => Medicine.HasStopped(Today);

    /// <summary>"Enough until 12 Dec 2026 · 14 left", "Until 30 Nov (course)", or "Stopped 2 Oct 2026".</summary>
    public string SupplyText
    {
        get
        {
            if (IsStopped)
                return Loc.F("Stopped {0}", Format.Date(Medicine.EndsOn!.Value));
            var parts = new List<string>();
            if (Medicine.RunsOutOn is { } runsOut)
            {
                var left = Medicine.StockOn(Today) ?? 0;
                parts.Add(runsOut < Today ? Loc.T("Run out") : Loc.F("Enough until {0}", Format.Date(runsOut)));
                parts.Add(Loc.F("{0} left", left.ToString("0.#", Loc.Instance.Culture)));
            }
            if (Medicine.EndsOn is { } end)
                parts.Add(Loc.F("course until {0}", Format.Date(end)));
            if (Medicine.StartedOn is { } start && start > Today)
                parts.Add(Loc.F("starts {0}", Format.Date(start)));
            return string.Join(" · ", parts);
        }
    }

    public bool HasSupply => SupplyText != "";

    public bool HasAlert => Alert is not null;

    public bool CanRefill => !IsStopped && Medicine.PackSize is > 0;

    public string RefillTip => Medicine.PackSize is { } size ? Loc.F("Got a new pack: add {0}", size) : "";

    public string PrescribedText => Medicine.PrescribedBy == "" ? "" : Loc.F("Prescribed by {0}", Medicine.PrescribedBy);

    public bool HasPrescribed => PrescribedText != "";
}

/// <summary>Medicines and pills per household member, with how long they last and when to get more.</summary>
/// <remarks>Its own view model; shared things (people, notices, reminders) come from the main one.</remarks>
public sealed partial class MedicinesViewModel(MainViewModel main) : ViewModelBase
{
    /// <summary>Every member's medicines.</summary>
    internal List<Medicine> All { get; } = [];

    /// <summary>Days ahead a medicine running out or a prescription ending is shown and reminded.</summary>
    internal const int MedicineWarningDays = 7;

    internal const int PrescriptionWarningDays = 14;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedicineRows))]
    public partial IReadOnlyList<MedicineRow> MedicineRows { get; set; } = [];

    public bool HasMedicineRows => MedicineRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoppedMedicineRows))]
    public partial IReadOnlyList<MedicineRow> StoppedMedicineRows { get; set; } = [];

    public bool HasStoppedMedicineRows => StoppedMedicineRows.Count > 0;

    /// <summary>"Runs out in 3 days", "Prescription ends 4 Dec": what to act on, or null.</summary>
    internal string? MedicineAlert(Medicine m)
    {
        if (!m.IsTaken(MainViewModel.Today))
            return null;
        if (m.RunsOutOn is { } runsOut && runsOut.DayNumber - MainViewModel.Today.DayNumber <= MedicineWarningDays)
            return runsOut < MainViewModel.Today ? Loc.T("Run out: get a new pack")
                : runsOut == MainViewModel.Today ? Loc.T("Last full day today: get a new pack")
                : Loc.Plural(runsOut.DayNumber - MainViewModel.Today.DayNumber, "Runs out in {0} day", "Runs out in {0} days");
        if (m.PrescriptionUntil is { } until && until.DayNumber - MainViewModel.Today.DayNumber <= PrescriptionWarningDays)
            return until < MainViewModel.Today ? Loc.F("Prescription ended {0}", Format.Date(until)) : Loc.F("Prescription ends {0}", Format.Date(until));
        return null;
    }

    internal void RefreshMedicineRows()
    {
        var mine = All.Where(m => m.PersonId == main.SelectedMemberId).ToList();
        MedicineRows = main.Sorted("medicines", mine.Where(m => !m.HasStopped(MainViewModel.Today))
                .OrderBy(m => MedicineAlert(m) is null).ThenBy(m => m.AsNeeded).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new MedicineRow(m, MainViewModel.Today, MedicineAlert(m))),
            r => r.Medicine.SortOrder,
            new SortKey<MedicineRow>("name", r => r.Name),
            new SortKey<MedicineRow>("supply", r => r.Medicine.RunsOutOn?.DayNumber ?? int.MaxValue));
        StoppedMedicineRows = mine.Where(m => m.HasStopped(MainViewModel.Today)).OrderByDescending(m => m.EndsOn)
            .Select(m => new MedicineRow(m, MainViewModel.Today, null)).ToList();
    }

    private void MedicinesChanged()
    {
        main.RefreshHousehold();
        main.RefreshDashboard();
        main.ScheduleReminders();
    }

    /// <summary>A new pack: what's left today plus one pack, counted from today.</summary>
    [RelayCommand]
    private async Task RefillMedicineAsync(Medicine medicine)
    {
        if (medicine.PackSize is not { } size || size <= 0)
            return;
        var (stock, counted) = (medicine.Stock, medicine.CountedOn);
        medicine.Stock = (medicine.StockOn(MainViewModel.Today) ?? 0) + size;
        medicine.CountedOn = MainViewModel.Today;
        try
        {
            await main.repository.SaveAsync(medicine, isNew: false);
        }
        catch (Exception ex)
        {
            (medicine.Stock, medicine.CountedOn) = (stock, counted);
            main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        MedicinesChanged();
        main.ShowNotice(Loc.F("{0}: {1} added, enough until {2}", medicine.Name, size, Format.Date(medicine.RunsOutOn ?? MainViewModel.Today)), async () =>
        {
            (medicine.Stock, medicine.CountedOn) = (stock, counted);
            await main.repository.SaveAsync(medicine, isNew: false);
            MedicinesChanged();
        });
    }

    [RelayCommand]
    private async Task DeleteMedicineAsync(Medicine medicine)
    {
        try
        {
            await main.repository.DeleteAsync<Medicine>(m => m.Id == medicine.Id);
        }
        catch (Exception ex)
        {
            main.ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        All.Remove(medicine);
        if (EditingMedicine == medicine)
            CloseMedicinePanel();
        MedicinesChanged();
        main.ShowNotice(Loc.F("Deleted “{0}”", medicine.Name), async () =>
        {
            await main.repository.SaveAsync(medicine, isNew: true);
            All.Add(medicine);
            MedicinesChanged();
        });
    }

    // ── The form ────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsMedicinePanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MedicineFormTitle))]
    public partial Medicine? EditingMedicine { get; set; }

    public string MedicineFormTitle => Loc.T(EditingMedicine is null ? "Add medicine" : "Edit medicine");

    [ObservableProperty]
    public partial PersonOption MedicinePerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string MedicineName { get; set; } = "";

    [ObservableProperty]
    public partial string MedicineStrength { get; set; } = "";

    [ObservableProperty]
    public partial decimal? MedicineDose { get; set; } = 1;

    [ObservableProperty]
    public partial string MedicineUnit { get; set; } = "";

    [ObservableProperty]
    public partial bool MedicineMorning { get; set; }

    [ObservableProperty]
    public partial bool MedicineNoon { get; set; }

    [ObservableProperty]
    public partial bool MedicineEvening { get; set; }

    [ObservableProperty]
    public partial bool MedicineNight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MedicineIsDaily))]
    public partial bool MedicineAsNeeded { get; set; }

    public bool MedicineIsDaily => !MedicineAsNeeded;

    [ObservableProperty]
    public partial string MedicineReason { get; set; } = "";

    [ObservableProperty]
    public partial string MedicinePrescribedBy { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? MedicineStartedOn { get; set; }

    [ObservableProperty]
    public partial DateTime? MedicineEndsOn { get; set; }

    /// <summary>Units on hand today; changing it re-counts from today.</summary>
    [ObservableProperty]
    public partial decimal? MedicineStock { get; set; }

    [ObservableProperty]
    public partial int? MedicinePackSize { get; set; }

    [ObservableProperty]
    public partial DateTime? MedicinePrescriptionUntil { get; set; }

    [ObservableProperty]
    public partial string MedicineNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedicineError))]
    public partial string? MedicineError { get; set; }

    public bool HasMedicineError => !string.IsNullOrEmpty(MedicineError);

    /// <summary>The on-hand count shown in the form when it opened, to tell whether it was changed.</summary>
    private decimal? _medicineStockShown;

    [RelayCommand]
    internal void NewMedicine()
    {
        main.CloseOtherPanels();
        EditingMedicine = null;
        MedicinePerson = main.PersonOptions.FirstOrDefault(o => o.Id == main.SelectedMemberId) ?? PersonOption.Shared;
        MedicineName = "";
        MedicineStrength = "";
        MedicineDose = 1;
        MedicineUnit = Loc.T("tablet");
        MedicineMorning = true;
        MedicineNoon = MedicineEvening = MedicineNight = MedicineAsNeeded = false;
        MedicineReason = "";
        MedicinePrescribedBy = "";
        MedicineStartedOn = DateTime.Today;
        MedicineEndsOn = null;
        MedicineStock = null;
        _medicineStockShown = null;
        MedicinePackSize = null;
        MedicinePrescriptionUntil = null;
        MedicineNotes = "";
        MedicineError = null;
        IsMedicinePanelOpen = true;
    }

    [RelayCommand]
    private void EditMedicine(Medicine medicine)
    {
        NewMedicine();
        EditingMedicine = medicine;
        MedicinePerson = main.PersonOptions.FirstOrDefault(o => o.Id == medicine.PersonId) ?? PersonOption.Shared;
        MedicineName = medicine.Name;
        MedicineStrength = medicine.Strength;
        MedicineDose = medicine.Dose;
        MedicineUnit = medicine.Unit;
        (MedicineMorning, MedicineNoon, MedicineEvening, MedicineNight) = (medicine.Morning, medicine.Noon, medicine.Evening, medicine.Night);
        MedicineAsNeeded = medicine.AsNeeded;
        MedicineReason = medicine.Reason;
        MedicinePrescribedBy = medicine.PrescribedBy;
        MedicineStartedOn = medicine.StartedOn?.ToDateTime(TimeOnly.MinValue);
        MedicineEndsOn = medicine.EndsOn?.ToDateTime(TimeOnly.MinValue);
        MedicineStock = _medicineStockShown = medicine.StockOn(MainViewModel.Today) is { } left ? Math.Round(left, 1) : null;
        MedicinePackSize = medicine.PackSize;
        MedicinePrescriptionUntil = medicine.PrescriptionUntil?.ToDateTime(TimeOnly.MinValue);
        MedicineNotes = medicine.Notes;
    }

    [RelayCommand]
    private async Task SaveMedicineAsync()
    {
        MedicineError = null;
        if (string.IsNullOrWhiteSpace(MedicineName))
        {
            MedicineError = Loc.T("Enter the medicine’s name, e.g. “Ibuprofen”.");
            return;
        }
        if (MedicineDose is not > 0)
        {
            MedicineError = Loc.T("Enter how much is taken at a time, e.g. 1.");
            return;
        }
        if (!MedicineAsNeeded && !(MedicineMorning || MedicineNoon || MedicineEvening || MedicineNight))
        {
            MedicineError = Loc.T("Choose when it’s taken, or tick “Only when needed”.");
            return;
        }
        if (MedicineStartedOn is { } s && MedicineEndsOn is { } e && e < s)
        {
            MedicineError = Loc.T("The last day can’t be before the first.");
            return;
        }

        var isNew = EditingMedicine is null;
        var m = EditingMedicine ?? new Medicine();
        m.PersonId = MedicinePerson.Id;
        m.Name = MedicineName.Trim();
        m.Strength = MedicineStrength.Trim();
        m.Dose = MedicineDose.Value;
        m.Unit = MedicineUnit.Trim();
        (m.Morning, m.Noon, m.Evening, m.Night) = MedicineAsNeeded ? (false, false, false, false)
            : (MedicineMorning, MedicineNoon, MedicineEvening, MedicineNight);
        m.AsNeeded = MedicineAsNeeded;
        m.Reason = MedicineReason.Trim();
        m.PrescribedBy = MedicinePrescribedBy.Trim();
        m.StartedOn = MedicineStartedOn is { } start ? DateOnly.FromDateTime(start) : null;
        m.EndsOn = MedicineEndsOn is { } end ? DateOnly.FromDateTime(end) : null;
        // A changed count starts counting down again from today; an unchanged one keeps its day.
        if (MedicineStock != _medicineStockShown)
        {
            m.Stock = MedicineStock is >= 0 ? MedicineStock : null;
            m.CountedOn = m.Stock is null ? null : MainViewModel.Today;
        }
        m.PackSize = MedicinePackSize is > 0 ? MedicinePackSize : null;
        m.PrescriptionUntil = MedicinePrescriptionUntil is { } until ? DateOnly.FromDateTime(until) : null;
        m.Notes = MedicineNotes.Trim();

        try
        {
            await main.repository.SaveAsync(m, isNew);
        }
        catch (Exception ex)
        {
            MedicineError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        if (isNew)
            All.Add(m);
        if (m.PersonId is not null)
            main.SelectedMemberId = m.PersonId;
        CloseMedicinePanel();
        MedicinesChanged();
    }

    internal void CloseMedicinePanel()
    {
        IsMedicinePanelOpen = false;
        EditingMedicine = null;
    }

    // ── "More details" in the form: unfolds when one of its fields has something in it ──

    [ObservableProperty]
    public partial bool MedicineMoreOpen { get; set; }

    partial void OnIsMedicinePanelOpenChanged(bool value)
    {
        if (value)
            MedicineMoreOpen = MedicineStock is not null || MedicinePackSize is not null || Has(MedicinePrescribedBy) ||
                               MedicinePrescriptionUntil is not null || MedicineEndsOn is not null || Has(MedicineNotes);
    }

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);

    partial void OnMedicineStockChanged(decimal? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicinePackSizeChanged(int? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicinePrescribedByChanged(string value) { if (Has(value)) MedicineMoreOpen = true; }
    partial void OnMedicinePrescriptionUntilChanged(DateTime? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicineEndsOnChanged(DateTime? value) { if (value is not null) MedicineMoreOpen = true; }
    partial void OnMedicineNotesChanged(string value) { if (Has(value)) MedicineMoreOpen = true; }
}
