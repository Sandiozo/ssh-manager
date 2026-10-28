using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHManager;

/// <summary>
/// Checks GitHub for a newer SSH Manager and installs it. On a PC that also runs the server, the server
/// updates everything itself (silently, it has admin rights), so this only runs where the app is
/// installed alone. The installer needs admin rights, so Windows asks (UAC); for a user who isn't an
/// administrator, or who said no once, updates are only announced, not started automatically.
/// An installer is started only when it matches its published SHA-256 and carries a valid release
/// signature (checked with Windows' ssh-keygen against the key pinned below); it stays open, so it
/// can't be changed, from the checks until it has started.
/// </summary>
internal sealed class AppUpdater(AppSettings settings, Action<string, string> notify)
{
    const string Repo = "Sandiozo/ssh-manager";
    const string Asset = "SSHManager-Setup.exe";
    const long MaxInstaller = 400L << 20;
    /// <summary>The release signing keys (public halves): releases are signed with the first, whose private half
    /// is the release workflow's secret; the second is a backup kept offline that can replace it.</summary>
    internal const string Signer = "sshmanager-release";
    internal const string AllowedSigners =
        "sshmanager-release ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINA7i2sOU03vcOMdvoyMlz09XEufb34fqN8PprEfNd8r\n" +
        "sshmanager-release ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKAaGIE8KESxqOVKe9VK88FTHud6/7Q19/KngCUQBemG\n";
    static readonly HttpClient Http = CreateClient();
    static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSHManager");
    static readonly string Folder = Path.Combine(DataFolder, "updates");
    static readonly string StateFile = Path.Combine(DataFolder, "update-state.json");

    public string Current { get; } = Application.ProductVersion.Split('+')[0];
    public string? Latest { get; private set; }
    public bool Available { get; private set; }
    public string Status { get; private set; } = "idle";   // idle | downloading | verifying | installing
    public string? Error { get; private set; }
    public event Action? Changed;
    string? assetUrl, shaUrl, sigUrl, announced;

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

    /// <summary>Whether this Windows user can install (is an administrator, also behind UAC).</summary>
    public static bool UserIsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            // TokenElevationType: 2 = elevated admin, 3 = admin with UAC's limited token
            if (GetTokenInformation(id.Token, 18, out var type, 4, out _) && type is 2 or 3) return true;
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return true; }   // can't tell: let Windows ask
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    public void Start() => _ = Task.Run(async () =>
    {
        CheckPreviousInstall();
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            await CheckAsync();
            if (Available && settings.AutoUpdate && !ServerUpdatesThisPc() && IsInstalled()) await AutoInstallAsync();
            await Task.Delay(TimeSpan.FromHours(6));
        }
    });

    async Task AutoInstallAsync()
    {
        var state = LoadState();
        string? why = null;
        if (!UserIsAdmin()) why = "Ask an administrator of this PC to install it (open SSH Manager, Install).";
        else if (state.Declined == Latest) why = "Open SSH Manager to install it.";
        else if (state.FailedVersion == Latest && DateTime.UtcNow < state.FailedAt.AddHours(24 * Math.Min(state.FailedCount, 3)))
            why = "The last try to install it didn't work; open SSH Manager to try again.";
        if (why is null)
        {
            await InstallAsync();
            return;
        }
        if (announced != Latest)
        {
            announced = Latest;
            notify("SSH Manager update", $"Version {Latest} is available. {why}");
        }
    }

    public async Task CheckAsync()
    {
        try
        {
            using var r = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Latest = root.TryGetProperty("tag_name", out var tag) ? tag.GetString()?.TrimStart('v', 'V') : null;
            assetUrl = shaUrl = sigUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == Asset) assetUrl = url;
                    if (name == Asset + ".sha256") shaUrl = url;
                    if (name == Asset + ".sig") sigUrl = url;
                }
            var was = Available;
            Available = assetUrl is not null && Latest is not null && Newer(Latest, Current);
            Error = Available && (shaUrl is null || sigUrl is null)
                ? $"SSH Manager {Latest} has no signature or checksum, so it isn't installed from here. Download it from GitHub." : null;
            if (Available && !was && !(settings.AutoUpdate && IsInstalled() && UserIsAdmin()))
            {
                announced = Latest;
                notify("SSH Manager update", $"Version {Latest} is available. Open SSH Manager to install it.");
            }
        }
        catch (Exception e) { Error = "Couldn't check for updates: " + e.Message; }
        Changed?.Invoke();
    }

    /// <summary>"1.6.0", "v1.7.0-rc.1": only the leading digits of each part count; a pre-release
    /// comes before the release with the same numbers.</summary>
    public static (int, int, int, int) ParseVersion(string s)
    {
        var core = s.Trim().TrimStart('v', 'V');
        var dash = core.IndexOf('-');
        var pre = dash >= 0;
        if (pre) core = core[..dash];
        var parts = core.Split('.');
        int Part(int i) => i < parts.Length && Regex.Match(parts[i], @"^\d+") is { Success: true } m && int.TryParse(m.Value, out var v) ? v : 0;
        return (Part(0), Part(1), Part(2), pre ? 0 : 1);
    }

    public static bool Newer(string a, string b) => ParseVersion(a).CompareTo(ParseVersion(b)) > 0;

    public async Task InstallAsync()
    {
        if (!Available || assetUrl is null || Status != "idle") return;
        if (shaUrl is null || sigUrl is null)
        {
            Error = $"SSH Manager {Latest} has no signature or checksum, so it can't be installed from here. Download it from GitHub.";
            Changed?.Invoke();
            return;
        }
        var version = Latest!;
        string? file = null;
        FileStream? locked = null;
        try
        {
            Status = "downloading";
            Error = null;
            Changed?.Invoke();
            CleanFolder();
            Directory.CreateDirectory(Folder);
            file = Path.Combine(Folder, $"SSHManager-Setup-{Regex.Replace(version, "[^0-9A-Za-z.-]", "")}.exe");
            await using (var src = await Http.GetStreamAsync(assetUrl))
            await using (var dst = File.Create(file))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    if ((total += n) > MaxInstaller) throw new InvalidOperationException("The download is unexpectedly large.");
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                }
            }
            var expected = (await Http.GetStringAsync(shaUrl)).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$")) throw new InvalidOperationException("The checksum file is invalid.");
            var signature = await Http.GetByteArrayAsync(sigUrl);
            if (signature.Length > 64 << 10) throw new InvalidOperationException("The signature file is unexpectedly large.");

            Status = "verifying";
            Changed?.Invoke();
            // checked on the open file, which nobody can change until the installer has started
            locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(locked)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("The download didn't match its checksum.");
            locked.Position = 0;
            if (await VerifySignatureAsync(locked, signature, Folder) is { } bad) throw new InvalidOperationException(bad);

            Status = "installing";
            Changed?.Invoke();
            var state = LoadState();
            state.Pending = version;
            SaveState(state);
            // UseShellExecute so Windows can ask for admin rights; the installer closes this app and
            // opens it again afterwards (also when the update fails or is cancelled).
            var process = Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-") { UseShellExecute = true });
            locked.Dispose();
            locked = null;
            _ = WatchInstallerAsync(process, version);
        }
        catch (Exception e)
        {
            locked?.Dispose();
            if (file is not null) try { File.Delete(file); } catch (Exception) { }
            var state = LoadState();
            state.Pending = null;
            if (e is System.ComponentModel.Win32Exception)
            {
                state.Declined = version;   // UAC: no; don't ask again on its own for this version
                Error = "The update was cancelled.";
            }
            else
            {
                RecordFailure(state, version);
                Error = "Update failed: " + e.Message;
            }
            SaveState(state);
            Status = "idle";
            Changed?.Invoke();
        }
    }

    /// <summary>The installer closes this app when it works; an installer that ends while the app still
    /// runs failed (or did nothing).</summary>
    async Task WatchInstallerAsync(Process? process, string version)
    {
        try
        {
            if (process is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await process.WaitForExitAsync(cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(20));
            var code = process.ExitCode;
            var state = LoadState();
            state.Pending = null;
            if (code != 0)
            {
                RecordFailure(state, version);
                Error = $"The installer for {version} stopped (exit code {code}).";
            }
            SaveState(state);
        }
        catch (Exception) { Error = $"The update to {version} didn't finish."; }
        Status = "idle";
        Changed?.Invoke();
    }

    /// <summary>At start: did the update started before finish? (Then this is the new version.)</summary>
    void CheckPreviousInstall()
    {
        var state = LoadState();
        if (state.Pending is { } pending)
        {
            if (ParseVersion(Current).CompareTo(ParseVersion(pending)) >= 0)
            {
                if (state.FailedVersion == pending) state.FailedVersion = null;
            }
            else
            {
                RecordFailure(state, pending);
                Error = $"The update to {pending} didn't finish; this PC still has {Current}.";
            }
            state.Pending = null;
            SaveState(state);
        }
        CleanFolder();
    }

    static void RecordFailure(UpdateState state, string version)
    {
        state.FailedCount = state.FailedVersion == version ? state.FailedCount + 1 : 1;
        state.FailedVersion = version;
        state.FailedAt = DateTime.UtcNow;
    }

    /// <summary>Old downloaded installers (also where versions before 1.7 put them).</summary>
    static void CleanFolder()
    {
        try { if (Directory.Exists(Folder)) foreach (var f in Directory.GetFiles(Folder)) try { File.Delete(f); } catch (Exception) { } }
        catch (Exception) { }
        try { foreach (var f in Directory.GetFiles(Path.GetTempPath(), "SSHManager-Setup-*.exe")) try { File.Delete(f); } catch (Exception) { } }
        catch (Exception) { }
    }

    /// <summary>Null when <paramref name="data"/> carries a valid release signature, else why not.
    /// Uses `ssh-keygen -Y verify` (OpenSSH 8.1 or newer): Windows' own, the Win32-OpenSSH MSI's, or one on PATH.</summary>
    internal static async Task<string?> VerifySignatureAsync(Stream data, byte[] signature, string workDir, string allowedSigners = AllowedSigners)
    {
        var signers = Path.Combine(workDir, "allowed_signers");
        var sig = Path.Combine(workDir, "installer.sig");
        var problem = "ssh-keygen.exe (OpenSSH) wasn't found";
        try
        {
            await File.WriteAllTextAsync(signers, allowedSigners, Encoding.ASCII);
            await File.WriteAllBytesAsync(sig, signature);
            var start = data.Position;
            foreach (var exe in KeygenCandidates())
            {
                data.Position = start;
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var a in new[] { "-Y", "verify", "-f", signers, "-I", Signer, "-n", Signer, "-s", sig }) psi.ArgumentList.Add(a);
                Process p;
                try { p = Process.Start(psi)!; }
                catch (Exception e) { problem = $"{exe}: {e.Message}"; continue; }
                using (p)
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    try
                    {
                        await data.CopyToAsync(p.StandardInput.BaseStream);   // the signed data comes on stdin
                        p.StandardInput.Close();
                    }
                    catch (IOException) { }   // it stopped reading (e.g. too old to know -Y)
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    try { await p.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { try { p.Kill(); } catch (Exception) { } problem = $"{exe} didn't answer"; continue; }
                    var output = string.Join("; ", ((await stdout) + "\n" + (await stderr))
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    if (p.ExitCode == 0 && output.StartsWith("Good", StringComparison.Ordinal)) return null;
                    if (output.Contains("unknown option") || output.Contains("usage:", StringComparison.OrdinalIgnoreCase))
                    {
                        problem = $"{exe} is too old to check signatures (needs OpenSSH 8.1 or newer)";
                        continue;
                    }
                    return $"The update's signature isn't valid ({(output.Length > 0 ? output : $"exit code {p.ExitCode}")}); not installing it.";
                }
            }
            return $"Couldn't check the update's signature: {problem}. Not installing it.";
        }
        finally
        {
            try { File.Delete(signers); } catch (Exception) { }
            try { File.Delete(sig); } catch (Exception) { }
        }
    }

    static IEnumerable<string> KeygenCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", "ssh-keygen.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { list.Add(Path.Combine(dir.Trim().Trim('"'), "ssh-keygen.exe")); } catch (ArgumentException) { }
        foreach (var f in list)
            if (File.Exists(f) && seen.Add(Path.GetFullPath(f))) yield return f;
    }

    // ---- what is remembered across restarts ----

    sealed class UpdateState
    {
        public string? Pending { get; set; }
        public string? Declined { get; set; }
        public string? FailedVersion { get; set; }
        public DateTime FailedAt { get; set; }
        public int FailedCount { get; set; }
    }

    static UpdateState LoadState()
    {
        try { return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFile)) ?? new(); }
        catch (Exception) { return new(); }
    }

    static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var tmp = StateFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch (Exception) { }
    }
}
