using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Color = MudBlazor.Color;
using Size = MudBlazor.Size;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class TraceDetail : ComponentBase
{
    [Parameter]
    public string TraceId { get; set; } = "";

    [Inject]
    private ITraceService TraceService { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    private List<SpanModel> _selectedTraceSpans = new();
    private string? _selectedSpanId = null;
    private List<SpanRow> _allSpanRows = new();
    private HashSet<string> _collapsedSpanIds = new();
    private long _traceStartNano = 0;
    private long _traceDurationNano = 1;
    private Dictionary<string, string> _serviceColorMap = new();
    private bool _isLoading = true;
    private string? _errorMessage = null;

    private record SpanRow(SpanModel Span, int Depth, bool HasChildren);

    private static readonly string[] ServiceColorPalette =
    [
        "var(--mud-palette-primary)",
        "var(--mud-palette-secondary)",
        "var(--mud-palette-tertiary)",
        "var(--mud-palette-warning)",
        "var(--mud-palette-info)",
        "var(--mud-palette-success)"
        ,
        
    ];

    protected override async Task OnParametersSetAsync()
    {
        await LoadTrace();
    }

    private async Task LoadTrace()
    {
        _isLoading = true;
        _errorMessage = null;
        _selectedTraceSpans = new();
        _selectedSpanId = null;
        StateHasChanged();

        try
        {
            _selectedTraceSpans = await TraceService.GetTraceByIdAsync(TraceId);
            if (_selectedTraceSpans.Count == 0)
                _errorMessage = $"No spans found for trace {TraceId}.";
            else
                BuildSpanRows();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void BuildSpanRows()
    {
        _allSpanRows = new();
        _serviceColorMap = new();
        _collapsedSpanIds = new();
        _selectedSpanId = null;

        if (!_selectedTraceSpans.Any()) return;

        _traceStartNano = _selectedTraceSpans.Min(s => s.StartTimeUnixNano);
        var traceEnd    = _selectedTraceSpans.Max(s => s.EndTimeUnixNano);
        _traceDurationNano = Math.Max(1, traceEnd - _traceStartNano);

        BuildSpanRowsRecursive(BuildSpanHierarchy(), 0);
    }

    private void BuildSpanRowsRecursive(List<SpanModel> spans, int depth)
    {
        foreach (var span in spans)
        {
            var service = GetServiceName(span);
            if (!_serviceColorMap.ContainsKey(service))
                _serviceColorMap[service] = ServiceColorPalette[_serviceColorMap.Count % ServiceColorPalette.Length];

            var children = GetChildSpans(span.SpanIdHex);
            _allSpanRows.Add(new SpanRow(span, depth, children.Any()));
            BuildSpanRowsRecursive(children, depth + 1);
        }
    }

    private IEnumerable<SpanRow> GetVisibleRows()
    {
        foreach (var row in _allSpanRows)
        {
            if (!IsHiddenByCollapse(row.Span))
                yield return row;
        }
    }

    private bool IsHiddenByCollapse(SpanModel span)
    {
        var parentId = span.ParentSpanIdHex;
        while (!string.IsNullOrEmpty(parentId))
        {
            if (_collapsedSpanIds.Contains(parentId)) return true;
            var parent = _selectedTraceSpans.FirstOrDefault(s => s.SpanIdHex == parentId);
            parentId = parent?.ParentSpanIdHex;
        }
        return false;
    }

    private void ToggleCollapse(string spanId)
    {
        if (!_collapsedSpanIds.Remove(spanId))
            _collapsedSpanIds.Add(spanId);
    }

    private void ToggleSpanDetail(string spanId)
    {
        _selectedSpanId = _selectedSpanId == spanId ? null : spanId;
    }

    private double GetSpanLeftPct(SpanModel span) =>
        (span.StartTimeUnixNano - _traceStartNano) / (double)_traceDurationNano * 100;

    private double GetSpanWidthPct(SpanModel span) =>
        Math.Max(0.5, (span.EndTimeUnixNano - span.StartTimeUnixNano) / (double)_traceDurationNano * 100);

    private string FormatRelativeNano(long nano)
    {
        var ms = nano / 1_000_000.0;
        if (ms < 1)    return $"{nano / 1_000.0:F0}μs";
        if (ms < 1000) return $"{ms:F2}ms";
        return $"{ms / 1000:F3}s";
    }

    private string FormatDateTime(DateTime dateTime) =>
        dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");

    private List<SpanModel> BuildSpanHierarchy() =>
        _selectedTraceSpans.Where(s => string.IsNullOrEmpty(s.ParentSpanIdHex)).ToList();

    private List<SpanModel> GetChildSpans(string parentSpanId) =>
        _selectedTraceSpans
            .Where(s => s.ParentSpanIdHex == parentSpanId)
            .OrderBy(s => s.StartTimeUnixNano)
            .ToList();

    private string GetServiceName(SpanModel span)
    {
        if (span.Resource?.Attributes != null &&
            span.Resource.Attributes.TryGetValue("service.name", out var name))
            return name?.ToString() ?? "unknown";
        return "unknown";
    }

    private DateTime UnixNanoToDateTime(long unixNano) =>
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMilliseconds(unixNano / 1_000_000.0);

    private string FormatAttributeValue(object? value)
    {
        if (value == null)   return "null";
        if (value is string s) return s;
        if (value is bool b)   return b.ToString().ToLower();
        if (value is int || value is long || value is double || value is decimal) return value.ToString()!;
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return value.ToString() ?? "N/A";
    }

    private string TruncateTraceId(string traceId, int maxLength = 16) =>
        traceId.Length > maxLength ? traceId[..maxLength] + "..." : traceId;

    private async Task CopyToClipboard(string text) =>
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
}
