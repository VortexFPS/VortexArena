// Port of qcsrc/menu/xonotic/crosshairpreview.qc (XonoticCrosshairPreview).
using System;
using System.Globalization;
using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The crosshair preview — a faithful C# port of <c>XonoticCrosshairPreview</c>
/// (qcsrc/menu/xonotic/crosshairpreview.qc): draws <c>gfx/crosshair{crosshair}</c> centered, scaled by
/// <c>crosshair_size</c>, tinted <c>crosshair_color</c> at <c>crosshair_alpha</c>; when <c>crosshair_dot</c> is
/// set it also draws <c>gfx/crosshairdot</c> (using <c>crosshair_dot_color</c> when
/// <c>crosshair_dot_color_custom</c> and the value isn't "0"), at <c>crosshair_dot_size</c> and
/// <c>alpha × crosshair_dot_alpha</c>. Live-updates as the bound cvars change.
/// </summary>
public partial class CrosshairPreview : Control
{
    private static CvarService Cvars => MenuState.Cvars;

    public CrosshairPreview()
    {
        CustomMinimumSize = new Vector2(0, 84);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _EnterTree() => Cvars.Changed += OnCvarChanged;
    public override void _ExitTree() => Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (name.StartsWith("crosshair"))
            QueueRedraw();
    }

    public override void _Draw()
    {
        string ix = Cvars.GetString("crosshair");
        Texture2D? tex = MenuSkin.Image($"gfx/crosshair{ix}");
        Color rgb = ParseRgb(Cvars.GetString("crosshair_color"), new Color(0.6f, 0.8f, 1f));
        float a = Cvars.GetFloat("crosshair_alpha");
        if (a <= 0f) a = 0.8f;
        Vector2 center = Size * 0.5f;

        if (tex is not null)
        {
            float size = Cvars.GetFloat("crosshair_size");
            if (size <= 0f) size = 0.4f;
            // QC scales the picture size by crosshair_size; the asset is large, so scale relative to the cell.
            Vector2 baseSz = tex.GetSize();
            float fit = baseSz.Y > 0 ? MathF.Min(Size.X, Size.Y) / baseSz.Y : 1f;
            Vector2 sz = baseSz * fit * size;
            DrawTextureRect(tex, new Rect2(center - sz * 0.5f, sz), false, new Color(rgb, a));

            if (Cvars.GetFloat("crosshair_dot") != 0f && MenuSkin.Image("gfx/crosshairdot") is { } dot)
            {
                Color dotRgb = rgb;
                if (Cvars.GetFloat("crosshair_dot_color_custom") != 0f
                    && Cvars.GetString("crosshair_dot_color") != "0")
                    dotRgb = ParseRgb(Cvars.GetString("crosshair_dot_color"), new Color(1f, 0f, 0f));
                float ds = Cvars.GetFloat("crosshair_dot_size");
                if (ds <= 0f) ds = 0.6f;
                float da = Cvars.GetFloat("crosshair_dot_alpha");
                if (da <= 0f) da = 1f;
                Vector2 dsz = sz * ds;
                DrawTextureRect(dot, new Rect2(center - dsz * 0.5f, dsz), false, new Color(dotRgb, a * da));
            }
        }
        else
        {
            var font = ThemeDB.FallbackFont;
            DrawString(font, center + new Vector2(-6, 7), "+", HorizontalAlignment.Center, -1, 24, new Color(rgb, a));
        }
    }

    /// <summary>Parse an "r g b" triplet (0..1 components), defaulting to <paramref name="fallback"/>.</summary>
    private static Color ParseRgb(string s, Color fallback)
    {
        string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            return new Color(r, g, b);
        return fallback;
    }
}
