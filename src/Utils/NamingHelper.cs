using System.Collections.Generic;

namespace BOAM.Utils;

/// <summary>
/// Shared naming utilities for unit labels and icon file resolution.
/// Used by both the minimap overlay and heatmap renderer.
/// </summary>
internal static class NamingHelper
{
    /// <summary>
    /// Strip faction prefix, trailing instance number, common prefixes, and title-case.
    /// "player.carda" → "Carda"
    /// "wildlife.alien_01_big_warrior_young.1" → "Warrior Young"
    /// "enemy_local.construct_scarecrow.2" → "Scarecrow"
    /// </summary>
    internal static string ShortLabel(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return "";
        // Remove faction prefix (everything up to and including first dot)
        int firstDot = uuid.IndexOf('.');
        var name = firstDot >= 0 ? uuid.Substring(firstDot + 1) : uuid;
        // Remove trailing .N instance number
        int lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && int.TryParse(name.Substring(lastDot + 1), out _))
            name = name.Substring(0, lastDot);
        // Strip common prefixes: alien_, construct_, rogue_, allied_, enemy_
        foreach (var prefix in new[] { "alien_", "construct_", "rogue_", "allied_", "enemy_" })
            if (name.StartsWith(prefix))
                name = name.Substring(prefix.Length);
        // Strip numeric prefixes like "01_", "02_"
        while (name.Length > 2 && char.IsDigit(name[0]) && char.IsDigit(name[1]) && name[2] == '_')
            name = name.Substring(3);
        // Replace underscores with spaces, drop noise words, title-case
        var words = name.Split('_');
        var filtered = new List<string>();
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            var lower = w.ToLower();
            if (lower == "big" || lower == "small") continue;
            filtered.Add(char.ToUpper(w[0]) + w.Substring(1));
        }
        return string.Join(" ", filtered);
    }

    /// <summary>Extract template short name: "enemy.alien_stinger" → "alien_stinger"</summary>
    internal static string TemplateFileName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "";
        int dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName.Substring(dot + 1);
    }

    /// <summary>Faction index to icon filename.</summary>
    internal static string FactionIconName(int factionIdx) => factionIdx switch
    {
        0 => "neutral", 1 => "player", 2 => "playerai", 3 => "civilian",
        4 => "allied", 5 => "enemy_local", 6 => "pirates", 7 => "wildlife",
        8 => "constructs", 9 => "rogue_army", _ => "neutral"
    };
}
