using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;
using Xunit;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

public class SimulationInvariantTests
{
    private static readonly SimChunkSet Data = TestSupport.Evening();

    [Fact]
    public void Produces_a_realistic_volume_of_every_signal()
    {
        Assert.True(Data.Chunk.Traces.Count > 500, $"only {Data.Chunk.Traces.Count} traces in 10 evening minutes");
        Assert.True(Data.Spans.Count > Data.Chunk.Traces.Count * 4, "traces should average several spans");
        Assert.True(Data.Spans.SelectMany(s => s.Logs).Count() > 1000);
        Assert.NotEmpty(Data.Chunk.Samples);
        Assert.Equal(Services.All.Count(), Data.Spans.Select(s => s.Instance.Service).Distinct().Count());
    }

    [Fact]
    public void Every_span_belongs_to_a_well_formed_tree()
    {
        var byId = Data.Spans.ToDictionary(s => s.SpanId);
        Assert.Equal(Data.Spans.Count, byId.Count); // span ids are unique

        foreach (var span in Data.Spans)
        {
            Assert.True(span.End >= span.Start, $"{span.Name} ends before it starts");
            if (span.ParentSpanId is null)
            {
                Assert.Contains(span, Data.Chunk.Traces);
                continue;
            }
            var parent = byId[span.ParentSpanId];
            Assert.Equal(parent.TraceId, span.TraceId);
            Assert.Contains(span, parent.Children);
            Assert.True(span.Start >= parent.Start, $"{span.Name} starts before its parent {parent.Name}");
            Assert.True(span.End <= parent.End, $"{span.Name} ends after its parent {parent.Name}");
        }
    }

    [Fact]
    public void Each_trace_has_exactly_one_root()
    {
        foreach (var group in Data.Spans.GroupBy(s => s.TraceId))
            Assert.Single(group, s => s.ParentSpanId is null);
    }

    [Fact]
    public void Roots_are_user_requests_or_message_consumers()
    {
        foreach (var root in Data.Chunk.Traces)
        {
            if (root.Kind == SimSpanKind.Consumer) Assert.Equal(Services.Notification, root.Instance.Service);
            else
            {
                Assert.Equal(SimSpanKind.Server, root.Kind);
                Assert.Equal(Services.Frontend, root.Instance.Service);
            }
        }
    }

    [Fact]
    public void Service_calls_pair_a_client_span_with_a_server_span_in_the_callee()
    {
        var calls = Data.Spans.Where(s => s.Kind == SimSpanKind.Client && s.Attributes.Get("peer.service") is string peer
            && Services.BaselineVersions.ContainsKey(peer)).ToList();
        Assert.NotEmpty(calls);
        foreach (var client in calls)
        {
            var server = Assert.Single(client.Children);
            Assert.Equal(SimSpanKind.Server, server.Kind);
            Assert.Equal((string)client.Attributes.Get("peer.service")!, server.Instance.Service);
            Assert.NotEqual(client.Instance.PodName, server.Instance.PodName);
        }
    }

    [Fact]
    public void A_server_error_is_explained_by_an_exception_and_visible_to_the_caller()
    {
        var failed = Data.Spans.Where(s => s.Kind == SimSpanKind.Server && s.Status == SimStatus.Error).ToList();
        Assert.NotEmpty(failed); // the model injects failures, so ten minutes of traffic includes some
        foreach (var span in failed)
        {
            Assert.Contains(span.Events, e => e.Name == "exception");
            Assert.True((long)span.Attributes.Get("http.response.status_code")! >= 500);
            Assert.NotNull(span.Attributes.Get("error.type"));
        }

        // A caller sees a callee's 5xx on its own Client span.
        foreach (var client in Data.Spans.Where(s => s.Kind == SimSpanKind.Client && s.Children.Count == 1 && s.Children[0].Status == SimStatus.Error))
            Assert.Equal(SimStatus.Error, client.Status);
    }

    [Fact]
    public void A_dependency_failure_propagates_up_to_the_request()
    {
        // Pick roots that failed; each must contain a failing leaf or a failing downstream server span below it.
        var failedRoots = Data.Chunk.Traces.Where(r => r.Kind == SimSpanKind.Server && r.Status == SimStatus.Error).ToList();
        Assert.NotEmpty(failedRoots);
        foreach (var root in failedRoots)
            Assert.Contains(root.Descendants().Skip(1), s => s.Status == SimStatus.Error);
    }

    [Fact]
    public void Checkout_publishes_a_message_that_a_consumer_trace_links_back_to()
    {
        var producers = Data.Spans.Where(s => s.Kind == SimSpanKind.Producer).ToList();
        var consumers = Data.Spans.Where(s => s.Kind == SimSpanKind.Consumer).ToList();
        Assert.NotEmpty(producers);
        Assert.Equal(producers.Count, consumers.Count);

        foreach (var consumer in consumers)
        {
            var link = Assert.Single(consumer.Links);
            Assert.Contains(link.Target, producers);
            Assert.NotEqual(link.Target.TraceId, consumer.TraceId); // a new trace, linked rather than parented
            Assert.True(consumer.Start >= link.Target.End, "a message is consumed after it was published");
            Assert.Equal(link.Target.Attributes.Get("messaging.message.id"), consumer.Attributes.Get("messaging.message.id"));
        }
    }

    [Fact]
    public void Logs_belong_to_the_span_and_pod_that_wrote_them()
    {
        var checkedLogs = 0;
        foreach (var span in Data.Spans)
        {
            foreach (var log in span.Logs)
            {
                Assert.Same(span, log.Span);
                Assert.Equal(span.Instance, log.Instance);
                Assert.InRange(log.Time, span.Start, span.End);
                checkedLogs++;
            }
        }
        Assert.True(checkedLogs > 0);
    }

    [Fact]
    public void Errors_and_slow_work_surface_in_the_logs()
    {
        var logs = Data.Spans.SelectMany(s => s.Logs).ToList();
        Assert.Contains(logs, l => l.Severity == SimSeverity.Error && l.Attributes.Get("exception.type") is not null);
        Assert.Contains(logs, l => l.Severity == SimSeverity.Warn);
        Assert.Contains(logs, l => l.Severity == SimSeverity.Debug);
        Assert.Contains(logs, l => l.Body.StartsWith("Order ") && l.Body.Contains("created"));

        // Every server span writes the host's request-finished line, so request logs outnumber everything else.
        Assert.Equal(Data.Spans.Count(s => s.Kind == SimSpanKind.Server),
            logs.Count(l => l.Category == "Microsoft.AspNetCore.Hosting.Diagnostics"));
    }

    [Fact]
    public void Every_server_span_records_a_request_duration_measurement_that_matches_it()
    {
        foreach (var span in Data.Spans.Where(s => s.Kind == SimSpanKind.Server))
        {
            var m = Assert.Single(span.Measurements, m => m.Instrument == Instruments.HttpServerDuration);
            Assert.Equal((span.End - span.Start).TotalSeconds, m.Value, 6);
            Assert.Equal(span.Attributes.Get("http.route"), m.Attributes.Get("http.route"));
            Assert.Equal(span.Attributes.Get("http.response.status_code"), m.Attributes.Get("http.response.status_code"));
        }
    }

    [Fact]
    public void Business_counters_follow_the_requests_that_caused_them()
    {
        var placed = Data.Spans.SelectMany(s => s.Measurements).Count(m => m.Instrument == Instruments.OrdersPlaced);
        var producers = Data.Spans.Count(s => s.Kind == SimSpanKind.Producer);
        Assert.Equal(producers, placed); // an order is counted exactly when its message was published
        Assert.Contains(Data.Spans.SelectMany(s => s.Measurements), m => m.Instrument == Instruments.PaymentsProcessed);
    }

    [Fact]
    public void Spans_follow_semantic_conventions()
    {
        foreach (var span in Data.Spans)
        {
            switch (span.Kind)
            {
                case SimSpanKind.Server:
                    Assert.NotNull(span.Attributes.Get("http.request.method"));
                    Assert.NotNull(span.Attributes.Get("http.route"));
                    Assert.NotNull(span.Attributes.Get("http.response.status_code"));
                    break;
                case SimSpanKind.Client when span.Attributes.Get("db.system.name") is not null:
                    Assert.NotNull(span.Attributes.Get("db.operation.name"));
                    Assert.NotNull(span.Attributes.Get("server.address"));
                    break;
                case SimSpanKind.Producer:
                case SimSpanKind.Consumer:
                    Assert.NotNull(span.Attributes.Get("messaging.system"));
                    Assert.NotNull(span.Attributes.Get("messaging.destination.name"));
                    break;
            }
        }
    }

    [Fact]
    public void Pods_have_distinct_stable_identities()
    {
        var instances = Data.Spans.Select(s => s.Instance).Distinct().ToList();
        Assert.Equal(instances.Count, instances.Select(i => i.PodName).Distinct().Count());
        Assert.Equal(instances.Count, instances.Select(i => i.InstanceId).Distinct().Count());
        Assert.All(instances, i => Assert.StartsWith(i.Service + "-", i.PodName));
    }

    [Fact]
    public void Resource_samples_cover_every_pod_that_served_traffic()
    {
        var pods = Data.Spans.Select(s => s.Instance.PodName).ToHashSet();
        var sampled = Data.Chunk.Samples.Select(s => s.Instance.PodName).ToHashSet();
        Assert.Superset(pods, sampled);
        Assert.Contains(Data.Chunk.Samples, s => s.Instrument == Instruments.ProcessCpu && s.Value is > 0 and < 1);
        Assert.Contains(Data.Chunk.Samples, s => s.Instrument == Instruments.DbConnections && s.Attributes.Get("db.client.connection.state") is "used");
    }
}
