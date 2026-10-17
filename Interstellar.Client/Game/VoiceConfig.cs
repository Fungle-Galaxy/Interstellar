using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace Interstellar.Voice;

/// <summary>Modifier half of a rebindable hotkey chord. Each bit is a key *class*
/// (either side of Ctrl counts), so a chord captured with LeftCtrl still fires on
/// RightCtrl — the way every OS shortcut list works.</summary>
[System.Flags]
public enum VoiceChordMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Cmd = 8,
}

/// <summary>The five rebindable actions, each backed by one VoiceChat.Keybinds entry.</summary>
public enum VoiceChord
{
    SettingsPanel,
    PublicLobby,
    PlayerVolume,
    CycleMic,
    ToggleSpeaker,
}

/// <summary>
/// BCL-compatible voice chat configuration.
/// Settings structure mirrors the BetterCrewLink independent client.
/// </summary>
public static class VoiceConfig
{
    public static VoiceRoomSettings SyncedRoomSettings { get; } = new();

    /// <summary>Fired when synced room settings change (received from host via voice server).</summary>
    public static Action<VoiceRoomSettings>? OnSyncedSettingsChanged;

    // ── Server ────────────────────────────────────────────
    /// <summary>The one voice server — BCL mode: the client no longer offers a
    /// server switch, so every dialer, publisher and HUD reads the same URL.</summary>
    public const string ForcedServerUrl = "https://bcl.server.amongusclub.cn";

    public static string GetActiveServerURL() => ForcedServerUrl;

    public static bool IsBeijingServer => GetActiveServerURL().Contains("bcl.server.amongusclub.cn");

    public static string GetServerLocationName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "Unknown";
        if (url.Contains("bcl-na.server.amongusclub.cn")) return "NA (US)";
        if (url.Contains("bcl.server.amongusclub.cn")) return "Beijing";
        if (url.Contains("bettercrewl.ink")) return "Official";
        return url.Length > 25 ? url[..25] + "..." : url;
    }

    // ── P2P ─────────────────────────────────────────────────
    /// <summary>
    /// Comma-separated STUN list used to discover the public (server-reflexive)
    /// address for hole punching. Whichever server answers first provides the
    /// candidate — a pile of them is used because some are unreachable from any
    /// given network.
    ///
    /// Deliberately config-file only: it never appears in the settings panel, so a
    /// private/on-prem STUN endpoint can be dropped into the local cfg without it
    /// ever ending up in a repository.
    /// </summary>
    public static string P2PStunServers => _p2pStun?.Value ?? DefaultStunServers;

    /// <summary>NAT fix (BCL's natFix): skip hole punching and relay every peer
    /// through TURN from the start. Default off — the automatic TURN fallback
    /// already rescues failed punches, and forcing relay makes every voice take
    /// the long way. The server's forceRelayOnly has the same effect and is not
    /// overridable (it arrives per connect in clientPeerConfig).</summary>
    public static bool NatFix
    {
        get => _natFix?.Value ?? false;
        set { if (_natFix != null) _natFix.Value = value; }
    }

    /// <summary>Manual TURN region ("" = the stepper's Auto option: race every
    /// relay the server sent and keep the lowest-latency one). Options come
    /// from the region tags in the server's config.</summary>
    public static string TurnRegion
    {
        get => _turnRegion?.Value ?? "";
        set { if (_turnRegion != null) _turnRegion.Value = value; }
    }

    /// <summary>Optional local TURN override. All three default to empty: by
    /// default every credential arrives with clientPeerConfig and nothing
    /// sensitive exists in this file. A non-empty URL pins TURN to that server
    /// regardless of region or latency (escape hatch for a private relay).</summary>
    public static string TurnOverrideUrl => _turnOverrideUrl?.Value ?? "";
    public static string TurnOverrideUsername => _turnOverrideUser?.Value ?? "";
    public static string TurnOverrideCredential => _turnOverridePass?.Value ?? "";

    private const string DefaultStunServers =
        "stun:stun.l.google.com:19302," +
        "stun:stun1.l.google.com:19302," +
        "stun:stun2.l.google.com:19302," +
        "stun:stun.cloudflare.com:3478," +
        "stun:global.stun.twilio.com:3478," +
        "stun:stun.qq.com:3478," +
        "stun:stun.miwifi.com:3478," +
        "stun:stun.synology.com:3478," +
        "stun:stun.voipgate.com:3478," +
        "stun:stun.sipgate.net:3478," +
        "stun:stun.ekiga.net:3478," +
        "stun:stun.counterpath.com:3478," +
        "stun:stun.ideasip.com:3478," +
        "stun:stun.sonetel.net:3478";

    // ── Audio devices ──────────────────────────────────────
    public static string MicrophoneDevice => _mic?.Value ?? "";
    public static string SpeakerDevice => _speaker?.Value ?? "";

    // ── Volume ─────────────────────────────────────────────
    public static float MasterVolume
    {
        get { float v = _masterVol?.Value ?? 1f; return float.IsFinite(v) ? Math.Clamp(v, 0.1f, 3f) : 1f; }
        set { if (_masterVol != null && float.IsFinite(value)) _masterVol.Value = value; }
    }
    public static float MicVolume
    {
        get { float v = _micVol?.Value ?? 1f; return float.IsFinite(v) ? Math.Clamp(v, 0.1f, 3f) : 1f; }
        set { if (_micVol != null && float.IsFinite(value)) _micVol.Value = value; }
    }

    // ── UI ─────────────────────────────────────────────────
    /// <summary>Scale multiplier for the bottom-left mic/speaker buttons, background
    /// plate included: 1 is the stock size, 0.5 half, 2 double.</summary>
    public static float HotkeyButtonScale
    {
        get { float v = _hotkeyBtnScale?.Value ?? 1f; return float.IsFinite(v) ? Math.Clamp(v, 0.5f, 2f) : 1f; }
        set { if (_hotkeyBtnScale != null && float.IsFinite(value)) _hotkeyBtnScale.Value = value; }
    }

    // ── Audio processing ───────────────────────────────────
    public static bool NoiseSuppression
    {
        get => _noiseSuppression?.Value ?? true;
        set { if (_noiseSuppression != null) _noiseSuppression.Value = value; }
    }
    public static bool EchoCancellation
    {
        get => _echoCancellation?.Value ?? true;
        set { if (_echoCancellation != null) _echoCancellation.Value = value; }
    }

    // ── VAD ────────────────────────────────────────────────
    public static bool VADEnabled
    {
        get => _vadEnabled?.Value ?? true;
        set { if (_vadEnabled != null) _vadEnabled.Value = value; }
    }

    // ── Host room settings (synced via server when host) ───
    public static float HostMaxChatDistance
    {
        get => Math.Clamp(_hostMaxDist?.Value ?? 6f, 1.5f, 20f);
        set { if (_hostMaxDist != null) _hostMaxDist.Value = value; }
    }
    public static bool HostWallsBlockSound
    {
        get => _hostWallsBlock?.Value ?? true;
        set { if (_hostWallsBlock != null) _hostWallsBlock.Value = value; }
    }
    public static bool HostOnlyHearInSight
    {
        get => _hostOnlyHearInSight?.Value ?? false;
        set { if (_hostOnlyHearInSight != null) _hostOnlyHearInSight.Value = value; }
    }
    public static bool HostImpostorHearGhosts
    {
        get => _hostImpGhost?.Value ?? false;
        set { if (_hostImpGhost != null) _hostImpGhost.Value = value; }
    }
    public static bool HostOnlyGhostsCanTalk
    {
        get => _hostOnlyGhost?.Value ?? false;
        set { if (_hostOnlyGhost != null) _hostOnlyGhost.Value = value; }
    }
    public static bool HostHearInVent
    {
        get => _hostHearVent?.Value ?? true;
        set { if (_hostHearVent != null) _hostHearVent.Value = value; }
    }
    public static bool HostHearVentPlayers
    {
        get => _hostHearVentPlayers?.Value ?? true;
        set { if (_hostHearVentPlayers != null) _hostHearVentPlayers.Value = value; }
    }
    public static bool HostVentPrivateChat
    {
        get => _hostVentChat?.Value ?? false;
        set { if (_hostVentChat != null) _hostVentChat.Value = value; }
    }
    public static bool HostCommsSabDisables
    {
        get => _hostCommSab?.Value ?? true;
        set { if (_hostCommSab != null) _hostCommSab.Value = value; }
    }
    public static bool HostCameraCanHear
    {
        get => _hostCamera?.Value ?? true;
        set { if (_hostCamera != null) _hostCamera.Value = value; }
    }
    public static bool HostImpostorPrivateRadio
    {
        get => _hostImpRadio?.Value ?? false;
        set { if (_hostImpRadio != null) _hostImpRadio.Value = value; }
    }
    public static bool HostOnlyMeetingOrLobby
    {
        get => _hostMeetingOnly?.Value ?? false;
        set { if (_hostMeetingOnly != null) _hostMeetingOnly.Value = value; }
    }

    // ── Public lobby ───────────────────────────────────────
    public static bool PublicLobbyEnabled
    {
        get => _publicLobby?.Value ?? false;
        set { if (_publicLobby != null) _publicLobby.Value = value; }
    }
    public static string PublicLobbyTitle
    {
        get => _publicTitle?.Value ?? "";
        set { if (_publicTitle != null) _publicTitle.Value = value; }
    }
    public static string PublicLobbyLanguage
    {
        get => _publicLang?.Value ?? "en";
        set { if (_publicLang != null) _publicLang.Value = value; }
    }

    // ── Per-player volume (0%-200%, remembered by player name) ─
    // In-memory cache is the source of truth during play; mirrored to a
    // single serialized config entry so it survives between sessions.
    // All access is guarded by PlayerStateLock: the WebSocket receive
    // thread reads these (profile updates) while the UI thread writes.
    public static readonly Dictionary<string, float> PlayerVolumes = new();
    private static readonly object PlayerStateLock = new();
    private static bool _volumesDirty;

    public static float GetPlayerVolume(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return 1f;
        lock (PlayerStateLock)
            return PlayerVolumes.TryGetValue(playerName, out var v) ? v : 1f;
    }

    public static void SetPlayerVolume(string playerName, float volume)
    {
        if (string.IsNullOrEmpty(playerName)) return;
        if (!float.IsFinite(volume)) volume = 1f;
        lock (PlayerStateLock)
        {
            PlayerVolumes[playerName] = Math.Clamp(volume, 0f, 2f);
            _volumesDirty = true;
        }
        // The config file itself is written by FlushPending() (at most once
        // per frame) — a slider drag used to trigger a full config-file
        // rewrite on every tick.
    }

    /// <summary>
    /// Writes pending per-player volume changes to the config file.
    /// Called once per frame from VCManager.Update.
    /// </summary>
    public static void FlushPending()
    {
        bool vol;
        lock (PlayerStateLock)
        {
            vol = _volumesDirty;
            _volumesDirty = false;
        }
        if (vol) SavePlayerVolumes();
    }

    private static void SavePlayerVolumes()
    {
        if (_savedPlayerVolumes == null) return;
        var sb = new System.Text.StringBuilder();
        lock (PlayerStateLock)
        {
            foreach (var kv in PlayerVolumes)
            {
                if (Math.Abs(kv.Value - 1f) < 0.005f) continue; // skip defaults, keep the entry small
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key.Replace(';', '_').Replace('=', '_'));
                sb.Append('=');
                sb.Append(kv.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        _savedPlayerVolumes.Value = sb.ToString();
    }

    private static void LoadPlayerVolumes()
    {
        lock (PlayerStateLock)
        {
            PlayerVolumes.Clear();
            var raw = _savedPlayerVolumes?.Value ?? "";
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var part in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int idx = part.LastIndexOf('=');
                if (idx <= 0 || idx == part.Length - 1) continue;
                var name = part[..idx];
                var valStr = part[(idx + 1)..];
                if (float.TryParse(valStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v)
                    && float.IsFinite(v))
                    PlayerVolumes[name] = Math.Clamp(v, 0f, 2f);
            }
        }
    }

    // ── Auto volume (one global switch, on the player-volume page) ────
    // When on, VCPlayer auto-levels every client's output volume every
    // frame instead of using the fixed PlayerVolumes value, and the panel
    // shows the sliders as read-only. The legacy per-player set below is
    // read once at startup purely to seed this switch's default.
    public static bool AutoVolume
    {
        get => _autoVolumeEnabled?.Value ?? false;
        set { if (_autoVolumeEnabled != null) _autoVolumeEnabled.Value = value; }
    }

    private static readonly HashSet<string> PlayerAutoVolume = new();

    private static void LoadPlayerAutoVolume()
    {
        lock (PlayerStateLock)
        {
            PlayerAutoVolume.Clear();
            var raw = _savedPlayerAutoVolume?.Value ?? "";
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var part in raw.Split(';'))
                if (!string.IsNullOrEmpty(part)) PlayerAutoVolume.Add(part);
        }
    }

    // ── Keybinds ───────────────────────────────────────────
    // Persisted as a chord string: a bare KeyCode ("F1") or modifiers joined with '+'
    // ("LeftControl+M"). Legacy single-key values parse unchanged, and every hotkey
    // reader polls the whole chord through ChordDown — so a binding only fires when all
    // of the modifiers it was captured with are held, and a cleared binding never fires.
    /// <summary>Opens the voice settings panel.</summary>
    public static KeyCode SettingsKey
    {
        get => ReadKey(_settingsKey, KeyCode.F11);
        set => WriteKey(_settingsKey, value);
    }
    /// <summary>Opens the public lobby window.</summary>
    public static KeyCode PublicLobbyKey
    {
        get => ReadKey(_publicLobbyKey, KeyCode.F2);
        set => WriteKey(_publicLobbyKey, value);
    }
    /// <summary>Opens the per-player volume window.</summary>
    public static KeyCode PlayerVolumeKey
    {
        get => ReadKey(_playerVolumeKey, KeyCode.F3);
        set => WriteKey(_playerVolumeKey, value);
    }
    /// <summary>Cycles the microphone mute state (and the impostor radio channel).</summary>
    public static KeyCode CycleMicKey
    {
        get => ReadKey(_cycleMicKey, KeyCode.M);
        set => WriteKey(_cycleMicKey, value);
    }
    /// <summary>Mutes and unmutes everything you hear.</summary>
    public static KeyCode ToggleSpeakerKey
    {
        get => ReadKey(_toggleSpeakerKey, KeyCode.N);
        set => WriteKey(_toggleSpeakerKey, value);
    }

    /// <summary>True on the frame the binding's key goes down with every modifier of its
    /// chord held. All five hotkey readers go through here, once per frame each, so the
    /// parsed chord is cached and only re-parsed when the cfg value actually differs.</summary>
    public static bool ChordDown(VoiceChord which)
    {
        int i = (int)which;
        if ((uint)i >= (uint)_chordKey.Length) return false;

        string? raw = EntryFor(which)?.Value;
        if (!_chordKnown[i] || !string.Equals(_chordRaw[i], raw, StringComparison.Ordinal))
        {
            ParseChord(raw, FallbackFor(which), out var k, out var m);
            _chordRaw[i] = raw;
            _chordKey[i] = k;
            _chordMods[i] = m;
            _chordKnown[i] = true;
        }

        if (_chordKey[i] == KeyCode.None) return false;
        return ModsHeld(_chordMods[i]) && UnityEngine.Input.GetKeyDown(_chordKey[i]);
    }

    /// <summary>Stored chord exactly as written in cfg, e.g. <c>LeftControl+M</c>. An
    /// entry that was never written reports the shipped default instead of nothing.</summary>
    public static string GetChord(VoiceChord which)
    {
        var raw = EntryFor(which)?.Value;
        return string.IsNullOrWhiteSpace(raw) ? FallbackFor(which).ToString() : raw;
    }

    /// <summary>Writes a chord (modifiers first, then the key). The cache invalidates
    /// itself on the next read by comparing the raw cfg value.</summary>
    public static void SetChord(VoiceChord which, string chord)
    {
        var e = EntryFor(which);
        if (e == null) return;
        e.Value = string.IsNullOrWhiteSpace(chord) ? KeyCode.None.ToString() : chord.Trim();
    }

    /// <summary>Unbinds an action: no key, no modifiers, no accidental firing.</summary>
    public static void ClearChord(VoiceChord which) => SetChord(which, KeyCode.None.ToString());

    private static ConfigEntry<string>? EntryFor(VoiceChord which) => which switch
    {
        VoiceChord.SettingsPanel => _settingsKey,
        VoiceChord.PublicLobby => _publicLobbyKey,
        VoiceChord.PlayerVolume => _playerVolumeKey,
        VoiceChord.CycleMic => _cycleMicKey,
        VoiceChord.ToggleSpeaker => _toggleSpeakerKey,
        _ => null,
    };

    private static KeyCode FallbackFor(VoiceChord which) => which switch
    {
        VoiceChord.SettingsPanel => KeyCode.F11,
        VoiceChord.PublicLobby => KeyCode.F2,
        VoiceChord.PlayerVolume => KeyCode.F3,
        VoiceChord.CycleMic => KeyCode.M,
        VoiceChord.ToggleSpeaker => KeyCode.N,
        _ => KeyCode.None,
    };

    /// <summary>Splits <c>LeftControl+M</c> into the key that must be pressed and the
    /// modifier classes that must be held. Junk tokens from a hand-edited cfg are
    /// ignored; an entry that holds nothing parseable falls back to the default; and a
    /// lone modifier ("LeftControl") counts as the key itself rather than a requirement.</summary>
    private static void ParseChord(string? raw, KeyCode fallback, out KeyCode key, out VoiceChordMods mods)
    {
        key = fallback;
        mods = VoiceChordMods.None;
        if (string.IsNullOrWhiteSpace(raw)) return;

        KeyCode lastPlain = KeyCode.None, lastAny = KeyCode.None;
        bool sawPlain = false, sawAny = false;
        var seen = VoiceChordMods.None;

        foreach (var part in raw.Split('+'))
        {
            var tok = part.Trim();
            if (tok.Length == 0) continue;
            if (!Enum.TryParse(tok, true, out KeyCode kc)) continue;
            lastAny = kc;
            sawAny = true;
            var m = ModOf(kc);
            if (m == VoiceChordMods.None) { lastPlain = kc; sawPlain = true; }
            else seen |= m;
        }

        mods = seen;
        if (sawPlain) { key = lastPlain; mods &= ~ModOf(lastPlain); }
        else if (sawAny) { key = lastAny; mods &= ~ModOf(lastAny); }
    }

    /// <summary>The modifier class a KeyCode belongs to (Ctrl/Shift/Alt/Cmd), or None.
    /// Public because the rebind row folds held modifiers into the chord it writes, and
    /// needs this to keep a modifier-bound-as-key from also demanding itself as a hold.
    /// </summary>
    public static VoiceChordMods ModOf(KeyCode k)
    {
        if (k == KeyCode.LeftControl || k == KeyCode.RightControl) return VoiceChordMods.Ctrl;
        if (k == KeyCode.LeftShift || k == KeyCode.RightShift) return VoiceChordMods.Shift;
        if (k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.AltGr) return VoiceChordMods.Alt;
        if (k == KeyCode.LeftCommand || k == KeyCode.RightCommand
            || k == KeyCode.LeftWindows || k == KeyCode.RightWindows) return VoiceChordMods.Cmd;
        return VoiceChordMods.None;
    }

    /// <summary>Modifier classes held on this very frame — the rebind row captures these
    /// into the chord it persists, so pressing Ctrl+M writes <c>LeftControl+M</c> and the
    /// binding only ever fires with Ctrl down. Left/right of the same class count once.</summary>
    public static VoiceChordMods HeldMods()
    {
        var mods = VoiceChordMods.None;
        if (UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl))
            mods |= VoiceChordMods.Ctrl;
        if (UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift))
            mods |= VoiceChordMods.Shift;
        if (UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt)
            || UnityEngine.Input.GetKey(KeyCode.AltGr))
            mods |= VoiceChordMods.Alt;
        if (UnityEngine.Input.GetKey(KeyCode.LeftCommand) || UnityEngine.Input.GetKey(KeyCode.RightCommand)
            || UnityEngine.Input.GetKey(KeyCode.LeftWindows) || UnityEngine.Input.GetKey(KeyCode.RightWindows))
            mods |= VoiceChordMods.Cmd;
        return mods;
    }

    private static bool ModsHeld(VoiceChordMods mods)
    {
        if (mods == VoiceChordMods.None) return true;
        if ((mods & VoiceChordMods.Ctrl) != 0
            && !(UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl)))
            return false;
        if ((mods & VoiceChordMods.Shift) != 0
            && !(UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)))
            return false;
        if ((mods & VoiceChordMods.Alt) != 0
            && !(UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt)
                 || UnityEngine.Input.GetKey(KeyCode.AltGr)))
            return false;
        if ((mods & VoiceChordMods.Cmd) != 0
            && !(UnityEngine.Input.GetKey(KeyCode.LeftCommand) || UnityEngine.Input.GetKey(KeyCode.RightCommand)
                 || UnityEngine.Input.GetKey(KeyCode.LeftWindows) || UnityEngine.Input.GetKey(KeyCode.RightWindows)))
            return false;
        return true;
    }

    /// <summary>Resolves a stored chord to its main key for the legacy KeyCode-shaped
    /// properties; anything unparseable (or not written yet) falls back to the shipped
    /// default so a hand-edited cfg can never leave a window without a key to open it.</summary>
    private static KeyCode ReadKey(ConfigEntry<string>? entry, KeyCode fallback)
    {
        ParseChord(entry?.Value, fallback, out var key, out _);
        return key;
    }

    /// <summary>Legacy single-key write: keeps the key and drops any modifiers. The
    /// rebind row writes full chords through <see cref="SetChord"/> instead.</summary>
    private static void WriteKey(ConfigEntry<string>? entry, KeyCode key)
    {
        if (entry != null) entry.Value = key.ToString();
    }

    // Parsed chord cache, indexed by VoiceChord (raw-value compare invalidates it).
    private static readonly string?[] _chordRaw = new string?[5];
    private static readonly KeyCode[] _chordKey = new KeyCode[5];
    private static readonly VoiceChordMods[] _chordMods = new VoiceChordMods[5];
    private static readonly bool[] _chordKnown = new bool[5];

    // ── Device caches ──────────────────────────────────────
    public static List<string> MicrophoneDevices { get; } = new();
    public static List<string> SpeakerDevices { get; } = new();
    public static bool DeviceSelectionSupported =>
        Application.platform != RuntimePlatform.Android;

    private static ConfigEntry<string>? _mic, _speaker;
    private static ConfigEntry<float>? _masterVol, _micVol;
    private static ConfigEntry<float>? _hotkeyBtnScale;
    private static ConfigEntry<bool>? _noiseSuppression, _echoCancellation;
    private static ConfigEntry<bool>? _vadEnabled;
    private static ConfigEntry<bool>? _autoVolumeEnabled;
    private static ConfigEntry<float>? _hostMaxDist;
    private static ConfigEntry<bool>? _hostWallsBlock, _hostOnlyHearInSight, _hostImpGhost;
    private static ConfigEntry<bool>? _hostOnlyGhost, _hostHearVent, _hostHearVentPlayers, _hostVentChat;
    private static ConfigEntry<bool>? _hostCommSab, _hostCamera, _hostImpRadio, _hostMeetingOnly;
    private static ConfigEntry<bool>? _publicLobby;
    private static ConfigEntry<string>? _publicTitle, _publicLang;
    private static ConfigEntry<string>? _savedPlayerVolumes;
    private static ConfigEntry<string>? _savedPlayerAutoVolume;
    private static ConfigEntry<string>? _p2pStun;
    // NAT fix / TURN selection — deliberately no-secret: TURN credentials arrive
    // per connect with the server's clientPeerConfig (Network.IceConfig), never
    // from this file; the override triple below defaults to empty.
    private static ConfigEntry<bool>? _natFix;
    private static ConfigEntry<string>? _turnRegion;
    private static ConfigEntry<string>? _turnOverrideUrl;
    private static ConfigEntry<string>? _turnOverrideUser;
    private static ConfigEntry<string>? _turnOverridePass;
    private static ConfigEntry<string>? _settingsKey, _publicLobbyKey, _playerVolumeKey;
    /// <summary>One-shot marker: the SettingsPanel default moved F1 -> F11 once, so a cfg
    /// written before that move is migrated exactly once and never touched again.</summary>
    private static ConfigEntry<bool>? _settingsKeyMigrated;
    private static ConfigEntry<string>? _cycleMicKey, _toggleSpeakerKey;

    private static bool _devicesCached;

    public static void RefreshDeviceCaches(bool force = false)
    {
        if (!DeviceSelectionSupported) return;
        if (_devicesCached && !force) return;

        MicrophoneDevices.Clear();
        MicrophoneDevices.Add("");
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
            {
                var c = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                if (!string.IsNullOrWhiteSpace(c.ProductName))
                    MicrophoneDevices.Add(c.ProductName);
            }
        }
        catch { }

        SpeakerDevices.Clear();
        SpeakerDevices.Add("");
        try
        {
            using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active))
                if (!string.IsNullOrWhiteSpace(d.FriendlyName))
                    SpeakerDevices.Add(d.FriendlyName);
        }
        catch { }

        _devicesCached = true;
    }

    public static void Init(ConfigFile cfg)
    {
        // Server selection is gone: BCL mode dials the one forced URL — see
        // GetActiveServerURL() / ForcedServerUrl. Nothing here to bind.

        _mic = cfg.Bind("VoiceChat", "MicrophoneDevice", "", "Microphone device name.");
        _speaker = cfg.Bind("VoiceChat", "SpeakerDevice", "", "Speaker device name.");
        _masterVol = cfg.Bind("VoiceChat", "MasterVolume", 1f,
            new ConfigDescription("Master output volume", new AcceptableValueRange<float>(0.1f, 3f)));
        _micVol = cfg.Bind("VoiceChat", "MicVolume", 1f,
            new ConfigDescription("Mic input volume", new AcceptableValueRange<float>(0.1f, 3f)));

        _hotkeyBtnScale = cfg.Bind("VoiceChat", "HotkeyButtonScale", 1f,
            new ConfigDescription("Bottom-left mic/speaker button size multiplier",
                new AcceptableValueRange<float>(0.5f, 2f)));

        _noiseSuppression = cfg.Bind("VoiceChat", "NoiseSuppression", true);
        _echoCancellation = cfg.Bind("VoiceChat", "EchoCancellation", true);
        _vadEnabled = cfg.Bind("VoiceChat", "VADEnabled", true);

        _hostMaxDist = cfg.Bind("VoiceChat.Room", "MaxChatDistance", 6f,
            new ConfigDescription("Max hearing distance", new AcceptableValueRange<float>(1.5f, 20f)));
        _hostWallsBlock = cfg.Bind("VoiceChat.Room", "WallsBlockSound", true);
        _hostOnlyHearInSight = cfg.Bind("VoiceChat.Room", "OnlyHearInSight", false);
        _hostImpGhost = cfg.Bind("VoiceChat.Room", "ImpostorHearGhosts", false);
        _hostOnlyGhost = cfg.Bind("VoiceChat.Room", "OnlyGhostsCanTalk", false);
        _hostHearVent = cfg.Bind("VoiceChat.Room", "HearInVent", true);
        _hostHearVentPlayers = cfg.Bind("VoiceChat.Room", "HearVentPlayers", true);
        _hostVentChat = cfg.Bind("VoiceChat.Room", "VentPrivateChat", false);
        _hostCommSab = cfg.Bind("VoiceChat.Room", "CommsSabDisables", true);
        _hostCamera = cfg.Bind("VoiceChat.Room", "CameraCanHear", true);
        _hostImpRadio = cfg.Bind("VoiceChat.Room", "ImpostorPrivateRadio", false);
        _hostMeetingOnly = cfg.Bind("VoiceChat.Room", "OnlyMeetingOrLobby", false);

        _publicLobby = cfg.Bind("VoiceChat.Room", "PublicLobby", false);
        _publicTitle = cfg.Bind("VoiceChat.Room", "PublicTitle", "Among Us Lobby");
        _publicLang = cfg.Bind("VoiceChat.Room", "PublicLanguage", "en");

        _savedPlayerAutoVolume = cfg.Bind("VoiceChat", "PlayerAutoVolume", "",
            "Legacy: player names that had auto-volume before it became a single global switch.");
        LoadPlayerAutoVolume();
        // Seed the global switch once from the legacy per-player set (any player was
        // auto → global on); once the key exists in the file this default is ignored.
        bool legacyAuto;
        lock (PlayerStateLock) legacyAuto = PlayerAutoVolume.Count > 0;
        _autoVolumeEnabled = cfg.Bind("VoiceChat", "AutoVolume", legacyAuto,
            "Auto-adjust every player's volume toward the target speech level.");
        _savedPlayerVolumes = cfg.Bind("VoiceChat", "PlayerVolumes", "",
            "Per-player volume overrides (0%-200%), remembered by player name. Internal serialized format.");
        LoadPlayerVolumes();

        _p2pStun = cfg.Bind("VoiceChat.P2P", "StunServers", DefaultStunServers,
            "Fallback STUN servers for hole punching (stun:host:port) — used only when the " +
            "server's clientPeerConfig sends none. Append your own here.");

        _natFix = cfg.Bind("VoiceChat.P2P", "NatFix", false,
            "NAT fix (BCL natFix): skip hole punching and relay every voice through TURN " +
            "from the start. Off by default — the automatic TURN fallback rescues failed " +
            "punches without pinning every conversation to a relay. The server's " +
            "forceRelayOnly forces this on for all players regardless of this setting.");
        _turnRegion = cfg.Bind("VoiceChat.P2P", "TurnRegion", "",
            "TURN region (empty = the settings stepper's Auto option: lowest latency). " +
            "Matched against the region tags in the server's config.");
        _turnOverrideUrl = cfg.Bind("VoiceChat.P2P", "TurnServerUrl", "",
            "Optional local TURN override (turn:host:port). Leave empty — the normal case — " +
            "to use the server-provided list; a non-empty value pins TURN to this server and " +
            "ignores region/latency. Credentials for server TURNs never live in this file.");
        _turnOverrideUser = cfg.Bind("VoiceChat.P2P", "TurnUsername", "",
            "Username for TurnServerUrl (empty unless you pinned your own TURN).");
        _turnOverridePass = cfg.Bind("VoiceChat.P2P", "TurnCredential", "",
            "Password for TurnServerUrl (empty unless you pinned your own TURN).");

        _settingsKey = cfg.Bind("VoiceChat.Keybinds", "SettingsPanel", KeyCode.F11.ToString(),
            "Key that opens the voice settings panel (and closes it again).");
        _publicLobbyKey = cfg.Bind("VoiceChat.Keybinds", "PublicLobby", KeyCode.F2.ToString(),
            "Key that jumps to the public lobby list inside the settings panel.");
        _playerVolumeKey = cfg.Bind("VoiceChat.Keybinds", "PlayerVolume", KeyCode.F3.ToString(),
            "Key that jumps to the per-player volume list inside the settings panel.");
        _cycleMicKey = cfg.Bind("VoiceChat.Keybinds", "CycleMic", KeyCode.M.ToString(),
            "Key that cycles the microphone mute state (and the impostor radio channel).");
        _toggleSpeakerKey = cfg.Bind("VoiceChat.Keybinds", "ToggleSpeaker", KeyCode.N.ToString(),
            "Key that mutes and unmutes everything you hear.");

        // The shipped default for the panel hotkey moved F1 -> F11. BepInEx wrote the old
        // default into the cfg the first time it was bound, so an untouched file still
        // holds "F1" and would keep opening the panel on F1 forever — migrate that one
        // value exactly once. A chord bound to anything else (including a deliberate,
        // hand-edited F1) is left alone from the second run onward.
        _settingsKeyMigrated = cfg.Bind("VoiceChat.Keybinds", "PanelHotkeyDefaultMigrated", false,
            "Internal: the SettingsPanel hotkey default has been migrated from F1 to F11.");
        if (!_settingsKeyMigrated.Value)
        {
            if (string.Equals(_settingsKey.Value, KeyCode.F1.ToString(), StringComparison.OrdinalIgnoreCase))
                _settingsKey.Value = KeyCode.F11.ToString();
            _settingsKeyMigrated.Value = true;
        }

        ApplyLocalHostSettingsToSynced();
    }

    public static void SetMicrophoneDevice(string v) { if (_mic != null) _mic.Value = v; }
    public static void SetSpeakerDevice(string v) { if (_speaker != null) _speaker.Value = v; }
    public static void SetMasterVolume(float v) => MasterVolume = v;
    public static void SetMicVolume(float v) => MicVolume = v;

    public static void SetHostMaxChatDistance(float v) => HostMaxChatDistance = v;
    public static void SetHostWallsBlockSound(bool v) => HostWallsBlockSound = v;
    public static void SetHostOnlyHearInSight(bool v) => HostOnlyHearInSight = v;
    public static void SetHostImpostorHearGhosts(bool v) => HostImpostorHearGhosts = v;
    public static void SetHostOnlyGhostsCanTalk(bool v) => HostOnlyGhostsCanTalk = v;
    public static void SetHostHearInVent(bool v) => HostHearInVent = v;
    public static void SetHostHearVentPlayers(bool v) => HostHearVentPlayers = v;
    public static void SetHostVentPrivateChat(bool v) => HostVentPrivateChat = v;
    public static void SetHostCommsSabDisables(bool v) => HostCommsSabDisables = v;
    public static void SetHostCameraCanHear(bool v) => HostCameraCanHear = v;
    public static void SetHostImpostorPrivateRadio(bool v) => HostImpostorPrivateRadio = v;
    public static void SetHostOnlyMeetingOrLobby(bool v) => HostOnlyMeetingOrLobby = v;

    public static void ApplyLocalHostSettingsToSynced()
    {
        var s = SyncedRoomSettings;
        s.MaxChatDistance = HostMaxChatDistance;
        s.WallsBlockSound = HostWallsBlockSound;
        s.OnlyHearInSight = HostOnlyHearInSight;
        s.ImpostorHearGhosts = HostImpostorHearGhosts;
        s.OnlyGhostsCanTalk = HostOnlyGhostsCanTalk;
        s.HearInVent = HostHearInVent;
        s.HearVentPlayers = HostHearVentPlayers;
        s.VentPrivateChat = HostVentPrivateChat;
        s.CommsSabDisables = HostCommsSabDisables;
        s.CameraCanHear = HostCameraCanHear;
        s.ImpostorPrivateRadio = HostImpostorPrivateRadio;
        s.OnlyMeetingOrLobby = HostOnlyMeetingOrLobby;
    }
}

// The old BCL server list (built-in defaults + remote server.json + Custom…)
// is gone: BCL mode dials VoiceConfig.ForcedServerUrl and nothing else, so
// there is nothing left to enumerate.
