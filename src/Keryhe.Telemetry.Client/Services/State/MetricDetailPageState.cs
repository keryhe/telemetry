using Keryhe.Telemetry.Client.Services;

namespace Keryhe.Telemetry.Client.Services.State;

public class MetricDetailPageState(LocalStorageService storage)
{
    private const string StorageKey = "state.metricDetail";

    public string? CurrentMetricName { get; set; }
    public bool AutoRefresh { get; set; } = false;
    public string? SelectedServiceFilter { get; set; }
    public Dictionary<string, string> SelectedLabelFilters { get; set; } = new();
    public bool ShowPerServiceView { get; set; } = false;
    public bool ShowRaw { get; set; } = false;
    public int ActiveTab { get; set; } = 0;

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<Dto>(StorageKey);
        if (dto is null) return;
        CurrentMetricName = dto.CurrentMetricName;
        SelectedServiceFilter = dto.SelectedServiceFilter;
        SelectedLabelFilters = dto.SelectedLabelFilters ?? new();
        ShowPerServiceView = dto.ShowPerServiceView;
        ShowRaw = dto.ShowRaw;
        ActiveTab = dto.ActiveTab;
        // AutoRefresh intentionally not restored — defaults to false on reload
    }

    public Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new Dto(CurrentMetricName, SelectedServiceFilter, SelectedLabelFilters, ShowPerServiceView, ShowRaw, ActiveTab));

    private record Dto(string? CurrentMetricName, string? SelectedServiceFilter, Dictionary<string, string>? SelectedLabelFilters, bool ShowPerServiceView, bool ShowRaw, int ActiveTab);
}
