// Port of the logic-gate / activator relay trigger families from qcsrc/common/mapobjects/trigger/:
//   flipflop.qc       -> trigger_flipflop
//   monoflop.qc       -> trigger_monoflop (+ MONOFLOP_FIXED)
//   multivibrator.qc  -> trigger_multivibrator
//   disablerelay.qc   -> trigger_disablerelay
//   relay_if.qc       -> trigger_relay_if (+ RELAYIF_NEGATE)
//   relay_teamcheck.qc-> trigger_relay_teamcheck (+ RELAYTEAMCHECK_NOTEAM/_INVERT)
//   relay_activators.qc-> relay_activate / relay_deactivate / relay_activatetoggle (+ generic_setactive)
//   gamestart.qc      -> trigger_gamestart
//   magicear.qc       -> trigger_magicear (string-match core; LIVE via the server Chat.Say pipeline)
//
// These are pure relay/timer entities: they fire their targets (MapMover.UseTargets, the port of
// SUB_UseTargets) on .use or on a schedule, with no volume. trigger_relay / target_relay / target_delay /
// trigger_delay already live in Triggers.cs (registered separately) — NOT re-ported here.
//
// QC's .use is 3-arg (this, actor, trigger); the port's EntityUse is 2-arg (self, activator), so the QC
// `trigger` param is dropped at the .use boundary. SUB_UseTargets only forwards `trigger` as the `trigger`
// arg of the downstream .use, which none of the ported targets read — so passing null here is faithful.

using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The logic-gate / activator-relay trigger entities (flipflop / monoflop / multivibrator / disablerelay /
/// relay_if / relay_teamcheck / relay_activate* / gamestart / magicear). Each setup is a spawnfunc registered
/// by <see cref="MapObjectsRegistry"/>. Faithful ports; the per-gate <c>.reset</c> bodies are installed where Base
/// installs them (flipflop/monoflop/disablerelay/relay_if/relay_teamcheck/relay_activate*) and driven by the live
/// GameWorld.ResetMapObjects pump (e.Reset, plus gamestart's e.Reset2). trigger_multivibrator deliberately gets no
/// reset (Base's spawnfunc installs none).
/// </summary>
public static class LogicGates
{
    // ===================================================================
    //  generic_setactive (mapobjects/triggers.qc) — shared by relay_activators
    // ===================================================================

    /// <summary>
    /// Port of <c>generic_setactive</c> (triggers.qc): set <paramref name="e"/>'s ACTIVE_* state to
    /// <paramref name="act"/>; ACTIVE_TOGGLE flips ACTIVE_ACTIVE&lt;-&gt;ACTIVE_NOT. The fallback relay_activators
    /// uses when a target has no <see cref="Entity.SetActive"/> pointer. (The netlinked variant only adds a
    /// SendFlags update, which is CSQC-only here, so generic_netlinked_setactive collapses to this.)
    /// </summary>
    public static void GenericSetActive(Entity e, int act)
    {
        if (act == MapMover.ActiveToggle)
            e.Active = e.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            e.Active = act;
    }

    // ===================================================================
    //  trigger_flipflop (flipflop.qc) — pass only every 2nd event
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_flipflop)</c> — a gate that lets only every second trigger event through.</summary>
    public static void FlipflopSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.GateState = (this_.SpawnFlags & MapMover.SpawnStartEnabled) != 0 ? 1 : 0;
        this_.Use = FlipflopUse;
        this_.ClassName = "trigger_flipflop";
        MapMover.IndexRegister(this_);
        // QC flipflop.qc:23: this.reset = spawnfunc_trigger_flipflop (a perfect resetter). Re-running the spawnfunc
        // re-asserts active + the START_ENABLED initial latch; the central ResetMapObjects pump drives it (e.Reset).
        this_.Reset = FlipflopSetup;
    }

    /// <summary>QC <c>flipflop_use</c>: toggle the latch; fire targets only when it flips ON.</summary>
    private static void FlipflopUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        self.GateState = self.GateState != 0 ? 0 : 1;
        if (self.GateState != 0)
            MapMover.UseTargets(self, actor, null);
    }

    // ===================================================================
    //  trigger_monoflop (monoflop.qc) — one event -> on, then off after .wait
    // ===================================================================

    public const int MonoflopFixed = 1 << 0; // MONOFLOP_FIXED

    /// <summary>
    /// <c>spawnfunc(trigger_monoflop)</c> — turns one trigger event into one "on" then one "off" event, separated
    /// by <c>.wait</c> seconds. MONOFLOP_FIXED uses the variant that ignores re-triggers while already on (the
    /// off-time is fixed at the first trigger); the default extends the off-time on every re-trigger.
    /// </summary>
    public static void MonoflopSetup(Entity this_)
    {
        if (this_.Wait == 0f)
            this_.Wait = 1f;
        this_.Use = (this_.SpawnFlags & MonoflopFixed) != 0 ? MonoflopFixedUse : MonoflopUse;
        this_.Think = MonoflopThink;
        this_.GateState = 0;
        this_.ClassName = "trigger_monoflop";
        MapMover.IndexRegister(this_);
        // QC monoflop.qc:64: this.reset = monoflop_reset. Driven by the central ResetMapObjects pump (e.Reset);
        // re-arms the latch so an ON-state monoflop snaps off on a round restart.
        this_.Reset = MonoflopReset;
    }

    /// <summary>QC <c>monoflop_use</c>: (re)arm the off-timer to time+wait every press; fire ON only on the rising edge.</summary>
    private static void MonoflopUse(Entity self, Entity actor)
    {
        self.NextThink = MapMover.Now() + self.Wait;
        self.Enemy = actor;
        if (self.GateState != 0)
            return;
        self.GateState = 1;
        MapMover.UseTargets(self, actor, null);
    }

    /// <summary>QC <c>monoflop_fixed_use</c>: ignore re-triggers while on; the off-timer is set once at the rising edge.</summary>
    private static void MonoflopFixedUse(Entity self, Entity actor)
    {
        if (self.GateState != 0)
            return;
        self.NextThink = MapMover.Now() + self.Wait;
        self.GateState = 1;
        self.Enemy = actor;
        MapMover.UseTargets(self, actor, null);
    }

    /// <summary>QC <c>monoflop_think</c>: the off-timer elapsed — clear the latch and fire the OFF event.</summary>
    private static void MonoflopThink(Entity self)
    {
        self.GateState = 0;
        MapMover.UseTargets(self, self.Enemy, null);
    }

    /// <summary>QC <c>monoflop_reset</c>: clear the latch + cancel the timer (dormant until reset infra exists).</summary>
    public static void MonoflopReset(Entity self)
    {
        self.GateState = 0;
        self.NextThink = 0f;
    }

    // ===================================================================
    //  trigger_multivibrator (multivibrator.qc) — free-running on/off oscillator
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(trigger_multivibrator)</c> — a free-running oscillator that repeatedly fires its targets,
    /// toggling on (<c>.wait</c> seconds) / off (<c>.respawntime</c> seconds). When targeted it can be toggled
    /// on/off; START_ENABLED assumes it starts on.
    /// </summary>
    public static void MultivibratorSetup(Entity this_)
    {
        if (this_.Wait == 0f)
            this_.Wait = 1f;
        if (this_.RespawnTimeMover == 0f)
            this_.RespawnTimeMover = this_.Wait;

        this_.GateState = 0;
        this_.Use = MultivibratorToggle;
        this_.Think = MultivibratorSendThink;
        this_.NextThink = MathF.Max(1f, MapMover.Now());
        this_.ClassName = "trigger_multivibrator";
        MapMover.IndexRegister(this_);

        // NOTE: Base spawnfunc(trigger_multivibrator) (multivibrator.qc:131-149) installs NO this.reset — unlike the
        // other gates it is NOT registered with the round-restart reset pump. multivibrator_reset is called inline
        // only for the targeted-init branch below. So we deliberately do NOT assign this_.Reset here (faithful).
        if (!string.IsNullOrEmpty(this_.TargetName))
            MultivibratorReset(this_);
    }

    /// <summary>
    /// QC <c>multivibrator_send</c>: compute the current phase of the wait/respawntime cycle, fire targets when
    /// the on/off state flips, and schedule the next think at the next phase boundary.
    /// </summary>
    private static void MultivibratorSend(Entity self)
    {
        float now = MapMover.Now();
        float period = self.Wait + self.RespawnTimeMover;
        float cyclestart = MathF.Floor((now + self.Phase) / period) * period - self.Phase;

        int newstate = now < cyclestart + self.Wait ? 1 : 0;

        if (self.GateState != newstate)
            MapMover.UseTargets(self, self, null);
        self.GateState = newstate;

        if (self.GateState != 0)
            self.NextThink = cyclestart + self.Wait + 0.01f;
        else
            self.NextThink = cyclestart + self.Wait + self.RespawnTimeMover + 0.01f;
    }

    /// <summary>QC <c>multivibrator_send_think</c>.</summary>
    private static void MultivibratorSendThink(Entity self) => MultivibratorSend(self);

    /// <summary>QC <c>multivibrator_toggle</c>: start the oscillator if stopped, else stop it (firing OFF if on).</summary>
    private static void MultivibratorToggle(Entity self, Entity actor)
    {
        if (self.NextThink == 0f)
        {
            MultivibratorSend(self);
        }
        else
        {
            if (self.GateState != 0)
            {
                MapMover.UseTargets(self, actor, null);
                self.GateState = 0;
            }
            self.NextThink = 0f;
        }
    }

    /// <summary>QC <c>multivibrator_reset</c>: a targeted oscillator waits for a trigger unless START_ENABLED.</summary>
    public static void MultivibratorReset(Entity self)
    {
        if ((self.SpawnFlags & MapMover.SpawnStartEnabled) == 0)
            self.NextThink = 0f; // wait for a trigger event
        else
            self.NextThink = MathF.Max(1f, MapMover.Now());
    }

    // ===================================================================
    //  trigger_disablerelay (disablerelay.qc) — toggle ACTIVE<->NOT on all named targets
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_disablerelay)</c> — flips every target's <c>.active</c> ACTIVE&lt;-&gt;NOT.</summary>
    public static void DisableRelaySetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = DisableRelayUse;
        this_.ClassName = "trigger_disablerelay";
        MapMover.IndexRegister(this_);
        // QC disablerelay.qc:28: this.reset = spawnfunc_trigger_disablerelay (resets fully) — re-asserts active.
        // Driven by the central ResetMapObjects pump (e.Reset).
        this_.Reset = DisableRelaySetup;
    }

    /// <summary>
    /// QC <c>trigger_disablerelay_use</c>: for every entity named by <c>.target</c>, flip ACTIVE_ACTIVE-&gt;
    /// ACTIVE_NOT (count a) and ACTIVE_NOT-&gt;ACTIVE_ACTIVE (count b). It is a usage error if the targets were
    /// all-on or all-off (so the flip would be a no-toggle); QC logs that via LOG_INFO.
    /// </summary>
    private static void DisableRelayUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        int a = 0, b = 0;
        foreach (Entity e in MapMover.FindByTargetName(self.Target).ToList())
        {
            if (e.Active == MapMover.ActiveActive)
            {
                e.Active = MapMover.ActiveNot;
                ++a;
            }
            else if (e.Active == MapMover.ActiveNot)
            {
                e.Active = MapMover.ActiveActive;
                ++b;
            }
        }

        // QC: if((!a) == (!b)) — both zero or both non-zero is a misconfiguration.
        if ((a == 0) == (b == 0))
            Log.Info($"Invalid use of trigger_disablerelay: {a} relays were on, {b} relays were off!");
    }

    // ===================================================================
    //  trigger_relay_if (relay_if.qc) — cvar-compare gate
    // ===================================================================

    public const int RelayIfNegate = 1 << 0; // RELAYIF_NEGATE

    /// <summary>
    /// <c>spawnfunc(trigger_relay_if)</c> — fires its targets only when the cvar named by <c>.netname</c> equals
    /// the cvar named by <c>.message</c> (RELAYIF_NEGATE inverts the test).
    /// </summary>
    public static void RelayIfSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = RelayIfUse;
        this_.ClassName = "trigger_relay_if";
        MapMover.IndexRegister(this_);
        // QC relay_if.qc:25: this.reset = spawnfunc_trigger_relay_if (resets fully) — re-asserts active.
        // Driven by the central ResetMapObjects pump (e.Reset).
        this_.Reset = RelayIfSetup;
    }

    /// <summary>QC <c>trigger_relay_if_use</c>: compare cvar_string(netname) vs cvar_string(message).</summary>
    private static void RelayIfUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        bool n = CvarString(self.NetName) == CvarString(self.Message);
        if ((self.SpawnFlags & RelayIfNegate) != 0)
            n = !n;

        if (n)
            MapMover.UseTargets(self, actor, null);
    }

    // ===================================================================
    //  trigger_relay_teamcheck (relay_teamcheck.qc) — team gate
    // ===================================================================

    public const int RelayTeamCheckNoTeam = 1 << 0; // RELAYTEAMCHECK_NOTEAM
    public const int RelayTeamCheckInvert = 1 << 1; // RELAYTEAMCHECK_INVERT

    /// <summary>
    /// <c>spawnfunc(trigger_relay_teamcheck)</c> — fires its targets only when the activator's team matches
    /// (or, with RELAYTEAMCHECK_INVERT, differs from) the entity's <c>.team</c>; a teamless activator fires only
    /// when RELAYTEAMCHECK_NOTEAM is set.
    /// </summary>
    public static void RelayTeamCheckSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        // QC relay_teamcheck.qc:38: this.team_saved = this.team; IL_PUSH(g_saved_team, this) — captured so the
        // reset can restore the original team gate after a round/match scramble.
        this_.TeamSaved = this_.Team;
        this_.Use = RelayTeamCheckUse;
        this_.ClassName = "trigger_relay_teamcheck";
        MapMover.IndexRegister(this_);
        // QC relay_teamcheck.qc:39: this.reset = trigger_relay_teamcheck_reset. Driven by the central
        // ResetMapObjects pump (e.Reset).
        this_.Reset = RelayTeamCheckReset;
    }

    /// <summary>QC <c>trigger_relay_teamcheck_reset</c> (relay_teamcheck.qc:31): re-assert active and restore the
    /// spawn-time team gate from <see cref="Entity.TeamSaved"/> on a round/match restart.</summary>
    private static void RelayTeamCheckReset(Entity self)
    {
        self.Active = MapMover.ActiveActive;
        self.Team = self.TeamSaved;
    }

    /// <summary>QC <c>trigger_relay_teamcheck_use</c>.</summary>
    private static void RelayTeamCheckUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        if (actor.Team != 0)
        {
            if ((self.SpawnFlags & RelayTeamCheckInvert) != 0)
            {
                // QC DIFF_TEAM(actor, this): no DiffTeam helper in Teams — inline the team mismatch.
                if (actor.Team != self.Team)
                    MapMover.UseTargets(self, actor, null);
            }
            else
            {
                if (Teams.SameTeam(actor, self))
                    MapMover.UseTargets(self, actor, null);
            }
        }
        else
        {
            if ((self.SpawnFlags & RelayTeamCheckNoTeam) != 0)
                MapMover.UseTargets(self, actor, null);
        }
    }

    // ===================================================================
    //  relay_activate / relay_deactivate / relay_activatetoggle (relay_activators.qc)
    // ===================================================================

    /// <summary>QC <c>relay_activators_init</c>: a relay that sets every named target's active state via .setactive.</summary>
    private static void RelayActivatorsInit(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = RelayActivatorsUse;
        MapMover.IndexRegister(this_);
        // QC relay_activators.qc:30: this.reset = relay_activators_init (doubles as reset) — re-asserts active.
        // Driven by the central ResetMapObjects pump (e.Reset).
        this_.Reset = RelayActivatorsInit;
    }

    /// <summary><c>spawnfunc(relay_activate)</c> — sets named targets ACTIVE.</summary>
    public static void RelayActivateSetup(Entity this_)
    {
        this_.Cnt = MapMover.ActiveActive;
        this_.ClassName = "relay_activate";
        RelayActivatorsInit(this_);
    }

    /// <summary><c>spawnfunc(relay_deactivate)</c> — sets named targets NOT-active.</summary>
    public static void RelayDeactivateSetup(Entity this_)
    {
        this_.Cnt = MapMover.ActiveNot;
        this_.ClassName = "relay_deactivate";
        RelayActivatorsInit(this_);
    }

    /// <summary><c>spawnfunc(relay_activatetoggle)</c> — toggles named targets' active state.</summary>
    public static void RelayActivateToggleSetup(Entity this_)
    {
        this_.Cnt = MapMover.ActiveToggle;
        this_.ClassName = "relay_activatetoggle";
        RelayActivatorsInit(this_);
    }

    /// <summary>
    /// QC <c>relay_activators_use</c>: for every entity named by <c>.target</c>, call its <c>.setactive</c> with
    /// <c>.cnt</c> (the ACTIVE_* to set), falling back to <see cref="GenericSetActive"/> when it has none.
    /// </summary>
    private static void RelayActivatorsUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        foreach (Entity trg in MapMover.FindByTargetName(self.Target).ToList())
        {
            if (trg.SetActive is not null)
                trg.SetActive(trg, self.Cnt);
            else
                GenericSetActive(trg, self.Cnt);
        }
    }

    // ===================================================================
    //  trigger_gamestart (gamestart.qc) — fire targets at game start (or after .wait), then delete
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(trigger_gamestart)</c> — fires its targets at game start (or <c>.wait</c> seconds after
    /// <c>game_starttime</c>), then deletes itself. With no wait it fires once at map init (a deferred think).
    /// </summary>
    public static void GamestartSetup(Entity this_)
    {
        this_.Use = GamestartUse;
        this_.ClassName = "trigger_gamestart";
        MapMover.IndexRegister(this_);
        // QC gamestart.qc:13: this.reset2 = spawnfunc_trigger_gamestart — on a map reset, re-run the spawnfunc to
        // re-arm the deferred fire (the reset_map second pass drives .reset2 now). gamestart_use delete()s the
        // trigger after it first fires its targets, so this only re-arms a trigger that is still alive at the reset.
        this_.Reset2 = GamestartSetup;

        if (this_.Wait != 0f)
        {
            // QC: setthink(adaptor_think2use); nextthink = game_starttime + wait.
            this_.Think = AdaptorThink2Use;
            this_.NextThink = GameStartTime + this_.Wait;
        }
        else
        {
            // QC: InitializeEntity(adaptor_think2use, INITPRIO_FINDTARGET) — fire after spawn settles. The port
            // has no InitializeEntity priority pass, so schedule a same-frame think (fires next sim step).
            this_.Think = AdaptorThink2Use;
            this_.NextThink = MapMover.Now();
        }
    }

    /// <summary>QC <c>adaptor_think2use</c>: invoke this entity's own <c>.use</c> with itself as actor.</summary>
    private static void AdaptorThink2Use(Entity self) => self.Use?.Invoke(self, self);

    /// <summary>QC <c>gamestart_use</c>: fire targets, then remove self.</summary>
    private static void GamestartUse(Entity self, Entity actor)
    {
        MapMover.UseTargets(self, self, null);
        MapMover.RemoveEntity(self);
    }

    /// <summary>
    /// QC <c>game_starttime</c> — the match start time (countdown end). Backed by
    /// <see cref="GameStartTimeProvider"/>: a host wires it to the live world's countdown end (e.g.
    /// <c>GameWorld.GameStartTime</c>, the same value the RoundHandler/Warmup hold) the way it wires
    /// <c>StartItem.GameStartTimeProvider</c>/<c>Announcer.GameStartTime</c>; when unwired it reads 0 (the
    /// headless t=0 start). Setting it overrides the provider with a fixed value for tests/standalone use.
    /// </summary>
    public static float GameStartTime
    {
        get => _gameStartTimeOverride ?? GameStartTimeProvider?.Invoke() ?? 0f;
        set => _gameStartTimeOverride = value;
    }

    private static float? _gameStartTimeOverride;

    /// <summary>
    /// Host-wired source of the live match countdown-end time (QC <c>game_starttime</c>). Set by the host at boot
    /// (e.g. <c>LogicGates.GameStartTimeProvider = () =&gt; GameWorld.GameStartTime</c>) so the <c>.wait</c> branch
    /// of <see cref="GamestartSetup"/> schedules relative to the real countdown end rather than 0. A direct set of
    /// <see cref="GameStartTime"/> takes precedence over this provider.
    /// </summary>
    public static System.Func<float>? GameStartTimeProvider { get; set; }

    // ===================================================================
    //  trigger_magicear (magicear.qc) — chat pattern match -> SUB_UseTargets / text replace
    //  LIVE: the server Chat.Say pipeline (XonoticGodot.Server/Chat.cs) calls MagicEarProcessAllEars on every
    //  non-empty player say (chat.qc:75-76), so registered ears now see messages on the live path. The TUBA
    //  melody branch is out of scope (W_Tuba_HasPlayed). The magicears list is linked via Enemy and walked
    //  exactly like QC's trigger_magicear_processmessage_forallears.
    // ===================================================================

    public const int MagicEarIgnoreSay = 1 << 0;             // MAGICEAR_IGNORE_SAY
    public const int MagicEarIgnoreTeamSay = 1 << 1;         // MAGICEAR_IGNORE_TEAMSAY
    public const int MagicEarIgnoreTell = 1 << 2;            // MAGICEAR_IGNORE_TELL
    public const int MagicEarIgnoreInvalidTell = 1 << 3;     // MAGICEAR_IGNORE_INVALIDTELL
    public const int MagicEarReplaceWholeMessage = 1 << 4;   // MAGICEAR_REPLACE_WHOLE_MESSAGE
    public const int MagicEarReplaceOutside = 1 << 5;        // MAGICEAR_REPLACE_OUTSIDE
    public const int MagicEarContinue = 1 << 6;              // MAGICEAR_CONTINUE
    public const int MagicEarNoDecolorize = 1 << 7;          // MAGICEAR_NODECOLORIZE
    public const int MagicEarTuba = 1 << 8;                  // MAGICEAR_TUBA (out of scope)
    public const int MagicEarTubaExactPitch = 1 << 9;        // MAGICEAR_TUBA_EXACTPITCH (out of scope)

    /// <summary>QC <c>magicears</c> — the head of the magicear linked list (each links the next via .enemy).</summary>
    public static Entity? MagicEars;

    /// <summary>QC <c>magicear_matched</c> — set by the last <see cref="MagicEarProcessMessage"/> call.</summary>
    public static bool MagicEarMatched;

    /// <summary><c>spawnfunc(trigger_magicear)</c> — register a chat-trigger ear (live: driven by Chat.Say).</summary>
    public static void MagicEarSetup(Entity this_)
    {
        this_.Enemy = MagicEars;
        MagicEars = this_;
        this_.ClassName = "trigger_magicear";
        MapMover.IndexRegister(this_);

        // QC: --this.movedir_x — map to tuba instrument numbers (kept for fidelity even though TUBA is skipped).
        System.Numerics.Vector3 md = this_.MoveDir;
        md.X -= 1f;
        this_.MoveDir = md;
    }

    /// <summary>
    /// Port of <c>trigger_magicear_processmessage</c> (magicear.qc): match <paramref name="msgin"/> against the
    /// ear's <c>.message</c> pattern (the *pattern* / *pattern / pattern* / exact branches), and on a match fire
    /// the ear's targets (when in-range) and return the replacement text. Sets <see cref="MagicEarMatched"/>.
    /// The TUBA note-sequence branch is out of scope (returns msgin unchanged for a TUBA ear).
    /// </summary>
    public static string MagicEarProcessMessage(Entity ear, Entity source, int teamsay, Entity? privatesay, string msgin)
    {
        MagicEarMatched = false;

        bool sourceIsPlayer = (source.Flags & EntFlags.Client) != 0 && !MapMover.IsDead(source);
        bool dotrigger = sourceIsPlayer
            && (ear.ImpulseRadius == 0f
                || (source.Origin - ear.Origin).Length() <= ear.ImpulseRadius);
        bool domatch = (ear.SpawnFlags & MagicEarReplaceOutside) != 0 || dotrigger;

        if (!domatch)
            return msgin;

        if (string.IsNullOrEmpty(msgin))
        {
            // TUBA mode (an empty say message — fired from W_Tuba_NoteOff). Only a MAGICEAR_TUBA ear listens for
            // a played melody; every other ear ignores the empty message. magicear.qc:19-46.
            if ((ear.SpawnFlags & MagicEarTuba) == 0)
                return msgin;

            // ear.movedir = (instrument+1, mintempo, maxtempo); MagicEarSetup already did --movedir.x so movedir.x
            // is the instrument index (-1 = any). EXACTPITCH disables transposition (ignorePitch = !exactpitch).
            System.Numerics.Vector3 md = ear.MoveDir;
            bool ignorePitch = (ear.SpawnFlags & MagicEarTubaExactPitch) == 0;

            // Every weapon slot must match the melody (QC loops slots and returns on the first miss). A bot/dead
            // source can still match a MAGICEAR_REPLACE_OUTSIDE ear (domatch passed above).
            for (int slot = 0; slot < WeaponFireDriver.MaxWeaponSlots; ++slot)
            {
                if (!Tuba.HasPlayed(source, new WeaponSlot(slot), ear.Message, (int)md.X, ignorePitch, md.Y, md.Z))
                    return msgin;
            }

            MagicEarMatched = true;

            if (dotrigger)
            {
                // QC blanks .message so SUB_UseTargets doesn't centerprint the pattern, fires, then restores it.
                string savemessage = ear.Message;
                ear.Message = "";
                MapMover.UseTargets(ear, source, null);
                ear.Message = savemessage;
            }

            return !string.IsNullOrEmpty(ear.NetName) ? ear.NetName : msgin;
        }

        if ((ear.SpawnFlags & MagicEarTuba) != 0) // ENOTUBA
            return msgin;

        if (privatesay is not null)
        {
            if ((ear.SpawnFlags & MagicEarIgnoreTell) != 0)
                return msgin;
        }
        else
        {
            if (teamsay == 0 && (ear.SpawnFlags & MagicEarIgnoreSay) != 0)
                return msgin;
            if (teamsay > 0 && (ear.SpawnFlags & MagicEarIgnoreTeamSay) != 0)
                return msgin;
            if (teamsay < 0 && (ear.SpawnFlags & MagicEarIgnoreInvalidTell) != 0)
                return msgin;
        }

        int matchstart = -1;
        string pattern = ear.Message;
        int l = pattern.Length;

        // QC strdecolorize — the port has no color codes in chat yet, so msg == msgin (NODECOLORIZE is a no-op).
        string msg = msgin;

        string s;
        if (pattern.StartsWith("*", System.StringComparison.Ordinal))
        {
            if (pattern.EndsWith("*", System.StringComparison.Ordinal) && pattern.Length >= 2)
            {
                // two wildcards: *pattern* — substring(message, 1, -2)
                s = pattern.Substring(1, pattern.Length - 2);
                l -= 2;
                if (msg.IndexOf(s, System.StringComparison.Ordinal) >= 0)
                    matchstart = -2; // we use strreplace on s
            }
            else
            {
                // match at start: *pattern -> substring(message, 1, -1)
                s = pattern.Substring(1);
                --l;
                if (l <= msg.Length && msg.Substring(msg.Length - l) == s)
                    matchstart = msg.Length - l;
            }
        }
        else
        {
            if (pattern.EndsWith("*", System.StringComparison.Ordinal))
            {
                // match at end: pattern* -> substring(message, 0, -2)
                s = pattern.Substring(0, pattern.Length - 1);
                --l;
                if (l <= msg.Length && msg.Substring(0, l) == s)
                    matchstart = 0;
            }
            else
            {
                // full match
                s = pattern;
                if (msg == pattern)
                    matchstart = 0;
            }
        }

        if (matchstart == -1) // no match
            return msgin;

        MagicEarMatched = true;

        if (dotrigger)
        {
            // QC blanks .message so SUB_UseTargets doesn't centerprint the pattern, fires, then restores it.
            string savemessage = ear.Message;
            ear.Message = "";
            MapMover.UseTargets(ear, source, null);
            ear.Message = savemessage;
        }

        if ((ear.SpawnFlags & MagicEarReplaceWholeMessage) != 0)
        {
            return ear.NetName;
        }
        else if (!string.IsNullOrEmpty(ear.NetName))
        {
            if (matchstart < 0)
                return msg.Replace(s, ear.NetName); // QC strreplace(s, netname, msg)
            return string.Concat(
                msg.Substring(0, matchstart),
                ear.NetName,
                msg.Substring(matchstart + l));
        }
        else
        {
            return msgin;
        }
    }

    /// <summary>
    /// Port of <c>trigger_magicear_processmessage_forallears</c>: run <paramref name="msgin"/> through every
    /// registered ear (walking <see cref="MagicEars"/> via <see cref="Entity.Enemy"/>), short-circuiting on the
    /// first match unless that ear has MAGICEAR_CONTINUE. Returns the (possibly replaced) message. Called live by
    /// the server Chat.Say pipeline for every non-empty player say.
    /// </summary>
    public static string MagicEarProcessAllEars(Entity source, int teamsay, Entity? privatesay, string msgin)
    {
        for (Entity? ear = MagicEars; ear is not null; ear = ear.Enemy)
        {
            string msgout = MagicEarProcessMessage(ear, source, teamsay, privatesay, msgin);
            if ((ear.SpawnFlags & MagicEarContinue) == 0 && MagicEarMatched)
                return msgout;
            msgin = msgout;
        }
        return msgin;
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>QC <c>cvar_string(name)</c> via the facade (empty when no facade / unset).</summary>
    private static string CvarString(string? name)
        => Api.Services is null || string.IsNullOrEmpty(name) ? "" : Api.Cvars.GetString(name);
}
