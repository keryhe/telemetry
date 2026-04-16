using Keryhe.Telemetry.Client.Services;

namespace Keryhe.Telemetry.Client.Services.State;

public class DashboardPageState(LocalStorageService storage)
{
    private const string StorageKey = "state.dashboard";

    public string? SelectedService { get; set; }
    public bool AutoRefresh { get; set; } = false;

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<Dto>(StorageKey);
        if (dto is null) return;
        SelectedService = dto.SelectedService;
        AutoRefresh = dto.AutoRefresh;
    }

    public Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new Dto(SelectedService, AutoRefresh));

    private record Dto(string? SelectedService, bool AutoRefresh);
}
