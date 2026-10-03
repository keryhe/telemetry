/** The API root of one tenant: `{apiUrl}/tenants/{tenantId}`. Tenant-scoped services append their own segment. */
export function tenantApiUrl(apiUrl: string, tenantId: number): string {
  return `${apiUrl}/tenants/${tenantId}`;
}
