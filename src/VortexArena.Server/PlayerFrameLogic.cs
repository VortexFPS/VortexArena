using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The per-player per-frame bookkeeping the QC server core runs outside of movement — the Godot-free port of
/// <c>player_regen</c> + <c>RotRegen</c>/<c>CalcRegen</c>/<c>CalcRot</c> (health/armor/fuel regen and rot),
/// <c>DrownPlayer</c> (the air timer + drown damage), and the player slice of <c>CreatureFrame_All</c>
/// (<c>CreatureFrame_Liquids</c>/<c>_hotliquids</c> contents damage + <c>CreatureFrame_FallDamage</c>) from
/// server/client.qc and server/main.qc.
///
/// These run once per server frame for each player from <see cref="GameWorld"/>'s PostThink/EndFrame, firing
/// the <see cref="MutatorHooks.PlayerRegen"/> hook (instagib disables regen) exactly where QC does. All the
/// balance values come from the cvar facade via <see cref="Cvars"/> (defaults seeded by
/// <see cref="Cvars.RegisterDefaults"/>), so server config tuning is honored.
/// </summary>
public static class PlayerFrameLogic
{
    // ---- QC content type constants (legacy CONTENT_*, what watertype carries) ----
    private const int ContentWater = (int)Contents.Water;
    private const int ContentSlime = (int)Contents.Slime;
    private const int ContentLava = (int)Contents.Lava;

    // ---- QC WATERLEVEL_* ----
    private const int WaterLevelNone = 0;
    private const int WaterLevelWetFeet = 1;
    private const int WaterLevelSwimming = 2;
    private const int WaterLevelSubmerged = 3;

    /// <summary>
    /// QC <c>player_regen</c>: regenerate/rot the player's health, armor, and fuel toward their stable
    /// values, gated by the damage-pause timers, and kill a player who rotted below 1 HP. Fires
    /// <see cref="MutatorHooks.PlayerRegen"/> first (a handler returning true — e.g. instagib — disables the
    /// health/armor regen, matching QC's <c>mutator_returnvalue</c> short-circuit). Call once per server
    /// frame per live player.
    /// </summary>
    public static void Regen(Player p, ServerPlayerState st, float frameTime)
    {
        // QC: MUTATOR_CALLHOOK(PlayerRegen, this, max_mod, regen_mod, rot_mod, limit_mod, regen_health,
        // regen_health_linear, regen_health_rot, regen_health_rotlinear, regen_health_stable,
        // regen_health_rotstable) — all 10 in/out tuning slots are seeded from cvars and passed through the
        // hook so mutators (handicap, buffs, etc.) may rewrite them. A true return disables health+armor regen.
        var regenArgs = new MutatorHooks.PlayerRegenArgs(
            player:               p,
            regenHealth:          Cvars.Float("g_balance_health_regen"),
            regenHealthLinear:    Cvars.Float("g_balance_health_regenlinear"),
            regenHealthRot:       Cvars.Float("g_balance_health_rot"),
            regenHealthRotLinear: Cvars.Float("g_balance_health_rotlinear"),
            regenHealthStable:    Cvars.Float("g_balance_health_regenstable"),
            regenHealthRotStable: Cvars.Float("g_balance_health_rotstable"));
        bool regenDisabled = MutatorHooks.PlayerRegen.Call(ref regenArgs);

        // Read back the (possibly mutator-rewritten) tuning mods + health values.
        float maxMod    = regenArgs.MaxMod;
        float regenMod  = regenArgs.RegenMod;
        float rotMod    = regenArgs.RotMod;
        float limitMod  = regenArgs.LimitMod;

        float now = Now;

        if (!regenDisabled)
        {
            // The regen/rot pause timers live on the Entity (DamageEntityState) so the damage path (which sets
            // PauseRegenFinished on a hit) and the pickup path (GiveResource sets PauseRot*Finished) and this
            // tick all share ONE storage. They were previously split across a separate ServerPlayerState copy
            // that the damage/pickup paths never wrote — so getting hit never paused regen and over-stacked
            // health/armor rotted with no grace (REGEN1/REGEN2).
            // ----- armor (QC RES_ARMOR; no max_mod applied to armor, per QC comment client.qc:1724) -----
            float armorRegenStable = Cvars.Float("g_balance_armor_regenstable");
            float armorRotStable = Cvars.Float("g_balance_armor_rotstable");
            float armorRegenFt = now > p.PauseRegenFinished ? regenMod * frameTime : 0f;
            float armorRotFt = now > p.PauseRotArmorFinished ? rotMod * frameTime : 0f;
            RotRegen(p, ResourceType.Armor, limitMod,
                armorRegenStable, Cvars.Float("g_balance_armor_regen"), Cvars.Float("g_balance_armor_regenlinear"), armorRegenFt,
                armorRotStable, Cvars.Float("g_balance_armor_rot"), Cvars.Float("g_balance_armor_rotlinear"), armorRotFt);

            // ----- health (QC RES_HEALTH; max_mod scales stable set-points, per client.qc:1725-1726) -----
            float healthRegenStable = regenArgs.RegenHealthStable * maxMod;
            float healthRotStable   = regenArgs.RegenHealthRotStable * maxMod;
            float healthRegenFt = now > p.PauseRegenFinished ? regenMod * frameTime : 0f;
            float healthRotFt = now > p.PauseRotHealthFinished ? rotMod * frameTime : 0f;
            RotRegen(p, ResourceType.Health, limitMod,
                healthRegenStable, regenArgs.RegenHealth, regenArgs.RegenHealthLinear, healthRegenFt,
                healthRotStable, regenArgs.RegenHealthRot, regenArgs.RegenHealthRotLinear, healthRotFt);
        }

        // QC: "if player rotted to death... die!" — checked even when regen was disabled.
        // (DEATH_ROT is a special deathtype; the pipeline carries it as a free-form tag.)
        if (p.GetResource(ResourceType.Health) < 1f && !p.IsDead)
        {
            // QC client.qc:1738 ejects an occupied vehicle (vehicles_exit, VHEF_RELEASE) BEFORE the DEATH_ROT
            // damage so the rotting player dies as a free pawn, not inside the vehicle.
            if (p.Vehicle is not null)
                VehicleBoarding.Exit(p);
            Combat.Damage(p, null, null, 1f, "rot", p.Origin, Vector3.Zero);
        }

        // ----- fuel (QC RES_FUEL) -----
        // QC client.qc:1744-1753: the ENTIRE fuel block (regen AND rot) is skipped under IT_UNLIMITED_AMMO. Fuel
        // REGEN frametime is gated on BOTH (time > pauseregen_finished) — fuel shares the health/armor regen
        // counter, which the jetpack/hook write — AND ownership of ITEM_FuelRegen (the jetpack/fuel-regen pickup):
        // a player who never picked up fuel regen does not regenerate fuel. Fuel ROT keeps its own pause timer.
        if ((p.Items & (int)ItemFlag.UnlimitedAmmo) == 0)
        {
            bool ownsFuelRegen = (p.Items & (int)ItemFlag.FuelRegen) != 0;
            float fuelRegenStable = Cvars.Float("g_balance_fuel_regenstable");
            float fuelRotStable = Cvars.Float("g_balance_fuel_rotstable");
            // QC: fuel regen/rot frametime uses raw frametime (no regen_mod/rot_mod), and limit_mod = 1 (QC
            // client.qc:1750: `RotRegen(this, RES_FUEL, 1, ...)` — the four mods don't apply to fuel).
            float fuelRegenFt = (now > p.PauseRegenFinished && ownsFuelRegen) ? frameTime : 0f;
            float fuelRotFt = now > p.PauseRotFuelFinished ? frameTime : 0f;
            RotRegen(p, ResourceType.Fuel, 1f,
                fuelRegenStable, Cvars.Float("g_balance_fuel_regen"), Cvars.Float("g_balance_fuel_regenlinear"), fuelRegenFt,
                fuelRotStable, Cvars.Float("g_balance_fuel_rot"), Cvars.Float("g_balance_fuel_rotlinear"), fuelRotFt);
        }

        // QC client.qc:2491: this.dmg_team = max(0, this.dmg_team - g_teamdamage_resetspeed * frametime).
        // Bleed off accumulated team damage so repeated single hits don't permanently lock in mirror punishment.
        float resetSpeed = Cvars.FloatOr("g_teamdamage_resetspeed", 20f);
        if (p.DmgTeam > 0f)
            p.DmgTeam = MathF.Max(0f, p.DmgTeam - resetSpeed * frameTime);

        // QC client.qc:2745-2753: deferred teamkill complaint voice (CS(attacker).teamkill_soundtime / teamkill_soundsource).
        // After teamplay_mode 4 friendly-fire, the offender's victim plays the "teamshoot" voice 0.4s later.
        // The attacker's entity tracks the timer + the victim entity; here we check it and fire the sound.
        if (p.TeamKillSoundTime != 0f && now > p.TeamKillSoundTime)
        {
            p.TeamKillSoundTime = 0f;
            Entity? victim = p.TeamKillSoundSource;
            p.TeamKillSoundSource = null;
            if (victim is not null && !victim.IsFreed)
            {
                // QC: PlayerSound(e, playersound_teamshoot, CH_VOICE, VOL_BASEVOICE, VOICETYPE_LASTATTACKER_ONLY, 1)
                // where e = the victim entity. pusher is temporarily the attacker so the sound uses the attacker's
                // voice (VOICETYPE_LASTATTACKER_ONLY reads pusher). Port: play "teamshoot" voice on the victim;
                // the model sound dir is resolved from the victim's model+skin (same as DamageSystem.ModelSoundDir).
                Entity? oldPusher = victim.Pusher;
                victim.Pusher = p;
                string? soundDir = Sounds.ModelSoundsFile(victim.Model, (int)victim.Skin);
                SoundSystem.PlayPlayerSound(victim, "teamshoot", soundDir,
                    SoundLevels.VolBaseVoice,
                    SoundLevels.AttenNorm);
                victim.Pusher = oldPusher;
            }
        }
    }

    /// <summary>
    /// QC <c>RotRegen</c>: move a resource toward its rot-stable value when above it (rotting) or toward its
    /// regen-stable value when below it (regenerating), then clamp to the resource limit scaled by
    /// <paramref name="limitMod"/> (QC client.qc:1680). Faithful port including the snap-when-close behavior
    /// of <see cref="CalcRegen"/>/<see cref="CalcRot"/>. <paramref name="limitMod"/> = 1 for stock play and
    /// the fuel block (which always passes 1); mutators that scale the resource cap write it via PlayerRegenArgs.
    /// </summary>
    public static void RotRegen(Player p, ResourceType res, float limitMod,
        float regenStable, float regenFactor, float regenLinear, float regenFrameTime,
        float rotStable, float rotFactor, float rotLinear, float rotFrameTime)
    {
        float old = p.GetResource(res);
        float current = old;

        if (current > rotStable)
        {
            if (rotFrameTime > 0f)
            {
                current = CalcRot(current, rotStable, rotFactor, rotFrameTime);
                current = System.Math.Max(rotStable, current - rotLinear * rotFrameTime);
            }
        }
        else if (current < regenStable)
        {
            if (regenFrameTime > 0f)
            {
                current = CalcRegen(current, regenStable, regenFactor, regenFrameTime);
                current = System.Math.Min(regenStable, current + regenLinear * regenFrameTime);
            }
        }

        // QC client.qc:1680: `float limit = GetResourceLimit(this, res) * limit_mod;` — the mutator may scale
        // the effective ceiling (e.g. a handicap mutator that raises max HP also shifts the regen ceiling).
        float limit = Resources.GetResourceLimit(p, res) * limitMod;
        if (limit != Resources.LimitNone && current > limit)
            current = limit;

        if (current != old)
            p.SetResource(res, current);
    }

    /// <summary>QC <c>CalcRegen</c>: exponential approach toward <paramref name="stable"/> from below, snapping when near.</summary>
    public static float CalcRegen(float current, float stable, float regenFactor, float regenFrameTime)
    {
        if (current > stable)
            return current;
        if (current > stable - 0.25f) // snap when close enough
            return stable;
        return System.Math.Min(stable, current + (stable - current) * regenFactor * regenFrameTime);
    }

    /// <summary>QC <c>CalcRot</c>: exponential approach toward <paramref name="stable"/> from above, snapping when near.</summary>
    public static float CalcRot(float current, float stable, float rotFactor, float rotFrameTime)
    {
        if (current < stable)
            return current;
        if (current < stable + 0.25f) // snap when close enough
            return stable;
        return System.Math.Max(stable, current + (stable - current) * rotFactor * rotFrameTime);
    }

    /// <summary>
    /// QC <c>DrownPlayer</c>: maintain the air timer while submerged and deal drown damage when it runs out.
    /// A player who isn't submerged (or is dead/frozen/out of water) has air reset. Call once per server
    /// frame per player. <paramref name="gameStopped"/> mirrors the QC <c>game_stopped || time &lt; game_starttime</c>
    /// gate (no drowning before the match starts / once it ends).
    /// </summary>
    public static void DrownPlayer(Player p, ServerPlayerState st, bool gameStopped)
    {
        bool frozen = StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(p, f);
        if (p.IsDead || gameStopped || frozen || p.WaterType != ContentWater)
        {
            st.AirFinished = 0f;
            return;
        }

        if (p.WaterLevel != WaterLevelSubmerged)
        {
            // QC client.qc:2777-2779: surfaced — if the air timer had run out (we were drowning), play the
            // gasp PlayerSound, then reset the timer. PlayerSound(this, playersound_gasp, CH_PLAYER, VOL_BASE,
            // VOICETYPE_PLAYERSOUND, 1). The model's sound dir is resolved the same way the teamkill/pain
            // voices resolve it (Sounds.ModelSoundsFile(model, skin)).
            float nowGasp = Now;
            if (st.AirFinished != 0f && st.AirFinished < nowGasp)
            {
                string? soundDir = Sounds.ModelSoundsFile(p.Model, (int)p.Skin);
                SoundSystem.PlayPlayerSound(p, "gasp", soundDir, SoundLevels.VolBase, SoundLevels.AttenNorm);
            }
            st.AirFinished = 0f;
        }
        else
        {
            float now = Now;
            if (st.AirFinished == 0f)
                st.AirFinished = now + Cvars.FloatOr("g_balance_contents_drowndelay", 10f);
            if (st.AirFinished < now)
            {
                // drown! (QC: pain_finished gates the 2 Hz drown damage)
                if (st.PainFinished < now)
                {
                    float dmg = Cvars.Float("g_balance_contents_playerdamage_drowning")
                                * Cvars.FloatOr("g_balance_contents_damagerate", 0.2f);
                    Combat.Damage(p, null, null, dmg, DeathTypes.Drown, p.Origin, Vector3.Zero);
                    st.PainFinished = now + 0.5f;
                }
            }
        }
    }

    // QC pressedkeys.qh KEY_* bits — the network PRESSED_KEYS stat layout (must match the HUD PressedKeysPanel).
    private const int KeyForward  = 1 << 0;
    private const int KeyBackward = 1 << 1;
    private const int KeyLeft     = 1 << 2;
    private const int KeyRight    = 1 << 3;
    private const int KeyJump     = 1 << 4;
    private const int KeyCrouch   = 1 << 5;
    private const int KeyAtck     = 1 << 6;
    private const int KeyAtck2    = 1 << 7;

    /// <summary>
    /// QC <c>GetPressedKeys</c> (server/client.qc:1767): compute the player's held-button bitset from this tick's
    /// move command and store it in the networked <c>PRESSED_KEYS</c> stat (<see cref="Player.PressedKeys"/>) so
    /// the pressed-keys / strafe HUD can show a SPECTATED player's keys. BITSETs FORWARD/BACK/RIGHT/LEFT from the wish-move,
    /// JUMP/ATCK/ATCK2 from the buttons, and CROUCH from <see cref="Entity.IsDucked"/> (QC's workaround: the
    /// player can't un-crouch until their path is clear, so the held-crouch bit tracks the ducked state, not the
    /// raw button). When the game is stopped the stat is zeroed. Call once per server frame per live player from
    /// PlayerPostThink. The QC observer-clears-PRESSED_KEYS rule is handled by <see cref="ClearPressedKeys"/>.
    /// </summary>
    public static void GetPressedKeys(Player p, IMovementInput input, bool gameStopped)
    {
        // QC: MUTATOR_CALLHOOK(GetPressedKeys, this) — in the port the race/cts speed-award + the Vortex/OkNex
        // velocity-charge consumers of that hook are already driven from their own gametype/weapon per-frame
        // ticks, so there is no separate GetPressedKeys hook chain to fire here (no port consumer is wired to it).

        if (gameStopped)
        {
            p.PressedKeys = 0;
            return;
        }

        int keys = p.PressedKeys;
        // QC reads CS(this).movement.x/.y (the wish-move); the port's IMovementInput.MoveValues carries the same
        // forward(X)/side(Y) wish in the move-speed scale.
        float fwd = input.MoveValues.X;
        float side = input.MoveValues.Y;
        keys = BitSet(keys, KeyForward,  fwd > 0f);
        keys = BitSet(keys, KeyBackward, fwd < 0f);
        keys = BitSet(keys, KeyRight,    side > 0f);
        keys = BitSet(keys, KeyLeft,     side < 0f);
        keys = BitSet(keys, KeyJump,     input.ButtonJump);
        // QC workaround: the player can't un-crouch until their path is clear, so the held-crouch bit tracks the
        // IS_DUCKED state rather than the raw button.
        keys = BitSet(keys, KeyCrouch,   p.IsDucked);
        keys = BitSet(keys, KeyAtck,     input.ButtonAttack1);
        keys = BitSet(keys, KeyAtck2,    input.ButtonAttack2);
        p.PressedKeys = keys;
    }

    /// <summary>
    /// QC <c>PlayerPostThink</c> observer branch (server/client.qc:2861): an observing/spectating client clears
    /// its own PRESSED_KEYS stat (it has no movement of its own — when following a player the spectate-copy
    /// inherits the spectatee's bits instead). Call for a connected observer each frame.
    /// </summary>
    public static void ClearPressedKeys(Player p) => p.PressedKeys = 0;

    private static int BitSet(int bits, int mask, bool on) => on ? (bits | mask) : (bits & ~mask);

    /// <summary>
    /// QC <c>CreatureFrame_Liquids</c> + <c>CreatureFrame_hotliquids</c>: deal periodic lava/slime damage to a
    /// player standing in a hot liquid (rate-limited by <see cref="ServerPlayerState.ContentsDamageTime"/>),
    /// and track the in-water flag. Water itself does no contents damage (that's drowning, handled separately).
    /// </summary>
    public static void ContentsDamage(Player p, ServerPlayerState st)
    {
        // QC FL_INWATER bookkeeping: any contents <= WATER with waterlevel > 0 counts as "in water".
        bool inLiquid = p.WaterType <= ContentWater && p.WaterLevel > 0;
        if (inLiquid)
        {
            if (!st.InWater)
            {
                st.InWater = true;
                st.ContentsDamageTime = 0f;
            }
            HotLiquids(p, st);
        }
        else
        {
            if (st.InWater)
            {
                st.InWater = false;
                st.ContentsDamageTime = 0f;
            }
        }
    }

    private static void HotLiquids(Player p, ServerPlayerState st)
    {
        float now = Now;
        if (st.ContentsDamageTime >= now)
            return;
        float rate = Cvars.FloatOr("g_balance_contents_damagerate", 0.2f);
        st.ContentsDamageTime = now + rate;

        bool frozen = StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(p, f);
        if (frozen)
        {
            // QC: frozen players still die in lava/slime (instant huge damage).
            if (p.WaterType == ContentLava)
                Combat.Damage(p, null, null, 10000f, DeathTypes.Lava, p.Origin, Vector3.Zero);
            else if (p.WaterType == ContentSlime)
                Combat.Damage(p, null, null, 10000f, DeathTypes.Slime, p.Origin, Vector3.Zero);
            return;
        }

        if (p.WaterType == ContentLava)
        {
            float dmg = Cvars.Float("g_balance_contents_playerdamage_lava") * rate * p.WaterLevel;
            Combat.Damage(p, null, null, dmg, DeathTypes.Lava, p.Origin, Vector3.Zero);
            // QC main.qc:104-105: if(g_balance_contents_playerdamage_lava_burn) Fire_AddDamage(...):
            // each lava tick re-ignites the player with lava_burn total damage over lava_burn_time seconds
            // (both scaled by waterlevel). Default 0 means disabled; non-zero enables burn-over-time after
            // leaving lava. The LEMMA merge in FireAddDamage handles repeated ignition per tick correctly.
            float lavaBurn = Cvars.FloatOr("g_balance_contents_playerdamage_lava_burn", 0f);
            if (lavaBurn != 0f)
            {
                float burnTime = Cvars.FloatOr("g_balance_contents_playerdamage_lava_burn_time", 2.5f) * p.WaterLevel;
                StatusEffectsCatalog.FireAddDamage(p, null, lavaBurn * p.WaterLevel, burnTime, DeathTypes.Lava);
            }
        }
        else if (p.WaterType == ContentSlime)
        {
            float dmg = Cvars.Float("g_balance_contents_playerdamage_slime") * rate * p.WaterLevel;
            Combat.Damage(p, null, null, dmg, DeathTypes.Slime, p.Origin, Vector3.Zero);
        }
    }

    /// <summary>
    /// QC <c>CreatureFrame_hotliquids</c> PROJECTILE branch (server/main.qc:78-84): a projectile resting in a
    /// hot liquid takes content damage. Mirrors the <c>FL_PROJECTILE</c> arm of the player path above, but uses
    /// <c>g_balance_contents_projectiledamage</c> (not the player lava/slime cvars) and applies the SAME value
    /// for both lava and slime. Rate-limited by the entity's <see cref="Entity.ContentsDamageTime"/>.
    ///
    /// <para>The damage routes through <see cref="Combat.Damage"/> with a NULL inflictor (DEATH_LAVA/DEATH_SLIME),
    /// exactly like Base's <c>Damage(this, NULL, NULL, ...)</c>: the projectile's own <c>event_damage</c> (its
    /// installed <see cref="Entity.GtEventDamage"/> shoot-down shim) subtracts HP and detonates it at &lt;= 0.
    /// Because the inflictor is NULL (not an <c>electro_orb_chain</c>/<c>electro_bolt</c>), Base's
    /// <c>W_Electro_Orb_Damage</c> takes the NON-combo branch and bursts the orb as a plain SECONDARY blast. The
    /// orb's shim distinguishes that case off the null inflictor (see Electro.AttackOrb's ProjectileDamage), so a
    /// content death detonates as a secondary blast, not a combo.</para>
    /// Call once per server frame per damaged-by-contents projectile (the QC <c>g_damagedbycontents</c> sweep).
    /// </summary>
    public static void ProjectileContentsDamage(Entity proj)
    {
        // QC CreatureFrame_Liquids gate: contents <= WATER with waterlevel > 0 == in a liquid (water itself does
        // no projectile damage; only the lava/slime branches below deal any).
        if (!(proj.WaterType <= ContentWater && proj.WaterLevel > 0))
            return;

        float now = Now;
        if (proj.ContentsDamageTime >= now)
            return;
        float rate = Cvars.FloatOr("g_balance_contents_damagerate", 0.2f);
        proj.ContentsDamageTime = now + rate;

        // QC main.qc:80-83: projectile content damage = projectiledamage * damagerate * waterlevel, same for both.
        float dmg = Cvars.Float("g_balance_contents_projectiledamage") * rate * proj.WaterLevel;
        if (proj.WaterType == ContentLava)
            Combat.Damage(proj, null, null, dmg, DeathTypes.Lava, proj.Origin, Vector3.Zero);
        else if (proj.WaterType == ContentSlime)
            Combat.Damage(proj, null, null, dmg, DeathTypes.Slime, proj.Origin, Vector3.Zero);
    }

    /// <summary>
    /// QC <c>CreatureFrame_FallDamage</c>: deal damage when the player's speed dropped sharply this frame
    /// (a hard landing). Uses the velocity delta between <see cref="ServerPlayerState.OldVelocity"/> and the
    /// current velocity (QC captures oldvelocity at the end of each CreatureFrame). Also applies the
    /// shooting-star kill when over <c>g_maxspeed</c>. Call once per server frame per player, then it stores
    /// the new oldvelocity for next frame.
    /// </summary>
    public static void FallDamage(Player p, ServerPlayerState st)
    {
        Vector3 vel = p.Velocity;
        Vector3 oldVel = st.OldVelocity;

        // QC: skip if the entity hasn't moved and isn't moving.
        if (vel == Vector3.Zero && oldVel == Vector3.Zero)
        {
            st.OldVelocity = vel;
            return;
        }

        float dm; // velocity DECREASE; an increase never causes damage.
        if (Cvars.Bool("g_balance_falldamage_onlyvertical"))
            dm = MathF.Abs(oldVel.Z) - vel.Length();
        else
            dm = oldVel.Length() - vel.Length();

        if (p.IsDead)
            dm = (dm - Cvars.Float("g_balance_falldamage_deadminspeed")) * Cvars.Float("g_balance_falldamage_factor");
        else
            dm = System.Math.Min(
                (dm - Cvars.Float("g_balance_falldamage_minspeed")) * Cvars.Float("g_balance_falldamage_factor"),
                Cvars.Float("g_balance_falldamage_maxdamage"));

        if (dm > 0f)
            Combat.Damage(p, null, null, dm, DeathTypes.Fall, p.Origin, Vector3.Zero);

        // QC shooting-star: moving faster than g_maxspeed is lethal (anticheat-ish guard); 0 = off.
        // server/main.qc:180: `Damage(this, NULL, NULL, 100000, DEATH_SHOOTING_STAR.m_id, DMG_NOWEP, this.origin, '0 0 0')`
        float maxSpeed = Cvars.Float("g_maxspeed");
        if (maxSpeed > 0f && vel.Length() > maxSpeed)
            Combat.Damage(p, null, null, 100000f, DeathTypes.ShootingStar, p.Origin, Vector3.Zero);

        st.OldVelocity = vel; // QC: it.oldvelocity = it.velocity at the end of CreatureFrame.
    }

    /// <summary>
    /// QC <c>player_powerups()</c> (server/client.qc:1539-1635) — the per-frame powerup pass, reduced to the
    /// slice this port models: snapshot the player's item bitmask (QC <c>items_prev</c>), run the superweapon
    /// countdown (<see cref="SuperweaponPass"/>, the WEPSET_SUPERWEAPONS block), then fire the
    /// <see cref="MutatorHooks.PlayerPowerups"/> hook with <c>(player, items_prev)</c> — the
    /// <c>MUTATOR_CALLHOOK(PlayerPowerups, this, items_prev)</c> at the tail of QC's function. The hook lets a
    /// mutator manipulate the values powerup items set this frame (the C# successor to QC's instagib/overkill
    /// PlayerPowerups handlers). Call once per frame per live player, in PostThink. The other powerup-status
    /// effects (strength/shield/invis/speed) are driven by the status-effect tick + their consumers, so this
    /// pass only carries the superweapon countdown today; the hook still fires unconditionally as in QC.
    /// </summary>
    public static void PlayerPowerups(Player p)
    {
        // QC client.qc:1539-1544: the jetpack modelflag + the EF_NODEPTHTEST clear-each-frame run unconditionally
        // (even when dead/gibbed). MF_ROCKET is a model-render flag with no Entity.ModelFlags channel in the port,
        // so the jetpack rocket-trail modelflag is dropped (cosmetic; the jetpack itself fires its own effect).
        // EF_NODEPTHTEST is cleared every frame and re-set below if g_nodepthtestplayers is on (QC client.qc:1542).
        p.Effects &= ~EffectFlags.NoDepthTest;

        // QC client.qc:1547-1548: a dead player loses its superweapon / unlimited bits (with the power-off sound).
        if (p.IsDead)
            PlayerPowerupsRemoveAll(p, true);

        // QC client.qc:1550-1551: a gibbed (alpha < 0) or dead player (not in a vehicle) skips the rest of the
        // function entirely — no items_prev, no superweapon block, NO PlayerPowerups hook. Mirror that bare
        // return so a corpse neither accrues superweapon flags nor fires the per-frame powerup mutator hook.
        if ((p.Alpha < 0f || p.IsDead) && p.Vehicle is null)
            return;

        // QC: items_prev = this.items — captured BEFORE the superweapon checks mutate it, so the hook sees the
        // pre-pass bitmask.
        int itemsPrev = p.Items;

        SuperweaponPass(p);

        // QC client.qc:1628-1632: draw-through-walls / fullbright debug toggles, OR'd in after the powerup block.
        if (Cvars.Bool("g_nodepthtestplayers"))
            p.Effects |= EffectFlags.NoDepthTest;
        if (Cvars.Bool("g_fullbrightplayers"))
            p.Effects |= EffectFlags.FullBright;

        // QC tail: MUTATOR_CALLHOOK(PlayerPowerups, this, items_prev).
        var args = new MutatorHooks.PlayerPowerupsArgs(p, itemsPrev);
        MutatorHooks.PlayerPowerups.Call(ref args);
    }

    /// <summary>QC <c>!g_cts</c> for the broadcast superweapon-pickup INFO line: a CTS map suppresses the
    /// "picked up a Superweapon" broadcast (it's part of the start loadout). Host-wired (default: not CTS).</summary>
    public static System.Func<bool> IsCtsGametype { get; set; } = static () => false;

    /// <summary>
    /// QC <c>player_powerups_remove_all(this, allow_poweroff_sound)</c> (server/client.qc:1521-1535): strip the
    /// superweapon / unlimited-ammo / unlimited-superweapon item bits and, when allowed, play the power-off sound.
    /// Called on death (allow=true) and on disconnect (allow=IS_PLAYER). The sound only plays for a live client
    /// after the first second of the match and only if the unlimited bits weren't part of the start loadout (so an
    /// arena's permanent unlimited ammo doesn't beep). Returns nothing; mutates <paramref name="p"/>'s item bits.
    /// </summary>
    public static void PlayerPowerupsRemoveAll(Player p, bool allowPoweroffSound)
    {
        const int powerBits = (int)ItemFlag.Superweapon | (int)ItemFlag.UnlimitedAmmo | (int)ItemFlag.UnlimitedSuperweapons;
        if ((p.Items & powerBits) == 0)
            return;

        // QC: don't play the poweroff sound when the game restarts or the player disconnects mid-loadout-unlimited.
        if (allowPoweroffSound && Api.Services is not null)
        {
            float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
            int startItems = StartItemFlagsCache();
            bool startUnlimited = (startItems & ((int)ItemFlag.UnlimitedAmmo | (int)ItemFlag.UnlimitedSuperweapons)) != 0;
            if (Api.Clock.Time > gameStart + 1f && !startUnlimited)
                Api.Sound.Play(p, SoundChannel.Auto, "misc/poweroff.wav");
        }

        p.Items &= ~powerBits;
    }

    // QC start_items & (IT_UNLIMITED_AMMO|IT_UNLIMITED_SUPERWEAPONS): whether the match's start loadout grants
    // permanent unlimited ammo/superweapons (an arena). Derived from the same SetStartItems pass the spawn uses.
    private static int StartItemFlagsCache()
    {
        var l = SpawnSystem.ComputeStartItems();
        int flags = 0;
        if (l.ItemFlags.Contains("UNLIMITED_AMMO")) flags |= (int)ItemFlag.UnlimitedAmmo;
        if (l.ItemFlags.Contains("UNLIMITED_SUPERWEAPONS")) flags |= (int)ItemFlag.UnlimitedSuperweapons;
        return flags;
    }

    /// <summary>
    /// QC the superweapon block of <c>player_powerups()</c> (server/client.qc:1556-1604): the four-case state
    /// machine over the <c>IT_SUPERWEAPON</c> item bit (<see cref="ItemFlag.Superweapon"/>) and whether the player
    /// still OWNS a superweapon (QC <c>WEPSET_SUPERWEAPONS</c> ≙ <see cref="Weapons.OwnsAnySuperWeapon"/>):
    /// <list type="bullet">
    /// <item>has the bit but no longer owns one → strip the effect + bit, <c>CENTER_SUPERWEAPON_LOST</c>;</item>
    /// <item>has the bit + unlimited → keep it pinned (never runs out);</item>
    /// <item>has the bit + a finite timer → <c>play_countdown(SND_POWEROFF)</c> the last seconds; when it lapses,
    ///   clear the bit + the owned superweapons, <c>CENTER_SUPERWEAPON_BROKEN</c>;</item>
    /// <item>owns one but doesn't have the bit yet → if the timer is live (or unlimited) set the bit (+ the pickup
    ///   notifications unless unlimited), else expire the effect and strip the owned superweapons.</item>
    /// </list>
    /// Call once per frame per live player. The IT_SUPERWEAPON bit drives the HUD superweapon ammo readout.
    /// </summary>
    private static void SuperweaponPass(Player p)
    {
        if (StatusEffectsCatalog.Superweapon is not { } sw) return;

        bool hasBit = (p.Items & (int)ItemFlag.Superweapon) != 0;
        bool ownsSuper = Weapons.OwnsAnySuperWeapon(p.OwnedWeapons);
        bool unlimited = (p.Items & (int)ItemFlag.UnlimitedSuperweapons) != 0;
        float now = Now;

        if (hasBit)
        {
            if (!ownsSuper)
            {
                // QC: no longer holding a superweapon → drop the effect + bit + "Superweapons have been lost".
                StatusEffectsCatalog.Remove(p, sw);
                p.Items &= ~(int)ItemFlag.Superweapon;
                NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center, "SUPERWEAPON_LOST");
            }
            else if (unlimited)
            {
                // QC: "don't let them run out" — nothing to do.
            }
            else
            {
                // QC play_countdown(this, StatusEffects_gettime(Superweapon), SND_POWEROFF): beep the last seconds.
                float finished = StatusEffectsCatalog.GetTime(p, sw, now);
                PlayCountdown(p, finished, now);
                if (now >= finished)
                {
                    p.Items &= ~(int)ItemFlag.Superweapon;
                    StripOwnedSuperweapons(p);
                    NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center, "SUPERWEAPON_BROKEN");
                }
            }
        }
        else if (ownsSuper)
        {
            // QC: keep the unlimited timer pinned so GetTime never lapses (port: persistence handled by the
            // PersistentCheck on the effect; re-apply a far-future window when unlimited so a fresh pickup arms it).
            if (unlimited && !StatusEffectsCatalog.Has(p, sw))
                StatusEffectsCatalog.Apply(p, sw, 999f);

            if (now < StatusEffectsCatalog.GetTime(p, sw, now) || unlimited)
            {
                p.Items |= (int)ItemFlag.Superweapon;
                if (!unlimited)
                {
                    // QC: broadcast "<name> picked up a Superweapon" (unless CTS, where it's part of the loadout),
                    // and centerprint "You now have a superweapon" to the owner.
                    if (!IsCtsGametype())
                        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "SUPERWEAPON_PICKUP", p.NetName);
                    NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center, "SUPERWEAPON_PICKUP");
                }
            }
            else
            {
                // QC: timer lapsed without ever setting the bit → expire the effect + strip the owned superweapons.
                if (StatusEffectsCatalog.Has(p, sw))
                    StatusEffectsCatalog.Remove(p, sw);
                StripOwnedSuperweapons(p);
            }
        }
        else if (StatusEffectsCatalog.Has(p, sw))
        {
            // QC: a stray Superweapon effect with no superweapon owned → clear it.
            StatusEffectsCatalog.Remove(p, sw);
        }
    }

    // QC STAT(WEAPONS,this) &= ~WEPSET_SUPERWEAPONS: drop every owned superweapon; reselect if the active slot
    // was one (the weapon frame picks the best remaining from the owned set).
    private static void StripOwnedSuperweapons(Player p)
    {
        bool strippedActive = false;
        foreach (Weapon w in Weapons.Superweapons)
        {
            if (p.OwnedWeapons.Remove(w.NetName) && p.ActiveWeaponId == w.RegistryId)
                strippedActive = true;
        }
        if (strippedActive)
        {
            p.ActiveWeaponId = -1;
            p.SwitchWeaponId = -1;
        }
    }

    // QC play_countdown(this, finished, samp) (server/client.qc:1532): beep once per integer second crossed in
    // the last 6 seconds before <paramref name="finished"/> (SND_POWEROFF = misc/poweroff.wav, CH_INFO).
    private static void PlayCountdown(Player p, float finished, float now)
    {
        if (Api.Services is null || finished <= 0f) return;
        float frametime = Api.Clock.FrameTime;
        float timeLeft = finished - now;
        if (timeLeft < 6f && MathF.Floor(timeLeft - frametime) != MathF.Floor(timeLeft))
            Api.Sound.Play(p, SoundChannel.Auto, "misc/poweroff.wav");
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
