using System;
using System.IO;
using MelonLoader;
using static BOAM.TacticalMap.JsonHelper;

namespace BOAM.Boundary;

/// <summary>
/// Two-tier config resolution: user persistent config → mod default.
/// Seeds missing user configs from mod defaults on first run.
/// </summary>
internal static class ConfigResolver
{
    /// <summary>
    /// Resolve a config file: pick user config if it exists and has configVersion >= mod default.
    /// Seeds the user config from mod default if missing.
    /// </summary>
    internal static string Resolve(string userPath, string defaultPath, string label, MelonLogger.Instance log)
    {
        // Seed user config from mod default if missing
        if (!File.Exists(userPath) && File.Exists(defaultPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(userPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(defaultPath, userPath);
                log.Msg($"[BOAM] Seeded user {label}: {userPath}");
            }
            catch (Exception ex)
            {
                log.Warning($"[BOAM] Failed to seed {label}: {ex.Message}");
                return defaultPath;
            }
        }

        if (!File.Exists(userPath))
            return defaultPath;

        int userVersion = ReadConfigVersion(userPath);
        int defaultVersion = File.Exists(defaultPath) ? ReadConfigVersion(defaultPath) : 0;

        if (userVersion >= defaultVersion)
        {
            log.Msg($"[BOAM] Using user {label} (v{userVersion}): {userPath}");
            return userPath;
        }

        log.Warning($"[BOAM] User {label} outdated (v{userVersion} < v{defaultVersion}), using mod default. Update: {userPath}");
        return defaultPath;
    }

    /// <summary>
    /// Read the configVersion field from a JSON5 config file.
    /// Returns 0 if the file can't be read or the field is missing.
    /// </summary>
    internal static int ReadConfigVersion(string path)
    {
        try
        {
            var json = StripJsonComments(File.ReadAllText(path));
            return ReadInt(json, "configVersion", 0);
        }
        catch { return 0; }
    }
}
