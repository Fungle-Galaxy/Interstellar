using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
/// Displays a microphone icon (Speaking.png) next to the name of each player
/// who is currently speaking, and a NoConnect.png icon for players not connected
/// to the BCL voice server.
/// Logic mirrors BetterCrewLink: disconnected / novoice / connected+speaking.
/// </summary>
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class PlayerNameSpeakingIconPatch
{
    private const float SpeakingThreshold = 0.01f;
    private const string SpeakingIconName = "VC_SpeakingIcon";
    private const string NoConnectIconName = "VC_NoConnectIcon";

    private static readonly Dictionary<byte, GameObject> SpeakingIconCache = new();
    private static readonly Dictionary<byte, GameObject> NoConnectIconCache = new();

    private static Sprite? _speakingSprite;
    private static Sprite? _noConnectSprite;

    static void Postfix()
    {
        var room = VoiceRoom.Current;
        if (room == null)
        {
            ClearAllIcons();
            return;
        }

        // Load sprites
        if (_speakingSprite == null)
            _speakingSprite = InterstellarHudState.LoadSpriteFromResources(
                "Interstellar.Resources.Speaking.png", 100f);
        if (_noConnectSprite == null)
            _noConnectSprite = InterstellarHudState.LoadSpriteFromResources(
                "Interstellar.Resources.NoConnect.png", 100f);

        // Build set of voice-connected player clientIds (from BCL server)
        var connectedIds = new HashSet<int>();
        foreach (var c in room.AllClients)
            if (c.PlayerId != byte.MaxValue)
                connectedIds.Add(c.ClientId);

        // Local player is always "connected" when room exists
        if (PlayerControl.LocalPlayer != null)
            connectedIds.Add(AmongUsClient.Instance?.ClientId ?? -1);

        // Build set of speaking player clientIds
        var speakingIds = new HashSet<int>();
        foreach (var c in room.AllClients)
            if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold && c.IsAudible)
                speakingIds.Add(c.ClientId);

        // Self-speaking: always show when mic is active and not muted
        if (PlayerControl.LocalPlayer != null
            && room.LocalMicLevel > SpeakingThreshold
            && !room.Mute)
            speakingIds.Add(AmongUsClient.Instance?.ClientId ?? -1);

        // Update icons
        var processedIds = new HashSet<byte>();
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null) continue;
            byte id = pc.PlayerId;
            processedIds.Add(id);
            if (pc.cosmetics?.nameText == null) continue;

            int clientId = pc.OwnerId;

            // Local player: while our own mic is off, never show a speaking icon
            // for ourselves, no matter what. This is checked first and
            // unconditionally so a muted mic can never leave a stale icon behind.
            bool isLocalPlayer = clientId == (AmongUsClient.Instance?.ClientId ?? -2);
            if (isLocalPlayer && room.Mute)
            {
                RemoveSpeakingIcon(id);
                RemoveNoConnectIcon(id);
                continue;
            }

            // Hide icons when player name/body is hidden or the name is faded out.
            // nameText.renderer.isVisible additionally catches cases where the name
            // is being visually clipped/occluded (e.g. shadowed/out-of-vision areas)
            // without the GameObject itself being deactivated or its alpha changing.
            var nameRenderer = pc.cosmetics.nameText.GetComponent<Renderer>();
            bool nameHidden = !pc.cosmetics.nameText.gameObject.activeInHierarchy
                || (nameRenderer != null && !nameRenderer.isVisible);
            float nameAlpha = pc.cosmetics.nameText.alpha;
            float bodyAlpha = pc.cosmetics.currentBodySprite?.BodySprite?.color.a ?? 1f;
            if (nameHidden || pc.inVent || bodyAlpha < 0.01f || nameAlpha < 0.01f)
            {
                RemoveSpeakingIcon(id);
                RemoveNoConnectIcon(id);
                continue;
            }

            bool isConnected = connectedIds.Contains(clientId);
            // Bar mode (Left/Right/Top/Bottom): the speaking highlight moves to the
            // screen-edge strip (SpeakingBarHud) — the name keeps only NoConnect.
            bool isSpeaking = speakingIds.Contains(clientId)
                && VoiceConfig.SpeakDisplayPos == 0;

            if (!isConnected)
            {
                // BCL "disconnected" state → show NoConnect
                RemoveSpeakingIcon(id);
                EnsureNoConnectIcon(pc, id);
            }
            else if (isSpeaking)
            {
                // BCL "connected + audio" → show Speaking
                RemoveNoConnectIcon(id);
                EnsureSpeakingIcon(pc, id);
            }
            else
            {
                // BCL "novoice" (connected but silent) → no icons
                RemoveSpeakingIcon(id);
                RemoveNoConnectIcon(id);
            }
        }

        // Clean up icons for players who left
        CleanupCache(SpeakingIconCache, processedIds, RemoveSpeakingIcon);
        CleanupCache(NoConnectIconCache, processedIds, RemoveNoConnectIcon);
    }

    static void CleanupCache(Dictionary<byte, GameObject> cache, HashSet<byte> aliveIds, System.Action<byte> remove)
    {
        var toRemove = new List<byte>();
        foreach (var kv in cache)
            if (!aliveIds.Contains(kv.Key))
                toRemove.Add(kv.Key);
        foreach (var id in toRemove) remove(id);
    }

    // ── Speaking Icon ───────────────────────────────────────────

    static void EnsureSpeakingIcon(PlayerControl pc, byte playerId)
    {
        if (_speakingSprite == null) return;
        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        if (SpeakingIconCache.TryGetValue(playerId, out var existing))
        {
            if (existing == null || existing.transform.parent != nameParent)
            {
                if (existing != null) Object.Destroy(existing);
                SpeakingIconCache.Remove(playerId);
            }
            else { UpdateIconPosition(existing, pc); return; }
        }
        CreateIcon(pc, playerId, SpeakingIconName, _speakingSprite, SpeakingIconCache);
    }

    static void RemoveSpeakingIcon(byte playerId)
    {
        if (SpeakingIconCache.TryGetValue(playerId, out var go))
        {
            if (go != null) Object.Destroy(go);
            SpeakingIconCache.Remove(playerId);
        }
    }

    // ── NoConnect Icon ──────────────────────────────────────────

    static void EnsureNoConnectIcon(PlayerControl pc, byte playerId)
    {
        if (_noConnectSprite == null) return;
        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        if (NoConnectIconCache.TryGetValue(playerId, out var existing))
        {
            if (existing == null || existing.transform.parent != nameParent)
            {
                if (existing != null) Object.Destroy(existing);
                NoConnectIconCache.Remove(playerId);
            }
            else { UpdateIconPosition(existing, pc); return; }
        }
        CreateIcon(pc, playerId, NoConnectIconName, _noConnectSprite, NoConnectIconCache);
    }

    static void RemoveNoConnectIcon(byte playerId)
    {
        if (NoConnectIconCache.TryGetValue(playerId, out var go))
        {
            if (go != null) Object.Destroy(go);
            NoConnectIconCache.Remove(playerId);
        }
    }

    // ── Icon Factory ────────────────────────────────────────────

    static void CreateIcon(PlayerControl pc, byte playerId, string name, Sprite sprite, Dictionary<byte, GameObject> cache)
    {
        var nameText = pc.cosmetics.nameText;
        var go = new GameObject(name);
        go.transform.SetParent(nameText.transform.parent, false);
        go.layer = pc.gameObject.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        // Same sorting layer AND same order as the name itself (not +1).
        // Anything that visually covers the name (e.g. a shadow/darkness layer
        // sitting just above the name's own order) then covers the icon too,
        // instead of the icon winning sorting ties and staying visible on top.
        sr.sortingLayerID = nameText.sortingLayerID;
        sr.sortingOrder = nameText.sortingOrder;
        UpdateIconPosition(go, pc);
        cache[playerId] = go;
    }

    static void UpdateIconPosition(GameObject go, PlayerControl pc)
    {
        var nameText = pc.cosmetics.nameText;
        // Position icon to the LEFT of the name text
        float offsetX = nameText.bounds.size.x / 2f + 0.4f;
        go.transform.localPosition = new Vector3(-offsetX, 0f, -1f);
        go.transform.localScale = new Vector3(0.5f, 0.5f, 1f);

        var sr = go.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        // Keep the icon on the exact same sorting layer/order as the name,
        // so anything that occludes the name also occludes the icon.
        sr.sortingLayerID = nameText.sortingLayerID;
        sr.sortingOrder = nameText.sortingOrder;
        // Follow the name's fade (e.g. behind walls) with a slight transparency.
        sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(nameText.alpha * 0.7f));
    }

    static void ClearAllIcons()
    {
        foreach (var kv in SpeakingIconCache)
            if (kv.Value != null) Object.Destroy(kv.Value);
        SpeakingIconCache.Clear();
        foreach (var kv in NoConnectIconCache)
            if (kv.Value != null) Object.Destroy(kv.Value);
        NoConnectIconCache.Clear();
    }
}
