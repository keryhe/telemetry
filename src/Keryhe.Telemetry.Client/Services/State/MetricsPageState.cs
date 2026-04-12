using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services.State;

public class MetricsPageState(LocalStorageService storage)
{
    private const string StorageKey = "state.metrics";

    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public MetricType? SelectedType { get; set; }
    public bool ShowUniqueNamesView { get; set; } = true;

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<Dto>(StorageKey);
        if (dto is null) return;
        SearchText = dto.SearchText;
        SelectedService = dto.SelectedService;
        SelectedType = dto.SelectedType;
        ShowUniqueNamesView = dto.ShowUniqueNamesView;
    }

    public Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new Dto(SearchText, SelectedService, SelectedType, ShowUniqueNamesView));

    private record Dto(string SearchText, string? SelectedService, MetricType? SelectedType, bool ShowUniqueNamesView);
}
