using Keryhe.Telemetry.Client.Models;

namespace Keryhe.Telemetry.Client.Services.State;

public class TimeRangeState
{
    public TimeRange SelectedTimeRange { get; private set; } = TimeRange.Last1Hour;
    public DateTime? CustomStart { get; private set; }
    public DateTime? CustomEnd { get; private set; }

    public event Action? OnChange;

    public void SetPreset(TimeRange range)
    {
        SelectedTimeRange = range;
        CustomStart = null;
        CustomEnd = null;
        OnChange?.Invoke();
    }

    public void SetCustomRange(DateTime start, DateTime end)
    {
        SelectedTimeRange = TimeRange.Custom;
        CustomStart = start;
        CustomEnd = end;
        OnChange?.Invoke();
    }

    public (DateTime Start, DateTime End) GetDateTimeRange()
    {
        if (SelectedTimeRange == TimeRange.Custom && CustomStart.HasValue && CustomEnd.HasValue)
        {
            return (CustomStart.Value, CustomEnd.Value);
        }
        return SelectedTimeRange.ToDateTimeRange();
    }
}
