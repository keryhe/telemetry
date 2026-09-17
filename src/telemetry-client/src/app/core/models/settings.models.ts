export interface RetentionSettings {
  traceRetentionDays: number;
  logRetentionDays: number;
  metricRetentionDays: number;
  // Absent when sending a PUT — the server sets this itself and ignores whatever the client
  // sends, so there is nothing meaningful to round-trip.
  updatedAt?: string;
}
