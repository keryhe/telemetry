using ApexCharts;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;
using Size = MudBlazor.Size;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Traces : ComponentBase, IDisposable
{
    private int _activeTabIndexBacking = 0;
    private int _activeTabIndex
    {
        get => _activeTabIndexBacking;
        set { _activeTabIndexBacking = value; State.ActiveTabIndex = value; }
    }
    private List<TraceInfo> _traces = new();
    private List<ServiceDependency> _serviceDependencies = new();
    private List<SpanModel> _selectedTraceSpans = new();
    private Dictionary<string, int> _operationCounts = new();
    private Dictionary<string, double> _averageLatencies = new();
    private List<string> _availableServices = new();
    private bool _dataLoading = true;
    private string _searchText = "";
    private string _filterMode = "all"; // all, errors, slow
    private string? _selectedService = null;
    private int _minDurationMs = 500;
    private string? _selectedTraceId = null;
    private string? _selectedAnalyticsService = null;

    // Span expansion state
    private HashSet<string> _expandedSpanIds = new();

    // Stats cards
    private int _totalTraces = 0;
    private double _avgDuration = 0;
    private double _errorRate = 0;
    private int _servicesCount = 0;

    // Chart data
    private record DataPoint(DateTime Time, double Value);
    private List<DataPoint> _traceTotal = new();
    private List<DataPoint> _traceErrors = new();
    private ApexChartOptions<DataPoint> _chartOptions = new();
    private bool _hasTraceData = false;

    [Inject]
    private ITraceService TraceService { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    private TracesPageState State { get; set; } = null!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "traceId")]
    public string? TraceIdFromQuery { get; set; }

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;

        // Restore filter state
        _filterMode = State.FilterMode;
        _selectedService = State.SelectedService;
        _minDurationMs = State.MinDurationMs;
        _selectedAnalyticsService = State.SelectedAnalyticsService;

        // Restore search text only when not navigating via traceId query param
        if (string.IsNullOrEmpty(TraceIdFromQuery))
        {
            _searchText = State.SearchText;
        }

        // Clamp tab index: tab 3 (Trace Detail) requires live span data
        _activeTabIndexBacking = Math.Min(State.ActiveTabIndex, 2);

        await LoadDataAsync();

        // If traceId is provided in query string, automatically view that trace
        if (!string.IsNullOrEmpty(TraceIdFromQuery))
        {
            await ViewTraceDetails(TraceIdFromQuery);
        }
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(LoadDataAsync);
    }

    private async Task LoadDataAsync()
    {
        // Persist current filter state
        State.FilterMode = _filterMode;
        State.SelectedService = _selectedService;
        State.MinDurationMs = _minDurationMs;
        State.SearchText = _searchText;

        _dataLoading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();

            // Load traces based on filter mode
            switch (_filterMode)
            {
                case "errors":
                    _traces = await TraceService.GetErrorTracesAsync(start, end, limit: 200);
                    break;
                case "slow":
                    _traces = await TraceService.GetSlowTracesAsync(TimeSpan.FromMilliseconds(_minDurationMs), start, end, limit: 200);
                    break;
                default:
                    if (!string.IsNullOrEmpty(_selectedService))
                    {
                        _traces = await TraceService.GetTracesByServiceAsync(_selectedService, start, end, limit: 200);
                    }
                    else
                    {
                        _traces = await TraceService.GetTracesByTimeRangeAsync(start, end, limit: 200);
                    }
                    break;
            }

            // Filter by search text if provided
            if (!string.IsNullOrEmpty(_searchText))
            {
                _traces = _traces.Where(t =>
                    t.TraceIdHex.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                    (t.ServiceName?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                ).ToList();
            }

            // Calculate stats
            CalculateStats();
            UpdateChartData(start, end);

            // Load service dependencies for service map tab
            _serviceDependencies = await TraceService.GetServiceDependenciesAsync(start, end);

            // Load available services
            _availableServices = await TraceService.GetDistinctServicesAsync(start, end);
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private async Task SearchTraces()
    {
        await LoadDataAsync();
    }

    private async Task OnFilterModeChanged(string mode)
    {
        _filterMode = mode;
        await LoadDataAsync();
    }

    private async Task OnServiceFilterChanged(string? service)
    {
        _selectedService = service;
        await LoadDataAsync();
    }

    private async Task ViewTraceDetails(string traceId)
    {
        _selectedTraceId = traceId;
        _dataLoading = true;
        StateHasChanged();

        try
        {
            _selectedTraceSpans = await TraceService.GetTraceByIdAsync(traceId);
            _activeTabIndex = 3; // Switch to trace detail tab
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private void BackToTraceList()
    {
        _selectedTraceId = null;
        _selectedTraceSpans.Clear();
        _activeTabIndex = 0;
    }

    private async Task OnAnalyticsServiceChanged(string? service)
    {
        _selectedAnalyticsService = service;
        State.SelectedAnalyticsService = service;

        if (string.IsNullOrEmpty(service))
        {
            _operationCounts.Clear();
            _averageLatencies.Clear();
            return;
        }

        _dataLoading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();

            _operationCounts = await TraceService.GetOperationCountsAsync(service, start, end);
            _averageLatencies = await TraceService.GetAverageLatenciesAsync(service, start, end);
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private void CalculateStats()
    {
        _totalTraces = _traces.Count;
        _avgDuration = _traces.Any() ? _traces.Average(t => t.TraceDuration.TotalMilliseconds) : 0;
        _errorRate = _traces.Any() ? (_traces.Count(t => t.HasErrors) / (double)_traces.Count) * 100 : 0;
        _servicesCount = _traces.Select(t => t.ServiceName).Distinct().Count();
    }

    private record BucketConfig(TimeSpan BucketSize, string LabelFormat);

    private static readonly BucketConfig[] BucketConfigs =
    [
        new(TimeSpan.FromMinutes(1),  "HH:mm"),
        new(TimeSpan.FromMinutes(5),  "HH:mm"),
        new(TimeSpan.FromMinutes(10), "HH:mm"),
        new(TimeSpan.FromMinutes(30), "HH:mm"),
        new(TimeSpan.FromHours(1),    "HH:mm"),
        new(TimeSpan.FromHours(3),    "MM/dd"),
        new(TimeSpan.FromHours(6),    "MM/dd"),
        new(TimeSpan.FromHours(12),   "MM/dd"),
        new(TimeSpan.FromDays(1),     "MM/dd"),
        new(TimeSpan.FromDays(2),     "MM/dd"),
        new(TimeSpan.FromDays(7),     "MMM"),
        new(TimeSpan.FromDays(30),    "MMM yy"),
    ];

    private const int MaxBucketCount = 50;

    private static BucketConfig SelectBucketConfig(TimeSpan duration)
    {
        foreach (var config in BucketConfigs)
        {
            if ((long)(duration / config.BucketSize) <= MaxBucketCount)
                return config;
        }
        return BucketConfigs[^1];
    }

    private static DateTime FloorToBucketBoundary(DateTime dt, TimeSpan bucketSize)
    {
        if (bucketSize >= TimeSpan.FromDays(1))
            return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Kind);

        return new DateTime(dt.Ticks / bucketSize.Ticks * bucketSize.Ticks, dt.Kind);
    }

    private void UpdateChartData(DateTime start, DateTime end)
    {
        if (_traces.Count == 0)
        {
            _traceTotal = new();
            _traceErrors = new();
            _chartOptions = new ApexChartOptions<DataPoint>();
            _hasTraceData = false;
            return;
        }

        var duration = end - start;
        var config = SelectBucketConfig(duration);
        var alignedStart = FloorToBucketBoundary(start, config.BucketSize);

        var buckets = new Dictionary<DateTime, (int Total, int Errors)>();
        var current = alignedStart;
        while (current <= end)
        {
            buckets[current] = (0, 0);
            current = current.Add(config.BucketSize);
        }

        foreach (var trace in _traces)
        {
            var offset = (long)((trace.TraceStartTime - alignedStart).Ticks / config.BucketSize.Ticks);
            var bucketKey = alignedStart + TimeSpan.FromTicks(offset * config.BucketSize.Ticks);
            if (buckets.ContainsKey(bucketKey))
            {
                var cur = buckets[bucketKey];
                buckets[bucketKey] = (cur.Total + 1, cur.Errors + (trace.HasErrors ? 1 : 0));
            }
        }

        var sorted = buckets.OrderBy(b => b.Key).ToList();

        _traceTotal  = sorted.Select(b => new DataPoint(b.Key, b.Value.Total)).ToList();
        _traceErrors = sorted.Select(b => new DataPoint(b.Key, b.Value.Errors)).ToList();

        _chartOptions = new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X } },
            Colors = new List<string> { "#2196F3", "#F44336" },
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Xaxis = new XAxis { Type = XAxisType.Datetime },
        };

        _hasTraceData = true;
    }

    private Task HandleChartZoomed(ZoomedData<DataPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return $"{duration.TotalMilliseconds:F0}ms";
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:F2}s";
        return $"{duration.TotalMinutes:F2}m";
    }

    private string FormatDateTime(DateTime dateTime)
    {
        return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    private string GetDurationColor(double milliseconds)
    {
        if (milliseconds < 100)
            return Colors.Green.Default;
        if (milliseconds < 500)
            return Colors.Yellow.Darken2;
        return Colors.Red.Default;
    }

    private string GetStatusColor(bool hasErrors)
    {
        return hasErrors ? Colors.Red.Default : Colors.Green.Default;
    }

    private string GetStatusIcon(bool hasErrors)
    {
        return hasErrors ? Icons.Material.Filled.Error : Icons.Material.Filled.CheckCircle;
    }

    private string GetSpanKindColor(SpanKind kind)
    {
        return kind switch
        {
            SpanKind.CLIENT => Colors.Blue.Default,
            SpanKind.SERVER => Colors.Green.Default,
            SpanKind.INTERNAL => Colors.Gray.Default,
            SpanKind.PRODUCER => Colors.Purple.Default,
            SpanKind.CONSUMER => Colors.Orange.Default,
            _ => Colors.Gray.Default
        };
    }

    private string TruncateTraceId(string traceId, int maxLength = 16)
    {
        return traceId.Length > maxLength ? traceId.Substring(0, maxLength) + "..." : traceId;
    }

    private List<SpanModel> BuildSpanHierarchy()
    {
        if (!_selectedTraceSpans.Any())
            return new List<SpanModel>();

        var rootSpans = _selectedTraceSpans.Where(s => string.IsNullOrEmpty(s.ParentSpanIdHex)).ToList();
        return rootSpans;
    }

    private List<SpanModel> GetChildSpans(string parentSpanId)
    {
        return _selectedTraceSpans
            .Where(s => s.ParentSpanIdHex == parentSpanId)
            .OrderBy(s => s.StartTimeUnixNano)
            .ToList();
    }

    private double GetSpanDurationMs(SpanModel span)
    {
        return (span.EndTimeUnixNano - span.StartTimeUnixNano) / 1_000_000.0;
    }

    private double GetSpanStartOffsetPercent(SpanModel span)
    {
        if (!_selectedTraceSpans.Any())
            return 0;

        var traceStart = _selectedTraceSpans.Min(s => s.StartTimeUnixNano);
        var traceEnd = _selectedTraceSpans.Max(s => s.EndTimeUnixNano);
        var traceDuration = traceEnd - traceStart;

        if (traceDuration == 0)
            return 0;

        var spanStart = span.StartTimeUnixNano - traceStart;
        return (spanStart / (double)traceDuration) * 100;
    }

    private double GetSpanWidthPercent(SpanModel span)
    {
        if (!_selectedTraceSpans.Any())
            return 0;

        var traceStart = _selectedTraceSpans.Min(s => s.StartTimeUnixNano);
        var traceEnd = _selectedTraceSpans.Max(s => s.EndTimeUnixNano);
        var traceDuration = traceEnd - traceStart;

        if (traceDuration == 0)
            return 100;

        var spanDuration = span.EndTimeUnixNano - span.StartTimeUnixNano;
        return (spanDuration / (double)traceDuration) * 100;
    }

    private string GetServiceName(SpanModel span)
    {
        if (span.Resource?.Attributes != null && span.Resource.Attributes.TryGetValue("service.name", out var serviceName))
        {
            return serviceName?.ToString() ?? "unknown";
        }
        return "unknown";
    }

    private DateTime UnixNanoToDateTime(long unixNano)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return unixEpoch.AddMilliseconds(unixNano / 1_000_000.0);
    }

    private int GetSpanLevel(SpanModel span)
    {
        var level = 0;
        var currentParent = span.ParentSpanIdHex;

        while (!string.IsNullOrEmpty(currentParent))
        {
            level++;
            var parentSpan = _selectedTraceSpans.FirstOrDefault(s => s.SpanIdHex == currentParent);
            if (parentSpan == null) break;
            currentParent = parentSpan.ParentSpanIdHex;
        }

        return level;
    }

    private bool IsSpanExpanded(string spanId)
    {
        return _expandedSpanIds.Contains(spanId);
    }

    private void ToggleSpanExpansion(string spanId)
    {
        if (_expandedSpanIds.Contains(spanId))
            _expandedSpanIds.Remove(spanId);
        else
            _expandedSpanIds.Add(spanId);

        StateHasChanged();
    }

    private string FormatAttributeValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        if (value is bool b) return b.ToString().ToLower();
        if (value is int || value is long || value is double || value is decimal) return value.ToString()!;
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return value.ToString() ?? "N/A";
    }

    private RenderFragment RenderSpanDetails(SpanModel span, int level) => builder =>
    {
        var seq = 0;

        builder.OpenComponent<MudPaper>(seq++);
        builder.AddAttribute(seq++, "Class", "pa-3 mt-1 mb-2");
        builder.AddAttribute(seq++, "Style", $"margin-left: {level * 20 + 40}px; background-color: #f9f9f9; border-left: 3px solid {GetSpanKindColor(span.Kind)};");
        builder.AddAttribute(seq++, "Elevation", 0);
        builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(builder2 =>
        {
            var seq2 = 0;

            if (span.Attributes != null && span.Attributes.Any())
            {
                builder2.OpenComponent<MudText>(seq2++);
                builder2.AddAttribute(seq2++, "Typo", Typo.subtitle2);
                builder2.AddAttribute(seq2++, "Class", "mb-2");
                builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(b => b.AddContent(0, "Attributes")));
                builder2.CloseComponent();

                builder2.OpenComponent<MudSimpleTable>(seq2++);
                builder2.AddAttribute(seq2++, "Dense", true);
                builder2.AddAttribute(seq2++, "Style", "font-size: 0.85rem; background-color: white;");
                builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
                {
                    var seq3 = 0;
                    builder3.OpenElement(seq3++, "tbody");

                    foreach (var attr in span.Attributes.OrderBy(a => a.Key))
                    {
                        builder3.OpenElement(seq3++, "tr");

                        builder3.OpenElement(seq3++, "td");
                        builder3.AddAttribute(seq3++, "style", "font-family: monospace; color: #666; font-weight: 500; width: 30%;");
                        builder3.AddContent(seq3++, attr.Key);
                        builder3.CloseElement();

                        builder3.OpenElement(seq3++, "td");
                        builder3.AddContent(seq3++, FormatAttributeValue(attr.Value));
                        builder3.CloseElement();

                        builder3.CloseElement(); // tr
                    }

                    builder3.CloseElement(); // tbody
                }));
                builder2.CloseComponent();
            }

            if (span.Events != null && span.Events.Any())
            {
                builder2.OpenComponent<MudText>(seq2++);
                builder2.AddAttribute(seq2++, "Typo", Typo.subtitle2);
                builder2.AddAttribute(seq2++, "Class", "mt-3 mb-2");
                builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"Events ({span.Events.Count})")));
                builder2.CloseComponent();

                builder2.OpenElement(seq2++, "div");
                builder2.AddAttribute(seq2++, "style", "padding-left: 0;");

                foreach (var evt in span.Events.OrderBy(e => e.TimeUnixNano))
                {
                    var eventTime = UnixNanoToDateTime(evt.TimeUnixNano).ToLocalTime();

                    builder2.OpenElement(seq2++, "div");
                    builder2.AddAttribute(seq2++, "style", "padding: 8px 0; border-bottom: 1px solid #eeeeee;");

                    builder2.OpenComponent<MudStack>(seq2++);
                    builder2.AddAttribute(seq2++, "Row", true);
                    builder2.AddAttribute(seq2++, "Spacing", 2);
                    builder2.AddAttribute(seq2++, "AlignItems", AlignItems.Center);
                    builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder5 =>
                    {
                        var seq5 = 0;

                        builder5.OpenComponent<MudIcon>(seq5++);
                        builder5.AddAttribute(seq5++, "Icon", Icons.Material.Filled.Event);
                        builder5.AddAttribute(seq5++, "Size", Size.Small);
                        builder5.AddAttribute(seq5++, "Color", Color.Info);
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.body2);
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, evt.Name)));
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.caption);
                        builder5.AddAttribute(seq5++, "Style", "font-family: monospace;");
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, eventTime.ToString("HH:mm:ss.fff"))));
                        builder5.CloseComponent();
                    }));
                    builder2.CloseComponent();

                    if (evt.Attributes != null && evt.Attributes.Any())
                    {
                        builder2.OpenComponent<MudText>(seq2++);
                        builder2.AddAttribute(seq2++, "Typo", Typo.caption);
                        builder2.AddAttribute(seq2++, "Class", "ml-6");
                        builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(b =>
                            b.AddContent(0, string.Join(", ", evt.Attributes.Select(a => $"{a.Key}={FormatAttributeValue(a.Value)}")))));
                        builder2.CloseComponent();
                    }

                    builder2.CloseElement(); // event div
                }

                builder2.CloseElement(); // container div
            }

            if (span.Links != null && span.Links.Any())
            {
                builder2.OpenComponent<MudText>(seq2++);
                builder2.AddAttribute(seq2++, "Typo", Typo.subtitle2);
                builder2.AddAttribute(seq2++, "Class", "mt-3 mb-2");
                builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"Links ({span.Links.Count})")));
                builder2.CloseComponent();

                builder2.OpenElement(seq2++, "div");
                builder2.AddAttribute(seq2++, "style", "padding-left: 0;");

                foreach (var link in span.Links)
                {
                    builder2.OpenElement(seq2++, "div");
                    builder2.AddAttribute(seq2++, "style", "padding: 8px 0; border-bottom: 1px solid #eeeeee;");

                    builder2.OpenComponent<MudStack>(seq2++);
                    builder2.AddAttribute(seq2++, "Row", true);
                    builder2.AddAttribute(seq2++, "Spacing", 2);
                    builder2.AddAttribute(seq2++, "AlignItems", AlignItems.Center);
                    builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder5 =>
                    {
                        var seq5 = 0;

                        builder5.OpenComponent<MudIcon>(seq5++);
                        builder5.AddAttribute(seq5++, "Icon", Icons.Material.Filled.Link);
                        builder5.AddAttribute(seq5++, "Size", Size.Small);
                        builder5.AddAttribute(seq5++, "Color", Color.Secondary);
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.body2);
                        builder5.AddAttribute(seq5++, "Style", "font-family: monospace; font-size: 0.8rem;");
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, link.LinkedTraceIdHex)));
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.caption);
                        builder5.AddAttribute(seq5++, "Color", Color.Secondary);
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"Span: {link.LinkedSpanIdHex}")));
                        builder5.CloseComponent();
                    }));
                    builder2.CloseComponent();

                    builder2.CloseElement(); // link div
                }

                builder2.CloseElement(); // container div
            }

            builder2.OpenComponent<MudGrid>(seq2++);
            builder2.AddAttribute(seq2++, "Class", "mt-3");
            builder2.AddAttribute(seq2++, "Spacing", 2);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;

                builder3.OpenComponent<MudItem>(seq3++);
                builder3.AddAttribute(seq3++, "xs", 6);
                builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(builder4 =>
                {
                    var seq4 = 0;

                    builder4.OpenComponent<MudText>(seq4++);
                    builder4.AddAttribute(seq4++, "Typo", Typo.subtitle2);
                    builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, "Status")));
                    builder4.CloseComponent();

                    builder4.OpenComponent<MudChip<string>>(seq4++);
                    builder4.AddAttribute(seq4++, "Size", Size.Small);
                    builder4.AddAttribute(seq4++, "Color", span.StatusCode == SpanStatusCode.ERROR ? Color.Error : Color.Success);
                    builder4.AddAttribute(seq4++, "Text", span.StatusCode.ToString());
                    builder4.CloseComponent();

                    if (!string.IsNullOrEmpty(span.StatusMessage))
                    {
                        builder4.OpenComponent<MudText>(seq4++);
                        builder4.AddAttribute(seq4++, "Typo", Typo.caption);
                        builder4.AddAttribute(seq4++, "Color", Color.Error);
                        builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, span.StatusMessage)));
                        builder4.CloseComponent();
                    }
                }));
                builder3.CloseComponent();

                if (span.InstrumentationScope != null)
                {
                    builder3.OpenComponent<MudItem>(seq3++);
                    builder3.AddAttribute(seq3++, "xs", 6);
                    builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(builder4 =>
                    {
                        var seq4 = 0;

                        builder4.OpenComponent<MudText>(seq4++);
                        builder4.AddAttribute(seq4++, "Typo", Typo.subtitle2);
                        builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, "Instrumentation")));
                        builder4.CloseComponent();

                        builder4.OpenComponent<MudText>(seq4++);
                        builder4.AddAttribute(seq4++, "Typo", Typo.body2);
                        builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, span.InstrumentationScope.Name ?? "N/A")));
                        builder4.CloseComponent();

                        if (!string.IsNullOrEmpty(span.InstrumentationScope.Version))
                        {
                            builder4.OpenComponent<MudText>(seq4++);
                            builder4.AddAttribute(seq4++, "Typo", Typo.caption);
                            builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"v{span.InstrumentationScope.Version}")));
                            builder4.CloseComponent();
                        }
                    }));
                    builder3.CloseComponent();
                }
            }));
            builder2.CloseComponent();
        }));
        builder.CloseComponent();
    };

    private RenderFragment RenderSpanTreeWithTimestamps(SpanModel span, int level, long traceStart, double traceDuration) => builder =>
    {
        var seq = 0;
        var spanStartTime = UnixNanoToDateTime(span.StartTimeUnixNano).ToLocalTime();
        var spanDuration = GetSpanDurationMs(span);
        var isExpanded = IsSpanExpanded(span.SpanIdHex);

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 4px;");

        builder.OpenComponent<MudGrid>(seq++);
        builder.AddAttribute(seq++, "Spacing", 0);
        builder.AddAttribute(seq++, "Style", "align-items: center;");
        builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(builder2 =>
        {
            var seq2 = 0;

            builder2.OpenComponent<MudItem>(seq2++);
            builder2.AddAttribute(seq2++, "xs", 5);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;

                builder3.OpenComponent<MudStack>(seq3++);
                builder3.AddAttribute(seq3++, "Row", true);
                builder3.AddAttribute(seq3++, "AlignItems", AlignItems.Center);
                builder3.AddAttribute(seq3++, "Spacing", 1);
                builder3.AddAttribute(seq3++, "Style", $"padding-left: {level * 20 + 8}px;");
                builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(builder4 =>
                {
                    var seq4 = 0;

                    builder4.OpenComponent<MudIconButton>(seq4++);
                    builder4.AddAttribute(seq4++, "Icon", isExpanded ? Icons.Material.Filled.ExpandMore : Icons.Material.Filled.ChevronRight);
                    builder4.AddAttribute(seq4++, "Size", Size.Small);
                    builder4.AddAttribute(seq4++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, _ => ToggleSpanExpansion(span.SpanIdHex)));
                    builder4.AddAttribute(seq4++, "Style", "padding: 0; margin-right: -4px;");
                    builder4.CloseComponent();

                    builder4.OpenComponent<MudChip<string>>(seq4++);
                    builder4.AddAttribute(seq4++, "Size", Size.Small);
                    builder4.AddAttribute(seq4++, "Text", span.Kind.ToString());
                    builder4.AddAttribute(seq4++, "Style", $"background-color: {GetSpanKindColor(span.Kind)}; color: white; font-size: 0.7rem;");
                    builder4.CloseComponent();

                    builder4.OpenComponent<MudText>(seq4++);
                    builder4.AddAttribute(seq4++, "Typo", Typo.body2);
                    builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(b => b.AddContent(0, span.Name)));
                    builder4.CloseComponent();

                    if (span.StatusCode == SpanStatusCode.ERROR)
                    {
                        builder4.OpenComponent<MudIcon>(seq4++);
                        builder4.AddAttribute(seq4++, "Icon", Icons.Material.Filled.Error);
                        builder4.AddAttribute(seq4++, "Color", Color.Error);
                        builder4.AddAttribute(seq4++, "Size", Size.Small);
                        builder4.CloseComponent();
                    }
                }));
                builder3.CloseComponent();
            }));
            builder2.CloseComponent();

            builder2.OpenComponent<MudItem>(seq2++);
            builder2.AddAttribute(seq2++, "xs", 2);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;
                builder3.OpenComponent<MudText>(seq3++);
                builder3.AddAttribute(seq3++, "Typo", Typo.caption);
                builder3.AddAttribute(seq3++, "Style", "font-family: monospace;");
                builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(b => b.AddContent(0, spanStartTime.ToString("HH:mm:ss.fff"))));
                builder3.CloseComponent();
            }));
            builder2.CloseComponent();

            builder2.OpenComponent<MudItem>(seq2++);
            builder2.AddAttribute(seq2++, "xs", 2);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;
                builder3.OpenComponent<MudText>(seq3++);
                builder3.AddAttribute(seq3++, "Typo", Typo.caption);
                builder3.AddAttribute(seq3++, "Color", Color.Secondary);
                builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"{spanDuration:F2}ms")));
                builder3.CloseComponent();
            }));
            builder2.CloseComponent();

            builder2.OpenComponent<MudItem>(seq2++);
            builder2.AddAttribute(seq2++, "xs", 3);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;
                builder3.OpenElement(seq3++, "div");
                builder3.AddAttribute(seq3++, "style", "position: relative; height: 16px; background-color: #e0e0e0; border-radius: 3px;");

                builder3.OpenElement(seq3++, "div");
                builder3.AddAttribute(seq3++, "style", $"position: absolute; left: {GetSpanStartOffsetPercent(span)}%; width: {GetSpanWidthPercent(span)}%; height: 100%; background-color: {GetSpanKindColor(span.Kind)}; border-radius: 3px; opacity: 0.8;");
                builder3.AddAttribute(seq3++, "title", $"{spanStartTime:HH:mm:ss.fff} - {spanDuration:F2}ms");
                builder3.CloseElement(); // fill

                builder3.CloseElement(); // container
            }));
            builder2.CloseComponent();
        }));
        builder.CloseComponent(); // MudGrid

        if (isExpanded)
        {
            builder.AddContent(seq++, RenderSpanDetails(span, level));
        }

        builder.CloseElement(); // container div

        var childSpans = GetChildSpans(span.SpanIdHex);
        foreach (var childSpan in childSpans)
        {
            builder.AddContent(seq++, RenderSpanTreeWithTimestamps(childSpan, level + 1, traceStart, traceDuration));
        }
    };

    private RenderFragment RenderSpanTree(SpanModel span, int level) => builder =>
    {
        var seq = 0;

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", $"margin-left: {level * 20}px; margin-bottom: 8px;");

        builder.OpenComponent<MudPaper>(seq++);
        builder.AddAttribute(seq++, "Elevation", 1);
        builder.AddAttribute(seq++, "Class", "pa-2");
        builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(builder2 =>
        {
            var seq2 = 0;

            builder2.OpenComponent<MudStack>(seq2++);
            builder2.AddAttribute(seq2++, "Spacing", 1);
            builder2.AddAttribute(seq2++, "ChildContent", (RenderFragment)(builder3 =>
            {
                var seq3 = 0;

                builder3.OpenComponent<MudStack>(seq3++);
                builder3.AddAttribute(seq3++, "Row", true);
                builder3.AddAttribute(seq3++, "AlignItems", AlignItems.Center);
                builder3.AddAttribute(seq3++, "Justify", Justify.SpaceBetween);
                builder3.AddAttribute(seq3++, "ChildContent", (RenderFragment)(builder4 =>
                {
                    var seq4 = 0;

                    builder4.OpenComponent<MudStack>(seq4++);
                    builder4.AddAttribute(seq4++, "Row", true);
                    builder4.AddAttribute(seq4++, "AlignItems", AlignItems.Center);
                    builder4.AddAttribute(seq4++, "Spacing", 2);
                    builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(builder5 =>
                    {
                        var seq5 = 0;

                        builder5.OpenComponent<MudChip<string>>(seq5++);
                        builder5.AddAttribute(seq5++, "Size", Size.Small);
                        builder5.AddAttribute(seq5++, "Text", span.Kind.ToString());
                        builder5.AddAttribute(seq5++, "Style", $"background-color: {GetSpanKindColor(span.Kind)}; color: white;");
                        builder5.AddAttribute(seq5++, "Variant", Variant.Filled);
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.body2);
                        builder5.AddAttribute(seq5++, "Style", "font-weight: 500;");
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, span.Name)));
                        builder5.CloseComponent();

                        builder5.OpenComponent<MudChip<string>>(seq5++);
                        builder5.AddAttribute(seq5++, "Size", Size.Small);
                        builder5.AddAttribute(seq5++, "Text", GetServiceName(span));
                        builder5.AddAttribute(seq5++, "Variant", Variant.Text);
                        builder5.CloseComponent();
                    }));
                    builder4.CloseComponent();

                    builder4.OpenComponent<MudStack>(seq4++);
                    builder4.AddAttribute(seq4++, "Row", true);
                    builder4.AddAttribute(seq4++, "AlignItems", AlignItems.Center);
                    builder4.AddAttribute(seq4++, "Spacing", 2);
                    builder4.AddAttribute(seq4++, "ChildContent", (RenderFragment)(builder5 =>
                    {
                        var seq5 = 0;

                        if (span.StatusCode == SpanStatusCode.ERROR)
                        {
                            builder5.OpenComponent<MudIcon>(seq5++);
                            builder5.AddAttribute(seq5++, "Icon", Icons.Material.Filled.Error);
                            builder5.AddAttribute(seq5++, "Color", Color.Error);
                            builder5.AddAttribute(seq5++, "Size", Size.Small);
                            builder5.CloseComponent();
                        }

                        builder5.OpenComponent<MudText>(seq5++);
                        builder5.AddAttribute(seq5++, "Typo", Typo.body2);
                        builder5.AddAttribute(seq5++, "Color", Color.Secondary);
                        builder5.AddAttribute(seq5++, "ChildContent", (RenderFragment)(b => b.AddContent(0, $"{GetSpanDurationMs(span):F2}ms")));
                        builder5.CloseComponent();
                    }));
                    builder4.CloseComponent();
                }));
                builder3.CloseComponent();

                builder3.OpenElement(seq3++, "div");
                builder3.AddAttribute(seq3++, "style", "position: relative; height: 20px; background-color: #f5f5f5; border-radius: 4px;");
                builder3.OpenElement(seq3++, "div");
                builder3.AddAttribute(seq3++, "style", $"position: absolute; left: {GetSpanStartOffsetPercent(span)}%; width: {GetSpanWidthPercent(span)}%; height: 100%; background-color: {GetSpanKindColor(span.Kind)}; border-radius: 4px; opacity: 0.7;");
                builder3.CloseElement(); // inner div
                builder3.CloseElement(); // outer div
            }));
            builder2.CloseComponent();
        }));
        builder.CloseComponent();

        var childSpans = GetChildSpans(span.SpanIdHex);
        foreach (var childSpan in childSpans)
        {
            builder.AddContent(seq++, RenderSpanTree(childSpan, level + 1));
        }
    };

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
    }
}
