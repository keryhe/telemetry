using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// The compactor's lease (<c>_leases/compaction.lease</c>, phase 4 decision 2): the owner's id and an expiry, replaced by temp +
/// rename. The owner renews it every interval; another instance takes it over once it has expired. It is a liveness device, not a
/// lock: two compactors overlapping for a moment (both read an expired lease, or the lease moves while a pass runs) are made
/// harmless by the exclusive generation publish, so this does not need to be airtight.
/// </summary>
public sealed class ParquetCompactionLease(IParquetFileStore store, IOptions<ParquetOptions> options, TimeProvider time)
{
    public const string RelativePath = "_leases/compaction.lease";

    public string OwnerId { get; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    private sealed record LeaseFile(string Owner, long ExpiresUnixMs);

    /// <summary>True when this instance holds the lease after the call (it took it, or renewed its own).</summary>
    public async Task<bool> TryAcquireOrRenewAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var current = await ReadAsync(ct);
        if (current is not null && current.Owner != OwnerId && now < current.ExpiresUnixMs) return false;

        var expires = now + options.Value.Compaction.LeaseSeconds * 1000L;
        await store.WriteTextAsync(RelativePath, JsonSerializer.Serialize(new LeaseFile(OwnerId, expires)), ct);

        // Two instances that both found it expired both write; the later rename wins, and the other sees that here.
        return (await ReadAsync(ct))?.Owner == OwnerId;
    }

    /// <summary>Gives the lease up so another instance can take it at once (a graceful stop).</summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current?.Owner == OwnerId)
            await store.WriteTextAsync(RelativePath, JsonSerializer.Serialize(new LeaseFile(OwnerId, 0)), ct);
    }

    /// <summary>The current owner and when its lease runs out (Unix milliseconds); null if there is none.</summary>
    public async Task<(string Owner, long ExpiresUnixMs)?> ReadCurrentAsync(CancellationToken ct)
        => await ReadAsync(ct) is { } lease ? (lease.Owner, lease.ExpiresUnixMs) : null;

    private async Task<LeaseFile?> ReadAsync(CancellationToken ct)
    {
        var text = await store.ReadTextAsync(RelativePath, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<LeaseFile>(text); }
        catch (JsonException) { return null; }
    }
}
