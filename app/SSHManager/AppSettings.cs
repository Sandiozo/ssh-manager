using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHManager;

/// <summary>One PC the app connects to (filled in by the setup wizard).</summary>
internal sealed class PcEntry
{
    public const string LocalId = "local";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>A host alias from ~/.ssh/config, a computer name or an IP address.</summary>
    public string Host { get; set; } = "";
    /// <summary>Windows user on that PC; empty = whatever ~/.ssh/config or ssh's default says.</summary>
    public string User { get; set; } = "";
    /// <summary>Private key to log in with; empty = ssh's defaults / ~/.ssh/config.</summary>
    public string KeyFile { get; set; } = "";
    /// <summary>Optional: look this name up with Windows (LLMNR/mDNS) and connect to that address. Helps
    /// when ssh can't resolve a LAN computer name that Windows can.</summary>
    public string LookupName { get; set; } = "";
    /// <summary>Port of the SSH Manager server on the PC (learned when connecting).</summary>
    public int RemotePort { get; set; } = 8765;
    /// <summary>Name the PC reports for itself (learned when connecting).</summary>
    public string PcName { get; set; } = "";
    /// <summary>MAC and addresses of the PC's network card (learned when connecting), for Wake-on-LAN.</summary>
    public string Mac { get; set; } = "";
    public List<string> Ips { get; set; } = [];

    [JsonIgnore] public bool IsLocal => Id == LocalId;
    [JsonIgnore] public string Target => User.Trim() != "" ? $"{User.Trim()}@{Host.Trim()}" : Host.Trim();
    [JsonIgnore] public string DisplayName => PcName != "" ? PcName : Host != "" ? Host : "this PC";
}

/// <summary>Settings in %LOCALAPPDATA%\SSHManager\settings.json.</summary>
internal sealed class AppSettings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSHManager");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<PcEntry> Pcs { get; set; } = [];
    /// <summary>What the window shows: a PC's id, or "all" for the overview.</summary>
    public string Selected { get; set; } = "";

    public bool NotifyConnection { get; set; } = true;
    public bool NotifyFailedLogin { get; set; } = true;
    public bool NotifyUnknown { get; set; } = true;
    public bool NotifyTransfers { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool AskWhereToSave { get; set; }
    public bool ShareClipboard { get; set; }
    public List<SyncPair> Syncs { get; set; } = [];
    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Identifies this app among computers with the same name (pairing and approvals).</summary>
    public string MachineId { get; set; } = "";

    public int[]? WindowBounds { get; set; }
    public bool Maximized { get; set; }
    public bool TrayHintShown { get; set; }

    // ---- settings files from earlier versions (one PC, stored at the top level) ----
    readonly PcEntry legacy = new();
    [JsonPropertyName("Host"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyHost { get => null; set => legacy.Host = value ?? ""; }
    [JsonPropertyName("SshAlias"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySshAlias { get => null; set { if (legacy.Host == "") legacy.Host = value ?? ""; } }
    [JsonPropertyName("User"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUser { get => null; set => legacy.User = value ?? ""; }
    [JsonPropertyName("KeyFile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyKeyFile { get => null; set => legacy.KeyFile = value ?? ""; }
    [JsonPropertyName("LookupName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLookupName { get => null; set => legacy.LookupName = value ?? ""; }
    [JsonPropertyName("MainPcName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMainPcName { get => null; set { if (legacy.LookupName == "") legacy.LookupName = value ?? ""; } }
    [JsonPropertyName("PcName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPcName { get => null; set => legacy.PcName = value ?? ""; }
    [JsonPropertyName("RemotePort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyRemotePort { get => null; set => legacy.RemotePort = value ?? 8765; }

    static AppSettings Read(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("empty settings");

    public static AppSettings Load()
    {
        AppSettings s = new();
        var bak = FilePath + ".bak";
        try
        {
            if (File.Exists(FilePath)) s = Read(FilePath);
            else if (File.Exists(bak)) s = Read(bak);   // a save stopped between its steps
        }
        catch (Exception e)
        {
            // keep the damaged file for a look later instead of overwriting it; fall back to the backup
            AppLog.Write("settings.json could not be read", e);
            try { File.Move(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}"); } catch (Exception) { }
            try { s = File.Exists(bak) ? Read(bak) : new(); }
            catch (Exception e2) { AppLog.Write("settings.json.bak could not be read either", e2); s = new(); }
        }
        if (s.MachineId == "") s.MachineId = Guid.NewGuid().ToString("N");
        if (s.Pcs.Count == 0 && s.legacy.Host != "")
        {
            s.Pcs.Add(s.legacy);
            s.Selected = s.legacy.Id;
        }
        s.Save();
        return s;
    }

    /// <summary>Lock for the lists (PCs, sync pairs) when they are read or changed off the window thread.</summary>
    [JsonIgnore] public object Gate { get; } = new();

    /// <summary>Saves from any thread: written to a temp file, then swapped in, keeping the previous file as .bak.</summary>
    public void Save()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var json = JsonSerializer.Serialize(this, Json);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                    else File.Move(tmp, FilePath);
                    return;
                }
                catch (InvalidOperationException) { Thread.Sleep(20); }   // a list changed while it was being written
                catch (IOException) { Thread.Sleep(50); }                 // e.g. a virus scanner has the file open
                catch (Exception e)
                {
                    AppLog.Write("Saving settings failed", e);
                    return;
                }
            }
            AppLog.Write("Saving settings failed 3 times");
        }
    }
}
