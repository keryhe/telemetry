namespace Keryhe.Telemetry.TestDataGenerator.Model;

/// <summary>An attribute bag. Values are string, long, double or bool.</summary>
public sealed class Tags : List<KeyValuePair<string, object?>>
{
    public Tags() { }

    public Tags(IEnumerable<KeyValuePair<string, object?>> items) : base(items) { }

    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, Normalize(value)));

    /// <summary>Replaces an existing key or appends it.</summary>
    public Tags Set(string key, object? value)
    {
        var i = FindIndex(kv => kv.Key == key);
        var item = new KeyValuePair<string, object?>(key, Normalize(value));
        if (i >= 0) this[i] = item; else Add(item);
        return this;
    }

    public object? Get(string key) => this.FirstOrDefault(kv => kv.Key == key).Value;

    /// <summary>Order-insensitive identity of the attribute set, used as a metric stream key.</summary>
    public string Key()
    {
        if (Count == 0) return "";
        return string.Join('|', this.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static object? Normalize(object? v) => v switch
    {
        int i => (long)i,
        short s => (long)s,
        float f => (double)f,
        _ => v,
    };
}
