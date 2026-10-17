using UnityEngine;
using Interstellar;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

internal static class InterstellarRoomDriver
{
    private static bool _wasInIntro = false;
    private static bool _wasInEndGame = false;

    private static bool IsLocalServer()
    {
        var addr = AmongUsClient.Instance?.networkAddress;
        return addr is "127.0.0.1" or "localhost";
    }

    internal static void Update()
    {
        // Nebula: shouldNotUseVC = !option || IsLocalServer()
        bool shouldNotUseVC = AmongUsClient.Instance == null
            || (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined
                && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started)
            || IsLocalServer();

        if (shouldNotUseVC)
        {
            if (VoiceRoom.Current != null)
                VoiceRoom.CloseCurrentRoom();
            _wasInIntro = _wasInEndGame = false;
            VoiceServerState.Reset();
            return;
        }

        // Nebula: if (Instance == null) StartVoiceChat(region, roomId)
        if (VoiceRoom.Current == null)
        {
            string region = AmongUsClient.Instance!.networkAddress;
            string roomId = AmongUsClient.Instance.GameId.ToString();
            VoiceRoom.Start(region, roomId);
            InterstellarHudState.ApplyMicState();
            InterstellarHudState.ApplySpeakerState();

            if (AmongUsClient.Instance.AmHost)
            {
                VoiceConfig.ApplyLocalHostSettingsToSynced();
                InterstellarHudState.MarkRoomSettingsDirty();
            }

            // Force profile send after room creation to ensure server
            // receives it even on first join before TryUpdateLocalProfile fires.
            VoiceRoom.Current!.ForceUpdateLocalProfile();

            InterstellarPlugin.Logger.LogInfo($"[VC] Room started: region={region} room={roomId}");
        }

        if (VoiceRoom.Current == null) return;

        // IntroCutscene ended → Rejoin to re-sync profiles
        bool inIntro = IntroCutscene.Instance != null;
        if (_wasInIntro && !inIntro)
        {
            foreach (var c in VoiceRoom.Current.AllClients)
                c.ResetMapping();
            VoiceRoom.Current.ForceUpdateLocalProfile();
            InterstellarPlugin.Logger.LogInfo("[VC] IntroCutscene ended: mappings reset, profile re-broadcast.");
        }
        _wasInIntro = inIntro;

        // EndGame started → Rejoin
        bool inEndGame = Object.FindObjectOfType<EndGameManager>() != null;
        if (inEndGame && !_wasInEndGame)
        {
            VoiceRoom.Current.Rejoin();
            VoiceRoom.Current.ForceUpdateLocalProfile();
            InterstellarPlugin.Logger.LogInfo("[VC] EndGame: room rejoined.");
        }
        _wasInEndGame = inEndGame;

        InterstellarHudState.TrySyncHostRoomSettings();
        InterstellarHudState.TrySyncPublicLobby();

        try { VoiceRoom.Current.Update(); }
        catch (System.Exception ex)
        { InterstellarPlugin.Logger.LogError("[VC] Room update error: " + ex); }
    }
}
