// NOT BUILT IN THE GODOT HOST as of 2026-10-08, and never run. It type-checks against GodotSharp 4.6.3 in a
// scratch project with stand-ins for the host classes it uses; the real build (source generators) is untried.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Game.Hud;

namespace VortexArena.Game.Modding;

internal enum ModDrawKind : byte { Rect, Pic, Text, SetClip, ResetClip }

/// <summary>One recorded draw command of a mod, in the mod's virtual 2D coordinates.</summary>
internal struct ModDrawOp
{
    public ModDrawKind Kind;
    public Rect2 Rect;
    public Color Color;
    public Texture2D? Texture;
    public string? Text;
    public float Size;
}

/// <summary>
/// One stretch of a mod's draw list that shares a clip rectangle, drawn by its own canvas item.
///
/// Godot has no scissor inside a canvas item's draw calls, so a clip rectangle is done the way
/// <c>game/legacy/LegacyDrawLayer.cs</c> does it: the stretch becomes a control placed exactly on the
/// clip rectangle with <see cref="Control.ClipContents"/> on, and what it draws is shifted back by the
/// rectangle's position. That clips rectangles, pictures and text alike, to the pixel. A stretch drawn
/// under no clip rectangle is a control the size of the layer with clipping off.
///
/// The segments are children of <see cref="ModLayer"/> in list order, so the order a mod drew in is
/// the order things appear in.
/// </summary>
public partial class ModDrawSegment : Control
{
    private List<ModDrawOp>? _ops;
    private int _start, _end;
    private float _unitScale = 1f;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    /// <summary>
    /// Points this segment at <paramref name="ops"/>[start, end). <paramref name="clip"/> is in virtual
    /// units; <paramref name="unitScale"/> is pixels per virtual unit.
    /// </summary>
    internal void Assign(List<ModDrawOp> ops, int start, int end, Rect2? clip, float unitScale, Vector2 layerSize)
    {
        _ops = ops;
        _start = start;
        _end = end;
        _unitScale = unitScale;
        if (clip is { } area)
        {
            Position = area.Position * unitScale;
            Size = area.Size * unitScale;
            ClipContents = true;
        }
        else
        {
            Position = Vector2.Zero;
            Size = layerSize;
            ClipContents = false;
        }
        Visible = true;
        QueueRedraw();
    }

    internal void Release()
    {
        if (!Visible && _ops is null) return;
        _ops = null;
        _start = _end = 0;
        Visible = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ops is null) return;
        // Virtual units to pixels, then back by this control's own position (it sits on the clip rectangle).
        DrawSetTransform(-Position, 0f, new Vector2(_unitScale, _unitScale));
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;

        int end = Math.Min(_end, _ops.Count);
        for (int i = _start; i < end; i++)
        {
            ModDrawOp op = _ops[i];
            switch (op.Kind)
            {
                case ModDrawKind.Rect:
                    if (op.Rect.HasArea()) DrawRect(op.Rect, op.Color);
                    break;
                case ModDrawKind.Pic when op.Texture is not null:
                    if (op.Rect.HasArea()) DrawTextureRect(op.Texture, op.Rect, false, op.Color);
                    break;
                case ModDrawKind.Text when op.Text is not null:
                {
                    // The same text path the HUD uses: the HUD font, drawn from its baseline.
                    int size = Math.Max(1, (int)MathF.Round(op.Size));
                    Vector2 baseline = op.Rect.Position + new Vector2(0f, font.GetAscent(size));
                    DrawString(font, baseline, op.Text, HorizontalAlignment.Left, -1f, size, op.Color);
                    break;
                }
            }
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }
}
