-- =============================================================================
-- ALERT RULES AND EVENTS SCHEMA
-- =============================================================================

CREATE TABLE alert_rules (
    id               SERIAL PRIMARY KEY,
    name             TEXT NOT NULL,
    type             TEXT NOT NULL,            -- 'MetricThreshold' | 'ErrorRate' | 'SlowTrace' | 'LogSeveritySpike'
    service_name     TEXT,                     -- NULL = all services
    condition        JSONB NOT NULL,           -- type-specific parameters
    webhook_url      TEXT NOT NULL,
    cooldown_minutes INT NOT NULL DEFAULT 60,
    enabled          BOOLEAN NOT NULL DEFAULT TRUE,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_fired_at    TIMESTAMPTZ
);

CREATE TABLE alert_events (
    id         BIGSERIAL PRIMARY KEY,
    rule_id    INT NOT NULL REFERENCES alert_rules(id) ON DELETE CASCADE,
    fired_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    details    JSONB NOT NULL
);

CREATE INDEX idx_alert_events_rule_id  ON alert_events(rule_id);
CREATE INDEX idx_alert_events_fired_at ON alert_events(fired_at DESC);
CREATE INDEX idx_alert_rules_enabled   ON alert_rules(enabled) WHERE enabled = TRUE;

-- Example condition shapes (documentation):
--
-- MetricThreshold: { "MetricName": "cpu_usage", "Operator": ">", "Threshold": 90.0 }
-- ErrorRate:       { "ThresholdPercent": 5.0, "WindowMinutes": 5 }
-- SlowTrace:       { "MinDurationMs": 2000, "WindowMinutes": 10 }
-- LogSeveritySpike:{ "MinSeverity": 17, "CountThreshold": 10, "WindowMinutes": 5 }
--
-- MinSeverity values: 5=Debug, 9=Info, 13=Warn, 17=Error, 21=Fatal
