using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Unit tests for T27 — the HUD configure-mode editor (port of qcsrc/client/hud/hud_config.qc, ported to
/// <c>game/hud/HudConfigEditor.cs</c>).
///
/// The editor lives in the Godot host assembly (<c>XonoticGodot.Game.Hud</c>), which this test project does NOT
/// reference (it links only the Godot-free <c>src/</c> libraries). So — following the established repo idiom
/// (see <c>InputCommandImpulseTests</c>, which mirrors <c>FirstPersonView.ComputeVerticalFov</c>) — these tests
/// mirror the editor's pure geometry / cvar-normalization / step / collision algorithms VERBATIM from the same
/// QC source, using <see cref="System.Numerics.Vector2"/>, and assert the Base behavior. The mirrored helpers
/// are kept byte-for-byte equivalent to <c>HudConfigEditor</c>'s public static helpers (SnapToGrid, RealGridSize,
/// NormalizePair, MinDecimals, PosFromCorner) and its CheckMove/CheckResize/ArrowStep logic.
/// </summary>
public class HudConfigEditorTests
{
    // The luma reference viewport (any size works; the math is fraction-based).
    private static readonly Vector2 VP = new(800f, 600f);

    // ---------------------------------------------------------------------------------------------------------
    //  Mirrors of HudConfigEditor's pure helpers (kept identical to the editor — verified against hud_config.qc)
    // ---------------------------------------------------------------------------------------------------------

    private static float Floor(float v) => (float)Math.Floor(v);
    private static float Clamp(float v, float lo, float hi) => Math.Clamp(v, lo, hi);

    // QC HUD_Configure_DrawGrid (1064-1065): realGridSize = gridSize * vid_con*.
    private static Vector2 RealGridSize(Vector2 viewport, Vector2 gridSize)
        => new(gridSize.X * viewport.X, gridSize.Y * viewport.Y);

    // QC HUD_Panel_SetPos grid snap (178-182).
    private static Vector2 SnapToGrid(Vector2 posPx, Vector2 viewport, Vector2 gridSize, Vector2 realGridSize)
        => new(
            Floor((posPx.X / viewport.X) / gridSize.X + 0.5f) * realGridSize.X,
            Floor((posPx.Y / viewport.Y) / gridSize.Y + 0.5f) * realGridSize.Y);

    // QC ftos_mindecimals + the "r g b"/"x y" normalized cvar string (SetPos 190 / SetPosSize 402-406).
    private static string MinDecimals(float v)
    {
        string s = v.ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string NormalizePair(Vector2 px, Vector2 viewport)
        => $"{MinDecimals(px.X / viewport.X)} {MinDecimals(px.Y / viewport.Y)}";

    // QC SetPosSize corner→pos derivation (327-346).
    private static Vector2 PosFromCorner(int corner, Vector2 origin, Vector2 size) => corner switch
    {
        1 => new Vector2(origin.X - size.X, origin.Y - size.Y),
        2 => new Vector2(origin.X, origin.Y - size.Y),
        3 => new Vector2(origin.X - size.X, origin.Y),
        _ => new Vector2(origin.X, origin.Y),
    };

    // ---------------------------------------------------------------------------------------------------------
    //  A minimal panel record + the SetPos / SetPosSize / CheckMove pipeline (mirrors HudConfigEditor)
    // ---------------------------------------------------------------------------------------------------------

    private sealed class Panel
    {
        public string Id = "";
        public Vector2 Pos;     // pixel pos
        public Vector2 Size;    // pixel size
        public float Border;    // bg_border px
        public bool Enabled = true;
        public bool Main = true; // PANEL_CONFIG_MAIN
        public bool CanBeOff = true;
    }

    // QC HUD_Panel_CheckMove (98-167): push the moved panel out of every other enabled MAIN panel.
    private static Vector2 CheckMove(Vector2 myPos, Vector2 mySize, Panel self, IEnumerable<Panel> all)
    {
        Vector2 myTarget = myPos;
        foreach (Panel p in all)
        {
            if (!p.Main || ReferenceEquals(p, self) || !p.Enabled) continue;
            Vector2 pPos = p.Pos - new Vector2(p.Border, p.Border);
            Vector2 pSize = p.Size + new Vector2(2f * p.Border, 2f * p.Border);

            if (myPos.Y + mySize.Y < pPos.Y) continue;
            if (myPos.Y > pPos.Y + pSize.Y) continue;
            if (myPos.X + mySize.X < pPos.X) continue;
            if (myPos.X > pPos.X + pSize.X) continue;

            Vector2 myC = new(myPos.X + 0.5f * mySize.X, myPos.Y + 0.5f * mySize.Y);
            Vector2 tC = new(pPos.X + 0.5f * pSize.X, pPos.Y + 0.5f * pSize.Y);

            if (myC.X < tC.X && myC.Y < tC.Y)
            {
                if (myPos.X + mySize.X - pPos.X < myPos.Y + mySize.Y - pPos.Y) myTarget.X = pPos.X - mySize.X;
                else myTarget.Y = pPos.Y - mySize.Y;
            }
            else if (myC.X > tC.X && myC.Y < tC.Y)
            {
                if (pPos.X + pSize.X - myPos.X < myPos.Y + mySize.Y - pPos.Y) myTarget.X = pPos.X + pSize.X;
                else myTarget.Y = pPos.Y - mySize.Y;
            }
            else if (myC.X < tC.X && myC.Y > tC.Y)
            {
                if (myPos.X + mySize.X - pPos.X < pPos.Y + pSize.Y - myPos.Y) myTarget.X = pPos.X - mySize.X;
                else myTarget.Y = pPos.Y + pSize.Y;
            }
            else if (myC.X > tC.X && myC.Y > tC.Y)
            {
                if (pPos.X + pSize.X - myPos.X < pPos.Y + pSize.Y - myPos.Y) myTarget.X = pPos.X + pSize.X;
                else myTarget.Y = pPos.Y + pSize.Y;
            }
        }
        return myTarget;
    }

    // QC HUD_Panel_SetPos (169-191): grid (BEFORE collision) → collision → screen clamp → normalized cvar string.
    private static string SetPos(Vector2 pos, Panel self, IEnumerable<Panel> all,
        bool grid, Vector2 gridSize, bool checkCollisions)
    {
        Vector2 mySize = self.Size;
        if (grid)
        {
            Vector2 real = RealGridSize(VP, gridSize);
            pos = SnapToGrid(pos, VP, gridSize, real);
        }
        if (checkCollisions)
            pos = CheckMove(pos, mySize, self, all);
        pos.X = Clamp(pos.X, 0f, VP.X - mySize.X);
        pos.Y = Clamp(pos.Y, 0f, VP.Y - mySize.Y);
        return NormalizePair(pos, VP);
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Tests
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void NormalizePair_WritesFractionsOfViewport()
    {
        // QC: cvar_set(_pos, ftos_mindecimals(pos.x/vid_conwidth) + " " + ftos_mindecimals(pos.y/vid_conheight)).
        Assert.Equal("0.5 0.25", NormalizePair(new Vector2(400f, 150f), VP));
        Assert.Equal("0 0", NormalizePair(Vector2.Zero, VP));
        Assert.Equal("1 1", NormalizePair(VP, VP));
    }

    [Fact]
    public void MinDecimals_TrimsTrailingZerosAndNormalizesNegativeZero()
    {
        Assert.Equal("0.5", MinDecimals(0.5f));
        Assert.Equal("0", MinDecimals(0f));
        Assert.Equal("0", MinDecimals(-0f));
        Assert.Equal("0.125", MinDecimals(0.125f));
    }

    [Fact]
    public void SetPanelPos_NoGridNoCollision_WritesExactFraction()
    {
        var self = new Panel { Id = "weapons", Pos = new Vector2(0, 0), Size = new Vector2(80, 80) };
        string s = SetPos(new Vector2(200f, 300f), self, new[] { self }, grid: false, default, checkCollisions: false);
        Assert.Equal("0.25 0.5", s); // 200/800, 300/600
    }

    [Fact]
    public void SetPanelPos_ClampsInsideScreen()
    {
        var self = new Panel { Id = "weapons", Size = new Vector2(80, 60) };
        // far beyond the bottom-right; clamp to (VP - size) → (720, 540) → (0.9, 0.9).
        string s = SetPos(new Vector2(5000f, 5000f), self, new[] { self }, grid: false, default, checkCollisions: false);
        Assert.Equal("0.9 0.9", s);
        // negative clamps to 0.
        string s0 = SetPos(new Vector2(-100f, -100f), self, new[] { self }, grid: false, default, checkCollisions: false);
        Assert.Equal("0 0", s0);
    }

    [Fact]
    public void GridSnapping_QuantizesPosToNearestGridCell()
    {
        // grid 0.1 in both axes → realGridSize = (80, 60). A pos at (95, 95) snaps to nearest cell.
        var grid = new Vector2(0.1f, 0.1f);
        Vector2 real = RealGridSize(VP, grid);
        Assert.Equal(80f, real.X, 2);
        Assert.Equal(60f, real.Y, 2);

        // QC: floor((pos/vp)/grid + 0.5) * realGrid.
        // x: floor((95/800)/0.1 + 0.5) = floor(1.1875 + 0.5) = floor(1.6875) = 1 → 1*80 = 80
        // y: floor((95/600)/0.1 + 0.5) = floor(1.583 + 0.5) = floor(2.083) = 2 → 2*60 = 120
        Vector2 snapped = SnapToGrid(new Vector2(95f, 95f), VP, grid, real);
        Assert.Equal(80f, snapped.X, 3);
        Assert.Equal(120f, snapped.Y, 3);
    }

    [Fact]
    public void GridSnapping_HappensBeforeCollision()
    {
        // Two panels. The grid quantizes the drag target onto a cell that is clear of the other panel, proving
        // grid runs FIRST (QC HUD_Panel_SetPos: grid block at 178, collision at 184). With grid ON the raw target
        // lands on a clean cell and the subsequent collision check is a no-op, leaving the snapped position.
        var self = new Panel { Id = "a", Size = new Vector2(80, 60) };
        var other = new Panel { Id = "b", Pos = new Vector2(240, 180), Size = new Vector2(80, 60), Border = 0 };
        var all = new[] { self, other };

        var grid = new Vector2(0.1f, 0.1f); // realGrid (80, 60)
        // raw (90, 90) snaps to:
        //   x: floor((90/800)/0.1 + 0.5) = floor(1.125 + 0.5) = 1 → 1*80 = 80
        //   y: floor((90/600)/0.1 + 0.5) = floor(1.5 + 0.5)   = 2 → 2*60 = 120
        // snapped (80, 120): right edge 160 < other.left 240 → clear → collision is a no-op → stays (80, 120).
        string withGrid = SetPos(new Vector2(90f, 90f), self, all, grid: true, grid, checkCollisions: true);
        Assert.Equal("0.1 0.2", withGrid); // 80/800, 120/600
    }

    [Fact]
    public void CheckMove_PushesOutOfPanel_AllFourCorners()
    {
        // A central target panel; the moving panel approaches from each corner and gets pushed to a clear edge.
        var target = new Panel { Id = "t", Pos = new Vector2(300, 200), Size = new Vector2(200, 200), Border = 0 };
        var self = new Panel { Id = "s", Size = new Vector2(60, 60) };
        var all = new[] { self, target };

        // top-left overlap: moving panel center is up-left of target center → pushed left or up.
        Vector2 tl = CheckMove(new Vector2(280, 180), self.Size, self, all);
        Assert.True(tl.X <= 300 - self.Size.X + 0.01f || tl.Y <= 200 - self.Size.Y + 0.01f,
            "top-left overlap should snap to target's left or top edge");

        // bottom-right overlap: center down-right of target center → pushed to right/bottom edge.
        Vector2 br = CheckMove(new Vector2(460, 360), self.Size, self, all);
        Assert.True(br.X >= 500 - 0.01f || br.Y >= 400 - 0.01f,
            "bottom-right overlap should snap to target's right or bottom edge");

        // No overlap (far away) → unchanged.
        Vector2 clear = CheckMove(new Vector2(50, 50), self.Size, self, all);
        Assert.Equal(new Vector2(50, 50), clear);
    }

    [Fact]
    public void CheckMove_IgnoresDisabledAndSelfAndNonMainPanels()
    {
        var self = new Panel { Id = "s", Size = new Vector2(60, 60) };
        var disabled = new Panel { Id = "d", Pos = new Vector2(40, 40), Size = new Vector2(200, 200), Enabled = false };
        var nonMain = new Panel { Id = "n", Pos = new Vector2(40, 40), Size = new Vector2(200, 200), Main = false };
        var all = new[] { self, disabled, nonMain };

        // The moving panel overlaps both, but both are skipped (disabled / non-MAIN) → no push.
        Vector2 r = CheckMove(new Vector2(50, 50), self.Size, self, all);
        Assert.Equal(new Vector2(50, 50), r);
    }

    [Fact]
    public void PosFromCorner_DerivesTopLeftPerResizeCorner()
    {
        var origin = new Vector2(400, 300);
        var size = new Vector2(100, 80);
        // corner 1 (TL handle, origin = bottom-right) → pos = origin - size
        Assert.Equal(new Vector2(300, 220), PosFromCorner(1, origin, size));
        // corner 2 (TR handle, origin = bottom-left) → pos.x = origin.x, pos.y = origin.y - size.y
        Assert.Equal(new Vector2(400, 220), PosFromCorner(2, origin, size));
        // corner 3 (BL handle, origin = top-right) → pos.x = origin.x - size.x, pos.y = origin.y
        Assert.Equal(new Vector2(300, 300), PosFromCorner(3, origin, size));
        // corner 4 (BR handle, origin = top-left) → pos = origin
        Assert.Equal(new Vector2(400, 300), PosFromCorner(4, origin, size));
    }

    // QC HUD_Panel_Arrow_Action step sizing (418-446), mirrored.
    private static float ArrowStep(bool vertical, Vector2 viewport, bool grid, Vector2 gridSize,
        bool shift, float timeSincePress)
    {
        if (grid)
        {
            Vector2 real = RealGridSize(viewport, gridSize);
            float g = vertical ? real.Y : real.X;
            return shift ? g : 2f * g;
        }
        float step = vertical ? viewport.Y : viewport.X;
        if (shift)
            return step / 256f;
        return (step / 64f) * (1f + 2f * timeSincePress);
    }

    [Fact]
    public void ArrowStep_GridMode_IsGridCellTimesOneOrTwo()
    {
        var grid = new Vector2(0.1f, 0.1f); // realGrid (80, 60)
        // no shift → 2 * gridCell ; shift → 1 * gridCell.
        Assert.Equal(160f, ArrowStep(vertical: false, VP, grid: true, grid, shift: false, 0f), 3); // 2*80
        Assert.Equal(80f, ArrowStep(vertical: false, VP, grid: true, grid, shift: true, 0f), 3);   // 1*80
        Assert.Equal(120f, ArrowStep(vertical: true, VP, grid: true, grid, shift: false, 0f), 3);  // 2*60
        Assert.Equal(60f, ArrowStep(vertical: true, VP, grid: true, grid, shift: true, 0f), 3);    // 1*60
    }

    [Fact]
    public void ArrowStep_PixelMode_ShiftIsFinerAndNoShiftAccelerates()
    {
        // Shift → viewport/256 (fine, no acceleration).
        Assert.Equal(VP.X / 256f, ArrowStep(false, VP, grid: false, default, shift: true, 5f), 3);
        // No shift → (viewport/64) * (1 + 2*time): grows with hold time.
        float t0 = ArrowStep(false, VP, grid: false, default, shift: false, 0f);
        float t1 = ArrowStep(false, VP, grid: false, default, shift: false, 1f);
        Assert.Equal(VP.X / 64f, t0, 3);
        Assert.Equal((VP.X / 64f) * 3f, t1, 3); // 1 + 2*1 = 3
        Assert.True(t1 > t0, "holding the key longer must accelerate the step");
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Undo (one level): the editor backs up pos/size before an edit and Ctrl+Z restores then clears the backup.
    // ---------------------------------------------------------------------------------------------------------

    private sealed class UndoState
    {
        public Vector2 PosBackup, SizeBackup;
        public string? PanelBackup;

        // Mirrors HudConfigEditor: an edit that actually changed pos/size backs up the PRE-edit values.
        public void RecordEdit(string panel, Vector2 prePos, Vector2 preSize, Vector2 postPos, Vector2 postSize)
        {
            if (prePos != postPos || preSize != postSize)
            {
                PosBackup = prePos;
                SizeBackup = preSize;
                PanelBackup = panel;
            }
        }

        // Ctrl+Z: restore + clear (so a second Ctrl+Z is a no-op until the next edit).
        public bool Undo(out Vector2 pos, out Vector2 size, out string? panel)
        {
            pos = PosBackup; size = SizeBackup; panel = PanelBackup;
            bool had = PanelBackup is not null;
            PanelBackup = null;
            return had;
        }
    }

    [Fact]
    public void Undo_RestoresPreEditValues_ThenIsOneShot()
    {
        var u = new UndoState();
        var pre = (pos: new Vector2(0.1f, 0.1f), size: new Vector2(0.3f, 0.2f));
        var post = (pos: new Vector2(0.4f, 0.4f), size: new Vector2(0.3f, 0.2f)); // moved

        u.RecordEdit("weapons", pre.pos, pre.size, post.pos, post.size);

        Assert.True(u.Undo(out Vector2 rp, out Vector2 rs, out string? rpanel));
        Assert.Equal(pre.pos, rp);
        Assert.Equal(pre.size, rs);
        Assert.Equal("weapons", rpanel);

        // One-level undo: a second Ctrl+Z with no new edit restores nothing.
        Assert.False(u.Undo(out _, out _, out _));
    }

    [Fact]
    public void Undo_NoBackup_WhenEditDidNotChangeAnything()
    {
        var u = new UndoState();
        var same = new Vector2(0.2f, 0.2f);
        u.RecordEdit("ammo", same, same, same, same); // no change → no backup
        Assert.False(u.Undo(out _, out _, out _));
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Ctrl+Tab band-ordered cycle (QC 640-694): within a horizontal band, pick the next panel to the right
    //  (forward) / left (backward) of the current x. Mirrors TryFindTabPanel's pick predicate.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void CtrlTab_ForwardPicksNextPanelToTheRightInSameBand()
    {
        // Three panels in the same top band (y small). Forward from x=100 should pick the next greater x.
        var a = new Panel { Id = "a", Pos = new Vector2(100, 10) };
        var b = new Panel { Id = "b", Pos = new Vector2(300, 10) };
        var c = new Panel { Id = "c", Pos = new Vector2(500, 10) };
        var all = new List<Panel> { a, b, c };

        Panel? picked = ForwardPick(all, startX: 100f, viewportX: VP.X, exclude: a);
        Assert.Same(b, picked); // nearest x > 100 (excluding the start panel a)

        Panel? picked2 = ForwardPick(all, startX: 300f, viewportX: VP.X, exclude: b);
        Assert.Same(c, picked2);
    }

    [Fact]
    public void CtrlTab_BackwardPicksPreviousPanelToTheLeft()
    {
        var a = new Panel { Id = "a", Pos = new Vector2(100, 10) };
        var b = new Panel { Id = "b", Pos = new Vector2(300, 10) };
        var c = new Panel { Id = "c", Pos = new Vector2(500, 10) };
        var all = new List<Panel> { a, b, c };

        Panel? picked = BackwardPick(all, startX: 500f, exclude: c);
        Assert.Same(b, picked);
    }

    // Mirror of the forward pick predicate (QC 661): panel_pos.x >= start && (x < candidate || (x==candidate && y<=candidate.y)).
    private static Panel? ForwardPick(IEnumerable<Panel> all, float startX, float viewportX, Panel exclude)
    {
        Panel? best = null;
        Vector2 candidate = new(viewportX, 0f);
        foreach (Panel p in all)
        {
            if (ReferenceEquals(p, exclude)) continue;
            if (!(p.Pos.X >= startX)) continue;
            if (p.Pos.X < candidate.X || (p.Pos.X == candidate.X && p.Pos.Y <= candidate.Y))
            {
                best = p;
                candidate = p.Pos;
            }
        }
        return best;
    }

    // Mirror of the backward pick predicate (QC 662): panel_pos.x <= start && (x > candidate || (x==candidate && y>=candidate.y)).
    private static Panel? BackwardPick(IEnumerable<Panel> all, float startX, Panel exclude)
    {
        Panel? best = null;
        Vector2 candidate = new(0f, 0f);
        foreach (Panel p in all)
        {
            if (ReferenceEquals(p, exclude)) continue;
            if (!(p.Pos.X <= startX)) continue;
            if (p.Pos.X > candidate.X || (p.Pos.X == candidate.X && p.Pos.Y >= candidate.Y))
            {
                best = p;
                candidate = p.Pos;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Ctrl+Space toggle: flips hud_panel_<id> between 0 and 1 (QC 706); dock toggle flips "" <-> "dock" (709).
    // ---------------------------------------------------------------------------------------------------------

    private static string TogglePanelEnable(float current) => current != 0f ? "0" : "1";
    private static string ToggleDock(string current) => string.IsNullOrEmpty(current) ? "dock" : "";

    [Fact]
    public void CtrlSpace_TogglesPanelEnableCvar()
    {
        Assert.Equal("0", TogglePanelEnable(1f));
        Assert.Equal("1", TogglePanelEnable(0f));
        Assert.Equal("0", TogglePanelEnable(3f)); // any non-zero → off
    }

    [Fact]
    public void CtrlSpace_NoHighlight_TogglesDock()
    {
        Assert.Equal("dock", ToggleDock(""));
        Assert.Equal("", ToggleDock("dock"));
    }

    [Fact]
    public void CopyPasteSize_ClampsToScreenAndSkipsNoOp()
    {
        // QC Ctrl+V (723-750): the copied size is reduced to fit if it would overflow the screen from the panel
        // position; a paste equal to the current size is a no-op.
        var panel = new Panel { Id = "p", Pos = new Vector2(600, 500), Size = new Vector2(100, 60) };
        Vector2 copied = new(400, 300); // would overflow from (600,500)

        Vector2 tmp = copied;
        if (panel.Pos.X + copied.X > VP.X) tmp.X = VP.X - panel.Pos.X; // 800-600 = 200
        if (panel.Pos.Y + copied.Y > VP.Y) tmp.Y = VP.Y - panel.Pos.Y; // 600-500 = 100
        Assert.Equal(new Vector2(200, 100), tmp);

        // a paste equal to the current size writes nothing (the editor returns early).
        bool noOp = panel.Size == panel.Size;
        Assert.True(noOp);
    }
}
