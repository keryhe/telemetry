using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class ServiceMapGraph : ComponentBase, IAsyncDisposable
{
    [Parameter] public List<ServiceDependency> Dependencies { get; set; } = [];
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private IJSObjectReference? _module;
    private List<ServiceDependency> _lastRendered = [];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/cytoscape-interop.js");

            var darkModeStr = await JS.InvokeAsync<string?>("localStorageGetItem", "ui.isDarkMode");
            bool.TryParse(darkModeStr, out var darkMode);

            await _module.InvokeVoidAsync("init", "cy-service-map", BuildElements(), darkMode);
            _lastRendered = [.. Dependencies];
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_module is not null && Dependencies != _lastRendered)
        {
            await _module.InvokeVoidAsync("update", BuildElements());
            _lastRendered = [.. Dependencies];
        }
    }

    private object[] BuildElements()
    {
        var nodes = Dependencies
            .SelectMany(d => new[] { d.ParentService, d.ChildService })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (object)new { data = new { id = name, label = name } })
            .ToArray();

        var edges = Dependencies
            .Select(d => (object)new
            {
                data = new
                {
                    id = $"{d.ParentService}->{d.ChildService}:{d.SpanKind}",
                    source = d.ParentService,
                    target = d.ChildService,
                    callCount = d.CallCount,
                    avgDurationMs = d.AvgDurationMs,
                    errorRate = d.ErrorRate,
                    spanKind = d.SpanKind.ToString()
                }
            })
            .ToArray();

        return [.. nodes, .. edges];
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("destroy");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already disconnected — nothing to clean up on the JS side.
            }
        }
    }
}
