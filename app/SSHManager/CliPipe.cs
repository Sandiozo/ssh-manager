using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SSHManager;

/// <summary>
/// Lets the command-line tool (sshmgr.exe) use the PCs this app is connected to: on a local named pipe
/// that only this Windows user can open (remote clients are rejected), it answers which PCs there are
/// and, for one PC, the local port of its connection and its access token. The tool then calls that
/// PC's server through the app's port forward, so it needs no SSH connection or token of its own.
///
/// Protocol: one JSON line each way. {"cmd":"pcs"} lists the PCs; {"cmd":"pc","pc":"name or id"}
/// returns {id, name, state, message, port, token} (port and token only while connected) or {error}.
/// </summary>
internal sealed class CliPipe(Func<List<Link>> links) : IDisposable
{
    public static string PipeName => "SSHManager.Cli." + WindowsIdentity.GetCurrent().User!.Value;

    readonly CancellationTokenSource stop = new();

    public void Start() => _ = Task.Run(() => ServeAsync(stop.Token));

    async Task ServeAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = Create(first); }
            catch (Exception) { return; }   // another copy of the app (another sign-in of this user) serves it
            first = false;
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (Exception)
            {
                pipe.Dispose();
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => AnswerAsync(pipe));
        }
    }

    async Task AnswerAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) return;
                var answer = JsonSerializer.Serialize(Answer(line)) + "\n";
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer), timeout.Token);
                await pipe.FlushAsync(timeout.Token);
                pipe.WaitForPipeDrain();
            }
            catch (Exception) { }
        }
    }

    object Answer(string line)
    {
        string cmd, query;
        try
        {
            using var doc = JsonDocument.Parse(line);
            cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
            query = doc.RootElement.TryGetProperty("pc", out var p) ? (p.GetString() ?? "").Trim() : "";
        }
        catch (Exception) { return new { error = "Bad request." }; }

        var all = links();
        var version = Application.ProductVersion.Split('+')[0];
        if (cmd == "pcs")
            return new
            {
                version,
                pcs = all.Select(l => new
                {
                    id = l.Pc.Id, name = l.Pc.DisplayName, host = l.Pc.IsLocal ? "" : l.Pc.Host, local = l.Pc.IsLocal,
                    state = l.State.ToString(), message = l.Message, version = l.Version,
                }),
            };
        if (cmd != "pc") return new { error = $"Unknown command: {cmd}" };

        var found = Find(all, query);
        if (found.Count == 0)
            return new { error = $"No PC called \"{query}\" in SSH Manager. PCs: {string.Join(", ", all.Select(l => l.Pc.DisplayName))}" };
        if (found.Count > 1)
            return new { error = $"\"{query}\" matches more than one PC ({string.Join(", ", found.Select(l => $"{l.Pc.DisplayName} [{l.Pc.Id}]"))}); use the id." };
        var link = found[0];
        var up = link.State == LinkState.Connected;
        return new
        {
            id = link.Pc.Id, name = link.Pc.DisplayName, state = link.State.ToString(), message = link.Message,
            port = up ? link.Port : 0, token = up ? link.Token : "",
        };
    }

    /// <summary>By id, else by the PC's name, the name or address it was added with, or its lookup name.</summary>
    static List<Link> Find(List<Link> all, string query)
    {
        var byId = all.Where(l => l.Pc.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count > 0) return byId;
        bool Is(string s) => s.Trim() != "" && s.Trim().Equals(query, StringComparison.OrdinalIgnoreCase);
        return all.Where(l => Is(l.Pc.DisplayName) || Is(l.Pc.PcName) || Is(l.Pc.Host) || Is(l.Pc.LookupName)).ToList();
    }

    // ---- the pipe: this user only, owned by this user (the tool checks that), no remote clients ----

    const uint PipeAccessDuplex = 0x3, FileFlagOverlapped = 0x40000000, FileFlagFirstPipeInstance = 0x80000;
    const uint PipeRejectRemoteClients = 0x8, PipeUnlimitedInstances = 255;

    static NamedPipeServerStream Create(bool first)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            var handle = CreateNamedPipe(@"\\.\pipe\" + PipeName,
                PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0),
                PipeRejectRemoteClients, PipeUnlimitedInstances, 4096, 4096, 0, ref sa);
            if (handle.IsInvalid) throw new IOException($"Couldn't create the pipe (error {Marshal.GetLastWin32Error()}).");
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    public void Dispose() => stop.Cancel();
}
