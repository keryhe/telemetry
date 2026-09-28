using System.Collections.Concurrent;
using System.Globalization;

namespace Keryhe.Telemetry.StressTests.Observers.Process;

/// <summary>
/// One published value of one instrument in one interval. <see cref="Kind"/> is <c>counter</c> (Value =
/// increase over the interval), <c>updowncounter</c>, <c>gauge</c>, or <c>histogram</c> (Value = mean
/// over the interval, with <see cref="Count"/>, <see cref="Sum"/> and the interval's percentiles).
/// </summary>
public sealed record MetricSample(
    DateTimeOffset At, string Meter, string Instrument, string Kind, IReadOnlyDictionary<string, string> Tags,
    double Value, double? Count = null, double? Sum = null, double? P50 = null, double? P95 = null, double? P99 = null);

/// <summary>Thread-safe append-only store of a host's metric samples, with the lookups the harness needs.</summary>
public sealed class MetricStore
{
    private readonly ConcurrentQueue<MetricSample> _samples = new();

    public void Add(MetricSample sample) => _samples.Enqueue(sample);

    public IReadOnlyList<MetricSample> All() => _samples.ToArray();

    public IReadOnlyList<MetricSample> Series(string instrument, string? tagKey = null, string? tagValue = null) =>
        _samples.Where(s => s.Instrument == instrument && Matches(s, tagKey, tagValue)).ToList();

    /// <summary>Samples of an instrument (optionally for one tag value) taken in <c>[from, to]</c>.</summary>
    public IReadOnlyList<MetricSample> Window(string instrument, DateTimeOffset from, DateTimeOffset to, string? tagKey = null, string? tagValue = null) =>
        _samples.Where(s => s.Instrument == instrument && s.At >= from && s.At <= to && Matches(s, tagKey, tagValue)).ToList();

    /// <summary>The most recent sample of the instrument (optionally for one tag value), or null if none arrived.</summary>
    public MetricSample? Latest(string instrument, string? tagKey = null, string? tagValue = null) =>
        _samples.Where(s => s.Instrument == instrument && Matches(s, tagKey, tagValue)).OrderBy(s => s.At).LastOrDefault();

    /// <summary>Total of a counter's per-interval increases across the whole run (optionally for one tag value).</summary>
    public double Total(string instrument, string? tagKey = null, string? tagValue = null) =>
        Series(instrument, tagKey, tagValue).Where(s => s.Kind == "counter" && !double.IsNaN(s.Value)).Sum(s => s.Value);

    private static bool Matches(MetricSample s, string? key, string? value) =>
        key is null || (s.Tags.TryGetValue(key, out var v) && v == value);

    internal static double ParseDouble(object? value) =>
        value is null ? double.NaN
        : value is IConvertible c && value is not string ? c.ToDouble(CultureInfo.InvariantCulture)
        : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}
