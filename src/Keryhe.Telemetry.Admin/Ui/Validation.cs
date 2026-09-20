using Spectre.Console;

namespace Keryhe.Telemetry.Admin.Ui;

/// <summary>Client-side validation shared by the tenant-name and key-name prompts. See
/// plans/admin-tui.md, sections 6.3 and 6.5.</summary>
public static class Validation
{
    // VARCHAR(255) / NVARCHAR(255) on every supported provider. A longer value is a driver-level
    // error on SQL Server and a silent truncation risk elsewhere, so it's rejected here first.
    public const int MaxNameLength = 255;

    public static ValidationResult ValidateName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return ValidationResult.Error("Name cannot be empty.");
        if (trimmed.Length > MaxNameLength) return ValidationResult.Error($"Name cannot exceed {MaxNameLength} characters.");
        return ValidationResult.Success();
    }
}
