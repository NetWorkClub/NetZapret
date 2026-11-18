using Avalonia.Controls;
using NetZapret.UI.ViewModels;

namespace NetZapret.UI.Views;

public partial class BenchmarkView : UserControl
{
    private MainWindowViewModel? _vm;

    public BenchmarkView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.RequestOpenTargetsWindow -= OnOpenTargetsWindow;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
            _vm.RequestOpenTargetsWindow += OnOpenTargetsWindow;
    }

    private async void OnOpenTargetsWindow()
    {
        if (_vm is null)
            return;

        var win = new TargetsEditorWindow { DataContext = _vm };
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
    }
}