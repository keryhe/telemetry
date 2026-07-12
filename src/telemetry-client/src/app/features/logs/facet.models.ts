/** The inferred type of an attribute value, used for the field's type glyph. */
export type FacetValueType = 'string' | 'number' | 'boolean';

/** One attribute value in a facet, with its count, share, and whether it's currently included/excluded. */
export interface FacetValue {
  value: string;
  count: number;
  /** Bar width %: count relative to the key's most-common value (legibility). */
  pct: number;
  /** Share of the key's total occurrences (count / total), for the numeric label. */
  share: number;
  active: boolean;
  excluded: boolean;
}

/** One attribute key in the faceting sidebar: its full value list, distinct-value count, and inferred type. */
export interface Facet {
  key: string;
  type: FacetValueType;
  values: FacetValue[];
  distinct: number;
  total: number;
}
