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
    /// <summary>As Late, but the key could not be taken out again.</summary>
    LateKeyLeft,
}

internal sealed record PairResult(PairOutcome Outcome, string Detail = "");

/// <summary>
/// The approving side of pairing (see sshmgr/pairing.py), without the window: what is shown about a
/// request, and what happens on Allow. A key gives its holder this PC, and a request lives for two
/// minutes or until the requester cancels it, so the key is added only while the server that holds the
/// request still reports it as open, and is taken out again if the answer no longer arrives.
/// </summary>
internal static class PairApproval
{
    /// <summary>The key types sshd accepts here (the same list as the server's keys.py).</summary>
    static readonly string[] KeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    /// <summary>Text a PC supplied, made fit to show in a prompt: one line, no control or invisible
    /// formatting characters (which could fake extra lines or reverse the text), at most
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
            if (!keep || rune.Value == 0xFFFD) sb.Append(' ');   // also what a broken character was read as
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
        try
        {
            using var r = await own(HttpMethod.Post, "/api/keys/add", new { public_key = publicKey, label });
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? "The key could not be added.");
        }
        catch (Exception e)
        {
            await AnswerAsync(pc, id, me, false, e.Message);   // the requester sees why instead of waiting for nothing
            return new(PairOutcome.KeyNotAdded, e.Message);
        }
        if (await AnswerAsync(pc, id, me, true)) return new(PairOutcome.Allowed);
        // the request expired or was cancelled in the moment between the check and the answer (or the answer
        // was lost): nobody is waiting for this key any more, so it doesn't stay
        try
        {
            using var r = await own(HttpMethod.Post, "/api/keys/remove", new { fingerprint });
            if (r.IsSuccessStatusCode) return new(PairOutcome.Late);
            return new(PairOutcome.LateKeyLeft, Pairing.Detail(await r.Content.ReadAsStringAsync()) ?? $"This PC's server answered {(int)r.StatusCode}.");
        }
        catch (Exception e) { return new(PairOutcome.LateKeyLeft, e.Message); }
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
