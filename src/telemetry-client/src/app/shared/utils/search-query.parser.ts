// Client-side port of Blazor's SearchQueryParser.cs — shared by the Logs and Traces pages.
// Supports: free text, exact `key=value`, contains `key:value`, `AND` between terms,
// a leading `-` to negate/exclude a term, and trace-id detection (32 hex chars =>
// fetch by trace, date range ignored).

export interface SearchTerm {
  isAttributeFilter: boolean;
  key?: string;
  value?: string;
  isExactMatch: boolean;
  freeText?: string;
  /** True when the term is prefixed with `-` (exclude matches rather than include). */
  negate?: boolean;
}

/** Build the `key=value` / `key:value` (optionally `-`-prefixed) text for a facet click. */
export function buildAttributeTerm(key: string, value: string, exclude = false): string {
  // Contains-match (`:`) keeps facet clicks consistent with the search box default.
  return `${exclude ? '-' : ''}${key}:${quoteIfNeeded(value)}`;
}

function quoteIfNeeded(value: string): string {
  return /[\s:=]/.test(value) ? `"${value}"` : value;
}

export interface ParsedSearchQuery {
  isTraceIdSearch: boolean;
  traceId?: string;
  terms: SearchTerm[];
}

const HEX_RE = /^[0-9a-fA-F]+$/;

function trimQuotes(s: string): string {
  return s.replace(/^['"]+|['"]+$/g, '');
}

export function parseSearchQuery(searchText: string): ParsedSearchQuery {
  if (!searchText || !searchText.trim()) {
    return { isTraceIdSearch: false, terms: [] };
  }

  const trimmed = searchText.trim();

  // Trace ID: exactly 32 hex chars, no spaces, =, :, or quotes.
  if (
    trimmed.length === 32 &&
    !trimmed.includes(' ') &&
    !trimmed.includes('=') &&
    !trimmed.includes(':') &&
    !trimmed.includes('"') &&
    HEX_RE.test(trimmed)
  ) {
    return { isTraceIdSearch: true, traceId: trimmed, terms: [] };
  }

  const rawTerms = trimmed
    .split(' AND ')
    .map((t) => t.trim())
    .filter((t) => t.length > 0);

  const terms: SearchTerm[] = rawTerms.map((raw) => {
    // A leading `-` (before a key/value or free text) excludes matches.
    const negate = raw.startsWith('-');
    const term = negate ? raw.slice(1).trim() : raw;
    const eq = term.indexOf('=');
    const colon = term.indexOf(':');

    if (eq >= 0) {
      return {
        isAttributeFilter: true,
        key: term.slice(0, eq).trim(),
        value: trimQuotes(term.slice(eq + 1).trim()),
        isExactMatch: true,
        negate,
      };
    }
    if (colon >= 0) {
      return {
        isAttributeFilter: true,
        key: term.slice(0, colon).trim(),
        value: trimQuotes(term.slice(colon + 1).trim()),
        isExactMatch: false,
        negate,
      };
    }
    return {
      isAttributeFilter: false,
      isExactMatch: false,
      freeText: trimQuotes(term),
      negate,
    };
  });

  return { isTraceIdSearch: false, terms };
}
