using System.Collections.Generic;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The interactive scoreboard's two pure algorithms — the TAB panel cycle and the Up/Down selection walk —
/// ported from <c>HUD_Scoreboard_InputEvent</c> (qcsrc/client/hud/panel/scoreboard.qc:231-505), plus the column
/// title condense factor from <c>Scoreboard_FixColumnWidth</c> (:1275-1283).
///
/// The panel lives in the Godot host assembly (<c>VortexArena.Game.Hud</c>), which this test project does not
/// reference — so, following the established repo idiom (see <c>HudConfigEditorTests</c>), these mirror the
/// algorithms VERBATIM from the same QC source and assert the Base behaviour. Keep them byte-equivalent to
/// <c>ScoreboardPanel.CyclePanel</c> / <c>MoveSelection</c> / <c>MeasureNumericColumn</c>.
/// </summary>
public class ScoreboardUiNavigationTests
{
    private const int PanelScoreboard = 1;
    private const int PanelRankings = 2;

    /// <summary>Mirror of <c>ScoreboardPanel.CyclePanel</c> (QC scoreboard.qc:288-311): wrap around, and SKIP the
    /// Rankings panel when there are no records — a TAB must never park focus on an empty panel.</summary>
    private static int CyclePanel(int selected, int dir, int rankingsCount)
    {
        int p = selected + dir;
        if (p == PanelRankings && rankingsCount == 0) p += dir;
        if (p < PanelScoreboard) p = PanelRankings;
        if (p > PanelRankings) p = PanelScoreboard;
        if (p == PanelRankings && rankingsCount == 0) p = PanelScoreboard;
        return p;
    }

    /// <summary>Mirror of <c>ScoreboardPanel.MoveSelection</c> (QC scoreboard.qc:318-408): step through the list;
    /// starting from "nothing selected" a forward step lands on the first entry and a back step on the last;
    /// stepping off either end returns to "nothing selected" (QC's <c>curr_pl == selected ? NULL</c> wrap).</summary>
    private static int Step(IReadOnlyList<int> items, int selected, int dir, int none = -1)
    {
        if (items.Count == 0) return none;
        int i = -1;
        for (int k = 0; k < items.Count; k++) if (items[k] == selected) { i = k; break; }
        int next = i < 0 ? (dir > 0 ? 0 : items.Count - 1) : i + dir;
        return (next < 0 || next >= items.Count) ? none : items[next];
    }

    // =====================================================================================
    //  TAB panel cycle
    // =====================================================================================

    [Fact]
    public void TabCycle_WithRankings_WrapsBothWays()
    {
        Assert.Equal(PanelRankings, CyclePanel(PanelScoreboard, +1, rankingsCount: 3));
        Assert.Equal(PanelScoreboard, CyclePanel(PanelRankings, +1, rankingsCount: 3)); // wraps forward
        Assert.Equal(PanelScoreboard, CyclePanel(PanelRankings, -1, rankingsCount: 3));
        Assert.Equal(PanelRankings, CyclePanel(PanelScoreboard, -1, rankingsCount: 3)); // wraps back
    }

    [Fact]
    public void TabCycle_WithoutRankings_StaysOnTheScoreboard()
    {
        // QC skips SB_PANEL_RANKINGS when rankings_cnt is 0 — there is nothing there to focus.
        Assert.Equal(PanelScoreboard, CyclePanel(PanelScoreboard, +1, rankingsCount: 0));
        Assert.Equal(PanelScoreboard, CyclePanel(PanelScoreboard, -1, rankingsCount: 0));
    }

    // =====================================================================================
    //  Up/Down selection walk
    // =====================================================================================

    [Fact]
    public void DownFromNothing_SelectsTheFirstEntry()
    {
        var rows = new[] { 10, 20, 30 };
        Assert.Equal(10, Step(rows, selected: -1, dir: +1));
    }

    [Fact]
    public void UpFromNothing_SelectsTheLastEntry()
    {
        var rows = new[] { 10, 20, 30 };
        Assert.Equal(30, Step(rows, selected: -1, dir: -1));
    }

    [Fact]
    public void SteppingPastEitherEnd_ClearsTheSelection()
    {
        // QC: `if (curr_pl == scoreboard_selected_player) curr_pl = NULL;` — the loop reached the last entry, so
        // one more step deselects rather than sticking or wrapping.
        var rows = new[] { 10, 20, 30 };
        Assert.Equal(-1, Step(rows, selected: 30, dir: +1));
        Assert.Equal(-1, Step(rows, selected: 10, dir: -1));
    }

    [Fact]
    public void WalkingTheWholeListVisitsEveryRowInOrder()
    {
        var rows = new[] { 7, 3, 9, 1 };   // the sorted scoreboard order, not sorted by value
        var seen = new List<int>();
        int sel = -1;
        for (int i = 0; i < rows.Length; i++) { sel = Step(rows, sel, +1); seen.Add(sel); }
        Assert.Equal(rows, seen);
        Assert.Equal(-1, Step(rows, sel, +1)); // and then off the end
    }

    [Fact]
    public void EmptyList_NeverSelectsAnything()
    {
        Assert.Equal(-1, Step(System.Array.Empty<int>(), selected: -1, dir: +1));
        Assert.Equal(-1, Step(System.Array.Empty<int>(), selected: -1, dir: -1));
    }

    // =====================================================================================
    //  Column title condense factor (QC sbt_field_title_condense_factor)
    // =====================================================================================

    /// <summary>
    /// Mirror of the WHOLE of <c>ScoreboardPanel.MeasureNumericColumn</c> (QC <c>Scoreboard_FixColumnWidth</c>'s
    /// non-name branch, scoreboard.qc:1256-1283): the column starts at its title width capped by
    /// <paramref name="titleMaxWidth"/>, grows to the widest value, and the title is condensed by the ratio when
    /// it no longer fits. Widths are passed in directly so the test needs no font.
    /// </summary>
    private static float MeasureColumn(float titleWidth, float titleMaxWidth,
        IReadOnlyList<float> valueWidths, out float condense)
    {
        float w = titleWidth > 0f ? System.Math.Min(titleWidth, titleMaxWidth) : 0f;
        foreach (float v in valueWidths) w = System.Math.Max(w, v);

        condense = 1f;
        if (titleWidth > w && titleWidth > 0f)
        {
            float realMaxWidth = titleWidth > titleMaxWidth ? System.Math.Max(w, titleMaxWidth) : w;
            condense = System.Math.Clamp(realMaxWidth / titleWidth, 0.2f, 1f);
        }
        return w;
    }

    [Fact]
    public void ShortTitle_ColumnSizedToContent_NoCondense()
    {
        // "pl" over 2-char values: the column is the wider of the two, and nothing is squeezed.
        float w = MeasureColumn(titleWidth: 14f, titleMaxWidth: 56f, new[] { 20f, 18f }, out float f);
        Assert.Equal(20f, w, 4);
        Assert.Equal(1f, f, 4);
    }

    [Fact]
    public void LongTitle_IsCappedAtMaxWidthAndCondensedToFit()
    {
        // "suicides" (80px) over a 1-digit column, with the cap at 56px: QC caps the COLUMN at the max width and
        // then squeezes the title into it rather than clipping it or letting it widen the table.
        float w = MeasureColumn(titleWidth: 80f, titleMaxWidth: 56f, new[] { 8f }, out float f);
        Assert.Equal(56f, w, 4);
        Assert.Equal(56f / 80f, f, 4);
        Assert.True(f < 1f);
    }

    [Fact]
    public void LongTitle_WithWideValues_CondensesOnlyToTheGrownColumn()
    {
        // A wide value (a 5-digit score) grows the column past the cap; the title then only needs that much
        // squeezing — QC's `real_maxwidth = max(sbt_field_size[i], title_maxwidth)`.
        float w = MeasureColumn(titleWidth: 80f, titleMaxWidth: 56f, new[] { 70f }, out float f);
        Assert.Equal(70f, w, 4);
        Assert.Equal(70f / 80f, f, 4);
    }

    [Fact]
    public void ValueWiderThanBothTitleAndCap_NeedsNoCondense()
    {
        float w = MeasureColumn(titleWidth: 40f, titleMaxWidth: 56f, new[] { 90f }, out float f);
        Assert.Equal(90f, w, 4);
        Assert.Equal(1f, f, 4);
    }

    [Fact]
    public void CondenseNeverCollapsesTheTitleToNothing()
    {
        // The port clamps at 0.2 so a pathological ratio still leaves readable glyphs.
        MeasureColumn(titleWidth: 1000f, titleMaxWidth: 1f, new[] { 1f }, out float f);
        Assert.Equal(0.2f, f, 4);
    }
}
