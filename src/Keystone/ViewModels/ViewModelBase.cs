using CommunityToolkit.Mvvm.ComponentModel;

namespace Keystone.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>After the language changed: every text the view shows is read again.</summary>
    internal virtual void RefreshTexts() => OnPropertyChanged(string.Empty);
}
