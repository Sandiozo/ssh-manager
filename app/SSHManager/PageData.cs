using System.Text.Json;

namespace SSHManager;

/// <summary>Reading JSON from PCs without trusting its shape: a missing or odd value gives a default.</summary>
internal static class J
{
    public static bool Has(JsonElement e, string n, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v);
    }

    public static string S(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    /// <summary>Text cut to at most <paramref name="max"/> characters.</summary>
    public static string S(JsonElement e, string n, int max) => Cap(S(e, n), max);
    public static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
    public static bool B(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.True;
    public static int I(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : 0;
    public static long L(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
    public static double D(JsonElement e, string n) => N(e, n) ?? 0;
    /// <summary>A number, or null when it is missing, not a number or too large to be one.</summary>
    public static double? N(JsonElement e, string n) =>
        Has(e, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && double.IsFinite(x) ? x : null;
    public static JsonElement O(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    public static IEnumerable<JsonElement> A(JsonElement e, string n) =>
        Has(e, n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    public static int Len(JsonElement e, string n) => Has(e, n, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}

/// <summary>
/// What a PC reports, made safe for the app's own pages (All PCs). Those pages can change the app, so
/// nothing a PC sent is passed on the way it arrived: every value is read as the kind it should be (a
/// number within its range, short text, a short list), and anything else is left out. A PC also passes
/// on what other computers reported to it, so this holds for a PC that is itself honest.
/// </summary>
internal static class PageData
{
    /// <summary>Names of PCs and computers are shown up to this length.</summary>
    public const int MaxName = 64;

    public static double? Percent(JsonElement e, string n) => J.N(e, n) is { } x ? Math.Clamp(x, 0, 100) : null;

    /// <summary>A size or a count: a whole number from 0 up, or null when it isn't a number.</summary>
    public static long? Amount(JsonElement e, string n) => J.N(e, n) is { } x ? (long)Math.Clamp(x, 0, 1e18) : null;

    public static object[] Disks(JsonElement e) => J.A(e, "disks").Where(d => d.ValueKind == JsonValueKind.Object).Take(8)
        .Select(d => (object)new { mount = J.S(d, "mount", 16), total = Amount(d, "total") ?? 0, free = Amount(d, "free") ?? 0 }).ToArray();

    /// <summary>A PC's own stats (the "host" part of its status).</summary>
    public static object HostStats(JsonElement host)
    {
        var ram = J.O(host, "ram");
        return new
        {
            cpu = Percent(host, "cpu") ?? 0,
            ram_total = Amount(ram, "total") ?? 0,
            ram_used = Amount(ram, "used") ?? 0,
            disks = Disks(host),
            uptime_s = Amount(host, "uptime_s") ?? 0,
        };
    }

    /// <summary>Stats a computer's app reported to a PC (see LocalStats), for its row in All PCs.</summary>
    public static object ClientStats(JsonElement s) => new
    {
        cpu = Percent(s, "cpu"),
        ram_total = Amount(s, "ram_total") ?? 0,
        ram_used = Amount(s, "ram_used") ?? 0,
        disks = Disks(s),
        battery = Percent(s, "battery") is { } b ? (int?)Math.Round(b) : null,
        charging = J.Has(s, "charging", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)(c.ValueKind == JsonValueKind.True) : null,
        uptime_s = Amount(s, "uptime_s"),
    };

    /// <summary>One activity entry: its plain values only (text, numbers, yes/no and short lists of them),
    /// each cut to size.</summary>
    public static Dictionary<string, object> Activity(JsonElement entry)
    {
        static object? Plain(JsonElement v, int max) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { } s ? (s.Length > max ? s[..max] + "…" : s) : null,
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        var o = new Dictionary<string, object>();
        if (entry.ValueKind != JsonValueKind.Object) return o;
        foreach (var p in entry.EnumerateObject().Where(p => p.Name.Length <= 40).Take(40))
        {
            var value = p.Value.ValueKind == JsonValueKind.Array
                ? p.Value.EnumerateArray().Take(50).Select(v => Plain(v, 300)).OfType<object>().ToArray()
                : Plain(p.Value, 4000);
            if (value is not null) o[p.Name] = value;
        }
        return o;
    }
}
