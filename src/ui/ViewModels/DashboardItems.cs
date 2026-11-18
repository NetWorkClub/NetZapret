using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetZapret.Core.Models;
using NetZapret.Platform;

namespace NetZapret.UI.ViewModels;

public sealed partial class StrategyItemVm : ObservableObject
{
    public StrategyDefinition Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string LegacyLabel => Model.IsCustom ? "CUSTOM" : (Model.LegacyName ?? Model.Id);
    public string Category => Model.Category;
    public string Description => Model.Description;
    public bool Recommended => Model.Recommended;
    public bool AdvancedWarning => Model.AdvancedWarning;
    public bool IsCustom => Model.IsCustom;

    [ObservableProperty]
    private bool _isSelectedForTest = true;

    [ObservableProperty]
    private BenchmarkRowVm? _lastBenchmark;

    public StrategyItemVm(StrategyDefinition model)
    {
        Model = model;
    }

    public string DisplayTitle => Recommended ? $"{Name} ★" : Name;
    public bool HasLastBenchmark => LastBenchmark is not null;
    public bool IsLastBest => LastBenchmark?.IsBest == true;

    public string LastBenchmarkSummary
    {
        get
        {
            if (LastBenchmark is null)
                return string.Empty;
            if (LastBenchmark.HasError)
                return "Последний тест: ошибка";
            return $"OK: {LastBenchmark.OkCount} · ERR: {LastBenchmark.FailCount} · {LastBenchmark.AvgPingText}";
        }
    }

    partial void OnLastBenchmarkChanged(BenchmarkRowVm? value)
    {
        OnPropertyChanged(nameof(HasLastBenchmark));
        OnPropertyChanged(nameof(IsLastBest));
        OnPropertyChanged(nameof(LastBenchmarkSummary));
    }
}

public sealed record BenchmarkTargetDetailVm(
    string TargetName,
    string EndpointText,
    string Http11Status,
    string Tls12Status,
    string Tls13Status,
    string PingText,
    bool HasFailure,
    string? ErrorHint
);

public sealed partial class BenchmarkRowVm : ObservableObject
{
    public string StrategyId { get; }
    public string StrategyName { get; }
    public int OkCount { get; }
    public int FailCount { get; }
    public int UnsupportedCount { get; }
    public int BlockedCount { get; }
    public int PingOkCount { get; }
    public double AvgPingMs { get; }
    public string AvgPingText { get; }
    public bool IsBest { get; }
    public string? Error { get; }
    public IReadOnlyList<BenchmarkTargetDetailVm> TargetDetails { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public BenchmarkRowVm(
        string strategyId,
        string strategyName,
        int okCount,
        int failCount,
        int unsupportedCount,
        int blockedCount,
        int pingOkCount,
        string avgPingText,
        bool isBest,
        string? error,
        double avgPingMs = 9999,
        IReadOnlyList<BenchmarkTargetDetailVm>? targetDetails = null)
    {
        StrategyId = strategyId;
        StrategyName = strategyName;
        OkCount = okCount;
        FailCount = failCount;
        UnsupportedCount = unsupportedCount;
        BlockedCount = blockedCount;
        PingOkCount = pingOkCount;
        AvgPingMs = avgPingMs;
        AvgPingText = avgPingText;
        IsBest = isBest;
        Error = error;
        TargetDetails = targetDetails ?? [];
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasDetails => TargetDetails.Count > 0 || HasError;
    public string ExpandButtonText => IsExpanded ? "Скрыть ▲" : "Детали ▼";
    public string StatusBadge => Error is not null
        ? "Ошибка"
        : IsBest
            ? "Лучшая ★"
            : "Готово";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandButtonText));

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

public sealed partial class TargetItemVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _value = "";

    public TargetItemVm()
    {
    }

    public TargetItemVm(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

public sealed record DiagnosticItemVm(
    string Title,
    string Message,
    DiagnosticSeverity Severity,
    string? ActionId,
    string? ActionLabel
)
{
    public bool HasAction => !string.IsNullOrEmpty(ActionId);
    public string SeverityBadge => Severity switch
    {
        DiagnosticSeverity.Ok => "OK",
        DiagnosticSeverity.Warning => "ВНИМАНИЕ",
        DiagnosticSeverity.Error => "ОШИБКА",
        _ => ""
    };
}

public sealed record FakeOptionVm(string Name, string FullPath);