using System.Text.RegularExpressions;
using Keryhe.Telemetry.Core.Data;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Keeps <see cref="ColumnLimits"/> in step with the schema scripts. A value longer than its column is a permanent flush error
/// on the relational providers, so every sized text column must either be clipped to (at most) its size in conversion or be a
/// code-controlled value (an enum-like word, a hash, an id). A schema change that adds or shrinks a column fails here instead of
/// in production.
/// </summary>
[Trait("Suite", "InputLimits")]
public class ColumnLimitsTests
{
    private static readonly string[] Scripts = ["PostgreSQL", "SqlServer", "MySQL"];

    // Columns whose values the application itself produces from a fixed vocabulary, a hash or an id (never client text).
    private static readonly HashSet<string> Controlled = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "status_code", "type", "aggregation_temporality", "body_type", "version_label",
        "resource_hash", "scope_hash", "trace_id", "span_id", "parent_span_id", "signal_kind", "key_name"
    };

    private static string SchemaPath(string provider)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", $"{provider}-Telemetry.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "schema", $"{provider}-Telemetry.sql");
    }

    private static Dictionary<string, int> SizedColumns(string provider)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var table = "";
        foreach (var line in File.ReadLines(SchemaPath(provider)))
        {
            var t = Regex.Match(line, @"^\s*CREATE TABLE\s+[`""\[]?(\w+)", RegexOptions.IgnoreCase);
            if (t.Success) { table = t.Groups[1].Value; continue; }
            var c = Regex.Match(line, @"^\s*[`""\[]?(\w+)[`""\]]?\s+N?(?:VAR)?CHAR\((\d+)\)", RegexOptions.IgnoreCase);
            if (c.Success && table.Length > 0) result[$"{table}.{c.Groups[1].Value}"] = int.Parse(c.Groups[2].Value);
        }
        return result;
    }

    [Fact]
    public void Every_sized_client_text_column_is_in_ColumnLimits_and_no_larger_than_its_script_allows()
    {
        var unknown = new List<string>();
        var tooSmall = new List<string>();
        var minimumSeen = new Dictionary<string, int>();

        foreach (var provider in Scripts)
        {
            var sized = SizedColumns(provider);
            Assert.True(sized.ContainsKey("spans.name") && sized.Count >= 10, $"{provider}: the parser found {sized.Count} sized columns; it is not reading the script");
            foreach (var (name, size) in sized)
            {
                var column = name[(name.IndexOf('.') + 1)..];
                if (Controlled.Contains(column) || name.StartsWith("telemetry_schema_version.") || name.StartsWith("rollup_compaction.")) continue;

                if (!ColumnLimits.ByColumn.TryGetValue(name, out var limit)) { unknown.Add($"{provider}: {name} VARCHAR({size})"); continue; }
                if (size < limit) tooSmall.Add($"{provider}: {name} is {size} but ColumnLimits clips to {limit}");
                minimumSeen[name] = Math.Min(minimumSeen.GetValueOrDefault(name, int.MaxValue), size);
            }
        }

        Assert.True(unknown.Count == 0, "Sized columns missing from ColumnLimits (add them and clip in conversion, or mark them controlled):\n" + string.Join("\n", unknown));
        Assert.True(tooSmall.Count == 0, string.Join("\n", tooSmall));

        // ColumnLimits claims the smallest size across the scripts: no entry may be smaller than every script's column, or it clips needlessly.
        foreach (var (name, limit) in ColumnLimits.ByColumn)
            if (minimumSeen.TryGetValue(name, out var min))
                Assert.True(limit == min, $"ColumnLimits has {name} = {limit} but the smallest script size is {min}");
    }

    [Fact]
    public void Every_ColumnLimits_entry_names_a_column_that_exists_somewhere()
    {
        var all = Scripts.SelectMany(p => SizedColumns(p).Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ColumnLimits.ByColumn.Keys)
            Assert.True(all.Contains(name) || name.Contains("severity_text"), $"{name} is in ColumnLimits but no script sizes it");   // severity_text is sized on MySQL only
    }

    [Fact]
    public void ClickHouse_declares_no_sized_string_column()
    {
        var script = File.ReadAllText(SchemaPath("ClickHouse"));
        Assert.DoesNotMatch(@"\bFixedString\s*\(\s*\d+\s*\)\s*,?\s*(--.*)?$", string.Join("\n", script.Split('\n').Where(l => !l.TrimStart().StartsWith("--"))));
    }
}
