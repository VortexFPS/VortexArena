using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>Which editor state a client is in (design doc §11.3).</summary>
public enum EditorState
{
    /// <summary>Free-flying and manipulating geometry — an observer, so Space rises and Crouch descends.</summary>
    Edit,

    /// <summary>A real player with full movement physics, running the level as a player would.</summary>
    Playtest,
}

/// <summary>
/// The EDIT ↔ PLAYTEST state machine of the <see cref="EditorMode"/> gametype.
///
/// The whole point is that toggling is INSTANT and IN PLACE: you fly to a wall you just moved, drop into
/// PLAYTEST to check you can jump onto it, and pop back — without a respawn, a teleport, or losing your view
/// direction. The engine already had one half of this (going observer preserves the origin, because a
/// spectator free-flies from where it was), but the other half did not exist: every path into a live player
/// ran spawn-point selection and overwrote position and angles. This supplies the missing half by placing the
/// player at a <see cref="SpawnPoint"/> built from its CURRENT transform, which reuses the whole
/// loadout/reset/hook pipeline and substitutes only the placement.
/// </summary>
public static class EditorSession
{
    /// <summary>Vertical search range, in Quake units, when nudging a playtest spawn out of solid.</summary>
    private const float MaxRise = 72f;

    /// <summary>The state a player is currently in. EDIT is modelled as the observer state.</summary>
    public static EditorState StateOf(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.IsObserver ? EditorState.Edit : EditorState.Playtest;
    }

    /// <summary>
    /// Flip the player's state, preserving position and view angles. Returns the state actually reached — it
    /// can equal the previous state when entering PLAYTEST was refused (see <see cref="TryEnterPlaytest"/>).
    /// </summary>
    public static EditorState Toggle(GameWorld world, Player p, out string message)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(p);

        if (StateOf(p) == EditorState.Playtest)
        {
            EnterEdit(world, p);
            message = "editor: EDIT (free-fly)";
            return EditorState.Edit;
        }

        if (TryEnterPlaytest(p))
        {
            message = "editor: PLAYTEST";
            return EditorState.Playtest;
        }

        message = "editor: cannot playtest here — no room for a player at this position";
        return EditorState.Edit;
    }

    /// <summary>
    /// Drop out of playtesting back into free-fly, keeping the transform.
    /// <c>PutObserverInServer</c> deliberately never touches origin or angles (an observer free-flies from
    /// wherever it was), so this direction needs no special handling.
    /// </summary>
    public static void EnterEdit(GameWorld world, Player p)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(p);
        world.Clients.PutObserverInServer(p);
    }

    /// <summary>
    /// Spawn the player as a live player AT ITS CURRENT POSITION rather than at a map spawn point.
    ///
    /// Returns false when the player hull cannot be placed anywhere from the current position up to
    /// <see cref="MaxRise"/> units above it — i.e. the camera is buried inside geometry. Refusing is the right
    /// call: silently teleporting the mapper to a real spawn point elsewhere in the level would lose exactly
    /// the context they toggled in order to test.
    /// </summary>
    public static bool TryEnterPlaytest(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (!TryFindClearOrigin(p, p.Origin, out Vector3 origin))
            return false;

        // Keep pitch/yaw; zero roll, matching what spawn placement does for a map spawn point.
        Vector3 angles = new(p.Angles.X, p.Angles.Y, 0f);

        // Source: null so no spawnpoint 'target' fires — this is not a real spawn event, and triggering a
        // mapper's spawn-linked logic every time they playtest would be wrong.
        SpawnSystem.PutPlayerInServer(p, new SpawnPoint(origin, angles), warmup: false);

        // PutPlayerInServer resets the physics/loadout state but does NOT clear the observer flag — that lives
        // on ClientManager's join/spawn path, which we deliberately bypass to keep our own placement. Clearing
        // it here is what actually makes the player live; without it the client stays flagged as observing and
        // the HUD, the scoreboard and this session's own state machine all still read EDIT.
        p.IsObserver = false;

        // PutPlayerInServer applies its own placement nudge on top of the origin it is given; re-assert the
        // view angles afterwards so the client's camera does not snap away from where the mapper was looking.
        p.Angles = angles;
        p.FixAngle = true;
        p.FixAngleAngles = angles;
        return true;
    }

    /// <summary>
    /// Find a position at or above <paramref name="from"/> where the player hull is not embedded in solid,
    /// mirroring the spawn system's move-out-of-solid step search.
    /// </summary>
    private static bool TryFindClearOrigin(Player p, Vector3 from, out Vector3 origin)
    {
        origin = from;
        if (Api.Services is null)
            return true; // headless/unit-test context with no collision: nothing to test against

        for (float dz = 0f; dz <= MaxRise; dz += 2f)
        {
            Vector3 candidate = from + new Vector3(0f, 0f, dz);
            TraceResult t = Api.Trace.Trace(
                candidate, SpawnSystem.PlayerMins, SpawnSystem.PlayerMaxs, candidate, MoveFilter.NoMonsters, p);
            if (!t.StartSolid)
            {
                origin = candidate;
                return true;
            }
        }
        return false;
    }
}
