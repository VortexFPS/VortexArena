// Port of the per-frame volume-scan brush volumes from qcsrc/common/mapobjects/func/:
//   conveyor.qc -> trigger_conveyor / func_conveyor  (mark overlapping pushables with a conveyor velocity)
//   ladder.qc   -> func_ladder / func_water          (mark overlapping players as climbing a ladder)
//
// These are SELF-THINKING volumes: each frame they FindInBox their own AABB and tag the overlapping
// entities with a per-entity field the MOVEMENT code then consumes. The CONSUMER side is ALREADY ported in
// PlayerPhysics:
//   - conveyor: PlayerPhysics subtracts Entity.ConveyorMoveDir before the slide-move and adds it back after
//     (sys_phys_update), so acceleration is computed in the conveyor's frame.
//   - ladder:   PlayerPhysics uses Entity.LadderEntity for the gravity-free climb branch, and special-cases a
//     func_water ladder to drive the waterlevel from the volume bounds.
// Only the PRODUCER (this file) was missing: the spawnfunc + the per-frame think that assigns those fields.
//
// The volume-overlap pattern is shared with Triggers.SwampThink: FindInBox on the volume's AABB (the precise
// overlap test lives inside the query) + re-tag each frame + reschedule the think for the next frame. The CSQC
// draw/networking halves and the bot waypoint tracetest tail of func_ladder_init (bot navigation only) are out
// of scope.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The per-frame volume-scan brush volumes: trigger_conveyor / func_conveyor (push) and func_ladder /
/// func_water (climb). Each setup is a spawnfunc registered by <see cref="MapObjectsRegistry"/>; the producer
/// thinks assign <see cref="Entity.ConveyorEntity"/> / <see cref="Entity.LadderEntity"/> on overlapping
/// entities for the (already-ported) PlayerPhysics consumer.
/// </summary>
public static class MapVolumes
{
    // ===================================================================
    //  trigger_conveyor / func_conveyor (func/conveyor.qc)
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_conveyor)</c> — a trigger-volume conveyor (no brush model collision).</summary>
    public static void TriggerConveyorSetup(Entity this_)
    {
        MapMover.SetMovedir(this_);
        MapMover.InitTrigger(this_); // WarpZoneLib_ExactTrigger_Init analogue: SOLID_TRIGGER + the bbox
        this_.ClassName = "trigger_conveyor";
        ConveyorInit(this_);
    }

    /// <summary><c>spawnfunc(func_conveyor)</c> — a solid brush conveyor (a moving-brush-style surface, MOVETYPE_NONE).</summary>
    public static void FuncConveyorSetup(Entity this_)
    {
        MapMover.SetMovedir(this_);
        MapMover.InitMovingBrushTrigger(this_);
        this_.MoveType = MoveType.None; // QC set_movetype(this, MOVETYPE_NONE)
        this_.ClassName = "func_conveyor";
        ConveyorInit(this_);
    }

    /// <summary>QC <c>conveyor_init</c>: default speed 200, bake movedir*=speed, start the per-frame think.</summary>
    private static void ConveyorInit(Entity this_)
    {
        if (this_.Speed == 0f)
            this_.Speed = 200f;
        this_.MoveDir *= this_.Speed;     // QC: movedir *= speed (the cached per-tick conveyor velocity)
        this_.Think = ConveyorThink;
        this_.NextThink = MapMover.Now();
        if (!string.IsNullOrEmpty(this_.TargetName))
            this_.Use = ConveyorLegacyUse; // QC generic_netlinked_legacy_use: a targeted conveyor toggles
        // QC: this.reset = generic_netlinked_reset; this.reset(this) — a TARGETED conveyor starts OFF unless
        // START_ENABLED (it's toggled on by its trigger); an untargeted one is always ACTIVE.
        GenericNetlinkedReset(this_);
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>generic_netlinked_reset</c>: targeted -&gt; START_ENABLED ? ACTIVE : NOT; untargeted -&gt; ACTIVE.</summary>
    private static void GenericNetlinkedReset(Entity e)
    {
        if (!string.IsNullOrEmpty(e.TargetName))
            e.Active = (e.SpawnFlags & MapMover.SpawnStartEnabled) != 0 ? MapMover.ActiveActive : MapMover.ActiveNot;
        else
            e.Active = MapMover.ActiveActive;
    }

    /// <summary>QC <c>generic_netlinked_legacy_use</c> (toggle active) for a targeted conveyor.</summary>
    private static void ConveyorLegacyUse(Entity self, Entity actor)
        => LogicGates.GenericSetActive(self, MapMover.ActiveToggle);

    // QC kept the conveyed/laddered entities in inventory lists (g_conveyed / g_ladderents) so the release
    // pass walks EVERY tagged entity regardless of position — a FindInRadius release would miss an entity that
    // left the sphere in one frame and leave it stuck. The port has no such IL, so each producer tracks the
    // entities it currently holds in a per-producer set (the g_conveyed/g_ladderents analogue).
    private static readonly Dictionary<Entity, List<Entity>> _conveyed = new();
    private static readonly Dictionary<Entity, List<Entity>> _laddered = new();

    /// <summary>Scratch buffer reused for the per-frame FindInBox volume scan (replaces a per-think
    /// <c>.ToList()</c>). The conveyor/ladder thinks run sequentially on the sim thread and never re-enter, and
    /// each fully drains the scan (into its per-producer held list) before the next think reuses this — so one
    /// shared static is safe. The result is iterated as a SNAPSHOT (the loop relinks entities via SetOrigin).</summary>
    private static readonly List<Entity> _volumeScratch = new();

    /// <summary>Drop the per-producer conveyed/laddered tracking lists (QC clears g_conveyed/g_ladderents on map (re)load).</summary>
    public static void ResetTracking()
    {
        _conveyed.Clear();
        _laddered.Clear();
    }

    /// <summary>
    /// QC <c>conveyor_think</c> (SVQC half): release every entity this conveyor was carrying, then — if active —
    /// re-tag the overlapping pushable entities with this conveyor + its movedir. Non-client carried entities
    /// are also nudged by movedir*frametime (clients are moved via velocity in PlayerPhysics). Reschedules every
    /// frame.
    /// </summary>
    private static void ConveyorThink(Entity self)
    {
        self.NextThink = MapMover.Now();
        if (Api.Services is null)
            return;

        // release everything we were carrying (QC IL_EACH(g_conveyed, it.conveyor == this) clear).
        List<Entity> held = Held(_conveyed, self);
        foreach (Entity it in held)
            if (ReferenceEquals(it.ConveyorEntity, self))
            {
                it.ConveyorEntity = null;
                it.ConveyorMoveDir = Vector3.Zero;
            }
        held.Clear();

        if (self.Active != MapMover.ActiveActive)
            return;

        // Snapshot the box scan into the reused buffer (alloc-free); the loop relinks entities via SetOrigin
        // below, so iterating a snapshot (not the live grid result) is required, and `held` above is a distinct
        // list so appending to it mid-loop doesn't disturb _volumeScratch. FindInBox queries the volume's exact
        // AABB (the precise overlap test lives inside it) — replaces the old bounding-SPHERE FindInRadius whose
        // half-diagonal radius over-scanned, then a per-entity BoxesOverlap trimmed (upstream b6e02fe3 pattern).
        Api.Entities.FindInBox(self.AbsMin, self.AbsMax, _volumeScratch);
        for (int i = 0; i < _volumeScratch.Count; i++)
        {
            Entity it = _volumeScratch[i];
            // QC: it.conveyor.active == ACTIVE_NOT && isPushable(it) — claim only entities not held by an ACTIVE
            // conveyor (a null conveyor reads as world.active == ACTIVE_NOT, i.e. claimable).
            bool claimable = it.ConveyorEntity is null || it.ConveyorEntity.Active == MapMover.ActiveNot;
            if (!claimable || !MapMover.IsPushable(it))
                continue;

            it.ConveyorEntity = self;
            it.ConveyorMoveDir = self.MoveDir;
            held.Add(it);
        }

        // QC: non-client carried ents are advanced by movedir*frametime (clients move via velocity in physics),
        // then move_out_of_solid nudges anything the nudge embedded back out of a wall/floor.
        float dt = MapMover.FrameTime();
        foreach (Entity it in held)
        {
            if ((it.Flags & EntFlags.Client) != 0)
                continue; // done in SV_PlayerPhysics
            MapMover.SetOrigin(it, it.Origin + self.MoveDir * dt);
            MoveOutOfSolid(it); // QC: move_out_of_solid(it) after the conveyor nudge
        }
    }

    /// <summary>
    /// Minimal port of QC <c>move_out_of_solid(entity this)</c> (no port builtin; mirrors the inline version in
    /// NadeTranslocateBoom): if the entity's box isn't embedded in solid, leave it; otherwise nudge it straight
    /// up in small steps (the common rest-on-a-moved-floor case). No-op without a live trace world.
    /// </summary>
    private static void MoveOutOfSolid(Entity e)
    {
        if (Api.Services is null)
            return;
        Vector3 origin = e.Origin;
        TraceResult tr = Api.Trace.Trace(origin, e.Mins, e.Maxs, origin, MoveFilter.NoMonsters, e);
        if (!tr.StartSolid)
            return;

        float step = 2f;
        float maxRise = (e.Maxs.Z - e.Mins.Z) + 16f;
        for (float dz = step; dz <= maxRise; dz += step)
        {
            Vector3 p = origin + new Vector3(0f, 0f, dz);
            TraceResult t = Api.Trace.Trace(p, e.Mins, e.Maxs, p, MoveFilter.NoMonsters, e);
            if (!t.StartSolid)
            {
                MapMover.SetOrigin(e, p);
                return;
            }
        }
    }

    // ===================================================================
    //  func_ladder / func_water (func/ladder.qc)
    // ===================================================================

    /// <summary><c>spawnfunc(func_ladder)</c> — a climbable volume (sets LadderEntity on overlapping players).</summary>
    public static void FuncLadderSetup(Entity this_)
    {
        this_.ClassName = "func_ladder";
        LadderInit(this_);
    }

    /// <summary>
    /// <c>spawnfunc(func_water)</c> — shares func_ladder's volume scan; PlayerPhysics special-cases the
    /// "func_water" classname to drive the waterlevel from the volume bounds (CheckWater skips its probe).
    /// </summary>
    public static void FuncWaterSetup(Entity this_)
    {
        this_.ClassName = "func_water";
        LadderInit(this_);
    }

    /// <summary>
    /// QC <c>func_ladder_init</c> (the part that matters server-side): WarpZoneLib_ExactTrigger_Init analogue
    /// (the trigger bbox) + the per-frame think. The bot-waypoint tracetest tail (after IL_PUSH) is bot
    /// navigation only and is skipped.
    /// </summary>
    private static void LadderInit(Entity this_)
    {
        MapMover.InitTrigger(this_); // SOLID_TRIGGER + the brush bbox (the exact-trigger init analogue)
        this_.Think = LadderThink;
        this_.NextThink = MapMover.Now();
        MapMover.IndexRegister(this_);
    }

    /// <summary>
    /// QC <c>func_ladder_think</c> (SVQC half): release every player this ladder was holding, then re-tag the
    /// overlapping live (non-noclip, non-dead) players with this ladder. Reschedules every frame.
    /// </summary>
    private static void LadderThink(Entity self)
    {
        self.NextThink = MapMover.Now();
        if (Api.Services is null)
            return;

        // release everything we were holding (QC IL_EACH(g_ladderents, it.ladder_entity == this) clear).
        List<Entity> held = Held(_laddered, self);
        foreach (Entity it in held)
            if (ReferenceEquals(it.LadderEntity, self))
                it.LadderEntity = null;
        held.Clear();

        // Snapshot the box scan into the reused buffer (alloc-free); `held` above is a distinct list, so
        // appending to it mid-loop doesn't disturb _volumeScratch (the conveyor think drained it first).
        // FindInBox queries the volume's exact AABB (precise overlap inside) — no follow-up BoxesOverlap needed.
        Api.Entities.FindInBox(self.AbsMin, self.AbsMax, _volumeScratch);
        for (int i = 0; i < _volumeScratch.Count; i++)
        {
            Entity it = _volumeScratch[i];
            // QC: !it.ladder_entity && IS_PLAYER(it) && it.move_movetype != MOVETYPE_NOCLIP && !IS_DEAD(it)
            if (it.LadderEntity is not null)
                continue;
            if ((it.Flags & EntFlags.Client) == 0)
                continue;
            if (it.MoveType == MoveType.Noclip)
                continue;
            if (MapMover.IsDead(it))
                continue;

            it.LadderEntity = self;
            held.Add(it);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>The per-producer "currently held" list (the g_conveyed / g_ladderents IL analogue), created on demand.</summary>
    private static List<Entity> Held(Dictionary<Entity, List<Entity>> map, Entity producer)
    {
        if (!map.TryGetValue(producer, out List<Entity>? list))
            map[producer] = list = new List<Entity>();
        return list;
    }
}
