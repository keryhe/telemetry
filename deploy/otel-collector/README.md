# Client-side OpenTelemetry Collector for Keryhe

`client-gateway.yaml` is an OpenTelemetry Collector configuration for the **client** side: your applications send OTLP to it with no key (gRPC 4317,
HTTP 4318), and its exporter adds your tenant's Keryhe API key and ships to the Keryhe collector. Why you would run one, how keys rotate and what
it sees when Keryhe is full are in [Sending through an OpenTelemetry Collector](../../docs/COLLECTOR.md#sending-through-an-opentelemetry-collector).

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

## The persistent queue

The `file_storage` sending queue (data sent while Keryhe is down survives a restart of this Collector) has not been tested against
Keryhe. To check it, restart the Collector while the Keryhe collector is down:

```bash
docker compose up -d otelcol
# stop the Keryhe collector, then:
docker compose --profile tools run --rm telemetrygen-traces
docker compose restart otelcol              # the queue is on the 'queue' volume
# start the Keryhe collector: the spans should arrive
```
