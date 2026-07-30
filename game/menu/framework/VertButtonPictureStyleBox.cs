using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The vertical counterpart of <see cref="ButtonPictureStyleBox"/> — a faithful port of the engine's
/// <c>draw_VertButtonPicture</c> (menu/draw.qc), used by the Xonotic listbox scrollbar (item/listbox.qc).
///
/// The source art is 1:4 (e.g. 64×256) split top-to-bottom into a quarter-height top cap, a half-height middle,
/// and a quarter-height bottom cap. As with the horizontal version the crucial detail is <c>square = theSize.x</c>:
/// the end caps are drawn as <b>width×width squares</b> (the quarter of the texture scaled uniformly to the
/// control's width) and only the middle half is stretched <em>vertically</em>. This is what a Godot
/// <see cref="StyleBoxTexture"/> can't express (fixed margins + horizontal stretch distort it), so the scrollbar
/// grabber/track are drawn here to match Base at any height.
/// </summary>
public partial class VertButtonPictureStyleBox : StyleBox
{
    /// <summary>The 1:4 scrollbar art (scrollbar_n/_f/_c/_s).</summary>
    public Texture2D? Texture { get; set; }

    /// <summary>Per-state colour tint (the QC theColor), default white.</summary>
    public Color Tint { get; set; } = Colors.White;

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
    {
        if (Texture == null)
            return;
        Rid tex = Texture.GetRid();
        Vector2 ts = Texture.GetSize();
        if (ts.X <= 0 || ts.Y <= 0)
            return;

        float w = rect.Size.X, h = rect.Size.Y;
        Vector2 o = rect.Position;

        if (h <= w * 2f)
        {
            // Not tall enough — draw just the two rounded ends (draw_VertButtonPicture narrow branch).
            float half = h * 0.5f;
            float bh = 0.25f * h / (w * 2f); // texture fraction sampled at each end
            Part(toCanvasItem, tex, ts, new Rect2(o, new Vector2(w, half)), 0f, bh);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(0f, half), new Vector2(w, half)), 1f - bh, bh);
        }
        else
        {
            float sq = w; // cap = a square of the control's width
            Part(toCanvasItem, tex, ts, new Rect2(o, new Vector2(w, sq)), 0f, 0.25f);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(0f, sq), new Vector2(w, h - 2f * sq)), 0.25f, 0.5f);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(0f, h - sq), new Vector2(w, sq)), 0.75f, 0.25f);
        }
    }

    private void Part(Rid ci, Rid tex, Vector2 ts, Rect2 dest, float srcYFrac, float srcHFrac)
    {
        var src = new Rect2(0f, srcYFrac * ts.Y, ts.X, srcHFrac * ts.Y);
        RenderingServer.CanvasItemAddTextureRectRegion(ci, dest, tex, src, Tint);
    }
}
