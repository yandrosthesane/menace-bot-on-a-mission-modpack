using UnityEngine;

namespace BOAM;

/// <summary>
/// Standalone IMGUI toast notification. Static, works in any scene.
/// Call Show() from anywhere, call OnGUI() from the plugin's OnGUI.
/// </summary>
internal static class Toast
{
    private static string _text;
    private static float _until;
    private static GUIStyle _style;
    private static readonly Color BgColor = new(0.1f, 0.1f, 0.1f, 0.85f);
    private const float Y_OFFSET = 40f;
    private const int FONT_SIZE = 14;

    internal static void Show(string text, float seconds = 3f)
    {
        _text = text;
        _until = Time.realtimeSinceStartup + seconds;
    }

    internal static void OnGUI()
    {
        if (_text != null && Time.realtimeSinceStartup < _until)
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    fontSize = FONT_SIZE,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                _style.normal.textColor = Color.white;
                var tex = new Texture2D(1, 1);
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.SetPixel(0, 0, BgColor);
                tex.Apply();
                _style.normal.background = tex;
                _style.padding = new RectOffset(12, 12, 6, 6);
            }
            var content = new GUIContent(_text);
            var size = _style.CalcSize(content);
            float x = (Screen.width - size.x) * 0.5f;
            GUI.Box(new Rect(x, Y_OFFSET, size.x, size.y), _text, _style);
        }
        else if (_text != null)
        {
            _text = null;
        }
    }
}
