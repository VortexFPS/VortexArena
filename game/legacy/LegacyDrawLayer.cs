// Port of Base/darkplaces/gl_draw.c DrawQ_Pic, DrawQ_RotPic, DrawQ_SuperPic (as drawsubpic uses it),
// DrawQ_Fill, DrawQ_Line, DrawQ_String_Scale (placement and colour runs), DrawQ_SetClipArea /
// DrawQ_ResetClipArea and DrawQ_ProcessDrawFlag (which DRAWFLAG_* means which blend), replaying a
// recorded list instead of drawing as the calls arrive.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Legacy.Presentation;

namespace VortexArena.Game.Legacy;

/// <summary>
/// A QuakeC program's 2D output - everything a client program's CSQC_UpdateView or the menu program's
/// m_draw drew with drawpic, drawstring, drawfill and their relatives - replayed onto the canvas.
///
/// The program's calls happen inside the VM, during a node's <c>_Process</c>, and are only RECORDED
/// there (<see cref="LegacyDrawList"/>); Godot allows canvas drawing only inside <c>_Draw</c>. The same
/// split the mod sandbox's layer uses (game/modding/ModLayer.cs), with DarkPlaces' vocabulary:
/// positions are in the virtual resolution vid_conwidth by vid_conheight, stretched over the window.
///
/// What is exact: order, positions, sizes, colours, sub-rectangles, rotation, all five blend functions
/// (normal, additive, modulate, 2x modulate, screen), and a clip area's effect on pictures, fills and
/// text. What is approximate is listed where it happens: a line, a rotated picture or a polygon that
/// crosses a clip area's edge is cut only when it shares its clip area with text that had to be cut.
/// </summary>
public partial class LegacyDrawLayer : Control
{
    // A frame that alternates blend modes on every call would otherwise ask for a canvas item per call.
    // The menu is what sets the number: one of its list boxes is a clip area whose first and last rows
    // are cut, and a settings dialog has several.
    private const int MaxSegments = 192;

    // A stretch is also ended after this many commands. A canvas item is replayed whole when anything in it
    // changed, and in a game something always has (a name tag that follows a player, the clock, a damage number
    // on its way up): with one item per blend mode the whole HUD was replayed every frame, text glyph by glyph,
    // to move three name tags - half a millisecond of a four millisecond frame. Cut into short stretches, the
    // ones that did not change are kept, and those are most of them. Not applied to the two blend modes that
    // read the screen (each of their items costs a copy of it). VORTEX_LEGACY_HUDCHUNK overrides the length
    // (0: no cutting), as the other developer aids: an environment variable, for the other arm of a comparison.
    /// <summary>Set by the presentation's developer aid (VORTEX_LEGACY_ABLATE / _TOGGLE "hudchunk"): no cutting.</summary>
    internal static bool ChunkOff;
    private static readonly int s_chunk = int.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_HUDCHUNK"), out int chunk) ? Math.Clamp(chunk, 0, 4096) : 8;

    private readonly List<LegacyDrawSegment> _segments = new();
    private readonly List<LegacyTextRun> _runs = new();
    private readonly Vector2[] _trianglePoints = new Vector2[3];
    private readonly Vector2[] _triangleUvs = new Vector2[3];
    private readonly Color[] _triangleColors = new Color[3];
    private CanvasItemMaterial? _additive, _multiply;
    private ShaderMaterial? _doubleMultiply, _screen;
    private readonly List<BackBufferCopy> _copies = new();
    private LegacyCanvas? _source;

    // DRAWFLAG_2XMODULATE is glBlendFunc(GL_DST_COLOR, GL_SRC_COLOR): destination * source * 2. DRAWFLAG_SCREEN
    // is glBlendFunc(GL_ONE_MINUS_DST_COLOR, GL_ONE): destination + source * (1 - destination). Neither is one
    // of the canvas blend modes (and a multiply that has to brighten cannot be written to an 8-bit target as a
    // source colour above 1), so both read what is on screen and write the blended result. Source alpha plays
    // no part in either function, as in DarkPlaces.
    private const string ScreenBlendShader = """
        shader_type canvas_item;
        render_mode blend_mix, unshaded;
        uniform sampler2D screen_copy : hint_screen_texture, filter_nearest, repeat_disable;
        uniform int mode = 0;
        void fragment() {
            vec3 source = COLOR.rgb;
            vec3 destination = texture(screen_copy, SCREEN_UV).rgb;
            vec3 blended = mode == 0 ? destination * source * 2.0 : destination + source * (vec3(1.0) - destination);
            COLOR = vec4(clamp(blended, vec3(0.0), vec3(1.0)), 1.0);
        }
        """;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _additive = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        _multiply = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Mul };
        Shader shader = new() { Code = ScreenBlendShader };
        _doubleMultiply = new ShaderMaterial { Shader = shader };
        _doubleMultiply.SetShaderParameter("mode", 0);
        _screen = new ShaderMaterial { Shader = shader };
        _screen.SetShaderParameter("mode", 1);
    }

    /// <summary>Canvas items in use for the last frame presented.</summary>
    public int SegmentsInUse { get; private set; }
    /// <summary>Of those, the ones that clip their contents to a clip area (text crossed its edge).</summary>
    public int ClippedSegmentsInUse { get; private set; }

    // The DRAWFLAG_* of a call (R_BeginPolygon's may carry DRAWFLAG_MIPMAP above the mask): 0 alpha blend,
    // 1 additive (GL_SRC_ALPHA, GL_ONE), 2 modulate (GL_DST_COLOR, GL_ZERO), 3 2x modulate, 4 screen. The
    // class is the flag; anything else is the normal blend.
    private static int BlendClass(int flags) => (flags & 0xFF) is >= 1 and <= 4 ? flags & 0xFF : 0;

    /// <summary>
    /// Takes the frame's list: cuts it into stretches that one canvas item can draw and schedules their
    /// redraw. A stretch ends where the blend mode changes (Godot fixes it per canvas item) and where
    /// text has to be cut by a clip area: Godot has no scissor inside a canvas item's draw calls, so a
    /// clip area whose text crosses its edge becomes a canvas item of exactly that rectangle that clips
    /// its contents, holding every command drawn under that clip area from there on.
    /// </summary>
    internal void Present(LegacyCanvas source)
    {
        _source = source;
        IReadOnlyList<LegacyDrawCommand> commands = source.DrawList.Commands;
        int used = 0, clipped = 0, start = 0, currentClass = -1, inStretch = 0;
        Rect2? clip = null, clipAtStart = null, hardClip = null;
        for (int i = 0; i < commands.Count; i++)
        {
            LegacyDrawCommand command = commands[i];
            if (command.Kind is LegacyDrawKind.SetClip or LegacyDrawKind.ResetClip)
            {
                clip = command.Kind == LegacyDrawKind.SetClip ? new Rect2(command.X, command.Y, command.Width, command.Height) : null;
                // A stretch that clips to one area cannot hold what is drawn under another.
                if (hardClip is not null && currentClass >= 0)
                {
                    if (i > start) Assign(used++, start, i, currentClass, clipAtStart, hardClip, ref clipped);
                    start = i + 1;
                    currentClass = -1;
                    hardClip = null;
                    clipAtStart = clip;
                }
                continue;
            }
            int blend = BlendClass(command.Flags);
            bool needsHardClip = hardClip is null && clip is { } area && command.Kind == LegacyDrawKind.Text && used < MaxSegments - 2
                && TextCrosses(source, command, area);
            if (currentClass < 0)
            {
                currentClass = blend;
                start = i;
                inStretch = 0;
                clipAtStart = clip;
                if (needsHardClip) hardClip = clip;
            }
            else if ((blend != currentClass || needsHardClip || (s_chunk > 0 && !ChunkOff && inStretch >= s_chunk && blend < 3 && used < MaxSegments - 32)) && used < MaxSegments - 1)
            {
                Assign(used++, start, i, currentClass, clipAtStart, hardClip, ref clipped);
                start = i;
                inStretch = 0;
                currentClass = blend;
                // The clip commands between the two stretches were consumed above; carry their result over.
                clipAtStart = clip;
                if (needsHardClip) hardClip = clip;
            }
            inStretch++;
        }
        if (commands.Count > start && currentClass >= 0) Assign(used++, start, commands.Count, currentClass, clipAtStart, hardClip, ref clipped);
        for (int i = used; i < _segments.Count; i++)
        {
            LegacyDrawSegment idle = _segments[i];
            if (idle.Start == idle.End && !idle.Visible) continue;
            idle.Start = idle.End = 0;
            idle.Visible = false;
            idle.HasDrawnContent = false;
            idle.BlendClass = -1;
            _copies[i].Visible = false;
        }
        SegmentsInUse = used;
        ClippedSegmentsInUse = clipped;
    }

    // DrawQ_String under a scissor: does this text reach outside the clip area? (Text that lies wholly
    // outside is simply not drawn; text wholly inside needs no cutting.)
    private static bool TextCrosses(LegacyCanvas source, in LegacyDrawCommand c, Rect2 area)
    {
        if (c.Text is null || c.Width == 0 || c.Height == 0) return false;
        Rect2 box = TextBox(source, c);
        return box.Intersects(area) && !area.Encloses(box);
    }

    // The rectangle a text command covers, in virtual-screen units: its measured width by one character cell.
    private static Rect2 TextBox(LegacyCanvas source, in LegacyDrawCommand c)
    {
        float width = source.StringWidth(c.Text ?? "", c.IgnoreColorCodes, new QuakeC.QcVector(c.Width, c.Height, 0), c.Font,
            new QuakeC.QcVector(c.FontScaleX, c.FontScaleY, 0));
        float height = MathF.Abs(c.Height * (c.FontScaleY != 0 ? c.FontScaleY : 1));
        return new Rect2(c.X, c.Y, MathF.Max(width, 0.01f), MathF.Max(height, 0.01f));
    }

    private void Assign(int index, int start, int end, int blendClass, Rect2? clipAtStart, Rect2? hardClip, ref int clipped)
    {
        while (_segments.Count <= index)
        {
            // A stretch that reads the screen needs what the stretches before it drew: the copy node ahead of
            // each segment is switched on only for those.
            BackBufferCopy copy = new() { Name = "Copy" + _segments.Count, CopyMode = BackBufferCopy.CopyModeEnum.Viewport, Visible = false };
            AddChild(copy);
            _copies.Add(copy);
            LegacyDrawSegment made = new() { Name = "Segment" + _segments.Count, Layer = this };
            AddChild(made);
            _segments.Add(made);
        }
        LegacyDrawSegment segment = _segments[index];
        segment.Start = start;
        segment.End = end;
        segment.ClipAtStart = clipAtStart;
        if (hardClip is not null) clipped++;

        // Most of a HUD and nearly all of a menu is the same from one frame to the next. A canvas item keeps
        // what was drawn on it until it is asked to redraw, so a stretch whose commands - and everything
        // they depend on - are what they were is left alone: no replay, no glyph-by-glyph calls into the
        // engine, nothing for the renderer to rebuild. (Measured on Xonotic's main menu: the replay was
        // 5.4 ms of an 11.6 ms frame.)
        ulong content = _source is { } summed ? Summarise(summed, start, end, blendClass, clipAtStart, hardClip) : 0;
        if (segment.HasDrawnContent && segment.DrawnContent == content && segment.BlendClass == blendClass && segment.Visible)
        {
            SegmentsKept++;
            return;
        }
        segment.DrawnContent = content;
        segment.HasDrawnContent = _source is not null;
        if (segment.BlendClass != blendClass)
        {
            segment.BlendClass = blendClass;
            segment.Material = blendClass switch { 1 => _additive, 2 => _multiply, 3 => _doubleMultiply, 4 => _screen, _ => null };
            _copies[index].Visible = blendClass is 3 or 4;
        }

        // DrawQ_SetClipArea turns the virtual rectangle into whole pixels ("(int)(0.5 + x * width / vid_conwidth)").
        Rect2? pixels = null;
        if (hardClip is { } area && _source is { } source && source.ConWidth > 0 && source.ConHeight > 0)
        {
            Vector2 scale = new(Size.X / source.ConWidth, Size.Y / source.ConHeight);
            float x0 = MathF.Floor(0.5f + area.Position.X * scale.X), y0 = MathF.Floor(0.5f + area.Position.Y * scale.Y);
            float x1 = MathF.Floor(0.5f + area.End.X * scale.X), y1 = MathF.Floor(0.5f + area.End.Y * scale.Y);
            pixels = new Rect2(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
        }
        segment.SetHardClip(pixels, Size);
        if (!segment.Visible) segment.Visible = true;
        segment.QueueRedraw();
    }

    /// <summary>Stretches left as they were in the frames presented so far (not replayed).</summary>
    public long SegmentsKept { get; private set; }

    // Everything a stretch's picture depends on, folded into one number: the commands themselves, the 2D
    // space they are drawn in, how text looks, and the canvas's own count of changes under the names.
    private ulong Summarise(LegacyCanvas source, int start, int end, int blendClass, Rect2? clipAtStart, Rect2? hardClip)
    {
        ulong h = 14695981039346656037UL;
        static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL;
        static ulong F(float f) => (ulong)(uint)BitConverter.SingleToInt32Bits(f);
        Vector2 size = Size;
        LegacyTextLook look = source.TextLook;
        h = Mix(h, (ulong)(uint)source.Generation);
        h = Mix(h, (ulong)(uint)blendClass);
        h = Mix(h, F(size.X)); h = Mix(h, F(size.Y));
        h = Mix(h, F(source.ConWidth)); h = Mix(h, F(source.ConHeight));
        h = Mix(h, F(source.PixelWidth)); h = Mix(h, F(source.PixelHeight));
        h = Mix(h, F(source.FontSizeSnapping)); h = Mix(h, source.FontKerning ? 1UL : 2UL); h = Mix(h, (ulong)source.FontHinting);
        h = Mix(h, F(look.Contrast)); h = Mix(h, F(look.Brightness)); h = Mix(h, F(look.Shadow));
        if (clipAtStart is { } c0) { h = Mix(h, F(c0.Position.X)); h = Mix(h, F(c0.Position.Y)); h = Mix(h, F(c0.Size.X)); h = Mix(h, F(c0.Size.Y)); }
        else h = Mix(h, 7);
        if (hardClip is { } c1) { h = Mix(h, F(c1.Position.X)); h = Mix(h, F(c1.Position.Y)); h = Mix(h, F(c1.Size.X)); h = Mix(h, F(c1.Size.Y)); }
        else h = Mix(h, 11);
        LegacyDrawList list = source.DrawList;
        IReadOnlyList<LegacyDrawCommand> commands = list.Commands;
        IReadOnlyList<LegacyDrawVertex> vertices = list.PolygonVertices;
        end = Math.Min(end, commands.Count);
        for (int i = start; i < end; i++)
        {
            LegacyDrawCommand c = commands[i];
            h = Mix(h, (ulong)(uint)c.Kind | ((ulong)(uint)c.Flags << 8) | (c.Rotated ? 1UL << 40 : 0) | (c.IgnoreColorCodes ? 1UL << 41 : 0) | ((ulong)(uint)c.Font << 44));
            h = Mix(h, F(c.X) | (F(c.Y) << 32));
            h = Mix(h, F(c.Width) | (F(c.Height) << 32));
            h = Mix(h, F(c.Color.R) | (F(c.Color.G) << 32));
            h = Mix(h, F(c.Color.B) | (F(c.Color.A) << 32));
            if (c.Text is { } text) h = Mix(h, (ulong)(uint)string.GetHashCode(text, StringComparison.Ordinal) | ((ulong)(uint)text.Length << 32));
            if (c.Kind is LegacyDrawKind.Picture)
            {
                h = Mix(h, F(c.SourceX) | (F(c.SourceY) << 32));
                h = Mix(h, F(c.SourceWidth) | (F(c.SourceHeight) << 32));
                h = Mix(h, F(c.PivotX) | (F(c.PivotY) << 32));
                h = Mix(h, F(c.Angle));
            }
            else if (c.Kind is LegacyDrawKind.Text) h = Mix(h, F(c.FontScaleX) | (F(c.FontScaleY) << 32));
            else if (c.Kind is LegacyDrawKind.Line) h = Mix(h, F(c.Angle));
            else if (c.Kind is LegacyDrawKind.Polygon && c.VertexStart >= 0 && c.VertexStart + c.VertexCount <= vertices.Count)
                for (int v = c.VertexStart, last = c.VertexStart + c.VertexCount; v < last; v++)
                {
                    LegacyDrawVertex vertex = vertices[v];
                    h = Mix(h, F(vertex.X) | (F(vertex.Y) << 32));
                    h = Mix(h, F(vertex.U) | (F(vertex.V) << 32));
                    h = Mix(h, F(vertex.Color.R) | (F(vertex.Color.G) << 32));
                    h = Mix(h, F(vertex.Color.B) | (F(vertex.Color.A) << 32));
                }
        }
        return h;
    }

    /// <summary>Draws one segment's stretch of the list onto it. Called from the segment's own _Draw.</summary>
    internal void Replay(LegacyDrawSegment target)
    {
        if (_source is not { } source) return;
        LegacyDrawList list = source.DrawList;
        IReadOnlyList<LegacyDrawCommand> commands = list.Commands;
        int end = Math.Min(target.End, commands.Count);
        if (target.Start >= end) return;

        float conWidth = source.ConWidth, conHeight = source.ConHeight;
        Vector2 size = Size;
        if (!(conWidth > 0) || !(conHeight > 0) || !(size.X > 0) || !(size.Y > 0)) return;
        // The virtual resolution stretched over the window: one transform for everything unrotated. A
        // segment that clips sits at its clip area's pixels, so what it draws is shifted back by that much.
        Vector2 scale = new(size.X / conWidth, size.Y / conHeight);
        Vector2 origin = target.HardClip is { } hard ? -hard.Position : Vector2.Zero;
        Transform2D baseTransform = new(new Vector2(scale.X, 0), new Vector2(0, scale.Y), origin);
        target.DrawSetTransformMatrix(baseTransform);
        bool scissored = target.HardClip is not null;

        Rect2? clip = target.ClipAtStart;
        for (int i = target.Start; i < end; i++)
        {
            LegacyDrawCommand c = commands[i];
            Color color = new(c.Color.R, c.Color.G, c.Color.B, c.Color.A);
            switch (c.Kind)
            {
                case LegacyDrawKind.SetClip: clip = new Rect2(c.X, c.Y, c.Width, c.Height); break;
                case LegacyDrawKind.ResetClip: clip = null; break;

                case LegacyDrawKind.Fill:
                {
                    Rect2 rect = new Rect2(c.X, c.Y, c.Width, c.Height).Abs();
                    if (clip is { } area) rect = rect.Intersection(area);
                    if (rect.HasArea()) target.DrawRect(rect, color);
                    break;
                }

                case LegacyDrawKind.Line:
                    // A line is not cut by the clip area; one that starts outside it is dropped.
                    if (!scissored && clip is { } lineArea && !lineArea.HasPoint(new Vector2(c.X, c.Y))) break;
                    target.DrawLine(new Vector2(c.X, c.Y), new Vector2(c.Width, c.Height), color, Math.Max(1f, c.Angle));
                    break;

                case LegacyDrawKind.Picture:
                    DrawPicture(target, source, c, color, clip, baseTransform, scissored);
                    break;

                case LegacyDrawKind.Text:
                    DrawText(target, source, c, clip, scale, origin, baseTransform, scissored);
                    break;

                case LegacyDrawKind.Polygon:
                    DrawPolygon2D(target, source, list, c, scissored ? null : clip);
                    break;
            }
        }
        target.DrawSetTransformMatrix(Transform2D.Identity);
    }

    private static void DrawPicture(LegacyDrawSegment target, LegacyCanvas source, in LegacyDrawCommand c, Color color, Rect2? clip, Transform2D baseTransform, bool scissored)
    {
        if (c.Text is null || source.LoadPictureTexture(c.Text) is not { } texture) return;
        Vector2 textureSize = texture.GetSize();
        // drawpic with a zero size draws the picture at its own size ("if (!width) width = pic->width").
        float width = c.Width != 0 ? c.Width : textureSize.X, height = c.Height != 0 ? c.Height : textureSize.Y;
        Rect2 sourceRect = new(c.SourceX * textureSize.X, c.SourceY * textureSize.Y, c.SourceWidth * textureSize.X, c.SourceHeight * textureSize.Y);

        if (c.Rotated)
        {
            // DrawQ_RotPic: (x, y) is where the pivot lands; the picture turns about it by -angle.
            float radians = Mathf.DegToRad(-c.Angle);
            float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
            Vector2 sx = baseTransform.X, sy = baseTransform.Y;
            Transform2D rotated = new(
                new Vector2(cos * sx.X, sin * sy.Y),
                new Vector2(-sin * sx.X, cos * sy.Y),
                new Vector2(c.X * sx.X, c.Y * sy.Y) + baseTransform.Origin);
            if (!scissored && clip is { } rotatedArea && !rotatedArea.HasPoint(new Vector2(c.X, c.Y))) return;
            target.DrawSetTransformMatrix(rotated);
            target.DrawTextureRectRegion(texture, new Rect2(-c.PivotX, -c.PivotY, width, height), sourceRect, color);
            target.DrawSetTransformMatrix(baseTransform);
            return;
        }

        Rect2 rect = new(c.X, c.Y, width, height);
        if (clip is { } area && width > 0 && height > 0)
        {
            Rect2 cut = rect.Intersection(area);
            if (!cut.HasArea()) return;
            // Cut the source region to match what is left of the destination.
            sourceRect = new Rect2(
                sourceRect.Position + (cut.Position - rect.Position) / rect.Size * sourceRect.Size,
                cut.Size / rect.Size * sourceRect.Size);
            rect = cut;
        }
        target.DrawTextureRectRegion(texture, rect, sourceRect, color);
    }

    private void DrawText(LegacyDrawSegment target, LegacyCanvas source, in LegacyDrawCommand c, Rect2? clip, Vector2 scale, Vector2 origin, Transform2D baseTransform, bool scissored)
    {
        if (c.Text is null) return;
        // Wholly outside the clip area: nothing of it shows. (Text that crosses the edge is in a segment
        // that clips, unless the frame ran out of segments - then it is drawn whole rather than not at all.)
        if (!scissored && clip is { } area && !TextBox(source, c).Intersects(area)) return;
        LegacyTextLayout layout = source.Layout(c.Width, c.Height, c.FontScaleX, c.FontScaleY, c.Font, out float yShift);
        if (layout.ScaleX == 0 || layout.ScaleY == 0) return;
        Font font = source.FontForSlot(c.Font);

        _runs.Clear();
        LegacyTextColors.Walk(c.Text, c.IgnoreColorCodes, c.Color, _runs);
        // The glyphs are rasterised at layout.PixelSize and stretched to the character cell asked for.
        target.DrawSetTransformMatrix(new Transform2D(
            new Vector2(scale.X * layout.ScaleX, 0), new Vector2(0, scale.Y * layout.ScaleY),
            // "startx = snap_to_pixel_x(startx, 0.4); starty = snap_to_pixel_y(starty, 0.4)"
            new Vector2(LegacyTextLayout.SnapX(c.X * scale.X), LegacyTextLayout.SnapY((c.Y + yShift) * scale.Y, 0.4f)) + origin));
        int cell = layout.Cell;
        // DrawQ_String: "for (shadow = r_textshadow.value != 0 && basealpha > 0; shadow >= 0; shadow--)" -
        // with r_textshadow set the whole string is drawn first in black, that many REAL pixels right and
        // down. Xonotic leaves it at 0; its text is set off by the outline baked into each glyph instead.
        LegacyTextLook look = source.TextLook;
        for (int pass = look.HasShadow(c.Color.A) ? 1 : 0; pass >= 0; pass--)
        {
            bool shadow = pass == 1;
            // In rasterised pixels of this call: one real pixel is 1 / (scale * layout scale) of them.
            float pen = shadow ? look.Shadow / (scale.X * MathF.Abs(layout.ScaleX)) : 0;
            float drop = shadow ? look.Shadow / (scale.Y * MathF.Abs(layout.ScaleY)) : 0;
            // DrawQ_String's prevch: kept across colour codes, forgotten after an old-font character.
            int previous = 0;
            foreach (LegacyTextRun run in _runs)
            {
                LegacyColor shown = shadow ? look.ShadowColor(run.Color) : look.Color(run.Color);
                Color color = new(shown.R, shown.G, shown.B, shown.A);
                foreach ((string text, int glyph) in source.Stretches(run.Text, c.Font))
                {
                    if (glyph < 0)
                    {
                        pen += source.KerningBetween(c.Font, layout.PixelSize, previous, text);
                        pen = source.DrawOutline(target, text, c.Font, layout.PixelSize, pen, layout.Baseline + drop, color);
                        previous = LegacyCanvas.LastRune(text);
                        continue;
                    }
                    previous = 0;
                    // An old-font character: its cell of the 16 x 16 bitmap font, as wide as the font's width table
                    // says, in the text's colour ("s = (ch & 15)*0.0625f + (0.5f / tw)", "u = 0.0625f * thisw - (1.0f / tw)").
                    (string picture, LegacyBitmapFontWidths widths) = source.BitmapFont(c.Font);
                    if (source.LoadPictureTexture(picture) is { } sheet)
                    {
                        Vector2 sheetSize = sheet.GetSize();
                        float width = widths.Widths[glyph];
                        Rect2 region = new((glyph & 15) / 16f * sheetSize.X + 0.5f, (glyph >> 4) / 16f * sheetSize.Y + 0.5f,
                            width / 16f * sheetSize.X - 1f, sheetSize.Y / 16f - 1f);
                        if (region.Size.X > 0 && region.Size.Y > 0)
                            target.DrawTextureRectRegion(sheet, new Rect2(pen, drop, cell * width, cell), region, color);
                    }
                    pen += widths.Advance(glyph, cell);
                }
            }
        }
        target.DrawSetTransformMatrix(baseTransform);
    }

    // A 2D R_BeginPolygon .. R_EndPolygon: a fan, drawn a triangle at a time (no triangulator to refuse a degenerate one).
    private void DrawPolygon2D(LegacyDrawSegment target, LegacyCanvas source, LegacyDrawList list, in LegacyDrawCommand c, Rect2? clip)
    {
        IReadOnlyList<LegacyDrawVertex> vertices = list.PolygonVertices;
        if (c.VertexCount < 3 || c.VertexStart < 0 || c.VertexStart + c.VertexCount > vertices.Count) return;
        LegacyDrawVertex first = vertices[c.VertexStart];
        if (clip is { } area && !area.HasPoint(new Vector2(first.X, first.Y))) return;
        Texture2D? texture = c.Text is null || c.Text == "$whiteimage" ? null : source.LoadPictureTexture(c.Text);
        for (int i = 1; i + 1 < c.VertexCount; i++)
        {
            Set(0, first);
            Set(1, vertices[c.VertexStart + i]);
            Set(2, vertices[c.VertexStart + i + 1]);
            target.DrawPrimitive(_trianglePoints, _triangleColors, _triangleUvs, texture);
        }

        void Set(int slot, LegacyDrawVertex v)
        {
            _trianglePoints[slot] = new Vector2(v.X, v.Y);
            _triangleUvs[slot] = new Vector2(v.U, v.V);
            _triangleColors[slot] = new Color(v.Color.R, v.Color.G, v.Color.B, v.Color.A);
        }
    }
}
