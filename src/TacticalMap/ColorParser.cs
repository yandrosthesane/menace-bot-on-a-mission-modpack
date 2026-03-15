// Hex color parser: converts #RRGGBB or #RRGGBBAA strings to UnityEngine.Color.

using UnityEngine;

namespace BOAM.TacticalMap;

internal static class ColorParser
{
    internal static Color ParseHex(string hex)
    {
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        byte red   = ParseHexByte(hex, 0);
        byte green = ParseHexByte(hex, 2);
        byte blue  = ParseHexByte(hex, 4);
        byte alpha = hex.Length >= 8 ? ParseHexByte(hex, 6) : (byte)255;
        return new Color(red / 255f, green / 255f, blue / 255f, alpha / 255f);
    }

    private static int HexVal(char hexChar) =>
        hexChar >= '0' && hexChar <= '9' ? hexChar - '0' :
        hexChar >= 'a' && hexChar <= 'f' ? hexChar - 'a' + 10 :
        hexChar >= 'A' && hexChar <= 'F' ? hexChar - 'A' + 10 : 0;

    private static byte ParseHexByte(string hexString, int offset) =>
        (byte)(HexVal(hexString[offset]) * 16 + HexVal(hexString[offset + 1]));
}
