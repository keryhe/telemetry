namespace Keryhe.Telemetry.Client.Services;

public record SearchTerm(
    bool IsAttributeFilter,
    string? Key,
    string? Value,
    bool IsExactMatch,
    string? FreeText);

public record ParsedSearchQuery(
    bool IsTraceIdSearch,
    string? TraceId,
    List<SearchTerm> Terms);

public static class SearchQueryParser
{
    public static ParsedSearchQuery Parse(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return new ParsedSearchQuery(false, null, new List<SearchTerm>());

        var trimmed = searchText.Trim();

        // Trace ID: 32 hex chars, no spaces, =, :, or quotes
        if (trimmed.Length == 32 &&
            !trimmed.Contains(' ') &&
            !trimmed.Contains('=') &&
            !trimmed.Contains(':') &&
            !trimmed.Contains('"') &&
            IsHexString(trimmed))
        {
            return new ParsedSearchQuery(true, trimmed, new List<SearchTerm>());
        }

        var rawTerms = trimmed.Split(new[] { " AND " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var terms = new List<SearchTerm>();

        foreach (var raw in rawTerms)
        {
            var term = raw.Trim();

            if (term.Contains('='))
            {
                var parts = term.Split('=', 2);
                terms.Add(new SearchTerm(true, parts[0].Trim(), parts[1].Trim().Trim('"', '\''), true, null));
            }
            else if (term.Contains(':'))
            {
                var parts = term.Split(':', 2);
                terms.Add(new SearchTerm(true, parts[0].Trim(), parts[1].Trim().Trim('"', '\''), false, null));
            }
            else
            {
                terms.Add(new SearchTerm(false, null, null, false, term.Trim('"')));
            }
        }

        return new ParsedSearchQuery(false, null, terms);
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
