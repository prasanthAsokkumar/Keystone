using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>
/// One person's view: picked once in the sidebar, it sets every page's person filter (entries, documents, tasks,
/// appointments, insurance, taxes, retirement) and narrows the lists without one (contracts, dates, warranties,
/// assets), and opens that person's Household profile. A bar says so on each page, with a way back to everyone.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    public partial IReadOnlyList<PersonOption> ViewPersonOptions { get; set; } = [PersonOption.Everyone];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersonView), nameof(PersonViewText), nameof(ShowsPersonViewBar))]
    public partial PersonOption ViewPerson { get; set; } = PersonOption.Everyone;

    public bool IsPersonView => ViewPerson is { IsEveryone: false };

    /// <summary>"Showing Anna’s things only".</summary>
    public string PersonViewText => IsPersonView ? Loc.F("Showing {0}’s things only", ViewPerson.Name) : "";

    /// <summary>The bar on the pages the view narrows (the Overview and Settings stay for everyone).</summary>
    public bool ShowsPersonViewBar => IsPersonView && CurrentPage is not (AppPage.Overview or AppPage.Settings);

    /// <summary>Whether a thing belonging to <paramref name="personId"/> (null: shared) is shown in the current view.</summary>
    internal bool InView(int? personId) => !IsPersonView || personId == ViewPerson.Id;

    partial void OnViewPersonChanged(PersonOption value)
    {
        if (value is null)
            return;
        PersonOption Filter() => value.IsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == value.Id) ?? PersonOption.Everyone;
        PersonFilter = Filter();
        DocumentPersonFilter = Filter();
        TaskPersonFilter = Filter();
        AppointmentPersonFilter = Filter();
        InsurancePersonFilter = Filter();
        TaxPersonFilter = Filter();
        RetirementPersonFilter = Filter();
        if (!value.IsEveryone)
            SelectedMemberId = value.Id;
        if (_isLoading)
            return;
        RefreshContracts();
        RefreshImportantDates();
        RefreshWarranties();
        RefreshAssets();
        RefreshHousehold();
    }

    [RelayCommand]
    private void ShowEveryone() => ViewPerson = PersonOption.Everyone;
}
