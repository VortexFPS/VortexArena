// Port of nothing in DarkPlaces on its own: one run of consecutive 2D draws that share a blend mode
// and, when text has to be cut, a scissor rectangle (gl_draw.c DrawQ_SetClipArea's GL_SCISSOR_TEST).
// See LegacyDrawLayer, which owns these.
using Godot;

namespace VortexArena.Game.Legacy;

/// <summary>
/// A canvas item that replays one contiguous stretch of the frame's 2D draw list. Godot fixes the
/// blend mode per canvas item (through its material), not per draw call, so a list that switches
/// between normal and additive drawing is cut into one of these per switch; they are siblings in list
/// order, which keeps the order on screen the order of the calls. The same goes for a scissor: the
/// only thing in Godot that cuts glyphs at an edge is a control that clips its contents, so a stretch
/// drawn under a clip area that text crosses is a control of exactly that rectangle.
/// </summary>
public partial class LegacyDrawSegment : Control
{
    /// <summary>The layer whose list this replays.</summary>
    public LegacyDrawLayer? Layer { get; set; }
    /// <summary>The commands [Start, End) of the list.</summary>
    public int Start { get; set; }
    public int End { get; set; }
    /// <summary>The clip area in force when this stretch begins, in virtual-screen coordinates; null for none.</summary>
    public Rect2? ClipAtStart { get; set; }
    /// <summary>What the segment last drew, as the layer summed it up (LegacyDrawLayer.Assign): while the next
    /// frame's stretch sums to the same, the canvas item keeps its drawing and nothing is replayed.</summary>
    internal ulong DrawnContent { get; set; }
    internal bool HasDrawnContent { get; set; }
    internal int BlendClass { get; set; } = -1;
    /// <summary>The rectangle this segment occupies and clips to, in the layer's pixels; null when it covers the layer and clips nothing.</summary>
    public Rect2? HardClip { get; private set; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Places the segment: over the whole layer, or on a scissor rectangle with clipping on.</summary>
    internal void SetHardClip(Rect2? pixels, Vector2 layerSize)
    {
        // Whole pixels, as a scissor rectangle is: a Control is placed on whole pixels whatever it is asked
        // for, and what is drawn inside is offset by this position, so a fraction here would shift it.
        if (pixels is { } asked)
        {
            Vector2 from = asked.Position.Floor(), to = asked.End.Ceil();
            pixels = new Rect2(from, to - from);
        }
        HardClip = pixels;
        ClipContents = pixels is not null;
        Rect2 rect = pixels ?? new Rect2(Vector2.Zero, layerSize);
        if (Position != rect.Position) Position = rect.Position;
        if (Size != rect.Size) Size = rect.Size;
    }

    public override void _Draw()
    {
        long began = LegacyPerfLog.Stamp();
        Layer?.Replay(this);
        LegacyPerfLog.Extra(LegacyPerfLog.XHudReplay, began);
    }
}
