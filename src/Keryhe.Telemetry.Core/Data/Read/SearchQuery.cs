using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// One parsed term of a <see cref="ParsedSearchQuery"/> — either a free-text substring term or a
/// <c>key:value</c>/<c>key=value</c> attribute filter, optionally negated. Field names/shape
/// mirror the TypeScript <c>SearchTerm</c> interface (<c>search-query.parser.ts</c>) deliberately,
/// so a future behavior diff between the two parsers is easy to spot.
/// </summary>
public sealed class SearchTerm
{
    public bool IsAttributeFilter { get; init; }
    public string? Key { get; init; }
    public string? Value { get; init; }
    public bool IsExactMatch { get; init; }
    public string? FreeText { get; init; }
    /// <summary>True when the term was prefixed with <c>-</c> (exclude matches rather than include).</summary>
    public bool Negate { get; init; }
}

/// <summary>Result of <see cref="SearchQueryParser.Parse"/>. Mirrors the TypeScript <c>ParsedSearchQuery</c> interface.</summary>
public sealed class ParsedSearchQuery
{
    public bool IsTraceIdSearch { get; init; }
    public string? TraceId { get; init; }
    public IReadOnlyList<SearchTerm> Terms { get; init; } = Array.Empty<SearchTerm>();

    public static readonly ParsedSearchQuery Empty = new() { Terms = Array.Empty<SearchTerm>() };
}

/// <summary>
/// Server-side port of <c>src/telemetry-client/src/app/shared/utils/search-query.parser.ts</c>
/// (list-pages-server-side plan, Phase 1, decision 10) — parsed once here so every provider shares
/// one grammar instead of each doing its own ad hoc parsing. This is a deliberate line-for-line
/// port: copy the TypeScript file's behavior exactly, including its quirks, rather than
/// "improving" it — see the <c>eq</c>-before-<c>colon</c> precedence note below for the one place
/// this is easy to get wrong.
/// </summary>
public static class SearchQueryParser
{
    private static readonly Regex HexRegex = new("^[0-9a-fA-F]+$", RegexOptions.Compiled);

    public static ParsedSearchQuery Parse(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return ParsedSearchQuery.Empty;

        var trimmed = searchText.Trim();

        // Trace ID: exactly 32 hex chars, no spaces, =, :, or quotes, anywhere in the WHOLE input
        // (not per-term) — this check runs before any AND-splitting.
        if (trimmed.Length == 32
            && !trimmed.Contains(' ')
            && !trimmed.Contains('=')
            && !trimmed.Contains(':')
            && !trimmed.Contains('"')
            && HexRegex.IsMatch(trimmed))
        {
            return new ParsedSearchQuery { IsTraceIdSearch = true, TraceId = trimmed, Terms = Array.Empty<SearchTerm>() };
        }

        var rawTerms = trimmed
            .Split(" AND ")
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var terms = rawTerms.Select(raw =>
        {
            // A leading `-` (before a key/value or free text) excludes matches.
            var negate = raw.StartsWith('-');
            var term = negate ? raw[1..].Trim() : raw;
            var eq = term.IndexOf('=');
            var colon = term.IndexOf(':');

            // `eq >= 0` always wins when present, regardless of which character comes first in the
            // string (e.g. "a:b=c" splits at "=", giving key="a:b", value="c", exact match). This is
            // DELIBERATELY DIFFERENT from Query.cs's TagFilter.Parse, which picks whichever
            // delimiter appears first (exact = eq >= 0 && (colon < 0 || eq < colon)). Ported exactly
            // from search-query.parser.ts's own rule — do not "fix" this to match TagFilter.Parse.
            if (eq >= 0)
            {
                return new SearchTerm
                {
                    IsAttributeFilter = true,
                    Key = term[..eq].Trim(),
                    Value = TrimQuotes(term[(eq + 1)..].Trim()),
                    IsExactMatch = true,
                    Negate = negate
                };
            }
            if (colon >= 0)
            {
                return new SearchTerm
                {
                    IsAttributeFilter = true,
                    Key = term[..colon].Trim(),
                    Value = TrimQuotes(term[(colon + 1)..].Trim()),
                    IsExactMatch = false,
                    Negate = negate
                };
            }
            return new SearchTerm
            {
                IsAttributeFilter = false,
                IsExactMatch = false,
                FreeText = TrimQuotes(term),
                Negate = negate
            };
        }).ToList();

        return new ParsedSearchQuery { IsTraceIdSearch = false, Terms = terms };
    }

    /// <summary>Strips any run of leading/trailing <c>'</c>/<c>"</c> characters — equivalent to <c>/^['"]+|['"]+$/g</c>.</summary>
    private static string TrimQuotes(string s) => s.Trim('\'', '"');
}
