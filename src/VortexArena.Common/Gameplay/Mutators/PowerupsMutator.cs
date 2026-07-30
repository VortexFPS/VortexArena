// Port of qcsrc/common/mutators/mutator/powerups/sv_powerups.qc (the consumer side of the powerups) +
// the per-powerup m_tick/m_apply/m_remove from powerup/{strength,shield,speed,invisibility}.qc.
//
// The powerup STATUS-EFFECT DEFS (strength/shield/speed/invisibility) and the PRODUCER (ItemPickupRules
// applying them on pickup) already exist; this is the CONSUMER side QC kept in sv_powerups.qc:
//   Damage_Calculate (30-59)            -> strength ×damage/×force, shield ×takedamage/×takeforce
//   PlayerPhysics_UpdateStats (179-186) -> speed ×highspeed (rides MutatorHooks.PlayerPhysics -> SpeedMultiplier)
//   WeaponRateFactor (188-194)          -> speed ×attack_time_multiplier (the new WeaponRateFactor hook)
//   PlayerDies (149-161)                -> drop each active powerup as a pickup-able item (drop_ondeath)
//   invisibility m_tick (invisibility.qc:33) -> alpha = invisibility_alpha while active (PlayerPreThink)
// plus the per-effect m_tick/m_apply/m_remove the port has no per-effect tick for, folded into PlayerPreThink:
//   strength/shield m_tick (28-33)      -> actor.effects |= (EF_BLUE|EF_RED) | EF_ADDITIVE | EF_FULLBRIGHT glow
//   *.qc m_apply / m_remove(TIMEOUT)    -> INFO_POWERUP_X broadcast + CENTER_POWERUP_X / CENTER_POWERDOWN_X
//   *.qc m_tick play_countdown(SND_POWEROFF) -> the last-6-seconds countdown beep (client.qc:1532)
//
// All keyed off StatusEffects being active on the entity (QC StatusEffects_active), NOT a g_powerups cvar:
// the powerups mutator is always-registered in QC; its hooks only fire when a player HAS the effect. So this
// mutator is always enabled (IsEnabled true) and every handler self-gates on StatusEffectsCatalog.Has.
//
// Wave-6b additions (now live for this file):
//   - the strength-fire sound (W_PlayStrengthSound) via the new MutatorHooks.WPlayStrengthSound hook, fired
//     once per bullet shot from WeaponFiring.FireBullet (the port analog of QC's fireBullet tracing block);
//   - the +use drop (PlayerUseKey hook, MutatorHooks.FirePlayerUseKey at VehicleBoarding.cs);
//   - the server-browser mutator strings (BuildMutatorsString / BuildMutatorsPrettyString overrides);
//   - bot stealth (Bot_ForbidAttack) via the new MutatorHooks.BotForbidAttack chain (consulted in
//     BotBrain.ShouldAttack alongside the host's gametype ForbidAttackHook delegate);
//   - monster stealth (MonsterValidTarget) via the new MutatorHooks.MonsterValidTarget chain (consulted in
//     MonsterAI.ValidTarget) — an invisible player is no longer acquired by monsters.
//
// Wave-8d additions (now live for this file):
//   - the exterior weapon-entity alpha + the if(!actor.vehicle) guard on the invisibility alpha apply/restore
//     (the held weapon node fades with the body in ViewEntityRenderer);
//   - the radar invisibility stealth (CustomizeWaypoint) — folded into ServerNet.WaypointVisible: an invisible
//     carrier's waypoint is hidden from enemy radar;
//   - the WP_Item countdown waypoint sprite on a dropped powerup + powerups_DropItem_Think (the time-left bar)
//     + the ItemTouched waypoint-kill-on-pickup hook.
//
// Wave-15 additions (now live for this file):
//   - the obituary item codes (LogDeath_AppendItemCodes, sv_powerups.qc:17): the new virtual
//     MutatorBase.LogDeathAppendItemCodes (chained by MutatorActivation) appends "S" (Strength)/"I" (Shield) to
//     a player's death-log item codes. The port's LogDeath pipeline is now wired: Scores.Obituary builds the
//     :kill:<mode>:<killer>:<killed>:type=<dt>:items=<codes> line (server/damage.qc:100 LogDeath +
//     AppendItemcodes) and hands it to GameLog via the Scores.LogDeath sink (GameWorld wires it, gated on
//     sv_eventlog). The S/I codes ride that :items=/:victimitems= field.

using System.Numerics;
using XonoticGodot.Common.Framework;
using static XonoticGodot.Common.Gameplay.Items;
using XonoticGodot.Common.Gameplay.Waypoints;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Powerups consumer mutator — applies the gameplay effects of the strength / shield / speed /
/// invisibility powerups while a player holds them. Always enabled (the effects self-gate on the
/// status-effect being active, mirroring QC's always-registered powerups mutator).
/// </summary>
[Mutator]
public sealed class PowerupsMutator : MutatorBase
{
    public PowerupsMutator() => NetName = "powerups";

    // QC: the powerups mutator is REGISTER_MUTATOR(powerups, true) — always registered. Its hooks only have
    // an effect when a player actually holds a powerup, so "enabled" is simply "services available".
    public override bool IsEnabled => Api.Services is not null;

    // ---- balance defaults (balance-xonotic.cfg:228-240) ----
    private float _strengthDamage = 3f;        // g_balance_powerup_strength_damage
    private float _strengthForce = 3f;         // g_balance_powerup_strength_force
    private float _strengthSelfDamage = 1.5f;  // g_balance_powerup_strength_selfdamage
    private float _strengthSelfForce = 1.5f;   // g_balance_powerup_strength_selfforce
    private float _invincibleTakeDamage = 0.33f; // g_balance_powerup_invincible_takedamage
    private float _invincibleTakeForce = 0.33f;  // g_balance_powerup_invincible_takeforce
    private float _speedHighspeed = 1.5f;      // g_balance_powerup_speed_highspeed
    private float _speedAttackRate = 0.8f;     // g_balance_powerup_speed_attack_time_multiplier
    private float _invisibilityAlpha = 0.15f;  // g_balance_powerup_invisibility_alpha

    // ---- drop-on-death / drop-on-use config (xonotic-server.cfg:208-209, 245) ----
    private int _dropOnDeath = 1;              // g_powerups_drop_ondeath (0=off, 1=timer continues, 2=freeze)
    private int _dropOnUse = 0;                // g_powerups_drop (0=off, 1=timer continues, 2=freeze) — +use drop
    private float _droppedLifetime = 20f;      // g_items_dropped_lifetime (frozen-timer drop lifetime)

    // ---- per-powerup max duration (balance-xonotic.cfg) — the WP_Item waypoint health-bar scale (maxtime) ----
    private float _strengthTime = 30f;     // g_balance_powerup_strength_time
    private float _invincibleTime = 30f;   // g_balance_powerup_invincible_time
    private float _speedTime = 30f;        // g_balance_powerup_speed_time
    private float _invisibilityTime = 30f; // g_balance_powerup_invisibility_time

    // ---- strength-fire sound anti-spam (xonotic-server.cfg:604-605) ----
    private float _strengthAntispamTime = 0.1f;       // sv_strengthsound_antispam_time
    private float _strengthAntispamRefire = 0.04f;    // sv_strengthsound_antispam_refire_threshold
    // QC player.prevstrengthsound / .prevstrengthsoundattempt (the per-player anti-spam stamps).
    private readonly System.Collections.Generic.Dictionary<Entity, float> _prevStrengthSound = new();
    private readonly System.Collections.Generic.Dictionary<Entity, float> _prevStrengthSoundAttempt = new();

    // QC EF_* render bits (dpextensions.qc) the strength/shield glow ORs into actor.effects. EF_ADDITIVE/
    // EF_FULLBRIGHT live in EntityMutatorState.EffectFlags; EF_BLUE/EF_RED are powerup-only here (the
    // client EF_BLUE/EF_RED -> dynamic-light path is in game/client/CsqcModelEffects.cs).
    private const int EfBlue = 64;   // EF_BLUE  (strength glow)
    private const int EfRed = 128;   // EF_RED   (shield glow)
    private const int StrengthGlow = EfBlue | EffectFlags.Additive | EffectFlags.FullBright;
    private const int ShieldGlow = EfRed | EffectFlags.Additive | EffectFlags.FullBright;

    // Per-player prior active-set, so PlayerPreThink can detect the apply/timeout EDGES the QC m_apply/m_remove
    // hooks fire on (the port has no per-effect tick). Keyed by entity; entries clear when the effect lapses.
    private readonly System.Collections.Generic.Dictionary<Entity, byte> _prevActive = new();

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.WeaponRateFactorArgs>? _onRate;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.WPlayStrengthSoundArgs>? _onStrengthSound;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _onUseKey;
    private HookHandler<MutatorHooks.BotForbidAttackArgs>? _onBotForbidAttack;
    private HookHandler<MutatorHooks.MonsterValidTargetArgs>? _onMonsterValidTarget;
    private HookHandler<MutatorHooks.ItemTouchedArgs>? _onItemTouched;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPhysics ??= OnPlayerPhysics;
        _onRate ??= OnWeaponRateFactor;
        _onPreThink ??= OnPlayerPreThink;
        _onPlayerDies ??= OnPlayerDies;
        _onStrengthSound ??= OnWPlayStrengthSound;
        _onUseKey ??= OnPlayerUseKey;
        _onBotForbidAttack ??= OnBotForbidAttack;
        _onMonsterValidTarget ??= OnMonsterValidTarget;
        _onItemTouched ??= OnItemTouched;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.WeaponRateFactor.Add(_onRate);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.WPlayStrengthSound.Add(_onStrengthSound);
        MutatorHooks.PlayerUseKey.Add(_onUseKey);
        MutatorHooks.BotForbidAttack.Add(_onBotForbidAttack);
        MutatorHooks.MonsterValidTarget.Add(_onMonsterValidTarget);
        MutatorHooks.ItemTouched.Add(_onItemTouched);

        if (Api.Services is not null)
        {
            R(ref _strengthDamage, "g_balance_powerup_strength_damage");
            R(ref _strengthForce, "g_balance_powerup_strength_force");
            R(ref _strengthSelfDamage, "g_balance_powerup_strength_selfdamage");
            R(ref _strengthSelfForce, "g_balance_powerup_strength_selfforce");
            R(ref _invincibleTakeDamage, "g_balance_powerup_invincible_takedamage");
            R(ref _invincibleTakeForce, "g_balance_powerup_invincible_takeforce");
            R(ref _speedHighspeed, "g_balance_powerup_speed_highspeed");
            R(ref _speedAttackRate, "g_balance_powerup_speed_attack_time_multiplier");
            R(ref _invisibilityAlpha, "g_balance_powerup_invisibility_alpha");
            R(ref _droppedLifetime, "g_items_dropped_lifetime");
            R(ref _strengthAntispamTime, "sv_strengthsound_antispam_time");
            R(ref _strengthAntispamRefire, "sv_strengthsound_antispam_refire_threshold");
            R(ref _strengthTime, "g_balance_powerup_strength_time");
            R(ref _invincibleTime, "g_balance_powerup_invincible_time");
            R(ref _speedTime, "g_balance_powerup_speed_time");
            R(ref _invisibilityTime, "g_balance_powerup_invisibility_time");
            // g_powerups_drop_ondeath defaults to 1; a 0 is meaningful (off) so read it directly.
            _dropOnDeath = (int)Api.Cvars.GetFloat("g_powerups_drop_ondeath");
            _dropOnUse = (int)Api.Cvars.GetFloat("g_powerups_drop"); // default 0 (off)
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onRate is not null) MutatorHooks.WeaponRateFactor.Remove(_onRate);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onStrengthSound is not null) MutatorHooks.WPlayStrengthSound.Remove(_onStrengthSound);
        if (_onUseKey is not null) MutatorHooks.PlayerUseKey.Remove(_onUseKey);
        if (_onBotForbidAttack is not null) MutatorHooks.BotForbidAttack.Remove(_onBotForbidAttack);
        if (_onMonsterValidTarget is not null) MutatorHooks.MonsterValidTarget.Remove(_onMonsterValidTarget);
        if (_onItemTouched is not null) MutatorHooks.ItemTouched.Remove(_onItemTouched);
        _prevActive.Clear();
        _prevStrengthSound.Clear();
        _prevStrengthSoundAttempt.Clear();
    }

    // ---- server-browser mutator strings (sv_powerups.qc:196 BuildMutatorsPrettyString / :212 BuildMutatorsString) ----
    // QC appends ", No powerups"/":no_powerups" when g_powerups==0 and ", Powerups"/":powerups" when g_powerups>0.
    public override string BuildMutatorsPrettyString(string s)
    {
        if (Api.Services is null) return s;
        float g = Api.Cvars.GetFloat("g_powerups");
        if (g == 0f) return s + ", No powerups";
        if (g > 0f) return s + ", Powerups";
        return s;
    }

    public override string BuildMutatorsString(string s)
    {
        if (Api.Services is null) return s;
        float g = Api.Cvars.GetFloat("g_powerups");
        if (g == 0f) return s + ":no_powerups";
        if (g > 0f) return s + ":powerups";
        return s;
    }

    // ---- death-log item codes (sv_powerups.qc:17 LogDeath_AppendItemCodes) ----
    // QC appends "S" when the player holds Strength and "I" when they hold Shield, into the player's
    // death-log item-code field (:items= / :victimitems=). Chained by MutatorActivation.LogDeathAppendItemCodes
    // and folded into the :kill: line by Scores.LogDeath (the port's LogDeath).
    public override string LogDeathAppendItemCodes(Entity player, string s)
    {
        if (Active(player, "strength")) s += "S";
        if (Active(player, "shield")) s += "I";
        // QC TODO comment: item codes for other powerups? (none yet)
        return s;
    }

    private static void R(ref float field, string cvar)
    {
        float v = Api.Cvars.GetFloat(cvar);
        if (v != 0f) field = v;
    }

    // =====================================================================================
    //  Damage_Calculate (sv_powerups.qc:30) — strength on attacker, shield on target
    // =====================================================================================
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;

        // strength: scale the attacker's outgoing damage + force (self-hit uses the gentler self-multipliers).
        if (attacker is not null && Active(attacker, "strength"))
        {
            if (ReferenceEquals(target, attacker))
            {
                args.Damage *= _strengthSelfDamage;
                args.Force *= _strengthSelfForce;
            }
            else
            {
                args.Damage *= _strengthDamage;
                args.Force *= _strengthForce;
            }
        }

        // shield (QC "invincible"): the target takes reduced damage; reduced incoming force unless self-hit.
        if (Active(target, "shield"))
        {
            args.Damage *= _invincibleTakeDamage;
            if (!ReferenceEquals(target, attacker))
                args.Force *= _invincibleTakeForce;
        }
        return false;
    }

    // =====================================================================================
    //  PlayerPhysics_UpdateStats (sv_powerups.qc:179) — speed ×highspeed via SpeedMultiplier
    // =====================================================================================
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;
        // QC: STAT(MOVEVARS_HIGHSPEED, player) *= g_balance_powerup_speed_highspeed. PlayerPhysics.Move resets
        // SpeedMultiplier to 1 before this hook and applies it (ApplyHighSpeed) after, so a pure multiply here
        // composes with the buffs/entrap factors.
        if (Active(player, "speed"))
            player.SpeedMultiplier *= _speedHighspeed;
        return false;
    }

    // =====================================================================================
    //  WeaponRateFactor (sv_powerups.qc:188) — speed ×attack_time_multiplier
    // =====================================================================================
    private bool OnWeaponRateFactor(ref MutatorHooks.WeaponRateFactorArgs args)
    {
        // QC: if Speed active, M_ARGV(0, float) *= g_balance_powerup_speed_attack_time_multiplier (< 1 => faster).
        if (Active(args.Player, "speed"))
            args.Factor *= _speedAttackRate;
        return false;
    }

    // =====================================================================================
    //  W_PlayStrengthSound (sv_powerups.qc:3) — anti-spammed SND_STRENGTH_FIRE on firing while Strength active
    // =====================================================================================
    private bool OnWPlayStrengthSound(ref MutatorHooks.WPlayStrengthSoundArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        if (!Active(player, "strength"))
        {
            // QC always stamps prevstrengthsoundattempt even when no Strength (it's set unconditionally at the
            // end of W_PlayStrengthSound), but it only matters while Strength is active, so a non-holder is a no-op.
            _prevStrengthSoundAttempt[player] = Api.Clock.Time;
            return false;
        }

        float now = Api.Clock.Time;
        _prevStrengthSound.TryGetValue(player, out float prevSound);
        _prevStrengthSoundAttempt.TryGetValue(player, out float prevAttempt);

        // QC: play if (time > prevstrengthsound + antispam_time) OR (time > prevstrengthsoundattempt + refire_threshold).
        if (now > prevSound + _strengthAntispamTime || now > prevAttempt + _strengthAntispamRefire)
        {
            // QC sound(player, CH_TRIGGER, SND_STRENGTH_FIRE, VOL_BASE, ATTEN_NORM). CH_TRIGGER == -3 == TriggerAuto.
            Api.Sound.Play(player, SoundChannel.TriggerAuto, "weapons/strength_fire.wav");
            _prevStrengthSound[player] = now;
        }
        _prevStrengthSoundAttempt[player] = now;
        return false;
    }

    // =====================================================================================
    //  PlayerUseKey (sv_powerups.qc:163) — +use drops the first active powerup and removes the effect (drop)
    // =====================================================================================
    private bool OnPlayerUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        // QC: if(MUTATOR_RETURNVALUE || game_stopped || !autocvar_g_powerups_drop) return;
        if (_dropOnUse == 0 || Api.Services is null || VehicleCommon.GameStopped) return false;

        Entity player = args.Player;
        bool freezeTimer = _dropOnUse == 2;

        // QC FOREACH(StatusEffects, instanceOfPowerupStatusEffect): drop the FIRST active powerup, remove it,
        // and return true (consume the +use press). Order matches the QC registration order.
        foreach (string name in PowerupNames)
        {
            var def = StatusEffectsCatalog.ByName(name);
            if (def is null || !StatusEffectsCatalog.Has(player, def)) continue;
            DropPowerup(player, name, freezeTimer);
            StatusEffectsCatalog.Remove(player, def);
            return true; // consume the press (QC return true)
        }
        return false;
    }

    // QC StatusEffects registration order for the four powerup status effects (FOREACH order).
    private static readonly string[] PowerupNames = { "strength", "shield", "speed", "invisibility" };

    // =====================================================================================
    //  Bot_ForbidAttack (sv_powerups.qc:204) — a bot may not attack a player holding Invisibility (bot stealth)
    // =====================================================================================
    private bool OnBotForbidAttack(ref MutatorHooks.BotForbidAttackArgs args)
        // QC: return StatusEffects_active(STATUSEFFECT_Invisibility, targ);
        => Active(args.Target, "invisibility");

    // =====================================================================================
    //  MonsterValidTarget (sv_powerups.qc:74) — a monster may not target a player holding Invisibility
    // =====================================================================================
    private bool OnMonsterValidTarget(ref MutatorHooks.MonsterValidTargetArgs args)
        // QC: return StatusEffects_active(STATUSEFFECT_Invisibility, targ); (true => target invalid).
        => Active(args.Target, "invisibility");

    // =====================================================================================
    //  ItemTouched (sv_powerups.qc:142) — kill the dropped powerup's countdown waypoint sprite on pickup
    // =====================================================================================
    private bool OnItemTouched(ref MutatorHooks.ItemTouchedArgs args)
    {
        // QC: if(e.waypointsprite_attached) WaypointSprite_Kill(e.waypointsprite_attached);
        if (args.Item.WaypointAttached is WaypointSprite wp)
        {
            WaypointSprites.Kill(wp);
            args.Item.WaypointAttached = null;
        }
        return false;
    }

    // Bit per powerup in the prior-active bitmap (apply/timeout edge detection).
    private const byte AStrength = 1;
    private const byte AShield = 2;
    private const byte ASpeed = 4;
    private const byte AInvis = 8;

    // =====================================================================================
    //  Per-frame powerup tick (the QC per-effect m_tick / m_apply / m_remove, which the port has no per-effect
    //  tick hook for, run here under PlayerPreThink): invisibility alpha, strength/shield glow bits, the
    //  pickup/powerdown notifications (on the active EDGE), and the last-seconds countdown beep.
    // =====================================================================================
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0) return false;

        bool strength = Active(player, "strength");
        bool shield = Active(player, "shield");
        bool speed = Active(player, "speed");
        bool invis = Active(player, "invisibility");

        // ---- invisibility alpha (InvisibilityStatusEffect.m_tick / m_remove, invisibility.qc:33-43) ----
        // QC sets actor.alpha = invisibility_alpha each frame while active and m_remove restores
        // default_player_alpha. Mirrors the BuffsMutator invisible-buff alpha handling: set while held,
        // restore to 1 on lapse. QC guards BOTH the apply (m_tick:36) AND the restore (m_remove:14) with
        // if(!actor.vehicle) — a player riding a vehicle keeps the vehicle's alpha untouched. The exterior
        // weapon alpha rides the same networked Entity.Alpha (the held-weapon node fades with the body in
        // ViewEntityRenderer), so setting player.Alpha here covers the weapon too.
        if (player.Vehicle is null)
        {
            if (invis)
                player.Alpha = _invisibilityAlpha;
            else if (player.Alpha == _invisibilityAlpha)
                player.Alpha = MutatorHooks.DefaultPlayerAlpha; // QC default_player_alpha (composes with Cloaked → 0.25, not 1)
        }

        // ---- strength/shield glow (strength.qc:31 / shield.qc:31 m_tick + m_remove:14) ----
        // QC m_tick ORs (EF_BLUE/EF_RED | EF_ADDITIVE | EF_FULLBRIGHT) into actor.effects every frame while
        // active; m_remove clears the same bits. Idempotent, so set/clear here without edge tracking.
        if (strength) player.Effects |= StrengthGlow; else player.Effects &= ~StrengthGlow;
        if (shield) player.Effects |= ShieldGlow; else player.Effects &= ~ShieldGlow;

        // ---- pickup/powerdown notifications + countdown beep (m_apply / m_remove(TIMEOUT) / m_tick) ----
        byte now = (byte)((strength ? AStrength : 0) | (shield ? AShield : 0)
                        | (speed ? ASpeed : 0) | (invis ? AInvis : 0));
        _prevActive.TryGetValue(player, out byte prev);
        if (now != prev)
        {
            // QC !g_cts gate on the INFO broadcast (strength.qc:22). g_cts is the CTS gametype flag.
            bool notCts = !(Api.Services is not null && Api.Cvars.GetFloat("g_cts") != 0f);
            NotifyEdge(player, strength, (prev & AStrength) != 0, "STRENGTH", notCts);
            NotifyEdge(player, shield, (prev & AShield) != 0, "SHIELD", notCts);
            NotifyEdge(player, speed, (prev & ASpeed) != 0, "SPEED", notCts);
            NotifyEdge(player, invis, (prev & AInvis) != 0, "INVISIBILITY", notCts);
            if (now == 0) _prevActive.Remove(player);
            else _prevActive[player] = now;
        }

        // QC every powerup m_tick: play_countdown(actor, StatusEffects_gettime(this, actor), SND_POWEROFF).
        if (strength) PlayCountdown(player, "strength");
        if (shield) PlayCountdown(player, "shield");
        if (speed) PlayCountdown(player, "speed");
        if (invis) PlayCountdown(player, "invisibility");

        return false;
    }

    // QC m_apply / m_remove(STATUSEFFECT_REMOVE_TIMEOUT): broadcast the pickup + center-print on the rising
    // edge, center-print the powerdown on the falling edge (the INFO_POWERDOWN broadcast is commented out in
    // Base, so only the CENTER fires on removal). Names map to the registered POWERUP_/POWERDOWN_ notifs.
    private static void NotifyEdge(Entity player, bool active, bool wasActive, string name, bool notCts)
    {
        if (active && !wasActive)
        {
            if (notCts)
                NotificationSystem.Info("POWERUP_" + name, player.NetName);
            NotificationSystem.Center(player, "POWERUP_" + name);
        }
        else if (!active && wasActive)
        {
            NotificationSystem.Center(player, "POWERDOWN_" + name);
        }
    }

    // QC play_countdown(this, finished, samp) (server/client.qc:1532): beep once per integer second crossed in
    // the last 6 seconds. floor(time_left - frametime) != floor(time_left) is the "crossed a second" test.
    private static void PlayCountdown(Entity player, string name)
    {
        if (Api.Services is null) return;
        var def = StatusEffectsCatalog.ByName(name);
        if (def is null) return;
        float finished = StatusEffectExpire(player, def);
        if (finished <= 0f) return; // permanent / not active
        float now = Api.Clock.Time;
        float frametime = Api.Clock.FrameTime;
        float timeLeft = finished - now;
        if (timeLeft < 6f
            && System.MathF.Floor(timeLeft - frametime) != System.MathF.Floor(timeLeft))
        {
            // CH_INFO, VOL_BASE, ATTEN_NORM → SoundChannel.Auto, default vol/atten.
            Api.Sound.Play(player, SoundChannel.Auto, "misc/poweroff.wav");
        }
    }

    // =====================================================================================
    //  PlayerDies (sv_powerups.qc:149) — drop each active powerup as a pickup-able item (drop_ondeath)
    // =====================================================================================
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        // QC: if(!autocvar_g_powerups_drop_ondeath) return; (default 1 = on, timer continues; 2 = freeze).
        if (_dropOnDeath == 0 || Api.Services is null) return false;

        Entity target = args.Target;
        bool freezeTimer = _dropOnDeath == 2;

        // QC FOREACH(StatusEffects, it.instanceOfPowerupStatusEffect, …): drop the four powerups it holds.
        DropPowerup(target, "strength", freezeTimer);
        DropPowerup(target, "shield", freezeTimer);
        DropPowerup(target, "speed", freezeTimer);
        DropPowerup(target, "invisibility", freezeTimer);
        return false;
    }

    // Port of powerups_DropItem (sv_powerups.qc:93): spawn a pickup-able item carrying the powerup's REMAINING
    // (freeze, mode 2) or ABSOLUTE (continue, mode 1) timer. ItemTouch re-applies it on pickup — for an
    // expiring (continue) drop it stores the absolute finish time and ItemTouch subtracts `time`; for a frozen
    // drop expiring is off and the stored value is the remaining seconds (max(existing,dur) on pickup).
    // Includes the WP_Item countdown waypoint sprite (powerups_DropItem + powerups_DropItem_Think): a marker
    // following the dropped item with a time-left health bar, updated each think and killed on pickup/despawn.
    private void DropPowerup(Entity owner, string name, bool freezeTimer)
    {
        var def = StatusEffectsCatalog.ByName(name);
        if (def is null) return;
        float finishedAbs = StatusEffectExpire(owner, def);
        if (finishedAbs <= 0f) return; // not active / permanent

        float now = Api.Clock.Time;
        float timeLeft = finishedAbs - now;
        if (timeLeft <= 1f) return; // QC: if(timeleft <= 1) return;

        // QC: finished = freezeTimer ? timeleft : t (remaining vs absolute end time).
        float finished = freezeTimer ? timeLeft : finishedAbs;
        // QC: e.lifetime = freezeTimer ? g_items_dropped_lifetime : timeleft.
        float lifetime = freezeTimer ? _droppedLifetime : timeLeft;

        Entity e = Api.Entities.Spawn();
        switch (name)
        {
            case "strength":     e.ClassName = "item_strength";     e.NetName = "strength";   e.StrengthFinished = finished;     break;
            case "shield":       e.ClassName = "item_invincible";   e.NetName = "invincible"; e.InvincibleFinished = finished;   break;
            case "speed":        e.ClassName = "item_speed";        e.NetName = "speed";      e.SpeedFinished = finished;        break;
            case "invisibility": e.ClassName = "item_invisibility"; e.NetName = "invisibility"; e.InvisibilityFinished = finished; break;
            default: Api.Entities.Remove(e); return;
        }

        Vector3 org = owner.Origin;
        Api.Entities.SetOrigin(e, org);
        e.Origin = org;
        // QC W_CalculateProjectileVelocity(this, velocity, v_forward * 750, false) — toss forward off the corpse.
        e.Velocity = owner.Velocity + QMath.Forward(owner.Angles) * 750f;
        e.MoveType = MoveType.Toss;
        e.Solid = Solid.Trigger;
        e.Flags |= EntFlags.Item;
        e.ItemIsLoot = true;

        // QC: if(!freezeTimer) ITEM_SET_EXPIRING(e, true). The continue-timer drop expires itself; ItemTouch
        // converts the stored ABSOLUTE finish to remaining by subtracting `time` on pickup.
        if (!freezeTimer)
            e.ItemIsExpiring = true;

        // The dropped item is picked up through the canonical Item_Touch path.
        e.Touch = (self, other) => ItemPickupRules.ItemTouch(self, other);

        // QC: SetResourceExplicit(e, RES_ARMOR, 1) on a frozen drop is the "timer frozen" marker the waypoint
        // think reads (!GetResource(RES_ARMOR) == timer running). The port has no RES_ARMOR on a loose item, so
        // reuse the freezeTimer flag captured in the think closure below (logically equivalent).

        // QC: spawn a WP_Item waypoint counting down the powerup's remaining time, following the dropped item at
        // its top (offset '0 0 1' * maxs.z). maxtime scales the health bar; timeleft fills it. The port renders
        // the generic "Item" def (magenta) — it has no wp_extra-driven per-powerup icon, a minor cosmetic gap.
        float maxtime = name switch
        {
            "strength" => _strengthTime,
            "shield" => _invincibleTime,
            "speed" => _speedTime,
            "invisibility" => _invisibilityTime,
            _ => 30f,
        };
        WaypointDef wpDef = WaypointRegistry.Get("Item");
        WaypointSprite wp = WaypointSprites.Spawn("Item", 0f, 0f, e, new Vector3(0f, 0f, e.Maxs.Z),
            default, 0, wpDef.Color, wpDef.RadarIcon, SpriteRule.Default, hideable: true);
        WaypointSprites.UpdateMaxHealth(wp, maxtime);
        // The port's WaypointSprite.Health is pre-normalized (0..1); QC passes raw timeleft + a maxhealth scale.
        WaypointSprites.UpdateHealth(wp, maxtime > 0f ? timeLeft / maxtime : -1f);
        e.WaypointAttached = wp;

        // QC e.wait = time + lifetime drives both the dropped-item despawn and powerups_DropItem_Think's countdown.
        float deadline = now + lifetime;
        e.ItemWait = deadline;
        // QC Item_Think → powerups_DropItem_Think: while the waypoint is attached AND the timer is RUNNING (not a
        // frozen drop), update the health bar to the floored seconds-left, killing it at 0; then expire the loot
        // at its lifetime. A frozen drop keeps a static bar (QC skips the update when RES_ARMOR==1).
        e.Think = self =>
        {
            float t = Api.Clock.Time;
            if (self.WaypointAttached is WaypointSprite spr)
            {
                if (!freezeTimer)
                {
                    float left = System.MathF.Floor(self.ItemWait - t);
                    if (left > 0f)
                        WaypointSprites.UpdateHealth(spr, maxtime > 0f ? left / maxtime : -1f);
                    else
                    {
                        WaypointSprites.Kill(spr);
                        self.WaypointAttached = null;
                    }
                }
            }
            if (t >= self.ItemWait)
            {
                if (self.WaypointAttached is WaypointSprite dspr)
                {
                    WaypointSprites.Kill(dspr);
                    self.WaypointAttached = null;
                }
                Api.Entities.Remove(self);
                return;
            }
            // Re-arm the think for the next countdown tick (~once per second is enough for the bar).
            self.NextThink = t + 0.5f;
        };
        e.NextThink = now + 0.5f;
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    // QC StatusEffects_active(STATUSEFFECT_X, e) -> StatusEffectsCatalog.Has(e, ByName("x")).
    private static bool Active(Entity? e, string name)
    {
        if (e is null) return false;
        var def = StatusEffectsCatalog.ByName(name);
        return def is not null && StatusEffectsCatalog.Has(e, def);
    }

    // QC StatusEffects_gettime(def, e): the ABSOLUTE expire time, 0 when not active/permanent.
    private static float StatusEffectExpire(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId) return s.ExpireTime;
        return 0f;
    }
}
