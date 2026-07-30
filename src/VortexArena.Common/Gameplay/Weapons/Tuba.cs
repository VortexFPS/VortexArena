using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The @!#%'n Tuba — port of common/weapons/weapon/tuba.{qh,qc}. A hidden splash weapon that "plays notes"
/// which damage and knock back nearby enemies. Each press (primary or secondary) plays a note, which
/// applies a small radius blast centered on the player. The note's pitch is chosen from the movement keys
/// held; secondary plays a higher pitch. Reload cycles through three instruments (Tuba/Accordion/Klein
/// Bottle). It uses no ammo.
///
/// Identity/attributes from tuba.qh; balance from bal-wep-xonotic.cfg (g_balance_tuba_*).
/// This port covers the per-note radius blast (a HITTYPE_SOUND attack that never hurts teammates), the
/// sustained note entity (held -> refresh, release -> note-off), the W_Tuba_GetNote pitch selection
/// (derived from movement direction since key input is headless), the instrument cycling on reload
/// (Tuba/Accordion/Klein Bottle, which carries the HITTYPE_SECONDARY/BOUNCE obituary bits), the per-note
/// smoke ring, the wr_setup instrument reset on equip, and the two-sample cos/sin crossfade timbre blend
/// (SoundChannel.Tuba = floor neighbour + SoundChannel.TubaBlend = upper neighbour, weighted by cos/sin
/// of the fractional semitone offset, matching tubasound's in-range branch). The melody-recognition chat
/// bprint line is handled via the TubaMelodyBprint host seam. Only the ENT_CLIENT_TUBANOTE remote-player
/// note networking and the cl_tuba_fadetime release fade remain unported (both need a per-frame CSQC
/// client-side volume think that the server-side port cannot reproduce).
/// </summary>
[Weapon]
public sealed class Tuba : Weapon
{
    // CSQC tuba.qc:432-434 — the playable note range and the ring-buffer depth.
    public const int TubaMin = -18;        // TUBA_MIN
    public const int TubaMax = 27;         // TUBA_MAX
    public const int MaxTubaNotes = 32;    // MAX_TUBANOTES (melody ring-buffer depth)

    /// <summary>Balance block — QC WEP_CVAR(WEP_TUBA, *) (single block, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Animtime;    // g_balance_tuba_animtime
        public float Attenuation; // g_balance_tuba_attenuation (sound falloff; affects networking only)
        public float Damage;      // g_balance_tuba_damage (per note, at the blast core)
        public float EdgeDamage;  // g_balance_tuba_edgedamage
        public float Force;       // g_balance_tuba_force
        public float Radius;      // g_balance_tuba_radius
        public float Refire;      // g_balance_tuba_refire
    }

    public Balance Cvars;

    /// <summary>
    /// Host seam for QC's <c>W_Tuba_NoteOff</c> recognized-melody broadcast (tuba.qc:115-131): when a magic-ear
    /// matches a played melody, QC <c>bprint</c>s "* NAME played on the @!#%'n Tuba/Accordeon/Klein Bottle: TEXT"
    /// to all clients. The arguments are <c>(actor, instrument, matchedText)</c>; the host composes the player
    /// display name (not carried on the Common entity) and the per-instrument label and broadcasts it via the
    /// chat pipeline. Unwired = the chat line is omitted (no regression, matching the prior behavior).
    /// </summary>
    public static System.Action<Entity, int, string>? TubaMelodyBprint;

    /// <summary>The three instrument display labels for <see cref="TubaMelodyBprint"/> (QC's bprint switch,
    /// tuba.qc:118-129): 0 = Tuba, 1 = Accordeon, 2 = Klein Bottle. Host composes
    /// "* NAME played on the @!#%'n {label}: {text}".</summary>
    public static string InstrumentLabel(int instrument) => instrument switch
    {
        1 => "@!#%'n Accordeon",
        2 => "@!#%'n Klein Bottle",
        _ => "@!#%'n Tuba",
    };

    public Tuba()
    {
        NetName = "tuba";
        DisplayName = "@!#%'n Tuba";
        Impulse = 1;
        // WEP_FLAG_HIDDEN | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.TypeSplash | WeaponFlags.NoDual | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.909f, 0.816f, 0.345f);
        ViewModel = "h_tuba.iqm";  // MDL_TUBA_VIEW
        WorldModel = "v_tuba.md3"; // MDL_TUBA_WORLD
        ItemModel = "g_tuba.md3";  // MDL_TUBA_ITEM
    }

    // METHOD(Tuba, describe) — the MENUQC weapon-guide prose (tuba.qc:600-612). Plain newline-separated
    // paragraphs with the %s name substitutions pre-filled (the literal "@!#%'n Tuba"), matching the Electro
    // precedent. The dynamic W_Guide_Keybinds / W_Guide_DPS lines (last two PARs) are owned by the guide page,
    // not this static text, so they're omitted here exactly as the Electro guide omits them.
    public override string? GuideDescription =>
        "The @!#%'n Tuba is a unique weapon that makes the ears of nearby enemies bleed by playing awful "
      + "sounds, also slightly knocking them back.\n\n"
      + "The secondary fire works the same way, playing a higher pitch.\n\n"
      + "The only ammo it needs to operate is the breath from your lungs.\n\n"
      + "Since your enemies need to be close by to hear your rubbish music, the @!#%'n Tuba is only effective "
      + "at very close ranges.\n\n"
      + "The pitch the @!#%'n Tuba plays depends on the movement keys pressed.";

    public override void Configure()
    {
        Cvars.Animtime = Bal("g_balance_tuba_animtime", 0.05f);
        Cvars.Attenuation = Bal("g_balance_tuba_attenuation", 0.5f);
        Cvars.Damage = Bal("g_balance_tuba_damage", 5f);
        Cvars.EdgeDamage = Bal("g_balance_tuba_edgedamage", 0f);
        Cvars.Force = Bal("g_balance_tuba_force", 40f);
        Cvars.Radius = Bal("g_balance_tuba_radius", 200f);
        Cvars.Refire = Bal("g_balance_tuba_refire", 0.05f);
    }

    // METHOD(Tuba, wr_setup) — reset the instrument to Tuba on (re)equip. tuba.qc:355-358
    // The driver calls WrSetup on switch-in (WeaponFireDriver), matching QC's wr_setup on equip.
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        actor.WeaponState(slot).TubaInstrument = 0;
    }

    // METHOD(Tuba, wr_think) — common/weapons/weapon/tuba.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The Tuba is a CONTINUOUS weapon — it sustains a note while a fire button is held. The note's blast
        // is refire-gated (tuba_refire ~0.05s) via PrepareAttack; the note entity's keep-alive (refire*2)
        // bridges the gap between blasts so a held note doesn't gap. The driver calls WrThink(Primary) every
        // tick, so we read the held buttons directly (st.ButtonAttack/2) and stop the note when neither is up.
        if (fire == FireMode.Secondary)
        {
            // Secondary blast (raises the pitch by +7); the Primary call already handled the primary blast.
            if (PrepareAttack(actor, slot, fire))
                NoteOn(actor, slot, secondary: true);
            return;
        }

        if (st.ButtonAttack)
        {
            if (PrepareAttack(actor, slot, fire))
                NoteOn(actor, slot, secondary: false);
        }
        else if (!st.ButtonAttack2)
        {
            NoteOff(actor, slot); // neither fire button down -> stop the sustained note
        }
    }

    // METHOD(Tuba, wr_aim) — tuba.qc:321-332. "Bots cannot play the Tuba well yet": when the enemy is within
    // the note radius, the bot presses primary or secondary at a 50/50 coin flip (random() > 0.5 -> primary,
    // else secondary). The brain has already decided to take a shot this frame and gates by range via the
    // generic aim; this hook owns only the primary-vs-secondary pick, so we route ~half the close-range shots
    // onto ATCK2 with the same fresh random() draw QC uses. The radius gate is the QC vdist(<, radius); the
    // shared brain aim already suppresses long-range shots for this very-short-radius (200u) weapon.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
        => enemyDistance < Cvars.Radius && ctx.Random01 > 0.5f;

    // Refire/animtime from the (cvar-seeded) balance block (a single tuba_refire/animtime for both pitches).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_Tuba_NoteOn — play a note: pick the pitch, spawn/refresh the sustained note entity, and blast nearby
    // enemies with a small radius hit centered on the player. tuba.qc:262-319
    private void NoteOn(Entity actor, WeaponSlot slot, bool secondary)
    {
        var st = actor.WeaponState(slot);
        int note = GetNote(actor, secondary);

        // Build the deathtype tag exactly as QC does (tuba.qc:266-272):
        //   hittype = HITTYPE_SOUND
        //   if (instrument & 1) hittype |= HITTYPE_SECONDARY   (accordion)
        //   if (instrument & 2) hittype |= HITTYPE_BOUNCE       (klein bottle)
        // HITTYPE_SOUND makes the blast a sound attack: DamageSystem.Apply NEVER hurts teammates (it honors
        // the same-team HITTYPE_SOUND rule). The SECONDARY/BOUNCE bits drive the per-instrument obituary
        // (DeathMessages: BOUNCE -> Klein Bottle, SECONDARY -> Accordeon, else Tuba). The int weapon-id path
        // (ApplyDamage(int)) cannot carry any of these, so we form the STRING deathtype here and pass it via
        // the deathTag override on WeaponSplash.RadiusDamage (the Wave-1 string damage channel).
        // Note: the secondary FIRE MODE itself does not set HITTYPE_SECONDARY (QC's wr_think OR's it, but only
        // the note pitch +7 is observable; the obituary instrument bit comes from the instrument, faithful to
        // W_Tuba_NoteOn which recomputes hittype from the instrument alone before RadiusDamage).
        string deathTag = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Sound);
        if ((st.TubaInstrument & 1) != 0)
            deathTag = DeathTypes.WithHitType(deathTag, DeathTypes.Secondary);
        if ((st.TubaInstrument & 2) != 0)
            deathTag = DeathTypes.WithHitType(deathTag, DeathTypes.Bounce);

        // Sustained note entity: refresh if the pitch/instrument is unchanged, else restart.
        Entity? cur = st.TubaNote;
        if (cur is not null && !cur.IsFreed && (cur.Count != note || cur.Frame != st.TubaInstrument))
        {
            NoteOff(actor, slot);
            cur = null;
        }
        if (cur is null || cur.IsFreed)
        {
            Entity n = Api.Entities.Spawn();
            n.ClassName = "tuba_note";
            n.Owner = actor;
            n.NetName = NetName;
            n.Count = note;                 // .cnt = note pitch
            n.Frame = st.TubaInstrument;    // which instrument
            n.LTime = Api.Clock.Time;       // QC note.spawnshieldtime = time (the note's START time)
            Api.Entities.SetOrigin(n, actor.Origin);
            // QC: setthink(note, W_Tuba_NoteThink); note.nextthink = time. The note self-expires once its
            // keep-alive (teleport_time / MaxHealth) lapses — i.e. when the player stops refreshing it.
            n.Think = self => NoteThink(self, actor, slot);
            n.NextThink = Api.Clock.Time;
            st.TubaNote = n;
        }
        // teleport_time: keep the note alive a little past the refire so a held note doesn't gap.
        // QC tuba.qc:292 — time + refire * 2 * W_WeaponRateFactor(actor). Scaled by the rate factor so a
        // haste/slow modifier that changes the refire keeps the keep-alive in step. Stored in MaxHealth.
        if (st.TubaNote is not null)
            st.TubaNote.MaxHealth = Api.Clock.Time + Cvars.Refire * 2f * WeaponRateFactor(actor);

        WeaponSplash.RadiusDamage(actor, actor.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            actor, RegistryId, Cvars.Force, deathTag: deathTag);

        // QC plays a per-note pitched loop sample (TUBA_STARTNOTE) chosen by pitch+instrument, sustained on the
        // dedicated CH_TUBA_SINGLE channel and faded client-side via ENT_CLIENT_TUBANOTE / tubasound
        // (tuba.qc:438-489). The note-sound NETWORKING (remote players' notes via ENT_CLIENT_TUBANOTE) is still
        // render-only, but the local sustained loop + pitch-step + cos/sin crossfade is reproduced here:
        //   * PitchStepBaseNote returns the PRIMARY (floor/edge) sample, its cos volume weight and pitch
        //     speed (2^(m/12)), AND — in the in-range branch — the upper-neighbour sample, its sin volume
        //     weight and pitch speed (2^((m-step)/12)), matching tubasound's four-branch logic (tuba.qc:449-477).
        //   * Primary loop plays on CH_TUBA (=SoundChannel.Tuba), upper-neighbour on CH_TUBA+1
        //     (=SoundChannel.TubaBlend). Both are keyed by (actor netId, channel) so a later Play on the
        //     same (entity, channel) replaces them without stacking — exactly QC's e / e.enemy entity pair.
        //   * When vol2 == 0 (single-sample edge branches) the TubaBlend channel is stopped so no stale
        //     loop bleeds through on a pitch change that leaves the crossfade branch.
        //   * volume = VOL_BASE * cl_tuba_volume (clamped [0,1]); attenuation = cl_tuba_attenuation.
        // Remaining residuals: cl_tuba_fadetime release ramp (needs CSQC/ENT_CLIENT_TUBANOTE, unportable
        // server-side), ENT_CLIENT_TUBANOTE remote-player networking.
        int baseNote = PitchStepBaseNote(note,
            out float vol1, out float speed1,
            out int upperNote, out float vol2, out float speed2);
        float baseVol = System.Math.Clamp(SoundLevels.VolBase * TubaVolumeCvar(), 0f, 1f);
        float att = TubaAttenuationCvar();
        Api.Sound.Play(actor, SoundChannel.Tuba, NoteSample(st.TubaInstrument, baseNote),
            baseVol * vol1, att, loop: true, pitch: speed1);
        if (vol2 > 0f)
        {
            // In-range crossfade: play the upper neighbour at the sin weight on the blend channel.
            // tuba.qc:479-481: sound7(e.enemy, CH_TUBA_SINGLE, snd2, vol*vol2, atten, 100*speed2, 0).
            Api.Sound.Play(actor, SoundChannel.TubaBlend, NoteSample(st.TubaInstrument, upperNote),
                baseVol * vol2, att, loop: true, pitch: speed2);
        }
        else
        {
            // Edge branch or exact-step: ensure any stale TubaBlend loop from a previous in-range note
            // is stopped (e.g. sliding from an in-range note into an edge note while holding fire).
            Api.Sound.Stop(actor, SoundChannel.TubaBlend);
        }

        // Per-note smoke ring (EFFECT_SMOKE_RING), throttled to 0.25s, with a per-instrument vertical offset.
        // tuba.qc:307-318. QC reads the weapon-tag origin (gettaginfo); headless, we approximate from the
        // player's eye + view vectors, which is what the tag sits relative to.
        if (Api.Clock.Time > st.TubaSmokeTime)
        {
            QMath.AngleVectors(actor.Angles, out Vector3 fwd, out Vector3 right, out Vector3 up);
            Vector3 org = actor.Origin + actor.ViewOfs;
            Vector3 ringOrg = st.TubaInstrument switch
            {
                1 => org + up * 25f + right * 10f + fwd * 14f,
                2 => org + up * 50f + right * 10f + fwd * 45f,
                _ => org + up * 40f + right * 10f + fwd * 14f,
            };
            EffectEmitter.Emit("SMOKE_RING", ringOrg, up * 100f, 1);
            st.TubaSmokeTime = Api.Clock.Time + 0.25f;
        }
    }

    // W_Tuba_NoteOff — stop the sustained note when the player lets go of fire. tuba.qc:99-134
    private void NoteOff(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        Entity? note = st.TubaNote;
        if (note is null)
            return;

        // QC: only the OWNING slot's current note records history (if (actor.(weaponentity).tuba_note == this)).
        // Record the just-ended note into the ring buffer as vec3(on=note.spawnshieldtime, off=time, pitch=cnt),
        // advance the write cursor, and bump the (capped) count. tuba.qc:107-112.
        st.TubaLastNotesLast = (st.TubaLastNotesLast + 1) % MaxTubaNotes;
        st.TubaLastNotes[st.TubaLastNotesLast] = new Vector3(note.LTime, Api.Clock.Time, note.Count);
        st.TubaNote = null;
        st.TubaLastNotesCount = System.Math.Min(st.TubaLastNotesCount + 1, MaxTubaNotes); // bound(0, cnt+1, MAX)

        // QC then runs the just-played note buffer through every magic-ear in TUBA mode (an empty say message),
        // so map melody triggers (W_Tuba_HasPlayed) fire. tuba.qc:114-131. When an ear MATCHES, it returns the
        // ear's replacement text (s != ""), and QC bprints "* NAME played on the @!#%'n Tuba/Accordeon/Klein
        // Bottle: s" to everyone. The compose+broadcast (player display name + bprint) lives host-side (the name
        // is not on the Common entity and there is no broadcast-print primitive in Common), so we capture the
        // matched text and the instrument and hand them to the host seam (TubaMelodyBprint). When unwired the
        // chat line is simply omitted — identical to the prior behavior, no regression.
        string melodyMatch = LogicGates.MagicEarProcessAllEars(actor, 0, null, "");
        if (!string.IsNullOrEmpty(melodyMatch))
            TubaMelodyBprint?.Invoke(actor, (int)note.Frame, melodyMatch); // note.Frame holds st.TubaInstrument (set at note spawn)

        // Stop both sustained loops on CH_TUBA and CH_TUBA_BLEND. QC's CSQC fades the note over
        // cl_tuba_fadetime (Ent_TubaNote_Think ramps tuba_volume down on e AND e.enemy before SND_Null);
        // we don't model that fade ramp (it needs a per-frame CSQC client-side volume think tied to
        // ENT_CLIENT_TUBANOTE), so both loops end cleanly — the 0.25s fade tail remains a residual gap.
        // Without both Stops the loops would hang after release (or after a pitch/instrument change).
        Api.Sound.Stop(actor, SoundChannel.Tuba);
        Api.Sound.Stop(actor, SoundChannel.TubaBlend);

        if (!note.IsFreed) Api.Entities.Remove(note);
    }

    // W_Tuba_NoteThink — the per-frame keep-alive on the sustained note entity (tuba.qc:226-260). The note
    // self-expires once its keep-alive (teleport_time / MaxHealth) lapses — i.e. the player stopped refreshing
    // it (released fire, or a frame was skipped past the keep-alive). The QC per-listener volume/angle re-origin
    // + SendFlags is moot here (no tuba-note sound networking), so we only model the self-expiry.
    private void NoteThink(Entity self, Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (st.TubaNote != self)
            return; // superseded (restarted at a new pitch/instrument) — the old note already off'd.
        if (Api.Clock.Time > self.MaxHealth) // QC: if (time > this.teleport_time) W_Tuba_NoteOff(this);
        {
            NoteOff(actor, slot);
            return;
        }
        self.NextThink = Api.Clock.Time;
    }

    /// <summary>
    /// Port of <c>tubasound</c>'s pitch-step branch (tuba.qc:438-489). Base records loop samples only at
    /// multiples of <c>cl_tuba_pitchstep</c> (default 6) across TUBA_MIN..TUBA_MAX (PRECACHE:591-594) and
    /// SOUND7-shifts the residual <c>m = pymod(note, step)</c> semitones.
    ///
    /// <para>Returns the PRIMARY (floor-neighbour) sample to play as the return value, its volume weight
    /// <paramref name="vol1"/> = cos(pi/2 · m/step) and pitch <paramref name="speed1"/> = 2^(m/12).
    /// When the note sits in the fully in-range branch (neither edge of TUBA_MIN..TUBA_MAX), also returns
    /// the UPPER-NEIGHBOUR sample <paramref name="note2"/> = <c>note - m + step</c>, its weight
    /// <paramref name="vol2"/> = sin(pi/2 · m/step) and pitch <paramref name="speed2"/> = 2^((m-step)/12)
    /// — matching tubasound's two-sample cos/sin crossfade (tuba.qc:466-474). In the single-sample branches
    /// <paramref name="vol2"/> is 0 and the caller should not emit a second loop.
    /// With pitchstep 0 (Base's no-pitch-step branch) the exact note is played at speed 1 with full volume.</para>
    /// </summary>
    private static int PitchStepBaseNote(int note,
        out float vol1, out float speed1,
        out int note2, out float vol2, out float speed2)
    {
        // Default 6 (xonotic-client.cfg ships `cl_tuba_pitchstep 6`, and the loop samples only exist at
        // multiples of 6). Honor an EXPLICIT 0 (Base's no-pitch-step / exact-pitch lookup) but fall back to 6
        // when the cvar is unregistered, so a headless context still resolves to a real sample.
        int step = 6;
        if (Api.Services is not null)
        {
            string s = Api.Cvars.GetString("cl_tuba_pitchstep");
            if (!string.IsNullOrEmpty(s))
                step = (int)Api.Cvars.GetFloat("cl_tuba_pitchstep");
        }
        // No-pitchstep / exact-pitch branch (Base tubasound else branch, tuba.qc:483-488).
        if (step <= 0)
        {
            vol1 = 1f; speed1 = 1f; note2 = note; vol2 = 0f; speed2 = 1f;
            return note;
        }

        // pymod: Python-style modulo so the residual is in [0, step) even for negative notes (matches QC pymod).
        int m = ((note % step) + step) % step;
        // Exact step multiple — single sample, no crossfade. tuba.qc:476-477.
        if (m == 0)
        {
            vol1 = 1f; speed1 = 1f; note2 = note; vol2 = 0f; speed2 = 1f;
            return note;
        }

        if (note - m < TubaMin)
        {
            // Floor neighbour is below the recorded range -> use the UPPER neighbour only, shift DOWN.
            // tuba.qc:452-457. Single sample — no crossfade.
            vol1 = 1f; speed1 = MathF.Pow(2f, (m - step) / 12f);
            note2 = note - m + step; vol2 = 0f; speed2 = 1f;
            return note - m + step;
        }
        if (note - m + step > TubaMax)
        {
            // Upper neighbour would exceed the recorded range -> floor sample only. tuba.qc:458-462.
            vol1 = 1f; speed1 = MathF.Pow(2f, m / 12f);
            note2 = note - m; vol2 = 0f; speed2 = 1f;
            return note - m;
        }
        // IN-RANGE branch: both floor AND upper neighbours exist in TUBA_MIN..TUBA_MAX.
        // Crossfade: vol1 = cos(pi/2 * m/step), vol2 = sin(pi/2 * m/step). tuba.qc:466-474.
        // The upper neighbour is pitched DOWN (speed2 = 2^((m-step)/12)) to meet the floor pitch-up midway;
        // the ear blends them into a smoother timbre than a hard jump between recorded samples.
        float frac = (float)m / step;
        vol1 = MathF.Cos(MathF.PI / 2f * frac);
        speed1 = MathF.Pow(2f, m / 12f);
        note2 = note - m + step;
        vol2 = MathF.Sin(MathF.PI / 2f * frac);
        speed2 = MathF.Pow(2f, (m - step) / 12f);
        return note - m;
    }

    // autocvar_cl_tuba_volume (default 1) — tubasound scales VOL_BASE by it. Headless fallback 1.
    private static float TubaVolumeCvar() => TubaCvar("cl_tuba_volume", 1f);

    // autocvar_cl_tuba_attenuation (default 0.5) — tubasound's falloff. Headless fallback 0.5.
    private static float TubaAttenuationCvar() => TubaCvar("cl_tuba_attenuation", 0.5f);

    private static float TubaCvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Port of W_Tuba_GetNote (tuba.qc): in QC the pitch is chosen from the movement keys held (a 3x3 grid),
    /// shifted by crouch (-12), jump (+12), secondary (+7) and the team/player tuning (+3). The movement-key
    /// and crouch/jump inputs aren't carried by the headless entity, so we derive a movement-direction note
    /// from the player's velocity (a faithful stand-in) plus the +7 secondary shift; pitch only affects the
    /// sound sample, so this divergence is purely cosmetic (registry-flagged intended divergence).
    /// </summary>
    private int GetNote(Entity actor, bool secondary)
    {
        // Map the horizontal velocity direction (relative to facing) to the same 1..9 movestate grid QC uses.
        QMath.AngleVectors(actor.Angles, out Vector3 fwd, out Vector3 right, out _);
        float vf = QMath.Dot(actor.Velocity, fwd);
        float vr = QMath.Dot(actor.Velocity, right);
        int movestate = 5;
        if (vf < -16f) movestate -= 3; else if (vf > 16f) movestate += 3;
        if (vr < -16f) movestate -= 1; else if (vr > 16f) movestate += 1;

        int note = movestate switch
        {
            1 => -6, 2 => -5, 3 => -4, 4 => +5,
            6 => +2, 7 => +3, 8 => +4, 9 => -1,
            _ => 0,
        };
        if (secondary) note += 7;
        return note;
    }

    // CSQC TUBA_STARTNOTE(i, n): _Sound_fixpath(W_Sound("tuba" + (i?ftos(i):"") + "_loopnote" + ftos(n))).
    // tuba.qc:430. The instrument prefix is "" for Tuba, "1"/"2" for Accordion/Klein Bottle; the suffix is the
    // signed note pitch. This reproduces the real per-pitch/instrument asset name (e.g. "weapons/tuba_loopnote0",
    // "weapons/tuba1_loopnote-6") that exists in data, instead of the flat "tuba_loopnote.wav" that resolved to
    // nothing. (The client-side pitch-step crossfade / fade-out / sustain loop remain unported.)
    private static string NoteSample(int instrument, int note)
    {
        string i = instrument != 0 ? instrument.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return "weapons/tuba" + i + "_loopnote"
            + note.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav";
    }

    // METHOD(Tuba, wr_reload) — cycle through the three instruments (Tuba -> Accordion -> Klein Bottle).
    // tuba.qc:360-390. Wired from WeaponImpulses.ReloadHandle (Wave-1 seam: a non-Reloadable weapon whose
    // wr_reload is repurposed dispatches here instead of the no-op base WrReload).
    public void Reload(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);

        // QC: early-return if the slot is not READY (can't cycle mid raise/fire/drop). tuba.qc:363
        if (st.State != WeaponFireState.Ready)
            return;

        // Cycle 0 -> 1 -> 2 -> 0 (Tuba -> Accordion -> Klein Bottle). tuba.qc:366-380
        st.TubaInstrument = (st.TubaInstrument + 1) % 3;

        // QC plays Send_Effect(EFFECT_TELEPORT, w_shotorg, ...) on the instrument switch (tuba.qc:387).
        EffectEmitter.Emit("TELEPORT", actor.Origin + actor.ViewOfs, Vector3.Zero, 1);

        // QC: state = WS_INUSE; weapon_thinkf(WFRAME_RELOAD, 0.5, w_ready) — lock the slot for the 0.5s reload
        // anim then settle back to READY. tuba.qc:388-389.
        st.State = WeaponFireState.InUse;
        WeaponFireDriver.ScheduleThink(st, 0.5f, static (pl, sl) =>
        {
            var s2 = pl.WeaponState(sl);
            if (s2.State == WeaponFireState.InUse)
                s2.State = WeaponFireState.Ready;
        });
    }

    // METHOD(Tuba, wr_checkammo1/2) — tuba.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;

    /// <summary>
    /// Port of <c>W_Tuba_HasPlayed</c> (tuba.qc:13-97): test whether the last N notes recorded for
    /// <paramref name="slot"/> form the given <paramref name="melody"/> ("pitch.duration pitch.duration ..."),
    /// optionally ignoring absolute pitch (transposing) and within a tempo window. On a match it clears the
    /// slot's note count (so the melody can't immediately re-trigger). Used by map magic-ear melody triggers.
    /// </summary>
    public static bool HasPlayed(Entity pl, WeaponSlot slot, string melody, int instrument,
        bool ignorePitch, float minTempo, float maxTempo)
    {
        var st = pl.WeaponState(slot);

        // Tokenize the melody into note tokens (QC tokenize_console). A token "P.D" carries pitch P and a
        // fractional duration D (note: floor(P.D) == P is the "no length" / pitch-only form).
        string[] tokens = melody.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        int n = tokens.Length;
        if (n > st.TubaLastNotesCount)
            return false;

        // Instrument filter: instrument < 0 means "any". tuba.qc:20-21.
        if (instrument >= 0 && st.TubaInstrument != instrument)
            return false;

        float pitchshift = 0f;
        bool nolength = false;

        // Verify the NOTES (read the ring backwards: i=0 is the most-recent note). tuba.qc:24-41.
        for (int i = 0; i < n; ++i)
        {
            Vector3 v = st.TubaLastNotes[((st.TubaLastNotesLast - i) % MaxTubaNotes + MaxTubaNotes) % MaxTubaNotes];
            float ai = ParseFloat(tokens[n - i - 1]);
            float np = MathF.Floor(ai);
            if (ai == np)
                nolength = true;
            if (ignorePitch && i == 0)
                pitchshift = np - v.Z;
            else if (v.Z + pitchshift != np)
                return false;
        }

        // Verify the RHYTHM unless a pitch-only token was present. tuba.qc:44-92.
        if (!nolength)
        {
            float mmin = maxTempo > 0f ? 240f / maxTempo : 0f;
            float mmax = minTempo > 0f ? 240f / minTempo : 240f;

            float ti = 0f;
            for (int i = 0; i < n; ++i)
            {
                Vector3 vi = st.TubaLastNotes[((st.TubaLastNotesLast - i) % MaxTubaNotes + MaxTubaNotes) % MaxTubaNotes];
                float ai = ParseFloat(tokens[n - i - 1]);
                ti -= 1f / (ai - MathF.Floor(ai));
                float tj = ti;
                for (int j = i + 1; j < n; ++j)
                {
                    Vector3 vj = st.TubaLastNotes[((st.TubaLastNotesLast - j) % MaxTubaNotes + MaxTubaNotes) % MaxTubaNotes];
                    float aj = ParseFloat(tokens[n - j - 1]);
                    tj -= aj - MathF.Floor(aj);
                    mmin = MathF.Max(mmin, (vi.X - vj.Y) / (ti - tj)); // lower bound
                    mmax = MathF.Min(mmax, (vi.Y - vj.X) / (ti - tj)); // upper bound
                }
            }

            if (mmin > mmax) // rhythm fail
                return false;
        }

        st.TubaLastNotesCount = 0;
        return true;
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
}
