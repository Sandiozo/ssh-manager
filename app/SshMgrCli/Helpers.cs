// Small pieces both the commands and the MCP server use: time limits for HTTP calls, uploads that report
// progress, text decoding, safe local file names and exit codes.
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace SshMgrCli;

/// <summary>
/// A time limit that can be pushed back: a short call gets a fixed limit; a download, upload or stream calls
/// Kick() whenever data moves, so it only runs out when nothing happens for that long.
/// </summary>
sealed class Watchdog : IDisposable
{
    readonly CancellationTokenSource cts;
    readonly CancellationToken outer;

    public Watchdog(double seconds, CancellationToken outer)
    {
        this.outer = outer;
        Seconds = seconds;
        cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
    }

    public double Seconds { get; }
    public CancellationToken Token => cts.Token;

    /// <summary>The limit ran out (rather than the caller cancelling).</summary>
    public bool Expired => cts.IsCancellationRequested && !outer.IsCancellationRequested;

    public void Kick()
    {
        try { if (!cts.IsCancellationRequested) cts.CancelAfter(TimeSpan.FromSeconds(Seconds)); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => cts.Dispose();
}

/// <summary>A request body read from a file (or bytes) that keeps the watchdog alive while it is sent.</summary>
sealed class UploadContent : HttpContent
{
    readonly Func<Stream> open;
    readonly long length;
    readonly Watchdog dog;

    public UploadContent(Func<Stream> open, long length, Watchdog dog)
    {
        this.open = open;
        this.length = length;
        this.dog = dog;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await using var source = open();
        var buffer = new byte[81920];
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, n), ct);
            dog.Kick();
        }
    }

    protected override bool TryComputeLength(out long l)
    {
        l = length;
        return true;
    }
}

static partial class Program
{
    /// <summary>Copies a response body, keeping the watchdog alive while data comes in.</summary>
    static async Task<long> CopyAsync(Stream from, Stream to, Watchdog dog)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buffer, dog.Token)) > 0)
        {
            try { await to.WriteAsync(buffer.AsMemory(0, n), dog.Token); }
            catch (IOException e) { throw new Failure($"Couldn't write on this computer: {e.Message}"); }
            total += n;
            dog.Kick();
        }
        return total;
    }

    /// <summary>Writes a file on this computer; a failure says so (it isn't the connection's).</summary>
    static async Task WriteLocal(string path, byte[] bytes, CancellationToken ct)
    {
        try { await File.WriteAllBytesAsync(path, bytes, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Failure($"Couldn't write {path}: {e.Message}"); }
    }

    // ---- exit codes ----

    /// <summary>A run's exit code. Windows reports it as an unsigned number (4294967295 for -1, 3221225477 for an
    /// access violation), so it is read as a long and taken as the same 32 bits.</summary>
    static int ExitCode(JsonNode? n)
    {
        if (n is not JsonValue v) return 1;
        if (v.TryGetValue<long>(out var l)) return unchecked((int)l);
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return unchecked((int)(long)d);
        return 1;
    }

    /// <summary>An exit code for people: negative ones also in hex (-1073741819 = 0xC0000005).</summary>
    static string ExitText(JsonNode? n)
    {
        if (n is not JsonValue) return "unknown";
        var c = ExitCode(n);
        return c < 0 ? $"{c} (0x{unchecked((uint)c):X8})" : c.ToString();
    }

    // ---- text ----

    [DllImport("kernel32.dll")]
    static extern int GetACP();

    /// <summary>The system's ANSI code page (for text files that aren't UTF-8), or Latin-1.</summary>
    static readonly Lazy<Encoding> Ansi = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp = GetACP();
            return cp is 0 or 65001 ? Encoding.Latin1 : Encoding.GetEncoding(cp);
        }
        catch (Exception) { return Encoding.Latin1; }
    });

    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>How text bytes are encoded: by byte order mark, UTF-16 without one, UTF-8, else the ANSI code page.
    /// Null for binary data. <paramref name="complete"/> = these are all the bytes (not just the start).</summary>
    static Encoding? TextEncoding(ReadOnlySpan<byte> b, bool complete)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Utf8;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        var sample = b[..Math.Min(b.Length, 8192)];
        int zeroOdd = 0, zeroEven = 0;
        for (var i = 0; i < sample.Length; i++)
            if (sample[i] == 0) { if (i % 2 == 1) zeroOdd++; else zeroEven++; }
        if (sample.Length >= 4)
        {
            // mostly ASCII text in UTF-16 without a byte order mark
            if (zeroEven == 0 && zeroOdd > sample.Length / 2 * 0.4) return Encoding.Unicode;
            if (zeroOdd == 0 && zeroEven > sample.Length / 2 * 0.4) return Encoding.BigEndianUnicode;
        }
        if (zeroOdd + zeroEven > 0) return null;
        try
        {
            new UTF8Encoding(false, true).GetDecoder().GetCharCount(b, flush: complete);
            return Utf8;
        }
        catch (DecoderFallbackException) { return Ansi.Value; }
    }

    static int BomLength(ReadOnlySpan<byte> b, Encoding e) =>
        e is UTF8Encoding && b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3
        : e.CodePage is 1200 or 1201 && b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)) ? 2 : 0;

    /// <summary>The text of a file (any of the encodings above), or null when it isn't text.</summary>
    static string? DecodeText(byte[] b)
    {
        if (TextEncoding(b, complete: true) is not { } e) return null;
        var skip = BomLength(b, e);
        return e.GetString(b, skip, b.Length - skip);
    }

    static string EncodingName(Encoding e) => e switch
    {
        UTF8Encoding => "UTF-8",
        { CodePage: 1200 } => "UTF-16",
        { CodePage: 1201 } => "UTF-16 big-endian",
        _ => $"code page {e.CodePage}",
    };

    /// <summary>The first and last lines of a text file on this computer, or null when it isn't text.</summary>
    static string? Preview(string path, int count = 10)
    {
        const int Chunk = 65536;
        using var f = File.OpenRead(path);
        var len = f.Length;
        var head = new byte[Math.Min(len, Chunk)];
        f.ReadExactly(head);
        if (TextEncoding(head, complete: len <= Chunk) is not { } e) return null;
        var skip = BomLength(head, e);
        var headLines = SplitLines(e.GetString(head, skip, head.Length - skip));
        if (len > Chunk && headLines.Count > 1) headLines.RemoveAt(headLines.Count - 1);   // cut off mid-line
        static string Clip(string l) => l.Length > 300 ? l[..300] + " [...]" : l;
        if (len <= Chunk && headLines.Count <= count * 2)
            return string.Join("\n", headLines.Select(Clip));
        var first = headLines.Take(count).Select(Clip);
        List<string> last;
        if (len <= Chunk) last = headLines.TakeLast(count).ToList();
        else
        {
            var start = len - Chunk;
            if (e.CodePage is 1200 or 1201 && start % 2 == 1) start++;
            f.Seek(start, SeekOrigin.Begin);
            var tail = new byte[len - start];
            f.ReadExactly(tail);
            last = SplitLines(e.GetString(tail));
            if (last.Count > 1) last.RemoveAt(0);   // starts mid-line
            last = last.TakeLast(count).ToList();
        }
        return $"First lines:\n{string.Join("\n", first)}\n[...]\nLast lines:\n{string.Join("\n", last.Select(Clip))}";
    }

    static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // ---- file names ----

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A plain file name from a name the server gave (e.g. Content-Disposition): no folders, no
    /// traversal, no characters Windows doesn't allow, no device names.</summary>
    static string SafeFileName(string? name, string fallback = "download")
    {
        var n = (name ?? "").Trim().Trim('"').Replace('/', '\\');
        n = n[(n.LastIndexOf('\\') + 1)..];
        n = Path.GetFileName(n);
        var invalid = Path.GetInvalidFileNameChars();
        n = string.Concat(n.Select(c => c < 32 || invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (n is "" or "." or "..") n = fallback;
        if (DeviceNames.Contains(n.Split('.')[0])) n = "_" + n;
        if (n.Length > 200)
        {
            var ext = Path.GetExtension(n);
            n = n[..(200 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : "");
        }
        return n;
    }

    /// <summary>The same path, or "name (2).ext" and so on when it exists.</summary>
    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }
}
