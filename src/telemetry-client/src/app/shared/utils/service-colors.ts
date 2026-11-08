/** Shared categorical palette for coloring services across the trace views. */
export const SERVICE_COLORS = [
  // No green/red/orange/yellow: those read as success/error/warning.
  '#1976d2', '#c2187e', '#0097a7', '#7b1fa2', // blue, magenta, cyan, purple
  '#546e7a', '#9575cd', '#283593', '#64b5f6', // slate, lavender, indigo, sky
];

/**
 * Deterministic color for a service name, stable across pages/views (unlike a
 * positional index).
 */
export function serviceColor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0;
  return SERVICE_COLORS[Math.abs(hash) % SERVICE_COLORS.length];
}
