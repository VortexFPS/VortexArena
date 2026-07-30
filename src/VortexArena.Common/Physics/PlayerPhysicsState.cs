using System.Numerics;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// Additional per-player movement state the full PM_ port keeps between ticks — the C# successor to the
/// flat QuakeC fields that <c>sys_phys_update</c> / <c>PlayerJump</c> / <c>PM_jetpack</c> /
/// <c>PM_ClientMovement_UpdateStatus</c> (common/physics/player.qc, ecs/systems/physics.qc) read and
/// write for water / ladders / jetpack / crouch / conveyors. Lives in a NEW partial <see cref="Entity"/>
/// file (Framework namespace) so the physics port carries what it needs without touching
/// <c>Framework/Entity.cs</c> (per the porting constraints). Field names are distinct from existing
/// <see cref="Entity"/> members and from <c>EntityMovementState.cs</c>.
/// </summary>
public partial class Entity
{
    // --- crouch / duck (QC IS_DUCKED / SET_DUCKED, PM_ClientMovement_UpdateStatus) ---
    /// <summary>QC <c>IS_DUCKED(this)</c>: the player is crouched (hull shrunk, wishspeed halved).</summary>
    public bool IsDucked;

    // --- ladders (QC .ladder_entity) ---
    /// <summary>
    /// QC <c>.ladder_entity</c>: the func_ladder / func_water volume the player currently overlaps. When
    /// set, the player uses the ladder movement branch (gravity-free climbing). A func_water ladder also
    /// drives the waterlevel from the volume bounds. Set by the trigger touch each frame; cleared when the
    /// player leaves the volume.
    /// </summary>
    public Entity? LadderEntity;

    // --- conveyors (QC .conveyor) ---
    /// <summary>
    /// QC <c>.conveyor</c>: the active conveyor surface under the player (a trigger that adds a constant
    /// velocity). The movement code subtracts <see cref="ConveyorMoveDir"/> before simulating and adds it
    /// back after, so acceleration is computed in the conveyor's frame. Null when not on a conveyor.
    /// </summary>
    public Entity? ConveyorEntity;
    /// <summary>QC <c>.conveyor.movedir</c>: the velocity the active conveyor imparts (cached for the tick).</summary>
    public Vector3 ConveyorMoveDir;

    // --- jetpack (QC IT_USING_JETPACK, .jetpack_stopped) ---
    /// <summary>QC <c>ITEMS_STAT(this) &amp; IT_USING_JETPACK</c>: the jetpack thrust branch is active this tick.</summary>
    public bool UsingJetpack;
    /// <summary>QC <c>.jetpack_stopped</c>: the jetpack cut out (ran out of fuel) and won't restart until released.</summary>
    public bool JetpackStopped;
    /// <summary>QC <c>ITEMS_STAT(this) &amp; ITEM_Jetpack.m_itemid</c>: the player is carrying a jetpack item.</summary>
    public bool HasJetpack;
    /// <summary>QC <c>ITEMS_STAT(this) &amp; IT_UNLIMITED_AMMO</c>: ammo (incl. jetpack fuel) is unlimited.</summary>
    public bool UnlimitedAmmo;

    // --- water-jump (QC FL_WATERJUMP uses .teleport_time as the safety-net timer + .movedir as the push) ---
    /// <summary>QC <c>.teleport_time</c>: the FL_WATERJUMP safety-net expiry (also a generic teleport debounce).</summary>
    public float TeleportTime;
    // NOTE: the FL_WATERJUMP horizontal push uses QC <c>.movedir</c> — already declared on the Entity
    // partial in <c>Gameplay/MapObjects/MapObjectsCommon.cs</c>, so it is reused here (not redeclared).

    // --- viewloc (QC .viewloc — 2.5D side-scroller volumes; changes swim/ladder behaviour) ---
    /// <summary>
    /// QC <c>.viewloc</c>: a side-scroller view-location volume. When set the swim/ladder code uses the
    /// alternate "drift" handling. Not used by the stock 3D modes; kept for fidelity (null in normal play).
    /// </summary>
    public Entity? ViewLoc;

    // --- specialcommand (QC .specialcommand_pos — the xwxw... button cheat-code decoder) ---
    /// <summary>
    /// QC <c>.specialcommand_pos</c> (common/physics/player.qc): the position pointer into the secret
    /// movement-input cheat sequence <c>"xwxwxsxsxaxdxaxdx1x "</c>. Each server tick the button mask is
    /// decoded to a character; matching the next character advances this, a mismatch resets it, and
    /// completing the sequence triggers <c>SpecialCommand</c> (give-all when cheats are allowed).
    /// </summary>
    public int SpecialCommandPos;

    // --- previous-tick wishmove (QC PHYS_CS(this).movement_old) ---
    /// <summary>
    /// QC <c>PHYS_CS(this).movement_old.x</c> (saved at ecs/systems/physics.qc:24 each tick): the previous
    /// tick's forward wishmove. Only the viewloc (1D-rail) ladder sub-case reads it (physics.qc:277-278:
    /// <c>wishvel.z = movement_old.x</c>). Inert on stock 3D maps (ViewLoc null). Updated in the postupdate
    /// bookkeeping after the branch chain.
    /// </summary>
    public float MovementForwardOld;
}
