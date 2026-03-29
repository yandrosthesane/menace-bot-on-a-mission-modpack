using System.Collections.Generic;

namespace BOAM.Boundary;

/// <summary>
/// Shared untyped store for C# game event state.
/// Events write named values, other events or systems read them.
/// Cleared on battle end.
/// </summary>
internal static class GameStore
{
    private static readonly Dictionary<string, object> _store = new();

    internal static void Write<T>(string key, T value) => _store[key] = value;

    internal static T Read<T>(string key, T fallback = default)
    {
        if (_store.TryGetValue(key, out var val) && val is T typed)
            return typed;
        return fallback;
    }

    internal static bool Has(string key) => _store.ContainsKey(key);

    internal static void Remove(string key) => _store.Remove(key);

    internal static void Clear() => _store.Clear();
}
