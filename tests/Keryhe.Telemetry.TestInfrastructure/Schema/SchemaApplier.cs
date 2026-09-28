using System.Text;

namespace Keryhe.Telemetry.TestInfrastructure.Schema;

/// <summary>
/// Reads the raw <c>schema/*.sql</c> scripts (copied next to the binaries by this project) and
/// splits them for drivers that cannot run a whole multi-statement script through one command,
/// mirroring what <c>schema/apply-schema.sh</c> does per provider.
/// </summary>
public static class SchemaApplier
{
    public static Task<string> ReadScriptAsync(string fileName, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", fileName), cancellationToken);

    /// <summary>
    /// Splits a schema script into individually-executable statements for providers whose driver
    /// can't run a whole multi-statement script through one command (MySQL, ClickHouse).
    /// Statements are ended by a line that, once trimmed, ends with <c>;</c> — adequate for these
    /// DDL-only scripts, none of which embed a literal semicolon inside a string value.
    /// </summary>
    public static List<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--") || trimmed.Length == 0)
                continue;
            current.AppendLine(line);
            if (trimmed.EndsWith(';'))
            {
                var statement = current.ToString().Trim();
                if (statement.Length > 0)
                    statements.Add(statement);
                current.Clear();
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0)
            statements.Add(tail);
        return statements;
    }

    /// <summary>Splits a SQL Server script on lines that are exactly <c>GO</c> (case-insensitive), the batch separator <c>SqlCommand</c> doesn't understand.</summary>
    public static List<string> SplitGoBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString().Trim();
                if (batch.Length > 0)
                    batches.Add(batch);
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0)
            batches.Add(tail);
        return batches;
    }
}
