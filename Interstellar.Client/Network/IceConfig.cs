using System.Text.Json;

namespace Interstellar.Network;

internal static class IceConfig
{
    internal sealed class TurnEntry
    {
        /// <summary>turn:host:port — plain UDP only (turns: TLS is skipped).</summary>
        public string Url = "";
        public string Username = "";
        public string Credential = "";
        /// <summary>From the server yml; "" = untagged/global (always eligible).</summary>
        public string Region = "";
    }

    private static readonly object _lock = new();
    private static List<string> _stun = new();
    private static List<TurnEntry> _turns = new();
    private static volatile bool _forceRelayOnly;
    private static int _version;

    /// <summary>Bumped on every apply — see the class summary.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Server's forceRelayOnly: relay-only for every peer (BCL parity).</summary>
    public static bool ForceRelayOnly => _forceRelayOnly;

    public static List<string> StunServers
    {
        get { lock (_lock) return new List<string>(_stun); }
    }

    public static List<TurnEntry> Turns
    {
        get { lock (_lock) return new List<TurnEntry>(_turns); }
    }

    /// <summary>Distinct non-empty regions, sorted — the manual picker's options.</summary>
    public static List<string> Regions
    {
        get
        {
            lock (_lock)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in _turns)
                    if (!string.IsNullOrWhiteSpace(t.Region)) set.Add(t.Region.Trim());
                var list = new List<string>(set);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }
    }

    /// <summary>Parse a clientPeerConfig payload. Logs counts and regions only —
    /// usernames and credentials never reach the log.</summary>
    public static void Apply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            bool force = root.TryGetProperty("forceRelayOnly", out var fr)
                         && fr.ValueKind == JsonValueKind.True;
            var stun = new List<string>();
            var turns = new List<TurnEntry>();

            if (root.TryGetProperty("iceServers", out var ice) && ice.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ice.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    string user = Str(s, "username");
                    string cred = Str(s, "credential");
                    string region = Str(s, "region");

                    foreach (var url in ReadUrls(s))
                    {
                        var l = url.Trim();
                        if (l.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
                            turns.Add(new TurnEntry
                            {
                                Url = l,
                                Username = user,
                                Credential = cred,
                                Region = region,
                            });
                        else if (l.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                            stun.Add(l);
                        // stuns:/turns: are TLS transports this transport cannot
                        // speak — skipped rather than silently failing later.
                    }
                }
            }

            string regions;
            lock (_lock)
            {
                _stun = stun;
                _turns = turns;
                _forceRelayOnly = force;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in turns)
                    if (!string.IsNullOrWhiteSpace(t.Region)) set.Add(t.Region.Trim());
                var list = new List<string>(set);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                regions = string.Join(",", list);
            }
            Interlocked.Increment(ref _version);

            InterstellarPlugin.Logger?.LogInfo(
                "[Ice] server config: stun=" + stun.Count + " turn=" + turns.Count +
                (regions.Length > 0 ? " regions=[" + regions + "]" : "") +
                " forceRelayOnly=" + force);
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger?.LogWarning(
                "[Ice] clientPeerConfig parse failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Socket gone / room torn down — next connect re-applies.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _stun = new List<string>();
            _turns = new List<TurnEntry>();
            _forceRelayOnly = false;
        }
        Interlocked.Increment(ref _version);
    }

    private static string Str(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }
    
    private static List<string> ReadUrls(JsonElement s)
    {
        var list = new List<string>();
        if (!s.TryGetProperty("urls", out var u)) return list;
        if (u.ValueKind == JsonValueKind.String)
        {
            var v = u.GetString();
            if (!string.IsNullOrEmpty(v)) list.Add(v!);
        }
        else if (u.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in u.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String) continue;
                var v = x.GetString();
                if (!string.IsNullOrEmpty(v)) list.Add(v!);
            }
        }
        return list;
    }
}
