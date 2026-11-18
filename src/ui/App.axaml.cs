using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NetZapret.UI.Services;
using NetZapret.UI.ViewModels;
using NetZapret.UI.Views;

namespace NetZapret.UI;

public partial class App : Application
{
    private MainWindowViewModel? _mainVm;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayToggleItem;
    private bool _isShuttingDown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainVm = new MainWindowViewModel();
            _mainWindow = new MainWindow
            {
                DataContext = _mainVm,
                Icon = AppIconBuilder.CreateShieldIcon(false)
            };

            SetupTrayIcon(desktop);

            _mainVm.BypassStateChanged += active =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var icon = AppIconBuilder.CreateShieldIcon(active);
                    if (_mainWindow is not null)
                        _mainWindow.Icon = icon;
                    if (_trayIcon is not null)
                    {
                        _trayIcon.Icon = icon;
                        _trayIcon.ToolTipText = active ? "NetZapret — активен" : "NetZapret — остановлен";
                    }
                    if (_trayToggleItem is not null)
                        _trayToggleItem.Header = active ? "Остановить обход" : "Запустить обход";
                });
            };

            _mainVm.RequestExit += () => ShutdownApplication(desktop);

            _mainWindow.Closing += (_, e) =>
            {
                if (_isShuttingDown)
                    return;

                if (_mainVm.StartMinimized)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                }
            };

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (!_isShuttingDown)
                {
                    _isShuttingDown = true;
                    await _mainVm.ShutdownAsync();
                }
            };

            desktop.MainWindow = _mainWindow;
            Dispatcher.UIThread.Post(async () =>
            {
                await _mainVm.InitializeAsync();
                if (_mainVm.StartMinimized && _mainWindow is not null)
                    _mainWindow.Hide();
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openItem = new NativeMenuItem("Открыть NetZapret");
        openItem.Click += (_, _) => ShowMainWindow();

        _trayToggleItem = new NativeMenuItem("Запустить обход");
        _trayToggleItem.Click += (_, _) =>
        {
            if (_mainVm is not null && _mainVm.ToggleBypassCommand.CanExecute(null))
                _mainVm.ToggleBypassCommand.Execute(null);
        };

        var exitItem = new NativeMenuItem("Выход");
        exitItem.Click += (_, _) => ShutdownApplication(desktop);

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = AppIconBuilder.CreateShieldIcon(false),
            ToolTipText = "NetZapret — остановлен",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
    }

    private async void ShutdownApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        if (_mainVm is not null)
            await _mainVm.ShutdownAsync();

        _trayIcon?.Dispose();
        desktop.Shutdown();
    }
}