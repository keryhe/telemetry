using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class TimeRangePicker : ComponentBase, IDisposable
{
    [Inject] private TimeRangeState TimeRangeState { get; set; } = null!;
    [Inject] private BrowserTimezoneService BrowserTimezoneService { get; set; } = null!;

    private bool _panelOpen = false;
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;

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
            // Show existing custom range in local time so the user can edit it naturally
            if (TimeRangeState.CustomStart.HasValue)
            {
                var localStart = BrowserTimezoneService.ToLocalTime(TimeRangeState.CustomStart.Value);
                _customStartDate = localStart.Date;
            }
            if (TimeRangeState.CustomEnd.HasValue)
            {
                var localEnd = BrowserTimezoneService.ToLocalTime(TimeRangeState.CustomEnd.Value);
                _customEndDate = localEnd.Date;
            }
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
        if (_customStartDate.HasValue && _customEndDate.HasValue)
        {
            // Combine date + time (default to 00:00 / 23:59:59 if time not set), then convert local→UTC
            var localStart = _customStartDate.Value.Add(TimeSpan.Zero);
            var localEnd   = _customEndDate.Value.Add(new TimeSpan(23, 59, 59));
            var utcStart = BrowserTimezoneService.ToUtc(localStart);
            var utcEnd   = BrowserTimezoneService.ToUtc(localEnd);
            TimeRangeState.SetCustomRange(utcStart, utcEnd);
            _panelOpen = false;
        }
    }

    private string GetButtonLabel()
    {
        if (TimeRangeState.SelectedTimeRange == TimeRange.Custom
            && TimeRangeState.CustomStart.HasValue
            && TimeRangeState.CustomEnd.HasValue)
        {
            // UTC DateTimes are converted to browser local time for display
            var localStart = BrowserTimezoneService.ToLocalTime(TimeRangeState.CustomStart.Value);
            var localEnd   = BrowserTimezoneService.ToLocalTime(TimeRangeState.CustomEnd.Value);
            return $"{localStart:MMM d HH:mm} \u2192 {localEnd:MMM d HH:mm}";
        }
        return TimeRangeState.SelectedTimeRange.GetDisplayName();
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnStateChanged;
    }
}
