using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>One section of a page (a card): it can be moved up or down and folded away; both are remembered.</summary>
public sealed partial class PageSection(string key, Action<PageSection, int> move, Action changed) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTip))]
    public partial bool IsOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    /// <summary>A section with nothing to show (no open bills) is hidden, and moves skip it.</summary>
    public bool IsShown { get; set; } = true;

    public string ToggleTip => Loc.T(IsOpen ? "Collapse" : "Expand");

    [RelayCommand]
    private void MoveUp() => move(this, -1);

    [RelayCommand]
    private void MoveDown() => move(this, +1);

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        changed();
    }
}

/// <summary>
/// The order and folding of a page's sections. The page puts its cards in <see cref="InsuranceSectionOrder"/>
/// (code-behind, by each card's Tag); the layout is saved in <c>AppSettings.PageLayout</c>.
/// </summary>
public partial class MainViewModel
{
    private sealed record SavedLayout(List<string> Order, List<string> Closed);

    private Dictionary<string, SavedLayout> _pageLayouts = [];

    private static readonly JsonSerializerOptions LayoutJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The Insurance page's cards, in their built-in order.</summary>
    private static readonly string[] InsuranceSectionKeys = ["bills", "health", "policies", "cover", "premiums", "papers"];

    private List<PageSection>? _insuranceSections;

    private List<PageSection> InsuranceSections => _insuranceSections ??= CreateSections("insurance", InsuranceSectionKeys);

    /// <summary>The Overview's cards, in their built-in order.</summary>
    private static readonly string[] OverviewSectionKeys = ["attention", "money", "upcoming", "chart"];

    private List<PageSection>? _overviewSections;

    private List<PageSection> OverviewSections => _overviewSections ??= CreateSections("overview", OverviewSectionKeys);

    public PageSection AttentionSection => OverviewSections.Single(s => s.Key == "attention");
    public PageSection MoneySection => OverviewSections.Single(s => s.Key == "money");
    public PageSection UpcomingSection => OverviewSections.Single(s => s.Key == "upcoming");
    public PageSection ChartSection => OverviewSections.Single(s => s.Key == "chart");

    /// <summary>The Overview's section keys, top to bottom.</summary>
    public IReadOnlyList<string> OverviewSectionOrder => OverviewSections.Select(s => s.Key).ToList();

    public PageSection BillsSection => InsuranceSections.Single(s => s.Key == "bills");
    public PageSection HealthSection => InsuranceSections.Single(s => s.Key == "health");
    public PageSection PoliciesSection => InsuranceSections.Single(s => s.Key == "policies");
    public PageSection CoverSection => InsuranceSections.Single(s => s.Key == "cover");
    public PageSection PremiumsSection => InsuranceSections.Single(s => s.Key == "premiums");
    public PageSection PapersSection => InsuranceSections.Single(s => s.Key == "papers");

    /// <summary>The Insurance page's section keys, top to bottom.</summary>
    public IReadOnlyList<string> InsuranceSectionOrder => InsuranceSections.Select(s => s.Key).ToList();

    private void LoadPageLayout(string json)
    {
        try
        {
            _pageLayouts = json == "" ? [] : JsonSerializer.Deserialize<Dictionary<string, SavedLayout>>(json, LayoutJson) ?? [];
        }
        catch (JsonException)
        {
            _pageLayouts = []; // a damaged layout only means the built-in one
        }
        _insuranceSections = null;
        _overviewSections = null;
        OnPropertyChanged(nameof(InsuranceSectionOrder));
        OnPropertyChanged(nameof(OverviewSectionOrder));
        foreach (var name in new[] { nameof(AttentionSection), nameof(MoneySection), nameof(UpcomingSection), nameof(ChartSection) })
            OnPropertyChanged(name);
        foreach (var name in new[] { nameof(BillsSection), nameof(HealthSection), nameof(PoliciesSection), nameof(CoverSection), nameof(PremiumsSection), nameof(PapersSection) })
            OnPropertyChanged(name);
    }

    private List<PageSection> CreateSections(string page, string[] keys)
    {
        var saved = _pageLayouts.GetValueOrDefault(page);
        // Saved order first; sections added in a later version go where they are built in.
        var order = (saved?.Order ?? []).Where(keys.Contains).Distinct().ToList();
        foreach (var key in keys.Where(k => !order.Contains(k)))
            order.Insert(Math.Min(Array.IndexOf(keys, key), order.Count), key);

        List<PageSection> sections = [];
        sections.AddRange(order.Select(key => new PageSection(key, (s, by) => MoveSection(page, sections, s, by), () => SavePageLayout(page, sections))
        {
            IsOpen = saved?.Closed.Contains(key) != true,
        }));
        UpdateMoves(sections);
        return sections;
    }

    /// <summary>Moves a section past the next one shown in that direction.</summary>
    private void MoveSection(string page, List<PageSection> sections, PageSection section, int by)
    {
        var from = sections.IndexOf(section);
        var to = from + by;
        while (to >= 0 && to < sections.Count && !sections[to].IsShown)
            to += by;
        if (to < 0 || to >= sections.Count)
            return;
        sections.RemoveAt(from);
        sections.Insert(to, section);
        UpdateMoves(sections);
        SavePageLayout(page, sections);
        OnPropertyChanged(page == "overview" ? nameof(OverviewSectionOrder) : nameof(InsuranceSectionOrder));
    }

    private static void UpdateMoves(List<PageSection> sections)
    {
        var shown = sections.Where(s => s.IsShown).ToList();
        foreach (var section in sections)
        {
            section.CanMoveUp = shown.IndexOf(section) > 0;
            section.CanMoveDown = shown.IndexOf(section) is >= 0 and var i && i < shown.Count - 1;
        }
    }

    /// <summary>Hides or shows a section that depends on content (open bills), and fixes what can move where.</summary>
    private void SetSectionShown(PageSection section, bool shown)
    {
        if (section.IsShown == shown)
            return;
        section.IsShown = shown;
        UpdateMoves(InsuranceSections);
    }

    private async void SavePageLayout(string page, List<PageSection> sections)
    {
        _pageLayouts[page] = new SavedLayout(sections.Select(s => s.Key).ToList(), sections.Where(s => !s.IsOpen).Select(s => s.Key).ToList());
        try
        {
            await repository.SetPageLayoutAsync(JsonSerializer.Serialize(_pageLayouts, LayoutJson));
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }
}
