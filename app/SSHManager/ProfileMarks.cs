using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHManager;

/// <summary>
/// What still has to be done to PC browser profiles, kept in a small file so it is not forgotten when the
/// app closes first: profiles to empty before they are used again (the PC was removed, or its entry now
/// leads to another computer), and profiles of removed PCs whose folder is deleted at the next start.
/// Only a profile named here is ever emptied or deleted: a PC that is merely missing from the list
/// (settings that couldn't be read, this PC's own server being reinstalled) keeps what it stored.
/// </summary>
internal sealed partial class ProfileMarks
{
    sealed class Saved
    {
        public List<string>? Empty { get; set; }
        public List<string>? Removed { get; set; }
    }

    readonly string path;
    readonly object gate = new();
    readonly HashSet<string> toEmpty = [], removed = [];

    [GeneratedRegex("^pc-[0-9a-f]{32}$")]
    private static partial Regex PcProfileName();

    /// <summary>A name the app gives a PC's profile. A name from the file becomes part of a folder that is
    /// deleted, so nothing else is taken from it.</summary>
    public static bool IsPcProfile(string? name) => name is not null && PcProfileName().IsMatch(name);

    public ProfileMarks(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            toEmpty.UnionWith((saved?.Empty ?? []).Where(IsPcProfile));
            removed.UnionWith((saved?.Removed ?? []).Where(IsPcProfile));
        }
        catch (Exception e) { AppLog.Write("Reading which browser profiles are to be cleaned up", e); }
    }

    /// <summary>The profile holds data of a removed PC or of another computer: it is emptied before a page is loaded in it.</summary>
    public bool NeedsEmptying(string profile)
    {
        lock (gate) return toEmpty.Contains(profile);
    }

    /// <summary>Profiles of PCs that were removed: their folders can go.</summary>
    public string[] Removed()
    {
        lock (gate) return [.. removed];
    }

    /// <summary>The PC was removed (<paramref name="pcRemoved"/>) or its entry now leads elsewhere.</summary>
    public void MustEmpty(string profile, bool pcRemoved) => Change(() => toEmpty.Add(profile) | (pcRemoved && removed.Add(profile)));

    public void Emptied(string profile) => Change(() => toEmpty.Remove(profile));

    /// <summary>A PC in the list uses the profile (again): its folder stays.</summary>
    public void InUse(string profile) => Change(() => removed.Remove(profile));

    /// <summary>The folder is gone, and everything in it.</summary>
    public void Deleted(string profile) => Change(() => toEmpty.Remove(profile) | removed.Remove(profile));

    void Change(Func<bool> change)
    {
        lock (gate)
        {
            if (!change()) return;   // nothing new to write down
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Saved { Empty = [.. toEmpty.Order()], Removed = [.. removed.Order()] }));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) { AppLog.Write("Saving which browser profiles are to be cleaned up", e); }
        }
    }
}
