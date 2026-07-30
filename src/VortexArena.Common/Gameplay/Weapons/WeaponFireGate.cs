using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
// Alias the animdecide unit so the call sites read unambiguously (it lives flat in XonoticGodot.Common.Gameplay).
using AnimDecideUnit = XonoticGodot.Common.Gameplay.AnimDecide;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The weapon-side of the fire driver: the C# successor to QuakeC's <c>weapon_prepareattack</c> +
/// <c>weapon_thinkf</c> refire/animtime machinery (server/weapons/weaponsystem.qc), expressed as members on
/// the <see cref="Weapon"/> base so every concrete weapon's <c>WrThink</c> can gate its attack exactly the
/// way QC does: <c>if (weapon_prepareattack(...)) { ...fire...; weapon_thinkf(...); }</c>.
///
/// This is a NEW partial of <see cref="Weapon"/> (it only ADDS members; the original declaration in
/// GameplayBases.cs is untouched), keeping the API-stability contract.
/// </summary>
public abstract partial class Weapon
{
    // =====================================================================================================
    //  the refire gate — weapon_prepareattack (server/weapons/weaponsystem.qc)
    // =====================================================================================================

    /// <summary>
    /// Port of <c>weapon_prepareattack</c> (server/weapons/weaponsystem.qc): decide whether
    /// <paramref name="actor"/> may fire this weapon's <paramref name="fire"/> mode THIS tick and, if so,
    /// arm the refire timer + schedule the return-to-READY animation. A weapon's <c>WrThink</c> calls this
    /// immediately before it actually fires:
    /// <code>if (PrepareAttack(actor, slot, fire)) { ...spawn projectile / trace...; }</code>
    ///
    /// <para>Returns <c>false</c> (and fires nothing) when: the fire button isn't held; the weapon isn't
    /// READY (mid raise/drop/animation); the refire timer (<c>ATTACK_FINISHED</c>) hasn't elapsed; or the
    /// mode is out of ammo (in which case it also auto-switches away — QC
    /// <c>weapon_prepareattack_checkammo</c>).</para>
    ///
    /// <para>Refire + animtime come from <see cref="RefireFor"/> / <see cref="AnimtimeFor"/> (the weapon's
    /// balance block); both honor the global <c>g_weaponratefactor</c> as QC does.</para>
    /// </summary>
    public bool PrepareAttack(Entity actor, WeaponSlot slot, FireMode fire)
        => PrepareAttack(actor, slot, fire, attackTime: float.NaN);

    /// <summary>
    /// Variant of <see cref="PrepareAttack(Entity,WeaponSlot,FireMode,float)"/> that decouples the
    /// <paramref name="buttonFire"/> (which physical fire button must be held) from <paramref name="fire"/>
    /// (which fire MODE — i.e. which <c>wr_checkammo</c> + refire/animtime — to commit). In QC,
    /// <c>weapon_prepareattack</c>'s <c>secondary</c> arg only selects the ammo check; the button is gated
    /// earlier by <c>W_WeaponFrame</c> via the <c>fire</c> bitmask handed to <c>wr_think</c>. The headless
    /// port re-derives the button check here, so weapons whose gate fires one MODE off the OTHER button
    /// (Shotgun's out-of-ammo auto-melee: a secondary-mode slap triggered by an empty PRIMARY press) pass the
    /// originating button as <paramref name="buttonFire"/> so the held-button gate matches what the player
    /// actually pressed.
    /// </summary>
    public bool PrepareAttack(Entity actor, WeaponSlot slot, FireMode fire, float attackTime, FireMode buttonFire)
        => PrepareAttackImpl(actor, slot, fire, attackTime, buttonFire);

    /// <summary>
    /// Port of <c>weapon_prepareattack</c> with the explicit <c>attacktime</c> parameter (QC's last arg). The
    /// default <see cref="PrepareAttack(Entity,WeaponSlot,FireMode)"/> overload passes <c>NaN</c> meaning "use
    /// this weapon's standard <see cref="RefireFor"/>"; weapons that run on a private interlock timer
    /// (Electro secondary's <c>electro_secondarytime</c>, Shotgun primary's <c>shotgun_primarytime</c>) pass a
    /// NEGATIVE <paramref name="attackTime"/> to take the QC <c>attacktime &lt; 0</c> escape: it neither
    /// consults nor advances the shared <c>ATTACK_FINISHED</c> (weaponsystem.qc
    /// weapon_prepareattack_check/_do both guard those branches with <c>attacktime &gt;= 0</c>), while still
    /// committing the shot (WS_INUSE, spawn-shield drop, bulletcounter). A weapon may also pass an explicit
    /// non-negative <paramref name="attackTime"/> to advance the shared timer by an amount other than the
    /// default refire (Shotgun primary parks its real refire elsewhere and advances the shared timer by
    /// animtime — see <see cref="AnimtimeFor"/>).
    /// </summary>
    public bool PrepareAttack(Entity actor, WeaponSlot slot, FireMode fire, float attackTime)
        => PrepareAttackImpl(actor, slot, fire, attackTime, buttonFire: fire);

    private bool PrepareAttackImpl(Entity actor, WeaponSlot slot, FireMode fire, float attackTime, FireMode buttonFire)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        bool secondary = fire == FireMode.Secondary;

        // QC W_WeaponFrame only invokes wr_think's fire branch for a HELD button; the headless driver always
        // calls WrThink(Primary) for upkeep, so the button check is the authority that no shot leaks out when
        // the player isn't pressing fire. The button gated here is the one the player PRESSED (buttonFire),
        // which can differ from the fire MODE being committed (Shotgun auto-melees a SECONDARY slap off an
        // empty PRIMARY press).
        bool held = buttonFire == FireMode.Secondary ? st.ButtonAttack2 : st.ButtonAttack;
        if (!held)
            return false;

        // weapon_prepareattack_check: ammo first (QC weapon_prepareattack_checkammo + the auto-switch).
        if (!CheckAmmoWithAutoSwitch(actor, slot, secondary))
            return false;

        // QC weapon_prepareattack_check: the WS_READY + refire (ATTACK_FINISHED) gates only run when
        // attacktime >= 0. A negative attacktime is the "own private timer" escape (Electro secondary /
        // Shotgun primary already gated themselves) and skips BOTH gates.
        bool useSharedTimer = !(attackTime < 0f); // NaN compares false -> treated as the standard shared path
        if (useSharedTimer)
        {
            // Must be idle+ready (QC: !actor.vehicle && this.state != WS_READY -> bail). A weapon mid
            // raise/drop or mid fire-anim can't start a new attack.
            if (st.State != WeaponFireState.Ready)
                return false;

            float frametime0 = WeaponFireDriver.WeaponFrameTime();
            // Refire gate (QC: ATTACK_FINISHED > time + weapon_frametime*0.5 -> bail).
            if (st.AttackFinished > Api.Clock.Time + frametime0 * 0.5f)
                return false;
        }

        // --- weapon_prepareattack_do: commit the attack ---
        st.State = WeaponFireState.InUse;

        // QC weapon_prepareattack_do: "kill spawn shield when you fire" (StatusEffects_remove(SpawnShield,
        // CLEAR)). The port models the player spawn shield as an absolute-time field on the actor entity;
        // clearing it to 0 is the StatusEffects_remove equivalent (DamageSystem.RemoveSpawnShield).
        if (actor.SpawnShieldExpire > Api.Clock.Time)
            actor.SpawnShieldExpire = 0f;

        float refire = float.IsNaN(attackTime) ? MathF.Max(0f, RefireFor(fire)) : attackTime;
        float animtime = MathF.Max(0f, AnimtimeFor(fire));
        // QC W_WeaponRateFactor is called with the firing entity so the WeaponRateFactor mutator hook (speed
        // powerup / speed buff) can scale the refire+animtime for THIS player. The shared fire gate is the one
        // QC point that has the actor, so wrap here (per-weapon private-timer escapes use the base no-arg form).
        float rate = WeaponRateFactor(actor);

        // The shared ATTACK_FINISHED is only consulted/advanced for the attacktime >= 0 path (QC the
        // "if (attacktime >= 0)" block in weapon_prepareattack_do).
        if (useSharedTimer)
        {
            float frametime = WeaponFireDriver.WeaponFrameTime();
            // If the weapon hasn't been firing continuously, reset the timer to now (QC the
            // "ATTACK_FINISHED < time - frametime*1.5" reset) so the first shot of a burst isn't penalized.
            if (st.AttackFinished < Api.Clock.Time - frametime * 1.5f)
                st.AttackFinished = Api.Clock.Time;
            st.AttackFinished += refire * rate;
        }

        ++st.BulletCounter;

        // weapon_thinkf(actor, weaponentity, WFRAME_FIRE*, animtime, w_ready): schedule the return to READY
        // once the fire animation ends. With animtime 0 the slot is READY again next tick (the refire timer
        // still gates the actual fire rate). This is the animtime half of the QC gate.
        WeaponFireDriver.ScheduleThink(st, animtime * rate, static (pl, sl) =>
        {
            WeaponSlotState s2 = pl.WeaponState(sl);
            // Only settle to READY if we're still the in-use action (a switch may have moved us on).
            if (s2.State == WeaponFireState.InUse)
                s2.State = WeaponFireState.Ready;
        });

        // [W14b LI1] animdecide upper-body action set-site. QC weapon_thinkf (weaponsystem.qc:415-423) latches the
        // SHOOT (or MELEE) upper action only when it schedules a WFRAME_FIRE1/FIRE2 with a NON-ZERO animtime
        // (`(fr == WFRAME_FIRE1 || fr == WFRAME_FIRE2) && t`). The committed attack is the port's WFRAME_FIRE
        // analogue, so latch the action now (restart = true to mirror QC's restartanim = fr != WFRAME_IDLE, so a
        // held-trigger stream restarts the window each shot). A ZERO-animtime fire mode (QC's `&& t` fails) does
        // NOT latch a torso overlay and, like QC's else-branch (weaponsystem.qc:422-423), CLEARS a lingering
        // SHOOT/MELEE — a zero-animtime shot returns the torso to idle.
        //
        // SHOOT vs MELEE (weaponsystem.qc:417-419): a PRIMARY shot off a WEP_TYPE_MELEE_PRI weapon, or a SECONDARY
        // shot off a WEP_TYPE_MELEE_SEC weapon (the Shotgun's bash), latches MELEE instead of SHOOT. The mode here is
        // `fire`, so test the matching melee spawnflag for that mode. (Pain/Draw/Taunt/Die latch at their own sites.)
        float fireNow = Api.Services is not null ? Api.Clock.Time : 0f;
        if (animtime > 0f)
        {
            bool primaryMelee = fire == FireMode.Primary && (SpawnFlags & WeaponFlags.TypeMeleePri) != 0;
            bool secondaryMelee = fire == FireMode.Secondary && (SpawnFlags & WeaponFlags.TypeMeleeSec) != 0;
            AnimDecideUnit.AnimUpperAction act0 = (primaryMelee || secondaryMelee)
                ? AnimDecideUnit.AnimUpperAction.Melee
                : AnimDecideUnit.AnimUpperAction.Shoot;
            var (act, start) = AnimDecideUnit.SetAction(
                actor.AnimUpperAction, actor.AnimActionStart, act0, fireNow, restart: true);
            actor.AnimUpperAction = act;
            actor.AnimActionStart = start;
        }
        else if (actor.AnimUpperAction is AnimDecideUnit.AnimUpperAction.Shoot or AnimDecideUnit.AnimUpperAction.Melee)
        {
            actor.AnimUpperAction = AnimDecideUnit.AnimUpperAction.None;
        }

        return true;
    }

    /// <summary>
    /// QC <c>weapon_prepareattack_checkammo</c>: true if the mode has ammo (or unlimited-ammo is set). On a
    /// dry mode this also performs the QC out-of-ammo behavior — if the OTHER mode can't fire either, switch
    /// away to a weapon that can (<see cref="SwitchToOtherWeapon"/>).
    /// </summary>
    private bool CheckAmmoWithAutoSwitch(Entity actor, WeaponSlot slot, bool secondary)
    {
        if (actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0)
            return true;

        if (WrCheckAmmo(actor, slot, secondary))
            return true;

        // QC weapon_prepareattack_checkammo (weaponsystem.qc:254-256): a Shotgun whose secondary is melee does
        // NOT dry-fire-click on an empty PRIMARY press — it stays quiet ("no clicking, just allow") so the
        // out-of-ammo auto-melee can slap instead of clicking. Mirror that weapon-specific early-out here so
        // the empty-primary auto-melee path is silent (and never auto-switches away) exactly like Base.
        if (this is Shotgun shotgunWep && !secondary && shotgunWep.Secondary.Secondary == 1)
            return false;

        WeaponSlotState st = actor.WeaponState(slot);

        // Dry-fire CLICK (QC: sound(actor, CH_WEAPON_A, SND_DRYFIRE, ...)), throttled to once a second so a
        // held trigger on an empty gun clicks rather than spams.
        if (Api.Services is not null && Api.Clock.Time - st.PrevDryFire > 1f)
        {
            Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/dryfire.wav");
            st.PrevDryFire = Api.Clock.Time;
        }

        // The mode is dry. QC: if the OTHER firing mode has ammo, keep this weapon (just don't fire this mode);
        // otherwise the weapon is totally unable to fire, so switch away to one that can.
        bool other = WrCheckAmmo(actor, slot, !secondary);
        if (!other)
            SwitchToOtherWeapon(actor, slot);

        return false;
    }

    // =====================================================================================================
    //  per-mode refire / animtime / ammo — overridable per weapon (defaults read the standard cvars)
    // =====================================================================================================

    /// <summary>
    /// The refire delay (seconds) for <paramref name="fire"/> — QC <c>WEP_CVAR_PRI/SEC(this, refire)</c>.
    /// Default reads the standard <c>g_balance_&lt;netname&gt;_primary_refire</c> /
    /// <c>_secondary_refire</c> (falling back to <c>g_balance_&lt;netname&gt;_refire</c> for single-mode
    /// weapons). Weapons whose refire cvar is irregular (MachineGun: sustained/first; Arc: beam/bolt)
    /// override this.
    /// </summary>
    public virtual float RefireFor(FireMode fire) => BalanceTiming(fire, "refire", 1f);

    /// <summary>
    /// The fire-animation time (seconds) for <paramref name="fire"/> — QC
    /// <c>WEP_CVAR_PRI/SEC(this, animtime)</c>. Default mirrors <see cref="RefireFor"/>'s cvar convention.
    /// </summary>
    public virtual float AnimtimeFor(FireMode fire) => BalanceTiming(fire, "animtime", 0f);

    /// <summary>
    /// Whether this weapon's <paramref name="secondary"/> (or primary) mode currently has ammo — QC
    /// <c>wr_checkammo1</c> / <c>wr_checkammo2</c>. The default dispatches to the concrete weapon's existing
    /// <c>CheckAmmoPrimary</c>/<c>CheckAmmoSecondary</c> via <see cref="WeaponAmmo"/> (so no weapon needs to
    /// override this); weapons with no ammo concept report true.
    /// </summary>
    public virtual bool WrCheckAmmo(Entity actor, WeaponSlot slot, bool secondary)
        => WeaponAmmo.Check(this, actor, secondary);

    /// <summary>
    /// QC <c>wr_reload</c> hook → <c>W_Reload</c> (server/weapons/weaponsystem.qc). Faithful port of the reload
    /// pipeline, generic over every reloadable weapon: in QC each weapon's <c>wr_reload</c> is just
    /// <c>W_Reload(actor, weaponentity, ammo_min, SND_RELOAD)</c> with a weapon-specific per-shot ammo floor, so
    /// the base computes that floor from the weapon's reload-ammo balance and runs the shared <see cref="WReload"/>.
    /// Non-reloadable weapons (no <see cref="WeaponFlags.Reloadable"/>) are a no-op, exactly like QC's early-out.
    /// Weapons whose reload has extra preconditions (MachineGun: don't reload mid-burst) can still override.
    /// </summary>
    public virtual void WrReload(Entity actor, WeaponSlot slot)
    {
        // ammo_min: QC passes min(primary_ammo, secondary_ammo) (the cheapest per-shot cost). The port doesn't
        // expose a uniform per-mode ammo-cost accessor, so use the clip-ammo balance as the floor: any positive
        // reserve allows a reload. reload_ammo == clip size; 1 is a safe lower bound for "can afford one shot".
        float ammoMin = MathF.Max(1f, ReloadingAmmoMin());
        WReload(actor, slot, ammoMin);
    }

    /// <summary>
    /// Port of <c>W_Reload</c> (server/weapons/weaponsystem.qc:755): begin reloading the weapon in
    /// <paramref name="slot"/> — validate (reloadable, not full, has ammo), then schedule
    /// <see cref="WReloadedAndReady"/> after <c>reloading_time</c> to transfer ammo into the clip. Marks the clip
    /// "scheduled for reload" (-1) while the think is pending. <paramref name="sentAmmoMin"/> is the per-shot ammo
    /// floor below which there's no point reloading (the weapon switches away instead).
    /// </summary>
    public void WReload(Entity actor, WeaponSlot slot, float sentAmmoMin)
    {
        WeaponSlotState st = actor.WeaponState(slot);

        st.ReloadAmmoMin = sentAmmoMin;
        st.ReloadAmmoAmount = ReloadingAmmo();
        st.ReloadTime = ReloadingTime();

        // don't reload weapons that don't have the RELOADABLE flag (QC LOG_TRACE + return).
        if ((SpawnFlags & WeaponFlags.Reloadable) == 0)
            return;

        // return if reloading is disabled for this weapon (reload_ammo == 0).
        if (st.ReloadAmmoAmount == 0f)
            return;

        // our weapon is fully loaded, no need to reload.
        if (st.ClipLoad >= st.ReloadAmmoAmount)
            return;

        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;

        // no ammo, so nothing to load (QC: ammo_type != RES_NONE && !GetResource && reload_ammo_min && !unlimited).
        if (AmmoType != ResourceType.None
            && actor.GetResource(AmmoType) <= 0f
            && st.ReloadAmmoMin != 0f
            && !unlimited)
        {
            float now0 = Api.Services is not null ? Api.Clock.Time : 0f;
            if (st.ReloadComplain < now0)
            {
                // QC weaponsystem.qc:794 play2(actor, SND(UNAVAILABLE)) — a per-recipient 2D cue on CH_INFO at
                // VOL_BASE 0.7 / ATTEN_NONE 0. SND(UNAVAILABLE) resolves to "weapons/unavailable" (NOT
                // "misc/unavailable"); SoundSystem.Play2 plays the registered cue 2D with Base's vol/atten/channel.
                if (Api.Services is not null)
                    SoundSystem.Play2(actor, "UNAVAILABLE");
                st.ReloadComplain = now0 + 1f;
            }
            // switch away if the amount of ammo is not enough to keep using this weapon.
            if (!(WrCheckAmmo(actor, slot, false) || WrCheckAmmo(actor, slot, true)))
            {
                st.ClipLoad = -1; // reload later
                SwitchToOtherWeapon(actor, slot);
            }
            return;
        }

        // QC: if (this.wframe == WFRAME_RELOAD) return; — already reloading.
        if (st.Reloading)
            return;

        // allow switching away while reloading, but that causes a new reload (QC: this.state = WS_READY).
        st.State = WeaponFireState.Ready;

        // begin the reloading process (QC plays the reload sound).
        if (Api.Services is not null)
            Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/reload.wav");

        // schedule W_ReloadedAndReady after reload_time (QC weapon_thinkf(WFRAME_RELOAD, reload_time, ...)).
        st.Reloading = true;
        float rate = WeaponRateFactor(actor);
        WeaponFireDriver.ScheduleThink(st, st.ReloadTime * rate, static (pl, sl) =>
        {
            Weapon? wpn = Inventory.CurrentWeapon(pl);
            wpn?.WReloadedAndReady(pl, sl);
        });

        if (st.ClipLoad < 0)
            st.ClipLoad = 0;
        st.OldClipLoad = st.ClipLoad;
        st.ClipLoad = -1;
        SetWeaponLoad(st, RegistryId, -1);
    }

    /// <summary>
    /// Port of <c>W_ReloadedAndReady</c> (server/weapons/weaponsystem.qc:726): the reload finished — restore the
    /// pre-reload clip, then top it up (free if the gun uses no ammo / unlimited ammo, else draw from the player's
    /// ammo pool without overdrawing), persist it into <c>weapon_load[]</c>, and return the slot to READY.
    /// </summary>
    public void WReloadedAndReady(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        st.Reloading = false;

        st.ClipLoad = st.OldClipLoad; // restore the counter (we may still have had ammo before reloading)

        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;

        if (st.ReloadAmmoMin == 0f || unlimited || AmmoType == ResourceType.None)
        {
            st.ClipLoad = (int)st.ReloadAmmoAmount;
        }
        else
        {
            // make sure we don't add more ammo than we have.
            float ammo = actor.GetResource(AmmoType);
            float load = MathF.Min(st.ReloadAmmoAmount - st.ClipLoad, ammo);
            if (load < 0f) load = 0f;
            st.ClipLoad += (int)load;
            actor.SetResource(AmmoType, ammo - load);
        }
        SetWeaponLoad(st, RegistryId, st.ClipLoad);

        // QC: return to READY (no ATTACK_FINISHED penalty — that was removed in QC to avoid switch-cancel delays).
        if (st.State == WeaponFireState.InUse || st.State == WeaponFireState.Ready)
            st.State = WeaponFireState.Ready;
    }

    /// <summary>
    /// Port of <c>W_DecreaseAmmo</c> (server/weapons/weaponsystem.qc:689): consume <paramref name="ammoUse"/> for
    /// a shot. If this weapon is reloadable, decrease its clip load (and persist it into <c>weapon_load[]</c>);
    /// otherwise decrease the player's ammo resource. Honors unlimited-ammo for non-reloadable weapons (a
    /// reloadable weapon still drains its clip even with unlimited ammo, as QC does — the clip is refilled free).
    /// </summary>
    public void DecreaseAmmo(Entity actor, WeaponSlot slot, float ammoUse)
    {
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        bool reloadable = (SpawnFlags & WeaponFlags.Reloadable) != 0 && ReloadingAmmo() != 0f;

        if (unlimited && !reloadable)
            return;

        WeaponSlotState st = actor.WeaponState(slot);
        if (reloadable)
        {
            st.ClipLoad -= (int)ammoUse;
            SetWeaponLoad(st, RegistryId, st.ClipLoad);
        }
        else if (AmmoType != ResourceType.None)
        {
            float ammo = actor.GetResource(AmmoType);
            actor.SetResource(AmmoType, MathF.Max(0f, ammo - ammoUse));
        }
    }

    // --- per-weapon persistent magazine store (QC .weapon_load[REGISTRY_MAX(Weapons)] on the weapon entity) ---

    /// <summary>QC <c>w_ent.(weapon_load[id])</c> read: the persistent clip for weapon <paramref name="id"/>.</summary>
    public static int GetWeaponLoad(WeaponSlotState st, int id)
        => (st.WeaponLoad is { Length: > 0 } a && id >= 0 && id < a.Length) ? a[id] : 0;

    /// <summary>
    /// QC the PutPlayerInServer reloadable-weapon seed (server/client.qc:809 <c>weapon_load[id] = reloading_ammo</c>):
    /// the first time a reloadable weapon is raised on this slot, start its magazine FULL. Idempotent per weapon id
    /// via <see cref="WeaponSlotState.WeaponLoadSeeded"/> so later switches don't refill the clip for free.
    /// </summary>
    public void SeedClipIfFresh(WeaponSlotState st, int id)
    {
        st.WeaponLoadSeeded ??= new HashSet<int>();
        if (!st.WeaponLoadSeeded.Add(id))
            return; // already seeded
        SetWeaponLoad(st, id, (int)ReloadingAmmo());
    }

    /// <summary>QC <c>w_ent.(weapon_load[id]) = v</c> write (grows the per-slot array to fit the registry).</summary>
    public static void SetWeaponLoad(WeaponSlotState st, int id, int v)
    {
        if (id < 0) return;
        int need = Registry<Weapon>.Count;
        if (st.WeaponLoad.Length < need)
        {
            var grown = new int[need];
            System.Array.Copy(st.WeaponLoad, grown, st.WeaponLoad.Length);
            st.WeaponLoad = grown;
        }
        if (id < st.WeaponLoad.Length)
            st.WeaponLoad[id] = v;
    }

    // --- reload / switch-delay balance reads (QC reloading_ammo/reloading_time + switchdelay_raise/drop) ---

    /// <summary>QC <c>this.reloading_ammo</c> (== g_balance_&lt;netname&gt;_reload_ammo): the magazine capacity.</summary>
    public virtual float ReloadingAmmo() => Bal($"g_balance_{NetName}_reload_ammo", 0f);

    /// <summary>QC <c>this.reloading_time</c> (== g_balance_&lt;netname&gt;_reload_time): the reload duration.</summary>
    public virtual float ReloadingTime() => Bal($"g_balance_{NetName}_reload_time", 0f);

    /// <summary>The per-shot ammo floor used as W_Reload's <c>sent_ammo_min</c> default; 1 unless overridden.</summary>
    protected virtual float ReloadingAmmoMin() => 1f;

    /// <summary>QC <c>this.switchdelay_raise</c> (== g_balance_&lt;netname&gt;_switchdelay_raise): raise-in delay.</summary>
    public float SwitchDelayRaise() => Bal($"g_balance_{NetName}_switchdelay_raise", 0f);

    /// <summary>QC <c>this.switchdelay_drop</c> (== g_balance_&lt;netname&gt;_switchdelay_drop): drop-out delay.</summary>
    public float SwitchDelayDrop() => Bal($"g_balance_{NetName}_switchdelay_drop", 0f);

    /// <summary>
    /// Read a standard balance-timing cvar for <paramref name="fire"/>: tries
    /// <c>g_balance_&lt;netname&gt;_{primary|secondary}_&lt;key&gt;</c>, then the single-mode
    /// <c>g_balance_&lt;netname&gt;_&lt;key&gt;</c>, then <paramref name="fallback"/>.
    /// </summary>
    private float BalanceTiming(FireMode fire, string key, float fallback)
    {
        string mode = fire == FireMode.Secondary ? "secondary" : "primary";
        float v = Bal($"g_balance_{NetName}_{mode}_{key}", float.NaN);
        if (!float.IsNaN(v)) return v;
        v = Bal($"g_balance_{NetName}_{key}", float.NaN);
        return float.IsNaN(v) ? fallback : v;
    }

    /// <summary>QC <c>W_WeaponRateFactor</c>: <c>1/g_weaponratefactor</c> (clamped to 1 when unset/non-positive).</summary>
    protected static float WeaponRateFactor()
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponratefactor");
        return f > 0f ? 1f / f : 1f;
    }

    /// <summary>
    /// Port of <c>W_WeaponRateFactor(entity actor)</c> (common/weapons/weapon.qh): the base
    /// <c>1/g_weaponratefactor</c> with the <c>WeaponRateFactor</c> mutator hook applied for
    /// <paramref name="actor"/>. The Speed powerup and the Speed buff multiply the factor here, so a sped-up
    /// player fires faster (and reloads faster). Used at the shared fire/reload gates, which have the actor.
    /// </summary>
    protected static float WeaponRateFactor(Entity actor)
    {
        float f = WeaponRateFactor();
        // QC: MUTATOR_CALLHOOK(WeaponRateFactor, f, actor); f = M_ARGV(0, float);
        var args = new MutatorHooks.WeaponRateFactorArgs(f, actor);
        MutatorHooks.WeaponRateFactor.Call(ref args);
        return args.Factor;
    }

    // =====================================================================================================
    //  auto-switch — W_SwitchToOtherWeapon (server/weapons/selection.qc)
    // =====================================================================================================

    /// <summary>
    /// Port of <c>W_SwitchToOtherWeapon</c> (server/weapons/selection.qc) + <c>w_getbestweapon</c>: switch the
    /// actor to the best owned weapon OTHER than the one currently held, preferring one that actually has ammo
    /// (so a dry weapon yields to a usable one), and falling back to the highest-impulse owned weapon if none
    /// has ammo. No-op if nothing else is owned.
    /// </summary>
    protected void SwitchToOtherWeapon(Entity actor, WeaponSlot slot)
    {
        Weapon? bestWithAmmo = null;
        Weapon? bestAny = null;
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        foreach (Weapon w in actor.OwnedWeaponSet.Weapons())
        {
            if (w.RegistryId == RegistryId) continue;
            if (bestAny is null || w.Impulse > bestAny.Impulse)
                bestAny = w;
            // Has-ammo preference (QC w_getbestweapon wants a usable weapon): either mode can fire.
            bool usable = unlimited
                || WeaponAmmo.Check(w, actor, secondary: false)
                || WeaponAmmo.Check(w, actor, secondary: true);
            if (usable && (bestWithAmmo is null || w.Impulse > bestWithAmmo.Impulse))
                bestWithAmmo = w;
        }
        Weapon? best = bestWithAmmo ?? bestAny;
        if (best is not null)
            Inventory.SwitchWeapon(actor, best);
    }

    /// <summary>QC <c>IT_UNLIMITED_AMMO</c> = BIT(0) (common/items/item.qh).</summary>
    private const int ItUnlimitedAmmo = 1 << 0;
}

/// <summary>
/// Dispatches a weapon's per-mode ammo check to its existing concrete <c>CheckAmmoPrimary</c> /
/// <c>CheckAmmoSecondary</c> method (each weapon already ports <c>wr_checkammo1</c>/<c>wr_checkammo2</c> as
/// a public instance method). Centralized here so the base <see cref="Weapon.WrCheckAmmo"/> works for every
/// weapon without editing the 19 weapon files. Weapons that have no such method (no ammo concept) report
/// "has ammo" = true.
/// </summary>
internal static class WeaponAmmo
{
    public static bool Check(Weapon w, Entity actor, bool secondary) => w switch
    {
        Blaster => true,
        Shotgun s => secondary ? s.CheckAmmoSecondary(actor) : s.CheckAmmoPrimary(actor),
        Machinegun m => m.CheckAmmoPrimary(actor), // sustained/burst both draw bullets; primary check suffices
        // Vortex secondary: wr_checkammo2 (vortex.qc:287-298) — a real check only when the secondary FIRE
        // mode is enabled (g_balance_vortex_secondary); with the stock zoom-charge secondary it reports
        // false ("zoom is not a fire mode"), so a cells-dry vortex auto-switches away like QC. [T57]
        Vortex v => secondary ? v.CheckAmmoSecondary(actor) : v.CheckAmmoPrimary(actor),
        Crylink c => secondary ? c.CheckAmmoSecondary(actor) : c.CheckAmmoPrimary(actor),
        Devastator d => d.CheckAmmoPrimary(actor),
        Mortar mo => secondary ? mo.CheckAmmoSecondary(actor) : mo.CheckAmmoPrimary(actor),
        Hagar h => secondary ? h.CheckAmmoSecondary(actor) : h.CheckAmmoPrimary(actor),
        Hlac hl => secondary ? hl.CheckAmmoSecondary(actor) : hl.CheckAmmoPrimary(actor),
        Electro e => secondary ? e.CheckAmmoSecondary(actor) : e.CheckAmmoPrimary(actor),
        Arc a => secondary ? a.CheckAmmoSecondary(actor) : a.CheckAmmoPrimary(actor),
        Rifle r => secondary ? r.CheckAmmoSecondary(actor) : r.CheckAmmoPrimary(actor),
        Seeker se => secondary ? se.CheckAmmoSecondary(actor) : se.CheckAmmoPrimary(actor),
        Minelayer ml => secondary ? ml.CheckAmmoSecondary(actor) : ml.CheckAmmoPrimary(actor),
        Fireball => true,
        Hook hk => secondary ? hk.CheckAmmoSecondary(actor) : hk.CheckAmmoPrimary(actor),
        Tuba => true,
        Porto => true,
        // wr_checkammo1 (rail, cells) / wr_checkammo2 (Blaster-laser secondary; free at the default Blaster ammo 0).
        Vaporizer vp => secondary ? vp.CheckAmmoSecondary(actor) : vp.CheckAmmoPrimary(actor),
        // Overkill weapons (okmachinegun.qc / okshotgun.qc / oknex.qc / okhmg.qc / okrpc.qc): without these
        // cases they fell through to the default `true`, so wr_checkammo never ran. OkMachinegun/OkNex use the
        // primary check for both modes (the secondary is the unlimited blaster jump / the zoom key, like
        // Machinegun/Vortex); the others honor wr_checkammo2 for secondary.
        OkMachinegun okmg => okmg.CheckAmmoPrimary(actor),               // okmachinegun.qc:115-126 (sec = unlimited blaster)
        OkShotgun oksg => secondary ? oksg.CheckAmmoSecondary(actor) : oksg.CheckAmmoPrimary(actor), // okshotgun.qc:72-82
        OkNex oknex => oknex.CheckAmmoPrimary(actor),                    // oknex.qc:261-279 (sec = zoom/charge key)
        OkHmg okhmg => secondary ? okhmg.CheckAmmoSecondary(actor) : okhmg.CheckAmmoPrimary(actor),  // okhmg.qc:116-130
        OkRpc okrpc => secondary ? okrpc.CheckAmmoSecondary(actor) : okrpc.CheckAmmoPrimary(actor),  // okrpc.qc:194-206
        _ => true,
    };
}
