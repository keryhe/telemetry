# Client-side OpenTelemetry Collector for Keryhe

`client-gateway.yaml` is an OpenTelemetry Collector configuration for the **client** side: your applications send OTLP to it with no key (gRPC 4317,
HTTP 4318), and its exporter adds your tenant's Keryhe API key and ships to the Keryhe collector. Why you would run one, how keys rotate and what
it sees when Keryhe is full are in the collector's README, [Sending through an OpenTelemetry Collector](../../src/Keryhe.Telemetry.Collector/README.md#sending-through-an-opentelemetry-collector).

| File | |
|---|---|
| `client-gateway.yaml` | The configuration. Needs the **contrib** distribution (`file_storage` is a contrib extension). Environment: `KERYHE_ENDPOINT`, `KERYHE_API_KEY`, `KERYHE_TLS_INSECURE`, `KERYHE_CA_FILE` |
| `docker-compose.yaml` | Runs it next to `telemetrygen` for a local check |

## Try it

```bash
export KERYHE_API_KEY=ktel_...            # a key for a tenant in the database the collector uses
docker compose up -d otelcol              # KERYHE_ENDPOINT defaults to host.docker.internal:5117 (the development collector, plaintext)
docker compose --profile tools run --rm telemetrygen-traces
docker compose --profile tools run --rm telemetrygen-logs
docker compose --profile tools run --rm telemetrygen-metrics
```

Against a TLS collector set `KERYHE_ENDPOINT=collector.example.com:7057 KERYHE_TLS_INSECURE=false` (and `KERYHE_CA_FILE` for a private CA, mounted into the container).

## What was checked, and how

On 2026-10-09, against the real collector host (`Keryhe.Telemetry.Collector.Server`, Development, PostgreSQL in a throwaway container) and
`otel/opentelemetry-collector:0.157.0` running this configuration, sending OTLP/JSON to its 4318 receiver:

| Check | Result |
|---|---|
| Traces, logs and metrics through the Collector, no key from the sender | Stored for the key's tenant: 100 spans, 100 log records, 100 data points |
| Keryhe stopped, 80 spans/logs/points sent, Keryhe started again | The Collector retried while Keryhe was down (`Exporting failed. Will retry`) and delivered all 80 of each after the restart |
| Keryhe's queue forced full (100 spans, 100 ms wait), 3,500 spans and logs sent | Keryhe answered `UNAVAILABLE` with `RetryInfo`; the Collector logged `Throttle (1.46s) ... The traces ingestion queue is not accepting data (throttled); retry in 1.5s`, backed off, and all 3,500 spans and 3,500 log records were stored: the Collector's exporter **honours `RetryInfo`** |
| An invalid key | `not retryable error: Permanent error: rpc error: code = Unauthenticated desc = Unauthenticated: invalid API key.`, `Dropping data.`, nothing stored, no retries: that log line is the client's signal to fix the key |
| `MaxConnectionAgeSeconds: 15` on Keryhe, one export every 4 s for 80 s (about five connection lifetimes) | Every export stored (200 of 200), zero failed exports in the Collector's log: its Go gRPC client reconnects on `GOAWAY` without loss |
| `validate` of the configuration | Accepted |

**Not verified here:** the `file_storage` sending queue, i.e. that data sent while Keryhe is down survives a restart of the *client* Collector. The
contrib image could not be pulled in the environment where this was checked (Docker's registry traffic stalled), so the checks above ran on the core
distribution with the same configuration minus the `file_storage` extension and its `storage:` line (an in-memory queue). The `file_storage` settings
follow the Collector's documented configuration but have not been run against this Keryhe. Before relying on it, repeat the outage check with the contrib image and
restart the Collector while Keryhe is down:

```bash
docker compose up -d otelcol
# stop the Keryhe collector, then:
docker compose --profile tools run --rm telemetrygen-traces
docker compose restart otelcol              # the queue is on the 'queue' volume
# start the Keryhe collector: the spans should arrive
```
