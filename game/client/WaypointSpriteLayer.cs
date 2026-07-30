using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay.Waypoints;
using VortexArena.Common.Services;
using VortexArena.Game.Hud;   // HudSkin, TextureCache, HudPanel.HudFont
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The 3D in-world waypoint sprites (client draw) — the C# port of QuakeC's <c>Draw_WaypointSprite</c>
/// (common/mutators/mutator/waypoints/waypointsprites.qc). For each networked waypoint
/// (<see cref="ClientNet.Waypoints"/>) it projects the world position through the first-person
/// <see cref="Camera3D"/> (QC <c>project_3d_to_2d</c>) and draws the floating marker: the objective ICON
/// (<c>gfx/hud/&lt;skin&gt;/&lt;icon&gt;</c>) or, when the def has none, its TEXT ("Flag", "Here", "Danger",
/// "… needing help!"); a directional ARROW clamped to the screen edge when the objective is off-screen or
/// behind the camera; an optional HEALTH BAR (Assault objectives / FreezeTag revival / generators); and the
/// lifetime / max-distance / helpme fades + blink. Modeled on <see cref="DamageTextLayer"/> (the existing
/// world→screen overlay). A plain <see cref="Control"/> that never eats input.
/// </summary>
public partial class WaypointSpriteLayer : Control
{
    /// <summary>The host's first-person camera, for projecting world positions to screen (QC project_3d_to_2d).</summary>
    public Camera3D? Camera { get; set; }

    /// <summary>Supplies the live per-frame waypoint list (the host wires this to <c>ClientNet.Waypoints</c>).</summary>
    public Func<IReadOnlyList<WaypointNet>>? Source { get; set; }

    /// <summary>QC <c>waypointsprite_fadedistance = vlen(mi_scale)</c> (the BSP worldspawn extent length), set by
    /// the host from the map bounds. Scales the distance-fade ramp distance. 0 (unset/headless) disables distance-fade.</summary>
    public float MapSize { get; set; }

    // QC autocvar defaults (xonotic-client.cfg:502-560) — the fallbacks used when the cvar store isn't wired
    // (headless tests). The port keeps its 'gently smaller than QC' icon/font defaults only as the fallback;
    // the live g_waypointsprite_iconsize/fontsize values (32/12) take over when the store is present.
    private const float DefEdgeInset = 0.06f; // g_waypointsprite_edgeoffset_*
    private const float DefIconSize = 24f;    // g_waypointsprite_iconsize fallback (QC default is 32)
    private const int DefFontSize = 13;       // g_waypointsprite_fontsize fallback (QC default is 12)

    // ---- live g_waypointsprite_* / cl_hidewaypoints reads (QC WaypointSprite_Load + Draw_WaypointSprite) ----
    private static bool HasCvars => VortexArena.Common.Services.Api.Services is not null;
    private static float CvarF(string name, float fallback)
        => HasCvars ? VortexArena.Common.Services.Api.Cvars.GetFloat(name) : fallback;
    private static bool CvarOn(string name) => HasCvars && VortexArena.Common.Services.Api.Cvars.GetFloat(name) != 0f;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore; // QC hud_cursormode off

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (Camera is null || Source is null)
            return;
        IReadOnlyList<WaypointNet> list = Source();
        if (list.Count == 0)
            return;

        // QC autocvar_cl_hidewaypoints >= 2 hides ALL in-world waypoints (radar only); == 1 hides only the
        // hideflag&1 "fixed/static" waypoints (Hideable here), keeping team/objective markers.
        int hideWaypoints = (int)CvarF("cl_hidewaypoints", 0f);
        if (hideWaypoints >= 2)
            return;

        // QC WaypointSprite_Load: g_waypointsprite_alpha/scale/iconsize/fontsize/edgeoffset_* + g_waypointsprite_text/uppercase.
        float globalAlpha = HasCvars ? Math.Clamp(CvarF("g_waypointsprite_alpha", 1f), 0f, 1f) : 1f;
        // QC: t = waypointsprite_scale is the BASE per-sprite scale; it's faded per-sprite (ws below) and applied
        // to the icon/font/arrow/healthbar there, so the base iconSize/fontSize here stay UNSCALED.
        float scale = HasCvars ? MathF.Max(0f, CvarF("g_waypointsprite_scale", 1f)) : 1f;
        float iconSize = HasCvars ? CvarF("g_waypointsprite_iconsize", DefIconSize) : DefIconSize;
        float fontSize = HasCvars ? CvarF("g_waypointsprite_fontsize", DefFontSize) : DefFontSize;
        bool forceText = CvarOn("g_waypointsprite_text");        // always show text instead of icons
        bool uppercase = HasCvars ? CvarOn("g_waypointsprite_uppercase") : true; // QC default 1
        float edgeL = HasCvars ? CvarF("g_waypointsprite_edgeoffset_left", DefEdgeInset) : DefEdgeInset;
        float edgeT = HasCvars ? CvarF("g_waypointsprite_edgeoffset_top", DefEdgeInset) : DefEdgeInset;
        float edgeR = HasCvars ? CvarF("g_waypointsprite_edgeoffset_right", DefEdgeInset) : DefEdgeInset;
        float edgeB = HasCvars ? CvarF("g_waypointsprite_edgeoffset_bottom", DefEdgeInset) : DefEdgeInset;

        // QC WaypointSprite_Load distance/edge/crosshair-fade cvars (xonotic-client.cfg defaults).
        float distFadeAlpha = HasCvars ? CvarF("g_waypointsprite_distancefadealpha", 1f) : 1f;
        float distFadeScale = HasCvars ? CvarF("g_waypointsprite_distancefadescale", 0.7f) : 0.7f;
        // distancefadedistance = mapsize * g_waypointsprite_distancefadedistancemultiplier (default 0.5).
        float distFadeDist = MapSize * (HasCvars ? CvarF("g_waypointsprite_distancefadedistancemultiplier", 0.5f) : 0.5f);
        float edgeFadeAlpha = HasCvars ? CvarF("g_waypointsprite_edgefadealpha", 0.5f) : 0.5f;
        float edgeFadeScale = HasCvars ? CvarF("g_waypointsprite_edgefadescale", 1f) : 1f;
        float edgeFadeDist = HasCvars ? CvarF("g_waypointsprite_edgefadedistance", 50f) : 50f;
        float xhairFadeAlpha = HasCvars ? CvarF("g_waypointsprite_crosshairfadealpha", 0.25f) : 0.25f;
        float xhairFadeScale = HasCvars ? CvarF("g_waypointsprite_crosshairfadescale", 1f) : 1f;
        float xhairFadeDist = HasCvars ? CvarF("g_waypointsprite_crosshairfadedistance", 150f) : 150f;

        Vector2 vp = GetViewportRect().Size;
        Vector2 ctr = vp * 0.5f;
        float l = vp.X * edgeL, t = vp.Y * edgeT, r = vp.X - vp.X * edgeR, b = vp.Y - vp.Y * edgeB;
        NVec3 camQ = Coords.ToQuake(Camera.GlobalPosition);
        float now = NowSec();
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        Font bold = HudSkin.BoldFont ?? font;

        foreach (WaypointNet wp in list)
        {
            if (!float.IsFinite(wp.Origin.X) || !float.IsFinite(wp.Origin.Y) || !float.IsFinite(wp.Origin.Z))
                continue;
            // QC: (hideflags & 1) && autocvar_cl_hidewaypoints → hide the fixed/static (Hideable) waypoints.
            if (hideWaypoints >= 1 && wp.Hideable)
                continue;
            WaypointDef def = WaypointRegistry.Get(wp.SpriteName);

            // ---- alpha: lifetime fade × max-distance fade × blink/helpme × g_waypointsprite_alpha (QC Draw_WaypointSprite) ----
            float dx = wp.Origin.X - camQ.X, dy = wp.Origin.Y - camQ.Y, dz = wp.Origin.Z - camQ.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float a = Math.Clamp(wp.Fade, 0f, 1f);
            if (wp.MaxDistance > 0f)
            {
                if (dist >= wp.MaxDistance) continue;                       // QC max-distance cull
                float norm = MathF.Min(512f, wp.MaxDistance - 1f);
                a *= MathF.Pow(Math.Clamp((wp.MaxDistance - dist) / MathF.Max(1f, wp.MaxDistance - norm), 0f, 1f), 2f);
            }

            // QC blink: blink_time = (health>=0) ? health*10 : time. In the BRIGHT half-cycle multiply alpha — by
            // SPRITE_HELPME_BLINK (2) for a helpme flash, else (for non-fading waypoints) by the def's own blink.
            float blinkTime = wp.Health >= 0f ? wp.Health * 10f : now;
            if (blinkTime - MathF.Floor(blinkTime) > 0.5f)
            {
                if (wp.Helpme > 0f)
                    a *= 2f;                                                 // SPRITE_HELPME_BLINK
                else if (wp.Fade >= 1f)                                     // QC: fading-out waypoints don't blink (!lifetime proxy)
                    a *= def.Blink;
            }
            if (a > 1f) a = 1f;                                             // QC: a>1 over-bright clamps to 1 (port keeps def color)
            if (a <= 0.003f)
                continue;

            // ---- project to screen; edge-clamp + arrow when off-screen / behind (QC the edgeoffset block) ----
            Vector3 g = Coords.ToGodot(wp.Origin + new NVec3(0f, 0f, 8f)); // tiny lift so it floats above the marker
            bool behind = Camera.IsPositionBehind(g);
            Vector2 sp = behind ? ctr : Camera.UnprojectPosition(g);
            bool edge = behind || sp.X < l || sp.Y < t || sp.X > r || sp.Y > b;
            float ang = 0f;
            if (edge)
            {
                Vector2 d = behind ? ctr - sp : sp - ctr;
                if (d.LengthSquared() < 1e-4f) d = new Vector2(0f, -1f);
                ang = MathF.Atan2(d.Y, d.X);
                sp = ClampToRect(ctr, d, l, t, r, b);
            }

            // ---- scale (t) + distance / edge / crosshair fades (QC Draw_WaypointSprite lines 629-643) ----
            float ws = scale;                       // QC: t = waypointsprite_scale (per-sprite scale, faded below)
            a *= globalAlpha;                       // QC: a *= waypointsprite_alpha
            // distance-fade: ramps alpha+scale down as the objective recedes (0 at distancefadedistance).
            if (distFadeDist > 0f)
            {
                float k = Math.Clamp(dist / distFadeDist, 0f, 1f);
                a  *= 1f - (1f - distFadeAlpha) * k;
                ws *= 1f - (1f - distFadeScale) * k;
            }
            // edge-fade: dims the marker as it nears the inset screen edge (edgedistance_min < edgefadedistance).
            float edgeDistMin = MathF.Min(MathF.Min(sp.Y - t, sp.X - l), MathF.Min(r - sp.X, b - sp.Y));
            if (edgeFadeDist > 0f && edgeDistMin < edgeFadeDist)
            {
                float k = 1f - Math.Clamp(edgeDistMin / edgeFadeDist, 0f, 1f);
                a  *= 1f - (1f - edgeFadeAlpha) * k;
                ws *= 1f - (1f - edgeFadeScale) * k;
            }
            // crosshair-fade: dims the marker as it nears the screen center / crosshair.
            float xhairDist = (sp - ctr).Length();
            if (xhairFadeDist > 0f && xhairDist < xhairFadeDist)
            {
                float k = 1f - Math.Clamp(xhairDist / xhairFadeDist, 0f, 1f);
                a  *= 1f - (1f - xhairFadeAlpha) * k;
                ws *= 1f - (1f - xhairFadeScale) * k;
            }
            if (a <= 0.003f)
                continue;
            ws = MathF.Max(0f, ws);
            float iconSz = iconSize * ws;
            int fontSz = Math.Max(1, (int)MathF.Round(fontSize * ws));

            Color col = new(def.Color.X, def.Color.Y, def.Color.Z, a);

            if (edge)
                DrawArrow(sp, ang, col, ws);

            // ---- icon or text (QC: icon if the def has one AND !g_waypointsprite_text, else the localized text) ----
            Texture2D? icon = (forceText || string.IsNullOrEmpty(def.Icon))
                ? null
                : TextureCache.GetFirst($"gfx/hud/{HudSkin.SkinName}/{def.Icon}", $"gfx/hud/default/{def.Icon}");

            float barY;
            if (icon is not null)
            {
                var ir = new Rect2(sp - new Vector2(iconSz, iconSz) * 0.5f, new Vector2(iconSz, iconSz));
                DrawTextureRect(icon, ir, false, new Color(1f, 1f, 1f, a)); // QC iconcolor 0 → white icon
                barY = ir.Position.Y - 6f;
            }
            else
            {
                string txt = def.Text;
                if (uppercase) txt = txt.ToUpperInvariant(); // QC g_waypointsprite_uppercase
                if (wp.Helpme > 0f) txt = txt + "!";
                DrawCenteredText(bold, sp, txt, col, fontSz);
                barY = sp.Y - fontSz - 6f;
            }

            // ---- health bar (objectives with health: Assault / FreezeTag revival / generators) ----
            if (wp.Health >= 0f)
                DrawHealthBar(new Vector2(sp.X, barY), wp.Health, col, a, ws);
        }
    }

    private static Vector2 ClampToRect(Vector2 c, Vector2 dir, float l, float t, float r, float b)
    {
        dir = dir.Normalized();
        float tx = dir.X > 0f ? (r - c.X) / dir.X : dir.X < 0f ? (l - c.X) / dir.X : float.MaxValue;
        float ty = dir.Y > 0f ? (b - c.Y) / dir.Y : dir.Y < 0f ? (t - c.Y) / dir.Y : float.MaxValue;
        float tt = MathF.Min(tx, ty);
        if (!float.IsFinite(tt) || tt < 0f) tt = 0f;
        return c + dir * tt;
    }

    private void DrawArrow(Vector2 p, float ang, Color col, float scale = 1f)
    {
        float s = 9f * MathF.Max(0.01f, scale); // QC SPRITE_ARROW_SCALE (1) * t (the faded per-sprite scale)
        Vector2 f = new(MathF.Cos(ang), MathF.Sin(ang));
        Vector2 side = new(-f.Y, f.X);
        var pts = new[] { p + f * s, p - f * (s * 0.6f) + side * (s * 0.6f), p - f * (s * 0.6f) - side * (s * 0.6f) };
        DrawColoredPolygon(pts, col);
    }

    private void DrawCenteredText(Font fnt, Vector2 p, string txt, Color col, int size)
    {
        if (string.IsNullOrEmpty(txt)) return;
        float w = fnt.GetStringSize(txt, HorizontalAlignment.Left, -1f, size).X;
        Vector2 at = new(p.X - w * 0.5f, p.Y);
        DrawString(fnt, at + new Vector2(1f, 1f), txt, HorizontalAlignment.Left, -1f, size, new Color(0f, 0f, 0f, col.A * 0.7f));
        DrawString(fnt, at, txt, HorizontalAlignment.Left, -1f, size, col);
    }

    private void DrawHealthBar(Vector2 p, float frac, Color col, float a, float scale = 1f)
    {
        float w = 52f * MathF.Max(0.01f, scale), h = 5f * MathF.Max(0.01f, scale);
        var bg = new Rect2(p.X - w * 0.5f, p.Y, w, h);
        DrawRect(bg, new Color(0f, 0f, 0f, 0.5f * a));
        DrawRect(new Rect2(bg.Position, new Vector2(w * Math.Clamp(frac, 0f, 1f), h)), new Color(col.R, col.G, col.B, a));
        DrawRect(bg, new Color(col.R, col.G, col.B, a), filled: false, width: 1f);
    }

    private static float NowSec()
        => Api.Services is not null ? Api.Clock.Time : Time.GetTicksMsec() / 1000f;
}
