using Avalonia.Controls;
using Avalonia.Interactivity;
using NetZapret.UI.ViewModels;

namespace NetZapret.UI.Views;

public partial class StrategyDetailsWindow : Window
{
    private MainWindowViewModel? _vm;

    public StrategyDetailsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.RequestCloseStrategyWindow -= OnRequestClose;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
            _vm.RequestCloseStrategyWindow += OnRequestClose;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null)
            _vm.RequestCloseStrategyWindow -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose() => Close();
}