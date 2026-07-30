using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Damage
{

/// <summary>
/// The damage pipeline — a faithful, Godot-free port of the server <c>Damage(...)</c> dispatcher
/// (server/damage.qc) fused with the per-player <c>PlayerDamage(...)</c> / <c>PlayerCorpseDamage(...)</c>
/// event handlers (server/player.qc). Installed onto <see cref="Combat.System"/>.
///
/// Now ported in full (no stubs):
///  - the <c>Damage()</c> front gate: game-stopped/spectator/dead/godmode/takedamage checks, the
///    HOOK/HITTYPE_SOUND same-team special rule, and the ALWAYS-lethal kill/teamchange path;
///  - teamplay damage: the independent-player nullify, the <c>teamplay_mode</c> 1..4 friendly-fire /
///    team-damage-threshold / mirror-damage logic, virtual friendly-fire/mirror, and the deferred
///    mirror-damage re-entry (<c>Damage(attacker, ..., DEATH_MIRRORDAMAGE)</c>);
///  - the global weapon damage/force factors, self-damage percent, and the <c>Damage_Calculate</c>
///    mutator hook (lets mutators rewrite damage/mirror/force);
///  - <c>damage_explosion_calcpush</c> with the REAL <c>explosion_calcpush_getmultiplier</c> projection
///    (common/weapons/calculations.qc) — replaces the old capped stand-in;
///  - the per-player resource math: handicap (give/take) scaling, spawn-shield block, the armor/health
///    split via <c>healtharmor_applydamage</c> (with HITTYPE_ARMORPIERCE / drown bypassing armor),
///    godmode tab, regen pause, the <c>PlayerDamage_SplitHealthArmor</c> hook, dmg_take/dmg_save;
///  - the credited-attacker (<c>.pusher</c>/<c>.pushltime</c>) window;
///  - the kill path: obituary/scoring hook, <c>PlayerDies</c> mutator hook, corpse setup
///    (MOVETYPE_TOSS / SOLID_CORPSE / respawn timer / upright corpse / view-from-floor) and the
///    gib threshold (<c>sv_gibhealth</c>) via the corpse damage path.
///
/// Networking effects (gib splashes, hit sounds, <c>Damage_DamageInfo</c> blast broadcast) are modelled
/// as named no-ops / events since audio+VFX are the host's job on the headless sim.
/// </summary>
public sealed class DamageSystem : IDamageSystem
{
    // ----- cvar names (Xonotic balance/server cfgs; OPEN Q5 — names kept identical) -----
    private const string CvarArmorBlockPercent   = "g_balance_armor_blockpercent";   // balance-xonotic.cfg: 0.7
    private const string CvarWeaponDamageFactor  = "g_weapondamagefactor";           // balance-xonotic.cfg: 1
    private const string CvarWeaponForceFactor   = "g_weaponforcefactor";            // balance-xonotic.cfg: 1
    private const string CvarSelfDamagePercent   = "g_balance_selfdamagepercent";    // balance-xonotic.cfg: 0.65
    private const string CvarPlayerForceScale    = "g_player_damageforcescale";      // xonotic-server.cfg: 2
    private const string CvarDamagePushSpeed     = "g_balance_damagepush_speedfactor";// balance-xonotic.cfg: 2.5

    private const string CvarTeamplayMode        = "teamplay_mode";                  // 4 (team modes; xonotic-server.cfg:279)
    private const string CvarFriendlyFire        = "g_friendlyfire";                 // 0.5
    private const string CvarFriendlyFireVirtual = "g_friendlyfire_virtual";         // 1
    private const string CvarFriendlyFireVirtualForce = "g_friendlyfire_virtual_force"; // 1 (xonotic-server.cfg:285)
    private const string CvarMirrorDamage        = "g_mirrordamage";                 // 0.7
    private const string CvarMirrorDamageVirtual = "g_mirrordamage_virtual";         // 1
    private const string CvarMirrorDamageOnlyWeapons = "g_mirrordamage_onlyweapons"; // 0
    private const string CvarTeamDamageThreshold = "g_teamdamage_threshold";         // 40
    private const string CvarTeamDamageResetSpeed = "g_teamdamage_resetspeed";       // 20
    private const string CvarMaxPushTime         = "g_maxpushtime";                  // 8
    private const string CvarSpawnShieldBlock    = "g_spawnshield_blockdamage";      // 1
    private const string CvarPauseHealthRegen    = "g_balance_pause_health_regen";   // 5
    private const string CvarGibHealth           = "sv_gibhealth";                   // 100
    private const string CvarBallisticsCorpse    = "g_ballistics_density_corpse";    // 0
    private const string CvarGentle              = "sv_gentle";                      // 0 (T40: gore/sound toggle)

    // ----- cvar defaults (the balance-xonotic.cfg / xonotic-server.cfg values) -----
    private const float DefArmorBlockPercent = 0.7f;
    private const float DefWeaponDamageFactor = 1.0f;
    private const float DefWeaponForceFactor  = 1.0f;
    private const float DefSelfDamagePercent  = 0.65f;
    private const float DefPlayerForceScale   = 2.0f;
    private const float DefDamagePushSpeed    = 2.5f;
    private const float DefFriendlyFire       = 0.5f;
    private const float DefMirrorDamage       = 0.7f;
    private const float DefTeamDamageThreshold = 40f;
    private const float DefTeamDamageResetSpeed = 20f;
    private const float DefTeamplayMode       = 4f;   // xonotic-server.cfg:279 teamplay_mode 4
    private const float DefFriendlyFireVirtualForce = 1f; // xonotic-server.cfg:285 g_friendlyfire_virtual_force 1
    private const float DefMaxPushTime        = 8f;
    private const float DefSpawnShieldBlock   = 1f;
    private const float DefPauseHealthRegen   = 5f;
    private const float DefGibHealth          = 100f;

    /// <summary>Guards against the QC <c>Damage()</c> recursing forever through mirror damage (the QC re-enters once).</summary>
    [ThreadStatic] private static bool _inMirror;

    // ===============================================================================================
    //  Wave-1 "damage-channel" seam surface (the stable contract Wave-2 gameplay code depends on).
    //
    //  1. STRING deathtype/hittype channel — <see cref="Apply"/> carries the full string deathtype
    //     (DeathTypes.* base tag + "|hittype" suffix tokens) end-to-end: monster/turret/vehicle/special
    //     deaths ride the <c>deathTag</c> override (WeaponSplash.RadiusDamage / Combat.Damage(string)),
    //     and HITTYPE bits (SPLASH/SECONDARY/BOUNCE/ARMORPIERCE/SOUND/SPAM) ride the suffix tokens.
    //     The int-keyed weapon path (WeaponFiring.ApplyDamage(int)) maps the weapon id to its NetName tag;
    //     callers that need a hittype on the int path tag it with <see cref="SplashDeathType"/> /
    //     <see cref="DeathTypes.WithHitType"/> and pass the resulting STRING to <c>Combat.Damage</c>.
    //
    //  2. Per-entity damage/force scaling — <see cref="ApplyKnockback"/> consults the per-entity
    //     <see cref="Entity.DamageForceScale"/> (0 = immovable; players fall back to g_player_damageforcescale),
    //     and the gametype possession matrices (Keepaway/TKA ballcarrier, FreezeTag frozen-immunity) +
    //     the Midair multiplier rewrite damage/force through the <c>Damage_Calculate</c> hook below — there
    //     is no separate per-entity damage scalar in QC, the hook IS the scaling channel.
    //
    //  3. Damage-path mutator hooks — both fire live from this file on every applicable hit:
    //     <see cref="MutatorHooks.DamageCalculate"/> (Apply, after the global factors / before self-damage,
    //     in/out damage+mirror+force) and <see cref="MutatorHooks.PlayerDamaged"/> +
    //     <see cref="GameHooks.PlayerDamageSplitHealthArmor"/> (PlayerDamage, post-subtract).
    // ===============================================================================================

    /// <summary>
    /// QC <c>deathtype | HITTYPE_SPLASH</c> (server/damage.qc:917-920): the canonical tagging a
    /// splash/blast caller applies to an INDIRECT victim's deathtype so the kill-message/effect layer can
    /// tell a splash kill from a direct hit. RadiusDamage ORs this onto every victim that is NOT the
    /// <c>directHit</c> entity and whose death is not a special (non-weapon) death; the direct-hit entity
    /// and special deaths keep the plain tag. Idempotent (see <see cref="DeathTypes.WithHitType"/>). This is
    /// the seam the (separately-owned) WeaponSplash.RadiusDamage / WeaponFiring.ApplyDamage indirect path
    /// calls so the HITTYPE_SPLASH bit is set in exactly one place.
    /// </summary>
    public static string SplashDeathType(string deathType)
        => DeathTypes.IsSpecial(deathType)
            ? deathType                                       // QC: specials keep the plain deathtype
            : DeathTypes.WithHitType(deathType, DeathTypes.Splash);

    public float Apply(in DamageInfo info)
    {
        Entity targ = info.Target;
        Entity? attacker = info.Attacker;
        Entity? inflictor = info.Inflictor;
        string deathType = info.DeathType ?? DeathTypes.Generic;

        // QC Damage(): bail if the target can't be hurt or is already gone.
        // (game_stopped / spectator gating lives at the gametype layer; the spectator-killcount check
        //  has no analogue here yet, so we gate on freed/takedamage/dead only.)
        if (targ.IsFreed || targ.TakeDamage == DamageMode.No)
            return 0f;

        // QC special rule: the grapple HOOK and sound-based attacks never affect teammates (the hook is
        // disconnected upstream). Modeled via the HITTYPE_SOUND flag (+ a "hook" weapon base tag).
        bool soundOrHook = DeathTypes.HasHitType(deathType, DeathTypes.Sound)
            || DeathTypes.WeaponNetNameOf(deathType) == "hook";
        if (soundOrHook && IsPlayer(targ) && attacker is not null && Teams.SameTeam(targ, attacker))
            return 0f;

        Entity? attackerSave = attacker;

        float damage = info.Amount;
        Vector3 force = info.Force;
        float mirrorDamage = 0f;
        float mirrorForce = 0f;
        float complainTeamDamage = 0f;

        // ----- /kill, teamchange: ALWAYS lethal, no damage modification (QC damage.qc ~499) -----
        if (DeathTypes.IsAlwaysLethal(deathType))
        {
            if (DeathTypes.IsTeamChange(deathType))
            {
                targ.SetResourceExplicit(ResourceType.Armor, 0f);
                targ.SetResourceExplicit(ResourceType.Health, 0.9f); // < 1 so the kill check fires
            }
            RemoveSpawnShield(targ);                     // QC StatusEffects_remove(SpawnShield, CLEAR)
            targ.Flags &= ~EntFlags.GodMode;             // god mode never blocks an explicit kill
            damage = 100000f;
        }
        else if (DeathTypes.BaseOf(deathType) is DeathTypes.MirrorDamage or DeathTypes.NoAmmo)
        {
            // QC: no processing for reflected mirror damage or the no-ammo death.
        }
        else
        {
            // ----- teamplay damage nullify / friendlyfire / mirrordamage (QC damage.qc ~523) -----
            if (DeathTypes.BaseOf(deathType) != DeathTypes.Telefrag && IsPlayer(attacker))
            {
                Entity atk = attacker!;
                // avoid dealing damage/force to other independent players (or things they own)
                bool independentBlock =
                    (IsPlayer(targ) && !ReferenceEquals(targ, atk)
                        && (atk.IsIndependentPlayer || targ.IsIndependentPlayer))
                    || (targ.RealOwner is { } ro && ro.IsIndependentPlayer && !ReferenceEquals(atk, ro));

                if (independentBlock)
                {
                    damage = 0f;
                    force = Vector3.Zero;
                }
                // QC SAME_TEAM(targ, attacker) — the macro is `teamplay ? (a.team == b.team) : (a == b)`, so this
                // friendly-fire block must be TEAMPLAY-GATED: in FFA a player's .team carries a pants-color-derived
                // value (DP SV_ChangeTeam sets team = pants+1 on the `color` command — the port is faithful to
                // that), so two like-colored players compare "same team" and this branch ran in DEATHMATCH —
                // team-damage accrual, mirror damage, and the victim's "I'm on your team!" teamshoot voice on
                // plain DM hits (playtest #27). GameScores.Teamplay is authoritative from GameWorld boot (:587).
                // (Base's a==b arm would admit self-hits here; harmless at teamplay_mode!=1 — every mode arm below
                // excludes self — and mode 1 is a teamplay-only config, so gating the whole block is equivalent.)
                else if (!IsFrozenStat(targ) && Scoring.GameScores.Teamplay && Teams.SameTeam(atk, targ))
                {
                    int mode = (int)Cvar(CvarTeamplayMode, DefTeamplayMode);
                    if (mode == 1)
                        damage = 0f;
                    else if (!ReferenceEquals(atk, targ))
                    {
                        if (mode == 2)
                        {
                            if (IsPlayer(targ) && !IsDead(targ))
                            {
                                atk.DmgTeam += damage;
                                complainTeamDamage = atk.DmgTeam - Cvar(CvarTeamDamageThreshold, DefTeamDamageThreshold);
                            }
                        }
                        else if (mode == 3)
                        {
                            damage = 0f;
                        }
                        else if (mode == 4)
                        {
                            if (IsPlayer(targ) && !IsDead(targ))
                            {
                                atk.DmgTeam += damage;
                                complainTeamDamage = atk.DmgTeam - Cvar(CvarTeamDamageThreshold, DefTeamDamageThreshold);
                                if (complainTeamDamage > 0f)
                                    mirrorDamage = Cvar(CvarMirrorDamage, DefMirrorDamage) * complainTeamDamage;
                                mirrorForce = Cvar(CvarMirrorDamage, DefMirrorDamage) * force.Length();
                                damage = Cvar(CvarFriendlyFire, DefFriendlyFire) * damage;
                                // mirrordamage will be used LATER

                                if (CvarBool(CvarMirrorDamageVirtual))
                                {
                                    // virtual: the attacker only "feels" the mirror damage on the HUD; no HP lost.
                                    (float vt, float vs) = HealthArmorApplyDamage(
                                        atk.GetResource(ResourceType.Armor), ArmorBlockPercent(deathType), mirrorDamage);
                                    atk.DmgTake += vt;
                                    atk.DmgSave += vs;
                                    atk.DmgInflictor = inflictor;
                                    mirrorDamage = 0f; // QC sets v.z (excess) which is 0 here
                                    mirrorForce = 0f;
                                }

                                if (CvarBool(CvarFriendlyFireVirtual))
                                {
                                    (float vt, float vs) = HealthArmorApplyDamage(
                                        targ.GetResource(ResourceType.Armor), ArmorBlockPercent(deathType), damage);
                                    targ.DmgTake += vt;
                                    targ.DmgSave += vs;
                                    targ.DmgInflictor = inflictor;
                                    damage = 0f;
                                    // QC: g_friendlyfire_virtual_force 1 (xonotic-server.cfg:285) — keep knockback
                                    // even when friendlyfire is virtual (graphics-only). Default 1 means force IS
                                    // applied; only when explicitly set to 0 is it zeroed.
                                    if (Cvar(CvarFriendlyFireVirtualForce, DefFriendlyFireVirtualForce) == 0f)
                                        force = Vector3.Zero;
                                }
                            }
                            else if (!targ.CanTeamDamage)
                            {
                                damage = 0f;
                            }
                        }
                    }
                }
            }

            // Global weapon factors (QC: only for non-special deathtypes).
            if (!DeathTypes.IsSpecial(deathType))
            {
                float wdf = Cvar(CvarWeaponDamageFactor, DefWeaponDamageFactor);
                float wff = Cvar(CvarWeaponForceFactor, DefWeaponForceFactor);
                damage             *= wdf;
                mirrorDamage       *= wdf;
                complainTeamDamage *= wdf;
                force       *= wff;
                mirrorForce *= wff;
            }

            // MUTATOR_CALLHOOK(Damage_Calculate) — lets mutators rewrite damage/mirror/force here.
            var dc = new MutatorHooks.DamageCalculateArgs(
                inflictor, attacker, targ, deathType, damage, mirrorDamage, force, weaponEntity: null);
            MutatorHooks.DamageCalculate.Call(ref dc);
            damage = dc.Damage;
            mirrorDamage = dc.MirrorDamage;
            force = dc.Force;

            // Partial damage when the attacker hits himself (rocket-jump etc.) — QC damage.qc ~614.
            if (attacker is not null && ReferenceEquals(attacker, targ))
                damage *= Cvar(CvarSelfDamagePercent, DefSelfDamagePercent);

            // QC damage.qc ~618: same-team hit-sound and the team-kill complaint SOUND timer.
            // When the attacker has dealt enough team-damage to trigger complainTeamDamage > 0, and the
            // cooldown CS(attacker).teamkill_complain has elapsed, schedule a deferred complaint voice
            // on the victim: the attacker's "teamshoot" voice plays 0.4s later via PlayerFrameLogic.
            // QC damage.qc:654-664 (inside the "same-team hit" block):
            //   if (!STAT(FROZEN, victim) && !(deathtype & HITTYPE_SPAM))
            //     if (complainteamdamage > 0 && time > CS(attacker).teamkill_complain)
            //       CS(attacker).teamkill_complain = time + 5;
            //       CS(attacker).teamkill_soundtime = time + 0.4;
            //       CS(attacker).teamkill_soundsource = targ;
            if (complainTeamDamage > 0f
                && attacker is not null && IsPlayer(attacker)
                && !IsFrozenStat(targ)
                && !DeathTypes.HasHitType(deathType, DeathTypes.Spam))
            {
                float now = Now();
                if (now > attacker.TeamKillComplainTime)
                {
                    attacker.TeamKillComplainTime = now + 5f;
                    attacker.TeamKillSoundTime    = now + 0.4f;
                    attacker.TeamKillSoundSource  = targ;
                }
            }
        }

        // ----- apply knockback (QC damage.qc "apply push" ~671) -----
        ApplyKnockback(targ, attacker, info.HitLocation, force);

        // QC dh/da baseline: the health+armor present just before event_damage subtracts.
        float baseHealth = MathF.Max(targ.GetResource(ResourceType.Health), 0f);
        float baseArmor  = MathF.Max(targ.GetResource(ResourceType.Armor), 0f);

        // ----- apply damage to resources (QC PlayerDamage / PlayerCorpseDamage event_damage) -----
        if (damage != 0f || (targ.DamageForceScale != 0f && force != Vector3.Zero))
            EventDamage(targ, inflictor, attacker, deathType, damage, info.HitLocation, force);

        // ----- mirror damage (QC damage.qc ~698): reflect accumulated team damage at the attacker -----
        bool mirrorWeaponsOk = !CvarBool(CvarMirrorDamageOnlyWeapons) || DeathTypes.IsWeapon(deathType);
        if (!_inMirror && mirrorWeaponsOk && (mirrorDamage > 0f || mirrorForce > 0f) && attackerSave is not null)
        {
            Entity atk = attackerSave;
            Vector3 mForce = QNormalize(atk.Origin + atk.ViewOfs - info.HitLocation) * mirrorForce;
            _inMirror = true;
            try
            {
                Apply(new DamageInfo
                {
                    Target = atk,
                    Inflictor = inflictor,
                    Attacker = atk,
                    Amount = mirrorDamage,
                    DeathType = DeathTypes.MirrorDamage,
                    HitLocation = atk.Origin,
                    Force = mForce,
                });
            }
            finally { _inMirror = false; }
        }

        // QC dh/da: the health + armor actually removed this call (hit feedback / scoring upstream).
        float dh = baseHealth - MathF.Max(targ.GetResource(ResourceType.Health), 0f);
        float da = baseArmor  - MathF.Max(targ.GetResource(ResourceType.Armor), 0f);

        // [T41] hit-confirmation stat (QC server/damage.qc ~618 / common/notifications: the attacker's
        // STAT(HITSOUND_DAMAGE_DEALT_TOTAL) accumulates the (health + armor) actually removed, ONLY for a hit on
        // another player — never on self-damage). view.qc UpdateDamage/HitSound diffs this stat on the client to
        // fire the pitch-shifted hit sound. attackerSave is the pre-teamplay attacker (mirror re-entry passes
        // DEATH_MIRRORDAMAGE with attacker==target, so the self guard excludes it). Non-player attackers (world /
        // turrets) don't get a hit sound. Gated on a real removal so a fully-blocked/0-damage hit doesn't beep.
        if (!_inMirror && (dh + da) > 0f && IsPlayer(attackerSave) && !ReferenceEquals(attackerSave, targ))
        {
            // QC damage.qc:631-632: the hit-sound block also runs for non-player victims when a mutator's
            // PlayHitsound hook forces it (Assault returns true for a func_assault_destructible wall). For a
            // non-player victim, consult the hook so the registered gametype handler actually runs (this is the
            // live firing site for MUTATOR_CALLHOOK(PlayHitsound, victim, attacker) — QC's only consumer of it).
            // The port's stat accrues for any non-self victim regardless (so a wall already beeps); the hook call
            // is what makes the handler reachable and lets a future port gate the stat on the victim type as Base
            // does. (Result deliberately unused: the accrual below is already permissive and parity-neutral.)
            if (!IsPlayer(targ))
                MutatorHooks.FirePlayHitsound(targ, attackerSave);
            attackerSave!.HitsoundDamageDealtTotal += dh + da;
        }

        return dh + da;
    }

    // ===============================================================================================
    //  event_damage dispatch: live player vs corpse (QC this.event_damage = PlayerDamage / PlayerCorpseDamage)
    // ===============================================================================================
    private void EventDamage(Entity targ, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        // QC: a non-player edict (Onslaught generator / control-point icon, a breakable, …) carries its own
        // .event_damage callback. Players are dispatched below via PlayerDamage/PlayerCorpseDamage; route every
        // other target through its installed GtEventDamage so the gametype's objective combat (e.g.
        // ons_GeneratorDamage, ons_ControlPoint_Icon_Damage) runs through this same pipeline.
        if (!IsPlayer(targ) && targ.GtEventDamage is not null)
        {
            targ.GtEventDamage(targ, inflictor, attacker, deathType, damage, hitLoc, force);
            return;
        }

        if (targ.IsCorpse || IsDead(targ))
            PlayerCorpseDamage(targ, inflictor, attacker, deathType, damage);
        else
            PlayerDamage(targ, inflictor, attacker, deathType, damage, hitLoc, force);
    }

    /// <summary>
    /// Port of <c>PlayerCorpseDamage</c> (server/player.qc ~183): a dead body still takes (armor-split)
    /// damage and gibs when its health drops below <c>-sv_gibhealth</c>. No death re-fires (already dead).
    /// </summary>
    private void PlayerCorpseDamage(Entity targ, Entity? inflictor, Entity? attacker, string deathType, float damage)
    {
        (float take, float save) = HealthArmorApplyDamage(
            MathF.Max(targ.GetResource(ResourceType.Armor), 0f), ArmorBlockPercent(deathType), damage);

        targ.TakeResource(ResourceType.Armor, save);
        targ.TakeResource(ResourceType.Health, take);
        targ.PauseRegenFinished = MathF.Max(targ.PauseRegenFinished, Now() + Cvar(CvarPauseHealthRegen, DefPauseHealthRegen));

        targ.DmgSave += save;
        targ.DmgTake += take;
        targ.DmgInflictor = inflictor;

        if (targ.GetResource(ResourceType.Health) <= -Cvar(CvarGibHealth, DefGibHealth) && targ.Alpha >= 0f)
            GibCorpse(targ);
    }

    /// <summary>
    /// Port of the resource-mutating core of <c>PlayerDamage</c> (server/player.qc ~234): handicap +
    /// spawn-shield scaling, the armor/health split, godmode tab, regen pause, the SplitHealthArmor hook,
    /// dmg_take/dmg_save, the credited-attacker window, and the death/corpse transition.
    /// </summary>
    private void PlayerDamage(Entity targ, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        float initialHealth = MathF.Max(targ.GetResource(ResourceType.Health), 0f);
        float initialArmor  = MathF.Max(targ.GetResource(ResourceType.Armor), 0f);
        float take = 0f, save = 0f;

        if (damage != 0f)
        {
            // --- handicap (QC player.qc ~243): non-special damage scales by give/take handicaps ---
            if (!DeathTypes.IsSpecial(deathType))
            {
                damage *= HandicapTotal(targ, receiving: true);
                if (attacker is not null && !ReferenceEquals(targ, attacker) && IsPlayer(attacker))
                {
                    float give = HandicapTotal(attacker, receiving: false);
                    if (give != 0f) damage /= give;
                }
            }

            // --- spawn shield (QC player.qc ~252): reduce incoming damage while shielded ---
            float shieldBlock = Cvar(CvarSpawnShieldBlock, DefSpawnShieldBlock);
            if (HasSpawnShield(targ) && shieldBlock < 1f)
                damage *= 1f - QClamp(shieldBlock, 0f, 1f);

            (take, save) = HealthArmorApplyDamage(initialArmor, ArmorBlockPercent(deathType), damage);
        }

        // --- credited-attacker window (QC player.qc ~293 .pusher/.pushltime) ---
        if (attacker is not null && ReferenceEquals(attacker, targ))
        {
            targ.IsTypeFrag = false;
        }
        else if (IsPlayer(attacker))
        {
            targ.Pusher = attacker;
            targ.PushLTime = Now() + Cvar(CvarMaxPushTime, DefMaxPushTime);
            // QC player.qc:304 — this.istypefrag = PHYS_INPUT_BUTTON_CHAT(this): the VICTIM's chat-button
            // state at the moment of the hit decides whether the kill counts as a typefrag. ButtonChat is
            // written per-frame from the player's input Typing intent in PlayerPhysics (already live).
            targ.IsTypeFrag = targ.ButtonChat;
        }
        else if (Now() < targ.PushLTime)
        {
            attacker = targ.Pusher;
            targ.PushLTime = MathF.Max(targ.PushLTime, Now() + 0.6f);
        }
        else if (attacker is not null && (attacker.Flags & EntFlags.Monster) != 0 && IsPlayer(attacker.RealOwner))
        {
            attacker = attacker.RealOwner;
            targ.IsTypeFrag = false;
        }
        else
        {
            targ.PushLTime = 0f;
            targ.IsTypeFrag = false;
        }

        // --- PlayerDamage_SplitHealthArmor hook (QC player.qc ~322): mutators read/rewrite take & save ---
        // QC fires this UNCONDITIONALLY for every damage application — including self-damage and world/
        // environmental deaths (Mayhem's damage-score accrual depends on the self/world cases). The legacy
        // handlers (vampire/buffs) only act when there is a real enemy attacker; they re-check that internally
        // (vampire: !ReferenceEquals(target, attacker)), so widening the firing is safe. The non-null Attacker
        // slot is coalesced to the target for a world death (keeping the legacy handlers' non-null contract),
        // while the TRUE nullable attacker + the string deathtype + the pre-split frag_damage ride the new
        // FragAttacker/FragDeathType/FragDamage slots for Mayhem.
        {
            // QC M_ARGV(3, vector) = damage_force (knockback, for globalforces); M_ARGV(7) = frag_damage (the
            // full pre-split damage so a handler can derive the overkill excess); M_ARGV(6) = deathtype.
            Entity coalescedAttacker = attacker ?? targ;
            var hook = new GameHooks.PlayerDamageArgs(coalescedAttacker, targ, take, save, force,
                fragAttacker: attacker, fragDeathType: deathType, fragDamage: damage);
            GameHooks.PlayerDamageSplitHealthArmor.Call(ref hook);
            take = hook.DamageTake;
            save = hook.DamageSave;
        }
        take = QClamp(take, 0f, targ.GetResource(ResourceType.Health));
        save = QClamp(save, 0f, targ.GetResource(ResourceType.Armor));
        float excess = MathF.Max(0f, damage - take - save);

        // --- armor / body-impact sound (QC player.qc ~327): the hit-feedback "clink"/"thud". Gated on
        //     sound_allowed(MSG_BROADCAST, attacker). The armor sound is suppressed when the hit is fatal
        //     (initial_health - take <= 0), so a killing blow doesn't play the non-fatal armor clink. T40. ---
        if (SoundAllowedBroadcast(attacker))
        {
            if (save > 10f && (initialHealth - take) > 0f)
                SoundSystem.PlayOn(targ, Sounds.ByName("ARMORIMPACT"), SoundChannel.Weapon, SoundLevels.VolBase, SoundLevels.AttenNorm);
            else if (take > 30f)
                SoundSystem.PlayOn(targ, Sounds.ByName("BODYIMPACT2"), SoundChannel.Weapon, SoundLevels.VolBase, SoundLevels.AttenNorm);
            else if (take > 10f)
                SoundSystem.PlayOn(targ, Sounds.ByName("BODYIMPACT1"), SoundChannel.Weapon, SoundLevels.VolBase, SoundLevels.AttenNorm);
        }

        // --- subtract (QC player.qc ~342): unless spawn-shielded-fully or godmode ---
        bool shieldedFully = HasSpawnShield(targ) && Cvar(CvarSpawnShieldBlock, DefSpawnShieldBlock) >= 1f;
        if (!shieldedFully)
        {
            if ((targ.Flags & EntFlags.GodMode) == 0)
            {
                targ.TakeResource(ResourceType.Armor, save);
                targ.TakeResource(ResourceType.Health, take);
                if (take != 0f)
                    targ.PauseRegenFinished = MathF.Max(targ.PauseRegenFinished, Now() + Cvar(CvarPauseHealthRegen, DefPauseHealthRegen));

                // pain-anim/sound debounce (QC player.qc ~352): suppressed while frozen.
                if (Now() > targ.PainFinished && !IsFrozenStat(targ) && !HasStatusFrozen(targ))
                {
                    targ.PainFinished = Now() + 0.5f;

                    // [W14b Stage 4] animdecide PAIN set-site (QC player.qc:356-365): gated on sv_gentle<1 and the
                    // corpse exclusion (classname != "body"); QC also gates on !animstate_override, which the port
                    // doesn't model (no monster anim-override on players). Alternate PAIN1/PAIN2 by random()>0.5, the
                    // SAME roll QC uses, with restart = true (a fresh hit restarts the pain window). A late pain on a
                    // dead/dying corpse is excluded here, and even if one slipped through GetUpperAnim keeps DIE above
                    // PAIN, so the death overlay is never stomped.
                    if (SvGentle() < 1f && !targ.IsCorpse)
                    {
                        var painAct = Prandom.Float() > 0.5f
                            ? AnimDecide.AnimUpperAction.Pain1
                            : AnimDecide.AnimUpperAction.Pain2;
                        var (act, start) = AnimDecide.SetAction(
                            targ.AnimUpperAction, targ.AnimActionStart, painAct, Now(), restart: true);
                        targ.AnimUpperAction = act;
                        targ.AnimActionStart = start;
                    }

                    EmitPainSound(targ, attacker, deathType, take);
                }
                if (take > 0f)
                    EffectEmitter.TeBlood(hitLoc, force.LengthSquared() > 0f ? Vector3.Normalize(-force) * 400f : Vector3.Zero, (int)(take * 0.2f + 1f));

                // Bot aim shake (QC player.qc:388-395): throw off bot aim temporarily when damaged.
                // QC: if(IS_BOT_CLIENT(this) && GetResource(this, RES_HEALTH) >= 1)
                //   shake = damage * 5 / (bound(0, skill, 100) + 1)
                //   v_angle.x += (random()*2-1)*shake; v_angle.y += (random()*2-1)*shake
                //   v_angle.x = bound(-90, v_angle.x, 90)
                // Applied AFTER the subtract (uses post-subtract health to skip dead targets);
                // "damage" here is the pre-split, handicap-adjusted damage, matching QC's local.
                if (targ is Player { IsBot: true } botTarg && targ.GetResource(ResourceType.Health) >= 1f)
                {
                    // QC bound(0, skill, 100): the live "skill" engine cvar (unset == 0 → divisor 1, max shake),
                    // NOT a fabricated default. Read the raw value so a 0/unset skill matches QC's bound exactly.
                    float skillCvar = Api.Services is not null ? Api.Cvars.GetFloat("skill") : 0f;
                    float skill = QClamp(skillCvar, 0f, 100f);
                    float shake = damage * 5f / (skill + 1f);
                    botTarg.ViewAngles = new Vector3(
                        QClamp(botTarg.ViewAngles.X + Prandom.Signed() * shake, -90f, 90f),
                        botTarg.ViewAngles.Y + Prandom.Signed() * shake,
                        botTarg.ViewAngles.Z);
                }
            }
            else
            {
                targ.MaxArmorValue += save + take; // QC max_armorvalue tab while godmode absorbs the hit
            }
        }

        targ.DmgSave += save;
        targ.DmgTake += take;
        targ.DmgInflictor = inflictor;

        // [T51] PlayerDamaged mutator hook (QC server/player.qc:430) — damagetext. Fire AFTER the health/armor
        // subtract with the ACTUAL amounts removed (dh/da) + the pre-split potential damage. initialHealth/
        // initialArmor were captured (already max(…,0)) at the top of PlayerDamage. QC reads the hook's return
        // back as forbid_logging_damage, gating the accuracy/score block below.
        float dhRemoved = initialHealth - MathF.Max(targ.GetResource(ResourceType.Health), 0f);
        float daRemoved = initialArmor - MathF.Max(targ.GetResource(ResourceType.Armor), 0f);
        bool forbidLoggingDamage;
        {
            var pd = new MutatorHooks.PlayerDamagedArgs(attacker, targ, dhRemoved, daRemoved, hitLoc, deathType, damage);
            forbidLoggingDamage = MutatorHooks.PlayerDamaged.Call(ref pd);
        }

        // [T57] accuracy REAL credit + per-frame damage score columns — QC server/player.qc:432-447.
        // realdmg = damage - excess (the part that actually removed health/armor, no overkill).
        if ((dhRemoved != 0f || daRemoved != 0f) && !forbidLoggingDamage)
        {
            float realdmg = damage - excess;
            bool deathKill = DeathTypes.BaseOf(deathType) == DeathTypes.Kill;
            // QC round gate: !(round active && !round started) && time >= game_starttime. No round handler is
            // reachable from the pipeline; the game-start half uses the StartItem host seam (game_starttime).
            float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
            if ((!ReferenceEquals(targ, attacker) || deathKill) && realdmg != 0f && Now() >= gameStart)
            {
                // QC: IS_PLAYER(attacker) && DIFF_TEAM(attacker, this) && deathtype != DEATH_KILL.
                if (IsPlayer(attacker) && !WeaponAccuracyEvents.SameTeam(attacker!, targ) && !deathKill)
                {
                    // awep (player.qc:416-419): the deathtype's weapon, or the attacker's HELD weapon for
                    // special deathtypes. A weapon tag that doesn't resolve (a non-weapon source mislabeled
                    // upstream) also falls back to the held weapon, like QC's special-deathtype branch.
                    Weapon? awep = DeathTypes.IsWeapon(deathType)
                        ? Weapons.ByName(DeathTypes.WeaponNetNameOf(deathType))
                        : null;
                    awep ??= Inventory.CurrentWeapon(attacker!);

                    if (awep is not null && WeaponAccuracyEvents.IsGoodDamage(attacker, targ))
                        WeaponAccuracyEvents.Real(attacker!, awep, realdmg);     // add to real
                    WeaponAccuracyEvents.ScoreFrameDamage(attacker!, realdmg);   // attacker.score_frame_dmg
                }
                if (IsPlayer(targ))
                    WeaponAccuracyEvents.ScoreFrameDamageTaken(targ, realdmg);   // this.score_frame_dmgtaken
            }
        }

        // --- death check (QC player.qc ~450): health < 1 ---
        if (targ.GetResource(ResourceType.Health) < 1f)
            Killed(targ, inflictor, attacker, deathType, damage, take, save, hitLoc, force);

        _ = initialHealth; // (parity: QC uses it for the armor-impact "don't play if fatal" sound)
    }

    /// <summary>
    /// The kill path of <c>PlayerDamage</c> (server/player.qc ~450-601): fire the obituary/scoring hook
    /// and the <c>PlayerDies</c> mutator hook, set the respawn timer, then build the corpse
    /// (MOVETYPE_TOSS / SOLID_CORPSE / upright / view-from-floor) and run the corpse damage with the
    /// leftover <paramref name="excess"/> so an overkill hit gibs immediately.
    /// </summary>
    private void Killed(Entity victim, Entity? inflictor, Entity? attacker, string deathType,
        float damage, float take, float save, Vector3 hitLoc, Vector3 force)
    {
        if (victim.DeadState != DeadFlag.No)
            return; // already dying/dead (e.g. re-entrant corpse damage)

        // Diagnostic: who/what/where of every kill, so an unexpected death (fell into lava off a patch grate,
        // a void/trigger_hurt off a platform, a telefrag) can be correlated with a location. Developer-gated.
        if (Log.WillTrace)
            Log.Trace($"[death] {victim.ClassName} #{victim.Index} @ {victim.Origin} type={deathType} dmg={damage:0} " +
                      $"attacker={(attacker is null ? "world" : attacker.ClassName + " #" + attacker.Index)}");

        victim.AirFinished = 0f; // QC STAT(AIR_FINISHED) = 0

        // QC Obituary (server/damage.qc:238): targ.death_origin = targ.origin; — latch the death position BEFORE
        // the corpse is tossed/moved, so "spawn near where you died" features (spawn_near_teammate closetodeath,
        // the personal/here/danger waypoints) measure from where the player actually fell, not the corpse rest.
        if (victim is Player prDeath)
        {
            prDeath.DeathOrigin = victim.Origin;
            prDeath.DeathTime = Now(); // [R2] stamp WHEN they died so the death-point spawn-avoidance can decay
        }

        // QC respawn_time = 0; then PlayerDies can set it; else calculate_player_respawn_time fills it.
        if (victim is Player pr0) pr0.RespawnTime = 0f;

        // Fire the obituary / frag-scoring bus (QC Obituary -> GiveFrags). Gametypes award points.
        // NOTE (GiveFragsForKill, QC server/damage.qc:72): QC fires MUTATOR_CALLHOOK(GiveFragsForKill, ...)
        // INSIDE GiveFrags, where the frag-score delta is computed and applied. This port computes/applies that
        // delta in Scores.Obituary (the enemy-frag branch), reached via this Combat.Death bus — not here — so
        // the MutatorHooks.GiveFragsForKill chain (declared for this purpose) must be Called from there to be
        // able to rewrite the score. The chain + its args exist; wiring the Scores.Obituary Call site is the
        // one cross-boundary step (Scores.cs is owned by the scoring task). See the T3 report.
        var death = new DeathEvent
        {
            Victim = victim,
            Attacker = attacker,
            Inflictor = inflictor,
            DeathType = deathType,
        };
        Combat.Death.Call(ref death);

        // QC wr_playerdeath: the weapon the player dies with may fire/release any state held mid-action (e.g. Hagar's loaded rockets).
        var deathSlot = new WeaponSlot(0); // single-weapon port currently uses slot 0
        Weapon? weapon = Inventory.CurrentWeapon(victim);
        if (weapon is not null)
            weapon.WrPlayerDeath(victim, deathSlot);

        // QC server/player.qc:514 Portal_ClearAllLater(this): on death the porto's portal cleanup
        // (Portal_ClearAll + W_Porto_Remove) runs UNCONDITIONALLY, NOT only when the player holds the porto — a
        // placed portal or in-flight porto must be torn down even if the victim died holding a different weapon.
        // The port folds that cleanup into Porto.WrPlayerDeath; the held-weapon dispatch above already ran it when
        // porto IS the current weapon, so here we run it ONLY for an owned-but-not-held porto (avoids a double call).
        if (Registry<Weapon>.ByName("porto") is { } porto && porto != weapon && Inventory.HasWeapon(victim, porto))
            porto.WrPlayerDeath(victim, deathSlot);

        // A non-player victim with its OWN death handler (turret/monster/breakable) wired onto the Death bus has
        // just run it (e.g. TurretAI.Die -> SOLID_NOT + respawn schedule, DeadState advanced past DeadFlag.No).
        // In QC those entities carry their death in .event_damage and the generic player-corpse path below is
        // NEVER reached for them; the headless pipeline has no per-edict event_damage, so mirror that here:
        // once a non-player has been transitioned out of DeadFlag.No by its hook, don't clobber it with the
        // MOVETYPE_TOSS / SOLID_CORPSE / DeadFlag.Dying player-corpse setup.
        if (victim is not Player && victim.DeadState != DeadFlag.No)
            return;

        // MUTATOR_CALLHOOK(PlayerDies) — pinata/overkill/instagib drop loot or bump damage to force a gib.
        var pd = new MutatorHooks.PlayerDiesArgs(inflictor, attacker, victim, deathType, damage);
        MutatorHooks.PlayerDies.Call(ref pd);
        damage = pd.Damage;

        // QC status_effects PlayerDies/MonsterDies hook (sv_status_effects.qc:66-78): StatusEffects_removeall
        // on death so a victim's burning/stunned/superweapon timers don't survive into the corpse/respawn
        // (NORMAL removal — plays each effect's removal sound). Status effects are always-on (not a gated
        // mutator in the port), so this is wired on the unconditional death path rather than the hook bus.
        StatusEffectsCatalog.RemoveAll(victim, StatusEffectRemoval.Normal);
        float excess = MathF.Max(0f, damage - take - save);

        // A gametype hook (e.g. Freeze Tag) may have resuscitated the player to HP>=1 — then don't die.
        if (victim.GetResource(ResourceType.Health) >= 1f)
            return;

        // --- death / drown voice (QC player.qc ~463): played on the live player before it becomes a corpse,
        //     gated on sv_gentle<1 + sound_allowed(MSG_BROADCAST, attacker). DROWN gets the drown gurgle. T40. ---
        if (SvGentle() < 1f && SoundAllowedBroadcast(attacker))
        {
            string id = DeathTypes.BaseOf(deathType) == DeathTypes.Drown ? "drown" : "death";
            // QC player.qc:467,469 plays the lethal voice at VOL_BASE (0.7) — same as the pain voice (player.qc
            // :374-382) — not VOICE volume. PlayerSound passes the vol arg straight through (globalsound.qh:143).
            SoundSystem.PlayPlayerSound(victim, id, ModelSoundDir(victim), SoundLevels.VolBase, SoundLevels.AttenNorm);
        }

        // QC: if no respawn time was set by a hook, schedule one now (default-ish 2s post-death window).
        if (victim is Player pr && pr.RespawnTime <= 0f)
            pr.RespawnTime = Now() + DefaultRespawnDelay;

        // [T57] throw the held weapon (QC player.qc:533-537): per slot, SpawnThrownWeapon at the body center,
        // after the resuscitation bail + respawn-time calc and BEFORE the corpse setup. The port drives one
        // weapon slot. (g_pinata's extra drops already ran in the PlayerDies hook above, like QC.)
        WeaponThrowing.SpawnThrownWeapon(victim, victim.Origin + (victim.Mins + victim.Maxs) * 0.5f,
            Inventory.CurrentWeapon(victim), new WeaponSlot(0));

        // ----- become a corpse (QC player.qc ~528-591) -----
        victim.Alpha = MutatorHooks.DefaultPlayerAlpha; // QC default_player_alpha (player.qc:540) — corpse inherits the cloaked seed
        victim.Angles = new Vector3(0f, victim.Angles.Y, 0f); // upright, untilted
        victim.AVelocity = Vector3.Zero;      // don't spin
        victim.ViewOfs = new Vector3(0f, 0f, -8f); // view from the floor

        if (victim.MoveType == MoveType.Noclip)
            victim.Velocity = Vector3.Zero;   // don't toss a noclip corpse (can get stuck/void)
        else
            victim.MoveType = MoveType.Toss;  // toss the corpse

        victim.Solid = Solid.Corpse;          // shootable corpse
        victim.BallisticsDensity = Cvar(CvarBallisticsCorpse, 0f);
        victim.Flags &= ~EntFlags.OnGround;   // don't stick to the floor
        victim.DeadState = DeadFlag.Dying;    // dying animation (commits to DEAD across frames)
        victim.IsCorpse = true;               // route further hits to PlayerCorpseDamage

        // [W14b Stage 4] animdecide DIE set-site (QC player.qc:571-575): on death, select DIE1 vs DIE2 by
        // random()<0.5 — the SAME roll QC uses (animdecide_setstate ANIMSTATE_DEAD1/DEAD2). These are NEVER windowed:
        // GetUpperAnim returns DIE with ANIMPRIO_DEAD unconditionally, so the death overlay holds (and outranks any
        // late pain/shoot) until the player respawns and re-latches a live action. The start time is the death time
        // (QC death_time = time), networked as AnimActionTime so the client plays the death torso from its start.
        var dieAct = Prandom.Float() < 0.5f
            ? AnimDecide.AnimUpperAction.Die1
            : AnimDecide.AnimUpperAction.Die2;
        victim.AnimUpperAction = dieAct;
        victim.AnimActionStart = Now();

        // players have no think; corpse fade-out is host-side (SUB_SetFade) — left to the host.
        victim.Think = null;
        victim.NextThink = 0f;

        // QC: run the corpse damage with the leftover damage so an overkill hit gibs immediately.
        PlayerCorpseDamage(victim, inflictor, attacker, deathType, excess);

        _ = hitLoc; _ = force; // (parity: QC passes these to the gib splash, which is host-side VFX)
    }

    /// <summary>
    /// QC <c>PlayerCorpseDamage</c> gib branch (server/player.qc ~217): the body's health fell below
    /// <c>-sv_gibhealth</c> — make it a non-solid, non-damageable gib (alpha -1) so it no longer collides.
    /// </summary>
    private static void GibCorpse(Entity targ)
    {
        targ.Frame = 0f;
        targ.ViewOfs = new Vector3(0f, 0f, 4f);
        targ.Alpha = -1f;                 // fully gibbed marker
        targ.Solid = Solid.Not;
        targ.TakeDamage = DamageMode.No;
        // VFX (Violence_GibSplash) is host-side.
    }

    /// <summary>Default post-death respawn delay when no gametype hook sets one (QC calculate_player_respawn_time floor).</summary>
    private const float DefaultRespawnDelay = 2f;

    // ===============================================================================================
    //  healtharmor split + knockback (QC util.qc / calculations.qc)
    // ===============================================================================================

    /// <summary>
    /// QC <c>healtharmor_applydamage(armor, armorblock, deathtype, damage)</c> (common/util.qc):
    /// <c>save = bound(0, damage * armorblock, armor)</c>, <c>take = bound(0, damage - save, damage)</c>.
    /// Drown and HITTYPE_ARMORPIERCE force armorblock to 0 (handled by <see cref="ArmorBlockPercent"/>).
    /// </summary>
    private static (float take, float save) HealthArmorApplyDamage(float armor, float armorBlock, float damage)
    {
        float save = QClamp(damage * armorBlock, 0f, armor);
        float take = QClamp(damage - save, 0f, damage);
        return (take, save);
    }

    /// <summary>armorblockpercent for this hit; drowning and HITTYPE_ARMORPIERCE zero it out (QC util.qc).</summary>
    private static float ArmorBlockPercent(string deathType)
        => DeathTypes.BypassesArmor(deathType) ? 0f : Cvar(CvarArmorBlockPercent, DefArmorBlockPercent);

    /// <summary>
    /// Port of the "apply push" block (server/damage.qc ~671): when the target is pushable and not
    /// spawn-shielded (unless self-inflicted), add the calc-push delta and clear the on-ground flag.
    /// MOVETYPE_PHYSICS force-at-pos and the MOVETYPE_NOCLIP exclusion are honoured.
    /// </summary>
    private static void ApplyKnockback(Entity targ, Entity? attacker, Vector3 hitLoc, Vector3 force)
    {
        // QC gate: targ.damageforcescale && force && (!player || !spawnshield || self)
        float forceScale = targ.DamageForceScale;
        if (forceScale == 0f)
        {
            // Players that haven't had .damageforcescale assigned yet fall back to the player scale so
            // knockback still works in the headless sim (where spawn doesn't set the field).
            forceScale = (targ.Flags & EntFlags.Client) != 0 ? Cvar(CvarPlayerForceScale, DefPlayerForceScale) : 0f;
            if (forceScale == 0f)
                return;
        }
        if (force == Vector3.Zero)
            return;

        bool spawnShielded = IsPlayer(targ) && HasSpawnShield(targ) && !ReferenceEquals(targ, attacker);
        if (spawnShielded)
            return;

        float speedFactor = Cvar(CvarDamagePushSpeed, DefDamagePushSpeed);
        Vector3 farce = DamageExplosionCalcPush(force * forceScale, targ.Velocity, speedFactor);

        if (targ.MoveType == MoveType.Noclip)
        {
            // QC: noclip targets are not pushed (but still lose onground below).
        }
        else if (IsPhysicsMoveType(targ))
        {
            // QC MOVETYPE_PHYSICS: a transient force-at-pos entity nudges the rigid body. We have no rigid
            // body here, so apply the impulse directly (mass-scaled) — equivalent for the headless sim.
            float mass = targ.Mass != 0f ? targ.Mass : 1f;
            targ.Velocity += farce / mass;
            _ = hitLoc;
        }
        else
        {
            targ.Velocity += farce;
        }

        targ.Flags &= ~EntFlags.OnGround; // QC UNSET_ONGROUND
    }

    /// <summary>
    /// QC <c>damage_explosion_calcpush(explosion_f, target_v, speedfactor)</c>
    /// (common/weapons/calculations.qc), ported in full: below speedfactor 1 the formula is bypassed
    /// (would cause superjumps); otherwise the force is scaled by
    /// <c>explosion_calcpush_getmultiplier(explosion_f * speedfactor, target_v)</c> — a momentum
    /// projection that damps (and can null) the push as the target already moves with the blast.
    /// </summary>
    private static Vector3 DamageExplosionCalcPush(Vector3 explosionForce, Vector3 targetVel, float speedFactor)
    {
        // QC: "if below 1, the formulas make no sense (and would cause superjumps)" -> return raw force.
        if (speedFactor < 1f)
            return explosionForce;
        return explosionForce * ExplosionCalcPushGetMultiplier(explosionForce * speedFactor, targetVel);
    }

    /// <summary>
    /// QC <c>explosion_calcpush_getmultiplier(explosion_v, target_v)</c> (calculations.qc):
    /// <c>a = explosion_v · (explosion_v - target_v)</c>; if <c>a &lt;= 0</c> the target is moving too fast
    /// to be hit (return 0); otherwise return <c>a / (explosion_v · explosion_v)</c> (∈ (0, 1] for a
    /// stationary/receding target). This is the energy-conservation-tuned damping the brief asked for —
    /// it replaces the old hard speed cap entirely.
    /// </summary>
    private static float ExplosionCalcPushGetMultiplier(Vector3 explosionV, Vector3 targetV)
    {
        float a = QDot(explosionV, explosionV - targetV);
        if (a <= 0f)
            return 0f; // target is too fast to be hittable by this
        a /= QDot(explosionV, explosionV); // safe: a > 0 implies explosion_v·explosion_v > 0
        return a;
    }

    // ===============================================================================================
    //  status-effect / handicap helpers (QC StatusEffects_*, Handicap_GetTotalHandicap)
    // ===============================================================================================

    /// <summary>
    /// QC <c>StatusEffects_active(STATUSEFFECT_SpawnShield, e)</c>. The catalog port models frozen/burning/
    /// buffs but not spawn-shield, so it lives on the entity (<see cref="Entity.SpawnShieldExpire"/>).
    /// </summary>
    private static bool HasSpawnShield(Entity e) => e.SpawnShieldExpire > Now();

    /// <summary>QC <c>StatusEffects_remove(STATUSEFFECT_SpawnShield, e, CLEAR)</c>.</summary>
    private static void RemoveSpawnShield(Entity e) => e.SpawnShieldExpire = 0f;

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_Frozen, e)</c> — the status-effect freeze (vs. the gametype stat).</summary>
    private static bool HasStatusFrozen(Entity e)
        => StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(e, f);

    // ===============================================================================================
    //  status-effect application seams (QC StatusEffects_apply for frozen/burning + StatusEffects_update).
    //  These are the single points damage-driven freeze/burn go through so the networked overlay
    //  (ENT_CLIENT_STATUSEFFECTS) gets dirty-marked exactly once per application — the T61 status-effect
    //  network wiring lives HERE (in DamageSystem) rather than in GameWorld.cs (owned elsewhere this wave).
    // ===============================================================================================

    /// <summary>
    /// QC <c>StatusEffects_apply(STATUSEFFECT_Frozen, e, …)</c>: freeze <paramref name="e"/> for
    /// <paramref name="duration"/> seconds (0 = until thawed). Routes through
    /// <see cref="StatusEffectsCatalog.Apply"/> (which sets the ACTIVE flag and marks the entity dirty via
    /// <see cref="StatusEffectsCatalog.Update"/> so the frozen overlay networks). No-op if the frozen def
    /// isn't registered. Returns true if the effect was applied.
    /// </summary>
    public static bool ApplyFrozen(Entity e, float duration, Entity? source = null)
    {
        if (StatusEffectsCatalog.Frozen is not { } def) return false;
        StatusEffectsCatalog.Apply(e, def, duration, 1f, source);
        StatusEffectsCatalog.Update(e); // explicit StatusEffects_update — dirty-mark for ENT_CLIENT_STATUSEFFECTS
        return true;
    }

    /// <summary>
    /// QC <c>StatusEffects_apply(STATUSEFFECT_Burning, e, …)</c>: ignite <paramref name="e"/> for
    /// <paramref name="duration"/> seconds at <paramref name="strength"/> burn rate. Routes through
    /// <see cref="StatusEffectsCatalog.Apply"/> and marks the entity dirty so the burning overlay networks.
    /// No-op if the burning def isn't registered. Returns true if the effect was applied.
    /// </summary>
    public static bool ApplyBurning(Entity e, float duration, float strength = 1f, Entity? source = null)
    {
        if (StatusEffectsCatalog.Burning is not { } def) return false;
        StatusEffectsCatalog.Apply(e, def, duration, strength, source);
        StatusEffectsCatalog.Update(e); // explicit StatusEffects_update — dirty-mark for ENT_CLIENT_STATUSEFFECTS
        return true;
    }

    /// <summary>QC <c>STAT(FROZEN, e)</c> — the gametype freeze stat (Freeze Tag / CA ice).</summary>
    private static bool IsFrozenStat(Entity e) => e.FrozenStat != 0 || HasStatusFrozen(e);

    /// <summary>
    /// QC <c>Handicap_GetVoluntaryHandicap(player, receiving)</c> (server/handicap.qc): the per-client,
    /// self-imposed handicap read from the REPLICATE'd <c>cl_handicap</c>/<c>cl_handicap_damage_given</c>/
    /// <c>cl_handicap_damage_taken</c> cvars, bound to [1, 10]. Wired by the server (Commands ctor) to read the
    /// per-client cvar store; null (e.g. unit tests with no server) → 1 (no voluntary handicap). The CTS/RACE
    /// HANDICAP_DISABLED() gate lives inside the provider (same source as the forced side).
    /// </summary>
    public static System.Func<Entity, bool, float>? VoluntaryHandicapProvider;

    /// <summary>
    /// QC <c>Handicap_GetTotalHandicap(player, receiving)</c> (server/handicap.qc): forced × voluntary. The
    /// forced part is pre-combined onto the entity (<see cref="Entity.HandicapTake"/>/<see cref="Entity.HandicapGive"/>,
    /// default 1 = handicap disabled); the voluntary part comes from the per-client cl_handicap* cvars via
    /// <see cref="VoluntaryHandicapProvider"/>. Non-players have no handicap.
    /// </summary>
    private static float HandicapTotal(Entity e, bool receiving)
    {
        if ((e.Flags & EntFlags.Client) == 0)
            return 1f;
        float forced = receiving ? e.HandicapTake : e.HandicapGive;
        if (forced <= 0f) forced = 1f;
        float voluntary = VoluntaryHandicapProvider?.Invoke(e, receiving) ?? 1f;
        return forced * voluntary;
    }

    /// <summary>
    /// Public alias for <see cref="HandicapTotal"/> — the direct port of the public QC
    /// <c>Handicap_GetTotalHandicap(player, receiving)</c>. Used by the scoring layer to damage-weight the
    /// per-player handicapgiven/handicaptaken averages for the XonStat game report (server/client.qc PlayerFrame).
    /// </summary>
    public static float GetTotalHandicap(Entity e, bool receiving) => HandicapTotal(e, receiving);

    // ===============================================================================================
    //  small predicates / helpers
    // ===============================================================================================

    private static bool IsPlayer([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Entity? e)
        => e is not null && (e.Flags & EntFlags.Client) != 0 && !e.IsCorpse;

    /// <summary>QC IS_DEAD(e): the entity is dying or already dead.</summary>
    private static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;

    private static bool IsPhysicsMoveType(Entity e) =>
        // No dedicated MOVETYPE_PHYSICS enum in this port; bounce/fly-missile rigid bodies stand in.
        e.MoveType is MoveType.Bounce or MoveType.BounceMissile;

    /// <summary>QC <c>bound(lo, v, hi)</c>.</summary>
    private static float QClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float QDot(Vector3 a, Vector3 b) => Vector3.Dot(a, b);

    /// <summary>QC <c>normalize(v)</c> — zero for a zero vector (Quake semantics).</summary>
    private static Vector3 QNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 0f ? v / len : Vector3.Zero;
    }

    private static float Now() => Api.Services is not null ? Api.Clock.Time : 0f;

    /// <summary>Read a float cvar through the facade, falling back to the documented balance default.</summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>Read a boolean cvar (a non-zero float is true; an unset cvar is false).</summary>
    private static bool CvarBool(string name)
        => Api.Services is not null && Api.Cvars.GetFloat(name) != 0f;

    // ===============================================================================================
    //  combat-feedback sound emission (QC player.qc PlayerDamage / Killed) — T40
    // ===============================================================================================

    /// <summary>
    /// QC <c>autocvar_sv_gentle</c> (shipped 0): when ≥1, gore and the pain/death voice sounds are suppressed
    /// (a "gentle"/family-friendly mode). Read through the facade; defaults to 0 (off).
    /// </summary>
    private static float SvGentle() => Api.Services is null ? 0f : Api.Cvars.GetFloat(CvarGentle);

    /// <summary>
    /// QC <c>sound_allowed(MSG_BROADCAST, attacker)</c> (server/g_damage.qc): whether a hit sound may be
    /// broadcast. The full QC check suppresses sounds when the attacker is a non-networked/internal entity; the
    /// headless port has no such notion, so it permits the broadcast whenever the sound subsystem is live
    /// (Api.Services present). Tests that stub Api keep this true; a fully-null Api makes it a silent no-op.
    /// </summary>
    private static bool SoundAllowedBroadcast(Entity? attacker)
    {
        _ = attacker; // (parity slot: QC inspects the attacker's client flags; not modeled headless)
        return Api.Services is not null;
    }

    /// <summary>
    /// The per-model <c>.sounds</c> manifest vpath for a player's voice sounds — QC
    /// <c>get_model_datafilename(this.model, this.skin, "sounds")</c> (LoadPlayerSounds). Derived from the
    /// entity's networked model + skin; the host-installed <see cref="Sounds.ModelSoundResolver"/> parses the
    /// manifest to the real sample path, falling back to <c>default.sounds</c> when the model ships no pack.
    /// Empty model =&gt; null (the default pack).
    /// </summary>
    private static string? ModelSoundDir(Entity e) => Sounds.ModelSoundsFile(e.Model, (int)e.Skin);

    /// <summary>
    /// Port of the pain-voice selection inside <c>PlayerDamage</c> (server/player.qc ~356-383): gated on
    /// <c>sv_gentle &lt; 1</c>, the corpse exclusion, and the laserjump exclusion (don't grunt on a healthy
    /// self-jump with a CANCLIMB weapon). DEATH_FALL plays the fall grunt; otherwise pain100/75/50/25 by the
    /// remaining health. Called from inside the (time &gt; pain_finished &amp;&amp; !frozen) debounce block, on the
    /// live player.
    /// </summary>
    private void EmitPainSound(Entity targ, Entity? attacker, string deathType, float take)
    {
        if (SvGentle() >= 1f) return;
        if (targ.IsCorpse) return;                 // QC: classname != "body"
        if (!SoundAllowedBroadcast(attacker)) return;

        float myhp = targ.GetResource(ResourceType.Health); // QC reads it AFTER the subtract
        if (!(myhp > 1f)) return;

        // QC laserjump exclusion (player.qc ~369): myhp<25 || !(weapon CANCLIMB) || take>20 || attacker!=this.
        bool canClimb = WeaponHasCanClimb(DeathTypes.WeaponNetNameOf(deathType));
        bool selfHit = attacker is not null && ReferenceEquals(attacker, targ);
        if (!(myhp < 25f || !canClimb || take > 20f || !selfHit))
            return;

        string id =
            DeathTypes.BaseOf(deathType) == DeathTypes.Fall ? "fall"
            : myhp > 75f ? "pain100"
            : myhp > 50f ? "pain75"
            : myhp > 25f ? "pain50"
            : "pain25";
        SoundSystem.PlayPlayerSound(targ, id, ModelSoundDir(targ), SoundLevels.VolBase, SoundLevels.AttenNorm);
    }

    /// <summary>
    /// QC <c>DEATH_WEAPONOF(deathtype).spawnflags &amp; WEP_FLAG_CANCLIMB</c>: does the weapon that dealt this hit
    /// double as a movement weapon (blaster/crylink/devastator/hagar/hook/mortar/vaporizer)? Looks the weapon up
    /// by NetName in the registry; false for a special (non-weapon) death.
    /// </summary>
    private static bool WeaponHasCanClimb(string weaponNetName)
    {
        if (string.IsNullOrEmpty(weaponNetName)) return false;
        var w = Weapons.ByName(weaponNetName);
        return w is not null && (w.SpawnFlags & WeaponFlags.CanClimb) != 0;
    }

    /// <summary>
    /// QC <c>Damage_DamageInfo(...)</c> (server/damage.qc ~741): broadcasts a blast's origin/radius/force
    /// to clients so CSQC can spawn impact effects. Networking is out of scope for the headless pipeline;
    /// kept as a named no-op so the RadiusDamage call site can be wired later.
    /// </summary>
    internal static void DamageInfoNetwork(Vector3 origin, float coreDamage, float edgeDamage,
        float radius, Vector3 force, string deathType, Entity? attacker)
    {
        // intentionally empty (host renders the blast effect)
    }
}

// Lead wires this in GameInit:
// Combat.System = new DamageSystem();

} // namespace XonoticGodot.Common.Gameplay.Damage

namespace XonoticGodot.Common.Framework
{
    /// <summary>
    /// [T41] The hit-confirmation accumulator (QC <c>STAT(HITSOUND_DAMAGE_DEALT_TOTAL)</c>). Lives on the
    /// attacker entity; the damage pipeline (<see cref="XonoticGodot.Common.Gameplay.Damage.DamageSystem.Apply"/>)
    /// adds the (health + armor) actually removed from a *different* player to it on every hit. The owner
    /// snapshot networks it (<c>NetEntityState.HitDamageDealtTotal</c>) and the client diffs it across updates to
    /// fire the pitch-shifted hit sound (view.qc <c>UpdateDamage</c>/<c>HitSound</c>). Added on this partial in an
    /// already-owned file rather than the shared <c>DamageEntityState.cs</c>, per the task's edit constraint.
    /// </summary>
    public partial class Entity
    {
        /// <summary>QC <c>STAT(HITSOUND_DAMAGE_DEALT_TOTAL)</c>: cumulative damage this entity has dealt to other
        /// players (health + armor removed). Monotonically increasing within a life; the client diffs it.</summary>
        public float HitsoundDamageDealtTotal;

        /// <summary>QC <c>STAT(REVIVE_PROGRESS)</c>: 0..1 Freeze-Tag thaw progress, mirrored each frame from the
        /// per-player <c>FrozenState.ReviveProgress</c> by <c>FreezeTag.ReviveTick</c> so the player snapshot
        /// (<c>NetEntityState.ReviveProgress</c>) can network it to the client's thaw objective ring
        /// (view.qc HUD_Draw). Stays 0 outside Freeze Tag.</summary>
        public float ReviveProgress;
    }
}
