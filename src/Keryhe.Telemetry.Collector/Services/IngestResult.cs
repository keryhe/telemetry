namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// What an ingestor did with an export that was not refused outright. <see cref="Rejected"/> counts the spans, log records or data points
/// the request itself made unusable (0 when everything was queued); <see cref="ErrorMessage"/> says why when it is not 0. Transports
/// answer success with a partial-success block built from it, as OTLP requires for per-request permanent problems.
/// </summary>
public sealed record IngestResult(int Rejected, string ErrorMessage)
{
    public static IngestResult Ok { get; } = new(0, "");
}
