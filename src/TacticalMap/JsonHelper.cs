// JSON5 mini-parser: strip // and /* */ comments, then read scalar, object,
// or array values by key. No dependencies beyond System.

using System;
using System.Collections.Generic;

namespace BOAM.TacticalMap;

internal static class JsonHelper
{
    internal static string StripJsonComments(string input)
    {
        var result = new System.Text.StringBuilder(input.Length);
        int position = 0;
        while (position < input.Length)
        {
            if (input[position] == '"')
            {
                result.Append('"');
                position++;
                while (position < input.Length && input[position] != '"')
                {
                    if (input[position] == '\\' && position + 1 < input.Length)
                    { result.Append(input[position]); result.Append(input[position + 1]); position += 2; }
                    else
                    { result.Append(input[position]); position++; }
                }
                if (position < input.Length) { result.Append('"'); position++; }
            }
            else if (position + 1 < input.Length && input[position] == '/' && input[position + 1] == '/')
            {
                while (position < input.Length && input[position] != '\n') position++;
            }
            else if (position + 1 < input.Length && input[position] == '/' && input[position + 1] == '*')
            {
                position += 2;
                while (position + 1 < input.Length && !(input[position] == '*' && input[position + 1] == '/')) position++;
                if (position + 1 < input.Length) position += 2;
            }
            else
            {
                result.Append(input[position]); position++;
            }
        }
        return result.ToString();
    }

    internal static List<string> SplitJsonArray(string json)
    {
        var elements = new List<string>();
        int arrayStart = json.IndexOf('[');
        if (arrayStart < 0) return elements;

        int braceDepth = 0;
        int objectStart = -1;
        for (int position = arrayStart + 1; position < json.Length; position++)
        {
            if (json[position] == '"')
            {
                position++;
                while (position < json.Length && json[position] != '"')
                {
                    if (json[position] == '\\') position++;
                    position++;
                }
            }
            else if (json[position] == '{')
            {
                if (braceDepth == 0) objectStart = position;
                braceDepth++;
            }
            else if (json[position] == '}')
            {
                braceDepth--;
                if (braceDepth == 0 && objectStart >= 0)
                {
                    elements.Add(json.Substring(objectStart, position - objectStart + 1));
                    objectStart = -1;
                }
            }
            else if (json[position] == ']' && braceDepth == 0)
                break;
        }
        return elements;
    }

    internal static bool HasKey(string json, string key)
    {
        return json.IndexOf($"\"{key}\"", StringComparison.Ordinal) >= 0;
    }

    internal static string ReadString(string json, string key)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int colonIndex = json.IndexOf(':', keyIndex + needle.Length);
        if (colonIndex < 0) return null;
        int openQuote = json.IndexOf('"', colonIndex + 1);
        if (openQuote < 0) return null;
        int closeQuote = json.IndexOf('"', openQuote + 1);
        if (closeQuote < 0) return null;
        return json.Substring(openQuote + 1, closeQuote - openQuote - 1);
    }

    internal static bool ReadBool(string json, string key, bool fallback)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return fallback;
        int colonIndex = json.IndexOf(':', keyIndex + needle.Length);
        if (colonIndex < 0) return fallback;
        var valueText = json.Substring(colonIndex + 1).TrimStart();
        if (valueText.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (valueText.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    internal static int ReadInt(string json, string key, int fallback)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return fallback;
        int colonIndex = json.IndexOf(':', keyIndex + needle.Length);
        if (colonIndex < 0) return fallback;
        var valueText = json.Substring(colonIndex + 1).TrimStart();
        int numberEnd = 0;
        while (numberEnd < valueText.Length && (char.IsDigit(valueText[numberEnd]) || valueText[numberEnd] == '-'))
            numberEnd++;
        if (numberEnd == 0) return fallback;
        return int.TryParse(valueText.Substring(0, numberEnd), out var parsedValue) ? parsedValue : fallback;
    }

    internal static float ReadFloat(string json, string key, float fallback)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return fallback;
        int colonIndex = json.IndexOf(':', keyIndex + needle.Length);
        if (colonIndex < 0) return fallback;
        var valueText = json.Substring(colonIndex + 1).TrimStart();
        int numberEnd = 0;
        while (numberEnd < valueText.Length && (char.IsDigit(valueText[numberEnd]) || valueText[numberEnd] == '-' || valueText[numberEnd] == '.'))
            numberEnd++;
        if (numberEnd == 0) return fallback;
        return float.TryParse(valueText.Substring(0, numberEnd), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : fallback;
    }

    internal static string ReadObject(string json, string key)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int braceStart = json.IndexOf('{', keyIndex + needle.Length);
        if (braceStart < 0) return null;

        int braceDepth = 0;
        for (int position = braceStart; position < json.Length; position++)
        {
            if (json[position] == '"')
            {
                position++;
                while (position < json.Length && json[position] != '"')
                {
                    if (json[position] == '\\') position++;
                    position++;
                }
            }
            else if (json[position] == '{') braceDepth++;
            else if (json[position] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return json.Substring(braceStart, position - braceStart + 1);
            }
        }
        return null;
    }

    internal static string ReadArray(string json, string key)
    {
        var needle = $"\"{key}\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int bracketStart = json.IndexOf('[', keyIndex + needle.Length);
        if (bracketStart < 0) return null;

        int bracketDepth = 0;
        for (int position = bracketStart; position < json.Length; position++)
        {
            if (json[position] == '"')
            {
                position++;
                while (position < json.Length && json[position] != '"')
                {
                    if (json[position] == '\\') position++;
                    position++;
                }
            }
            else if (json[position] == '[') bracketDepth++;
            else if (json[position] == ']')
            {
                bracketDepth--;
                if (bracketDepth == 0)
                    return json.Substring(bracketStart, position - bracketStart + 1);
            }
        }
        return null;
    }

    internal static List<KeyValuePair<string, string>> ReadAllStringPairs(string json)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        int position = json.IndexOf('{');
        if (position < 0) return pairs;
        position++;

        while (position < json.Length)
        {
            int keyOpenQuote = json.IndexOf('"', position);
            if (keyOpenQuote < 0) break;
            int keyCloseQuote = json.IndexOf('"', keyOpenQuote + 1);
            if (keyCloseQuote < 0) break;
            var key = json.Substring(keyOpenQuote + 1, keyCloseQuote - keyOpenQuote - 1);

            int colonIndex = json.IndexOf(':', keyCloseQuote + 1);
            if (colonIndex < 0) break;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;

            if (valueStart < json.Length && json[valueStart] == '"')
            {
                int valueCloseQuote = json.IndexOf('"', valueStart + 1);
                if (valueCloseQuote < 0) break;
                pairs.Add(new KeyValuePair<string, string>(key, json.Substring(valueStart + 1, valueCloseQuote - valueStart - 1)));
                position = valueCloseQuote + 1;
            }
            else
            {
                position = valueStart + 1;
            }
        }
        return pairs;
    }
}
