using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>The Insurance page's sections can be moved and folded, and stay that way.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class PageLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-layout-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public PageLayoutTests()
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

    [Fact]
    public async Task SectionsMoveAndFoldAndAreRemembered()
    {
        var vm = await LoadAsync();
        Assert.Equal(["bills", "health", "policies", "cover", "premiums", "papers"], vm.InsuranceSectionOrder);

        // No open bills: that section is hidden, so Health costs is first and can't move further up.
        Assert.False(vm.HealthSection.CanMoveUp);
        Assert.True(vm.PoliciesSection.CanMoveUp);

        vm.PapersSection.MoveUpCommand.Execute(null);
        vm.PapersSection.MoveUpCommand.Execute(null);
        vm.PoliciesSection.MoveDownCommand.Execute(null);
        Assert.Equal(["bills", "health", "papers", "policies", "cover", "premiums"], vm.InsuranceSectionOrder);
        Assert.False(vm.PremiumsSection.CanMoveDown);

        vm.CoverSection.ToggleCommand.Execute(null);
        Assert.False(vm.CoverSection.IsOpen);
        Assert.Equal("Expand", vm.CoverSection.ToggleTip);

        await Task.Delay(200); // saved in the background
        var again = await LoadAsync();
        Assert.Equal(["bills", "health", "papers", "policies", "cover", "premiums"], again.InsuranceSectionOrder);
        Assert.False(again.CoverSection.IsOpen);
        Assert.True(again.PoliciesSection.IsOpen);
    }

    [Fact]
    public async Task ADamagedLayoutFallsBackToTheBuiltInOne()
    {
        await _repository.SetPageLayoutAsync("{not json");
        var vm = await LoadAsync();
        Assert.Equal(["bills", "health", "policies", "cover", "premiums", "papers"], vm.InsuranceSectionOrder);
    }
}
