using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetZapret.Core.Models;
using NetZapret.Core.Services;
using NetZapret.Platform;
using NetZapret.UI.Services;

namespace NetZapret.UI.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IPlatformBackend _backend;
    private readonly string _assetsPath;
    private StrategyManifest _manifest = new();
    private AppSettings _appSettings = new();
    private CancellationTokenSource? _benchmarkCts;
    private bool _isInitializing = true;
    private string _benchmarkSortColumn = "best";
    private bool _benchmarkSortDescending = true;

    public event Action<bool>? BypassStateChanged;
    public event Action? RequestExit;
    public event Action? RequestOpenTargetsWindow;
    public event Action? RequestCloseTargetsWindow;
    public event Action? RequestOpenStrategyWindow;
    public event Action? RequestCloseStrategyWindow;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isBypassRunning;

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private bool _isServiceRunning;

    [ObservableProperty]
    private string _statusText = "Остановлен";

    [ObservableProperty]
    private string _driverStatusText = "—";

    [ObservableProperty]
    private string _activeServiceStrategyText = "Служба не установлена";

    [ObservableProperty]
    private string _elevationStatusText = "";

    [ObservableProperty]
    private string _platformLabel = OperatingSystem.IsWindows() ? "Windows (WinDivert)" : "Linux (NFQUEUE / nftables)";

    [ObservableProperty]
    private string _activityMessage = "Готов к работе.";

    [ObservableProperty]
    private StrategyItemVm? _selectedStrategy;

    [ObservableProperty]
    private int _selectedBenchmarkModeIndex;

    [ObservableProperty]
    private bool _isBenchmarkRunning;

    [ObservableProperty]
    private double _benchmarkProgressPercent;

    [ObservableProperty]
    private string _benchmarkStatusText = "Выберите режим и запустите проверку стратегий.";

    [ObservableProperty]
    private string? _bestStrategyId;

    [ObservableProperty]
    private string _bestStrategySummary = "";

    [ObservableProperty]
    private string _lastReportPath = "";

    [ObservableProperty]
    private string _newTargetName = "";

    [ObservableProperty]
    private string _newTargetValue = "";

    [ObservableProperty]
    private int _editorTabIndex;

    [ObservableProperty]
    private string _editorTitle = "Конструктор стратегии";

    [ObservableProperty]
    private string _editorSaveButtonText = "Сохранить стратегию";

    [ObservableProperty]
    private bool _editorCanDelete;

    [ObservableProperty]
    private BenchmarkRowVm? _editorTargetLastBenchmark;

    [ObservableProperty]
    private bool _editorIsNew = true;

    [ObservableProperty]
    private string _editorStrategyId = "";

    [ObservableProperty]
    private string _editorName = "";

    [ObservableProperty]
    private string _editorDescription = "";

    [ObservableProperty]
    private string _editorCategory = "custom";

    [ObservableProperty]
    private string _editorWebArgs = "";

    [ObservableProperty]
    private string _editorGoogleArgs = "";

    [ObservableProperty]
    private string _editorDiscordMediaArgs = "";

    [ObservableProperty]
    private int _editorQuicRepeats = 6;

    [ObservableProperty]
    private string _editorGameTcpCutoff = "n3";

    [ObservableProperty]
    private string _editorError = "";

    [ObservableProperty]
    private int _selectedGameFilterIndex;

    [ObservableProperty]
    private string _gameTcpPorts = "1024-65535";

    [ObservableProperty]
    private string _gameUdpPorts = "1024-65535";

    [ObservableProperty]
    private int _selectedIpsetIndex;

    [ObservableProperty]
    private string _ipsetModeText = "none";

    [ObservableProperty]
    private FakeOptionVm? _selectedDiscordFake;

    [ObservableProperty]
    private FakeOptionVm? _selectedGameFake;

    [ObservableProperty]
    private string _activeDiscordFakeName = "—";

    [ObservableProperty]
    private string _activeGameFakeName = "—";

    [ObservableProperty]
    private string _listGeneralUserText = "";

    [ObservableProperty]
    private string _listExcludeUserText = "";

    [ObservableProperty]
    private string _ipsetExcludeUserText = "";

    [ObservableProperty]
    private string _currentVersionText = UpdateService.DefaultLocalVersion;

    [ObservableProperty]
    private string _updateStatusText = "База списков не проверялась";

    [ObservableProperty]
    private string _updateReleaseUrl = UpdateService.ReleasesLatestUrl;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _autoCheckUpdates = true;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _closeToTray = true;


    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public string AppVersionText => UpdateService.AppVersion;

    public bool IsDashboardTab => SelectedTabIndex == 0;
    public bool IsBenchmarkTab => SelectedTabIndex == 1;
    public bool IsFiltersTab => SelectedTabIndex == 2;
    public bool IsDiagnosticsTab => SelectedTabIndex == 3;

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool CanRestartElevated => OperatingSystem.IsWindows() && !_backend.IsElevated;
    public bool HasBestStrategy => !string.IsNullOrEmpty(BestStrategyId);
    public bool HasLastReport => !string.IsNullOrEmpty(LastReportPath) && File.Exists(LastReportPath);
    public bool EditorHasLastBenchmark => EditorTargetLastBenchmark is not null;

    public string ToggleBypassButtonText => IsBypassRunning ? "Остановить обход" : "Запустить обход";
    public string TrayToggleText => IsBypassRunning ? "Остановить обход" : "Запустить обход";

    public ObservableCollection<StrategyItemVm> Strategies { get; } = [];
    public ObservableCollection<BenchmarkRowVm> BenchmarkResults { get; } = [];
    public ObservableCollection<TargetItemVm> TestTargets { get; } = [];
    public ObservableCollection<DiagnosticItemVm> DiagnosticItems { get; } = [];
    public ObservableCollection<FakeOptionVm> AvailableFakes { get; } = [];

    public MainWindowViewModel() : this(PlatformFactory.Create(), AssetPathResolver.ResolveAssetsPath())
    {
    }

    public MainWindowViewModel(IPlatformBackend backend, string assetsPath)
    {
        _backend = backend;
        _assetsPath = assetsPath;
        ElevationStatusText = _backend.IsElevated
            ? (OperatingSystem.IsWindows() ? "Права администратора" : "Права root")
            : (OperatingSystem.IsWindows() ? "Обычный пользователь (для драйвера нужны права админа)" : "Обычный пользователь (повышение через pkexec)");
    }

    public async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            ListManager.EnsureUserListsExist(_assetsPath);
            await ReloadStrategiesAsync();
            _appSettings = await StrategyStore.LoadSettingsAsync(_assetsPath);
            await LoadLastBenchmarkAsync();

            var savedStrategy = StrategyStore.FindStrategy(_manifest, _appSettings.SelectedStrategyId);
            SelectedStrategy = Strategies.FirstOrDefault(x => x.Id == savedStrategy?.Id) ?? Strategies.FirstOrDefault();

            SelectedGameFilterIndex = (int)_appSettings.GameFilter;
            GameTcpPorts = _appSettings.GameTcpPorts;
            GameUdpPorts = _appSettings.GameUdpPorts;
            AutoCheckUpdates = _appSettings.AutoCheckUpdates;
            StartMinimized = _appSettings.StartMinimized;
            CloseToTray = _appSettings.CloseToTray;
            IsSidebarCollapsed = _appSettings.IsSidebarCollapsed;
            CurrentVersionText = UpdateService.GetLocalVersion(_assetsPath);

            LoadTestTargets();
            RefreshIpsetState();
            await LoadFakesAsync();
            await LoadUserListsAsync();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ActivityMessage = $"Ошибка инициализации: {ex.Message}";
        }
        finally
        {
            _isInitializing = false;
        }

        if (AutoCheckUpdates)
            _ = CheckUpdatesCoreAsync(silent: true);
    }

    public async Task ShutdownAsync()
    {
        try
        {
            _benchmarkCts?.Cancel();
            var status = await _backend.GetStatusAsync();
            if (!status.IsServiceRunning && status.IsBypassRunning)
                await _backend.StopBypassAsync();

            if (_backend is ISessionPlatformBackend sessionBackend)
                await sessionBackend.EndSessionAsync();
        }
        catch
        {
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDashboardTab));
        OnPropertyChanged(nameof(IsBenchmarkTab));
        OnPropertyChanged(nameof(IsFiltersTab));
        OnPropertyChanged(nameof(IsDiagnosticsTab));
    }

    partial void OnIsBypassRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleBypassButtonText));
        OnPropertyChanged(nameof(TrayToggleText));
        BypassStateChanged?.Invoke(value);
    }

    partial void OnSelectedStrategyChanged(StrategyItemVm? value)
    {
        if (_isInitializing || value is null)
            return;

        _appSettings.SelectedStrategyId = value.Id;
        _ = StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_isInitializing) return;
        _appSettings.AutoCheckUpdates = value;
        _ = StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_isInitializing) return;
        _appSettings.StartMinimized = value;
        _ = StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_isInitializing) return;
        _appSettings.CloseToTray = value;
        _ = StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        if (_isInitializing) return;
        _appSettings.IsSidebarCollapsed = value;
        _ = StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    partial void OnBestStrategyIdChanged(string? value) => OnPropertyChanged(nameof(HasBestStrategy));
    partial void OnLastReportPathChanged(string value) => OnPropertyChanged(nameof(HasLastReport));
    partial void OnEditorTargetLastBenchmarkChanged(BenchmarkRowVm? value) => OnPropertyChanged(nameof(EditorHasLastBenchmark));

    [RelayCommand]
    private void SelectTab(string indexText)
    {
        if (int.TryParse(indexText, out var idx))
            SelectedTabIndex = idx;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        var st = await _backend.GetStatusAsync();
        IsBypassRunning = st.IsBypassRunning;
        IsServiceInstalled = st.IsServiceInstalled;
        IsServiceRunning = st.IsServiceRunning;
        DriverStatusText = string.IsNullOrWhiteSpace(st.DriverStatus) ? "—" : st.DriverStatus;

        if (st.IsServiceRunning)
            StatusText = "Работает (служба)";
        else if (st.IsBypassRunning)
            StatusText = "Работает";
        else
            StatusText = "Остановлен";

        ActiveServiceStrategyText = st.IsServiceInstalled
            ? $"Служба: {st.ActiveStrategyName ?? "установлена"}"
            : "Служба не установлена";
    }

    [RelayCommand]
    private async Task ToggleBypassAsync()
    {
        if (IsBusy || IsBenchmarkRunning)
            return;

        IsBusy = true;
        try
        {
            if (IsBypassRunning)
            {
                if (IsServiceRunning)
                {
                    ActivityMessage = "Обход запущен как системная служба. Остановите или удалите службу.";
                    return;
                }

                await _backend.StopBypassAsync();
                ActivityMessage = "Обход остановлен.";
            }
            else
            {
                if (SelectedStrategy is null)
                {
                    ActivityMessage = "Выберите стратегию.";
                    return;
                }

                if (!TryBuildLaunchSettings(out var launchSettings, out var portErr))
                {
                    ActivityMessage = portErr;
                    return;
                }

                var (ok, msg) = await _backend.StartBypassAsync(_manifest, SelectedStrategy.Model, launchSettings);
                ActivityMessage = msg;
                if (!ok)
                    await _backend.StopBypassAsync();
            }

            await RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        if (IsBusy || IsBenchmarkRunning || SelectedStrategy is null)
            return;

        if (!TryBuildLaunchSettings(out var launchSettings, out var portErr))
        {
            ActivityMessage = portErr;
            return;
        }

        IsBusy = true;
        try
        {
            ActivityMessage = "Установка службы...";
            var (_, msg) = await _backend.InstallServiceAsync(_manifest, SelectedStrategy.Model, launchSettings);
            ActivityMessage = msg;
            await RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveServiceAsync()
    {
        if (IsBusy || IsBenchmarkRunning)
            return;

        IsBusy = true;
        try
        {
            ActivityMessage = "Удаление службы...";
            var (_, msg) = await _backend.RemoveServiceAsync();
            ActivityMessage = msg;
            await RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            RequestExit?.Invoke();
        }
        catch (Exception ex)
        {
            ActivityMessage = $"Не удалось повысить права: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenNewStrategyEditor()
    {
        EditorIsNew = true;
        EditorStrategyId = $"custom-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        EditorTitle = "Новая стратегия";
        EditorSaveButtonText = "Создать стратегию";
        EditorCanDelete = false;
        EditorTargetLastBenchmark = null;
        EditorTabIndex = 1;
        EditorName = "Моя стратегия";
        EditorDescription = "Пользовательская стратегия DPI-обхода.";
        EditorCategory = "custom";
        EditorWebArgs = "--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={FAKES}tls_clienthello_www_google_com.bin";
        EditorGoogleArgs = "";
        EditorDiscordMediaArgs = "";
        EditorQuicRepeats = 6;
        EditorGameTcpCutoff = "n3";
        EditorError = "";
        RequestOpenStrategyWindow?.Invoke();
    }

    [RelayCommand]
    private void OpenStrategyDetails(StrategyItemVm? item)
    {
        var target = item ?? SelectedStrategy;
        if (target is null)
            return;

        SelectedStrategy = target;
        var src = target.Model;

        if (target.IsCustom)
        {
            EditorIsNew = false;
            EditorStrategyId = src.Id;
            EditorSaveButtonText = "Сохранить изменения";
            EditorCanDelete = true;
            EditorName = src.Name;
        }
        else
        {
            EditorIsNew = true;
            EditorStrategyId = $"custom-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            EditorSaveButtonText = "Сохранить как свою копию";
            EditorCanDelete = false;
            EditorName = $"{src.Name} (Моя)";
        }

        EditorTitle = target.Name;
        EditorDescription = src.Description;
        EditorCategory = target.IsCustom ? src.Category : "custom";
        EditorWebArgs = src.Web ?? src.GlobalIpv4Mode ?? "";
        EditorGoogleArgs = src.Google ?? "";
        EditorDiscordMediaArgs = src.DiscordMedia ?? "";
        EditorQuicRepeats = src.Quic?.Repeats ?? 6;
        EditorGameTcpCutoff = src.GameTcpCutoff ?? "n3";
        EditorError = "";
        EditorTargetLastBenchmark = target.LastBenchmark;
        EditorTabIndex = target.LastBenchmark is not null ? 0 : 1;

        RequestOpenStrategyWindow?.Invoke();
    }

    [RelayCommand]
    private async Task SaveCustomStrategyAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            EditorError = "Укажите название стратегии.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorWebArgs))
        {
            EditorError = "Поле Web-аргументов (--dpi-desync=...) не должно быть пустым.";
            return;
        }

        var def = new StrategyDefinition
        {
            Id = string.IsNullOrWhiteSpace(EditorStrategyId)
                ? $"custom-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
                : EditorStrategyId.Trim(),
            Name = EditorName.Trim(),
            LegacyName = "CUSTOM",
            Category = string.IsNullOrWhiteSpace(EditorCategory) ? "custom" : EditorCategory.Trim(),
            Description = EditorDescription.Trim(),
            IsCustom = true,
            Web = EditorWebArgs.Trim(),
            Google = string.IsNullOrWhiteSpace(EditorGoogleArgs) ? null : EditorGoogleArgs.Trim(),
            DiscordMedia = string.IsNullOrWhiteSpace(EditorDiscordMediaArgs) ? null : EditorDiscordMediaArgs.Trim(),
            Quic = new QuicRule { Repeats = Math.Clamp(EditorQuicRepeats, 1, 20) },
            GameTcpCutoff = string.IsNullOrWhiteSpace(EditorGameTcpCutoff) ? "n3" : EditorGameTcpCutoff.Trim()
        };

        await StrategyStore.SaveCustomStrategyAsync(_assetsPath, def);
        await ReloadStrategiesAsync();
        await LoadLastBenchmarkAsync();
        SelectedStrategy = Strategies.FirstOrDefault(s => s.Id == def.Id) ?? SelectedStrategy;
        ActivityMessage = $"Стратегия \"{def.Name}\" сохранена.";
        RequestCloseStrategyWindow?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteCustomStrategyFromEditorAsync()
    {
        if (EditorIsNew || string.IsNullOrWhiteSpace(EditorStrategyId))
            return;

        await StrategyStore.DeleteCustomStrategyAsync(_assetsPath, EditorStrategyId);
        await ReloadStrategiesAsync();
        await LoadLastBenchmarkAsync();
        SelectedStrategy = Strategies.FirstOrDefault();
        ActivityMessage = $"Стратегия \"{EditorName}\" удалена.";
        RequestCloseStrategyWindow?.Invoke();
    }

    [RelayCommand]
    private void SelectAllStrategiesForTest()
    {
        foreach (var s in Strategies)
            s.IsSelectedForTest = true;
    }

    [RelayCommand]
    private void UnselectAllStrategiesForTest()
    {
        foreach (var s in Strategies)
            s.IsSelectedForTest = false;
    }

    [RelayCommand]
    private void OpenTargetsEditor() => RequestOpenTargetsWindow?.Invoke();

    [RelayCommand]
    private void AddTestTarget()
    {
        var name = NewTargetName.Trim();
        var val = NewTargetValue.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(val))
            return;

        if (!val.StartsWith("PING:", StringComparison.OrdinalIgnoreCase) &&
            !val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            val = "https://" + val;
        }

        TestTargets.Add(new TargetItemVm(name, val));
        NewTargetName = "";
        NewTargetValue = "";
    }

    [RelayCommand]
    private void RemoveTestTarget(TargetItemVm? item)
    {
        if (item is not null)
            TestTargets.Remove(item);
    }

    [RelayCommand]
    private void SaveTestTargets()
    {
        var list = TestTargets
            .Where(t => !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.Value))
            .Select(t =>
            {
                var v = t.Value.Trim();
                if (v.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                    return new TargetItem(t.Name.Trim(), null, v["PING:".Length..].Trim());

                if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    v = "https://" + v;

                var host = v.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);
                var slash = host.IndexOf('/');
                if (slash >= 0) host = host[..slash];
                return new TargetItem(t.Name.Trim(), v, host);
            })
            .ToList();

        var targetsPath = Path.Combine(_assetsPath, "utils", "targets.txt");
        BenchmarkEngine.SaveTargets(targetsPath, list);
        LoadTestTargets();
        BenchmarkStatusText = $"Цели проверки сохранены ({list.Count} шт.).";
        RequestCloseTargetsWindow?.Invoke();
    }

    [RelayCommand]
    private void ReloadTestTargets() => LoadTestTargets();

    [RelayCommand]
    private void SortBenchmark(string column)
    {
        if (string.Equals(_benchmarkSortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            _benchmarkSortDescending = !_benchmarkSortDescending;
        }
        else
        {
            _benchmarkSortColumn = column;
            _benchmarkSortDescending = column is "best" or "ok";
        }

        ApplyBenchmarkSorting();
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsBenchmarkRunning || IsBusy)
            return;

        var selectedList = Strategies.Where(s => s.IsSelectedForTest).Select(s => s.Model).ToList();
        if (selectedList.Count == 0)
        {
            BenchmarkStatusText = "Отметьте хотя бы одну стратегию для проверки.";
            return;
        }

        if (!TryBuildLaunchSettings(out var launchSettings, out var portErr))
        {
            BenchmarkStatusText = portErr;
            return;
        }

        var mode = SelectedBenchmarkModeIndex switch
        {
            1 => BenchmarkMode.Quick,
            2 => BenchmarkMode.Dpi1620,
            _ => BenchmarkMode.Standard
        };

        BenchmarkResults.Clear();
        BestStrategyId = null;
        BestStrategySummary = "";
        LastReportPath = "";
        BenchmarkProgressPercent = 0;
        _benchmarkSortColumn = "best";
        _benchmarkSortDescending = true;
        IsBenchmarkRunning = true;
        _benchmarkCts = new CancellationTokenSource();

        var runCompleted = false;
        var progress = new InlineProgress(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (runCompleted)
                    return;

                BenchmarkStatusText = p.StageMessage;
                if (p.TotalCount > 0)
                    BenchmarkProgressPercent = (double)p.CurrentIndex / p.TotalCount * 100.0;

                if (p.CompletedDetail is { } detail)
                    BenchmarkResults.Add(BuildBenchmarkRow(detail, mode, isBest: false));
            });
        });

        try
        {
            var batch = await BenchmarkRunner.RunAsync(
                _backend,
                _manifest,
                selectedList,
                launchSettings,
                mode,
                progress,
                _benchmarkCts.Token);

            runCompleted = true;
            BenchmarkResults.Clear();

            var rowMap = new Dictionary<string, BenchmarkRowVm>(StringComparer.OrdinalIgnoreCase);
            var history = new SavedBenchmarkHistory
            {
                Mode = mode.ToString(),
                SavedAt = DateTimeOffset.Now,
                BestStrategyId = batch.BestStrategy?.StrategyId
            };

            foreach (var d in batch.Details)
            {
                var isBest = batch.BestStrategy is not null && batch.BestStrategy.StrategyId == d.Strategy.Id && d.Error is null;
                var row = BuildBenchmarkRow(d, mode, isBest);
                BenchmarkResults.Add(row);
                rowMap[row.StrategyId] = row;

                history.Entries.Add(new SavedStrategyBenchmark
                {
                    StrategyId = row.StrategyId,
                    StrategyName = row.StrategyName,
                    OkCount = row.OkCount,
                    FailCount = row.FailCount,
                    UnsupportedCount = row.UnsupportedCount,
                    BlockedCount = row.BlockedCount,
                    PingOkCount = row.PingOkCount,
                    AvgPingMs = row.AvgPingMs,
                    IsBest = row.IsBest,
                    Error = row.Error,
                    Targets = row.TargetDetails.Select(t => new SavedTargetCheck
                    {
                        TargetName = t.TargetName,
                        EndpointText = t.EndpointText,
                        Http11Status = t.Http11Status,
                        Tls12Status = t.Tls12Status,
                        Tls13Status = t.Tls13Status,
                        PingText = t.PingText,
                        HasFailure = t.HasFailure,
                        ErrorHint = t.ErrorHint
                    }).ToList()
                });
            }

            await BenchmarkHistoryStore.SaveAsync(_assetsPath, history);

            foreach (var s in Strategies)
            {
                if (rowMap.TryGetValue(s.Id, out var row))
                    s.LastBenchmark = row;
            }

            SortDashboardStrategies();
            ApplyBenchmarkSorting();

            LastReportPath = batch.ReportFilePath;
            if (batch.BestStrategy is { } best)
            {
                BestStrategyId = best.StrategyId;
                BestStrategySummary = $"Лучшая стратегия: {best.StrategyName} (OK: {best.OkCount}, ошибок: {best.FailCount})";
                BenchmarkStatusText = BestStrategySummary;
            }
            else
            {
                BenchmarkStatusText = "Тестирование завершено, рабочая стратегия не определена.";
            }
        }
        catch (OperationCanceledException)
        {
            runCompleted = true;
            BenchmarkStatusText = "Тестирование отменено пользователем.";
        }
        catch (Exception ex)
        {
            runCompleted = true;
            BenchmarkStatusText = $"Ошибка тестирования: {ex.Message}";
        }
        finally
        {
            IsBenchmarkRunning = false;
            _benchmarkCts?.Dispose();
            _benchmarkCts = null;
            RefreshIpsetState();
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private void CancelBenchmark()
    {
        _benchmarkCts?.Cancel();
        BenchmarkStatusText = "Остановка тестирования...";
    }

    [RelayCommand]
    private async Task ApplyBestStrategyAsync()
    {
        if (string.IsNullOrEmpty(BestStrategyId))
            return;

        var match = Strategies.FirstOrDefault(s => s.Id == BestStrategyId);
        if (match is null)
            return;

        SelectedStrategy = match;
        SelectedTabIndex = 0;

        if (!IsBypassRunning && !IsServiceRunning)
            await ToggleBypassAsync();
    }

    [RelayCommand]
    private void OpenLastReport()
    {
        if (HasLastReport)
            AssetPathResolver.OpenPath(LastReportPath);
    }

    [RelayCommand]
    private async Task SaveGameFilterAsync()
    {
        if (!ListManager.ValidatePortRanges(GameTcpPorts, out var normTcp))
        {
            ActivityMessage = "Некорректный формат TCP портов (пример: 1024-65535 или 80,443,3000-4000).";
            return;
        }

        if (!ListManager.ValidatePortRanges(GameUdpPorts, out var normUdp))
        {
            ActivityMessage = "Некорректный формат UDP портов.";
            return;
        }

        GameTcpPorts = normTcp;
        GameUdpPorts = normUdp;

        _appSettings.GameFilter = (GameFilterMode)SelectedGameFilterIndex;
        _appSettings.GameTcpPorts = normTcp;
        _appSettings.GameUdpPorts = normUdp;

        await StrategyStore.SaveSettingsAsync(_assetsPath, _appSettings);
        ActivityMessage = "Настройки Game Filter сохранены. Перезапустите обход/службу для применения.";
    }

    [RelayCommand]
    private void ApplyIpsetMode()
    {
        var target = SelectedIpsetIndex switch
        {
            1 => IpsetMode.None,
            2 => IpsetMode.Any,
            _ => IpsetMode.Loaded
        };

        var ok = ListManager.SetIpsetMode(_assetsPath, target);
        RefreshIpsetState();
        ActivityMessage = ok
            ? $"IPSet переключён в режим: {IpsetModeText}. Перезапустите обход для применения."
            : "Не найден backup для режима Loaded. Сначала обновите IPSet из репозитория.";
    }

    [RelayCommand]
    private async Task UpdateIpsetFromRepoAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ActivityMessage = "Загрузка актуального IPSet...";
            await ListManager.UpdateIpsetFromRepoAsync(_assetsPath);
            RefreshIpsetState();
            ActivityMessage = "Список IPSet обновлён и включён в режиме loaded.";
        }
        catch (Exception ex)
        {
            ActivityMessage = $"Ошибка загрузки IPSet: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllListsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ActivityMessage = "Обновление базовых списков (домены и IPSet) с GitHub...";
            var remoteVer = await ListManager.UpdateAllListsFromRepoAsync(_assetsPath);
            RefreshIpsetState();
            CurrentVersionText = UpdateService.GetLocalVersion(_assetsPath);
            IsUpdateAvailable = false;
            UpdateStatusText = string.IsNullOrWhiteSpace(remoteVer)
                ? $"Базовые списки обновлены (текущая база: {CurrentVersionText})"
                : $"База списков обновлена до {remoteVer}";
            ActivityMessage = "Все базовые списки обновлены (пользовательские *-user.txt сохранены). Перезапустите обход.";
        }
        catch (Exception ex)
        {
            ActivityMessage = $"Ошибка обновления списков: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyDiscordFakeAsync()
    {
        if (SelectedDiscordFake is null) return;
        ListManager.ReplaceActiveFake(_assetsPath, isDiscord: true, SelectedDiscordFake.FullPath);
        await LoadFakesAsync();
        ActivityMessage = $"Активный фейк Discord UDP заменён на {SelectedDiscordFake.Name}.";
    }

    [RelayCommand]
    private async Task ApplyGameFakeAsync()
    {
        if (SelectedGameFake is null) return;
        ListManager.ReplaceActiveFake(_assetsPath, isDiscord: false, SelectedGameFake.FullPath);
        await LoadFakesAsync();
        ActivityMessage = $"Активный фейк Game UDP заменён на {SelectedGameFake.Name}.";
    }

    [RelayCommand]
    private async Task SaveUserListsAsync()
    {
        var listsDir = Path.Combine(_assetsPath, "lists");
        Directory.CreateDirectory(listsDir);

        await File.WriteAllTextAsync(Path.Combine(listsDir, "list-general-user.txt"), ListGeneralUserText);
        await File.WriteAllTextAsync(Path.Combine(listsDir, "list-exclude-user.txt"), ListExcludeUserText);
        await File.WriteAllTextAsync(Path.Combine(listsDir, "ipset-exclude-user.txt"), IpsetExcludeUserText);

        ActivityMessage = "Пользовательские списки сохранены. Перезапустите обход для применения.";
    }

    [RelayCommand]
    private async Task ReloadUserListsAsync()
    {
        await LoadUserListsAsync();
        ActivityMessage = "Пользовательские списки перечитаны с диска.";
    }

    [RelayCommand]
    private void OpenAssetsFolder() => AssetPathResolver.OpenPath(_assetsPath);

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ActivityMessage = "Выполняется диагностика...";
            var items = await _backend.RunDiagnosticsAsync(_assetsPath);
            DiagnosticItems.Clear();
            foreach (var item in items)
                DiagnosticItems.Add(new DiagnosticItemVm(item.Title, item.Message, item.Severity, item.ActionId, item.ActionLabel));
            ActivityMessage = $"Диагностика завершена (проверок: {DiagnosticItems.Count}).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteDiagnosticActionAsync(DiagnosticItemVm? item)
    {
        if (item?.ActionId is null || IsBusy)
            return;

        IsBusy = true;
        try
        {
            var (_, msg) = await _backend.ExecuteDiagnosticActionAsync(item.ActionId, _assetsPath);
            ActivityMessage = msg;
        }
        finally
        {
            IsBusy = false;
        }

        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    private async Task ClearDiscordCacheAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var (_, msg) = await _backend.ClearDiscordCacheAsync();
            ActivityMessage = msg;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateHostsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ActivityMessage = "Проверка и обновление hosts...";
            var remote = await ListManager.FetchRemoteHostsAsync();
            var (_, msg) = await _backend.UpdateHostsAsync(remote);
            ActivityMessage = msg;
        }
        catch (Exception ex)
        {
            ActivityMessage = $"Ошибка обновления hosts: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync() => await CheckUpdatesCoreAsync(silent: false);

    [RelayCommand]
    private void OpenUpdatePage() => AssetPathResolver.OpenUrl(UpdateReleaseUrl);

    [RelayCommand]
    private void ExitApp() => RequestExit?.Invoke();

    private async Task ReloadStrategiesAsync()
    {
        var prevChecked = Strategies.ToDictionary(s => s.Id, s => s.IsSelectedForTest);
        _manifest = await StrategyStore.LoadManifestAsync(_assetsPath);
        Strategies.Clear();
        foreach (var s in _manifest.Strategies)
        {
            var vm = new StrategyItemVm(s);
            if (prevChecked.TryGetValue(s.Id, out var isChecked))
                vm.IsSelectedForTest = isChecked;
            Strategies.Add(vm);
        }
    }

    private async Task LoadLastBenchmarkAsync()
    {
        var history = await BenchmarkHistoryStore.LoadAsync(_assetsPath);
        if (history is null || history.Entries.Count == 0)
            return;

        BenchmarkResults.Clear();
        var map = new Dictionary<string, BenchmarkRowVm>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in history.Entries)
        {
            var pingText = e.PingOkCount == 0 ? "—" : $"{e.AvgPingMs:F0} мс";
            var details = e.Targets.Select(t => new BenchmarkTargetDetailVm(
                t.TargetName,
                t.EndpointText,
                t.Http11Status,
                t.Tls12Status,
                t.Tls13Status,
                t.PingText,
                t.HasFailure,
                t.ErrorHint)).ToList();

            var row = new BenchmarkRowVm(
                e.StrategyId,
                e.StrategyName,
                e.OkCount,
                e.FailCount,
                e.UnsupportedCount,
                e.BlockedCount,
                e.PingOkCount,
                pingText,
                e.IsBest,
                e.Error,
                e.AvgPingMs,
                details);

            BenchmarkResults.Add(row);
            map[e.StrategyId] = row;
        }

        foreach (var s in Strategies)
        {
            if (map.TryGetValue(s.Id, out var row))
                s.LastBenchmark = row;
        }

        SortDashboardStrategies();
        ApplyBenchmarkSorting();

        var bestRow = BenchmarkResults.FirstOrDefault(r => r.IsBest) ?? BenchmarkResults.FirstOrDefault(r => !r.HasError);
        if (bestRow is not null)
        {
            BestStrategyId = bestRow.StrategyId;
            BestStrategySummary = $"Последний тест ({history.SavedAt.LocalDateTime:dd.MM HH:mm}): лучшая — {bestRow.StrategyName} (OK: {bestRow.OkCount})";
            BenchmarkStatusText = BestStrategySummary;
        }
    }

    private void SortDashboardStrategies()
    {
        if (Strategies.Count <= 1)
            return;

        var currentId = SelectedStrategy?.Id;
        var sorted = Strategies
            .OrderByDescending(s => s.LastBenchmark is not null)
            .ThenByDescending(s => s.LastBenchmark is not null && !s.LastBenchmark.HasError)
            .ThenByDescending(s => s.IsLastBest)
            .ThenByDescending(s => s.LastBenchmark?.OkCount ?? -1)
            .ThenBy(s => s.LastBenchmark?.BlockedCount ?? int.MaxValue)
            .ThenBy(s => s.LastBenchmark?.FailCount ?? int.MaxValue)
            .ThenBy(s => s.LastBenchmark?.AvgPingMs ?? double.MaxValue)
            .ThenByDescending(s => s.Recommended)
            .ToList();

        Strategies.Clear();
        foreach (var s in sorted)
            Strategies.Add(s);

        if (!string.IsNullOrEmpty(currentId))
            SelectedStrategy = Strategies.FirstOrDefault(s => s.Id == currentId) ?? Strategies.FirstOrDefault();
    }

    private void LoadTestTargets()
    {
        var path = BenchmarkEngine.GetEffectiveTargetsPath(_assetsPath);
        var items = BenchmarkEngine.LoadTargets(path);
        TestTargets.Clear();
        foreach (var t in items)
        {
            var val = string.IsNullOrWhiteSpace(t.Url) ? $"PING:{t.PingHost}" : t.Url;
            TestTargets.Add(new TargetItemVm(t.Name, val));
        }
    }

    private void ApplyBenchmarkSorting()
    {
        if (BenchmarkResults.Count <= 1)
            return;

        IOrderedEnumerable<BenchmarkRowVm> ordered = _benchmarkSortColumn switch
        {
            "name" => _benchmarkSortDescending
                ? BenchmarkResults.OrderByDescending(r => r.StrategyName, StringComparer.OrdinalIgnoreCase)
                : BenchmarkResults.OrderBy(r => r.StrategyName, StringComparer.OrdinalIgnoreCase),
            "ok" => _benchmarkSortDescending
                ? BenchmarkResults.OrderByDescending(r => r.OkCount).ThenBy(r => r.FailCount)
                : BenchmarkResults.OrderBy(r => r.OkCount).ThenBy(r => r.FailCount),
            "err" => _benchmarkSortDescending
                ? BenchmarkResults.OrderByDescending(r => r.FailCount)
                : BenchmarkResults.OrderBy(r => r.FailCount),
            "blocked" => _benchmarkSortDescending
                ? BenchmarkResults.OrderByDescending(r => r.BlockedCount)
                : BenchmarkResults.OrderBy(r => r.BlockedCount),
            "ping" => _benchmarkSortDescending
                ? BenchmarkResults.OrderByDescending(r => r.AvgPingMs)
                : BenchmarkResults.OrderBy(r => r.AvgPingMs),
            _ => _benchmarkSortDescending
                ? BenchmarkResults
                    .OrderByDescending(r => !r.HasError)
                    .ThenByDescending(r => r.OkCount)
                    .ThenBy(r => r.BlockedCount)
                    .ThenBy(r => r.FailCount)
                    .ThenByDescending(r => r.PingOkCount)
                    .ThenBy(r => r.AvgPingMs)
                : BenchmarkResults
                    .OrderBy(r => !r.HasError)
                    .ThenBy(r => r.OkCount)
        };

        var sorted = ordered.ToList();
        BenchmarkResults.Clear();
        foreach (var row in sorted)
            BenchmarkResults.Add(row);
    }

    private static BenchmarkRowVm BuildBenchmarkRow(StrategyRunDetail detail, BenchmarkMode mode, bool isBest)
    {
        var s = detail.Summary;
        var pingText = mode == BenchmarkMode.Dpi1620 || s.PingOkCount == 0
            ? "—"
            : $"{s.AveragePingMs:F0} мс";

        var targetDetails = new List<BenchmarkTargetDetailVm>();

        if (detail.StandardResults is not null)
        {
            foreach (var r in detail.StandardResults)
            {
                var hasProbes = r.Probes.Count > 0;
                var h11 = hasProbes ? FormatProbe(r.Probes, ProbeType.Http11) : "—";
                var t12 = hasProbes ? FormatProbe(r.Probes, ProbeType.Tls12) : "—";
                var t13 = hasProbes ? FormatProbe(r.Probes, ProbeType.Tls13) : "—";
                var ping = r.PingMs is { } ms ? $"{ms} мс" : "Timeout";
                var failed = r.Probes.Any(p => p.Status is CheckStatus.Fail or CheckStatus.SslMismatch or CheckStatus.LikelyBlocked)
                             || (!hasProbes && r.PingMs is null);
                var hints = r.Probes
                    .Where(p => p.Status is CheckStatus.Fail or CheckStatus.SslMismatch or CheckStatus.LikelyBlocked && !string.IsNullOrWhiteSpace(p.Detail))
                    .Select(p => $"{p.Probe}: {p.Detail}")
                    .Distinct()
                    .ToList();

                targetDetails.Add(new BenchmarkTargetDetailVm(
                    r.Target.Name,
                    r.Target.Url ?? $"PING:{r.Target.PingHost}",
                    h11,
                    t12,
                    t13,
                    ping,
                    failed,
                    hints.Count > 0 ? string.Join("; ", hints) : null));
            }
        }
        else if (detail.DpiResults is not null)
        {
            foreach (var r in detail.DpiResults)
            {
                var h11 = FormatProbe(r.Probes, ProbeType.Http11);
                var t12 = FormatProbe(r.Probes, ProbeType.Tls12);
                var t13 = FormatProbe(r.Probes, ProbeType.Tls13);
                var status = r.HasFreezePattern ? "FREEZE 16-20KB" : "OK";
                var failed = r.HasFreezePattern || r.Probes.Any(p => p.Status == CheckStatus.Fail);
                var hints = r.Probes
                    .Where(p => p.Status is CheckStatus.Fail or CheckStatus.LikelyBlocked && !string.IsNullOrWhiteSpace(p.Detail))
                    .Select(p => $"{p.Probe}: {p.Detail}")
                    .Distinct()
                    .ToList();

                targetDetails.Add(new BenchmarkTargetDetailVm(
                    $"[{r.Target.Country}] {r.Target.Id}",
                    $"{r.Target.Provider} ({r.Target.Host})",
                    h11,
                    t12,
                    t13,
                    status,
                    failed,
                    hints.Count > 0 ? string.Join("; ", hints) : null));
            }
        }

        return new BenchmarkRowVm(
            s.StrategyId,
            s.StrategyName,
            s.OkCount,
            s.FailCount,
            s.UnsupportedCount,
            s.BlockedCount,
            s.PingOkCount,
            pingText,
            isBest,
            detail.Error,
            s.AveragePingMs,
            targetDetails);
    }

    private static string FormatProbe(IReadOnlyList<ProbeResult> probes, ProbeType type)
    {
        var p = probes.FirstOrDefault(x => x.Probe == type);
        if (p is null)
            return "—";

        return p.Status switch
        {
            CheckStatus.Ok => $"OK ({p.Detail})",
            CheckStatus.Unsupported => "UNSUP",
            CheckStatus.SslMismatch => "SSL",
            CheckStatus.LikelyBlocked => "BLOCKED",
            _ => "FAIL"
        };
    }

    private async Task CheckUpdatesCoreAsync(bool silent)
    {
        var res = await UpdateService.CheckForUpdatesAsync(_assetsPath);
        CurrentVersionText = res.CurrentVersion;
        UpdateReleaseUrl = res.ReleasePageUrl;

        if (!res.Success)
        {
            IsUpdateAvailable = false;
            UpdateStatusText = silent ? "Не удалось проверить базу списков" : $"Ошибка проверки: {res.Error}";
            return;
        }

        IsUpdateAvailable = res.IsUpdateAvailable;
        UpdateStatusText = res.IsUpdateAvailable
            ? $"Доступно обновление базы списков Flowseal: {res.LatestVersion} (нажмите «Обновить все списки»)"
            : $"База списков Flowseal актуальна ({res.CurrentVersion})";
    }

    private void RefreshIpsetState()
    {
        var mode = ListManager.GetIpsetMode(_assetsPath);
        SelectedIpsetIndex = mode switch
        {
            IpsetMode.None => 1,
            IpsetMode.Any => 2,
            _ => 0
        };
        IpsetModeText = mode.ToString().ToLowerInvariant();
    }

    private async Task LoadFakesAsync()
    {
        var state = await ListManager.GetActiveFakesAsync(_assetsPath);
        ActiveDiscordFakeName = state.DiscordFakeName;
        ActiveGameFakeName = state.GameFakeName;

        AvailableFakes.Clear();
        foreach (var f in state.AvailableFakes)
            AvailableFakes.Add(new FakeOptionVm(f.Name, f.FullPath));

        SelectedDiscordFake = AvailableFakes.FirstOrDefault(x => x.Name == state.DiscordFakeName) ?? AvailableFakes.FirstOrDefault();
        SelectedGameFake = AvailableFakes.FirstOrDefault(x => x.Name == state.GameFakeName) ?? AvailableFakes.FirstOrDefault();
    }

    private async Task LoadUserListsAsync()
    {
        ListManager.EnsureUserListsExist(_assetsPath);
        var listsDir = Path.Combine(_assetsPath, "lists");

        ListGeneralUserText = await File.ReadAllTextAsync(Path.Combine(listsDir, "list-general-user.txt"));
        ListExcludeUserText = await File.ReadAllTextAsync(Path.Combine(listsDir, "list-exclude-user.txt"));
        IpsetExcludeUserText = await File.ReadAllTextAsync(Path.Combine(listsDir, "ipset-exclude-user.txt"));
    }

    private bool TryBuildLaunchSettings(out LaunchSettings launchSettings, out string error)
    {
        error = string.Empty;
        if (!ListManager.ValidatePortRanges(GameTcpPorts, out var normTcp))
        {
            launchSettings = new LaunchSettings(_assetsPath);
            error = "Некорректный формат TCP портов Game Filter.";
            return false;
        }

        if (!ListManager.ValidatePortRanges(GameUdpPorts, out var normUdp))
        {
            launchSettings = new LaunchSettings(_assetsPath);
            error = "Некорректный формат UDP портов Game Filter.";
            return false;
        }

        launchSettings = new LaunchSettings(
            _assetsPath,
            (GameFilterMode)SelectedGameFilterIndex,
            normTcp,
            normUdp);
        return true;
    }

    private sealed class InlineProgress(Action<BenchmarkProgress> callback) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => callback(value);
    }
}