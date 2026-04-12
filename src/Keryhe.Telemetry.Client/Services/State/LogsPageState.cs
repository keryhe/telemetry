using Keryhe.Telemetry.Client.Services;

namespace Keryhe.Telemetry.Client.Services.State;

public class LogsPageState(LocalStorageService storage)
{
    private const string StorageKey = "state.logs";

    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public string? SelectedSeverity { get; set; }

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<Dto>(StorageKey);
        if (dto is null) return;
        SearchText = dto.SearchText;
        SelectedService = dto.SelectedService;
        SelectedSeverity = dto.SelectedSeverity;
    }

    public Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new Dto(SearchText, SelectedService, SelectedSeverity));

    private record Dto(string SearchText, string? SelectedService, string? SelectedSeverity);
}
