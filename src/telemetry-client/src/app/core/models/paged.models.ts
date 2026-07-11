/** Server-side pagination envelope: one page of rows plus the full filtered total. */
export interface PagedResult<T> {
  items: T[];
  total: number;
  capped: boolean;
}
