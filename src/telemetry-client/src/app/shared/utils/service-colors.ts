/** Shared categorical palette for coloring services across the trace views. */
export const SERVICE_COLORS = [
  '#1976d2', '#f57c00', '#388e3c', '#7b1fa2',
  '#00838f', '#5d4037', '#558b2f', '#4527a0',
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
