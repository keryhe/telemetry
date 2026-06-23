// Client-side port of Blazor's SearchQueryParser.cs — shared by the Logs and Traces pages.
// Supports: free text, exact `key=value`, contains `key:value`, `AND` between terms,
// and trace-id detection (32 hex chars => fetch by trace, date range ignored).

export interface SearchTerm {
  isAttributeFilter: boolean;
  key?: string;
  value?: string;
  isExactMatch: boolean;
  freeText?: string;
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

  const terms: SearchTerm[] = rawTerms.map((term) => {
    const eq = term.indexOf('=');
    const colon = term.indexOf(':');

    if (eq >= 0) {
      return {
        isAttributeFilter: true,
        key: term.slice(0, eq).trim(),
        value: trimQuotes(term.slice(eq + 1).trim()),
        isExactMatch: true,
      };
    }
    if (colon >= 0) {
      return {
        isAttributeFilter: true,
        key: term.slice(0, colon).trim(),
        value: trimQuotes(term.slice(colon + 1).trim()),
        isExactMatch: false,
      };
    }
    return {
      isAttributeFilter: false,
      isExactMatch: false,
      freeText: trimQuotes(term),
    };
  });

  return { isTraceIdSearch: false, terms };
}
