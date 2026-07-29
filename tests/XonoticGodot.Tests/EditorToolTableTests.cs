using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The tool/mode vocabulary tables (<see cref="EditorTools"/>) checked against themselves.
///
/// These are six switch statements over one enum, read by three consumers that must not disagree — the
/// context menu's rows, the HUD action line, and the controller's dispatch. Adding an <see cref="EditorTool"/>
/// member and updating five of the six is the failure this catches, and it is invisible until a mapper
/// switches to the tool and finds it has no modes, cannot be cycled to, or renders as its enum name.
/// </summary>
public class EditorToolTableTests
{
    /// <summary>Every declared tool appears in the menu order exactly once.</summary>
    [Fact]
    public void AllListsEveryToolOnce()
    {
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            int seen = 0;
            foreach (EditorTool listed in EditorTools.All)
                if (listed == tool)
                    seen++;
            Assert.True(seen == 1, $"{tool} appears {seen} times in EditorTools.All");
        }
        Assert.Equal(Enum.GetValues<EditorTool>().Length, EditorTools.All.Count);
    }

    /// <summary>
    /// A tool that says it works has to offer a mode and a name of its own. Falling through to the enum's
    /// ToString is what a half-added member looks like from the menu.
    /// </summary>
    [Theory]
    [MemberData(nameof(ImplementedTools))]
    public void AnImplementedToolHasModesAndALabel(EditorTool tool)
    {
        if (tool == EditorTool.None)
            return;      // None is the "look at the map" state: no modes is the point of it

        Assert.NotEmpty(EditorTools.ModesFor(tool));
        Assert.NotEqual(ToolMode.None, EditorTools.DefaultMode(tool));
        Assert.True(EditorTools.Supports(tool, EditorTools.DefaultMode(tool)));
    }

    /// <summary>
    /// Every mode any tool offers is spelled out for a menu. A one-word mode legitimately renders as its own
    /// name ("Move"); a COMPOUND one falling through to the enum name is the tell — the default arm would put
    /// "ShiftUv" and "PlaceJump" in front of a mapper.
    /// </summary>
    [Fact]
    public void EveryOfferedModeHasASpelledOutLabel()
    {
        foreach (EditorTool tool in EditorTools.All)
            foreach (ToolMode mode in EditorTools.ModesFor(tool))
            {
                string name = mode.ToString();
                string label = EditorTools.Label(mode);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{mode} has no label");

                bool compound = false;
                for (int i = 1; i < name.Length; i++)
                    if (char.IsUpper(name[i]))
                        compound = true;
                if (compound)
                    Assert.True(label != name, $"{mode} falls through to its enum name");
            }
    }

    [Fact]
    public void EveryToolHasALabel()
    {
        foreach (EditorTool tool in EditorTools.All)
            Assert.False(string.IsNullOrWhiteSpace(EditorTools.Label(tool)));
    }

    /// <summary>
    /// Carrying a mode across a tool switch must always land on something the new tool offers, or the editor
    /// sits in a state its own menu would refuse to show.
    /// </summary>
    [Fact]
    public void CarryModeAlwaysLandsOnAModeTheNewToolOffers()
    {
        foreach (EditorTool tool in EditorTools.All)
            foreach (ToolMode mode in Enum.GetValues<ToolMode>())
            {
                ToolMode carried = EditorTools.CarryMode(tool, mode);
                if (EditorTools.ModesFor(tool).Count == 0)
                {
                    Assert.Equal(ToolMode.None, carried);
                    continue;
                }
                Assert.True(EditorTools.Supports(tool, carried),
                    $"{tool} carried {mode} to {carried}, which it does not offer");
            }
    }

    // ---------------------------------------------------------------- the light tool (backlog T2)

    /// <summary>
    /// A light IS an entity — the tool is a partition of the entity set, not a new object kind — so it must
    /// pick as one, or every entity op silently stops applying to lights.
    /// </summary>
    [Fact]
    public void TheLightToolPicksEntities()
    {
        Assert.Equal(VmapSelectionKind.Entity, EditorTools.PickKind(EditorTool.Light));
        Assert.Equal(VmapSelectionKind.Entity, EditorTools.PickKind(EditorTool.Entity));
    }

    [Fact]
    public void TheLightToolIsInTheKeyboardCycle()
        => Assert.True(EditorTools.IsImplemented(EditorTool.Light));

    /// <summary>
    /// No Rotate (a point light's aim is a `target` key, not an angle) and no Scale (a bigger light is a
    /// bigger `light` value). Pinned because both would draw handles that then do nothing.
    /// </summary>
    [Fact]
    public void TheLightToolOffersNoRotateOrScale()
    {
        Assert.False(EditorTools.Supports(EditorTool.Light, ToolMode.Rotate));
        Assert.False(EditorTools.Supports(EditorTool.Light, ToolMode.Scale));
        Assert.True(EditorTools.Supports(EditorTool.Light, ToolMode.Move));
        Assert.True(EditorTools.Supports(EditorTool.Light, ToolMode.Properties));
        Assert.True(EditorTools.Supports(EditorTool.Light, ToolMode.Create));
    }

    /// <summary>Entity→Light while rotating has to land somewhere Light can actually do.</summary>
    [Fact]
    public void SwitchingFromEntityWhileRotatingFallsBackToMove()
        => Assert.Equal(ToolMode.Move, EditorTools.CarryMode(EditorTool.Light, ToolMode.Rotate));

    public static TheoryData<EditorTool> ImplementedTools()
    {
        var data = new TheoryData<EditorTool>();
        foreach (EditorTool tool in EditorTools.All)
            if (EditorTools.IsImplemented(tool))
                data.Add(tool);
        return data;
    }
}
