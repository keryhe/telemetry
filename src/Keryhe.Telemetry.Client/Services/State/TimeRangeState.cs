using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Client.Services;

namespace Keryhe.Telemetry.Client.Services.State;

public class TimeRangeState(LocalStorageService storage)
{
    private const string StorageKey = "state.timeRange";

    public TimeRange SelectedTimeRange { get; private set; } = TimeRange.Last1Hour;
    public DateTime? CustomStart { get; private set; }
    public DateTime? CustomEnd { get; private set; }

    public event Action? OnChange;

    public void SetPreset(TimeRange range)
    {
        SelectedTimeRange = range;
        CustomStart = null;
        CustomEnd = null;
        _ = SaveAsync();
        OnChange?.Invoke();
    }

    public void SetCustomRange(DateTime start, DateTime end)
    {
        SelectedTimeRange = TimeRange.Custom;
        CustomStart = start;
        CustomEnd = end;
        _ = SaveAsync();
        OnChange?.Invoke();
    }

    public async Task LoadAsync()
    {
        var dto = await storage.GetItemAsync<TimeRangeDto>(StorageKey);
        if (dto is null) return;

        if (dto.SelectedTimeRange == TimeRange.Custom && dto.CustomStart.HasValue && dto.CustomEnd.HasValue)
        {
            SelectedTimeRange = TimeRange.Custom;
            CustomStart = dto.CustomStart;
            CustomEnd = dto.CustomEnd;
        }
        else
        {
            SelectedTimeRange = dto.SelectedTimeRange;
            CustomStart = null;
            CustomEnd = null;
        }

        OnChange?.Invoke();
    }

    private Task SaveAsync() =>
        storage.SetItemAsync(StorageKey, new TimeRangeDto(SelectedTimeRange, CustomStart, CustomEnd));

    public (DateTime Start, DateTime End) GetDateTimeRange()
    {
        if (SelectedTimeRange == TimeRange.Custom && CustomStart.HasValue && CustomEnd.HasValue)
        {
            return (CustomStart.Value, CustomEnd.Value);
        }
        return SelectedTimeRange.ToDateTimeRange();
    }

    private record TimeRangeDto(TimeRange SelectedTimeRange, DateTime? CustomStart, DateTime? CustomEnd);
}
