#pragma warning disable CS8602, CS8603, CS8618
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

/// <summary>
/// Thin facade over <see cref="Interstellar.UI.VoiceSettingsPanel"/> (Perfect-Comms style).
/// The old 900x900 IMGUI-style window is gone: this component only owns the hotkeys,
/// the open/close choreography and the legacy <c>ShowWindow</c> flag other windows read.
/// </summary>
public class VoiceSettingsWindow : MonoBehaviour
{
    public VoiceSettingsWindow(System.IntPtr ptr) : base(ptr) { }

    public static VoiceSettingsWindow? Instance { get; private set; }

    /// <summary>Live open state of the settings panel (kept for legacy callers).</summary>
    public bool ShowWindow => Interstellar.UI.VoiceSettingsPanel.IsOpen;

    private bool _isAndroid => Application.platform == RuntimePlatform.Android;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        try { Interstellar.UI.VoiceSettingsPanel.Hide(); } catch { }
    }

    void Update()
    {
        // The menu chips are plain objects, not MonoBehaviours: drive them from here,
        // which runs every frame in every scene.
        VoiceChipEntries.TickAll();

        if (!Interstellar.UI.VoiceUiKit.SuppressGlobalHotkeys && VoiceConfig.ChordDown(VoiceChord.SettingsPanel)) Toggle();
        // While a rebind row captures, Esc belongs to the capture: it cancels the
        // capture and never closes the panel — including on the frame it ends.
        if (ShowWindow && !Interstellar.UI.VoiceUiKit.SuppressGlobalHotkeys
            && Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    public void Toggle()
    {
        if (ShowWindow) Close(); else Open();
    }

    /// <summary>Opens the panel on a given rail category — what F2 (public lobby) and
    /// F3 (player volume) call now that neither owns a window any more.</summary>
    public void OpenCategory(int category)
    {
        if (!ShowWindow) Open();
        Interstellar.UI.VoiceSettingsPanel.SelectCategory(category);
    }

    public void Open()
    {
        try
        {
            VCUiKit.AnyWindowDragging = false;

            try { VoiceConfig.RefreshDeviceCaches(true); } catch { }

            if (!_isAndroid)
            {
                var opt = Object.FindObjectOfType<OptionsMenuBehaviour>();
                if (opt) { try { opt.Close(); } catch { } }
            }



            Interstellar.UI.VoiceSettingsPanel.Show();
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger?.LogError($"[VC] Open settings failed: {e}");
            try { Interstellar.UI.VoiceSettingsPanel.Hide(); } catch { }
        }
    }

    public void Close()
    {
        VCUiKit.AnyWindowDragging = false;
        Interstellar.UI.VoiceSettingsPanel.Hide();
        VCDropdown.Hide();
    }
}
