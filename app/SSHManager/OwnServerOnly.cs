using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SSHManager;

/// <summary>
/// Keeps a PC's access token on that PC's own server.
///
/// Every dashboard is on "localhost", one port each, and browsers normally send a cookie to every port of
/// its host: a picture, a redirect, a WebSocket or a prefetch the page asks for on another PC's port would
/// hand that server this PC's token. So the cookie is tied to the PC's port: the browser is started with
/// port-bound cookies and the cookie is set for that port. The browser then never sends it anywhere else,
/// whatever a page asks for (prefetch and prerender included), and it costs nothing per request. The
/// cookies of ports the PC had before are deleted, as another PC's connection may get such a port later.
/// A frame of another address is not shown in the view either (its request still goes out, without the token).
///
/// A browser update could drop port binding; each view checks that it holds. Where it doesn't, the view
/// falls back to being held to the PC's own server by checking its pages: every page and frame is checked
/// before its request is sent (and at each step of a redirect), and every page that is let in gets a content
/// security policy on top of any it brings, so what it loads, posts or connects to has to be on its own
/// server too. Workers are then only allowed from the page's own script (blob:), as those inherit the
/// policy; service workers kept in the profile from before are deleted first, as nothing checks theirs.
///
/// The fallback does not stop everything: a page can still have the browser prefetch or prerender an address
/// on another local port (one speculation-rules script or link rel=prerender in it is enough), and that
/// request carries the PC's token. The browser makes it outside the page, where neither the interception nor
/// the policy reaches. So the fallback is logged as a warning, once per start: it means a browser update
/// took port binding away and this needs fixing. It also costs a little: it uses the browser's request
/// interception (DevTools protocol), for pages and frames only, but uploads get slower with it switched on.
/// The policy can only be added by handing the browser the page again with it (the interception ignores
/// changed headers on an answer that is let through), so a page is read whole first; downloads and answers
/// the browser only shows as they are (pictures, text) are let through untouched.
/// </summary>
internal static class OwnServerOnly
{
    /// <summary>For the browser of all views: a cookie is only sent to the port it was set for.</summary>
    public const string BrowserArguments = "--enable-features=EnablePortBoundCookies";

    /// <summary>Own server only; inline script and style, eval, and things the page builds itself (data:, blob:)
    /// stay as they were, this is about where requests may go.</summary>
    public const string Policy = "default-src 'self' data: blob: 'unsafe-inline' 'unsafe-eval'; worker-src blob:; form-action 'self'";

    /// <summary>A page larger than this is not shown: its policy can't be added without reading it whole.</summary>
    const int MaxPage = 32 << 20;

    static bool toldUnbound;

    /// <summary>Gives a new view the PC's access token, as a cookie for its port only (page script can't read
    /// it), and keeps the view to the PC's own server. Before the view loads anything.</summary>
    public static async Task ApplyAsync(CoreWebView2 core, int port, string token)
    {
        // another address isn't shown in a frame (the main frame is held in MainForm.Configure)
        core.FrameNavigationStarting += (_, a) =>
        {
            if (!Allowed(a.Uri, port)) a.Cancel = true;
        };
        var name = $"sshmgr_token_{port}";
        // the cookies of the ports this PC had before (its connection came back on another one): once another
        // PC's connection gets such a port, a request this PC's page makes there would carry this PC's token
        foreach (var old in await core.CookieManager.GetCookiesAsync(""))
            if (old.Name.StartsWith(CookiePrefix, StringComparison.Ordinal) && old.Name != name) core.CookieManager.DeleteCookie(old);
        var url = $"http://localhost:{port}/";
        await CallAsync(core, "Network.setCookie", new
        {
            name, value = token, url, path = "/", httpOnly = true, sameSite = "Strict", sourceScheme = "NonSecure", sourcePort = port,
        });
        // a cookie the browser refuses comes back as an error; the answer's "success" is deprecated (always true)
        // and may go, so it isn't read: the cookie has to be there for the PC's own address
        if (!await HasCookieAsync(core, url, name, token)) throw new InvalidOperationException("The browser didn't take the access cookie.");
        var left = (await core.CookieManager.GetCookiesAsync("")).Count(c => c.Name.StartsWith(CookiePrefix, StringComparison.Ordinal) && c.Name != name);
        if (left > 0) throw new InvalidOperationException("The browser didn't delete the access cookies of this PC's earlier connections.");
        // the cookies the browser would send to the next port: this one isn't among them where it is bound
        if (!await HasCookieAsync(core, $"http://localhost:{(port == 65535 ? 1 : port + 1)}/", name)) return;
        if (!toldUnbound)
        {
            toldUnbound = true;
            AppLog.Write($"WARNING: this browser version ({core.Environment.BrowserVersionString}) doesn't keep a cookie to its port, so PC views " +
                "fall back to checking their pages. The fallback doesn't stop everything: a PC's page can still have the browser prefetch " +
                "or prerender an address on another local port, and that request carries the PC's access token. SSH Manager needs " +
                "an update for this browser version: please report this line.");
        }
        // service workers registered while binding held: their requests are not checked, and one can answer
        // for a page of its own, without the policy
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.ServiceWorkers);
        core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused").DevToolsProtocolEventReceived += (_, e) =>
            _ = PausedAsync(core, e.ParameterObjectAsJson, port);
        // pages and frames only, before the request is sent and when its answer starts
        await core.CallDevToolsProtocolMethodAsync("Fetch.enable",
            """{"patterns":[{"urlPattern":"*","resourceType":"Document","requestStage":"Request"},{"urlPattern":"*","resourceType":"Document","requestStage":"Response"}]}""");
    }

    static async Task PausedAsync(CoreWebView2 core, string json, int port)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.GetProperty("requestId").GetString();
            if (!Allowed(root.GetProperty("request").GetProperty("url").GetString() ?? "", port))
            {
                await FailAsync(core, id);
                return;
            }
            // before the request is sent: it goes to the PC's own server
            if (!root.TryGetProperty("responseStatusCode", out var code) || !code.TryGetInt32(out var status))
            {
                await CallAsync(core, "Fetch.continueRequest", new { requestId = id });
                return;
            }
            var headers = root.TryGetProperty("responseHeaders", out var h) && h.ValueKind == JsonValueKind.Array
                ? h.EnumerateArray().Select(x => (Name: x.GetProperty("name").GetString() ?? "", Value: x.GetProperty("value").GetString() ?? "")).ToList()
                : [];
            string Header(string name) => headers.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            // a redirect (its target comes back here as a new request), a download, or something shown as it is
            if (status is >= 300 and < 400 or 204 or 205 || Header("Content-Disposition").TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase)
                || Inert(Header("Content-Type")))
            {
                await CallAsync(core, "Fetch.continueRequest", new { requestId = id });
                return;
            }
            if (long.TryParse(Header("Content-Length"), out var length) && length > MaxPage)
            {
                AppLog.Write($"A page of {length >> 20} MB was not shown in a PC's view.");
                await FailAsync(core, id);
                return;
            }
            using var answer = JsonDocument.Parse(await CallAsync(core, "Fetch.getResponseBody", new { requestId = id }));
            var text = answer.RootElement.GetProperty("body").GetString() ?? "";
            var base64 = answer.RootElement.GetProperty("base64Encoded").GetBoolean();
            // the browser hands over text already decoded: it goes back as UTF-8, and says so
            var body = base64 ? text : Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            if (body.Length / 4 * 3 > MaxPage)
            {
                AppLog.Write("A page too large to check was not shown in a PC's view.");
                await FailAsync(core, id);
                return;
            }
            var type = Header("Content-Type");
            var kept = headers
                .Where(x => x.Name.ToLowerInvariant() is not ("content-length" or "content-encoding" or "transfer-encoding") && (base64 || !x.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                .Select(x => (object)new { name = x.Name, value = x.Value }).ToList();
            if (!base64 && type != "") kept.Add(new { name = "Content-Type", value = type.Split(';')[0].Trim() + "; charset=utf-8" });
            kept.Add(new { name = "Content-Security-Policy", value = Policy });
            await CallAsync(core, "Fetch.fulfillRequest", new { requestId = id, responseCode = status, responseHeaders = kept, body });
        }
        catch (Exception ex)
        {
            if (id is null) return;
            if (ex is not ObjectDisposedException && !Closed(ex)) AppLog.Write("Checking a page a PC's view loads", ex);
            try { await FailAsync(core, id); } catch (Exception) { }   // the view was closed meanwhile
        }
    }

    /// <summary>The view (or its browser process) went away while a page was checked.</summary>
    static bool Closed(Exception ex) => ex is InvalidOperationException || ex.HResult == unchecked((int)0x8007139F);

    const string CookiePrefix = "sshmgr_token_";

    /// <summary>Whether the browser would send the cookie (with that value, when given) to this address.</summary>
    static async Task<bool> HasCookieAsync(CoreWebView2 core, string url, string name, string? value = null)
    {
        using var doc = JsonDocument.Parse(await CallAsync(core, "Network.getCookies", new { urls = new[] { url } }));
        return doc.RootElement.GetProperty("cookies").EnumerateArray().Any(c =>
            c.TryGetProperty("name", out var n) && n.GetString() == name
            && (value is null || (c.TryGetProperty("value", out var v) && v.GetString() == value)));
    }

    static Task FailAsync(CoreWebView2 core, string? id) => CallAsync(core, "Fetch.failRequest", new { requestId = id, errorReason = "BlockedByClient" });

    static Task<string> CallAsync(CoreWebView2 core, string method, object args) =>
        core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));

    /// <summary>A type the browser shows as it is, without loading or running anything for it.</summary>
    static bool Inert(string contentType)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type is "text/plain" or "application/json" or "application/octet-stream" or "application/zip"
            || (type.StartsWith("image/") && type != "image/svg+xml") || type.StartsWith("audio/") || type.StartsWith("video/");
    }

    /// <summary>The PC's own server, or an address that stays inside the page: something the page built
    /// itself (blob:, data:) or an empty frame.</summary>
    public static bool Allowed(string uri, int port)
    {
        if (Own(uri, port)) return true;
        if (uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return Own(uri[5..], port);   // made by a page of this server
        return uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Empty(uri, "about:blank") || Empty(uri, "about:srcdoc");
    }

    /// <summary>That empty page, also with a "#…" or "?…" after it (still the same empty page to the browser).</summary>
    static bool Empty(string uri, string page) =>
        uri.StartsWith(page, StringComparison.OrdinalIgnoreCase) && (uri.Length == page.Length || uri[page.Length] is '#' or '?');

    /// <summary>http://localhost:port/… exactly: not another port, host or scheme, and no user name in front.</summary>
    public static bool Own(string uri, int port) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.UserInfo == ""
        && u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && u.Port == port;
}
