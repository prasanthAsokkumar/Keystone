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
public partial class MainViewModel
{
    private readonly List<Medicine> _medicines = [];

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
        if (!m.IsTaken(Today))
            return null;
        if (m.RunsOutOn is { } runsOut && runsOut.DayNumber - Today.DayNumber <= MedicineWarningDays)
            return runsOut < Today ? Loc.T("Run out: get a new pack")
                : runsOut == Today ? Loc.T("Last full day today: get a new pack")
                : Loc.Plural(runsOut.DayNumber - Today.DayNumber, "Runs out in {0} day", "Runs out in {0} days");
        if (m.PrescriptionUntil is { } until && until.DayNumber - Today.DayNumber <= PrescriptionWarningDays)
            return until < Today ? Loc.F("Prescription ended {0}", Format.Date(until)) : Loc.F("Prescription ends {0}", Format.Date(until));
        return null;
    }

    private void RefreshMedicineRows()
    {
        var mine = _medicines.Where(m => m.PersonId == SelectedMemberId).ToList();
        MedicineRows = Sorted("medicines", mine.Where(m => !m.HasStopped(Today))
                .OrderBy(m => MedicineAlert(m) is null).ThenBy(m => m.AsNeeded).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new MedicineRow(m, Today, MedicineAlert(m))),
            r => r.Medicine.SortOrder,
            new SortKey<MedicineRow>("name", r => r.Name),
            new SortKey<MedicineRow>("supply", r => r.Medicine.RunsOutOn?.DayNumber ?? int.MaxValue));
        StoppedMedicineRows = mine.Where(m => m.HasStopped(Today)).OrderByDescending(m => m.EndsOn)
            .Select(m => new MedicineRow(m, Today, null)).ToList();
    }

    private void MedicinesChanged()
    {
        RefreshHousehold();
        RefreshDashboard();
        ScheduleReminders();
    }

    /// <summary>A new pack: what's left today plus one pack, counted from today.</summary>
    [RelayCommand]
    private async Task RefillMedicineAsync(Medicine medicine)
    {
        if (medicine.PackSize is not { } size || size <= 0)
            return;
        var (stock, counted) = (medicine.Stock, medicine.CountedOn);
        medicine.Stock = (medicine.StockOn(Today) ?? 0) + size;
        medicine.CountedOn = Today;
        try
        {
            await repository.SaveAsync(medicine, isNew: false);
        }
        catch (Exception ex)
        {
            (medicine.Stock, medicine.CountedOn) = (stock, counted);
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        MedicinesChanged();
        ShowNotice(Loc.F("{0}: {1} added, enough until {2}", medicine.Name, size, Format.Date(medicine.RunsOutOn ?? Today)), async () =>
        {
            (medicine.Stock, medicine.CountedOn) = (stock, counted);
            await repository.SaveAsync(medicine, isNew: false);
            MedicinesChanged();
        });
    }

    [RelayCommand]
    private async Task DeleteMedicineAsync(Medicine medicine)
    {
        try
        {
            await repository.DeleteAsync<Medicine>(m => m.Id == medicine.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        _medicines.Remove(medicine);
        if (EditingMedicine == medicine)
            CloseMedicinePanel();
        MedicinesChanged();
        ShowNotice(Loc.F("Deleted “{0}”", medicine.Name), async () =>
        {
            await repository.SaveAsync(medicine, isNew: true);
            _medicines.Add(medicine);
            MedicinesChanged();
        });
    }

    // ── The form ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
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
    private void NewMedicine()
    {
        CloseOtherPanels();
        EditingMedicine = null;
        MedicinePerson = PersonOptions.FirstOrDefault(o => o.Id == SelectedMemberId) ?? PersonOption.Shared;
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
        MedicinePerson = PersonOptions.FirstOrDefault(o => o.Id == medicine.PersonId) ?? PersonOption.Shared;
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
        MedicineStock = _medicineStockShown = medicine.StockOn(Today) is { } left ? Math.Round(left, 1) : null;
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
            m.CountedOn = m.Stock is null ? null : Today;
        }
        m.PackSize = MedicinePackSize is > 0 ? MedicinePackSize : null;
        m.PrescriptionUntil = MedicinePrescriptionUntil is { } until ? DateOnly.FromDateTime(until) : null;
        m.Notes = MedicineNotes.Trim();

        try
        {
            await repository.SaveAsync(m, isNew);
        }
        catch (Exception ex)
        {
            MedicineError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        if (isNew)
            _medicines.Add(m);
        if (m.PersonId is not null)
            SelectedMemberId = m.PersonId;
        CloseMedicinePanel();
        MedicinesChanged();
    }

    private void CloseMedicinePanel()
    {
        IsMedicinePanelOpen = false;
        EditingMedicine = null;
    }
}
