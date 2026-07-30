using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Phaser Cannon Turret — port of common/turrets/turret/phaser.{qh,qc} (+ phaser_weapon.qc). A sniper-class
/// hitscan turret (TUR_FLAG_SNIPER | TUR_FLAG_HITSCAN) that fires a continuous energy beam which deals damage
/// over its duration and slows players caught in it. Heavy per-burst damage (100), slow refire. Identity/hitbox
/// from phaser.qh; balance from turrets.cfg (<c>g_turrets_unit_phaser_*</c>).
///
/// Ported faithfully: the SUSTAINED beam — firing spawns a beam that re-traces along the head's aim every frame
/// for <c>shot_speed</c> (4) seconds, applying <c>shot_dmg / (shot_speed/frametime)</c> per tick and SLOWING
/// the target each tick (QC FireImoBeam <c>velocity *= 0.75</c>, util.qc). The turret keeps tracking while the
/// beam runs and only resets its refire clock when the beam ends (phaser_weapon.qc beam_think). A short
/// disability status effect is layered on the slow if the catalog provides one. Only the beam model visual +
/// charge/discharge head frames are client render.
/// </summary>
[Turret]
public sealed class PhaserTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_phaser_*) ---
    private const float ShotDamage = 100f;       // total damage spread over the beam duration
    private const float ShotRadius = 8f;          // beam thickness
    private const float ShotSpeed = 4f;           // beam DURATION in seconds (QC overloads shot_speed)
    private const float ShotForce = 5f;
    private const float ShotRefire = 4f;
    private const float RespawnTime = 90f;        // turrets.cfg g_turrets_unit_phaser_respawntime (NOT the shared 60s default)
    private const float VelFactor = 0.75f;        // per-tick slow (QC FireImoBeam f_velfactor)
    private const float TargetRange = 3000f;
    private const float HumPeriod = 2f;           // phaser_weapon.qc: re-trigger SND_TUR_PHASER every 2s (shot_spread clock)
    private const string SndPhaser = "turrets/phaser";   // SND_TUR_PHASER (the phaser hum)
    private const string SndImpact = "weapons/neximpact"; // SND_PhaserTurretAttack_IMPACT (neximpact)
    private const SoundChannel BeamHumChannel = SoundChannel.Body; // CH_SHOTS_SINGLE — kept on the beam so Stop() silences it on end
    private const float TargetRangeMin = 0f;
    private const float TargetRangeOptimal = 1500f;
    private const float AmmoMax = 2000f;
    private const float AmmoRecharge = 25f;
    private const float AimSpeed = 300f;
    private const float AimMaxPitch = 30f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 100f;

    // QC phaser.qc tr_setup keeps the sv_turrets default select: LOS, players, range, team, angle. Lead aim.
    private const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    public PhaserTurret()
    {
        NetName = "phaser";
        DisplayName = "Phaser Cannon";
        Model = "models/turrets/base.md3";
        HeadModel = "models/turrets/phaser.md3";   // QC phaser.qh: head_model — the separate tracking head-bone model
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0, respawnTime: RespawnTime);

    public override void Think(Entity e)
    {
        // phaser.qc tr_think: the head charge/discharge frame animation, driven by the fireflag state machine.
        // Base nets a SEPARATE tur_head entity's frame; the port has no head bone entity, so — like the Plasma/
        // Hellion/Hk turrets — the cycle runs on the turret edict's own networked Entity.Frame. (The real
        // phaser.md3 head model attachment is a client-render concern still deferred.)
        TrThinkHeadAnim(e);

        // phaser.qc turret_phaser_firecheck: Base blocks fire entirely while fireflag != 0 (beam active or
        // discharging). RunCombat now consults st.FireFlag in its fire gate (the port's analogue of the per-unit
        // .turret_firecheckfunc), so the discharge window (FireFlag==2, the ~5-frame head return-to-idle) blocks
        // fire faithfully in addition to the shot_refire (4s) AttackFinished hold.

        // phaser.qc tr_setup: aim_flags = TFL_AIM_LEAD ONLY — no SHOTTIMECOMPENSATE, no ZPREDICT, no SPLASH.
        // The phaser is hitscan so shot-time compensation is moot, and Base deliberately omits z-prediction;
        // both are left at their false defaults so the lead matches Base (esp. vs airborne targets).
        // turrets.cfg g_turrets_unit_phaser_* target-selection biases (302-306) and head-track tuning (317-320).
        // The phaser weights distance and angle more heavily (rangebias 0.85, anglebias 0.25) and zeroes out the
        // sticky same-target and missile biases (samebias 0, missilebias 0). track_accel_rot 0.65 / track_blendrate
        // 0.2 tune the fluid-inertia head motor; track_accel_pitch 0.5 happens to match the TurretParams default.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: 35000f /*beam is hitscan for lead*/,
            aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            rangeBias: 0.85f, sameBias: 0f, angleBias: 0.25f, missileBias: 0f, playerBias: 1f,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: 0.5f, trackAccelRot: 0.65f,
            trackBlendRate: 0.2f);
        TurretAI.RunCombat(e, in p, Attack);
    }

    /// <summary>
    /// phaser.qc tr_think: the head charge/discharge frame animation state machine. Only runs while a frame is
    /// already in motion (frame != 0); fireflag==1 cycles the head 1↔10 (charge/fire loop), fireflag==2 advances
    /// to 15 then resets to idle (frame=0, fireflag=0) — re-enabling fire via the firecheck guard above.
    /// </summary>
    private static void TrThinkHeadAnim(Entity e)
    {
        TurretState st = TurretAI.State(e);
        if (e.Frame == 0f) return;
        if (st.FireFlag == 1)
        {
            if (e.Frame == 10f) e.Frame = 1f;
            else e.Frame += 1f;
        }
        else if (st.FireFlag == 2)
        {
            e.Frame += 1f;
            if (e.Frame == 15f)
            {
                e.Frame = 0f;
                st.FireFlag = 0;
            }
        }
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // phaser_weapon.qc wr_think: spawn the sustained beam. It thinks each frame for shot_speed seconds, ticking
    // beam damage + slow, and holds the turret's refire until it ends.
    private void Attack(Entity turret, Entity enemy)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        // The beam owns the refire: it re-parks attack_finished each frame while running, then sets the real
        // shot_refire when it ends (QC beam_think). The now + p.Refire that RunCombat.Fire sets right after this
        // is harmlessly overwritten by the first BeamThink tick this same frame.

        Entity beam = Api.Entities.Spawn();
        beam.ClassName = "PhaserTurret_beam";
        beam.Owner = turret;
        beam.Enemy = enemy;
        beam.Solid = Solid.Not;
        beam.MoveType = MoveType.None;
        float endTime = now + ShotSpeed;
        float perTickDamage = ShotDamage / (ShotSpeed / System.Math.Max(Api.Clock.FrameTime, 0.0001f));
        float nextHum = now + HumPeriod;   // QC beam.shot_spread = time + 2

        // phaser_weapon.qc wr_think: register the beam as a dodgeable threat so havocbots strafe out of it
        // (beam.bot_dodge = true; IL_PUSH(g_bot_dodge, beam); beam.bot_dodgerating = beam.shot_dmg — the per-tick
        // damage). The g_bot_dodge danger-list CONSUMER is now ported (BotBrain.HavocbotDodge scans every edict
        // flagged Entity.BotDodge and steers away; Waypoint.UpdateDangerousObjects folds it into A* route cost),
        // so this is LIVE: SUPERBOT-skill bots actively dodge the phaser beam.
        beam.BotDodge = true;
        beam.BotDodgeRating = perTickDamage;

        beam.Think = self => BeamThink(self, endTime, perTickDamage, nextHum);
        beam.NextThink = now;

        // phaser_weapon.qc wr_think (turret-edict branch): the phaser hum starts on the BEAM (CH_SHOTS_SINGLE)
        // so it follows the beam and can be silenced on end; the player-manned branch (electro_fire) is not the
        // turret path. The neximpact cue is played once at the spawn-time trace endpoint.
        Api.Sound.Play(beam, BeamHumChannel, SndPhaser, SoundLevels.VolBase, SoundLevels.AttenNorm);

        Vector3 shotOrg = TurretAI.ShotOrigin(turret);
        Vector3 fwd = QMath.Forward(TurretAI.HeadWorldAngles(turret));
        TraceResult impact = Api.Trace.Trace(shotOrg, Vector3.Zero, Vector3.Zero,
            shotOrg + fwd * TargetRange, MoveFilter.Normal, turret);
        Api.Sound.PlayAt(impact.EndPos, SoundChannel.ShotsAuto, SndImpact, SoundLevels.VolBase, SoundLevels.AttenNorm);

        // phaser_weapon.qc wr_think: actor.fireflag = 1 (start the charge anim), and (turret-edict branch only)
        // `if (tur_head.frame == 0) tur_head.frame = 1` to kick the head off idle. The per-think tr_think state
        // machine then cycles the head 1↔10 while firing (TrThinkHeadAnim). The head frame rides the turret
        // edict's own networked Entity.Frame (the port has no separate tur_head sub-entity).
        TurretState st = TurretAI.State(turret);
        st.FireFlag = 1;
        if (turret.Frame == 0f)
            turret.Frame = 1f;

        // phaser_weapon.qc wr_think: setmodel(MDL_TUR_PHASER_BEAM) + effects=EF_LOWPRECISION + beam.scale =
        // actor.target_range/256 (initial visual length). Entity.ScaleFactor is read by EntityNode on the
        // listen-server/demo path (game/EntityNode.cs:116: `float s = Entity.ScaleFactor; if (s>0) Scale=...`);
        // remote clients see scale 1 until an EntityField.Scale wire bump (T48 deferred gap). The QC
        // setattachment(beam,tur_head,"tag_fire") is unportable (no tag-socket/sub-entity); the beam is placed
        // at the computed muzzle origin with the head world-angles instead.
        beam.Model = "models/turrets/phaser_beam.md3";   // MDL_TUR_PHASER_BEAM
        if (Api.Services is not null) Api.Entities.SetModel(beam, beam.Model);
        beam.Effects |= AdvancedMovers.EfLowPrecision;   // QC: effects = EF_LOWPRECISION
        beam.ScaleFactor = TargetRange / 256f;            // QC: beam.scale = actor.target_range / 256
        // Place beam at the muzzle facing head aim (setattachment approximation).
        beam.Origin = shotOrg;
        beam.Angles = TurretAI.HeadWorldAngles(turret);
    }

    // beam_think (phaser_weapon.qc): re-trace along the head's aim each frame, damage + slow the first hit, and
    // tear down + reset the turret refire when the duration elapses.
    private void BeamThink(Entity beam, float endTime, float perTickDamage, float nextHum)
    {
        Entity turret = beam.Owner!;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        if (now > endTime || turret.IsFreed || turret.DeadState != DeadFlag.No)
        {
            // End of beam (QC beam_think:59-66): silence the hum (sound(this, CH_SHOTS_SINGLE, SND_Null)), set the
            // real refire, enter the discharge head animation (fireflag=2, tur_head.frame=10 — tr_think then
            // advances 10→15 then resets to idle), and remove the beam. The fireflag=2 fire-block is subsumed by
            // the shot_refire hold (4s) here, the only refire gate the port consults (TurretAI:655); the frame
            // advance is now driven by TrThinkHeadAnim on the turret edict's Entity.Frame.
            if (Api.Services is not null) Api.Sound.Stop(beam, BeamHumChannel);
            if (!turret.IsFreed)
            {
                TurretState endState = TurretAI.State(turret);
                endState.AttackFinished = now + ShotRefire;
                endState.FireFlag = 2;
                turret.Frame = 10f;
            }
            if (Api.Services is not null) Api.Entities.Remove(beam);
            return;
        }

        // phaser_weapon.qc beam_think: every 2s re-trigger SND_TUR_PHASER and re-arm the clock (shot_spread = time + 2).
        if (now - nextHum > 0f)
        {
            nextHum = now + HumPeriod;
            if (Api.Services is not null)
                Api.Sound.Play(beam, BeamHumChannel, SndPhaser, SoundLevels.VolBase, SoundLevels.AttenNorm);
        }

        TurretState st = TurretAI.State(turret);
        st.AttackFinished = endTime + 0.001f;   // hold refire past the beam while it runs (QC attack_finished = time + frametime)
        st.ShotOrg = TurretAI.ShotOrigin(turret);
        Vector3 dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));
        Vector3 start = st.ShotOrg;
        Vector3 end = start + dir * TargetRange;

        // FireImoBeam (util.qc): a railgun-style PENETRATING thick beam. Trace repeatedly, making each hit
        // entity non-solid and continuing until a world brush (SOLID_BSP) or empty trace, then damage + slow
        // EVERY entity collected along the line (no falloff) — so players lined up behind one another are all hit.
        if (Api.Services is not null)
        {
            Vector3 half = new Vector3(ShotRadius, ShotRadius, ShotRadius);
            Vector3 traceEnd = end + dir;   // QC: go a little into the wall so the final trace registers it
            var pierced = new List<(Entity ent, Vector3 loc, Solid solid)>();
            Vector3 cur = start;
            // beamEnd: wall-hit endpoint for beam.scale (QC trace_endpos after the FireImoBeam loop).
            Vector3 beamEnd = end;  // default = full acquisition range when nothing intercepts
            for (int guard = 0; guard < 64; ++guard)
            {
                TraceResult tr = Api.Trace.Trace(cur, -half, half, traceEnd, MoveFilter.Normal, turret);
                Entity? hit = tr.Ent;
                if (hit is null || tr.Fraction >= 1f) { beamEnd = tr.EndPos; break; }  // hit world / nothing -> stop
                pierced.Add((hit, tr.EndPos, hit.Solid));
                if (hit.Solid == Solid.Bsp) { beamEnd = tr.EndPos; break; }            // a world brush ends the beam
                hit.Solid = Solid.Not;                          // make non-solid so the next trace passes through
                cur = tr.EndPos;
            }

            // Restore solidity, then damage + slow everyone we passed through (QC: velocity *= f_velfactor).
            foreach (var p in pierced) p.ent.Solid = p.solid;
            foreach (var p in pierced)
            {
                if (p.ent.TakeDamage == DamageMode.No) continue;
                // phaser_weapon.qc: the beam tick is DEATH_TURRET_PHASER.
                Combat.Damage(p.ent, turret, turret, perTickDamage, DeathTypes.TurretPhaser,
                    p.loc, dir * ShotForce);
                p.ent.Velocity *= VelFactor;   // QC FireImoBeam slow
                ApplySlow(p.ent);              // optional status-effect layer
            }

            // beam_think: beam.scale = vlen(tur_shotorg - trace_endpos) / 256 (visual length to the wall hit).
            // Re-position/re-orient the beam each frame so it tracks the slewing muzzle
            // (setattachment(beam,tur_head,"tag_fire") approximation). EntityNode reads ScaleFactor per-frame.
            beam.ScaleFactor = QMath.VLen(start - beamEnd) / 256f;
            beam.Origin = start;
            beam.Angles = TurretAI.HeadWorldAngles(turret);
        }

        // Re-park the think with the (possibly advanced) hum clock so the 2s re-trigger persists across frames
        // (the QC beam carries shot_spread on the edict; here it rides the rebound delegate).
        float carriedHum = nextHum;
        beam.Think = self => BeamThink(self, endTime, perTickDamage, carriedHum);
        beam.NextThink = now;
    }

    /// <summary>Layer a brief slow/disability status on the beam target if the catalog offers one (QC slow is the velocity scale; this just augments it).</summary>
    private static void ApplySlow(Entity target)
    {
        StatusEffectDef? slow = StatusEffectsCatalog.ByName("buff_disability") ?? StatusEffectsCatalog.Frozen;
        if (slow is not null && slow != StatusEffectsCatalog.Frozen)
            StatusEffectsCatalog.Apply(target, slow, duration: 0.2f, strength: 1f);
    }
}
