using Google.Protobuf.Collections;
using Keryhe.Telemetry.Collector.Services;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Threading;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Collector improvements phase 2, no database: the converter's limits at each edge, the column clip, and the gate's byte budget.</summary>
[Trait("Suite", "InputLimits")]
public class InputLimitsTests
{
    private static OtlpAttributeConverter Converter(Action<IngestionLimitsOptions>? configure = null)
    {
        var limits = new IngestionLimitsOptions();
        configure?.Invoke(limits);
        return new OtlpAttributeConverter(Options.Create(limits), new IngestionMetrics());
    }

    private static RepeatedField<KeyValue> Attrs(params (string Key, AnyValue Value)[] kvs)
    {
        var f = new RepeatedField<KeyValue>();
        foreach (var (k, v) in kvs) f.Add(new KeyValue { Key = k, Value = v });
        return f;
    }

    private static AnyValue Str(string s) => new() { StringValue = s };

    private static AnyValue Nested(int levels)
    {
        var v = Str("leaf");
        for (var i = 0; i < levels; i++) v = new AnyValue { ArrayValue = new ArrayValue { Values = { v } } };
        return v;
    }

    private static int DepthOf(object? value)
    {
        var depth = 0;
        while (value is object[] arr && arr.Length > 0) { depth++; value = arr[0]; }
        return depth;
    }

    // ---- attribute count ----

    [Theory]
    [InlineData(127, 0)]
    [InlineData(128, 0)]
    [InlineData(129, 1)]
    [InlineData(500, 372)]
    public void Attribute_count_is_capped_and_the_rest_reported_as_dropped(int sent, int expectedDropped)
    {
        var attrs = Attrs(Enumerable.Range(0, sent).Select(i => ($"k{i}", Str("v"))).ToArray());
        var result = Converter().ConvertAttributes(attrs, "traces", out var dropped);

        Assert.Equal(Math.Min(sent, 128), result.Count);
        Assert.Equal(expectedDropped, dropped);
        Assert.Contains("k0", result.Keys);   // the first ones are kept
    }

    [Fact]
    public void Zero_means_unlimited_for_every_limit()
    {
        var c = Converter(l => { l.MaxAttributes = 0; l.MaxAttributeKeyLength = 0; l.MaxAttributeValueLength = 0; l.MaxNestingDepth = 0; l.MaxElementsPerLevel = 0; });
        var attrs = Attrs(Enumerable.Range(0, 1000).Select(i => (new string('k', 5000) + i, Str(new string('v', 100_000)))).ToArray());
        var result = c.ConvertAttributes(attrs, "traces", out var dropped);

        Assert.Equal(1000, result.Count);
        Assert.Equal(0, dropped);
        Assert.All(result.Values, v => Assert.Equal(100_000, ((string)v).Length));
        Assert.Equal(40, DepthOf(c.ConvertAnyValue(Nested(40), "traces")));
    }

    // ---- key and value length ----

    [Theory]
    [InlineData(255, 255)]
    [InlineData(256, 256)]
    [InlineData(257, 256)]
    public void Attribute_key_is_cut_at_the_limit(int length, int expected)
    {
        var result = Converter().ConvertAttributes(Attrs((new string('k', length), Str("v"))), "traces");
        Assert.Equal(expected, Assert.Single(result.Keys).Length);
    }

    [Theory]
    [InlineData(16383, 16383)]
    [InlineData(16384, 16384)]
    [InlineData(16385, 16384)]
    public void String_value_is_cut_at_the_limit(int length, int expected)
    {
        var result = Converter().ConvertAttributes(Attrs(("k", Str(new string('v', length)))), "traces");
        Assert.Equal(expected, ((string)result["k"]).Length);
    }

    [Fact]
    public void Bytes_value_is_cut_at_the_value_limit()
    {
        var value = new AnyValue { BytesValue = Google.Protobuf.ByteString.CopyFrom(new byte[20_000]) };
        var result = Converter().ConvertAttributes(Attrs(("k", value)), "traces");
        Assert.Equal(16_384, ((byte[])result["k"]).Length);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 255 'a' then a pair (two chars) then more: a cut at 256 would leave a lone high surrogate.
        var s = new string('a', 255) + "\U0001F600" + "tail";
        var clipped = ColumnLimits.Clip(s, 256, out var was);

        Assert.True(was);
        Assert.Equal(255, clipped!.Length);
        Assert.False(char.IsHighSurrogate(clipped[^1]));
        // The same cut one character later keeps the whole pair.
        Assert.Equal(257, ColumnLimits.Clip(s, 257, out _)!.Length);
        // Not clipped, and unlimited, leave the value alone.
        Assert.Same("short", ColumnLimits.Clip("short", 256, out var none));
        Assert.False(none);
        Assert.Same(s, ColumnLimits.Clip(s, 0, out _));
        Assert.Null(ColumnLimits.Clip(null, 10, out _));
    }

    // ---- nesting depth and elements per level ----

    [Fact]
    public void Nesting_deeper_than_the_limit_is_cut_at_the_limit()
    {
        var c = Converter();
        // An attribute's own array is depth 1. Thirty-two arrays around a leaf are all kept, the leaf included.
        var kept = c.ConvertAttributes(Attrs(("k", Nested(32))), "traces");
        var chain = kept["k"];
        for (var i = 0; i < 31; i++) chain = ((object[])chain)[0];
        Assert.Equal("leaf", Assert.Single((object[])chain));

        // One more level is dropped: the chain still ends at depth 32, now holding nothing (the 33rd array is gone).
        var cut = c.ConvertAttributes(Attrs(("k", Nested(33))), "traces");
        var tail = cut["k"];
        for (var i = 0; i < 31; i++) tail = ((object[])tail)[0];
        Assert.Empty((object[])tail);
    }

    [Theory]
    [InlineData(1023, 1023)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    public void Array_elements_are_capped_per_level(int sent, int expected)
    {
        var array = new ArrayValue();
        for (var i = 0; i < sent; i++) array.Values.Add(Str("x"));
        var result = Converter().ConvertAttributes(Attrs(("k", new AnyValue { ArrayValue = array })), "traces");
        Assert.Equal(expected, ((object[])result["k"]).Length);
    }

    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    public void Map_entries_are_capped_per_level(int sent, int expected)
    {
        var map = new KeyValueList();
        for (var i = 0; i < sent; i++) map.Values.Add(new KeyValue { Key = $"k{i}", Value = Str("x") });
        var result = Converter().ConvertAttributes(Attrs(("k", new AnyValue { KvlistValue = map })), "traces");
        Assert.Equal(expected, ((Dictionary<string, object>)result["k"]).Count);
    }

    // ---- events and links ----

    [Theory]
    [InlineData(128, 128, 0)]
    [InlineData(129, 128, 1)]
    [InlineData(1000, 128, 872)]
    public void Events_and_links_are_capped_and_the_remainder_counted(int count, int kept, int dropped)
    {
        var allowed = Converter().Allow(count, 128, "traces", "events", out var cut);
        Assert.Equal((kept, dropped), (allowed, cut));
    }

    // ---- the resource hash ----

    [Fact]
    public void The_same_oversized_resource_always_hashes_the_same()
    {
        var c = Converter();
        ResourceModel Build(string tail) => new()
        {
            TenantId = 1,
            Attributes = c.ClipServiceName(c.ConvertAttributes(Attrs(("service.name", Str(new string('s', 300) + tail)), ("a", Str(new string('v', 20_000) + tail))), "traces"), "traces")
        };

        var a = Build("one");
        var b = Build("two");   // differs only beyond the cut

        Assert.Equal(255, ((string)a.Attributes["service.name"]).Length);
        Assert.Equal(TelemetryIngestionHelpers.HashResource(a), TelemetryIngestionHelpers.HashResource(b));
        // Control: a difference inside the kept part does change it.
        var other = new ResourceModel { TenantId = 1, Attributes = c.ConvertAttributes(Attrs(("service.name", Str("different"))), "traces") };
        Assert.NotEqual(TelemetryIngestionHelpers.HashResource(a), TelemetryIngestionHelpers.HashResource(other));
    }

    [Fact]
    public void Every_cut_is_counted_on_records_truncated_by_limit()
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name.EndsWith("records_truncated")) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            string? signal = null, limit = null;
            foreach (var t in tags) { if (t.Key == "signal") signal = (string?)t.Value; if (t.Key == "limit") limit = (string?)t.Value; }
            if (signal == "limits-count-test") seen.AddOrUpdate(limit!, v, (_, o) => o + v);   // a signal name only this test uses
        });
        listener.Start();

        var c = Converter();
        var many = Attrs(Enumerable.Range(0, 130).Select(i => ($"k{i}", Str("v"))).ToArray());
        many.Add(new KeyValue { Key = new string('k', 300), Value = Str("v") });
        c.ConvertAttributes(many, "limits-count-test");
        c.ConvertAttributes(Attrs(("k", Str(new string('v', 20_000)))), "limits-count-test");
        c.ConvertAttributes(Attrs(("k", Nested(40))), "limits-count-test");
        c.ClipColumn(new string('n', 400), ColumnLimits.SpanName, "limits-count-test", "span_name");

        Assert.Equal(3, seen["attributes"]);          // 131 sent, 128 kept
        Assert.Equal(1, seen["attribute_value"]);
        Assert.True(seen["depth"] >= 1);
        Assert.Equal(1, seen["span_name"]);
    }

    // ---- the gate's byte budget ----

    [Fact]
    public async Task Gate_admits_only_when_both_records_and_bytes_fit()
    {
        var gate = new RecordCountGate(capacity: 100, byteCapacity: 1000);
        Assert.True(await gate.TryAcquireAsync(10, 600, TimeSpan.Zero, CancellationToken.None));
        Assert.False(await gate.TryAcquireAsync(10, 600, TimeSpan.Zero, CancellationToken.None));    // records fit, bytes do not
        Assert.True(await gate.TryAcquireAsync(10, 400, TimeSpan.Zero, CancellationToken.None));
        Assert.True(gate.IsSaturated);                                                                  // bytes at capacity, records far below
        Assert.False(await gate.TryAcquireAsync(1, 1, TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(1000, gate.ResidentBytes);

        gate.Release(10, 600);
        Assert.False(gate.IsSaturated);
        Assert.Equal(400, gate.ResidentBytes);
        Assert.True(await gate.TryAcquireAsync(1, 500, TimeSpan.Zero, CancellationToken.None));

        // Records still bind on their own.
        var records = new RecordCountGate(capacity: 5, byteCapacity: 1_000_000);
        Assert.True(await records.TryAcquireAsync(5, 10, TimeSpan.Zero, CancellationToken.None));
        Assert.False(await records.TryAcquireAsync(1, 10, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Gate_still_admits_one_oversized_export_into_an_empty_gate_and_releases_both()
    {
        var gate = new RecordCountGate(capacity: 10, byteCapacity: 100);
        Assert.True(await gate.TryAcquireAsync(3, 5_000, TimeSpan.Zero, CancellationToken.None));   // far over the byte budget, but alone
        Assert.False(await gate.TryAcquireAsync(1, 1, TimeSpan.Zero, CancellationToken.None));
        gate.Release(3, 5_000);
        Assert.Equal((0, 0L), (gate.Resident, gate.ResidentBytes));
        Assert.True(await gate.TryAcquireAsync(1, 1, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task A_zero_byte_capacity_turns_the_byte_budget_off()
    {
        var gate = new RecordCountGate(capacity: 10, byteCapacity: 0);
        Assert.True(await gate.TryAcquireAsync(1, long.MaxValue / 2, TimeSpan.Zero, CancellationToken.None));
        Assert.True(await gate.TryAcquireAsync(1, long.MaxValue / 2, TimeSpan.Zero, CancellationToken.None));
        Assert.False(gate.IsSaturated);
    }
}
