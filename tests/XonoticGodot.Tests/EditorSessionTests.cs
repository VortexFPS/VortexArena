using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the editor's EDIT ↔ PLAYTEST transition (design doc §11.3).
///
/// The behaviour under test is the one the engine did NOT already have: entering PLAYTEST must place the
/// player exactly where the free-fly camera was, keeping the view direction. Every pre-existing path into a
/// live player ran spawn-point selection and overwrote both — which would teleport a mapper across the level
/// the instant they tried to test the thing they were standing in front of, making the toggle useless.
/// </summary>
public class EditorSessionTests
{
    public EditorSessionTests()
    {
        GameRegistries.Bootstrap();
        Api.Services = new EngineServices(new CollisionWorld());
    }

    [Fact]
    public void EnteringPlaytest_KeepsPositionAndViewAngles()
    {
        var p = new Player
        {
            Flags = EntFlags.Client,
            IsObserver = true,
            Origin = new NVec3(1024f, -512f, 96f),
            Angles = new NVec3(-15f, 130f, 0f),
        };

        Assert.Equal(EditorState.Edit, EditorSession.StateOf(p));
        Assert.True(EditorSession.TryEnterPlaytest(p));

        Assert.Equal(EditorState.Playtest, EditorSession.StateOf(p));
        Assert.False(p.IsObserver);
        Assert.Equal(MoveType.Walk, p.MoveType);

        // Position is preserved. PutPlayerInServer applies a small placement nudge on top of the origin it is
        // handed, so allow for that rather than demanding bit-equality — what matters is that the player did
        // not get relocated to a spawn point somewhere else in the map.
        Assert.Equal(1024f, p.Origin.X, 1);
        Assert.Equal(-512f, p.Origin.Y, 1);
        Assert.True(MathF.Abs(p.Origin.Z - 96f) < 8f, $"z moved too far: {p.Origin.Z}");

        // View direction is preserved, and FixAngle is asserted so the client snaps to it rather than keeping
        // whatever the spawn path wrote.
        Assert.Equal(-15f, p.Angles.X, 3);
        Assert.Equal(130f, p.Angles.Y, 3);
        Assert.Equal(0f, p.Angles.Z, 3);
        Assert.True(p.FixAngle);
        Assert.Equal(130f, p.FixAngleAngles.Y, 3);
    }

    [Fact]
    public void PlaytestSpawn_ClearsTheDeadAndCorpseState()
    {
        // Toggling into playtest after dying in a previous playtest must produce a clean live player, not a
        // corpse that can never be damaged again.
        var p = new Player
        {
            Flags = EntFlags.Client,
            IsObserver = true,
            Origin = new NVec3(0f, 0f, 64f),
            DeadState = DeadFlag.Dead,
            IsCorpse = true,
        };

        Assert.True(EditorSession.TryEnterPlaytest(p));

        Assert.Equal(DeadFlag.No, p.DeadState);
        Assert.False(p.IsCorpse);
        Assert.False(p.OwnedWeaponSet.IsEmpty);   // a real loadout, so the level can actually be tested
    }

    [Fact]
    public void StateOf_TracksTheObserverFlag()
    {
        // EDIT is modelled as the observer state rather than a parallel flag, so free-fly, the spectator speed
        // ladder and Space/Crouch vertical movement all come for free instead of being re-implemented.
        var p = new Player { Flags = EntFlags.Client, IsObserver = true };
        Assert.Equal(EditorState.Edit, EditorSession.StateOf(p));

        p.IsObserver = false;
        Assert.Equal(EditorState.Playtest, EditorSession.StateOf(p));
    }

    [Fact]
    public void EditorGametype_IsRegisteredAndInert()
    {
        // The source generator discovers [GameType] classes, so the mode is selectable via `gametype editor`
        // with no registry edit. It must also stay scoreless: an editor session has no winner.
        GameType? editor = GameTypes.ByName("editor");

        Assert.NotNull(editor);
        Assert.IsType<EditorMode>(editor);
        Assert.False(editor!.TeamGame);
        Assert.False(editor.ReportsTie(new List<Player>()));
        Assert.False(string.IsNullOrEmpty(editor.MenuDescription));
    }
}
