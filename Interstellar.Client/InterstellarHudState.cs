using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

public static class InterstellarHudState
{
    public static bool IsSpeakerMuted => VoiceButtons.IsSpeakerMuted;
    public static bool IsImpostorRadioOnly => VoiceButtons.IsImpostorRadioOnly;

    private static VoiceRoomSettings? _lastSentSettings;
    public static void MarkRoomSettingsDirty() => _lastSentSettings = null;

    private static TextMeshProUGUI? _serverInfoText;

    private static bool _lastPublicLobbyState;
    private static int _lastPublicLobbyPlayers;
    private static string _lastPublicLobbyCode = "";

    internal static void Init()
    {
        SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)((_, __) =>
        {
            DestroyServerInfoText();
        });
    }

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;
        EnsureServerInfoText(hud);
        UpdateServerInfoText();
    }

    internal static void ApplyMicState() => VoiceButtons.ApplyMicState();
    internal static void ApplySpeakerState() => VoiceButtons.ApplySpeakerState();
    internal static void CycleMicPublic() => VoiceButtons.CycleMic();
    internal static void ToggleSpeakerPublic() => VoiceButtons.ToggleSpeaker();

    internal static void TrySyncHostRoomSettings()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined) return;

        var cur = VoiceConfig.SyncedRoomSettings;
        if (_lastSentSettings != null && cur.ContentEquals(_lastSentSettings)) return;

        VoiceRoom.Current?.SendHostSettings(cur);
        _lastSentSettings = new VoiceRoomSettings();
        _lastSentSettings.Apply(cur);
    }

    // Room whose advertisement flag was already auto-reset — one shot per room.
    private static string _publicLobbyResetCode = "";

    /// <summary>Game state last published, in BCL's wire enum (-1 = never).</summary>
    private static int _lastPublicLobbyGameState = -1;

    internal static void TrySyncPublicLobby()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        var room = VoiceRoom.Current;
        if (room == null)
        {
            // Outside a room there is no sync identity: forget it so a rejoin of
            // the SAME room re-advertises (the server forgets lobbies on disconnect).
            _lastPublicLobbyState = false;
            _lastPublicLobbyPlayers = 0;
            _lastPublicLobbyCode = "";
            _lastPublicLobbyGameState = -1;
            return;
        }

        string code = AmongUsClient.Instance.GameId.ToString();

        // A brand-new room starts unlisted: the option auto-resets to off and the
        // host opts this room in themselves — the publish then fires from their
        // toggle, long after the room's control socket is up.
        if (code != _publicLobbyResetCode)
        {
            _publicLobbyResetCode = code;
            if (VoiceConfig.PublicLobbyEnabled) VoiceConfig.PublicLobbyEnabled = false;
        }

        // BCL's wire enum (join_lobby hands out codes ONLY for 0 and the browser
        // greys every other state): 0=Lobby 1=Tasks 2=Discussion. Publish the
        // REAL state — a mid-game room that keeps claiming Lobby advertises codes
        // that cannot be used.
        int gameState;
        bool inMeeting = false;
        try { inMeeting = MeetingHud.Instance != null; } catch { }
        if (inMeeting) gameState = 2; // DISCUSSION
        else gameState = AmongUsClient.Instance.GameState
             == InnerNet.InnerNetClient.GameStates.Joined ? 0 : 1; // LOBBY : TASKS

        bool wantPublic = VoiceConfig.PublicLobbyEnabled;
        int curPlayers = 0;
        foreach (var p in PlayerControl.AllPlayerControls) if (p != null) curPlayers++;

        // The control socket carries the publish, and SendRaw drops the frame
        // silently before it is up (a freshly created room is still connecting).
        // Wait WITHOUT consuming the state change so the next frame retries.
        if (!room.CanPublishLobby) return;

        // The room code is part of the sync identity: leaving one room and creating
        // another keeps both the flag and the player count identical (a solo test
        // room is always one player), which used to skip the republish forever.
        if (wantPublic == _lastPublicLobbyState
            && curPlayers == _lastPublicLobbyPlayers
            && gameState == _lastPublicLobbyGameState
            && code == _lastPublicLobbyCode) return;
        bool wasPublic = _lastPublicLobbyState;
        _lastPublicLobbyState = wantPublic;
        _lastPublicLobbyPlayers = curPlayers;
        _lastPublicLobbyCode = code;
        _lastPublicLobbyGameState = gameState;

        if (wantPublic)
        {
            _ = room.PublishLobbyAsync(code, new PublicLobbyManager.LobbyInfo
            {
                title = VoiceConfig.PublicLobbyTitle,
                host = PlayerControl.LocalPlayer?.name ?? "Host",
                current_players = curPlayers,
                max_players = GameOptionsManager.Instance?.currentNormalGameOptions?.MaxPlayers ?? 10,
                language = VoiceConfig.PublicLobbyLanguage,
                mods = "Interstellar",
                server = VoiceConfig.GetActiveServerURL(),
                gameState = gameState,
            });
        }
        else if (wasPublic)
        {
            // Only retract what we actually advertised.
            _ = room.RemoveLobbyAsync(code);
        }
    }

    private static void DestroyServerInfoText()
    {
        if (_serverInfoText != null) { Object.Destroy(_serverInfoText.gameObject); _serverInfoText = null; }
    }

    private static void EnsureServerInfoText(HudManager hud)
    {
        if (_serverInfoText != null) return;
        var canvas = VCUiKit.EnsureCanvas();
        _serverInfoText = VCUiKit.CreateText(canvas.transform, "VC_ServerInfo", "",
            Vector2.zero, new Vector2(1400f, 36f), 18f, new Color(0.6f, 0.85f, 0.6f),
            align: TextAlignmentOptions.BottomLeft);
        Object.DontDestroyOnLoad(_serverInfoText.gameObject);
        var rt = _serverInfoText.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(30f, 20f);
    }

    private static void UpdateServerInfoText()
    {
        if (_serverInfoText == null) return;

        bool inLobby = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "OnlineGame";
        if (!inLobby) { _serverInfoText.text = ""; return; }

        var room = VoiceRoom.Current;
        if (room == null) { _serverInfoText.text = ""; return; }

        string serverUrl = VoiceServerState.HasInfo ? VoiceServerState.VoiceServerUrl : VoiceConfig.GetActiveServerURL();
        if (string.IsNullOrEmpty(serverUrl)) { _serverInfoText.text = ""; return; }

        int cur = 0;
        foreach (var _ in room.AllClients) cur++;
        int opt = VoiceServerState.OptimalPlayers;
        var ok = new Color(0.6f, 0.85f, 0.6f);
        var warn = new Color(1f, 0.65f, 0.2f);

        // From the moment a room is entered this chip narrates the connection
        // flow until the socket is up: the attempt counter while connecting, a
        // visible 3s countdown after each failure, and a parked "failed" after
        // the third one (a new room starts the cycle over).
        Interstellar.Network.VoiceConnStatus.GetState(out var phase, out var attempt, out var retryIn);
        if (phase == Interstellar.Network.VoiceConnStatus.Phase.Connecting)
        {
            _serverInfoText.text = string.Format(
                TranslationHelper.Get("vc.hud.conn.connecting", "Connecting to voice server… ({0}/{1})"),
                attempt, Interstellar.Network.VoiceConnStatus.MaxAttempts);
            _serverInfoText.color = ok;
            return;
        }
        if (phase == Interstellar.Network.VoiceConnStatus.Phase.Retrying)
        {
            _serverInfoText.text = string.Format(
                TranslationHelper.Get("vc.hud.conn.retry", "Voice server unreachable — retrying in {0}s ({1}/{2})"),
                retryIn, attempt + 1, Interstellar.Network.VoiceConnStatus.MaxAttempts);
            _serverInfoText.color = warn;
            return;
        }
        if (phase == Interstellar.Network.VoiceConnStatus.Phase.Failed)
        {
            _serverInfoText.text = TranslationHelper.Get("vc.hud.conn.failed", "Voice server connection failed");
            _serverInfoText.color = new Color(1f, 0.35f, 0.35f);
            return;
        }

        // Media path narration: ONE line at the original spot — the current
        // connect step (TURN phase, peer handshake), collapsing to the REAL
        // path summary (relay vs direct) once every peer is up, so a NAT-fix
        // or region toggle is visible the moment it takes effect.
        var p2p = Interstellar.Network.P2P.P2pTransport.Active;
        string? progress = p2p != null ? p2p.DescribeProgress() : null;
        if (progress != null)
        {
            _serverInfoText.text = opt > 0 ? progress + "  " + cur + "/" + opt : progress + "  " + cur;
            _serverInfoText.color = ok;
            return;
        }

        // Media never rides the server in BCL mode (direct UDP, TURN on failure),
        // so the transport leads — printing the server's name first would read as
        // if voice still went through it. The roster count stays (still your
        // room), and so does the capacity warning: the server still admits
        // signups and carries the signalling.
        string lead = TransportLabel();
        if (opt > 0)
        {
            _serverInfoText.text = lead + "  " + cur + "/" + opt;
            _serverInfoText.color = cur >= opt ? warn : ok;
        }
        else
        {
            _serverInfoText.text = lead + "  " + cur;
            _serverInfoText.color = ok;
        }
    }

    /// <summary>Current media path, in the panel's own words — NAT fix (or the
    /// server's forceRelayOnly) pins every peer to TURN; otherwise it is
    /// direct-first with TURN fallback.</summary>
    private static string TransportLabel()
    {
        if (VoiceConfig.NatFix || Interstellar.Network.IceConfig.ForceRelayOnly)
            return TranslationHelper.Get("vc.hud.natfix", "TURN relay (NAT fix)");
        return TranslationHelper.Get("vc.settings.transport.auto", "P2P + TURN");
    }

    private static string ResolveServerName(string url) => VoiceConfig.GetServerLocationName(url);

    static string ShortenServerUrl(string url)
    {
        var host = url.Replace("ws://", "").Replace("wss://", "").Replace("/vc", "");
        var colon = host.LastIndexOf(':');
        if (colon > 0) host = host.Substring(0, colon);
        return host;
    }

    static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = spr;
            return spr;
        }
        catch { return null!; }
    }

    public static Sprite? LoadSpriteFromResources(string path, float pixelsPerUnit)
    {
        var key = path + "@" + pixelsPerUnit;
        if (_spriteCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            if (stream == null) return null;
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[key] = spr;
            return spr;
        }
        catch { return null; }
    }
}