using Avalonia.Controls;
using Avalonia.Input;
using NetZapret.UI.ViewModels;

namespace NetZapret.UI.Views;

public partial class DashboardView : UserControl
{
    private MainWindowViewModel? _vm;

    public DashboardView()
    {
        InitializeComponent();
        StrategiesList.DoubleTapped += OnStrategyDoubleTapped;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.RequestOpenStrategyWindow -= OnOpenStrategyWindow;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
            _vm.RequestOpenStrategyWindow += OnOpenStrategyWindow;
    }

    private void OnStrategyDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.SelectedStrategy is not null)
            _vm.OpenStrategyDetailsCommand.Execute(_vm.SelectedStrategy);
    }

    private async void OnOpenStrategyWindow()
    {
        if (_vm is null)
            return;

        var win = new StrategyDetailsWindow { DataContext = _vm };
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }
}