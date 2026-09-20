namespace Keryhe.Telemetry.Admin.Data;

public sealed record TenantRow(long Id, string Name, DateTime CreatedAt, int KeyCount, int ActiveKeyCount);

public sealed record ApiKeyRow(
    long Id,
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    string KeyHash)
{
    /// <summary>First 8 characters of the full hash — enough to correlate a row with a log line,
    /// never the full 64. See plans/admin-tui.md, section 6.4.</summary>
    public string HashPrefix => KeyHash.Length >= 8 ? KeyHash[..8] : KeyHash;
}

/// <summary>Thrown when an insert violates a unique constraint the tool already knows about
/// (tenant name). Providers translate their own driver exception into this.</summary>
public sealed class UniqueConstraintViolationException(string message) : Exception(message);
