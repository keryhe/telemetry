using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keryhe.Telemetry.Api.Export;

/// <summary>
/// CSV formula-injection guard (list-pages-server-side plan, Phase 8): a cell whose text starts
/// with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is prefixed with a leading <c>'</c> before it is
/// written. Spreadsheet applications (Excel, Google Sheets, LibreOffice) treat any of those four
/// leading characters as "this cell is a formula," so an unescaped attribute value or log body
/// containing something like <c>=cmd|'/c calc'!A1</c> would execute when the exported file is
/// opened — this is the well-known CSV/formula-injection class of vulnerability, not a
/// hypothetical. The leading apostrophe is itself the standard, widely-supported mitigation: every
/// major spreadsheet application renders a leading <c>'</c> as "treat the rest of this cell as
/// literal text" and does not display the apostrophe itself.
/// </summary>
public static class CsvFormulaGuard
{
    private static readonly char[] DangerousLeadingChars = ['=', '+', '-', '@'];

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        return DangerousLeadingChars.Contains(value[0]) ? "'" + value : value;
    }
}

/// <summary>
/// Minimal streaming CSV writer over an arbitrary <see cref="Stream"/> (the HTTP response body for
/// every caller in this codebase). RFC 4180 quoting: a cell containing a comma, quote or newline is
/// wrapped in double quotes with internal quotes doubled. Every cell is passed through
/// <see cref="CsvFormulaGuard.Escape"/> first — quoting happens on the ALREADY-guarded text, so a
/// guarded cell that also needs quoting (e.g. <c>=SUM(A1:A2)</c> containing no comma but starting
/// with <c>=</c>) still gets both defenses applied in the right order.
/// </summary>
public sealed class CsvRowWriter : IAsyncDisposable
{
    private static readonly char[] NeedsQuoting = [',', '"', '\n', '\r'];
    private readonly StreamWriter _writer;

    public CsvRowWriter(Stream stream)
    {
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
    }

    public Task WriteHeaderAsync(IEnumerable<string> columns) => WriteRowAsync(columns);

    public async Task WriteRowAsync(IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) await _writer.WriteAsync(',');
            first = false;
            await _writer.WriteAsync(FormatCell(cell));
        }
        await _writer.WriteAsync('\n');
    }

    public Task FlushAsync() => _writer.FlushAsync();

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }

    private static string FormatCell(string? raw)
    {
        var guarded = CsvFormulaGuard.Escape(raw);
        if (guarded.IndexOfAny(NeedsQuoting) < 0)
            return guarded;
        return "\"" + guarded.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>Minimal streaming NDJSON writer: one <c>JsonSerializer.SerializeAsync</c> call per line, followed by a bare <c>\n</c>.</summary>
public static class NdjsonRowWriter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
        await stream.WriteAsync(NewLine, cancellationToken);
    }
}

/// <summary>The two export formats <c>/api/*/export?format=</c> accepts.</summary>
public enum ExportFormat
{
    Ndjson,
    Csv
}

public static class ExportFormatParser
{
    public static bool TryParse(string? format, out ExportFormat result)
    {
        switch ((format ?? "ndjson").Trim().ToLowerInvariant())
        {
            case "ndjson":
                result = ExportFormat.Ndjson;
                return true;
            case "csv":
                result = ExportFormat.Csv;
                return true;
            default:
                result = default;
                return false;
        }
    }

    public static string ContentType(this ExportFormat format) => format switch
    {
        ExportFormat.Csv => "text/csv",
        _ => "application/x-ndjson"
    };

    public static string FileExtension(this ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        _ => "ndjson"
    };
}
