using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Keystone.ViewModels;

namespace Keystone.Views.Pages;

public partial class OverviewPage : UserControl
{
    private MainViewModel? _viewModel;

    public OverviewPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = DataContext as MainViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ArrangeSections();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.OverviewSectionOrder) or "")
            ArrangeSections();
    }

    /// <summary>Puts the cards (each tagged with its section key) in the owner's order.</summary>
    private void ArrangeSections()
    {
        if (_viewModel is null)
            return;
        var order = _viewModel.OverviewSectionOrder.ToList();
        var cards = Sections.Children.Where(c => c.Tag is string).OrderBy(c => order.IndexOf((string)c.Tag!)).ToList();
        var first = Sections.Children.Count - cards.Count;
        for (var i = 0; i < cards.Count; i++)
        {
            var at = Sections.Children.IndexOf(cards[i]);
            if (at != first + i)
                Sections.Children.Move(at, first + i);
        }
    }
}
