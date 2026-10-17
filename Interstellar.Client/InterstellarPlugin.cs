using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Interstellar.UI;
using Interstellar.Voice;

namespace Interstellar;

[BepInPlugin(Id, "Interstellar Voice Chat", PluginVersion)]
[BepInProcess("Among Us.exe")]
public class InterstellarPlugin : BasePlugin
{
    public const string Id = "com.interstellar.voice";
    public const string PluginVersion = "4.0.0";
    public static ManualLogSource Logger { get; private set; } = null!;

    private const string ResPrefix = "Lib.";
    private static readonly Dictionary<string, Assembly> _asmCache
        = new(StringComparer.OrdinalIgnoreCase);

    static InterstellarPlugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
    }

    private static Assembly? ResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        var shortName = new AssemblyName(args.Name).Name;
        if (shortName == null) return null;
        if (_asmCache.TryGetValue(shortName, out var cached)) return cached;

        var resourceName = ResPrefix + shortName + ".dll";
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var loaded = Assembly.Load(ms.ToArray());
        _asmCache[shortName] = loaded;
        return loaded;
    }

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo("[VC] Loading Interstellar.");

        VoiceConfig.Init(Config);
        TranslationHelper.Load();

        // Register IL2CPP types
        ClassInjector.RegisterTypeInIl2Cpp<VoiceSettingsWindow>();
        ClassInjector.RegisterTypeInIl2Cpp<PublicLobbyWindow>();
        ClassInjector.RegisterTypeInIl2Cpp<PlayerVolumeWindow>();
        ClassInjector.RegisterTypeInIl2Cpp<VoiceUiDriver>();

        var settingsWindow = this.AddComponent<VoiceSettingsWindow>();
        var lobbyWindow = this.AddComponent<PublicLobbyWindow>();
        var playerVolumeWindow = this.AddComponent<PlayerVolumeWindow>();

        _ = settingsWindow;
        _ = lobbyWindow;
        _ = playerVolumeWindow;

        VCManager.RegisterSceneHook();
        InterstellarHudState.Init();

        Harmony harmony = new(Id);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        Logger.LogInfo("[VC] Interstellar loaded build=" + BuildFingerprint() + ".");
    }

    /// <summary>
    /// First 8 hex chars of the running plugin dll's SHA256. Logged at startup so any
    /// pasted log unambiguously shows which build a machine is actually running —
    /// relay fixes are impossible to debug when both machines may be on old binaries.
    /// </summary>
    private static string BuildFingerprint()
    {
        try
        {
            string path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path)) return "unknown";
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = System.IO.File.OpenRead(path);
            byte[] hash = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(8);
            for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("X2"));
            return sb.ToString();
        }
        catch { return "unknown"; }
    }
}
