using System.Net.Http.Json;
using System.Text.Json;

namespace SSHManager;

/// <summary>Approve-on-the-other-PC pairing, requester side: the request goes to this PC's own server,
/// where the other computer's app (connected to it) picks it up and asks its user. See sshmgr/pairing.py.</summary>
internal sealed class Pairing(Func<Link?> localLink)
{
    public sealed record Status(string State, string User, string Error);

    Link? Server => localLink() is { State: LinkState.Connected } l ? l : null;

    /// <summary>Requests can only be filed while this PC's own server is running.</summary>
    public bool Available => Server is not null;

    public async Task<string> RequestAsync(string target, string publicKey)
    {
        var server = Server ?? throw new InvalidOperationException("SSH Manager's server isn't running on this PC.");
        using var r = await Link.Http.SendAsync(server.Request(HttpMethod.Post, "/api/pair/request",
            JsonContent.Create(new { target, public_key = publicKey })));
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Detail(body) ?? "The request could not be sent.");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<Status> StatusAsync(string id)
    {
        var server = Server ?? throw new InvalidOperationException("SSH Manager's server on this PC stopped.");
        using var r = await Link.Http.SendAsync(server.Request(HttpMethod.Get, "/api/pair/" + Uri.EscapeDataString(id)));
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Detail(body) ?? "The request was lost.");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new(root.GetProperty("state").GetString() ?? "", root.GetProperty("user").GetString() ?? "",
                   root.GetProperty("error").GetString() ?? "");
    }

    public async Task CancelAsync(string id)
    {
        try
        {
            if (Server is { } server)
                using (await Link.Http.SendAsync(server.Request(HttpMethod.Post, $"/api/pair/{Uri.EscapeDataString(id)}/cancel"))) { }
        }
        catch (Exception) { }
    }

    public static string? Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (Exception) { return null; }
    }
}
