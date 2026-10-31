using HarmonyLib;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Interstellar.Voice;
using Interstellar.UI;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
/// Perfect-Comms style speaking strip. The "Speaking display" setting picks where
/// talking players are highlighted: the Name option keeps the classic mic icon
/// next to each player's name (PlayerNameSpeakingIconPatch), while Left / Right /
/// Top / Bottom render a screen-edge strip here — one slot per speaker, ordered by
/// player id: their composed outfit avatar (CrewmateIcon, the reference renderer
/// port — identity-coloured body plus live hat/skin/visor layers), a soft glow in
/// their colour that tracks their voice level, and their name. Top/bottom lay out
/// horizontally (Perfect-Comms itself defaults to the top), left/right vertically.
///
/// Slots fade in while a player talks and fade out when they stop; a slot whose
/// player leaves the strip keeps its place until the fade completes so the row
/// never jumps mid-dissolve. The fade is one CanvasGroup per slot so cosmetic
/// layer tints stay exact.
/// </summary>
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class SpeakingBarHud
{
    private const float Threshold = 0.01f;
    // Perfect-Comms' own fade/smoothing rates, so the strip feels familiar.
    private const float FadeInSpeed = 7f;
    private const float FadeOutSpeed = 5f;
    private const float LevelSmoothSpeed = 12f;
    private const float SlotWidth = 150f;
    private const float HorizontalPitch = 132f;
    private const float VerticalPitch = 126f;
    private const float EdgeMargin = 48f;
    private const float AvatarBox = 88f;

    private sealed class Slot
    {
        public GameObject Root = null!;
        public RectTransform Rect = null!;
        public CanvasGroup Group = null!;
        public Image Glow = null!;
        public RectTransform AvatarSlot = null!;
        public Image Fallback = null!;
        public TextMeshProUGUI Label = null!;
        public readonly List<Image> Layers = new();
        public List<CrewmateIcon.AvatarLayer>? Applied;
        public float Alpha;
        public float Smooth;
        public bool Target;
    }

    private static readonly Dictionary<byte, Slot> Slots = new();

    static void Postfix()
    {
        try { Tick(); }
        catch { }
    }

    private static void Tick()
    {
        var room = VoiceRoom.Current;
        bool inGame = room != null
            && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "OnlineGame";
        int pos = VoiceConfig.SpeakDisplayPos;
        if (pos <= 0 || pos > 4 || !inGame) { DestroyAll(); return; }

        // clientId -> voice level for every audible speaker, local included.
        var levels = new Dictionary<int, float>();
        foreach (var c in room!.AllClients)
            if (c.PlayerId != byte.MaxValue && c.Level > Threshold && c.IsAudible)
                levels[c.ClientId] = c.Level;
        if (PlayerControl.LocalPlayer != null && !room.Mute && room.LocalMicLevel > Threshold
            && AmongUsClient.Instance != null)
            levels[AmongUsClient.Instance.ClientId] = room.LocalMicLevel;

        // Match world players to those clientIds; order by playerId for a stable strip.
        var wanted = new List<(byte pid, PlayerControl pc, float level)>();
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null) continue;
            if (levels.TryGetValue(pc.OwnerId, out float lvl))
                wanted.Add((pc.PlayerId, pc, lvl));
        }
        wanted.Sort((a, b) => a.pid.CompareTo(b.pid));

        // Retarget slots: speakers fade in, everyone else fades out and dies at zero.
        var alive = new HashSet<byte>();
        foreach (var (pid, pc, lvl) in wanted)
        {
            alive.Add(pid);
            if (!Slots.TryGetValue(pid, out var slot)) { slot = CreateSlot(); Slots[pid] = slot; }
            slot.Target = true;
            UpdateSlot(slot, pc);
            slot.Smooth = Mathf.Lerp(slot.Smooth, Mathf.Clamp01(lvl * 2f),
                Mathf.Clamp01(Time.unscaledDeltaTime * LevelSmoothSpeed));
        }
        foreach (var kv in Slots)
            if (!alive.Contains(kv.Key)) kv.Value.Target = false;

        // Layout from the slot order (fading-out slots keep their place so the
        // strip does not jump while they dissolve).
        var order = new List<byte>(Slots.Keys);
        order.Sort();
        int n = order.Count;
        for (int i = 0; i < n; i++)
            Place(Slots[order[i]].Rect, i, n, pos);

        float dt = Time.unscaledDeltaTime;
        var dead = new List<byte>();
        foreach (var kv in Slots)
        {
            var s = kv.Value;
            float speed = s.Target ? FadeInSpeed : FadeOutSpeed;
            s.Alpha = Mathf.MoveTowards(s.Alpha, s.Target ? 1f : 0f, dt * speed);
            ApplyAlpha(s);
            if (!s.Target && s.Alpha <= 0.001f) dead.Add(kv.Key);
        }
        foreach (var pid in dead)
        {
            var s = Slots[pid];
            if (s.Root != null) Object.Destroy(s.Root);
            Slots.Remove(pid);
        }
    }

    private static Slot CreateSlot()
    {
        var canvas = VCUiKit.EnsureCanvas();
        var root = VCUiKit.NewRect(canvas.transform, "VC_SpeakSlot");
        root.sizeDelta = new Vector2(SlotWidth, 124f);
        var group = root.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.alpha = 0f;

        var glow = VCUiKit.CreateImage(root, "Glow", new Vector2(0f, 6f),
            new Vector2(100f, 100f), VCUiKit.CircleSprite, Color.white);
        glow.raycastTarget = false;

        var avatarSlot = VCUiKit.NewRect(root, "Avatar");
        avatarSlot.anchorMin = avatarSlot.anchorMax = new Vector2(0.5f, 0.5f);
        avatarSlot.pivot = new Vector2(0.5f, 0.5f);
        avatarSlot.anchoredPosition = new Vector2(0f, 6f);
        avatarSlot.sizeDelta = new Vector2(AvatarBox, AvatarBox);
        var fallback = avatarSlot.gameObject.AddComponent<Image>();
        fallback.enabled = false;
        fallback.raycastTarget = false;

        var label = VCUiKit.CreateText(root, "Name", "", new Vector2(0f, -54f),
            new Vector2(SlotWidth, 26f), 20f, Color.white, align: TextAlignmentOptions.Center);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        return new Slot
        {
            Root = root.gameObject, Rect = root, Group = group, Glow = glow,
            AvatarSlot = avatarSlot, Fallback = fallback, Label = label,
        };
    }

    private static void UpdateSlot(Slot slot, PlayerControl pc)
    {
        if (slot.Root == null) return;

        // Full Perfect-Comms outfit avatar: identity-coloured body plus the live
        // hat/skin/visor layers. CrewmateIcon caches per outfit, and hands back
        // the same list while nothing changed, so the reference check keeps this
        // cheap every frame.
        var layers = CrewmateIcon.AvatarLayersFor(pc);
        if (layers != null)
        {
            if (!ReferenceEquals(layers, slot.Applied))
            {
                BuildAvatarStack(slot, layers);
                slot.Applied = layers;
            }
        }
        else
        {
            // Dead / no-body answers: fall back to the plain identity body.
            if (slot.Applied != null)
            {
                ClearAvatarStack(slot);
                slot.Applied = null;
            }
            var av = CrewmateIcon.SpriteFor(pc);
            if (av != null && !ReferenceEquals(av, slot.Fallback.sprite))
            {
                slot.Fallback.enabled = true;
                slot.Fallback.sprite = av;
                slot.Fallback.preserveAspect = true;
                slot.Fallback.color = Color.white;
            }
        }
        slot.Glow.color = CrewmateIcon.ColorFor(pc);

        string name = pc.name;
        if (!string.Equals(slot.Label.text, name, StringComparison.Ordinal))
            slot.Label.text = name;
    }

    /// <summary>Layers the identity body and the cosmetics as child images inside
    /// the avatar box, fitted as a whole (a tall hat shrinks the stack instead of
    /// spilling over the name). Draw order is the list order — already sorted
    /// back-to-front by CrewmateIcon. Geometry mapping verbatim from
    /// PlayerVolumeRow.BuildAvatarStack.</summary>
    private static void BuildAvatarStack(Slot slot, List<CrewmateIcon.AvatarLayer> layers)
    {
        ClearAvatarStack(slot);
        if (layers.Count == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < layers.Count; i++)
        {
            var r = layers[i].Local;
            if (r.xMin < minX) minX = r.xMin;
            if (r.xMax > maxX) maxX = r.xMax;
            if (r.yMin < minY) minY = r.yMin;
            if (r.yMax > maxY) maxY = r.yMax;
        }
        var box = slot.AvatarSlot;
        float spanX = Mathf.Max(maxX - minX, 1e-4f);
        float spanY = Mathf.Max(maxY - minY, 1e-4f);
        float k = Mathf.Min(box.sizeDelta.x / spanX, box.sizeDelta.y / spanY);
        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;

        slot.Fallback.enabled = false;
        for (int i = 0; i < layers.Count; i++)
        {
            var L = layers[i];
            var rt = VCUiKit.NewRect(box, "L" + i);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(L.Local.width * k, L.Local.height * k);
            // RectTransforms are y-up like player-local space: map straight
            // across, no negation. Mirroring flips about the layer's own pivot.
            rt.anchoredPosition = new Vector2(
                (L.Local.x + L.Local.width * 0.5f - cx) * k,
                (L.Local.y + L.Local.height * 0.5f - cy) * k);
            rt.localScale = new Vector3(L.FlipX ? -1f : 1f, L.FlipY ? -1f : 1f, 1f);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = L.Sprite;
            img.color = L.Tint;
            img.raycastTarget = false;
            slot.Layers.Add(img);
        }
    }

    private static void ClearAvatarStack(Slot slot)
    {
        for (int i = 0; i < slot.Layers.Count; i++)
        {
            var img = slot.Layers[i];
            if (img != null) Object.Destroy(img.gameObject);
        }
        slot.Layers.Clear();
        if (slot.Fallback != null) slot.Fallback.enabled = true;
    }

    private static void Place(RectTransform root, int index, int count, int pos)
    {
        float mid = (count - 1) * 0.5f;
        switch (pos)
        {
            case 1: // Left — vertical column hugging the left edge
                root.anchorMin = root.anchorMax = new Vector2(0f, 0.5f);
                root.pivot = new Vector2(0f, 0.5f);
                root.anchoredPosition = new Vector2(EdgeMargin, (mid - index) * VerticalPitch);
                break;
            case 2: // Right
                root.anchorMin = root.anchorMax = new Vector2(1f, 0.5f);
                root.pivot = new Vector2(1f, 0.5f);
                root.anchoredPosition = new Vector2(-EdgeMargin, (mid - index) * VerticalPitch);
                break;
            case 3: // Top — horizontal row under the top edge (Perfect-Comms' default side)
                root.anchorMin = root.anchorMax = new Vector2(0.5f, 1f);
                root.pivot = new Vector2(0.5f, 1f);
                root.anchoredPosition = new Vector2((index - mid) * HorizontalPitch, -(EdgeMargin + 16f));
                break;
            default: // 4 = Bottom — above the status line's band, clear of the buttons
                root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
                root.pivot = new Vector2(0.5f, 0f);
                root.anchoredPosition = new Vector2((index - mid) * HorizontalPitch, EdgeMargin + 40f);
                break;
        }
    }

    private static void ApplyAlpha(Slot s)
    {
        if (s.Root == null) return;
        // One CanvasGroup fades avatar stack, glow and name together — the
        // cosmetic layer tints stay exact.
        if (s.Group != null) s.Group.alpha = s.Alpha;
        // The glow tracks the smoothed voice level: quiet = faint halo, loud = bright.
        var g = s.Glow.color;
        s.Glow.color = new Color(g.r, g.g, g.b, 0.18f + 0.55f * s.Smooth);
        float k = 1f + 0.2f * s.Smooth;
        s.Glow.rectTransform.localScale = new Vector3(k, k, 1f);
    }

    private static void DestroyAll()
    {
        if (Slots.Count == 0) return;
        foreach (var kv in Slots)
            if (kv.Value.Root != null) Object.Destroy(kv.Value.Root);
        Slots.Clear();
    }
}
