// Port of qcsrc/common/mapobjects/models.qc — the decoration-prop / static-wall family:
//   misc_gamemodel / misc_clientmodel / misc_models  (non-solid external-model props; pitch FLIPPED first)
//   func_illusionary / func_clientillusionary        (non-solid brush entities)
//   func_wall / func_clientwall / func_static        (SOLID brush entities)
//
// Port notes:
//  * SetBrushEntityModel == Api.Entities.SetModel, called AFTER solid exactly as QC (models.qc:180) — an
//    inline "*N" model gives the wall its real brush bounds AND, with Solid.Bsp, real collision through
//    TraceService.ClipToEntities (this is what makes stormkeep's 17 func_walls actually block).
//  * The g_clientmodel_* variants drop the extra Net_LinkEntity ENT_CLIENT_WALL stream (the listen-server/demo
//    client reads the shared entity; the fade keys fade_start/fade_end/alpha_max/alpha_min/fade_vertical_offset
//    ride the entity). The SERVER-side antiwall solid-toggle relay (g_clientmodel_use antiwall_flag, with the
//    .inactive flag + .default_solid restore) IS now ported (ClientModelUse): a trigger that toggles a
//    func_clientwall/func_clientillusionary solid/non-solid actually changes the wall's collision. The CLIENT
//    render halves (Ent_Wall_PreDraw distance-fade alpha + PVS alpha-cull, and the bgmscript ADSR animation)
//    remain out of scope — a func_clientwall's faces are baked into the static map mesh (no per-entity render
//    node to drive alpha) and there is no bgmtime music-position clock; no shipped stock map depends on either.
//  * External-model props keep their full "models/…" path on Entity.Model — that's the networked-render
//    contract (ServerNet networks any non-'*' model as a Generic entity and the client loads it by path).
//    No ResolveModelPath prefixing here: map keys carry full paths, unlike the bare item-def names (T35).
//  * INITPRIO_DROPTOFLOOR runs inline at spawn: the static world the MOVE_NOMONSTERS drop traces against
//    is already built before the entity lump spawns in both hosts.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The models.qc decoration/wall spawnfuncs. Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class MapModels
{
    // ---- the eight classname spawnfuncs (models.qc:207-218) ----

    /// <summary><c>spawnfunc(misc_gamemodel)</c> — non-solid model entity (pitch flipped first).</summary>
    public static void GameModelSetup(Entity e) { e.ClassName = "misc_gamemodel"; FlipPitch(e); GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(misc_clientmodel)</c> — client-networked model entity (pitch flipped first).</summary>
    public static void ClientModelSetup(Entity e) { e.ClassName = "misc_clientmodel"; FlipPitch(e); GModelInit(e, Solid.Not, clientModel: true); }

    /// <summary><c>spawnfunc(misc_models)</c> — DEPRECATED compat alias of misc_gamemodel.</summary>
    public static void ModelsSetup(Entity e) { e.ClassName = "misc_models"; FlipPitch(e); GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(func_illusionary)</c> — non-solid brush entity (Q1 name).</summary>
    public static void IllusionarySetup(Entity e) { e.ClassName = "func_illusionary"; GModelInit(e, Solid.Not); }

    /// <summary><c>spawnfunc(func_clientillusionary)</c> — non-solid client-networked brush entity.</summary>
    public static void ClientIllusionarySetup(Entity e) { e.ClassName = "func_clientillusionary"; GModelInit(e, Solid.Not, clientModel: true); }

    /// <summary><c>spawnfunc(func_wall)</c> — SOLID brush entity (Q1 name).</summary>
    public static void WallSetup(Entity e) { e.ClassName = "func_wall"; GModelInit(e, Solid.Bsp); }

    /// <summary><c>spawnfunc(func_clientwall)</c> — SOLID client-networked brush entity.</summary>
    public static void ClientWallSetup(Entity e) { e.ClassName = "func_clientwall"; GModelInit(e, Solid.Bsp, clientModel: true); }

    /// <summary><c>spawnfunc(func_static)</c> — DEPRECATED alias of func_wall.</summary>
    public static void StaticSetup(Entity e) { e.ClassName = "func_static"; GModelInit(e, Solid.Bsp); }

    /// <summary>The three misc_* model entities flip mapper pitch into the engine model convention
    /// (models.qc:207-209 <c>this.angles_x = -this.angles.x</c>).</summary>
    private static void FlipPitch(Entity e) => e.Angles = new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z);

    /// <summary>Port of <c>g_model_init</c> / <c>g_clientmodel_init</c> (models.qc:173-204). The client variant
    /// (<paramref name="clientModel"/>: misc_clientmodel / func_clientillusionary / func_clientwall) differs by
    /// the networking we don't need PLUS two behaviors that ARE gameplay-observable and are reproduced here: it
    /// installs <see cref="ClientModelUse"/> (the antiwall solid-toggle relay) instead of the plain colormap use,
    /// and records <see cref="Entity.DefaultSolid"/> (the original solidity the relay restores). (The
    /// bgmscriptsustain default at g_clientmodel_init:200-201 belongs to the unported bgmscript ADSR subsystem.)</summary>
    internal static void GModelInit(Entity ent, Solid sol, bool clientModel = false)
    {
        // (QC: geomtype + ODE physics — not ported, no stock map uses it.)
        if (ent.ScaleFactor == 0f)
            ent.ScaleFactor = ent.ModelScale;

        // solid: unset -> the family default; a mapper 'solid -1' key forces non-solid; any other explicit
        // value is kept raw (the port Solid enum carries the QC SOLID_* values 1:1).
        if (ent.SolidOverride == 0f)
            ent.Solid = sol;
        else if (ent.SolidOverride < 0f)
            ent.Solid = Solid.Not;
        else
            ent.Solid = (Solid)(int)ent.SolidOverride;

        ent.MoveType = MoveType.None;

        // SetBrushEntityModel — AFTER solid, as QC notes, for correct area linking.
        if (Api.Services is not null && !string.IsNullOrEmpty(ent.Model))
            Api.Entities.SetModel(ent, ent.Model);

        // The g_clientmodel_* variants get the antiwall solid-toggle .use (models.qc:195); the rest get the
        // plain colormap-to-activator .use (models.qc:182).
        ent.Use = clientModel ? ClientModelUse : SetColormapToActivator;

        // INITPRIO_DROPTOFLOOR (models.qc:65-82) — run inline, see header.
        DropBySpawnflags(ent);

        // models.qc:203: default_solid = sol — the original solidity the antiwall "activate" branch restores.
        if (clientModel)
            ent.DefaultSolid = sol;

        MapMover.IndexRegister(ent);
    }

    /// <summary>Port of <c>g_clientmodel_use</c> (models.qc:37-63): the antiwall solid-toggle relay. A trigger
    /// whose <c>.target</c> names a func_clientwall/func_clientillusionary fires this <c>.use</c>; the wall reads
    /// the firing trigger's <c>.antiwall_flag</c> (1 = deactivate -&gt; SOLID_NOT + hidden, 2 = activate -&gt;
    /// restore <see cref="Entity.DefaultSolid"/> + shown, 0 = nothing) and re-links itself, then runs the colormap
    /// pass. A map that toggles a clientwall solid/visible via a trigger now actually changes the wall.</summary>
    internal static void ClientModelUse(Entity this_, Entity actor)
    {
        // QC's SUB_UseTargets fires `t.use(t, actor, trigger)` and g_clientmodel_use reads trigger.antiwall_flag.
        // The port's 2-arg .use carries no trigger param, so the firing trigger is threaded through
        // MapMover.CurrentUseTrigger (the same seam door_use uses for .trigger_reverse). models.qc:41-42: only
        // func_clientwall/func_clientillusionary adopt the relayed flag.
        if (this_.ClassName is "func_clientwall" or "func_clientillusionary")
            this_.AntiwallFlag = MapMover.CurrentUseTrigger?.AntiwallFlag ?? this_.AntiwallFlag;

        if (this_.AntiwallFlag == 1)
        {
            this_.Inactive = true;
            if (this_.Solid != Solid.Not)
            {
                this_.Solid = Solid.Not;
                MapMover.SetOrigin(this_, this_.Origin); // unlink (QC setorigin relink)
            }
        }
        else if (this_.AntiwallFlag == 2)
        {
            this_.Inactive = false;
            if (this_.Solid != this_.DefaultSolid)
            {
                this_.Solid = this_.DefaultSolid;
                MapMover.SetOrigin(this_, this_.Origin); // link (QC setorigin relink)
            }
        }

        SetColormapToActivator(this_, actor); // g_clientmodel_setcolormaptoactivator (models.qc:62)
    }

    /// <summary>Port of <c>g_model_setcolormaptoactivator</c> (models.qc:17-29). The colormap value is stored
    /// on <see cref="Entity.ColorMapOverride"/> (render consumption is a follow-up seam).</summary>
    internal static void SetColormapToActivator(Entity this_, Entity actor)
    {
        // QC checks the global `teamplay`; the established Common-side read is the cvar (MonsterAI.IsTeamplay).
        bool teamplay = Api.Services is not null && Api.Cvars.GetFloat("teamplay") != 0f;
        if (teamplay)
        {
            if (actor.Team != 0f)
                this_.ColorMapOverride = (int)(actor.Team - 1f) * 0x11;
            else
                this_.ColorMapOverride = 0x00;
        }
        else
        {
            this_.ColorMapOverride = (int)System.MathF.Floor(Prandom.Float() * 256f);
        }
        this_.ColorMapOverride |= Entity.RenderColormapped; // BIT(10)
    }

    /// <summary>Port of <c>g_model_dropbyspawnflags</c> (models.qc:65-82): spawnflags&amp;3 —
    /// 1 = ALIGN_ORIGIN (line trace), 2 = ALIGN_BOTTOM (box trace), 3 = origin trace lifted by -mins.z.</summary>
    internal static void DropBySpawnflags(Entity this_)
    {
        if (Api.Services is null)
            return;

        Vector3 down = this_.Origin - new Vector3(0f, 0f, 4096f);
        switch (this_.SpawnFlags & 3)
        {
            case 1: // ALIGN_ORIGIN
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, Vector3.Zero, Vector3.Zero, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos);
                break;
            }
            case 2: // ALIGN_BOTTOM
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, this_.Mins, this_.Maxs, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos);
                break;
            }
            case 3: // ALIGN_ORIGIN | ALIGN_BOTTOM
            {
                TraceResult tr = Api.Trace.Trace(this_.Origin, Vector3.Zero, Vector3.Zero, down, MoveFilter.NoMonsters, this_);
                MapMover.SetOrigin(this_, tr.EndPos - new Vector3(0f, 0f, 1f) * this_.Mins.Z);
                break;
            }
        }
    }
}
