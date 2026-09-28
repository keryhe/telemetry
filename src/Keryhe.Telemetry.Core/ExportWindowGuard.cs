namespace Keryhe.Telemetry.Core;

/// <summary>
/// Enforces <see cref="ProviderCapabilities.ExportMaxWindowDays"/> (list-pages-server-side plan,
/// Phase 8, decision 17): unlike <see cref="RawSearchWindowGuard"/>, this limit applies to EVERY
/// export request regardless of filters — export streams full records/trace-summary rows/bucketed
/// series with no row cap, so the time window is the only thing bounding how much work one export
/// does. Checked before any query runs, mirroring <see cref="RawSearchWindowGuard"/>'s own
/// "reject before touching the database" shape.
/// </summary>
public static class ExportWindowGuard
{
    public readonly record struct Result(bool Allowed, string? Message)
    {
        public static readonly Result Ok = new(true, null);
    }

    public static Result Check(ProviderCapabilities capabilities, TimeSpan windowLength)
    {
        var maxDays = capabilities.ExportMaxWindowDays;
        if (windowLength <= TimeSpan.FromDays(maxDays))
            return Result.Ok;

        return new Result(false,
            $"Export is limited to a {maxDays}-day window on {capabilities.Tier} tier. Narrow the time range.");
    }
}
