namespace Keryhe.Telemetry.Alerting.Models;

public class MetricThresholdCondition
{
    public string MetricName { get; set; } = "";
    /// <summary>Comparison operator: &gt;, &lt;, &gt;=, &lt;=</summary>
    public string Operator { get; set; } = ">";
    public double Threshold { get; set; }
}

public class ErrorRateCondition
{
    public double ThresholdPercent { get; set; } = 5.0;
    public int WindowMinutes { get; set; } = 5;
}

public class SlowTraceCondition
{
    public int MinDurationMs { get; set; } = 1000;
    public int WindowMinutes { get; set; } = 10;
}

public class LogSeveritySpikeCondition
{
    /// <summary>
    /// Minimum OTel severity number. Common values: 5=Debug, 9=Info, 13=Warn, 17=Error, 21=Fatal.
    /// </summary>
    public int MinSeverity { get; set; } = 17;
    public int CountThreshold { get; set; } = 10;
    public int WindowMinutes { get; set; } = 5;
}
