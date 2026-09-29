using System.Globalization;
using System.Net;
using System.Text;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>
/// Inline SVG line charts rendered in C# (stress-test plan, Phase 8): no scripts and nothing fetched, so a report opens offline. Every chart of a
/// scenario is drawn on the same x-axis (seconds since warm-up began) and the same plot width, with the same vertical markers, so a latency
/// spike can be matched to a lock burst or a retention sweep by eye. Styling comes from the page's CSS classes, so light and dark both work.
/// </summary>
public static class SvgChart
{
    public const int Width = 960, Height = 210;
    private const int Left = 62, Right = 14, Top = 10, Bottom = 30;
    private const int MaxPointsPerLine = 1200;

    private static readonly string[] Palette = ["#4e79a7", "#f28e2b", "#e15759", "#76b7b2", "#59a14f", "#edc948", "#b07aa1", "#9c755f"];

    public static string Color(int index) => Palette[index % Palette.Length];

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>A titled chart: heading, the SVG, and a legend. Returns a "no data" figure when every series is empty.</summary>
    /// <param name="peak">Bucket by maximum instead of mean when a line has to be thinned, so latency spikes survive.</param>
    public static string Figure(string title, string unit, IReadOnlyList<NamedSeries> series, double tMax, IReadOnlyList<TimelineMarker> markers, bool peak = false)
    {
        var sb = new StringBuilder();
        sb.Append("<figure class=\"chart\"><figcaption>").Append(E(title)).Append(" <span class=\"unit\">").Append(E(unit)).Append("</span></figcaption>");
        if (series.All(s => s.Points.Count == 0))
        {
            sb.Append("<p class=\"nodata\">No data recorded.</p></figure>");
            return sb.ToString();
        }

        var plotW = Width - Left - Right;
        var plotH = Height - Top - Bottom;
        var yMax = NiceMax(series.SelectMany(s => s.Points).Select(p => p.V).DefaultIfEmpty(1).Max());
        double X(double t) => Left + Math.Clamp(t / tMax, 0, 1) * plotW;
        double Y(double v) => Top + plotH - Math.Clamp(v / yMax, 0, 1) * plotH;

        sb.Append("<svg viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).Append("\" role=\"img\" aria-label=\"").Append(E(title)).Append("\">");

        foreach (var tick in YTicks(yMax))
            sb.Append("<line class=\"grid\" x1=\"").Append(Left).Append("\" x2=\"").Append(Width - Right).Append("\" y1=\"").Append(F(Y(tick))).Append("\" y2=\"").Append(F(Y(tick)))
              .Append("\"/><text class=\"tick\" x=\"").Append(Left - 6).Append("\" y=\"").Append(F(Y(tick) + 4)).Append("\" text-anchor=\"end\">").Append(E(Compact(tick))).Append("</text>");

        var step = XStep(tMax);
        for (var t = 0.0; t <= tMax + 1e-9; t += step)
            sb.Append("<line class=\"axis\" x1=\"").Append(F(X(t))).Append("\" x2=\"").Append(F(X(t))).Append("\" y1=\"").Append(Top + plotH).Append("\" y2=\"").Append(Top + plotH + 4)
              .Append("\"/><text class=\"tick\" x=\"").Append(F(X(t))).Append("\" y=\"").Append(Height - 10).Append("\" text-anchor=\"middle\">").Append(E(Clock(t))).Append("</text>");
        sb.Append("<line class=\"axis\" x1=\"").Append(Left).Append("\" x2=\"").Append(Width - Right).Append("\" y1=\"").Append(Top + plotH).Append("\" y2=\"").Append(Top + plotH).Append("\"/>");

        foreach (var m in markers)
        {
            if (m.T < 0 || m.T > tMax) continue;
            sb.Append("<line class=\"marker ").Append(E(m.Kind)).Append("\" x1=\"").Append(F(X(m.T))).Append("\" x2=\"").Append(F(X(m.T))).Append("\" y1=\"").Append(Top).Append("\" y2=\"").Append(Top + plotH)
              .Append("\"><title>").Append(E(m.Label)).Append("</title></line>");
        }

        for (var i = 0; i < series.Count; i++)
        {
            var points = Thin(series[i].Points, plotW, tMax, peak);
            if (points.Count == 0) continue;
            sb.Append("<polyline fill=\"none\" stroke=\"").Append(Color(i)).Append("\" stroke-width=\"1.5\" stroke-linejoin=\"round\" points=\"")
              .Append(string.Join(' ', points.Select(p => $"{F(X(p.T))},{F(Y(p.V))}"))).Append("\"><title>").Append(E(series[i].Name)).Append("</title></polyline>");
            if (points.Count == 1)
                sb.Append("<circle cx=\"").Append(F(X(points[0].T))).Append("\" cy=\"").Append(F(Y(points[0].V))).Append("\" r=\"2.5\" fill=\"").Append(Color(i)).Append("\"/>");
        }
        sb.Append("</svg>");

        sb.Append("<div class=\"legend\">");
        for (var i = 0; i < series.Count; i++)
            if (series[i].Points.Count > 0)
                sb.Append("<span><i style=\"background:").Append(Color(i)).Append("\"></i>").Append(E(series[i].Name)).Append("</span>");
        sb.Append("</div></figure>");
        return sb.ToString();
    }

    /// <summary>Thins a line to at most about one point per pixel column, so a four-hour soak does not become a 15,000-point path.</summary>
    public static IReadOnlyList<SeriesPoint> Thin(IReadOnlyList<SeriesPoint> points, int plotWidth, double tMax, bool peak)
    {
        if (points.Count <= MaxPointsPerLine) return points;
        var buckets = Math.Min(MaxPointsPerLine, plotWidth);
        var result = new List<SeriesPoint>(buckets);
        foreach (var g in points.GroupBy(p => Math.Min(buckets - 1, (int)(Math.Clamp(p.T / tMax, 0, 1) * buckets))).OrderBy(g => g.Key))
            result.Add(new SeriesPoint(g.Average(p => p.T), peak ? g.Max(p => p.V) : g.Average(p => p.V)));
        return result;
    }

    /// <summary>The smallest "round" number (1, 2, 2.5, 5 times a power of ten) at or above the value, so the top gridline is a clean figure.</summary>
    public static double NiceMax(double max)
    {
        if (max <= 0 || double.IsNaN(max) || double.IsInfinity(max)) return 1;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (max <= m * pow + 1e-12) return m * pow;
        return 10 * pow;
    }

    public static IEnumerable<double> YTicks(double niceMax)
    {
        for (var i = 0; i <= 4; i++) yield return niceMax * i / 4;
    }

    /// <summary>A gridline step giving roughly 6 to 12 labelled ticks across the axis.</summary>
    public static double XStep(double tMax)
    {
        foreach (var s in new double[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 })
            if (tMax / s <= 12) return s;
        return 7200;
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Compact(double v) => Math.Abs(v) switch
    {
        >= 1_000_000 => F(v / 1_000_000) + "M",
        >= 10_000 => F(v / 1000) + "k",
        >= 100 => v.ToString("0", CultureInfo.InvariantCulture),
        >= 10 => v.ToString("0.#", CultureInfo.InvariantCulture),
        // Small values (a sub-millisecond gate wait) keep their significant digits rather than rounding to 0.
        _ => v.ToString("G3", CultureInfo.InvariantCulture),
    };
}
