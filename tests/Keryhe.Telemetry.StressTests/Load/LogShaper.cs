using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;

namespace Keryhe.Telemetry.StressTests.Load;

public sealed class LogShaper
{
    private static readonly (string Name, int Number)[] Severities =
        [("TRACE", 1), ("DEBUG", 5), ("INFO", 9), ("WARN", 13), ("ERROR", 17), ("FATAL", 21)];

    private readonly LoadProfile _profile;
    private readonly Topology _topology;
    private readonly Random _rng;
    private readonly double[] _severityCumulative;
    private readonly string _textPool;

    public LogShaper(LoadProfile profile, Topology topology, Random rng)
    {
        _profile = profile;
        _topology = topology;
        _rng = rng;

        var running = 0.0;
        _severityCumulative = Severities
            .Select(s => running += profile.Logs.SeverityWeights.TryGetValue(s.Name, out var w) ? Math.Max(0, w) : 0)
            .ToArray();
        if (running <= 0) throw new InvalidDataException("Logs.SeverityWeights has no positive weight.");

        // One pool of pseudo-words that bodies slice from, instead of building a string per record.
        var chars = new char[Math.Max(4096, profile.Logs.BodyLength.Max + 64)];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = rng.Next(6) == 0 ? ' ' : (char)('a' + rng.Next(26));
        _textPool = new string(chars);
    }

    public Payload<ExportLogsServiceRequest> Next()
    {
        var tenantIndex = _topology.PickTenant(_rng);
        var services = _topology.ServicesByTenant[tenantIndex];
        var load = _profile.Logs;
        var target = _profile.Transport.RecordsPerExport;

        var byService = new Dictionary<string, ScopeLogs>();
        long current = 0, backdated = 0;

        for (var i = 0; i < target; i++)
        {
            var service = services[_rng.Next(services.Length)];
            var (nanos, age) = TimeStamps.Pick(_profile.Time, _rng);
            var (sevName, sevNumber) = PickSeverity();

            var length = load.BodyLength.Sample(_rng);
            var start = _rng.Next(_textPool.Length - length + 1);
            var record = new LogRecord
            {
                TimeUnixNano = (ulong)nanos,
                ObservedTimeUnixNano = (ulong)nanos,
                SeverityNumber = (SeverityNumber)sevNumber,
                SeverityText = sevName,
                Body = new AnyValue { StringValue = _textPool.Substring(start, length) }
            };
            for (var a = 0; a < load.AttributeCount; a++)
                record.Attributes.Add(Topology.Str($"attr.k{a}", $"v{_rng.Next(Math.Max(1, load.AttributeCardinality))}"));
            if (_rng.NextDouble() < load.TraceCorrelatedFraction)
            {
                record.TraceId = Topology.RandomId(_rng, 16);
                record.SpanId = Topology.RandomId(_rng, 8);
            }

            if (!byService.TryGetValue(service.Name, out var scope))
            {
                scope = new ScopeLogs { Scope = _topology.Scope };
                byService[service.Name] = scope;
            }
            scope.LogRecords.Add(record);

            if (age == RecordAge.Backdated) backdated++; else current++;
        }

        var request = new ExportLogsServiceRequest();
        foreach (var service in services.Where(s => byService.ContainsKey(s.Name)))
        {
            var rl = new ResourceLogs { Resource = service.Resource };
            rl.ScopeLogs.Add(byService[service.Name]);
            request.ResourceLogs.Add(rl);
        }

        var entries = new List<LedgerEntry>(2);
        if (current > 0) entries.Add(new LedgerEntry("log_records", RecordAge.Current, current, false, Dedups: false));
        if (backdated > 0) entries.Add(new LedgerEntry("log_records", RecordAge.Backdated, backdated, false, Dedups: false));
        // The summary rollup counts every log record, a re-delivered one again.
        if (current > 0) entries.Add(new LedgerEntry(RollupTables.Log, RecordAge.Current, current, false, Dedups: false));

        return new Payload<ExportLogsServiceRequest>(request, tenantIndex, target, entries,
            _rng.NextDouble() < _profile.Time.RedeliveryFraction);
    }

    private (string Name, int Number) PickSeverity()
    {
        var x = _rng.NextDouble() * _severityCumulative[^1];
        for (var i = 0; i < _severityCumulative.Length; i++)
            if (x < _severityCumulative[i]) return Severities[i];
        return Severities[^1];
    }
}
