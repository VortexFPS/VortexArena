using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A <see cref="StyleBox"/> that draws a Xonotic "button picture" exactly like the engine's
/// <c>draw_ButtonPicture</c> (menu/draw.qc), instead of Godot's built-in 9-slice.
///
/// The source art is 4:1 (e.g. 256×64) split into three parts: a quarter-width left cap, a half-width middle, and
/// a quarter-width right cap. The crucial detail Base uses — and a Godot <see cref="StyleBoxTexture"/> cannot —
/// is <c>square = theSize.y</c>: the end caps are drawn as <b>height×height squares</b> (the quarter of the
/// texture scaled <em>uniformly</em> to the control's height), and only the middle half is stretched
/// horizontally. A StyleBoxTexture instead uses fixed pixel margins and stretches the caps vertically, so the
/// rounded ends distort on any button that isn't the texture's native height (the "borders scale with the button"
/// artefact). This reproduces the QC rendering so buttons look identical at any size.
/// </summary>
public partial class ButtonPictureStyleBox : StyleBox
{
    /// <summary>The 4:1 button art (button_n/_f/_c/_d, bigbutton_*, etc.).</summary>
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

        if (w <= h * 2f)
        {
            // Not wide enough — draw just the two rounded ends (draw_ButtonPicture narrow branch).
            float half = w * 0.5f;
            float bw = 0.25f * w / (h * 2f); // texture fraction sampled at each end
            Part(toCanvasItem, tex, ts, new Rect2(o, new Vector2(half, h)), 0f, bw);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(half, 0f), new Vector2(half, h)), 1f - bw, bw);
        }
        else
        {
            float sq = h; // cap = a square of the control's height
            Part(toCanvasItem, tex, ts, new Rect2(o, new Vector2(sq, h)), 0f, 0.25f);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(sq, 0f), new Vector2(w - 2f * sq, h)), 0.25f, 0.5f);
            Part(toCanvasItem, tex, ts, new Rect2(o + new Vector2(w - sq, 0f), new Vector2(sq, h)), 0.75f, 0.25f);
        }
    }

    private void Part(Rid ci, Rid tex, Vector2 ts, Rect2 dest, float srcXFrac, float srcWFrac)
    {
        var src = new Rect2(srcXFrac * ts.X, 0f, srcWFrac * ts.X, ts.Y);
        RenderingServer.CanvasItemAddTextureRectRegion(ci, dest, tex, src, Tint);
    }
}
