using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>
/// What the correctness ledger expects of the schema under test, per table and per schema version (schema-simplification plan, Phase 1 item 5).
/// A table "collapses" a re-delivered export when it has a unique key on the rows' identity: spans did on 2.x, do not on 3.0.0; log records and
/// data points never did.
/// </summary>
public static class LedgerSemantics
{
    /// <summary>The <c>TELEMETRY_TARGET_VERSION</c> in <c>schema/apply-schema.sh</c>, or null when the script or the line is missing.</summary>
    public static string? TargetSchemaVersion(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "schema", "apply-schema.sh");
        if (!File.Exists(path)) return null;
        var match = Regex.Match(File.ReadAllText(path), "^TELEMETRY_TARGET_VERSION=\"([^\"]+)\"", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>True for a 2.x schema (and an unknown one, the conservative pre-3.0.0 behaviour); false from 3.0.0 on.</summary>
    public static bool SpansCollapseRedelivery(string? schemaVersion) =>
        !(Version.TryParse(schemaVersion, out var v) && v.Major >= 3);
}
