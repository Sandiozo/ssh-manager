using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SSHManager;

/// <summary>
/// Checks GitHub for a newer SSH Manager and installs it. On a PC that also runs the server, the server
/// updates everything itself (silently, it has admin rights), so this only runs where the app is
/// installed alone. The installer needs admin rights, so Windows asks once (UAC).
/// </summary>
internal sealed class AppUpdater(AppSettings settings, Action<string, string> notify)
{
    const string Repo = "Sandiozo/ssh-manager";
    const string Asset = "SSHManager-Setup.exe";
    static readonly HttpClient Http = CreateClient();

    public string Current { get; } = Application.ProductVersion.Split('+')[0];
    public string? Latest { get; private set; }
    public bool Available { get; private set; }
    public string Status { get; private set; } = "idle";   // idle | downloading | installing
    public string? Error { get; private set; }
    public event Action? Changed;
    string? assetUrl, shaUrl;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ssh-manager-app");
        return c;
    }

    /// <summary>True when this PC's own server keeps the installation up to date.</summary>
    public static bool ServerUpdatesThisPc() =>
        Ssh.LocalInstallFolder() is { } f && File.Exists(Path.Combine(f, "SSHManagerServer.exe"));

    public static bool IsInstalled() =>
        Environment.ProcessPath is { } p && p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    public void Start() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            await CheckAsync();
            if (Available && settings.AutoUpdate && !ServerUpdatesThisPc() && IsInstalled()) await InstallAsync();
            await Task.Delay(TimeSpan.FromHours(6));
        }
    });

    public async Task CheckAsync()
    {
        try
        {
            using var r = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Latest = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            assetUrl = shaUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                if (name == Asset) assetUrl = a.GetProperty("browser_download_url").GetString();
                if (name == Asset + ".sha256") shaUrl = a.GetProperty("browser_download_url").GetString();
            }
            var was = Available;
            Available = assetUrl is not null && Latest is not null && Newer(Latest, Current);
            Error = null;
            if (Available && !was && !(settings.AutoUpdate && IsInstalled()))
                notify("SSH Manager update", $"Version {Latest} is available. Open SSH Manager to install it.");
        }
        catch (Exception e) { Error = "Couldn't check for updates: " + e.Message; }
        Changed?.Invoke();
    }

    public static bool Newer(string a, string b)
    {
        static Version V(string s) => System.Version.TryParse(new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), out var v) ? v : new Version(0, 0);
        return V(a) > V(b);
    }

    public async Task InstallAsync()
    {
        if (!Available || assetUrl is null || Status != "idle") return;
        try
        {
            Status = "downloading";
            Changed?.Invoke();
            var file = Path.Combine(Path.GetTempPath(), $"SSHManager-Setup-{Latest}.exe");
            await using (var src = await Http.GetStreamAsync(assetUrl))
            await using (var dst = File.Create(file))
                await src.CopyToAsync(dst);
            if (shaUrl is not null)
            {
                var expected = (await Http.GetStringAsync(shaUrl)).Split(' ', '\t', '\n')[0].Trim().ToLowerInvariant();
                await using var f = File.OpenRead(file);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
                if (actual != expected) throw new InvalidOperationException("The download didn't match its checksum.");
            }
            Status = "installing";
            Changed?.Invoke();
            // UseShellExecute so Windows can ask for admin rights; the installer closes this app and
            // Windows restarts it afterwards (RegisterApplicationRestart in Program.cs).
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Error = e is System.ComponentModel.Win32Exception ? "The update was cancelled." : "Update failed: " + e.Message;
            Status = "idle";
            Changed?.Invoke();
        }
    }
}
