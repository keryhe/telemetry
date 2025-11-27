using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Logs : ComponentBase
{
    private List<LogRecordModel> _logs = new();
    private bool _pageLoading = true;
    private bool _dataLoading = true;
    private LogRecordModel? _expandedItem;
    private string _searchText = "";
    private DateRange? _dateRange = new DateRange(DateTime.Today.AddDays(-7), DateTime.Today);
    
    [Inject]
    private ILogService LogService { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _pageLoading = true;
        _dataLoading = true;

        _logs = await LogService.GetLogRecordsByTimeRangeAsync(_dateRange?.Start, _dateRange?.End);
        _dataLoading = false;
        _pageLoading = false;
    }

    private void OnRowClick(DataGridRowClickEventArgs<LogRecordModel> args)
    {
        _expandedItem = _expandedItem == args.Item ? null : args.Item;
    }

    private string FormatUnixNanoToDateTime(long unixNano)
    {
        // Convert nanoseconds to ticks (1 tick = 100 nanoseconds)
        long ticks = unixNano / 100;
        
        // Unix epoch start: January 1, 1970
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epoch.AddTicks(ticks);
        
        // Format as local time
        return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
    
    private Color GetSeverityColor(string? severity)
    {
        return severity?.ToUpper() switch
        {
            "Information" => Color.Info,      // Blue
            "Debug" => Color.Default,    // Green
            "Warning" => Color.Warning,    // Yellow/Orange
            "Error" => Color.Error,     // Red
            "FATAL" => Color.Error,     // Red
            _ => Color.Default
        };
    }
    
    private async Task SearchLogs()
    {
        _dataLoading = true;
        StateHasChanged();
        
        _logs = await LogService.GetLogRecordsByTimeRangeAsync(_dateRange?.Start, _dateRange?.End);
        
        _dataLoading = false;
    }
}