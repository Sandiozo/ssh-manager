using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSHManager;

/// <summary>Settings in %LOCALAPPDATA%\SSHManager\settings.json.</summary>
internal sealed class AppSettings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSHManager");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---- connection (filled in by the setup wizard) ----
    /// <summary>The PC to manage: a host alias from ~/.ssh/config, a computer name or an IP address.</summary>
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
    /// <summary>Preferred local port for the forward; another free port is used if it's taken.</summary>
    public int LocalPort { get; set; } = 8765;
    /// <summary>Name the PC reports for itself (learned when connecting); used in notifications.</summary>
    public string PcName { get; set; } = "";

    // ---- notifications and downloads ----
    public bool NotifyConnection { get; set; } = true;
    public bool NotifyFailedLogin { get; set; } = true;
    public bool NotifyUnknown { get; set; } = true;
    public bool NotifyTransfers { get; set; } = true;
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool AskWhereToSave { get; set; }

    // ---- window ----
    public int[]? WindowBounds { get; set; }
    public bool Maximized { get; set; }
    public bool TrayHintShown { get; set; }

    // ---- settings files from version 1.0 ----
    [JsonPropertyName("SshAlias"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySshAlias { get => null; set { if (!string.IsNullOrEmpty(value) && Host == "") Host = value; } }
    [JsonPropertyName("MainPcName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMainPcName { get => null; set { if (!string.IsNullOrEmpty(value) && LookupName == "") LookupName = value; } }

    [JsonIgnore] public bool IsConfigured => Host.Trim() != "";
    [JsonIgnore] public string Target => User.Trim() != "" ? $"{User.Trim()}@{Host.Trim()}" : Host.Trim();
    [JsonIgnore] public string DisplayName => PcName != "" ? PcName : Host != "" ? Host : "the PC";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { }
    }
}
