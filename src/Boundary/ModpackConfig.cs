using System;
using System.IO;
using MelonLoader;
using static BOAM.TacticalMap.JsonHelper;

namespace BOAM.Boundary;

/// <summary>
/// Modpack-side config — loaded independently from the tactical engine.
/// Uses the same two-tier resolution as other configs (user persistent → mod default).
/// </summary>
internal static class ModpackConfig
{
    internal static void Load(string modFolder, MelonLogger.Instance log)
    {
        var persistentDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "BOAM");

        var userPath = Path.Combine(persistentDir, "configs", "modpack.json5");
        var defaultPath = Path.Combine(modFolder, "configs", "modpack.json5");

        var configPath = ConfigResolver.Resolve(userPath, defaultPath, "modpack config", log);

        try
        {
            var json = StripJsonComments(File.ReadAllText(configPath));
            log.Msg($"[BOAM] Modpack config loaded: {configPath}");
        }
        catch (Exception ex)
        {
            log.Warning($"[BOAM] Failed to load modpack config: {ex.Message}");
        }
    }
}
