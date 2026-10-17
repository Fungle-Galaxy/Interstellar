using System.Collections;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static Interstellar.Voice.TranslationHelper;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
///     Shows the Interstellar Voice Chat logo as a transition animation
///     when the main menu first loads. Runs AFTER COG's splash screen
///     (if present), avoiding any Harmony conflicts.
/// </summary>
[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
public static class SplashScreenPatch
{
    private static bool _hasShown;

    public static void Prefix(MainMenuManager __instance)
    {
        if (_hasShown) return;
        _hasShown = true;
        __instance.StartCoroutine(ShowSplash().WrapToIl2Cpp());
    }

    private static Sprite? _logoSprite;

    private static Sprite LoadLogoSprite()
    {
        if (_logoSprite != null) return _logoSprite;

        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Interstellar.Resources.Logo.png")!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);

            _logoSprite = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 175f);
            _logoSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return _logoSprite;
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Failed to load logo sprite: {e}");
            return null!;
        }
    }

    private static IEnumerator ShowSplash()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            InterstellarPlugin.Logger.LogWarning("[VC] No main camera for splash screen.");
            yield break;
        }

        var overlay = new GameObject("VC_SplashOverlay");
        overlay.transform.SetParent(cam.transform, false);
        overlay.transform.localPosition = new Vector3(0f, 0f, 3f);
        overlay.transform.localRotation = Quaternion.identity;
        overlay.transform.localScale = new Vector3(30f, 30f, 1f);

        var bgSr = overlay.AddComponent<SpriteRenderer>();
        bgSr.sprite = CreateSolidSprite();
        bgSr.sortingOrder = 32766;
        bgSr.color = Color.clear;

        var logoGo = new GameObject("VC_SplashLogo");
        logoGo.transform.SetParent(cam.transform, false);
        logoGo.transform.localPosition = new Vector3(0f, 0.4f, 3.1f);
        logoGo.transform.localRotation = Quaternion.identity;

        var logoSr = logoGo.AddComponent<SpriteRenderer>();
        logoSr.sprite = LoadLogoSprite();
        if (logoSr.sprite == null)
        {
            Object.Destroy(overlay);
            Object.Destroy(logoGo);
            yield break;
        }
        logoSr.color = Color.clear;
        logoSr.sortingOrder = 32767;

        var textGo = new GameObject("VC_SplashText");
        textGo.transform.SetParent(cam.transform, false);
        textGo.transform.localPosition = new Vector3(0f, -2f, 3.1f);
        textGo.transform.localRotation = Quaternion.identity;

        var textTmp = textGo.AddComponent<TextMeshPro>();
        textTmp.text = Get("vc.splash.title", "Interstellar Voice Chat") + $" v{InterstellarPlugin.PluginVersion}" + "\n<size=70%>"
            + Get("vc.splash.madeBy", "Made by") + " <color=#8A2BE2>Fungle-Galaxy</color>";
        textTmp.fontSize = 2.5f;
        textTmp.fontStyle = FontStyles.Bold;
        textTmp.alignment = TextAlignmentOptions.Center;
        textTmp.color = new Color(1f, 1f, 1f, 0f);
        textTmp.sortingOrder = 32767;

        // Fade in (0.5s)
        const float fadeInDuration = 0.5f;
        var elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            var t = Mathf.SmoothStep(0f, 1f, elapsed / fadeInDuration);
            bgSr.color = Color.Lerp(Color.clear, new Color(0f, 0f, 0f, 0.92f), t);
            logoSr.color = Color.Lerp(Color.clear, Color.white, t);
            textTmp.color = Color.Lerp(new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 0.7f), t);
            yield return null;
        }

        bgSr.color = new Color(0f, 0f, 0f, 0.92f);
        logoSr.color = Color.white;
        textTmp.color = new Color(1f, 1f, 1f, 0.7f);

        // Hold (1.5s)
        yield return new WaitForSeconds(1.5f);

        // Fade out (1.0s)
        const float fadeOutDuration = 1.0f;
        elapsed = 0f;
        var startBg = bgSr.color;
        var startLogo = logoSr.color;
        var startText = textTmp.color;

        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            var t = Mathf.SmoothStep(0f, 1f, elapsed / fadeOutDuration);
            bgSr.color = Color.Lerp(startBg, Color.clear, t);
            logoSr.color = Color.Lerp(startLogo, Color.clear, t);
            textTmp.color = Color.Lerp(startText, new Color(1f, 1f, 1f, 0f), t);
            yield return null;
        }

        Object.Destroy(overlay);
        Object.Destroy(logoGo);
        Object.Destroy(textGo);
    }

    private static Sprite? _solidSprite;

    private static Sprite CreateSolidSprite()
    {
        if (_solidSprite != null) return _solidSprite;

        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        _solidSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1),
            new Vector2(0.5f, 0.5f), 1f);
        _solidSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
        return _solidSprite;
    }
}
