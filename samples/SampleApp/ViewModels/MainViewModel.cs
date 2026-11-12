using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SampleApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>The window title.</summary>
    [ObservableProperty]
    private string _title = "Sample";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public ItemViewModel? Selected { get; set; }

    public Task<string> Greeting => Task.FromResult("hi");

    [RelayCommand]
    private void Save()
    {
    }

    public void Reset()
    {
    }
}

public partial class ItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private decimal _price;
}
