using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression for the "weapon projectile hits the firer" bug (esp. the Devastator while walking forward):
/// the port set projectiles <c>SOLID_BBOX</c> (DPCONTENTS_BODY) instead of Base's
/// <c>PROJECTILE_MAKETRIGGER</c> = <c>SOLID_CORPSE</c> + <c>dphitcontentsmask SOLID|BODY|CORPSE</c>
/// (server/weapons/common.qh:33). A player's own movement trace masks <c>SOLID|BODY|PLAYERCLIP</c> — WITHOUT
/// CORPSE — so a SOLID_CORPSE projectile is transparent to player movement, while a SOLID_BBOX one is a solid
/// wall. On a listen server the predicting local player is a DISTINCT entity from the projectile's server-side
/// <c>.owner</c>, so the owner trace-exception cannot protect it — only the CORPSE channel does.
///
/// Mirrors: server/weapons/common.qh PROJECTILE_MAKETRIGGER; sv_phys.c SV_GenericHitSuperContentsMask.
/// </summary>
[Collection("GlobalState")]
public class ProjectileFirerCollisionTests
{
    private static CollisionWorld FloorWorld()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-4096, -4096, -16), new Vector3(4096, 4096, 0), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }

    private static Entity NewPlayer(EntityService ents, Vector3 origin)
    {
        var p = ents.Spawn();
        p.Flags = EntFlags.Client;
        p.Solid = Solid.SlideBox;
        ents.SetSize(p, new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        ents.SetOrigin(p, origin);
        return p;
    }

    /// <summary>
    /// A projectile made via <see cref="Projectiles.MakeTrigger"/> is SOLID_CORPSE and TRANSPARENT to a
    /// NON-OWNER player's forward movement (the listen-server prediction reality), but a plain SOLID_BBOX
    /// entity blocks that same move.
    /// </summary>
    [Fact]
    public void Maketrigger_projectile_is_transparent_to_player_movement()
    {
        var loop = new SimulationLoop(FloorWorld());
        loop.InstallAsAmbient();
        var ents = loop.Services.EntityTable;

        var serverOwner = NewPlayer(ents, new Vector3(500, 0, 25)); // the projectile's server-side owner
        var localPlayer = NewPlayer(ents, new Vector3(0, 0, 25));   // a DIFFERENT predicting client instance

        var proj = ents.Spawn();
        proj.ClassName = "rocket";
        proj.Owner = serverOwner; // NOT localPlayer — owner exception can't help
        proj.MoveType = MoveType.Fly;
        ents.SetSize(proj, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        ents.SetOrigin(proj, new Vector3(20, 0, 30)); // directly in front of localPlayer

        Vector3 end = localPlayer.Origin + new Vector3(60, 0, 0);

        // SOLID_BBOX (the bug): the projectile blocks the move.
        proj.Solid = Solid.BBox;
        proj.DpHitContentsMask = 0;
        ents.InvalidateSolidCache();
        var blocked = Api.Trace.Trace(localPlayer.Origin, localPlayer.Mins, localPlayer.Maxs, end, MoveFilter.Normal, localPlayer);
        Assert.True(blocked.Ent is not null && blocked.Ent.ClassName == "rocket",
            "a SOLID_BBOX projectile (the bug) blocks the firer's movement");

        // PROJECTILE_MAKETRIGGER (the fix): transparent to player movement.
        Projectiles.MakeTrigger(proj);
        ents.InvalidateSolidCache();
        var passes = Api.Trace.Trace(localPlayer.Origin, localPlayer.Mins, localPlayer.Maxs, end, MoveFilter.Normal, localPlayer);
        Assert.True(passes.Ent is null || passes.Ent.ClassName != "rocket",
            "a SOLID_CORPSE (MAKETRIGGER) projectile is transparent to the firer's movement");
    }

    /// <summary>
    /// The REAL listen-server bug: the predicting local player is the <c>client_predict</c> CARRIER, which is
    /// kept <c>SOLID_NOT</c> (NetGame.SpawnCarrier) so the server authority never collides with the ghost. But
    /// <see cref="TraceService.GenericHitMask"/> derives a SOLID_NOT mover's mask from the <c>default</c> branch
    /// = <c>Solid|Body|CORPSE</c> — which INCLUDES the CORPSE bit a MAKETRIGGER projectile lives in. So making
    /// projectiles SOLID_CORPSE was NOT enough: the carrier still collided with (and detonated) its own slow
    /// projectiles (Electro orbs, the guided Devastator rocket). The fix gives the carrier the SAME
    /// dphitcontentsmask the authoritative SOLID_SLIDEBOX player uses (<c>Solid|Body|PlayerClip</c>, no CORPSE),
    /// so prediction matches authority and the firer passes through its own projectiles.
    /// </summary>
    [Fact]
    public void Solid_not_prediction_carrier_passes_through_own_projectile_only_with_player_mask()
    {
        var loop = new SimulationLoop(FloorWorld());
        loop.InstallAsAmbient();
        var ents = loop.Services.EntityTable;

        var serverOwner = NewPlayer(ents, new Vector3(500, 0, 25)); // the projectile's server-side owner

        // The prediction carrier: a SOLID_NOT client entity, a DISTINCT instance from the projectile's owner.
        var carrier = ents.Spawn();
        carrier.ClassName = "client_predict";
        carrier.Flags = EntFlags.Client;
        carrier.Solid = Solid.Not;
        ents.SetSize(carrier, new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        ents.SetOrigin(carrier, new Vector3(0, 0, 25));

        var proj = ents.Spawn();
        proj.ClassName = "rocket";
        proj.Owner = serverOwner; // NOT the carrier — owner exception can't help
        proj.MoveType = MoveType.Fly;
        ents.SetSize(proj, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        ents.SetOrigin(proj, new Vector3(20, 0, 30)); // directly in front of the carrier
        Projectiles.MakeTrigger(proj);               // SOLID_CORPSE (the prior fix, necessary but insufficient)

        Vector3 end = carrier.Origin + new Vector3(60, 0, 0);

        // BUG: a SOLID_NOT carrier with no mask override gets the default Solid|Body|CORPSE move mask, so the
        // CORPSE projectile still blocks it.
        carrier.DpHitContentsMask = 0;
        ents.InvalidateSolidCache();
        var blocked = Api.Trace.Trace(carrier.Origin, carrier.Mins, carrier.Maxs, end, MoveFilter.Normal, carrier);
        Assert.True(blocked.Ent is not null && blocked.Ent.ClassName == "rocket",
            "without the player mask the SOLID_NOT carrier still collides with its own CORPSE projectile (the residual bug)");

        // FIX: give the carrier the authoritative player mask (Solid|Body|PlayerClip, no CORPSE) — it now passes
        // through, exactly as the SOLID_SLIDEBOX server player does.
        carrier.DpHitContentsMask = SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip;
        ents.InvalidateSolidCache();
        var passes = Api.Trace.Trace(carrier.Origin, carrier.Mins, carrier.Maxs, end, MoveFilter.Normal, carrier);
        Assert.True(passes.Ent is null || passes.Ent.ClassName != "rocket",
            "with the player mask the carrier is transparent to its own projectile (prediction matches authority)");
    }

    /// <summary>
    /// Listen-server prediction parity: the predicted <c>client_predict</c> carrier (SOLID_NOT) and the
    /// authoritative host Player (SOLID_SLIDEBOX body) are two DISTINCT, co-located entities in the same world.
    /// The carrier's slide-move (MoveFilter.Normal, mask includes BODY) clips the host player's own body — which
    /// the server player (ignore=self) never does — so the predicted origin drifts sideways from authority and
    /// the camera gets tugged to the side then crawls back. Linking <c>carrier.Owner = hostPlayer</c> makes
    /// <see cref="TraceService.ClipToEntities"/> skip it (it excludes <c>ignore.Owner==touch</c>), without
    /// affecting projectile/world clipping.
    /// </summary>
    [Fact]
    public void Prediction_carrier_owner_linked_to_host_player_passes_through_its_body()
    {
        var loop = new SimulationLoop(FloorWorld());
        loop.InstallAsAmbient();
        var ents = loop.Services.EntityTable;

        // The authoritative host Player body, slightly ahead so the sweep meets it (not startsolid co-location).
        var host = NewPlayer(ents, new Vector3(40, 0, 25));

        // The predicted carrier: SOLID_NOT client entity with the player move mask (as NetGame.SpawnCarrier sets).
        var carrier = ents.Spawn();
        carrier.ClassName = "client_predict";
        carrier.Flags = EntFlags.Client;
        carrier.Solid = Solid.Not;
        carrier.DpHitContentsMask = SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip;
        ents.SetSize(carrier, new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        ents.SetOrigin(carrier, new Vector3(0, 0, 25));

        Vector3 end = new(60, 0, 25);

        // BUG: no owner link → the carrier's forward sweep is blocked by the host player's body.
        carrier.Owner = null;
        ents.InvalidateSolidCache();
        var blocked = Api.Trace.Trace(carrier.Origin, carrier.Mins, carrier.Maxs, end, MoveFilter.Normal, carrier);
        Assert.Same(host, blocked.Ent);

        // FIX: link the carrier to the host Player → the sweep passes through its body (prediction matches authority).
        carrier.Owner = host;
        ents.InvalidateSolidCache();
        var passes = Api.Trace.Trace(carrier.Origin, carrier.Mins, carrier.Maxs, end, MoveFilter.Normal, carrier);
        Assert.True(passes.Ent is null || !ReferenceEquals(passes.Ent, host),
            "an owner-linked carrier passes through the authoritative host player body");
    }

    /// <summary>
    /// The MAKETRIGGER projectile still hits ENEMY bodies and the world during its OWN move (its
    /// dphitcontentsmask keeps SOLID|BODY|CORPSE) — so the fix doesn't make projectiles pass through targets.
    /// </summary>
    [Fact]
    public void Maketrigger_projectile_still_hits_enemy_body()
    {
        var loop = new SimulationLoop(FloorWorld());
        loop.InstallAsAmbient();
        var ents = loop.Services.EntityTable;

        var enemy = NewPlayer(ents, new Vector3(40, 0, 25));

        var proj = ents.Spawn();
        proj.ClassName = "rocket";
        proj.MoveType = MoveType.Fly;
        ents.SetSize(proj, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        ents.SetOrigin(proj, new Vector3(0, 0, 25));
        Projectiles.MakeTrigger(proj);
        ents.InvalidateSolidCache();

        // The projectile sweeps toward the enemy. ignore = projectile. Its dphitcontentsmask includes BODY,
        // so it must hit the enemy player's body.
        var tr = Api.Trace.Trace(proj.Origin, proj.Mins, proj.Maxs, new Vector3(80, 0, 25), MoveFilter.Normal, proj);
        Assert.Same(enemy, tr.Ent);
    }
}
