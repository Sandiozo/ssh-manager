using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SSHManager;

/// <summary>
/// What still has to be done to PC browser profiles, kept in a small file so it is not forgotten when the
/// app closes first: profiles to empty before they are used again (the PC was removed, or its entry now
/// leads to another computer), and profiles of removed PCs whose folder is deleted at the next start.
/// Only a profile named here is ever emptied or deleted: a PC that is merely missing from the list
/// (settings that couldn't be read, this PC's own server being reinstalled) keeps what it stored.
/// A file that is there but can't be read could have named any profile: then every PC's profile is emptied
/// before its next use (each once, also across starts), and no folder is deleted for it.
/// </summary>
internal sealed partial class ProfileMarks
{
    sealed class Saved
    {
        public List<string>? Empty { get; set; }
        public List<string>? Removed { get; set; }
        /// <summary>The file couldn't be read once: every PC profile not in <see cref="Emptied"/> is emptied before use.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? EmptyAll { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Emptied { get; set; }
    }

    readonly string path;
    readonly object gate = new();
    readonly HashSet<string> toEmpty = [], removed = [];
    /// <summary>Since the file couldn't be read: the profiles emptied since then (the others still have to be).</summary>
    bool emptyAll;
    readonly HashSet<string> emptiedSince = [];

    [GeneratedRegex(@"\Apc-[0-9a-f]{32}\z")]
    private static partial Regex PcProfileName();

    /// <summary>A name the app gives a PC's profile. A name from the file becomes part of a folder that is
    /// deleted, so nothing else is taken from it.</summary>
    public static bool IsPcProfile(string? name) => name is not null && PcProfileName().IsMatch(name);

    public ProfileMarks(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path) || Read(path) is not { } text) return;
            var saved = JsonSerializer.Deserialize<Saved>(text) ?? throw new JsonException("The file holds nothing.");
            toEmpty.UnionWith((saved.Empty ?? []).Where(IsPcProfile));
            removed.UnionWith((saved.Removed ?? []).Where(IsPcProfile));
            emptyAll = saved.EmptyAll == true;
            if (emptyAll) emptiedSince.UnionWith((saved.Emptied ?? []).Where(IsPcProfile));
        }
        catch (Exception e)
        {
            AppLog.Write("Reading which browser profiles are to be cleaned up (each PC's browser data is deleted before its next use)", e);
            toEmpty.Clear();
            removed.Clear();
            emptiedSince.Clear();
            emptyAll = true;
        }
    }

    /// <summary>A file that can't be opened is tried again this often, this long apart (about a second in all).</summary>
    const int ReadTries = 6;
    static readonly TimeSpan ReadPause = TimeSpan.FromMilliseconds(200);

    /// <summary>The file's text, or null when there is no file (any more). A file that someone else holds
    /// open for a moment (an antivirus scan, a backup) is read once it is free; one that stays shut throws,
    /// like one whose text can't be understood (that is not tried again).</summary>
    static string? Read(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < ReadTries) { Thread.Sleep(ReadPause); }
        }
    }

    /// <summary>The profile holds data of a removed PC or of another computer: it is emptied before a page is loaded in it.</summary>
    public bool NeedsEmptying(string profile)
    {
        lock (gate) return toEmpty.Contains(profile) || (emptyAll && !emptiedSince.Contains(profile));
    }

    /// <summary>Profiles of PCs that were removed: their folders can go.</summary>
    public string[] Removed()
    {
        lock (gate) return [.. removed];
    }

    /// <summary>The PC was removed (<paramref name="pcRemoved"/>) or its entry now leads elsewhere.</summary>
    public void MustEmpty(string profile, bool pcRemoved) => Change(() => toEmpty.Add(profile) | (pcRemoved && removed.Add(profile)));

    public void Emptied(string profile) => Change(() => toEmpty.Remove(profile) | (emptyAll && emptiedSince.Add(profile)));

    /// <summary>A PC in the list uses the profile (again): its folder stays.</summary>
    public void InUse(string profile) => Change(() => removed.Remove(profile));

    /// <summary>The folder is gone, and everything in it.</summary>
    public void Deleted(string profile) => Change(() => toEmpty.Remove(profile) | removed.Remove(profile) | (emptyAll && emptiedSince.Add(profile)));

    void Change(Func<bool> change)
    {
        lock (gate)
        {
            if (!change()) return;   // nothing new to write down
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Saved
                {
                    Empty = [.. toEmpty.Order()], Removed = [.. removed.Order()],
                    EmptyAll = emptyAll ? true : null, Emptied = emptyAll ? [.. emptiedSince.Order()] : null,
                }));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) { AppLog.Write("Saving which browser profiles are to be cleaned up", e); }
        }
    }
}
