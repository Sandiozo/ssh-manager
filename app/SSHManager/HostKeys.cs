using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHManager;

/// <summary>How ssh checks who answers for a PC: the name its host key is filed under and the file that
/// holds it. With <paramref name="AcceptNew"/> the file is empty and the first key seen is written to it
/// (trust on first use); otherwise only a key that is in the file is accepted.</summary>
internal sealed record HostTrust(string Alias, string File, bool AcceptNew);

/// <summary>
/// The host keys (identities) of the PCs the app connects to. Every saved PC has a small known-hosts file
/// of its own in the app's folder, with its key filed under a name made from the PC's id instead of its
/// address. ssh is told to check that name in that file only, so a PC is recognised by its key at whatever
/// address it is reached, and a computer that answers with another key is refused. The user's own
/// known_hosts is only read here, to carry over keys ssh already had on record.
/// </summary>
internal static partial class HostKeys
{
    /// <summary>Where the files are (a test can point it somewhere else).</summary>
    public static string Folder { get; set; } = Path.Combine(AppSettings.Folder, "hostkeys");

    /// <summary>Another ssh config file than the user's, to look their known-hosts files up with (for tests).</summary>
    public static string? SshConfig { get; set; }

    /// <summary>Held while a file is checked and replaced: connections and the setup wizard write from different threads.</summary>
    static readonly object Gate = new();

    /// <summary>The name a PC's key is filed under: safe on a command line and as a file name.</summary>
    public static string Alias(string pcId) =>
        SimpleId().IsMatch(pcId) ? "sshmgr-" + pcId
        : "sshmgr-x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pcId)))[..16].ToLowerInvariant();

    public static string FileFor(string pcId) => Path.Combine(Folder, Alias(pcId));

    /// <summary>The keys in one of the app's files, each as "type base64". A file that exists but can't be
    /// read is an error, not "no key": the caller must not fall back to trusting a new one.</summary>
    public static List<string> Read(string file)
    {
        try { return Keys(File.ReadAllLines(file)); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
    }

    /// <summary>Replaces a file's keys (written next to it, then swapped in). No keys: the file is removed.</summary>
    public static void Write(string file, string alias, IReadOnlyCollection<string> keys)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (keys.Count == 0)
            {
                File.Delete(file);
                return;
            }
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, string.Concat(keys.Select(k => $"{alias} {k}\n")));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, file, overwrite: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    Thread.Sleep(100);   // ssh is reading it right now
                }
            }
        }
    }

    /// <summary>The keys in known_hosts lines (also as `ssh-keygen -F` prints them), each as "type base64".
    /// Certificate authorities and revoked keys are left out.</summary>
    public static List<string> Keys(IEnumerable<string> lines)
    {
        var keys = new List<string>();
        var revoked = new HashSet<string>();
        foreach (var raw in lines)
        {
            var p = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            var marker = p[0].StartsWith('@') ? p[0] : "";
            if (marker != "") p = p[1..];
            if (p.Length < 3 || !KeyType().IsMatch(p[1]) || !KeyBlob().IsMatch(p[2])) continue;
            var key = $"{p[1]} {p[2]}";
            if (marker == "@revoked") revoked.Add(key);
            else if (marker == "" && !keys.Contains(key)) keys.Add(key);
        }
        keys.RemoveAll(revoked.Contains);
        return keys;
    }

    /// <summary>Keys as people compare them, the way ssh-keygen shows them: "ED25519 SHA256:…".</summary>
    public static List<string> Fingerprints(IEnumerable<string> keys) => keys.Select(key =>
    {
        var p = key.Split(' ');
        var kind = p[0] switch
        {
            "ssh-ed25519" => "ED25519",
            "ssh-rsa" => "RSA",
            "ssh-dss" => "DSA",
            _ when p[0].StartsWith("ecdsa-") => "ECDSA",
            _ => p[0],
        };
        try { return $"{kind} SHA256:{Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(p[1]))).TrimEnd('=')}"; }
        catch (FormatException) { return $"{kind} (unreadable key)"; }
    }).Distinct().ToList();

    /// <summary>How ssh checks a saved PC. A PC saved by an earlier version gets its file here, once, from
    /// the keys ssh already has on record for its saved address (read from known_hosts, not fetched from
    /// the network). With nothing on record the first connection may record the key: in a separate file,
    /// kept by <see cref="Keep"/> only once that connection turned out to be the PC. A PC that had a key
    /// saved before (<see cref="PcEntry.KeySaved"/>) is never trusted anew: its missing file is an error.</summary>
    public static async Task<HostTrust> ForSavedAsync(PcEntry pc)
    {
        var (alias, file) = (Alias(pc.Id), FileFor(pc.Id));
        var fresh = file + ".new";
        Directory.CreateDirectory(Folder);
        File.Delete(fresh);   // left by a first connection that didn't work out
        if (Read(file).Count > 0) return new(alias, file, AcceptNew: false);
        if (pc.KeySaved)
            throw new IOException("The file with its host key is missing or empty. Use Change connection and save the PC again to check its identity.");
        var known = await OnRecordAsync(pc);
        lock (Gate)
        {
            if (Read(file).Count > 0) return new(alias, file, AcceptNew: false);   // saved meanwhile (the setup wizard)
            if (known.Count == 0) return new(alias, fresh, AcceptNew: true);
            Write(file, alias, known);
            return new(alias, file, AcceptNew: false);
        }
    }

    /// <summary>Makes the keys recorded by a first connection the PC's saved identity. False when another
    /// key was saved for the PC meanwhile (the setup wizard): that one stays.</summary>
    public static bool Keep(HostTrust firstUse, string pcId, IReadOnlyCollection<string> keys)
    {
        try { File.Delete(firstUse.File); } catch (Exception) { }
        lock (Gate)
        {
            var saved = Read(FileFor(pcId));
            if (saved.Count > 0) return saved.Intersect(keys).Any();
            Write(FileFor(pcId), Alias(pcId), keys);
            return true;
        }
    }

    /// <summary>The keys the user's own ssh has on record for this PC's saved address or ssh-config host:
    /// looked up the way ssh would (its known-hosts files, port and HostKeyAlias from the ssh config), by
    /// ssh-keygen, so hashed names are found too.</summary>
    public static async Task<List<string>> OnRecordAsync(PcEntry pc)
    {
        var args = new List<string>();
        if (SshConfig is not null) args.AddRange(["-F", SshConfig]);
        args.AddRange(["-G", "--", pc.Target]);
        // ssh prints paths as UTF-8 (a profile folder with other than English letters)
        var (code, effective) = await Ssh.RunAsync("ssh.exe", args, 15000, output: Encoding.UTF8);
        if (code == -1) throw new IOException("Reading the ssh settings took too long.");
        var (name, files) = Place(code == 0 ? effective : "", pc.Host);
        var lines = new List<string>();
        foreach (var f in files.Where(File.Exists))
        {
            var (found, text) = await Ssh.RunAsync("ssh-keygen.exe", ["-F", name, "-f", f], 15000);
            if (found == -1) throw new IOException("Reading the host keys ssh has on record took too long.");
            lines.AddRange(text.Split('\n'));
        }
        return Keys(lines);
    }

    /// <summary>From what `ssh -G` prints for a host: the name ssh files its key under, and the known-hosts
    /// files it looks in. Empty input (ssh -G failed): ssh's defaults.</summary>
    public static (string name, List<string> files) Place(string effective, string host)
    {
        string hostname = host.Trim(), alias = "";
        string? user = null, global = null;
        var port = 22;
        foreach (var raw in effective.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            var value = line[(space + 1)..].Trim();
            switch (line[..space].ToLowerInvariant())
            {
                case "hostname": hostname = value; break;
                case "port": if (int.TryParse(value, out var p)) port = p; break;
                case "hostkeyalias": alias = value; break;
                case "userknownhostsfile": user = value; break;
                case "globalknownhostsfile": global = value; break;
            }
        }
        var name = alias != "" ? alias : port == 22 ? hostname : $"[{hostname}]:{port}";
        var files = Paths(user ?? "~/.ssh/known_hosts ~/.ssh/known_hosts2")
            .Concat(Paths(global ?? @"__PROGRAMDATA__\ssh/ssh_known_hosts __PROGRAMDATA__\ssh/ssh_known_hosts2"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (name.ToLowerInvariant(), files);
    }

    /// <summary>The files in a known-hosts setting as ssh -G prints it: separated by spaces, not quoted, so
    /// a space only starts a new file when a path's beginning follows it.</summary>
    static IEnumerable<string> Paths(string value)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var part in NextPath().Split(value.Trim()))
        {
            var p = part.Trim();
            if (p.StartsWith("__PROGRAMDATA__", StringComparison.OrdinalIgnoreCase)) p = programData + p["__PROGRAMDATA__".Length..];
            else if (p.StartsWith("~/") || p.StartsWith(@"~\")) p = home + p[1..];
            if (p == "" || !Path.IsPathFullyQualified(p)) continue;   // "none", NUL
            yield return Path.GetFullPath(p);
        }
    }

    [GeneratedRegex(@"^[a-z0-9]{1,32}\z")]
    private static partial Regex SimpleId();

    // "ssh-ed25519", "ecdsa-sha2-nistp256", "sk-ssh-ed25519@openssh.com": not the bare numbers of a protocol 1 line
    [GeneratedRegex(@"^[a-z][a-z0-9@._]*-[a-z0-9@._-]+\z")]
    private static partial Regex KeyType();

    [GeneratedRegex(@"^AAAA[A-Za-z0-9+/]+={0,2}\z")]
    private static partial Regex KeyBlob();

    [GeneratedRegex(@" +(?=[A-Za-z]:[\\/]|__PROGRAMDATA__|[\\/~])")]
    private static partial Regex NextPath();
}
