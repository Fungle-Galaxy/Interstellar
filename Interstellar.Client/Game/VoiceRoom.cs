using System.Diagnostics.CodeAnalysis;
using Interstellar.Routing;
using Interstellar.Routing.Router;
using Interstellar.Voice;
using UnityEngine;
using Interstellar.Android;

namespace Interstellar.Voice;

public class VoiceRoom
{
    public static VoiceRoom? Current { get; private set; }

    private readonly VCRoom _interstellar;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter _imager;
    private readonly VolumeRouter _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    private readonly Dictionary<int, VCPlayer> _clients = new();
    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    private readonly List<VCPlayer> _clientsSnapshotBuf = new();
    private readonly List<SpeakerCache> _speakerCacheBuf = new();

    private readonly List<IVoiceComponent> _virtualMics = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c) => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c) => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c) => _virtualSpeakers.Remove(c);

    public bool UsingMicrophone => _interstellar.Microphone != null;
    public float LocalMicLevel => _interstellar.LocalLevel;
    public bool IsClientImpostorRadio(int clientId) => _interstellar.IsClientImpostorRadio(clientId);
    public bool Mute => _interstellar.Mute;
    public bool HasSpeaker => _interstellar.Speaker != null;
    public int SampleRate => _interstellar.SampleRate;

    private LevelMeterRouter.Property? _localMicMeter;

    private int _androidNoRxFrames;
    private static bool _androidDidFullRestart;
    private bool _androidDidResync;
    private bool _commsSabActive;
    private float _commsSabCheckTimer;
    private float _diagTimer = 30f;

    private AndroidMicrophone? _androidMic;
    private AndroidSpeaker? _androidSpeaker;
    private static readonly bool IsAndroid = Application.platform == RuntimePlatform.Android;
    public static VoiceRoom Start(string region, string roomCode)
    {
        // A menu mic test holds its own capture handle; release it before the room
        // opens the same device, so only one capture is ever live.
        VoiceDeviceTest.StopMicTest();
        Current?.Close();
        Current = new VoiceRoom(region, roomCode);
        // Every path that reaches here (InterstellarRoomDriver, the settings
        // panel's Refresh button, the Android join watchdog) must keep the user's
        // mic/speaker choice — otherwise refreshing the room silently unmutes the mic.
        VoiceButtons.ApplyMicState();
        VoiceButtons.ApplySpeakerState();
        return Current;
    }

    public static void RestartForCurrentGame()
    {
        if (AmongUsClient.Instance == null) return;
        if (AmongUsClient.Instance.networkAddress is "127.0.0.1" or "localhost") return;
        Start(AmongUsClient.Instance.networkAddress, AmongUsClient.Instance.GameId.ToString());
    }

    public static void CloseCurrentRoom()
    {
        Current?.Close();
        Current = null;
        _androidDidFullRestart = false;
    }

    private VoiceRoom(string region, string roomCode)
    {
        SimpleRouter source = new();
        SimpleEndpoint endpoint = new();

        _imager = new StereoRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume = new VolumeRouter();
        _radioVolume = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter = new LevelMeterRouter();

        FilterRouter ghostLowpass = FilterRouter.CreateLowPassFilter(1900f, 2f);
        FilterRouter radioHighpass = FilterRouter.CreateHighPassFilter(650f, 3.2f);
        FilterRouter radioLowpass = FilterRouter.CreateLowPassFilter(800f, 2.1f);
        DistortionFilter radioDistort = new() { IsGlobalRouter = true, DefaultThreshold = 0.55f };
        VolumeRouter masterRouter = new() { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_levelMeter);
        _levelMeter.Connect(_normalVolume);
        _normalVolume.Connect(masterRouter);
        _imager.Connect(ghostLowpass);
        ghostLowpass.Connect(_ghostVolume);
        _ghostVolume.Connect(masterRouter);
        _clientVolume.Connect(radioHighpass);
        radioHighpass.Connect(radioLowpass);
        radioLowpass.Connect(_radioVolume);
        _radioVolume.Connect(radioDistort);
        radioDistort.Connect(masterRouter);
        masterRouter.Connect(endpoint);

        string server = VoiceConfig.GetActiveServerURL();

        _interstellar = new VCRoom(source, roomCode, region, server,
            new VCRoomParameters
            {
                OnConnectClient = (clientId, instance, isLocal) =>
                {
                    if (isLocal)
                    {
                        _clientVolume.GetProperty(instance).Volume = 1f;
                        _normalVolume.GetProperty(instance).Volume = 0f;
                        _localMicMeter = _levelMeter.GetProperty(instance);
                    }
                    else
                    {
                        _clients[clientId] = new VCPlayer(this, instance,
                            _imager, _normalVolume, _ghostVolume, _radioVolume, _clientVolume, _levelMeter);
                    }
                },
                OnUpdateProfile = (clientId, playerId, playerName) =>
                {
                    if (_clients.TryGetValue(clientId, out var p))
                    {
                        p.UpdateProfile(playerId, playerName);
                        p.SetVolume(VoiceConfig.GetPlayerVolume(playerName));
                    }
                },
                OnDisconnect = clientId =>
                {
                    _clients.Remove(clientId);
                },
            // Jitter buffer cushion (steady-state playout delay) + hard capacity,
            // both in samples @48kHz:
            //   Android: 160ms cushion / 640ms capacity.
            //   Desktop: 120ms cushion / 400ms capacity.
            // bufferLength is the target the playback holds at; bufferLength*2 is
            // where incoming bursts get trimmed back down to it (gradually).
            // Sized from the diag logs: the link delivers in bursts (10→40→7
            // frames/s against a steady 25/s send), so the old 120ms cushion
            // starved once per second and the 480ms capacity was actually
            // reached — overflow there drops *new* speech.
            }.SetBufferLength(IsAndroid ? 7680 : 5760, IsAndroid ? 23040 : 13440));

        _masterVolumeProperty = masterRouter.GetProperty(_interstellar);
        SetMasterVolume(VoiceConfig.MasterVolume);
        _interstellar.SetLoopBack(false);

        if (IsAndroid)
        {
            SetupAndroidMicrophone();
            SetupAndroidSpeaker();
            _androidMic?.Warmup();
            _androidSpeaker?.Warmup();
        }
        else
        {
            SetMicrophone(VoiceConfig.MicrophoneDevice);
            SetSpeaker(VoiceConfig.SpeakerDevice);
        }
    }

    public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;
    public void SetMicVolume(float v) => _interstellar.Microphone?.SetVolume(v);
    public void SetLoopBack(bool lb) => _interstellar.SetLoopBack(lb);
    public void SetMute(bool mute, bool isImpostorRadio = false) => _interstellar.SetMute(mute, isImpostorRadio);
    public void ToggleMute() => SetMute(!Mute);
    public void SendHostSettings(VoiceRoomSettings s) => _interstellar.SendHostSettings(s);

    public void SetMicrophone(string deviceName)
    {
        if (IsAndroid)
        {
            SetupAndroidMicrophone();
            return;
        }

        try
        {
            _interstellar.Microphone = new WindowsMicrophone(deviceName);
            _interstellar.Microphone?.SetVolume(VoiceConfig.MicVolume);
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Mic init failed: {ex.Message}");
            try { _interstellar.Microphone = null; } catch { }
        }
    }

    public void SetSpeaker(string deviceName)
    {
        if (IsAndroid)
        {
            SetupAndroidSpeaker();
            return;
        }

        try
        {
            _interstellar.Speaker = new WindowsSpeaker(deviceName);
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Speaker init failed: {ex.Message}");
            try { _interstellar.Speaker = null; } catch { }
        }
    }

    private void SetupAndroidMicrophone()
    {
        try
        {
            _androidMic?.Dispose();
            _androidMic = null;

            _androidMic = new AndroidMicrophone();
            _interstellar.Microphone = _androidMic.Microphone;
            _interstellar.Microphone?.SetVolume(VoiceConfig.MicVolume);
            InterstellarPlugin.Logger.LogInfo("[VC] Android mic (Starlight) initialised.");
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Android mic init failed: {ex.Message}");
            try { _androidMic?.Dispose(); } catch { }
            _androidMic = null;
        }
    }

    private void SetupAndroidSpeaker()
    {
        try
        {
            _androidSpeaker?.Dispose();
            _androidSpeaker = null;

            _androidSpeaker = new AndroidSpeaker();
            _androidSpeaker.Setup();
            _interstellar.Speaker = _androidSpeaker.Speaker; // Initialize BEFORE playback
            _androidSpeaker.StartPlayback(); // Start PCM callback AFTER Initialize
            InterstellarPlugin.Logger.LogInfo("[VC] Android speaker (AudioTrack) initialised.");
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Android speaker init failed: {ex.Message}");
            try { _androidSpeaker?.Dispose(); } catch { }
            _androidSpeaker = null;
        }
    }

    public void Update()
    {
        _androidMic?.Update();
        _androidSpeaker?.Update();
        _interstellar.DecayFarEnd(Time.deltaTime);

        // Role-mod meeting gag poll (main thread only — reads the quick chat button,
        // never the chat text; see BlackmailVoiceGate).
        BlackmailVoiceGate.Poll();

        TryUpdateLocalProfile();

        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = 0.5f;
            _commsSabActive = CheckCommsSabotage();
        }

        var localPlayer = PlayerControl.LocalPlayer;
        Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;
        bool localInVent = localPlayer != null && localPlayer.inVent;

        if (listenerPos.HasValue && VoiceConfig.SyncedRoomSettings.CameraCanHear)
        {
            var camPos = TryGetCameraListenerPosition();
            if (camPos.HasValue) listenerPos = camPos;
        }

        if (IsAndroid && !_androidDidFullRestart && listenerPos.HasValue && _clients.Count > 0)
        {
            bool relayAlive = false;
            foreach (var kv in _clients)
            {
                if (kv.Value.Level > 0f || _interstellar.PeerHasReceived(kv.Key))
                {
                    relayAlive = true;
                    break;
                }
            }
            if (relayAlive)
            {
                _androidDidFullRestart = true;
                _androidNoRxFrames = 0;
            }
            else
            {
                _androidNoRxFrames++;
                if (_androidNoRxFrames >= 480)
                {
                    if (!_androidDidResync)
                    {
                        _androidDidResync = true;
                        _androidNoRxFrames = 0;
                        InterstellarPlugin.Logger.LogWarning(
                            $"[VC] Android: no packet at all from {_clients.Count} peer(s) after 8s — resyncing.");
                        _interstellar.ResyncPeers("no incoming audio");
                        return;
                    }
                    InterstellarPlugin.Logger.LogWarning(
                        $"[VC] Android: still no packet from {_clients.Count} peer(s) after resync — restarting room.");
                    _androidDidFullRestart = true;
                    Close();
                    RestartForCurrentGame();
                    return;
                }
            }
        }

        List<SpeakerCache> speakerCache = _speakerCacheBuf;
        speakerCache.Clear();
        if (listenerPos.HasValue)
        {
            float maxRange = VoiceConfig.SyncedRoomSettings.MaxChatDistance;
            foreach (var v in _virtualSpeakers)
            {
                float d = Vector2.Distance(v.Position, listenerPos.Value);
                if (d < maxRange)
                    speakerCache.Add(new(v, GetVolume(d, maxRange), GetPan(listenerPos.Value.x, v.Position.x)));
            }
        }

        bool inLobby = LobbyBehaviour.Instance != null;
        bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
        bool inGame = ShipStatus.Instance != null;

        _clientsSnapshotBuf.Clear();
        try
        {
            foreach (var v in _clients.Values) _clientsSnapshotBuf.Add(v);
        }
        catch (InvalidOperationException)
        {
            _clientsSnapshotBuf.Clear();
            _clientsSnapshotBuf.AddRange(_clients.Values.ToArray());
        }

        foreach (var client in _clientsSnapshotBuf)
        {
            if (inLobby || !inGame)
                client.UpdateLobby();
            else if (inMeeting)
                client.UpdateMeeting();
            else
                client.UpdateTaskPhase(listenerPos, speakerCache, _virtualMics, localInVent, _commsSabActive);
        }

        _diagTimer -= Time.deltaTime;
        if (_diagTimer <= 0f)
        {
            _diagTimer = 30f;
            LogDiagnostics();
        }
    }

    private void LogDiagnostics()
    {
        if (_clients.Count == 0) return;
        var sb = new System.Text.StringBuilder(320);
        sb.Append("[VC:Diag] gate=").Append(_interstellar.SendGate)
          .Append(" tx=").Append(_interstellar.TxFrames)
          .Append(" dVad=").Append(_interstellar.TxDropVad)
          .Append(" dMute=").Append(_interstellar.TxDropMute)
          .Append(" txErr=").Append(_interstellar.TxEncodeErrors)
          .Append(" micLvl=").Append(LocalMicLevel.ToString("0.00"))
          .Append(" farEnd=").Append(_interstellar.FarEndLevel.ToString("0.00"));
        foreach (var kv in _clients)
        {
            var p = kv.Value;
            _interstellar.GetDiag(kv.Key, out int rx, out int buf, out int un, out bool hold, out int idle, out int tgt);
            sb.Append(" | cid=").Append(kv.Key)
              .Append(" pid=").Append(p.PlayerId)
              .Append(" rx=").Append(rx)
              .Append(" idle=").Append(idle).Append("ms")
              .Append(" buf=").Append(buf / 48).Append("ms")
              .Append("/").Append(tgt).Append("ms")
              .Append(hold ? "/hold" : "")
              .Append(" un=").Append(un)
              .Append(" lvl=").Append(p.Level.ToString("0.00"))
              .Append(" vol=").Append(p.Volume.ToString("0.00"))
              .Append(" aud=").Append(p.IsAudible ? 1 : 0)
              .Append(" map=").Append(p.IsMapped ? 1 : 0)
              .Append(" vmute=").Append(_interstellar.IsClientMuted(kv.Key) ? 1 : 0);
        }
        InterstellarPlugin.Logger.LogInfo(sb.ToString());
    }

    private static bool CheckCommsSabotage()
    {
        if (ShipStatus.Instance == null) return false;
        foreach (var sys in ShipStatus.Instance.Systems.Values)
        {
            var hud = sys.TryCast<HudOverrideSystemType>();
            if (hud != null && hud.IsActive) return true;
        }
        return false;
    }

    private static Vector2? TryGetCameraListenerPosition()
    {
        var mg = Minigame.Instance;
        if (mg == null) return null;

        // Polus / Airship: PlanetSurveillanceMinigame.Camera (public) is the
        // render camera positioned at the currently selected surveillance camera.
        var planet = mg.TryCast<PlanetSurveillanceMinigame>();
        if (planet != null && planet.Camera != null)
            return (Vector2)planet.Camera.transform.position;

        // Skeld: the minigame instance existing means the player is viewing
        // security. The player hears anyone near a camera, so use the nearest
        // SurvCamera (ShipStatus.AllCameras is public) as the listener proxy.
        var surv = mg.TryCast<SurveillanceMinigame>();
        if (surv != null && ShipStatus.Instance != null && PlayerControl.LocalPlayer != null)
        {
            var cams = ShipStatus.Instance.AllCameras;
            if (cams != null)
            {
                var pos = (Vector2)PlayerControl.LocalPlayer.transform.position;
                SurvCamera? best = null;
                float bestD = float.MaxValue;
                foreach (var cam in cams)
                {
                    if (cam == null) continue;
                    float d = Vector2.Distance((Vector2)cam.transform.position, pos);
                    if (d < bestD) { bestD = d; best = cam; }
                }
                if (best != null) return (Vector2)best.transform.position;
            }
        }
        return null;
    }

    /// <summary>Refresh the voice connection by re-joining the room: a fresh
    /// server sid makes every peer drop our old session and session us from
    /// scratch. The in-place P2P rebuild kept the peers' old session objects —
    /// probes flowed again but media never resumed — while a re-join always
    /// recovers (the long-standing manual workaround, now automatic). Used by
    /// the NAT-fix / TURN-region toggles and the manual refresh button; Start()
    /// preserves the mic/speaker choice.</summary>
    public void RestartTransport(string reason)
    {
        InterstellarPlugin.Logger.LogInfo("[VC] Room refresh (" + reason + ")");
        RestartForCurrentGame();
    }

    public void Rejoin()
    {
        _interstellar.Rejoin();
        UpdateLocalProfile(true);
        foreach (var c in _clients.Values) c.ResetMapping();
        _commsSabActive = false;
    }

    public void Close()
    {
        _androidMic?.Dispose();
        _androidMic = null;

        _androidSpeaker?.Dispose();
        _androidSpeaker = null;

        _interstellar.Disconnect();
    }

    public bool TryGetPlayer(byte playerId, [MaybeNullWhen(false)] out VCPlayer player)
    {
        foreach (var c in _clients.Values)
            if (c.PlayerId == playerId) { player = c; return true; }
        player = null;
        return false;
    }

    private byte _lastId = byte.MaxValue;
    private string _lastName = null!;

    private void TryUpdateLocalProfile() => UpdateLocalProfile(false);

    internal void ForceUpdateLocalProfile() => UpdateLocalProfile(true);

    public async System.Threading.Tasks.Task JoinPublicLobby(int lobbyId)
    {
        await _interstellar.JoinLobby(lobbyId, (err, code, server) =>
        {
            PublicLobbyManager.OnLobbyJoinResult?.Invoke(err, code, server);
        });
    }

    public async System.Threading.Tasks.Task WatchLobbyBrowserAsync(bool watch)
    {
        await _interstellar.WatchLobbyBrowser(watch);
    }

    /// <summary>The control socket must be up before a publish is attempted: the
    /// public-lobby sync waits on this instead of consuming a change that Emit
    /// would silently drop on a socket that is still connecting.</summary>
    public bool CanPublishLobby => _interstellar.IsControlSocketOpen;

    public async System.Threading.Tasks.Task PublishLobbyAsync(string code, PublicLobbyManager.LobbyInfo info)
    {
        InterstellarPlugin.Logger?.LogInfo("[VC] Lobby publish emitted");
        await _interstellar.PublishLobby(code, info);
    }

    public async System.Threading.Tasks.Task RemoveLobbyAsync(string code)
    {
        await _interstellar.RemoveLobby(code);
    }

    private void UpdateLocalProfile(bool always)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (!always && lp.PlayerId == _lastId && lp.name == _lastName) return;

        _lastId = lp.PlayerId;
        _lastName = lp.name;
        var cid = AmongUsClient.Instance ? AmongUsClient.Instance.ClientId : 0;
        _interstellar.UpdateProfile(_lastName, _lastId, cid);
    }

    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
