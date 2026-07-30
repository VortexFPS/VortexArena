// Port of qcsrc/menu/xonotic/crosshairpicker.qc (XonoticCrosshairPicker).
using System.Globalization;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The crosshair picker — a faithful C# port of <c>XonoticCrosshairPicker</c>
/// (qcsrc/menu/xonotic/crosshairpicker.qc): a 3×12 grid whose cells map to crosshair indices 31..66 (the
/// gfx/crosshair31..66.tga images). Each cell draws its crosshair image (aspect-preserved, 0.95 of the cell)
/// tinted by the skin's crosshair-picker color, plus the center dot when <c>crosshair_dot</c> is set; picking a
/// cell writes the <c>crosshair</c> cvar. Selection is initialized from the cvar via the picker math (the stock
/// default 16 is outside the 31..66 range, so the picker shows no selection until the user picks — faithful).
/// </summary>
public partial class CrosshairPicker : PickerGrid
{
    private static CvarService Cvars => MenuState.Cvars;

    public CrosshairPicker()
    {
        Rows = MenuPickerMath.CrosshairRows;
        Columns = MenuPickerMath.CrosshairColumns;
        CustomMinimumSize = new Vector2(0, 96);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _EnterTree()
    {
        Cvars.Changed += OnCvarChanged;
        // QC configure: SUPER.cellSelect(crosshairToCell(cvar_string("crosshair"))) — set the initial selection
        // without re-writing the cvar.
        SetSelected(MenuPickerMath.CrosshairIndexToCell(Cvars.GetString("crosshair")));
    }

    public override void _ExitTree() => Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (name == "crosshair")
            SetSelected(MenuPickerMath.CrosshairIndexToCell(Cvars.GetString("crosshair")));
        if (name is "crosshair" or "crosshair_dot" or "crosshair_dot_size")
            QueueRedraw();
    }

    protected override bool CellIsValid(PickerCell cell)
        => MenuPickerMath.CellToCrosshairIndex(cell) >= 0;

    protected override void OnCellSelect(PickerCell cell)
    {
        int index = MenuPickerMath.CellToCrosshairIndex(cell);
        if (index < 0) return;
        SetSelected(cell);
        Cvars.Set("crosshair", index.ToString(CultureInfo.InvariantCulture)); // QC cvar_set("crosshair", cellToCrosshair)
        Cvars.MarkArchived("crosshair");
    }

    protected override void DrawCell(PickerCell cell, Rect2 rect)
    {
        int index = MenuPickerMath.CellToCrosshairIndex(cell);
        if (index < 0) return;

        Texture2D? tex = MenuSkin.Image($"gfx/crosshair{index}");
        // The skin crosshair-picker tint (SKINCOLOR_CROSSHAIRPICKER_CROSSHAIR ~ the cool body color).
        Color tint = MenuSkin.Text;

        if (tex is not null)
        {
            // QC: aspect-preserved, width = realCellSize.x * 0.95.
            Vector2 src = tex.GetSize();
            float ar = src.Y > 0 ? src.X / src.Y : 1f;
            float w = rect.Size.X * 0.95f;
            float h = ar > 0 ? w / ar : w;
            var sz = new Vector2(w, h);
            Vector2 center = rect.Position + rect.Size * 0.5f;
            DrawTextureRect(tex, new Rect2(center - sz * 0.5f, sz), false, tint);

            if (Cvars.GetFloat("crosshair_dot") != 0f && MenuSkin.Image("gfx/crosshairdot") is { } dot)
            {
                float ds = Cvars.GetFloat("crosshair_dot_size");
                if (ds <= 0f) ds = 1f;
                Vector2 dsz = sz * ds;
                DrawTextureRect(dot, new Rect2(center - dsz * 0.5f, dsz), false, tint);
            }
        }
        else
        {
            // No content repo: a small crosshair glyph so the grid still reads as a picker.
            var font = ThemeDB.FallbackFont;
            DrawString(font, rect.Position + rect.Size * 0.5f + new Vector2(-4, 5), "+",
                HorizontalAlignment.Center, -1, 16, tint);
        }
    }
}
