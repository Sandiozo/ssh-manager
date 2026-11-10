using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SSHManager;

/// <summary>Sends one request to a PC's server (this PC's own, or the one a pairing request came through),
/// as the app itself. The body, if any, is sent as JSON.</summary>
internal delegate Task<HttpResponseMessage> PairSend(HttpMethod method, string path, object? body = null);

/// <summary>Who answers: this computer, its app's install id and the Windows user the key is added for.</summary>
internal sealed record PairIdentity(string Machine, string MachineId, string User);

internal enum PairOutcome
{
    /// <summary>The key is on this PC and the requester was told.</summary>
    Allowed,
    /// <summary>The request had expired or was cancelled: nothing was changed.</summary>
    Gone,
    /// <summary>The PC that holds the request didn't answer when asked whether it is still open: nothing was changed.</summary>
    Unreachable,
    /// <summary>This PC's own server isn't running, so the key could not be added; the requester was told.</summary>
    NoServer,
    /// <summary>This PC's server refused the key; the requester was told why.</summary>
    KeyNotAdded,
    /// <summary>The request ended between the check and the answer: the key was taken out again.</summary>
    Late,
    /// <summary>The answer could not be delivered and the PC couldn't be asked what became of the request:
    /// the key was taken out again.</summary>
    Unanswered,
    /// <summary>As Late or Unanswered, but the key could not be taken out again: it is still on this PC.</summary>
    KeyLeft,
    /// <summary>Adding the key failed in a way that doesn't say whether it was added, and this PC's keys
    /// could not be read afterwards: the key may be on this PC. The requester was told it failed.</summary>
    KeyUnsure,
}

internal sealed record PairResult(PairOutcome Outcome, string Detail = "");

/// <summary>
/// The approving side of pairing (see sshmgr/pairing.py), without the window: what is shown about a
/// request, and what happens on Allow. A key gives its holder this PC, and a request lives for two
/// minutes or until the requester cancels it, so the key is added only while the server that holds the
/// request still reports it as open, and is taken out again if the answer no longer arrives.
///
/// A call that fails doesn't say nothing happened: the server may have done its part before the answer
/// got lost (the app stops waiting after a few seconds). So after a failed call the app looks at what is
/// really there, this PC's key list or the request's state, and what the user is told follows that.
/// </summary>
internal static class PairApproval
{
    /// <summary>The key types sshd accepts here (the same list as the server's keys.py).</summary>
    static readonly string[] KeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    /// <summary>Looked at again this long after a call that failed.</summary>
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    /// <summary>Letters that show as nothing (Hangul fillers, the empty Braille pattern, the combining
    /// grapheme joiner, two Khmer vowels): Unicode calls them letters or marks, so they are named here.</summary>
    static readonly HashSet<int> Blank = [0x034F, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x2800, 0x3164, 0xFFA0];

    /// <summary>Text a PC supplied, made fit to show in a prompt: one line, no control or invisible
    /// characters (which could fake extra lines, reverse the text or pad it out of sight), at most
    /// <paramref name="max"/> characters.</summary>
    public static string Clean(string? text, int max)
    {
        var sb = new StringBuilder();
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            var keep = Rune.GetUnicodeCategory(rune) switch
            {
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned => false,
                _ => true,
            };
            if (!keep || rune.Value == 0xFFFD || Blank.Contains(rune.Value)) sb.Append(' ');   // also what a broken character was read as
            else if (Rune.IsWhiteSpace(rune)) sb.Append(' ');
            else sb.Append(rune.ToString());
        }
        var oneLine = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length <= max) return oneLine;
        var cut = max > 0 && char.IsHighSurrogate(oneLine[max - 1]) ? max - 1 : max;   // not through the middle of a character
        return oneLine[..cut].TrimEnd() + "…";
    }

    /// <summary>The fingerprint of a public key line ("type base64 [comment]") the way ssh-keygen -l shows
    /// it: SHA256 of the key data, as base64 without padding. Worked out here from the key that would be
    /// added, not taken from what the PC says it is. Null when the line isn't a public key.</summary>
    public static string? Fingerprint(string? publicKey)
    {
        var parts = (publicKey ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KeyTypes.Contains(parts[0])) return null;
        var blob = new byte[parts[1].Length];
        if (parts[1].Length % 4 != 0 || !Convert.TryFromBase64String(parts[1], blob, out var n) || n < 8) return null;
        blob = blob[..n];
        // the key data starts with its type (4 bytes of length, then the name): it has to be the type the line names
        var typeLength = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        if (typeLength != parts[0].Length || n < 4 + typeLength || Encoding.ASCII.GetString(blob, 4, typeLength) != parts[0]) return null;
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    /// <summary>Allow was clicked. <paramref name="pc"/> is the PC the request came through,
    /// <paramref name="own"/> this PC's own server (null when it isn't running).</summary>
    public static async Task<PairResult> AllowAsync(PairSend pc, PairSend? own, string id, string publicKey, string fingerprint,
        string label, PairIdentity me)
    {
        // a click can come long after the request was made: only a request that is still open is acted on
        switch (await StateAsync(pc, id))
        {
            case "pending": break;
            case "": return new(PairOutcome.Unreachable);
            default: return new(PairOutcome.Gone);
        }
        if (own is null)
        {
            const string why = "SSH Manager's server isn't running on this PC.";
            await AnswerAsync(pc, id, me, false, why);
            return new(PairOutcome.NoServer, why);
        }
        // whether the key is here already decides what a failure further on means, and what may be taken out again
        var before = await HasKeyAsync(own, fingerprint);
        if (before is null)
        {
            const string why = "This PC's SSH keys could not be read.";
            await AnswerAsync(pc, id, me, false, why);
            return new(PairOutcome.KeyNotAdded, why);
        }
        var added = false;
        if (before == false)
        {
            string? error = null;
            try
            {
                using var r = await own(HttpMethod.Post, "/api/keys/add", new { public_key = publicKey, label });
                if (!r.IsSuccessStatusCode) error = Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? "The key could not be added.";
            }
            catch (Exception e)
            {
                AppLog.Write("Adding a paired PC's key", e);
                error = NoAnswer;
            }
            if (error is not null)
            {
                // the server writes the key before it finishes its answer: the key may be there all the same
                var now = await HasKeyAsync(own, fingerprint, tries: 3);
                if (now is null && await TakeOutAsync(own, fingerprint) is null) now = false;
                if (now != true)
                {
                    await AnswerAsync(pc, id, me, false, error);   // the requester sees why instead of waiting for nothing
                    return new(now == false ? PairOutcome.KeyNotAdded : PairOutcome.KeyUnsure, error);
                }
            }
            added = true;
        }
        // the key is on this PC: tell the requester. An answer that seems lost may have arrived, so the
        // request's own state decides
        var state = "";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await AnswerAsync(pc, id, me, true)) return new(PairOutcome.Allowed);
            state = await StateAsync(pc, id);
            if (state == "allowed") return new(PairOutcome.Allowed);
            if (state is not ("pending" or "")) break;   // expired or cancelled meanwhile
            if (attempt < 3) await Task.Delay(Pause);
        }
        var ended = state is not ("pending" or "");
        // a key that was here before this request stays as it was; one added for it doesn't stay, as nobody
        // is waiting for it any more (or nobody could be told)
        if (!added) return new(ended ? PairOutcome.Gone : PairOutcome.Unreachable);
        return await TakeOutAsync(own, fingerprint) is { } left ? new(PairOutcome.KeyLeft, left)
            : new(ended ? PairOutcome.Late : PairOutcome.Unanswered);
    }

    /// <summary>Whether this PC's server lists the key in the file keys are added to. Null when that
    /// couldn't be found out.</summary>
    static async Task<bool?> HasKeyAsync(PairSend own, string fingerprint, int tries = 1)
    {
        static string S(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var r = await own(HttpMethod.Get, "/api/keys");
                if (r.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                    {
                        // a keys file the server can't read is one whose keys it doesn't list
                        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
                            && files.EnumerateArray().Any(f => S(f, "scope") == "admin" && f.TryGetProperty("readable", out var ok) && ok.ValueKind == JsonValueKind.False))
                            return null;
                        return keys.EnumerateArray().Any(k => S(k, "fingerprint") == fingerprint && S(k, "file") is "admin" or "");
                    }
                }
            }
            catch (Exception) { }
            if (attempt >= tries) return null;
            await Task.Delay(Pause);
        }
    }

    const string NoAnswer = "SSH Manager's server on this PC didn't answer.";

    /// <summary>Takes the key off this PC again. Null when it is gone; otherwise why it may still be there.</summary>
    static async Task<string?> TakeOutAsync(PairSend own, string fingerprint)
    {
        string why;
        try
        {
            using var r = await own(HttpMethod.Post, "/api/keys/remove", new { fingerprint });
            if (r.IsSuccessStatusCode) return null;
            why = Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? $"This PC's server answered {(int)r.StatusCode}.";
        }
        catch (Exception e)
        {
            AppLog.Write("Taking a paired PC's key out again", e);
            why = NoAnswer;
        }
        // the same here: what counts is whether the key is still listed
        return await HasKeyAsync(own, fingerprint, tries: 3) == false ? null : why;
    }

    /// <summary>Deny was clicked (or the request can't be allowed): tells the requester. Nothing else changes.</summary>
    public static Task DenyAsync(PairSend pc, string id, PairIdentity me, string error = "") => AnswerAsync(pc, id, me, false, error);

    /// <summary>"pending", "expired" (also when cancelled), "allowed", "denied" or "gone" as the server reports
    /// the request; empty when it couldn't be asked.</summary>
    static async Task<string> StateAsync(PairSend pc, string id)
    {
        try
        {
            using var r = await pc(HttpMethod.Get, $"/api/pair/{Uri.EscapeDataString(id)}");
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return "gone";
            if (!r.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.String) return "";
            var state = s.GetString() ?? "";
            // a request with no time left is not open, whatever it is called
            if (state == "pending" && root.TryGetProperty("expires_in", out var left) && left.ValueKind == JsonValueKind.Number
                && left.TryGetDouble(out var seconds) && seconds <= 0) return "expired";
            return state;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>True when the server took the answer; false when the request was no longer open or the
    /// answer didn't arrive.</summary>
    static async Task<bool> AnswerAsync(PairSend pc, string id, PairIdentity me, bool allowed, string error = "")
    {
        try
        {
            using var r = await pc(HttpMethod.Post, $"/api/pair/{Uri.EscapeDataString(id)}/answer", new
            {
                machine = me.Machine, machine_id = me.MachineId, allowed, user = allowed ? me.User : "",
                error = error.Length > 500 ? error[..500] : error,
            });
            return r.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The request body for <see cref="PairSend"/> implementations.</summary>
    public static HttpContent? Json(object? body) => body is null ? null : JsonContent.Create(body);
}
