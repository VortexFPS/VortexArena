using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// The HUD skin layer — the C# successor to QuakeC's <c>draw_BorderPicture</c> / <c>HUD_Panel_DrawBg</c>
/// (Base/.../qcsrc/lib/draw.qh + client/hud/hud.qc) and the engine's skinned <c>drawpic</c>. Xonotic frames
/// every panel with a 9-slice border image drawn from the active HUD skin (<c>gfx/hud/&lt;skin&gt;/…</c>,
/// default <c>luma</c>, falling back to <c>gfx/hud/default/…</c>). This static helper resolves the border
/// texture through the existing <see cref="TextureCache"/> and issues the nine <see cref="CanvasItem"/>
/// region blits that make up a stretched 9-slice frame.
///
/// The per-panel <c>bg</c> names in the luma skin (<c>border_default_south</c>, <c>border_tab_south</c>,
/// <c>border_corner_northwest</c>, …) are <em>separate texture files</em> shipped by the skin, NOT engine
/// crops — QC's <see cref="DrawBorderPicture"/> (<c>draw_BorderPicture</c>) always renders the full 9-slice of
/// whatever <c>pic</c> resolved; the directional look comes from the texture's own art, not from masking. So
/// rendering the full 9-slice for every variant name is the faithful behavior; this resolver just selects the
/// right texture by name (skin → default → <c>border_default</c>), exactly as <c>HUD_Panel_GetBg</c> does.
/// </summary>
public static class HudSkin
{
    /// <summary>The active skin folder name (QC <c>hud_skin</c>); default <c>luma</c>. Set by the manager
    /// when the <c>hud_skin</c> cvar changes (which also clears <see cref="TextureCache"/>).</summary>
    public static string SkinName { get; set; } = "luma";

    /// <summary>The Xolonium bold font (centerprint titles / emphasised text), wired by the host like
    /// <see cref="HudPanel.HudFont"/>. Falls back to the regular HUD font when null.</summary>
    public static Font? BoldFont { get; set; }

    /// <summary>
    /// Resolve a border/background image by its luma <paramref name="bg"/> name, trying the active skin then
    /// the default skin then the bare <c>border_default</c> atlas. Returns null when nothing resolves (caller
    /// draws a flat fallback). <c>""</c> and <c>"0"</c> are treated as "no image" by the caller, not here.
    /// </summary>
    public static Texture2D? ResolveBorderTex(string bg)
    {
        if (string.IsNullOrEmpty(bg) || bg == "0") return null;
        return TextureCache.GetFirst(
            $"gfx/hud/{SkinName}/{bg}",
            $"gfx/hud/default/{bg}",
            $"gfx/hud/{SkinName}/border_default",
            "gfx/hud/default/border_default");
    }

    /// <summary>
    /// Draw a 9-slice border picture into <paramref name="ci"/> over the (panel-local) <paramref name="rect"/>
    /// with corner thickness <paramref name="border"/> px, tinted <paramref name="color"/> at
    /// <paramref name="alpha"/>. Line-for-line port of QC <c>draw_BorderPicture</c> (lib/draw.qh:43-116): the
    /// source texture is divided on the {0,.25,.75,1} UV grid — the outer quarter on each side is the corner/edge,
    /// the central half stretches. The two "not wide/high enough" branches (panel smaller than two corners) crop
    /// the cap UVs proportionally (<c>bW = 0.25 * size/(2*border)</c>) instead of stretching the whole atlas, so a
    /// thin/short bg-enabled panel shows the correct partial frame Base draws. Returns false when no texture
    /// resolved so the caller can paint its translucent fallback.
    /// </summary>
    public static bool DrawBorderPicture(CanvasItem ci, Rect2 rect, string bg, Color color, float alpha, float border)
    {
        Texture2D? tex = ResolveBorderTex(bg);
        if (tex is null) return false;

        var mod = new Color(color.R, color.G, color.B, alpha);
        Vector2 ts = tex.GetSize();
        if (ts.X <= 0f || ts.Y <= 0f) return false;

        float x = rect.Position.X, y = rect.Position.Y, w = rect.Size.X, h = rect.Size.Y;
        if (w <= 0f || h <= 0f) return false;

        // QC: theBorderSize.x == 0 && .y == 0 → draw only the central part (no frame expansion).
        float t = border;
        if (t <= 0f)
        {
            // central-part only (UV 0.25..0.75 stretched over the whole rect).
            Blit(ci, tex, mod, rect, new Rect2(0.25f, 0.25f, 0.5f, 0.5f));
            return true;
        }

        bool narrow = w <= t * 2f;   // QC: theSize.x <= theBorderSize.x * 2
        bool shortH = h <= t * 2f;   // QC: theSize.y <= theBorderSize.y * 2

        if (narrow)
        {
            float bW = 0.25f * w / (t * 2f); // QC bW = 0.25 * size.x / (border*2)
            if (shortH)
            {
                // not wide AND not high → four cropped corners only (QC:71-74).
                float bH = 0.25f * h / (t * 2f);
                float hw = w * 0.5f, hh = h * 0.5f;
                Blit(ci, tex, mod, new Rect2(x,      y,      hw, hh), new Rect2(0f,       0f,       bW, bH));
                Blit(ci, tex, mod, new Rect2(x + hw, y,      hw, hh), new Rect2(1f - bW,  0f,       bW, bH));
                Blit(ci, tex, mod, new Rect2(x,      y + hh, hw, hh), new Rect2(0f,       1f - bH,  bW, bH));
                Blit(ci, tex, mod, new Rect2(x + hw, y + hh, hw, hh), new Rect2(1f - bW,  1f - bH,  bW, bH));
            }
            else
            {
                // not wide enough → left+right columns, full height in 3 rows (QC:79-84). dY = border in px.
                float hw = w * 0.5f;
                Blit(ci, tex, mod, new Rect2(x,      y,         hw, t),         new Rect2(0f,      0f,    bW, 0.25f));
                Blit(ci, tex, mod, new Rect2(x + hw, y,         hw, t),         new Rect2(1f - bW, 0f,    bW, 0.25f));
                Blit(ci, tex, mod, new Rect2(x,      y + t,     hw, h - 2f * t), new Rect2(0f,      0.25f, bW, 0.5f));
                Blit(ci, tex, mod, new Rect2(x + hw, y + t,     hw, h - 2f * t), new Rect2(1f - bW, 0.25f, bW, 0.5f));
                Blit(ci, tex, mod, new Rect2(x,      y + h - t, hw, t),         new Rect2(0f,      0.75f, bW, 0.25f));
                Blit(ci, tex, mod, new Rect2(x + hw, y + h - t, hw, t),         new Rect2(1f - bW, 0.75f, bW, 0.25f));
            }
            return true;
        }

        if (shortH)
        {
            // not high enough → top+bottom rows, full width in 3 columns (QC:94-99). dX = border in px.
            float bH = 0.25f * h / (t * 2f);
            float hh = h * 0.5f;
            Blit(ci, tex, mod, new Rect2(x,         y,      t,         hh), new Rect2(0f,    0f,      0.25f, bH));
            Blit(ci, tex, mod, new Rect2(x + t,     y,      w - 2f * t, hh), new Rect2(0.25f, 0f,      0.5f,  bH));
            Blit(ci, tex, mod, new Rect2(x + w - t, y,      t,         hh), new Rect2(0.75f, 0f,      0.25f, bH));
            Blit(ci, tex, mod, new Rect2(x,         y + hh, t,         hh), new Rect2(0f,    1f - bH, 0.25f, bH));
            Blit(ci, tex, mod, new Rect2(x + t,     y + hh, w - 2f * t, hh), new Rect2(0.25f, 1f - bH, 0.5f,  bH));
            Blit(ci, tex, mod, new Rect2(x + w - t, y + hh, t,         hh), new Rect2(0.75f, 1f - bH, 0.25f, bH));
            return true;
        }

        // Full 9-slice (QC:105-113). Destination columns/rows (px) and the source UV grid (fraction).
        float[] dx = { x, x + t, x + w - t, x + w };
        float[] dy = { y, y + t, y + h - t, y + h };
        float[] ux = { 0f, 0.25f, 0.75f, 1f };
        float[] uy = { 0f, 0.25f, 0.75f, 1f };

        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            float dw = dx[c + 1] - dx[c];
            float dh = dy[r + 1] - dy[r];
            if (dw <= 0f || dh <= 0f) continue;
            var dst = new Rect2(dx[c], dy[r], dw, dh);
            var src = new Rect2(ux[c] * ts.X, uy[r] * ts.Y,
                                (ux[c + 1] - ux[c]) * ts.X, (uy[r + 1] - uy[r]) * ts.Y);
            ci.DrawTextureRectRegion(tex, dst, src, mod);
        }
        return true;
    }

    /// <summary>Blit a 0..1 UV sub-region (QC <c>drawsubpic</c>) of <paramref name="tex"/> into the (panel-local)
    /// destination <paramref name="dst"/>, converting the UV rect to the texture's pixel rect.</summary>
    private static void Blit(CanvasItem ci, Texture2D tex, Color mod, Rect2 dst, Rect2 srcUv)
    {
        if (dst.Size.X <= 0f || dst.Size.Y <= 0f) return;
        Vector2 ts = tex.GetSize();
        var srcPx = new Rect2(srcUv.Position.X * ts.X, srcUv.Position.Y * ts.Y,
                              srcUv.Size.X * ts.X, srcUv.Size.Y * ts.Y);
        ci.DrawTextureRectRegion(tex, dst, srcPx, mod);
    }
}
