using XonoticGodot.Common.Services;

namespace XonoticGodot.Net;

/// <summary>
/// Server→client replication of the movement cvars (DP's <c>movevars_*</c> / Xonotic's <c>sv_*</c> physics
/// set). The client predicts with the SAME deterministic movement sim as the server
/// (<c>MovementParameters.FromCvars</c>), so its prediction only matches authority if it reads the same
/// <c>sv_*</c> values. Stock defaults already agree, but a mid-match physics/mutator change (a different
/// <c>physics*.cfg</c>, <c>g_movement_highspeed</c>, an XPM ruleset vote) would silently desync prediction —
/// this block carries the server's live values so the client stamps them into its own cvar store.
///
/// Sent owner-only and <strong>only when changed</strong> (gated by a content <see cref="Hash"/>), so the
/// steady-state cost is a single bool per snapshot. The value list is exactly the cvar set
/// <c>MovementParameters.FromCvars</c> consumes (keep <see cref="MovementCvars"/> in sync with it), serialized
/// positionally — no names on the wire.
/// </summary>
public static class MoveVarsBlock
{
    /// <summary>
    /// The <c>sv_*</c> (and a couple of <c>g_*</c>) movement cvars the deterministic predictor reads, in wire
    /// order. MUST stay in sync with <c>XonoticGodot.Common.Physics.MovementParameters.FromCvars</c> AND with
    /// <c>MovementParameters.FromValues</c> (which decodes this vector positionally). Sent positionally as
    /// floats (covers ints/bools, and NaN for the uncapped jumpspeedcaps). APPEND-ONLY — never reorder
    /// (Apply/FromValues are prefix-stable across versions).
    /// </summary>
    public static readonly string[] MovementCvars =
    {
        "sv_maxspeed", "sv_accelerate", "sv_friction", "sv_friction_slick", "sv_stopspeed",
        "sv_slickaccelerate", "sv_friction_on_land",
        "sv_maxairspeed", "sv_airaccelerate", "sv_airaccel_qw", "sv_airstrafeaccel_qw",
        "sv_airaccel_qw_stretchfactor", "sv_airspeedlimit_nonqw", "sv_airaccel_sideways_friction",
        "sv_maxairstrafespeed", "sv_airstrafeaccelerate", "sv_airstopaccelerate", "sv_airstopaccelerate_full",
        "sv_aircontrol", "sv_aircontrol_flags", "sv_aircontrol_power", "sv_aircontrol_penalty",
        "sv_warsowbunny_turnaccel", "sv_warsowbunny_airforwardaccel", "sv_warsowbunny_topspeed",
        "sv_warsowbunny_accel", "sv_warsowbunny_backtosideratio",
        "sv_jumpvelocity", "sv_jumpvelocity_crouch", "sv_jumpspeedcap_min", "sv_jumpspeedcap_max",
        "sv_jumpspeedcap_max_disable_on_ramps", "sv_track_canjump", "sv_doublejump", "sv_jumpstep",
        "sv_gravity", "sv_stepheight", "sv_gameplayfix_stepdown", "sv_gameplayfix_stepdown_maxspeed",
        "sv_wallfriction",
        // ---- v7 additions (T54): the rest of the stats.qh MOVEVARS breadth the shared sim consults. ----
        "g_movement_highspeed",            // STAT(MOVEVARS_HIGHSPEED), player.qc:47 — unset reads 1 (see Capture)
        "g_movement_highspeed_q3_compat",  // the q3-compat maxspd_mod fold selector (player.qc:52-65), stock 0
        "sv_gameplayfix_nudgeoutofsolid",  // read ambiently by PlayerPhysics.NudgeOutOfSolid — unset reads 1 (DP default ON)
        "sv_wallclip",                     // STAT(MOVEVARS_WALLCLIP) (stats.qh:448) — no cfg sets it, default 0
        "sv_nostep",                       // STAT(NOSTEP) (stats.qh:235), default 0
        "sv_slick_applygravity",           // STAT(SLICK_APPLYGRAVITY) (stats.qh:365), default 0
        // ---- port extension: step-up vertical-velocity limiter (read by PlayerPhysics.ApplyStepUpSpeedClamp).
        //      Replicated so a remote client's prediction tames the step "launch" identically to the server.
        //      Unset defaults are NON-ZERO (scale→1, max→-1) → see AbsentDefaults (verbatim Raw-tail decode). ----
        "sv_step_upspeed_scale",           // multiply positive velocity.z surviving a step-up (1 = vanilla)
        "sv_step_upspeed_max",             // hard cap (u/s) on that upward velocity (-1 = disabled)
        // ---- v8: the air-strafe accel limiter (QC MOVEFLAG_Q2AIRACCELERATE — Base replicates it inside the
        //      moveflags stat, stats.qh:401, precisely so prediction agrees with the server on the accel step). ----
        "sv_gameplayfix_q2airaccelerate",  // wishspeed0 := clamped wishspeed inside PM_Accelerate — default 1
        // ---- v9: soft player collision (PORT EXTENSION). Read ambiently by PlayerPhysics.PlayerClipFilter —
        //      replicated so a remote client's prediction picks the same movement clip filter (players
        //      pass-through vs stock solid) as the authority. Unset default is ON (see AbsentDefaults). ----
        "sv_player_softcollision",         // players pass through each other; server pushes overlaps apart — default 1
        // Deliberately NOT replicated: MOVEVARS_TICRATE/TIMESCALE (the port advertises the tick rate at
        // handshake and ships dt per InputCommand), MOVEVARS_ENTGRAVITY (per-entity, not a cvar),
        // MOVEVARS_CL_TRACK_CANJUMP (a client→server cvar loopback — rides the sentcvar channel instead).
    };

    /// <summary>The number of replicated movement cvars.</summary>
    public static int Count => MovementCvars.Length;

    /// <summary>
    /// Absent-cvar default for the movement cvars whose <c>MovementParameters.FromCvars</c> fallback is NON-ZERO
    /// AND which decode through <c>FromValues</c>' verbatim <c>Raw</c>/<c>B</c> path (present-slot-is-authoritative).
    ///
    /// WHY this exists (the unregistered-cvar asymmetry): many <c>sv_*</c> movement cvars are NOT registered in the
    /// port's server cvar table (see <c>XonoticGodot.Server.Cvars.Defaults</c> — only <c>sv_gravity</c> is; the rest
    /// of the per-tick tunables intentionally live only as <c>FromCvars</c> fallbacks). On a default-config server
    /// those cvars are absent, so a bare <c>GetFloat</c> in <see cref="CaptureOne"/> reads <b>0</b>. But the server's
    /// OWN physics reads the SAME absent cvar through <c>FromCvars</c>, which — now that its per-cvar fallback is
    /// gated on cvar EXISTENCE (<c>CvarRaw</c>/<c>CvarBool</c>), not value==0 — resolves the stock port default
    /// (e.g. <c>sv_airaccel_qw</c> → -0.8, <c>sv_gameplayfix_stepdown</c> → 2, <c>sv_wallfriction</c> → 1). If we put
    /// the bare 0 on the wire, the client stamps 0, its EXISTS-gated <c>FromCvars</c> reads the present "0" as a real
    /// configured 0, and <c>FromValues</c> (which treats a PRESENT wire slot as authoritative for these Raw/B fields)
    /// keeps the 0 — so client and server disagree → prediction rubber-bands in the default deployment.
    ///
    /// Fix: for an ABSENT cvar, emit the SAME stock default <c>FromCvars</c> resolves (sourced field-by-field below),
    /// so the wire carries the default and both sides agree. The magnitude (<c>F</c>) fields don't need an entry:
    /// <c>FromValues.F</c> already falls back to the port default on a wire-0, mirroring <c>FromCvars</c>' <c>Cvar</c>
    /// helper (value==0 → fallback), so an absent magnitude cvar is already symmetric at 0. Zero-default Raw/B fields
    /// also need no entry (Capture's GetFloat→0 already matches the 0 fallback). Names already special-cased in
    /// <see cref="CaptureOne"/> (g_movement_highspeed / sv_gameplayfix_nudgeoutofsolid → unset 1; the jumpspeedcaps →
    /// NaN) are deliberately ABSENT from this map — their branch handles the asymmetry already.
    ///
    /// Each default is taken verbatim from the matching <c>MovementParameters.FromCvars</c> fallback
    /// (= the <c>MovementParameters.Defaults</c> field). Keep them EXACTLY in lockstep — a drift reintroduces the
    /// desync in the other direction.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, float> AbsentDefaults = new()
    {
        // FromCvars  CvarRaw("airaccel_qw",      Defaults.AirAccelQW = -0.8)
        ["sv_airaccel_qw"] = -0.8f,
        // FromCvars  CvarRaw("airstrafeaccel_qw", Defaults.AirStrafeAccelQW = -0.95)
        ["sv_airstrafeaccel_qw"] = -0.95f,
        // FromCvars  CvarBool("jumpspeedcap_max_disable_on_ramps", Defaults.JumpSpeedCapMaxDisableOnRamps = true)
        ["sv_jumpspeedcap_max_disable_on_ramps"] = 1f,
        // FromCvars  CvarBool("jumpstep",        Defaults.JumpStep = true)
        ["sv_jumpstep"] = 1f,
        // FromCvars  (int)CvarRaw("gameplayfix_stepdown", Defaults.StepDown = 2)
        ["sv_gameplayfix_stepdown"] = 2f,
        // FromCvars  (int)CvarRaw("wallfriction", Defaults.WallFriction = 1)
        ["sv_wallfriction"] = 1f,
        // FromCvars  CvarRaw("step_upspeed_scale", Defaults.StepUpSpeedScale = 1) — verbatim Raw-tail decode
        ["sv_step_upspeed_scale"] = 1f,
        // FromCvars  CvarRaw("step_upspeed_max", Defaults.StepUpSpeedMax = -1) — -1 = disabled (uncapped)
        ["sv_step_upspeed_max"] = -1f,
        // FromCvars  CvarBool("gameplayfix_q2airaccelerate", Defaults.GameplayFixQ2AirAccelerate = true)
        ["sv_gameplayfix_q2airaccelerate"] = 1f,
        // FromCvars  CvarBool("player_softcollision", Defaults.PlayerSoftCollision = true) — port extension
        ["sv_player_softcollision"] = 1f,
    };

    /// <summary>Read the live movement-cvar values from the server's store, in <see cref="MovementCvars"/> order.</summary>
    public static float[] Capture(ICvarService cvars)
    {
        var v = new float[MovementCvars.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = CaptureOne(cvars, MovementCvars[i]);
        return v;
    }

    /// <summary>
    /// Read one movement cvar with the wire's value semantics. Three names need more than a bare GetFloat:
    /// <list type="bullet">
    ///   <item><c>g_movement_highspeed</c> / <c>sv_gameplayfix_nudgeoutofsolid</c>: their engine defaults are 1
    ///         (xonotic-server.cfg:586 / DP sv_gameplayfix default), but an UNSET cvar reads 0 via GetFloat —
    ///         so an empty store must capture 1, not silently turn the feature off for remote clients.</item>
    ///   <item><c>sv_jumpspeedcap_min/_max</c>: the stock value is the literal string <c>"nan"</c> (= cap
    ///         disabled). The wire is float, so NaN IS the disabled sentinel — map "nan"/unset → NaN and keep a
    ///         REAL 0 (the xdf/quake3/cpma presets) as 0. NaN survives serialization.</item>
    /// </list>
    /// </summary>
    public static float CaptureOne(ICvarService cvars, string name)
    {
        switch (name)
        {
            case "g_movement_highspeed":
            case "sv_gameplayfix_nudgeoutofsolid":
            {
                string s = cvars.GetString(name);
                return s.Length == 0 ? 1f : cvars.GetFloat(name);
            }
            case "sv_jumpspeedcap_min":
            case "sv_jumpspeedcap_max":
                return ParseNanFloat(cvars.GetString(name));
            default:
                // Unregistered-cvar asymmetry (see AbsentDefaults): an ABSENT movement cvar reads "" → GetFloat 0,
                // but the server's own FromCvars resolves the stock port default for it (EXISTS-gated CvarRaw/Bool).
                // For the Raw/B-decoded fields with a non-zero default, put that default on the wire so both sides
                // agree; everything else keeps the bare GetFloat (a genuine 0 default, or a registered live value).
                if (cvars.GetString(name).Length == 0 && AbsentDefaults.TryGetValue(name, out float dflt))
                    return dflt;
                return cvars.GetFloat(name);
        }
    }

    /// <summary>DP <c>atof("nan")</c> semantics for the jumpspeedcap cvars: ""/unset or "nan" → NaN (cap
    /// disabled), anything else parses as a float (a real 0 stays 0 — the xdf/quake3/cpma presets).</summary>
    public static float ParseNanFloat(string s)
    {
        if (s.Length == 0 || s.Equals("nan", System.StringComparison.OrdinalIgnoreCase))
            return float.NaN;
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : float.NaN;
    }

    /// <summary>
    /// Build a client's preset-RESOLVED movevar vector — the per-player half of QC
    /// <c>Physics_UpdateStats</c> (common/physics/player.qc:44-151): every preset-resolvable entry goes through
    /// <c>Physics_ClientOption</c> (<see cref="XonoticGodot.Common.Physics.PhysicsPreset.Resolve"/>) with the
    /// already-captured global value as the <c>autocvar_sv_*</c> fallback; global-only entries (the stats.qh
    /// names WITH a cvar expression — jumpstep, friction_on_land, wallfriction, stepdown_maxspeed, …) are copied
    /// from <paramref name="globalValues"/> verbatim. The maxspd_mod fold is NOT applied here — both sides fold
    /// it at move time via <c>MovementParameters.ApplyHighSpeed</c> (the replicated <c>g_movement_highspeed</c>
    /// entry), so folding it into the vector would double-apply.
    /// </summary>
    /// <param name="cvars">The server's cvar store (holds the <c>g_physics_&lt;set&gt;_*</c> table).</param>
    /// <param name="clPhysics">The client's replicated <c>cl_physics</c> ("" for a bot / not sent).</param>
    /// <param name="globalValues">This tick's <see cref="Capture"/> result (the sv_* fallbacks).</param>
    /// <param name="exists">CVAR_TYPEFLAG_EXISTS check (pass <c>CvarService.Has</c>); null → GetString != "".</param>
    public static float[] CaptureResolved(ICvarService cvars, string clPhysics, float[] globalValues,
        System.Func<string, bool>? exists = null)
    {
        var v = new float[MovementCvars.Length];
        int n = System.Math.Min(globalValues.Length, v.Length);
        for (int i = 0; i < n; i++)
        {
            string? option = XonoticGodot.Common.Physics.PhysicsPreset.OptionFor(MovementCvars[i]);
            v[i] = option is null
                ? globalValues[i]
                : XonoticGodot.Common.Physics.PhysicsPreset.Resolve(cvars, clPhysics, option, globalValues[i], exists);
        }
        return v;
    }

    /// <summary>Stamp received values back into the client's cvar store so <c>FromCvars</c> reads the server's physics.</summary>
    public static void Apply(ICvarService cvars, float[] values)
    {
        int n = System.Math.Min(values.Length, MovementCvars.Length);
        for (int i = 0; i < n; i++)
            cvars.Set(MovementCvars[i], values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Write the positional float block.</summary>
    public static void Serialize(BitWriter w, float[] values)
    {
        w.WriteByte(values.Length);
        for (int i = 0; i < values.Length; i++)
            w.WriteFloat(values[i]);
    }

    /// <summary>Read the positional float block.</summary>
    public static float[] Deserialize(ref BitReader r)
    {
        int n = r.ReadByte();
        if (n < 0 || n > 255) return System.Array.Empty<float>();
        var v = new float[n];
        for (int i = 0; i < n; i++)
            v[i] = r.ReadFloat();
        return v;
    }

    /// <summary>A content hash of the values for cheap "did the physics change?" change-detection (FNV-1a over the bits).</summary>
    public static uint Hash(float[] values)
    {
        uint h = 2166136261u;
        for (int i = 0; i < values.Length; i++)
        {
            uint bits = (uint)System.BitConverter.SingleToInt32Bits(values[i]);
            for (int b = 0; b < 4; b++) { h ^= bits & 0xFF; h *= 16777619u; bits >>= 8; }
        }
        return h;
    }
}
