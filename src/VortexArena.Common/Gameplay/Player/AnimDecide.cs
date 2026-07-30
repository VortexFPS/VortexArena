namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The upper-body ACTION half of QuakeC's <c>animdecide</c> (common/animdecide.qc) — the DECISION logic only,
/// kept Godot-free and unit-testable. This is the SHARED CONTRACT referenced by BOTH the server producer
/// (the weapon-fire set-site + the per-frame expiry in ServerNet/BuildEntitySet) and the client torso-overlay
/// consumer (LocomotionBlend.SelectTorsoAction / PlayerModel.Pose), so the two agree on the action id space
/// and the running-window timing.
///
/// <para>Scope (Wave-14b Stage 4): the full action set — DRAW, PAIN1/PAIN2, SHOOT, MELEE, TAUNT, DIE1/DIE2 —
/// is wired. Each has its Base (fps, numframes) row in <see cref="SpecFor"/> and a faithful server set-site
/// (DRAW on weapon raise; PAIN on the pain debounce; SHOOT/MELEE at the fire-commit; TAUNT on the manual-taunt
/// voice; DIE on death), mirroring Base's <c>animdecide_setaction</c> / <c>animdecide_setstate</c> call sites.</para>
///
/// <para>Faithful mapping: Base's <c>animdecide_getupperanim</c> (animdecide.qc:109-153) chooses, in
/// priority order, DEAD (die1/die2/frozen) &gt; ACTIVE (a running explicit action whose window
/// <c>time &lt;= start + numframes/framerate</c> hasn't elapsed) &gt; IDLE. The SHOOT window mirrors
/// <c>e.anim_shoot = animfixfps(e, ANIM_VEC(shoot, 1, 5), …)</c> — numframes 1, framerate 5 → a 1/5 = 0.2s
/// run, after which the action expires back to idle (None). <c>SetAction</c> is the
/// <c>animdecide_setaction</c> latch (records the action + its start time).</para>
/// </summary>
public static class AnimDecide
{
    /// <summary>
    /// The upper-body action id (the port's <c>ANIMACTION_*</c>). 0 = no overlay (idle/aim). This is the
    /// SHARED byte both the server producer and the client consumer reference, and the value carried on the
    /// wire (<c>NetEntityState.UpperAction</c>). NOTE: these are the PORT's internal ids — they need NOT match
    /// Base's ANIMACTION_* numeric constants (Base has TAUNT=5/MELEE=6); only producer↔consumer must agree.
    /// </summary>
    public enum AnimUpperAction : byte
    {
        None = 0,
        Draw = 1,
        Pain1 = 2,
        Pain2 = 3,
        Shoot = 4,
        Melee = 5,
        Taunt = 6,
        Die1 = 7,
        Die2 = 8,
    }

    /// <summary>The animdecide priority cascade (animdecide.qc:104-107 ANIMPRIO_*): IDLE &lt; ACTIVE &lt; DEAD.</summary>
    public enum AnimPriority { Idle = 0, Active = 1, Dead = 3 }

    /// <summary>
    /// A per-anim running-window descriptor — the <c>(numframes, framerate)</c> pair from Base's
    /// <c>animfixfps(e, ANIM_VEC(name, numframes, rate), …)</c> (animdecide.qc:53-102). The action runs for
    /// <see cref="DurationSeconds"/> = numframes / framerate seconds, then expires.
    /// </summary>
    public readonly struct AnimSpec
    {
        public readonly int NumFrames;
        public readonly float FrameRate;
        public AnimSpec(int numFrames, float frameRate) { NumFrames = numFrames; FrameRate = frameRate; }
        /// <summary>The action's running window in seconds (QC <c>outframe.y / outframe.z</c>).</summary>
        public float DurationSeconds => FrameRate > 0f ? NumFrames / FrameRate : 0f;
    }

    /// <summary>
    /// The per-action (numframes, framerate) table — seeded with SHOOT (Base <c>ANIM_VEC(shoot, 1, 5)</c> →
    /// 0.2s). PAIN/DRAW/TAUNT/MELEE/DIE rows are reserved for Stage 4; until set they fall through to a
    /// zero-duration window (they expire immediately, so a stray latch never sticks). The same numbers are
    /// referenced client-side by <see cref="XonoticGodot.Engine.Simulation"/> via <see cref="SpecFor"/> so the
    /// phase/clamp math agrees with the server expiry.
    /// </summary>
    public static AnimSpec SpecFor(AnimUpperAction action) => action switch
    {
        // Base animdecide.qc:68-88 — each row is `animfixfps(e, ANIM_VEC(<name>, numframes, rate), …)`: numframes
        // 1 with the listed per-anim framerate, so the running window is `1 / rate` seconds (DurationSeconds).
        AnimUpperAction.Draw => new AnimSpec(1, 3f),      // animdecide.qc:70 ANIM_VEC(draw, 1, 3)    -> 0.333s
        AnimUpperAction.Pain1 => new AnimSpec(1, 2f),     // animdecide.qc:76 ANIM_VEC(pain1, 1, 2)   -> 0.5s
        AnimUpperAction.Pain2 => new AnimSpec(1, 2f),     // animdecide.qc:77 ANIM_VEC(pain2, 1, 2)   -> 0.5s
        AnimUpperAction.Shoot => new AnimSpec(1, 5f),     // animdecide.qc:78 ANIM_VEC(shoot, 1, 5)   -> 0.2s
        AnimUpperAction.Melee => new AnimSpec(1, 1f),     // animdecide.qc:88 ANIM_VEC(melee, 1, 1)   -> 1.0s (clip falls back to shoot)
        AnimUpperAction.Taunt => new AnimSpec(1, 0.33f),  // animdecide.qc:79 ANIM_VEC(taunt, 1, 0.33) -> ~3.03s
        AnimUpperAction.Die1 => new AnimSpec(1, 0.5f),    // animdecide.qc:68 ANIM_VEC(die1, 1, 0.5)  -> 2.0s
        AnimUpperAction.Die2 => new AnimSpec(1, 0.5f),    // animdecide.qc:69 ANIM_VEC(die2, 1, 0.5)  -> 2.0s
        _ => new AnimSpec(0, 0f),                         // None / idle: no window (the static aim pose carries the torso).
    };

    /// <summary>
    /// Port of <c>animdecide_getupperanim</c> (animdecide.qc:109-153): resolve the upper-body action this
    /// frame from the latched <paramref name="action"/> + its <paramref name="start"/> time at <paramref name="now"/>,
    /// applying the priority cascade. DEAD (die1/die2) always wins; an ACTIVE action wins while its running
    /// window hasn't elapsed (<c>now &lt;= start + numframes/framerate</c>); otherwise the upper body falls
    /// back to IDLE (None). Returns the action to render (None = the stable aim pose) and its priority.
    /// </summary>
    public static (AnimUpperAction action, AnimPriority priority) GetUpperAnim(AnimUpperAction action, float start, float now)
    {
        // DEAD priority: die1/die2 are never windowed — they hold until the state clears (animdecide.qc:114-117).
        if (action is AnimUpperAction.Die1 or AnimUpperAction.Die2)
            return (action, AnimPriority.Dead);

        // An explicit ACTIVE action runs while its window hasn't elapsed (animdecide.qc:141-148).
        if (action != AnimUpperAction.None)
        {
            AnimSpec spec = SpecFor(action);
            if (now <= start + spec.DurationSeconds)
                return (action, AnimPriority.Active);
        }

        // Expired / none → IDLE (animdecide.qc:150-152). The legs/aim pose carries the body; no torso overlay.
        return (AnimUpperAction.None, AnimPriority.Idle);
    }

    /// <summary>
    /// Port of <c>animdecide_setaction</c> (animdecide.qc:338-356) for the UPPER body: latch
    /// <paramref name="action"/> and stamp its start time to <paramref name="now"/>. Like QC's non-restart
    /// path, re-latching the SAME action that is still set is a no-op (the start time is NOT reset) so a held
    /// trigger doesn't restart the window every tick; pass <paramref name="restart"/> = true to force a restart
    /// (QC <c>restart</c>, used when the fire frame re-fires — <c>weapon_thinkf</c> passes restartanim).
    /// Writes back the new (action, start) for the caller to store on the producer entity.
    /// </summary>
    public static (AnimUpperAction action, float start) SetAction(
        AnimUpperAction current, float currentStart, AnimUpperAction action, float now, bool restart = false)
    {
        if (!restart && action == current)
            return (current, currentStart); // QC: same action, not restarting → keep the existing start time
        return (action, now);
    }
}
