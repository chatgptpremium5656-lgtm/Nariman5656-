using UnityEngine;

namespace Autobahn
{
    // The wanted stars on the driving HUD, top right under the route pill: lit ones in the text
    // colour, flashing while the police search; and the arrest bar while an officer holds us.
    // Drawn by DriveHud inside its 1440 x 900 canvas, right-aligned at `right`.
    public static class WantedHud
    {
        static GUIStyle star, label;

        public static void Draw(float right, float top)
        {
            int stars = Wanted.Stars;
            var force = PoliceForce.Active;
            float arrest = force ? force.ArrestProgress : 0;
            if (stars <= 0 && arrest <= 0) return;
            star ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = Color.white } };
            var tint = GUI.contentColor;
            bool dim = Wanted.Searching && Mathf.Repeat(Time.unscaledTime, .8f) < .4f;
            for (int i = 0; i < 5; i++)
            {
                bool lit = i < stars;
                GUI.contentColor = lit ? UiTheme.Alpha(UiTheme.Text, dim ? .35f : 1) : UiTheme.Alpha(UiTheme.Muted, .4f);
                GUI.Label(new Rect(right - (5 - i) * 34, top, 34, 36), "★", star);
            }
            if (arrest > 0)
            {
                GUI.contentColor = UiTheme.Bad;
                GUI.Label(new Rect(right - 220, top + 38, 220, 16), "АРЕСТ", label);
                var colour = GUI.color;
                GUI.color = UiTheme.Alpha(Color.white, .12f);
                GUI.DrawTexture(new Rect(right - 170, top + 58, 170, 4), Texture2D.whiteTexture);
                GUI.color = UiTheme.Bad;
                GUI.DrawTexture(new Rect(right - 170, top + 58, 170 * Mathf.Clamp01(arrest), 4), Texture2D.whiteTexture);
                GUI.color = colour;
            }
            GUI.contentColor = tint;
        }
    }
}
