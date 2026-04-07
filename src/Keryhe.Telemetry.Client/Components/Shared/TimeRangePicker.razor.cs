using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Client.Services.State;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class TimeRangePicker : ComponentBase, IDisposable
{
    [Inject] private TimeRangeState TimeRangeState { get; set; } = null!;

    private bool _panelOpen = false;
    private DateTime? _customStart;
    private DateTime? _customEnd;

    protected override void OnInitialized()
    {
        TimeRangeState.OnChange += OnStateChanged;
    }

    private void OnStateChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void TogglePanel()
    {
        _panelOpen = !_panelOpen;
        if (_panelOpen && TimeRangeState.SelectedTimeRange == TimeRange.Custom)
        {
            _customStart = TimeRangeState.CustomStart;
            _customEnd = TimeRangeState.CustomEnd;
        }
    }

    private void ClosePanel()
    {
        _panelOpen = false;
    }

    private void SelectPreset(TimeRange range)
    {
        TimeRangeState.SetPreset(range);
        _panelOpen = false;
    }

    private void ApplyCustomRange()
    {
        if (_customStart.HasValue && _customEnd.HasValue)
        {
            TimeRangeState.SetCustomRange(_customStart.Value, _customEnd.Value);
            _panelOpen = false;
        }
    }

    private string GetButtonLabel()
    {
        if (TimeRangeState.SelectedTimeRange == TimeRange.Custom
            && TimeRangeState.CustomStart.HasValue
            && TimeRangeState.CustomEnd.HasValue)
        {
            return $"{TimeRangeState.CustomStart.Value:MMM d HH:mm} \u2192 {TimeRangeState.CustomEnd.Value:MMM d HH:mm}";
        }
        return TimeRangeState.SelectedTimeRange.GetDisplayName();
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnStateChanged;
    }
}
