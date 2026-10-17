#pragma warning disable CS8602, CS8603, CS8618
using System;
using System.Collections.Generic;
using System.Linq;
using Interstellar.Voice;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.UI;

/// <summary>
/// Perfect-Comms-style settings panel (single scrolling pane + category rail) bound to
/// <see cref="VoiceConfig"/>. The visual language — colors, metrics, animation and input
/// handling — lives in <see cref="VoiceUiKit"/>; this file only owns the entry list,
/// layout order, scrolling and open/close state.
/// </summary>
public static class VoiceSettingsPanel
{
    private const float PanelW = 908f;
    private const float PanelH = 554f;
    private const float PanelScale = 1.3f;
    private const float RowH = 72f;
    private const float DeviceRowH = 142f;
    private const float HeaderH = 42f;
    private const float TopPad = 12f;
    private const float SmoothScrollRate = 18f;
    private const float ScrollbarMinThumbHeight = 30f;

    // Seven rail entries: the old DEVICES slot became KEYBINDS — the microphone and
    // speaker pickers moved to AUDIO, and only the chord list stays here. KEYBINDS
    // still drops off the rail on Android (chords need a hardware keyboard).
    private static readonly string[] CategoryKeys =
    {
        "vc.cat.audio",
        "vc.cat.keybinds",
        "vc.cat.volume",
        "vc.cat.lobby",
        "vc.settings.room",
        "vc.cat.connection",
        "vc.settings.advanced",
    };

    private static readonly string[] CategoryFallbacks =
    {
        "AUDIO",
        "KEYBINDS",
        "Player Volume",
        "Public Lobby",
        "Room",
        "Connection",
        "Advanced",
    };

    // Public so the F2/F3 chord readers (PublicLobbyWindow / PlayerVolumeWindow) can
    // ask the panel to open on their category instead of owning a window each.
    public const int CatAudio = 0;
    public const int CatKeybinds = 1;
    public const int CatVolume = 2;
    public const int CatLobby = 3;
    public const int CatRoom = 4;
    public const int CatConnection = 5;
    public const int CatAdvanced = 6;

    private static bool IsAndroid => Application.platform == RuntimePlatform.Android;

    // Rail index → category const, so an entry can drop out of the rail (KEYBINDS on
    // Android) while the public Cat* constants keep their fixed values.
    private static readonly int[] CategoryMap = BuildCategoryMap();

    private static int[] BuildCategoryMap()
    {
        var map = Enumerable.Range(0, CategoryKeys.Length).ToArray();
        if (IsAndroid) map = map.Where(c => c != CatKeybinds).ToArray();
        return map;
    }

    /// <summary>Exact option list from the legacy window (Game/VoiceSettingsWindow.cs).</summary>
    private static readonly string[] Langs = { "en", "zh_CN", "ja", "ko", "ru", "es", "pt_BR", "Other" };

    private static readonly string[] LangNames =
        { "English", "中文", "日本語", "한국어", "Русский", "Español", "Português", "" };

    private static string LangDisplay(int i)
        => i == Langs.Length - 1 ? T("vc.lang.other", "Other") : LangNames[i];

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static float _scrollTarget;
    private static float _contentHeight;
    private static float _animT;
    private static int _visSignature = int.MinValue;
    private static bool _rebuildRequested;
    /// <summary>Last observed speaker-test state, used to refresh the Play button label.</summary>
    private static bool _speakerTestShown;
    private static RectTransform? _scrollbarRoot;
    private static RectTransform? _scrollbarThumb;
    private static Image? _scrollbarTrackImage;
    private static Image? _scrollbarThumbImage;
    private static bool _scrollbarDragging;
    private static float _scrollbarDragOffset;

    // Whole-pane drag scrolling (touch has no wheel on Android): a press inside the
    // pane is held back until we know whether it is a tap, a vertical scroll gesture,
    // or a horizontal control drag (slider).
    private static bool _panePending;
    private static bool _paneDragging;
    private static float _paneDownX;
    private static float _paneDownY;
    private static Vector2 _paneLastLocal;
    private static float PaneDragThreshold => Mathf.Clamp(Screen.height * 0.015f, 10f, 45f);

    private static bool ShellAlive => _shell != null && _shell.Root != null;
    private static bool _shown;
    public static bool IsOpen => ShellAlive && _shown;

    public static void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    public static void Show()
    {
        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        bool rebuilt = false;
        if (!ShellAlive)
        {
            Destroy();
            Build();
            rebuilt = true;
        }

        _shell!.Root.SetActive(true);
        _shell.Group.alpha = 1f;
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        _scroll = 0f;
        _scrollTarget = 0f;
        CancelScrollbarDrag();
        _panePending = false;
        _paneDragging = false;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        _shown = true;

        ApplyPanelPresentation();
        if (!rebuilt) RebuildRows(true);

        VoiceUiKit.RaiseAbove(_shell.RootRect);

        SyncHosts();
    }

    public static void SelectCategory(int cat)
    {
        if (!IsOpen || _rail == null) return;
        int idx = Array.IndexOf(CategoryMap, cat);
        if (idx < 0) return; // e.g. KEYBINDS on Android: category not in the rail
        _rail.Select(idx);   // fires OnSelect (rebuild + SyncHosts) only when it moves
    }

    public static bool IsOnCategory(int cat)
        => IsOpen && _rail != null && _rail.Selected >= 0
           && _rail.Selected < CategoryMap.Length
           && CategoryMap[_rail.Selected] == cat;

    private static void SyncHosts()
    {
        int cat = -1;
        if (_rail != null && _rail.Selected >= 0 && _rail.Selected < CategoryMap.Length)
            cat = CategoryMap[_rail.Selected];
        bool on = IsOpen;
        try { PublicLobbyWindow.Instance?.SetBrowsing(on && cat == CatLobby); } catch { }
        try { PlayerVolumeWindow.Instance?.SetPanelActive(on && cat == CatVolume); } catch { }
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell(
            "VC_SettingsPanel",
            TranslationHelper.Get("vc.settings.title", "Interstellar Voice Chat"),
            PanelW,
            PanelH,
            HeaderClose);
        _rail = new VoiceUiKit.CategoryRail();
        _rail.Build(_shell.RailRoot, _shell.RailWidth, BuildCategoryLabels());
        _rail.OnSelect = _ =>
        {
            _rebuildRequested = false;
            SyncHosts();
            RebuildRows(true);
        };
        BuildScrollbar();
        RebuildRows(true);
    }

    private static string[] BuildCategoryLabels()
    {
        var labels = new string[CategoryMap.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int cat = CategoryMap[i];
            labels[i] = TranslationHelper.Get(CategoryKeys[cat], CategoryFallbacks[cat]);
        }
        return labels;
    }

    private static void BuildScrollbar()
    {
        if (_shell == null) return;

        _scrollbarRoot = VoiceUiKit.Rect("SettingsScrollbar", _shell.PaneClip);
        _scrollbarRoot.Anchor(new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarRoot.sizeDelta = new Vector2(18f, -16f);
        _scrollbarRoot.anchoredPosition = new Vector2(-9f, 0f);

        _scrollbarTrackImage = VoiceUiKit.Panel(
            "ScrollbarTrack",
            _scrollbarRoot,
            new Color32(26, 39, 55, 205),
            rounded: true,
            soft: true);
        _scrollbarTrackImage.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 0.5f));
        _scrollbarTrackImage.rectTransform.sizeDelta = new Vector2(4f, -4f);
        _scrollbarTrackImage.rectTransform.anchoredPosition = Vector2.zero;

        _scrollbarThumb = VoiceUiKit.Rect("ScrollbarThumbHit", _scrollbarRoot);
        _scrollbarThumb.Anchor(
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f));
        _scrollbarThumb.sizeDelta = new Vector2(18f, ScrollbarMinThumbHeight);
        _scrollbarThumb.anchoredPosition = Vector2.zero;

        _scrollbarThumbImage = VoiceUiKit.Panel(
            "ScrollbarThumb",
            _scrollbarThumb,
            VoiceUiKit.TextMuted,
            rounded: true,
            soft: true);
        _scrollbarThumbImage.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 0.5f));
        _scrollbarThumbImage.rectTransform.sizeDelta = new Vector2(7f, -2f);
        _scrollbarThumbImage.rectTransform.anchoredPosition = Vector2.zero;

        _scrollbarRoot.SetAsLastSibling();
        UpdateScrollbarVisual();
    }

    public static void Hide()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelScrollbarDrag();
        _panePending = false;
        _paneDragging = false;
        VoiceDeviceTest.StopMicTest();
        _rebuildRequested = false;
        _speakerTestShown = false;
        _lobbyInfoShown = false;
        _shown = false;
        _animT = 0f;
        _activeRow = null;
        if (_shell != null && _shell.Root != null)
        {
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }

        SyncHosts();
    }

    private static void HeaderClose()
    {
        VoiceUiKit.SwallowClick();
        Hide();
    }

    public static void ForceClose()
    {
        Hide();
    }

    private static void Destroy()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelScrollbarDrag();
        _panePending = false;
        _paneDragging = false;
        VoiceDeviceTest.StopMicTest();
        _shown = false;
        if (_shell != null)
        {
            if (_shell.Root != null) Object.Destroy(_shell.Root);
            _shell = null;
        }
        _rail = null;
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _scrollTarget = 0f;
        _contentHeight = 0f;
        _visSignature = int.MinValue;
        _rebuildRequested = false;
        _scrollbarRoot = null;
        _scrollbarThumb = null;
        _scrollbarTrackImage = null;
        _scrollbarThumbImage = null;

        SyncHosts();
    }

    private sealed class Entry
    {
        public string Key = "";
        public float Height = RowH;
        public bool IsHeader;
        public Func<bool> Visible = () => true;
        public Func<RectTransform, float, float, VoiceUiKit.Row?> Build = (_, _, _) => null;
    }

    private static readonly Func<bool> Always = () => true;

    private static List<Entry> BuildCategory(int cat)
    {
        var defs = new List<Entry>();
        if ((uint)cat >= (uint)CategoryMap.Length)
            return defs;

        // The rail index maps through CategoryMap so KEYBINDS can disappear on
        // Android without renumbering the categories that follow it.
        switch (CategoryMap[cat])
        {
            case CatAudio: BuildAudio(defs); break;
            case CatKeybinds: BuildKeybinds(defs); break;
            case CatVolume: BuildVolume(defs); break;
            case CatLobby: BuildLobby(defs); break;
            case CatRoom: BuildRoom(defs); break;
            case CatConnection: BuildConnection(defs); break;
            case CatAdvanced: BuildAdvanced(defs); break;
        }
        return defs;
    }

    private static List<Entry> CollectVisible(int cat)
    {
        var all = BuildCategory(cat);
        var list = new List<Entry>();
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.IsHeader && !e.Visible()) continue;
            list.Add(e);
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].IsHeader) break;
            list.RemoveAt(i);
        }
        return list;
    }

    private static int Signature(List<Entry> entries)
    {
        int sig = entries.Count;
        for (int i = 0; i < entries.Count; i++)
            sig = sig * 31 + entries[i].Key.GetHashCode();
        sig = sig * 31 + VoiceConfig.MicrophoneDevices.Count;
        sig = sig * 31 + VoiceConfig.SpeakerDevices.Count;
        sig = sig * 31 + Interstellar.Network.IceConfig.Version;
        return sig;
    }

    private static void RebuildRows(bool resetScroll)
    {
        if (_shell == null) return;
        // Before the rows are torn down: the captured row has to let go while its
        // objects still exist (EndCapture touches its own rect transforms).
        VoiceUiKit.RebindRow.CancelCapture();
        CancelScrollbarDrag();
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;

        var visible = CollectVisible(_rail!.Selected);

        float y = -TopPad;
        for (int i = 0; i < visible.Count; i++)
        {
            var e = visible[i];
            var row = e.Build(_shell.PaneRoot, _shell.PaneWidth, y);
            if (row != null) _rows.Add(row);
            bool nextIsRow = i < visible.Count - 1 && !visible[i + 1].IsHeader;
            if (!e.IsHeader && nextIsRow)
            {
                var div = VoiceUiKit.Panel("Div", _shell.PaneRoot, VoiceUiKit.Divider, false);
                div.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                div.rectTransform.sizeDelta = new Vector2(-20f, 1f);
                div.rectTransform.anchoredPosition = new Vector2(0f, y - e.Height + 1f);
            }
            y -= e.Height;
        }
        _contentHeight = -y;
        _visSignature = Signature(visible);

        ApplyScroll(resetScroll);

        if (visible.Count == 0)
        {
            var empty = VoiceUiKit.Text("Empty", _shell.PaneRoot, T("vc.settings.noOptions", "No options"), 16f,
                VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(0f, 40f);
            empty.rectTransform.anchoredPosition = new Vector2(0f, -30f);
        }
    }

    private static string T(string key, string fallback) => TranslationHelper.Get(key, fallback);

    private static string Help(string id, string fallback)
        => TranslationHelper.Get("vc.settings.help." + id, fallback);

    private static void AddSection(List<Entry> defs, string key, string fallback)
    {
        string title = T(key, fallback);
        defs.Add(new Entry
        {
            Key = "##" + key,
            Height = HeaderH,
            IsHeader = true,
            Visible = Always,
            Build = (pane, paneW, y) =>
            {
                VoiceUiKit.SectionHeader(key, pane, title, paneW, y, HeaderH);
                return null;
            }
        });
    }

    private static void AddSlider(
        List<Entry> defs,
        string key,
        string fallback,
        Func<float> get,
        Action<float> set,
        float min,
        float max,
        Func<float, string> fmt,
        string help,
        Func<bool>? enabled = null,
        Func<bool>? visible = null)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.SliderRow(
                get, set, min, max, fmt, stacked: false, enabled: enabled)
                .Build(pane, label, paneW, y, RowH, help)
        });
    }

    private static void AddToggle(
        List<Entry> defs,
        string key,
        string fallback,
        Func<bool> get,
        Action<bool> set,
        string help,
        Func<bool>? enabled = null,
        Func<bool>? visible = null)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ToggleRow(get, set, enabled)
                .Build(pane, label, paneW, y, RowH, help)
        });
    }

    private static void AddStepper(
        List<Entry> defs,
        string key,
        string fallback,
        Func<int> get,
        Action<int> set,
        Func<int> count,
        Func<int, string> labelOf,
        string help,
        float height = RowH,
        bool fullWidthValue = false)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Height = height,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                get, set, count, labelOf, fullWidthValue: fullWidthValue)
                .Build(pane, label, paneW, y, height, help)
        });
    }

    private static void AddAction(
        List<Entry> defs,
        string key,
        string fallback,
        string buttonText,
        Action onClick,
        string help,
        Func<bool>? visible = null)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(onClick)
                .Build(pane, label, buttonText, paneW, y, RowH, help)
        });
    }

    private static void AddRebind(
        List<Entry> defs,
        string key,
        string fallback,
        Func<string> getChord,
        Action<string> setChord,
        Action clear,
        string help,
        Func<bool>? visible = null)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.RebindRow(getChord, setChord, clear)
                .Build(pane, label, paneW, y, RowH, help)
        });
    }

    private static void AddValue(
        List<Entry> defs,
        string key,
        string fallback,
        Func<string> value,
        string help,
        Func<bool>? visible = null)
    {
        string label = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ValueRow()
                .Build(pane, label, value(), paneW, y, RowH, help)
        });
    }

    private static void AddInfo(List<Entry> defs, string key, string fallback, Func<bool>? visible = null)
    {
        string message = T(key, fallback);
        defs.Add(new Entry
        {
            Key = key,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.InfoRow()
                .Build(pane, message, paneW, y, RowH)
        });
    }

    private static readonly Func<float, string> Pct =
        v => $"<color=#22D3EE>{v * 100f:0}%</color>";
    private static readonly Func<float, string> Distance =
        v => $"<color=#22D3EE>{v:F1}m</color>";

    private static void BuildAudio(List<Entry> defs)
    {
        AddSlider(defs, "vc.settings.micVolume", "Mic Volume",
            () => VoiceConfig.MicVolume,
            v => { VoiceConfig.SetMicVolume(v); VoiceRoom.Current?.SetMicVolume(v); },
            0.1f, 3f, Pct,
            Help("micVolume", "Input gain for your microphone. 100% is the normal level; raise it only if other players say you are too quiet."));

        AddSlider(defs, "vc.settings.masterVolume", "Master Volume",
            () => VoiceConfig.MasterVolume,
            v => { VoiceConfig.SetMasterVolume(v); VoiceRoom.Current?.SetMasterVolume(v); },
            0.1f, 3f, Pct,
            Help("masterVolume", "Overall playback volume for every other player. 100% is the normal level."));

        // "Player Volume" is its own rail category now (CatVolume) — no jumper row.

        // The microphone/speaker pickers moved here from the old DEVICES slot,
        // which became the KEYBINDS rail entry.
        AddSection(defs, "vc.cat.devices", "DEVICES");
        if (VoiceConfig.DeviceSelectionSupported)
        {
            defs.Add(new Entry
            {
                Key = "vc.settings.microphone",
                Height = DeviceRowH,
                Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                        () => DeviceIndex(VoiceConfig.MicrophoneDevice, VoiceConfig.MicrophoneDevices),
                        SelectMicrophone,
                        () => VoiceConfig.MicrophoneDevices.Count,
                        i => DeviceLabel(VoiceConfig.MicrophoneDevices, i),
                        fullWidthValue: true)
                    .Build(pane, T("vc.settings.microphone", "Microphone"), paneW, y, DeviceRowH,
                        Help("microphone", "Which microphone the voice chat captures. Pick Default to use the system default device."))
            });

            defs.Add(new Entry
            {
                Key = "vc.settings.speaker",
                Height = DeviceRowH,
                Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                        () => DeviceIndex(VoiceConfig.SpeakerDevice, VoiceConfig.SpeakerDevices),
                        SelectSpeaker,
                        () => VoiceConfig.SpeakerDevices.Count,
                        i => DeviceLabel(VoiceConfig.SpeakerDevices, i),
                        fullWidthValue: true)
                    .Build(pane, T("vc.settings.speaker", "Speaker"), paneW, y, DeviceRowH,
                        Help("speaker", "Which output device the voice chat plays through. Pick Default to use the system default device."))
            });

            defs.Add(new Entry
            {
                Key = "vc.settings.micLevel",
                Build = (pane, paneW, y) => new VoiceUiKit.MeterRow(
                        () => VoiceDeviceTest.MicLevel,
                        v => $"<color=#22D3EE>{v * 100f:0}%</color>")
                    .Build(pane, T("vc.settings.micLevel", "Input Level"), paneW, y, RowH,
                        Help("micLevel", "Live microphone level. Speak: the bar should jump. If it never moves, the wrong input device is selected or the mic is muted elsewhere."))
            });

            // The test button is only meaningful while nothing else holds the
            // device — inside a game the room's own capture feeds the meter.
            defs.Add(new Entry
            {
                Key = "vc.settings.micTest",
                Visible = () => !VoiceDeviceTest.RoomOwnsMicrophone,
                Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(
                        () => { VoiceDeviceTest.ToggleMicTest(); _rebuildRequested = true; })
                    .Build(pane, T("vc.settings.micTest", "Microphone Test"),
                        VoiceDeviceTest.MicRunning
                            ? T("vc.settings.testStop", "Stop")
                            : T("vc.settings.testStart", "Start"),
                        paneW, y, RowH,
                        Help("micTest", "Opens the selected microphone so you can watch the level bar above while you speak."))
            });

            defs.Add(new Entry
            {
                Key = "vc.settings.speakerTest",
                Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(
                        () => VoiceDeviceTest.PlaySpeakerTone())
                    .Build(pane, T("vc.settings.speakerTest", "Speaker Test"),
                        VoiceDeviceTest.SpeakerPlaying
                            ? T("vc.settings.testPlaying", "Playing")
                            : T("vc.settings.testPlay", "Play"),
                        paneW, y, RowH,
                        Help("speakerTest", "Plays a short test chime through the selected output device."))
            });
        }
        else
        {
            AddInfo(defs, "vc.settings.noDeviceSupport",
                "Device selection is not supported on this platform.");
        }
    }

    private static int DeviceIndex(string current, List<string> devices)
        => Mathf.Max(0, devices.IndexOf(current ?? ""));

    private static string DeviceLabel(List<string> devices, int i)
    {
        if (devices.Count == 0) return "--";
        string name = devices[Mathf.Clamp(i, 0, devices.Count - 1)];
        return string.IsNullOrEmpty(name) ? T("vc.settings.defaultDevice", "Default") : name;
    }

    private static void SelectMicrophone(int i)
    {
        var devices = VoiceConfig.MicrophoneDevices;
        if (devices.Count == 0) return;
        string name = devices[Mathf.Clamp(i, 0, devices.Count - 1)];
        VoiceConfig.SetMicrophoneDevice(name);
        VoiceRoom.Current?.SetMicrophone(name);
    }

    private static void SelectSpeaker(int i)
    {
        var devices = VoiceConfig.SpeakerDevices;
        if (devices.Count == 0) return;
        string name = devices[Mathf.Clamp(i, 0, devices.Count - 1)];
        VoiceConfig.SetSpeakerDevice(name);
        VoiceRoom.Current?.SetSpeaker(name);
    }

    /// <summary>Same host check the legacy window used in RebuildContent().</summary>
    private static bool IsHost
    {
        get
        {
            try
            {
                var inst = AmongUsClient.Instance;
                if (inst != null)
                    return inst.AmHost && inst.GameState == InnerNet.InnerNetClient.GameStates.Joined;
            }
            catch { }
            return false;
        }
    }

    /// <summary>Identical to the legacy window's RoomChanged().</summary>
    private static void RoomChanged()
    {
        VoiceConfig.ApplyLocalHostSettingsToSynced();
        InterstellarHudState.MarkRoomSettingsDirty();
    }

    private static void AddHostToggle(
        List<Entry> defs,
        string key,
        string fallback,
        Func<bool> get,
        Action<bool> set,
        string help)
        => AddToggle(defs, key, fallback, get,
            v => { set(v); RoomChanged(); },
            help,
            enabled: () => IsHost);

    private static void BuildKeybinds(List<Entry> defs)
    {
        // No leading "KEYBINDS" title — the rail entry already says it; start with
        // the first group header.
        AddSection(defs, "vc.settings.keybinds.windows", "Windows");

        AddRebind(defs, "vc.settings.keybinds.settings", "Open Voice Settings",
            () => VoiceConfig.GetChord(VoiceChord.SettingsPanel),
            v => VoiceConfig.SetChord(VoiceChord.SettingsPanel, v),
            () => VoiceConfig.ClearChord(VoiceChord.SettingsPanel),
            Help("keybinds.settings", "Opens this settings panel. Hold Ctrl/Shift/Alt while pressing a key to bind a combination. Delete or Backspace clears the binding, Esc cancels the capture."));

        AddRebind(defs, "vc.settings.keybinds.publicLobby", "Open Public Lobby",
            () => VoiceConfig.GetChord(VoiceChord.PublicLobby),
            v => VoiceConfig.SetChord(VoiceChord.PublicLobby, v),
            () => VoiceConfig.ClearChord(VoiceChord.PublicLobby),
            Help("keybinds.publicLobby", "Opens this settings panel on the public lobby list."));

        AddRebind(defs, "vc.settings.keybinds.playerVolume", "Open Player Volume",
            () => VoiceConfig.GetChord(VoiceChord.PlayerVolume),
            v => VoiceConfig.SetChord(VoiceChord.PlayerVolume, v),
            () => VoiceConfig.ClearChord(VoiceChord.PlayerVolume),
            Help("keybinds.playerVolume", "Opens this settings panel on the per-player volume list."));

        AddSection(defs, "vc.settings.keybinds.quickActions", "Quick Actions");

        AddRebind(defs, "vc.settings.keybinds.cycleMic", "Cycle Mic",
            () => VoiceConfig.GetChord(VoiceChord.CycleMic),
            v => VoiceConfig.SetChord(VoiceChord.CycleMic, v),
            () => VoiceConfig.ClearChord(VoiceChord.CycleMic),
            Help("keybinds.cycleMic", "Mutes and unmutes your microphone, switching to the impostor radio channel when that is enabled."));

        AddRebind(defs, "vc.settings.keybinds.toggleSpeaker", "Toggle Speaker",
            () => VoiceConfig.GetChord(VoiceChord.ToggleSpeaker),
            v => VoiceConfig.SetChord(VoiceChord.ToggleSpeaker, v),
            () => VoiceConfig.ClearChord(VoiceChord.ToggleSpeaker),
            Help("keybinds.toggleSpeaker", "Mutes and unmutes everything you hear."));
    }

    /// <summary>State of the PUBLIC LOBBY Info switch — reset when the panel closes.</summary>
    private static bool _lobbyInfoShown;
    /// <summary>Height of the revealed info paragraph (the old window's Info overlay).</summary>
    private const float LobbyInfoH = 92f;
    /// <summary>Lobby rows carry a title plus a wrapped details line, taller than the
    /// standard 72 so nothing ellipsizes out of the row.</summary>
    private const float LobbyRowH = 96f;

    private static void BuildVolume(List<Entry> defs)
    {
        AddToggle(defs, "vc.player.autoVolume", "Auto Volume",
            () => VoiceConfig.AutoVolume,
            v =>
            {
                VoiceConfig.AutoVolume = v;
                if (!v)
                {
                    // Turning it off pins the levels auto settled on so they survive a restart.
                    try
                    {
                        foreach (var p in PlayerVolumeWindow.SnapshotPlayers())
                            VoiceConfig.SetPlayerVolume(p.PlayerName, p.Volume);
                    }
                    catch { }
                }
            },
            Help("playerAutoVolume",
                "Keeps everyone's volume at a steady level automatically. While it is on, the sliders below are read-only."));

        var players = PlayerVolumeWindow.SnapshotPlayers();
        if (players.Count == 0)
        {
            bool inRoom = false;
            try { inRoom = VoiceRoom.Current != null; } catch { }
            if (inRoom)
                AddInfo(defs, "vc.playerVolume.noPlayers", "No other players connected yet.");
            else
                AddInfo(defs, "vc.playerVolume.noRoom", "Not connected to a voice room.");
            return;
        }

        for (int i = 0; i < players.Count; i++)
        {
            VCPlayer p = players[i];
            string displayName = PlayerVolumeWindow.ResolveDisplayName(p);
            defs.Add(new Entry
            {
                Key = $"vol.{p.ClientId}.{p.PlayerId}.{displayName}",
                Build = (pane, paneW, y) =>
                    PlayerVolumeWindow.BuildRow(pane, p, displayName, paneW, y, RowH)
            });
        }
    }

    /// <summary>
    /// PUBLIC LOBBY — the old F2 window's content as panel rows: a status line with the
    /// Refresh button beside it (the socket's own words while it works — Connecting...,
    /// Getting code..., Copied: XXX — the found count after), an Info switch that
    /// reveals the help paragraph, then one row per lobby with the Copy slot on the
    /// right: bright title over the dimmed details line the window drew underneath it.
    /// Scrolling replaces the old three-row cap; the websocket belongs to
    /// PublicLobbyWindow and follows this category through SyncHosts.
    /// </summary>
    private static void BuildLobby(List<Entry> defs)
    {
        var lobbies = CopyLobbies();
        string status = LobbyStatusText(lobbies);
        string infoText = T("vc.lobby.infoText",
            "Click a lobby to copy its room code, then join from the Among Us title screen. Grey rows are in a game. Refresh reloads the list.");

        // Status row carrying the Refresh button: the copy request keeps its live
        // status there too, exactly like the window's status line. Narrow button +
        // wrapping label so long statuses ("Error: …") are never ellipsized away.
        defs.Add(new Entry
        {
            Key = "lobby.status|" + status,
            Height = RowH,
            Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(() =>
                {
                    PublicLobbyWindow.Instance?.Refresh();
                    _rebuildRequested = true;
                }, null, 130f)
                .Build(pane, status, T("vc.settings.refresh", "Refresh"), paneW, y, RowH,
                    wrapLabel: true)
        });

        AddToggle(defs, "vc.lobby.info", "Info",
            () => _lobbyInfoShown,
            v => { _lobbyInfoShown = v; _rebuildRequested = true; },
            infoText);

        defs.Add(new Entry
        {
            Key = "lobby.infoText",
            Height = LobbyInfoH,
            Visible = () => _lobbyInfoShown,
            Build = (pane, paneW, y) => new VoiceUiKit.InfoRow()
                .Build(pane, infoText, paneW, y, LobbyInfoH)
        });

        for (int i = 0; i < lobbies.Count; i++)
        {
            var lobby = lobbies[i]; // body-scoped: the Build closure captures this copy
            // BCL's wire enum: 0 = in the lobby (joinable), anything else = in a
            // game — and the server refuses join_lobby for those anyway.
            bool canCopy = lobby.gameState == 0;
            string title = PublicLobbyWindow.Truncate(lobby.title, 24);
            string details = PublicLobbyWindow.BuildDetails(lobby);
            string button = !canCopy
                ? PublicLobbyManager.GetGameStateName(lobby.gameState).ToUpperInvariant()
                : lobby.id == (PublicLobbyWindow.Instance?.LastCopiedId ?? -1)
                    ? T("vc.lobby.copiedShort", "Copied")
                    : T("vc.lobby.copy", "Copy");

            defs.Add(new Entry
            {
                // The code enters the Key so a row re-renders the moment a
                // background fetch learns it (Signature hashes the Keys).
                Key = $"lobby.{lobby.id}.{lobby.current_players}.{lobby.gameState}.{button}.{lobby.code}",
                Height = LobbyRowH,
                Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(
                        () => PublicLobbyWindow.Instance?.CopyLobbyCode(lobby.id), null, 130f)
                    .Build(pane, $"<b>{title}</b>\n<color=#7C8CA3>{details}</color>",
                        button, paneW, y, LobbyRowH, wrapLabel: true)
            });
        }
    }

    /// <summary>Status precedence, exactly the old RebuildLobbyList: loading, then the
    /// socket's own status, then the found count, then the empty line.</summary>
    private static string LobbyStatusText(List<PublicLobbyManager.LobbyInfo> lobbies)
    {
        if (PublicLobbyManager.IsLoading) return T("vc.lobby.loading", "Loading...");
        string raw = PublicLobbyWindow.Instance?.StatusText ?? "";
        if (!string.IsNullOrEmpty(raw)) return raw;
        if (lobbies.Count > 0)
            return string.Format(T("vc.lobby.found", "{0} public lobbies found"), lobbies.Count);
        return T("vc.lobby.empty", "No public lobbies available.");
    }

    /// <summary>Open lobbies first, stably — the order the reference list used. The
    /// socket thread can mutate the source mid-enumeration: report "no lobbies" and let
    /// the next signature pass pick the result up.</summary>
    private static List<PublicLobbyManager.LobbyInfo> CopyLobbies()
    {
        try
        {
            var lobbies = PublicLobbyManager.CachedLobbies;
            if (lobbies == null) return new List<PublicLobbyManager.LobbyInfo>();
            return lobbies.OrderBy(l => l.gameState == 0 ? 0 : 1).ToList();
        }
        catch
        {
            return new List<PublicLobbyManager.LobbyInfo>();
        }
    }

    private static void BuildRoom(List<Entry> defs)
    {
        AddSection(defs, "vc.settings.room", "Room");

        AddSlider(defs, "vc.settings.maxChatDistance", "Max Chat Distance",
            () => VoiceConfig.HostMaxChatDistance,
            v => { VoiceConfig.SetHostMaxChatDistance(v); RoomChanged(); },
            1.5f, 20f, Distance,
            Help("maxChatDistance", "How far away other players can still hear you, in metres. Host setting."),
            enabled: () => IsHost);

        AddHostToggle(defs, "vc.settings.wallsBlockSound", "Walls Block Voice",
            () => VoiceConfig.HostWallsBlockSound, v => VoiceConfig.SetHostWallsBlockSound(v),
            Help("wallsBlockSound", "Walls and closed doors stop voice from passing through. Host setting."));
        AddHostToggle(defs, "vc.settings.onlyHearInSight", "Only Hear In Sight",
            () => VoiceConfig.HostOnlyHearInSight, v => VoiceConfig.SetHostOnlyHearInSight(v),
            Help("onlyHearInSight", "Players outside your line of sight become inaudible. Host setting."));
        AddHostToggle(defs, "vc.settings.impostorHearGhosts", "Impostor Can Hear Ghosts",
            () => VoiceConfig.HostImpostorHearGhosts, v => VoiceConfig.SetHostImpostorHearGhosts(v),
            Help("impostorHearGhosts", "Lets the impostor hear dead players talking. Host setting."));
        AddHostToggle(defs, "vc.settings.onlyGhostsCanTalk", "Only Ghosts Can Talk",
            () => VoiceConfig.HostOnlyGhostsCanTalk, v => VoiceConfig.SetHostOnlyGhostsCanTalk(v),
            Help("onlyGhostsCanTalk", "Only dead players can speak; alive players are muted. Host setting."));
        AddHostToggle(defs, "vc.settings.hearInVent", "Hear Outside While In Vent",
            () => VoiceConfig.HostHearInVent, v => VoiceConfig.SetHostHearInVent(v),
            Help("hearInVent", "While hiding in a vent you can still hear players outside. Host setting."));
        AddHostToggle(defs, "vc.settings.hearVentPlayers", "Hear Players In Vent",
            () => VoiceConfig.HostHearVentPlayers, v => VoiceConfig.SetHostHearVentPlayers(v),
            Help("hearVentPlayers", "Players outside can hear the people hiding in vents. Host setting."));
        AddHostToggle(defs, "vc.settings.ventPrivateChat", "Vent Private Chat",
            () => VoiceConfig.HostVentPrivateChat, v => VoiceConfig.SetHostVentPrivateChat(v),
            Help("ventPrivateChat", "Conversation inside a vent stays private to vent occupants. Host setting."));
        AddHostToggle(defs, "vc.settings.commsSabotageMutes", "Mute During Sabotage",
            () => VoiceConfig.HostCommsSabDisables, v => VoiceConfig.SetHostCommsSabDisables(v),
            Help("commsSabotageMutes", "Mutes everyone while the communications sabotage is active. Host setting."));
        AddHostToggle(defs, "vc.settings.cameraCanHear", "Hear While On Camera",
            () => VoiceConfig.HostCameraCanHear, v => VoiceConfig.SetHostCameraCanHear(v),
            Help("cameraCanHear", "Players watching cameras keep their voice chat enabled. Host setting."));
        AddHostToggle(defs, "vc.settings.impostorPrivateRadio", "Impostor Private Radio",
            () => VoiceConfig.HostImpostorPrivateRadio, v => VoiceConfig.SetHostImpostorPrivateRadio(v),
            Help("impostorPrivateRadio", "The impostor gets a private channel only other impostors can hear. Host setting."));
        AddHostToggle(defs, "vc.settings.onlyMeetingOrLobby", "Only Meeting / Lobby Can Chat",
            () => VoiceConfig.HostOnlyMeetingOrLobby, v => VoiceConfig.SetHostOnlyMeetingOrLobby(v),
            Help("onlyMeetingOrLobby", "Voice chat only works during meetings and in the lobby. Host setting."));

        AddSection(defs, "vc.settings.publicLobby", "Public Lobby");

        AddToggle(defs, "vc.settings.publicLobbyEnable", "Enable Public Lobby",
            () => VoiceConfig.PublicLobbyEnabled,
            v => VoiceConfig.PublicLobbyEnabled = v,
            Help("publicLobbyEnable", "Advertise this lobby in the public lobby list so other players can join. Host setting."),
            enabled: () => IsHost);

        AddValue(defs, "vc.settings.publicLobbyTitle", "Public Lobby Title",
            PublicLobbyTitleValue,
            Help("publicLobbyTitle", "The title shown for this lobby in the public list. Edited in the config file."));

        AddStepper(defs, "vc.settings.language", "Language",
            LanguageIndex,
            i => VoiceConfig.PublicLobbyLanguage = Langs[Mathf.Clamp(i, 0, Langs.Length - 1)],
            () => Langs.Length,
            i => LangDisplay(Mathf.Clamp(i, 0, Langs.Length - 1)),
            Help("language", "The language other players see when browsing the public lobby list."));

        // Browsing moved to the PUBLIC LOBBY rail category (CatLobby) — no jumper row.
    }

    private static int LanguageIndex()
    {
        int idx = Array.IndexOf(Langs, VoiceConfig.PublicLobbyLanguage);
        return idx < 0 ? 0 : idx;
    }

    private static string PublicLobbyTitleValue()
    {
        string title = VoiceConfig.PublicLobbyTitle ?? "";
        if (string.IsNullOrEmpty(title)) return "(set in config)";
        return title.Length <= 26 ? title : title[..23] + "...";
    }

    private static void BuildConnection(List<Entry> defs)
    {
        // No server row on purpose: BCL mode has exactly one server, fixed by
        // the client — displaying it only invites the question "how do I change
        // it?", which is precisely what the row's absence answers.
        // No separate "auto pick TURN" toggle either: the region stepper's Auto
        // option already IS the automatic pick (see P2pTransport.BuildTurnCandidates).

        AddToggle(defs, "vc.settings.natFix", "NAT fix",
            () => VoiceConfig.NatFix || Interstellar.Network.IceConfig.ForceRelayOnly,
            v =>
            {
                VoiceConfig.NatFix = v;
                // Routing mode changed: established paths never re-route on
                // their own — reload the connection so the new mode applies now.
                VoiceRoom.Current?.RestartTransport("NAT fix " + (v ? "on" : "off"));
            },
            Help("natFix", "Sends all voice through a relay server from the start instead of trying a direct connection between players. Only turn it on if you keep failing to hear each other — the trade-off is a little extra delay for everyone in the room."));

        AddStepper(defs, "vc.settings.turnRegion", "TURN region",
            TurnRegionIndex,
            OnTurnRegionSelected,
            () => RegionOptions().Length,
            i => RegionOptions()[Mathf.Clamp(i, 0, RegionOptions().Length - 1)],
            Help("turnRegion", "Which relay server region to use. Auto always picks the one that answers fastest; pick a specific region only if you want to force it. Most players should leave this on Auto."));

        AddAction(defs, "vc.settings.refreshConn", "Refresh connection",
            T("vc.settings.refreshConnBtn", "Refresh"),
            () => VoiceRoom.Current?.RestartTransport("manual refresh"),
            Help("refreshConn", "Reconnects voice from scratch — use it if voice stops working (no sound, or the bottom-left status looks stuck). Takes a couple of seconds; mic and speaker state are kept."));
    }

    /// <summary>["Auto"] + the distinct region tags the server has sent so far.
    /// Snapshot per build — the row Signature folds IceConfig.Version in, so a
    /// config that lands mid-session repopulates this list.</summary>
    private static string[] RegionOptions()
    {
        var regions = Interstellar.Network.IceConfig.Regions;
        var opts = new string[regions.Count + 1];
        opts[0] = T("vc.settings.turnRegion.auto", "Auto");
        for (int i = 0; i < regions.Count; i++)
            opts[i + 1] = regions[i];
        return opts;
    }

    private static int TurnRegionIndex()
    {
        string cur = VoiceConfig.TurnRegion;
        if (string.IsNullOrEmpty(cur)) return 0;
        var opts = RegionOptions();
        for (int i = 1; i < opts.Length; i++)
            if (string.Equals(opts[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static void OnTurnRegionSelected(int idx)
    {
        var opts = RegionOptions();
        int i = Mathf.Clamp(idx, 0, opts.Length - 1);
        string value = i <= 0 ? "" : opts[i];
        if (value == VoiceConfig.TurnRegion) return;
        VoiceConfig.TurnRegion = value;
        _rebuildRequested = true;
        // Same as the NAT fix toggle: a pinned region changes which relay the
        // race picks — reload the connection so it applies now.
        VoiceRoom.Current?.RestartTransport("TURN region " + (value.Length == 0 ? "auto" : value));
    }

    private static void BuildAdvanced(List<Entry> defs)
    {
        AddToggle(defs, "vc.settings.noiseSuppression", "Noise Suppression",
            () => VoiceConfig.NoiseSuppression,
            v => VoiceConfig.NoiseSuppression = v,
            Help("noiseSuppression", "Removes constant background noise such as fans and keyboard clicks."));

        AddToggle(defs, "vc.settings.echoCancellation", "Echo Cancellation",
            () => VoiceConfig.EchoCancellation,
            v => VoiceConfig.EchoCancellation = v,
            Help("echoCancellation", "Reduces feedback when you play through speakers instead of headphones."));

        AddToggle(defs, "vc.settings.vad", "VAD (Voice Activity Detection)",
            () => VoiceConfig.VADEnabled,
            v => VoiceConfig.VADEnabled = v,
            Help("vad", "Voice activity detection: only sends audio while you are actually speaking."));

        AddSlider(defs, "vc.settings.buttonSize", "Mic/Speaker Button Size",
            () => VoiceConfig.HotkeyButtonScale,
            v => VoiceConfig.HotkeyButtonScale = v,
            0.5f, 2f,
            v => $"<color=#22D3EE>{v:F2}x</color>",
            Help("buttonSize", "Size of the mic and speaker buttons in the bottom-left corner, background plate included. 1.00x is the default; the slider ranges from 0.50x to 2.00x."));
    }

    // ========================================================
    //  Tick / input / scrolling
    // ========================================================
    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        // While a rebind row captures, Esc cancels that capture instead of closing
        // the panel (RebindRow.Tick reads it later in this same frame).
        if (!VoiceUiKit.IsCapturingKey && Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            Hide();
            return;
        }

        float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        if (_animT < 1f)
            _animT = Mathf.Min(1f, _animT + dt / 0.22f);
        ApplyPanelPresentation();

        _shell.TickHeader();
        if (_shell == null || !_shown) return;
        _rail!.Tick();
        bool scrollbarOwnsPointer = HandleScrollInput();
        UpdateSmoothScroll(dt);
        HandleInput(scrollbarOwnsPointer);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        // PLAYER VOLUME rows carry a live level bar; the host feeds them only while
        // those rows are the ones on screen (they are part of _rows, so a stale row
        // cannot exist once this category is left or the panel is rebuilt).
        if (IsOnCategory(CatVolume)) PlayerVolumeWindow.FeedMeters(_rows);

        if (_rebuildRequested)
        {
            _rebuildRequested = false;
            RebuildRows(false);
            return;
        }

        // Keep the speaker-test button label honest: the chime stops on its own
        // (VoiceDeviceTest.Tick), so rebuild the row when that flips.
        if (_speakerTestShown != VoiceDeviceTest.SpeakerPlaying)
        {
            _speakerTestShown = VoiceDeviceTest.SpeakerPlaying;
            _rebuildRequested = true;
        }

        if (Time.frameCount % 20 == 0) RefreshVisibilityIfChanged();
    }

    private static void RefreshVisibilityIfChanged()
    {
        if (_rail == null) return;
        // Never rebuild under a live drag: a joining player (volume) or a lobby count
        // change would destroy the row the pointer is holding.
        if (_activeRow != null) return;
        var visible = CollectVisible(_rail.Selected);
        if (Signature(visible) == _visSignature) return;
        RebuildRows(false);
    }

    private static void ApplyPanelPresentation()
    {
        if (_shell == null) return;
        float scale = PanelScale * VoiceUiKit.AppearScale(_animT);
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = Mathf.Clamp01(_animT / 0.6f);
    }

    private static void ApplyScroll(bool reset)
    {
        float maxScroll = MaxScroll();
        if (reset)
        {
            _scroll = 0f;
            _scrollTarget = 0f;
        }
        else
        {
            _scroll = Mathf.Clamp(_scroll, 0f, maxScroll);
            _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, maxScroll);
        }
        _shell!.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static float ViewHeight()
        => _shell != null ? _shell.PaneHeight - 24f : 0f;

    private static float MaxScroll()
        => VoiceSettingsScrollPolicy.MaxScroll(_contentHeight, ViewHeight());

    private static bool HandleScrollInput()
    {
        if (_shell == null)
        {
            _panePending = false;
            _paneDragging = false;
            CancelScrollbarDrag();
            return false;
        }

        bool scrollbarLive = _scrollbarRoot != null && _scrollbarRoot.gameObject.activeSelf;
        if (!scrollbarLive)
            CancelScrollbarDrag();

        if (scrollbarLive && _scrollbarDragging)
        {
            if (!Input.GetMouseButton(0))
            {
                CancelScrollbarDrag();
                return false;
            }
            if (TryGetScrollbarPointerFromTop(out float pointerFromTop))
                SetScrollFromThumbTop(pointerFromTop - _scrollbarDragOffset, snap: true);
            return true;
        }

        bool inputBusy = _activeRow != null || VoiceUiKit.IsCapturingKey;
        if (inputBusy)
            _scrollTarget = _scroll;
        if (scrollbarLive && _scrollbarRoot != null && !inputBusy
            && Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_scrollbarRoot))
        {
            if (!TryGetScrollbarPointerFromTop(out float pointerFromTop)) return true;
            float thumbTop = _scrollbarThumb != null ? -_scrollbarThumb.anchoredPosition.y : 0f;
            if (_scrollbarThumb != null && VoiceUiKit.Contains(_scrollbarThumb))
            {
                _scrollbarDragging = true;
                _scrollbarDragOffset = pointerFromTop - thumbTop;
            }
            else
            {
                float thumbHeight = _scrollbarThumb != null
                    ? _scrollbarThumb.rect.height
                    : ScrollbarMinThumbHeight;
                SetScrollFromThumbTop(pointerFromTop - thumbHeight * 0.5f, snap: false);
            }
            return true;
        }

        if (HandlePaneDrag(inputBusy))
            return true;

        if (!inputBusy && VoiceUiKit.Contains(_shell.PaneClip))
        {
            if (Input.GetMouseButtonDown(0))
                _scrollTarget = _scroll;
            float delta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(delta) > 0.01f)
                _scrollTarget = Mathf.Clamp(_scrollTarget - delta * RowH, 0f, MaxScroll());
        }
        return false;
    }

    /// <summary>
    /// Whole-pane drag scrolling (touch devices have no wheel, so on Android the page
    /// itself must be the scroller). Rows act on press, so a press inside the pane is
    /// deferred: released without moving it dispatches to the rows on the up frame (a
    /// tap), moved vertically past a device-scaled threshold it becomes a scroll the
    /// rows never see, moved horizontally past the threshold it is handed to the rows
    /// late so sliders can still grab. Returns true while the press belongs to the page
    /// or is still undecided, so HandleInput does not dispatch it.
    /// </summary>
    private static bool HandlePaneDrag(bool inputBusy)
    {
        if (_shell == null)
        {
            _panePending = false;
            _paneDragging = false;
            return false;
        }

        if (_paneDragging)
        {
            if (!Input.GetMouseButton(0))
            {
                _paneDragging = false;
                return false;
            }
            if (VoiceUiKit.LocalPoint(_shell!.PaneClip, out var local))
            {
                float dy = local.y - _paneLastLocal.y;
                _paneLastLocal = local;
                _scrollTarget = Mathf.Clamp(_scrollTarget + dy, 0f, MaxScroll());
                _scroll = _scrollTarget; // direct finger follow, no easing
            }
            return true;
        }

        if (_panePending)
        {
            if (!Input.GetMouseButton(0))
            {
                _panePending = false;
                DispatchPressToRows(); // tap: rows fire on the up frame
                return true;
            }
            float dx = Input.mousePosition.x - _paneDownX;
            float dy = Input.mousePosition.y - _paneDownY;
            float threshold = PaneDragThreshold;
            if (Mathf.Abs(dy) > threshold && Mathf.Abs(dy) >= Mathf.Abs(dx))
            {
                _panePending = false;
                _paneDragging = true;
                if (VoiceUiKit.LocalPoint(_shell.PaneClip, out var start))
                    _paneLastLocal = start;
                return true;
            }
            if (Mathf.Abs(dx) > threshold)
            {
                _panePending = false;
                DispatchPressToRows(); // horizontal control gesture (slider): hand it over
                return true;
            }
            return true; // undecided: hold the press back
        }

        // Only defer where there is something to scroll; when the content fits, keep the
        // original immediate down-frame dispatch so taps behave exactly as before.
        if (!inputBusy && MaxScroll() > 0.5f
            && Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_shell.PaneClip))
        {
            _panePending = true;
            _paneDownX = Input.mousePosition.x;
            _paneDownY = Input.mousePosition.y;
            _scrollTarget = _scroll;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Dispatches a deferred press to the rows exactly as HandleInput does on its down
    /// frame (rows re-check their own hit areas), then picks up any row that claims a
    /// drag; HandleInput's up branch releases it within the same frame for taps.
    /// </summary>
    private static void DispatchPressToRows()
    {
        if (_shell == null || !VoiceUiKit.Contains(_shell.PaneClip)) return;
        for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
        _activeRow = FindDragging();
    }

    private static bool TryGetScrollbarPointerFromTop(out float pointerFromTop)
    {
        pointerFromTop = 0f;
        if (_scrollbarRoot == null || !VoiceUiKit.LocalPoint(_scrollbarRoot, out var local))
            return false;
        pointerFromTop = _scrollbarRoot.rect.yMax - local.y;
        return true;
    }

    private static void SetScrollFromThumbTop(float thumbTop, bool snap)
    {
        if (_scrollbarRoot == null || _scrollbarThumb == null) return;
        _scrollTarget = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            thumbTop,
            MaxScroll(),
            _scrollbarRoot.rect.height,
            _scrollbarThumb.rect.height);
        if (!snap) return;
        _scroll = _scrollTarget;
        if (_shell != null)
            _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void UpdateSmoothScroll(float unscaledDeltaTime)
    {
        if (_shell == null) return;
        float maxScroll = MaxScroll();
        _scrollTarget = VoiceSettingsScrollPolicy.Clamp(_scrollTarget, maxScroll);
        _scroll = VoiceSettingsScrollPolicy.Clamp(_scroll, maxScroll);

        if (!_scrollbarDragging && !_paneDragging)
            _scroll = VoiceSettingsScrollPolicy.Advance(
                _scroll,
                _scrollTarget,
                SmoothScrollRate,
                unscaledDeltaTime);

        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void UpdateScrollbarVisual()
    {
        if (_scrollbarRoot == null || _scrollbarThumb == null) return;
        float maxScroll = MaxScroll();
        bool visible = maxScroll > 0.5f;
        if (_scrollbarRoot.gameObject.activeSelf != visible)
            _scrollbarRoot.gameObject.SetActive(visible);
        if (!visible)
        {
            CancelScrollbarDrag();
            return;
        }

        float trackHeight = Mathf.Max(1f, _scrollbarRoot.rect.height);
        float viewHeight = Mathf.Max(1f, ViewHeight());
        float thumbHeight = VoiceSettingsScrollPolicy.ThumbHeight(
            trackHeight,
            viewHeight,
            _contentHeight,
            ScrollbarMinThumbHeight);
        _scrollbarThumb.sizeDelta = new Vector2(18f, thumbHeight);
        float thumbTop = VoiceSettingsScrollPolicy.ThumbTopFromScroll(
            _scroll,
            maxScroll,
            trackHeight,
            thumbHeight);
        _scrollbarThumb.anchoredPosition = new Vector2(0f, -thumbTop);

        bool hover = VoiceUiKit.Contains(_scrollbarThumb) || VoiceUiKit.Contains(_scrollbarRoot);
        if (_scrollbarThumbImage != null)
        {
            Color target = _scrollbarDragging
                ? VoiceUiKit.Accent
                : hover ? VoiceUiKit.TextPrimary : VoiceUiKit.TextMuted;
            _scrollbarThumbImage.color = Color.Lerp(_scrollbarThumbImage.color, target, 0.24f);
        }
        if (_scrollbarTrackImage != null)
        {
            Color target = hover
                ? new Color32(38, 58, 78, 225)
                : new Color32(26, 39, 55, 205);
            _scrollbarTrackImage.color = Color.Lerp(_scrollbarTrackImage.color, target, 0.20f);
        }
    }

    private static void CancelScrollbarDrag()
    {
        _scrollbarDragging = false;
        _scrollbarDragOffset = 0f;
    }

    private static void HandleInput(bool pointerConsumed)
    {
        if (!Input.GetMouseButton(0))
        {
            if (_activeRow != null) { _activeRow.OnMouseUp(); _activeRow = null; }
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            if (pointerConsumed) return;
            if (_shell == null || !VoiceUiKit.Contains(_shell.PaneClip)) return;
            for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
            _activeRow = FindDragging();
        }
        else if (_activeRow != null)
        {
            _activeRow.OnMouseDrag();
        }
    }

    private static VoiceUiKit.Row? FindDragging()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsDragging) return _rows[i];
        return null;
    }
}

/// <summary>
/// Pure scrolling math shared by the settings panel: clamping, easing and scrollbar
/// geometry stay deterministic so the panel only owns Unity input.
/// </summary>
internal static class VoiceSettingsScrollPolicy
{
    internal static float MaxScroll(float contentHeight, float viewHeight)
        => Math.Max(0f, Sanitize(contentHeight) - Sanitize(viewHeight));

    internal static float Clamp(float value, float maxScroll)
        => Math.Clamp(Sanitize(value), 0f, Math.Max(0f, Sanitize(maxScroll)));

    internal static float Advance(float current, float target, float rate, float deltaTime)
    {
        current = Sanitize(current);
        target = Sanitize(target);
        rate = Math.Max(0f, Sanitize(rate));
        deltaTime = Math.Max(0f, Sanitize(deltaTime));
        if (rate <= 0f || deltaTime <= 0f) return current;
        float blend = 1f - MathF.Exp(-rate * deltaTime);
        float next = current + (target - current) * blend;
        return MathF.Abs(next - target) < 0.05f ? target : next;
    }

    internal static float ThumbHeight(
        float trackHeight,
        float viewHeight,
        float contentHeight,
        float minimumThumbHeight)
    {
        trackHeight = Math.Max(0f, Sanitize(trackHeight));
        viewHeight = Math.Max(1f, Sanitize(viewHeight));
        contentHeight = Math.Max(viewHeight, Sanitize(contentHeight));
        minimumThumbHeight = Math.Clamp(Sanitize(minimumThumbHeight), 0f, trackHeight);
        return Math.Clamp(trackHeight * viewHeight / contentHeight, minimumThumbHeight, trackHeight);
    }

    internal static float ThumbTopFromScroll(
        float scroll,
        float maxScroll,
        float trackHeight,
        float thumbHeight)
    {
        float travel = Math.Max(0f, Sanitize(trackHeight) - Sanitize(thumbHeight));
        maxScroll = Math.Max(0f, Sanitize(maxScroll));
        if (travel <= 0f || maxScroll <= 0f) return 0f;
        return travel * Clamp(scroll, maxScroll) / maxScroll;
    }

    internal static float ScrollFromThumbTop(
        float thumbTop,
        float maxScroll,
        float trackHeight,
        float thumbHeight)
    {
        float travel = Math.Max(0f, Sanitize(trackHeight) - Sanitize(thumbHeight));
        maxScroll = Math.Max(0f, Sanitize(maxScroll));
        if (travel <= 0f || maxScroll <= 0f) return 0f;
        return maxScroll * Math.Clamp(Sanitize(thumbTop) / travel, 0f, 1f);
    }

    private static float Sanitize(float value)
        => float.IsFinite(value) ? value : 0f;
}
