using Keryhe.Telemetry.Client.Services;

namespace Keryhe.Telemetry.Client.Services.State;

public class TracesPageState(LocalStorageService storage)
{
    private const string StorageKey = "state.traces";

    public int ActiveTabIndex { get; set; } = 0;
    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public string FilterMode { get; set; } = "all";
    public int MinDurationMs { get; set; } = 500;
    public string? SelectedAnalyticsService { get; set; }

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<Dto>(StorageKey);
        if (dto is null) return;
        ActiveTabIndex = dto.ActiveTabIndex;
        SearchText = dto.SearchText;
        SelectedService = dto.SelectedService;
        FilterMode = dto.FilterMode;
        MinDurationMs = dto.MinDurationMs;
        SelectedAnalyticsService = dto.SelectedAnalyticsService;
    }

    public Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new Dto(ActiveTabIndex, SearchText, SelectedService, FilterMode, MinDurationMs, SelectedAnalyticsService));

    private record Dto(int ActiveTabIndex, string SearchText, string? SelectedService, string FilterMode, int MinDurationMs, string? SelectedAnalyticsService);
}
