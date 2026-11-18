using Avalonia.Controls;
using NetZapret.UI.ViewModels;

namespace NetZapret.UI.Views;

public partial class TargetsEditorWindow : Window
{
    private MainWindowViewModel? _vm;

    public TargetsEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.RequestCloseTargetsWindow -= OnRequestClose;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
            _vm.RequestCloseTargetsWindow += OnRequestClose;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null)
            _vm.RequestCloseTargetsWindow -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose() => Close();
}