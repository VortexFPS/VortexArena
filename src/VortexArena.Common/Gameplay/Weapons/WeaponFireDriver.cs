using VortexArena.Common.Framework;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;

namespace VortexArena.Common.Gameplay;

/// <summary>
/// The per-tick weapon-fire driver — the C# successor to QuakeC's <c>W_WeaponFrame</c> +
/// <c>weapon_prepareattack</c> + <c>weapon_thinkf</c> (server/weapons/weaponsystem.qc). It owns the
/// raise/ready/fire/drop state machine for a player's weapon slot(s), reads the player's
/// <c>BUTTON_ATCK</c>/<c>BUTTON_ATCK2</c> from the per-tick input, gates fire rate via the per-slot
/// <c>ATTACK_FINISHED</c> refire timer, and routes BOTH fire modes into the active weapon's
/// <see cref="Weapon.WrThink"/>.
///
/// <para><b>Why this exists:</b> previously <c>GameWorld.WeaponThink</c> called
/// <c>WrThink(p, slot 0, Primary)</c> unconditionally every server tick, so every weapon fired once per
/// tick, refire/animtime cvars were ignored, the secondary never fired, and slots &gt; 0 never drove. This
/// driver fixes all of that.</para>
///
/// <para><b>Faithful structure:</b> mirroring QC, the active weapon's <see cref="Weapon.WrThink"/> is called
/// EVERY tick with the held fire mode(s) so per-tick upkeep still runs (Vortex charge regen, Hook reel-in,
/// Devastator guiding, Crylink link-join, Arc beam continuation). The refire <i>gate</i> lives inside the
/// weapon, in <see cref="Weapon.PrepareAttack"/> (== QC's <c>weapon_prepareattack</c>), which each weapon
/// calls before it actually fires: it rejects the shot while <c>time &lt; ATTACK_FINISHED</c>, checks ammo,
/// and on success advances the timer + schedules the return-to-READY animation.</para>
///
/// <para><b>Slots:</b> QC supports up to <c>MAX_WEAPONSLOTS</c> independent weapon entities (dual-wielding).
/// This port currently tracks a single active weapon (<see cref="Entity.ActiveWeaponId"/>); slot 0 carries
/// it. The loop is written over all slots for fidelity / future dual-wield, but only slot 0 is populated
/// today.</para>
///
/// <para><b>Deferred to the next wave (T6):</b> the precise raise/drop switch <i>delays</i>
/// (<c>switchdelay_raise/drop</c>) and the clip-reload system (<c>clip_load</c>/<c>clip_size</c>/
/// <c>W_Reload</c>). Clean hooks for both exist (the WS_RAISE/WS_DROP transitions in
/// <see cref="DriveWeaponSwitch"/> and the <see cref="Weapon.WrReload"/> entry point); their timing is left
/// to T6.</para>
/// </summary>
public static class WeaponFireDriver
{
    /// <summary>QC <c>MAX_WEAPONSLOTS</c> (common/weapons/all.qh): independent weapon-entity slots per actor.</summary>
    public const int MaxWeaponSlots = 2;

    // (perf 2.1) which weapon ids have logged their first attack this process — the melt-correlation
    // breadcrumb above. Process-lifetime is fine: a fresh match in the same process has warm caches anyway.
    private static readonly System.Collections.Generic.HashSet<int> _firstFireLogged = new();

    // (perf 2.1) per-weapon WrThink scope names ("wf.hagar", …), cached so the per-tick sample never
    // allocates — the melt trees then name the weapon directly instead of a generic stage.
    private static readonly System.Collections.Generic.Dictionary<int, string> _wfScopeName = new();
    private static string WfScopeName(Weapon w)
    {
        if (!_wfScopeName.TryGetValue(w.RegistryId, out string? n))
            _wfScopeName[w.RegistryId] = n = "wf." + (string.IsNullOrEmpty(w.NetName) ? w.RegistryId.ToString() : w.NetName);
        return n;
    }

    /// <summary>
    /// Drive the player's weapon slots for one server tick (QC <c>W_WeaponFrame</c> per slot). Reads the
    /// current attack buttons from <paramref name="input"/>, advances the switch + fire state machine, and
    /// fires the active weapon's primary/secondary as the refire gate allows.
    /// </summary>
    /// <param name="player">The owning player actor (must be alive — the caller gates on game-stopped/dead).</param>
    /// <param name="input">This tick's input (supplies <see cref="IMovementInput.ButtonAttack1"/>/2).</param>
    public static void Frame(Entity player, IMovementInput? input)
    {
        // QC: dead players can't use weapons (W_WeaponFrame returns if health < 1). The caller already gates
        // on game-stopped + dead, but stay defensive.
        if (player.GetResource(ResourceType.Health) < 1f)
            return;

        bool buttonAtck = input?.ButtonAttack1 ?? false;
        bool buttonAtck2 = input?.ButtonAttack2 ?? false;
        // PHYS_INPUT_BUTTON_ZOOM | PHYS_INPUT_BUTTON_ZOOMSCRIPT (rifle.qc:16): the rifle re-aims from the eye
        // while scoped. Mirrored onto the slot below alongside the fire buttons so the fire path can read it.
        bool buttonZoom = input?.ButtonZoom ?? false;

        // QC W_WeaponFrame (weaponsystem.qc:608-618): publish the +hook / offhand-fire button onto the player so
        // the offhand-weapon think runs this tick — the grapple hook, the offhand blaster, and the nade
        // prime/throw all read Entity.OffhandFirePressed from their PlayerPreThink hook (the headless analogue of
        // the engine offhand_think dispatch). QC gates the key on `!actor.vehicle` and weaponUseForbidden; the
        // forbidden gate below zeroes it alongside the fire buttons. The caller (WeaponThink) already returns while
        // seated, but honor the `!actor.vehicle` gate explicitly here too so this PostThink write can never leave a
        // stale un-gated press for a future seated consumer (matching the authoritative publish in OnClientMove).
        player.OffhandFirePressed = player.Vehicle is null && (input?.ButtonHook ?? false);

        // QC W_WeaponFrame: weaponUseForbidden(actor) zeroes the fire buttons (round active-but-not-started OR
        // the ForbidWeaponUse mutator hook) but still allows weapon switching. The round_handler / Forbid
        // WeaponUse state isn't reachable from this driver, but honor the contract: a forbidden frame must not
        // fire while the switch machine below keeps running.
        if (WeaponUseForbidden(player))
            buttonAtck = buttonAtck2 = player.OffhandFirePressed = false;

        // QC W_WeaponFrame: if weaponLocked(actor) && state != WS_CLEAR -> run ONLY w_ready (become ready, no
        // fire, no switch) and return. weaponLocked covers game_stopped / player_blocked / Frozen / LockWeapon;
        // the caller already gates game-stopped, so the reachable piece here is the freeze (STAT(FROZEN) or the
        // STATUSEFFECT_Frozen status effect — QC PHYS_FROZEN). A frozen/blocked player can't fire.
        if (WeaponLocked(player))
        {
            for (int s = 0; s < 1; ++s)
            {
                WeaponSlotState lockedSt = player.WeaponState(new WeaponSlot(s));
                if (lockedSt.State != WeaponFireState.Clear)
                {
                    BecomeReadyOrClear(lockedSt, ready: true); // QC w_ready(...)
                    // QC w_ready also overwrites the pending weapon_think (weapon_thinkf WFRAME_IDLE 1000000 w_ready),
                    // so a re-fire stream pending when the freeze lands (Electro orbs, MG burst) does NOT resume
                    // when the player thaws.
                    lockedSt.WeaponThink = null;
                    lockedSt.WeaponNextThink = 0f;
                }
            }
            return;
        }

        // QC weaponentities[] loop. This port tracks a single active weapon carried by slot 0; higher slots
        // exist for fidelity / future dual-wield but are unpopulated, so we only drive slot 0 (driving an
        // empty slot is a no-op and we skip allocating its scratch state). When dual-wield lands, raise the
        // bound to MaxWeaponSlots and feed each slot its own weapon.
        const int populatedSlots = 1;
        for (int s = 0; s < populatedSlots; ++s)
        {
            var slot = new WeaponSlot(s);
            WeaponSlotState st = player.WeaponState(slot);

            // Slot 0 mirrors the player's single active weapon (this port tracks one active weapon, switched
            // immediately by Inventory.SwitchWeapon). The switch TARGET is the player's active/switch weapon;
            // the slot's own CurrentWeaponId is moved toward it by the state machine below (so a fresh equip /
            // a weapon change re-runs wr_setup + resets the slot).
            {
                st.SwitchWeaponId = player.ActiveWeaponId;
                if (st.CurrentWeaponId < 0)
                    st.CurrentWeaponId = player.ActiveWeaponId; // first equip: latch immediately
            }

            // ---- weapon switch state machine (QC the "Change weapon" switch in W_WeaponFrame) ----
            // Resolves to the now-current weapon (after applying any raise/drop transition this tick).
            // (perf 2.1) wf.* stage scopes: the census's 90-146 ms mp.weapon melts needed the stage named
            // (switch vs scheduled think vs the WrThink fire path) — see the perf-campaign doc.
            Weapon? weapon;
            using (VortexArena.Common.Diagnostics.Prof.Sample("wf.switch"))
                weapon = DriveWeaponSwitch(player, slot, st);

            // No weapon equipped in this slot -> nothing to fire (QC m_weapon == WEP_Null branch).
            if (weapon is null)
            {
                if (st.State != WeaponFireState.Clear)
                    BecomeReadyOrClear(st, ready: false);
                continue;
            }

            // Record the held buttons BEFORE running the scheduled think: QC's W_WeaponFrame invokes the
            // .weapon_think with the live button bitmask (button_atck | button_atck2<<1), and some scheduled
            // thinks re-arm an attack from inside it (Electro W_Electro_CheckAttack streams orbs while ATCK2 is
            // held; MachineGun W_MachineGun_Attack_Burst spaces rounds; the auto re-think keeps firing). Those
            // re-fire paths read st.ButtonAttack(2) via PrepareAttack, so the buttons must be live here.
            SetButtons(st, buttonAtck, buttonAtck2, buttonZoom);

            // ---- the scheduled weapon think (QC: fire the .weapon_think when weapon_nextthink elapses) ----
            // This drives the animtime timer: a fired weapon scheduled "become READY" after its animtime; a
            // raise/drop scheduled the matching transition. Run it BEFORE the fire decision so a shot whose
            // animtime just elapsed is ready to fire again this tick (QC orders the think after wr_think, but
            // the net effect on the READY gate is the same once ATTACK_FINISHED is the authority). The pending
            // indicator is the think delegate itself (a 0 next-think time at sim time 0 is still valid).
            if (st.WeaponThink is not null
                && Api.Clock.Time + WeaponFrameTime() * 0.5f >= st.WeaponNextThink)
            {
                var think = st.WeaponThink;
                st.WeaponThink = null;
                st.WeaponNextThink = 0f;
                using (VortexArena.Common.Diagnostics.Prof.Sample("wf.sched"))
                    think(player, slot);
            }

            // ---- call wr_think with the held fire mode(s) (QC e.wr_think(..., button bits)) ----
            // QC passes a combined ATK|ATK2<<1 bitmask in ONE wr_think call every tick; the C# WrThink takes
            // a single FireMode, so we record which buttons are held for this (actor, slot) and split it:
            //   * WrThink(Primary) is called EVERY tick — it carries the weapon's per-tick upkeep that QC runs
            //     at the top of wr_think regardless of buttons (Vortex charge regen, Crylink link-join, Hook
            //     reel-in/cooldown, Devastator guiding, Arc beam cooldown). Whether it actually FIRES is the
            //     weapon's call: a discrete weapon gates its shot on Weapon.PrepareAttack (which returns false
            //     unless ATK is held AND the refire timer/ammo allow), and a continuous weapon (Arc beam,
            //     Hook, Tuba) checks st.ButtonAttack itself — so nothing fires while ATK is up.
            //   * WrThink(Secondary) is called only when ATK2 is held, so the secondary acts without
            //     double-running the shared top-of-wr_think upkeep already done in the Primary call.
            // The recorded buttons are the authority PrepareAttack / the continuous weapons read.
            // (perf 2.1) First-use breadcrumb: the mp.weapon melts cluster like one-per-weapon-per-match —
            // a timestamped first-fire line correlates them with a specific weapon's cold path in the log.
            // PER PROCESS, not per match, and deliberately so: the latch is static and never cleared, and
            // the caches whose cold cost this is pointing at (effects, sounds, material pipelines) now
            // persist across maps too (cl_persist_asset_cache). A second map's first fire is already warm,
            // so re-arming this per match would just add a line with nothing behind it.
            if (buttonAtck && _firstFireLogged.Add(weapon.RegistryId))
                VortexArena.Common.Diagnostics.Log.Info($"[wf] first attack this session: {weapon.NetName}");
            using (VortexArena.Common.Diagnostics.Prof.Sample(WfScopeName(weapon)))
            {
                weapon.WrThink(player, slot, FireMode.Primary);
                if (buttonAtck2)
                    weapon.WrThink(player, slot, FireMode.Secondary);
            }
            ClearButtons(st);
        }
    }

    // --- per-(actor,slot) held-button context, so Weapon.PrepareAttack can check the fire button is down ---
    // (QC reads PHYS_INPUT_BUTTON_ATCK directly; the headless weapons get it via the active slot state set by
    // the driver around each WrThink call.)
    private static void SetButtons(WeaponSlotState st, bool atck, bool atck2, bool zoom = false)
    {
        st.ButtonAttack = atck;
        st.ButtonAttack2 = atck2;
        st.ButtonZoom = zoom;
    }

    private static void ClearButtons(WeaponSlotState st)
    {
        st.ButtonAttack = false;
        st.ButtonAttack2 = false;
        st.ButtonZoom = false;
    }

    /// <summary>
    /// QC the "Change weapon" switch in <c>W_WeaponFrame</c> (the WS_CLEAR/RAISE/DROP/READY cases): when the
    /// slot's switch target (<see cref="WeaponSlotState.SwitchWeaponId"/>) differs from what it's currently
    /// holding (<see cref="WeaponSlotState.CurrentWeaponId"/>), run the drop → clear → raise sequence. The
    /// switch-delay timing is 0 for now (T6 owns <c>switchdelay_raise/drop</c>), so a change takes a couple of
    /// ticks via the scheduled transitions; the WS_RAISE/WS_DROP states are the clean hook for that wave.
    /// Returns the weapon the slot currently holds (after applying this tick's transition), or null if empty.
    /// </summary>
    private static Weapon? DriveWeaponSwitch(Entity player, WeaponSlot slot, WeaponSlotState st)
    {
        int targetId = st.SwitchWeaponId;
        bool changing = targetId >= 0 && targetId != st.CurrentWeaponId;

        if (!changing)
        {
            // Settled. If we hold a weapon but the slot is still CLEAR (a fresh equip), raise it now.
            if (st.CurrentWeaponId >= 0 && st.State == WeaponFireState.Clear)
            {
                Weapon nw = Registry<Weapon>.ById(st.CurrentWeaponId);
                BeginRaise(player, slot, st, nw);
                return nw;
            }
            return CurrentWeaponOf(st);
        }

        // A switch is pending: QC switch on this.state.
        switch (st.State)
        {
            case WeaponFireState.InUse:
            case WeaponFireState.Raise:
                // Busy mid fire/raise: defer the switch (QC just breaks); keep firing the current weapon.
                return CurrentWeaponOf(st);

            case WeaponFireState.Drop:
                // In the dropping phase we can switch at any time (QC WS_DROP): just latch the in-progress switch.
                st.SwitchingWeaponId = targetId;
                return CurrentWeaponOf(st);

            case WeaponFireState.Clear:
            {
                // End switching: equip the new weapon and start its raise (QC the WS_CLEAR case).
                if (st.CurrentWeaponId >= 0)
                    player.LastWeaponId = st.CurrentWeaponId; // W_LastWeapon bookkeeping
                Weapon newwep = Registry<Weapon>.ById(targetId);
                st.SwitchingWeaponId = targetId;
                st.CurrentWeaponId = targetId;
                if (slot.Index == 0)
                {
                    player.ActiveWeaponId = targetId; // keep the single-weapon mirror in sync
                    player.SwitchWeaponId = targetId;
                }
                BeginRaise(player, slot, st, newwep);
                return newwep;
            }

            case WeaponFireState.Ready:
            default:
            {
                // Start switching: begin the drop (QC WS_READY). Schedule the transition to CLEAR after the
                // OUTGOING weapon's switchdelay_drop (QC weapon_thinkf(..., oldwep.switchdelay_drop, w_clear)), so
                // the new weapon can raise once the drop animation has played.
                st.SwitchingWeaponId = targetId;
                Weapon? oldwep = CurrentWeaponOf(st);
                // QC wr_gonethink: the weapon we're leaving should fire/release any state held (e.g. Hagar's loaded rockets).
                if (oldwep is not null)
                    oldwep.WrGoneThink(player, slot);
                float dropDelay = oldwep?.SwitchDelayDrop() ?? 0f;
                st.State = WeaponFireState.Drop;
                // Switching cancels any in-progress reload: scheduling the drop think here overwrites the pending
                // W_ReloadedAndReady think (which is what would have cleared Reloading), so the reload can never
                // finish. QC is safe because its reload guard is wframe==WFRAME_RELOAD and this weapon_thinkf just
                // overwrote wframe; mirror that by clearing the reload latch as the switch begins (otherwise the
                // stale latch blocks every future reload on this slot — Reloading would stay true forever).
                st.Reloading = false;
                ScheduleThink(st, dropDelay, static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Clear);
                return CurrentWeaponOf(st);
            }
        }
    }

    /// <summary>The weapon the slot currently holds (<see cref="WeaponSlotState.CurrentWeaponId"/>), or null.</summary>
    private static Weapon? CurrentWeaponOf(WeaponSlotState st)
        => (st.CurrentWeaponId >= 0 && st.CurrentWeaponId < Registry<Weapon>.Count)
            ? Registry<Weapon>.ById(st.CurrentWeaponId)
            : null;

    /// <summary>
    /// QC <c>the WS_CLEAR "end switching" branch</c> in W_WeaponFrame: set up the new weapon, seed its clip if
    /// it's reloadable, mark the slot RAISE, then schedule become-READY after the weapon's
    /// <c>switchdelay_raise</c>. The slot can't fire until READY (PrepareAttack gates on WS_READY), so the raise
    /// delay is a real switch-in cost matching QC.
    /// </summary>
    private static void BeginRaise(Entity player, WeaponSlot slot, WeaponSlotState st, Weapon newwep)
    {
        st.BulletCounter = 0;
        // A reload is per-weapon and must not survive a switch into a fresh weapon: clear the latch on raise so
        // the newly equipped weapon starts reloadable (QC the raise's weapon_thinkf overwrites wframe, ending any
        // WFRAME_RELOAD state). Without this, a switch landing here while Reloading was set would strand the latch.
        st.Reloading = false;
        newwep.WrSetup(player, slot); // QC newwep.wr_setup(...)
        st.State = WeaponFireState.Raise;

        // [W14b Stage 4] animdecide DRAW set-site. The WS_RAISE transition (this is the port's WS_CLEAR→WS_RAISE
        // "end switching" branch, weaponsystem.qc:539-561) is the weapon-raise moment; latch the DRAW upper-body
        // action so a remote player visibly raises the new weapon (window = ANIM_VEC(draw, 1, 3) -> 0.333s). Base
        // reserves ANIMACTION_DRAW in the getupperanim switch but never fires it; the design (LI3) wires it at this
        // raise transition. restart = true (a re-equip should restart the draw). A dead player's death overlay still
        // wins — GetUpperAnim gives DIE1/DIE2 absolute priority over any latched ACTIVE action — so a late raise can't
        // stomp death; only the slot-0 raise drives the (single) per-player upper action.
        if (slot.Index == 0)
        {
            float drawNow = Api.Services is not null ? Api.Clock.Time : 0f;
            var (act, start) = AnimDecide.SetAction(
                player.AnimUpperAction, player.AnimActionStart, AnimDecide.AnimUpperAction.Draw, drawNow, restart: true);
            player.AnimUpperAction = act;
            player.AnimActionStart = start;
        }

        // Seed our clip load to the load of the weapon we switched to, if it's reloadable (QC weaponsystem.qc:
        // 552-559): clip_load = weapon_load[newwep.id]; clip_size = reloading_ammo. Else clip_load = clip_size = 0.
        if ((newwep.SpawnFlags & WeaponFlags.Reloadable) != 0 && newwep.ReloadingAmmo() != 0f)
        {
            newwep.SeedClipIfFresh(st, newwep.RegistryId); // QC PutPlayerInServer: reloadable clips start full
            st.ClipLoad = Weapon.GetWeaponLoad(st, newwep.RegistryId);
            st.ClipSize = (int)newwep.ReloadingAmmo();
        }
        else
        {
            st.ClipLoad = st.ClipSize = 0;
        }

        // weapon_thinkf(actor, weaponentity, WFRAME_DONTCHANGE, newwep.switchdelay_raise, w_ready)
        float raiseDelay = newwep.SwitchDelayRaise();
        ScheduleThink(st, raiseDelay, static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Ready);
    }

    /// <summary>QC <c>w_ready</c>/<c>w_clear</c>: settle the slot to READY (has weapon) or CLEAR (empty).</summary>
    private static void BecomeReadyOrClear(WeaponSlotState st, bool ready)
        => st.State = ready ? WeaponFireState.Ready : WeaponFireState.Clear;

    /// <summary>
    /// QC <c>weapon_thinkf</c> (the scheduling half): defer <paramref name="think"/> until
    /// <c>time + delay</c>. The animtime/refire scaling is applied by the caller (PrepareAttack already folds
    /// in the weapon-rate factor). Overwrites any pending think (QC the last weapon_thinkf wins).
    /// </summary>
    internal static void ScheduleThink(WeaponSlotState st, float delay, Action<Entity, WeaponSlot> think)
    {
        st.WeaponNextThink = Api.Clock.Time + MathF.Max(0f, delay);
        st.WeaponThink = think;
    }

    /// <summary>
    /// QC <c>weaponUseForbidden</c> (weaponsystem.qc:426): <c>round_handler_IsActive() &amp;&amp;
    /// !round_handler_IsRoundStarted()</c> — a round-based mode is in its pre-round grace window (warmup /
    /// countdown / end-delay), so the fire buttons are zeroed while switching stays allowed. The active
    /// round handler lives in the server <see cref="GameWorld"/>, out of reach of this headless Common
    /// driver, so the host wires it via <see cref="RoundFireForbidden"/>. (The <c>ForbidWeaponUse</c>
    /// mutator hook half is not modeled — no port mutator forbids weapon use; the dead/game-stopped gating
    /// is already done by the caller via <see cref="WeaponLocked"/>.)
    /// </summary>
    public static Func<Entity, bool>? RoundFireForbidden { get; set; }

    private static bool WeaponUseForbidden(Entity player) => RoundFireForbidden?.Invoke(player) ?? false;

    /// <summary>
    /// QC <c>weaponLocked</c> (weaponsystem.qc): the player can't fire at all — when locked the weapon system
    /// runs only <c>w_ready</c>. QC's full predicate is
    /// <c>(time &lt; game_starttime &amp;&amp; !sv_ready_restart_after_countdown) || game_stopped ||
    /// player_blocked || StatusEffects_active(Frozen) || LockWeapon</c>. The game-start/game-stopped halves are
    /// already gated by the caller; <c>player_blocked</c> and the <c>LockWeapon</c> mutator hook aren't modeled
    /// in the port yet. The reachable, gameplay-critical piece is the freeze — QC <c>PHYS_FROZEN</c>: the
    /// gametype freeze stat (<see cref="Entity.FrozenStat"/>, e.g. Freeze Tag) OR the
    /// <c>STATUSEFFECT_Frozen</c> status effect. A frozen player must not fire.
    /// </summary>
    private static bool WeaponLocked(Entity player)
    {
        if (player.FrozenStat != 0)
            return true;
        StatusEffectDef? frozen = StatusEffectsCatalog.Frozen;
        return frozen is not null && StatusEffectsCatalog.Has(player, frozen);
    }

    /// <summary>
    /// QC <c>this.weapon_frametime</c> (== the engine <c>frametime</c>, the tick length). Read from the sim
    /// clock; the ATTACK_FINISHED half-frame tolerance in PrepareAttack uses it.
    /// </summary>
    internal static float WeaponFrameTime()
        => Api.Services is null ? SimulationTicLengthFallback : Api.Clock.FrameTime;

    /// <summary>Fallback tick length (72 Hz) when no clock is wired (a bare unit test).</summary>
    private const float SimulationTicLengthFallback = 1f / 72f;
}
