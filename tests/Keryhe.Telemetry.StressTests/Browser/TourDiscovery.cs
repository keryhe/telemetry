using System.Text.Json;

namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>Looks up, through the API, a service and a histogram and a sum metric for a tenant, so the tour opens real pages. Anything not found is left out of the plan.</summary>
public static class TourDiscovery
{
    /// <summary>
    /// Discovers, retrying until both metrics are found or <paramref name="wait"/> passes: the metrics catalog only lists
    /// a metric once the periodic <c>MetricTouchWorker</c> has flushed (60s by default), so it is empty for the first minute of a run.
    /// </summary>
    public static async Task<TourData> DiscoverWithRetryAsync(HttpClient api, long tenantId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var data = await DiscoverAsync(api, tenantId, cancellationToken);
            if ((data.HistogramMetric is not null && data.SumMetric is not null) || DateTime.UtcNow >= deadline) return data;
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    public static async Task<TourData> DiscoverAsync(HttpClient api, long tenantId, CancellationToken cancellationToken)
    {
        async Task<JsonElement?> GetAsync(string path)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                using var response = await api.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                return doc.RootElement.Clone();
            }
            catch (HttpRequestException) { return null; }
        }

        var end = DateTime.UtcNow;
        var range = $"start={Uri.EscapeDataString(end.AddDays(-7).ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}";

        string? service = null;
        if (await GetAsync($"api/tenants/{tenantId}/resources/services") is { ValueKind: JsonValueKind.Array } services && services.GetArrayLength() > 0)
            // The marker probe's own service ("stress.marker") is not the workload under test.
            service = services.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(n => n is not null && !n.StartsWith("stress.", StringComparison.Ordinal));

        async Task<string?> MetricAsync(string type)
        {
            if (await GetAsync($"api/tenants/{tenantId}/metrics/catalog?{range}&type={type}&groupBy=name&size=1") is not { ValueKind: JsonValueKind.Object } page) return null;
            foreach (var p in page.EnumerateObject())
                if (p.NameEquals("names") && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                    foreach (var n in p.Value[0].EnumerateObject())
                        if (n.NameEquals("name")) return n.Value.GetString();
            return null;
        }
        return new TourData(service, await MetricAsync("HISTOGRAM"), await MetricAsync("SUM"));
    }
}
