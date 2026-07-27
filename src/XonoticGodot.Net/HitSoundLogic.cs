// Port of the client hit-feedback state machine in qcsrc/client/view.qc — UpdateDamage() (:890-912) +
// HitSound() (:914-978). Pure C# (no Godot) so the semantics are unit-testable; the game-side
// XonoticGodot.Game.Client.HitSound wraps this with the actual cvar reads + AudioStreamPlayer.

namespace XonoticGodot.Net;

/// <summary>The sound/flash cues one <see cref="HitSoundLogic.Update"/> step decided to fire.</summary>
public struct HitSoundCues
{
    /// <summary>Play the hit-confirmation beep (<c>misc/hit</c>) at <see cref="HitPitch"/>.</summary>
    public bool PlayHit;
    /// <summary>Pitch for the hit beep (mode 2/3 vary it with the accumulated damage; else 1.0).</summary>
    public float HitPitch;
    /// <summary>Play the team-hit / chat-protected-hit "dink" (<c>misc/typehit</c>).</summary>
    public bool PlayTypeHit;
    /// <summary>Play the kill confirmation (<c>misc/kill</c>).</summary>
    public bool PlayKill;
    /// <summary>New confirmed damage registered this step — drives the crosshair hit-indication flash
    /// (QC crosshair.qc:387 reads the same <c>unaccounted_damage</c> this machine accumulates).</summary>
    public bool NewDamage;
}

/// <summary>
/// The faithful client hit-feedback state machine (view.qc <c>UpdateDamage</c> + <c>HitSound</c>):
/// <list type="bullet">
///   <item><b>Hit beep</b>: diffs the cumulative <c>HITSOUND_DAMAGE_DEALT_TOTAL</c> stat, gated on
///   <c>HIT_TIME</c> advancing, into an <c>unaccounted_damage</c> accumulator. A free-running antispam
///   window (<c>cl_hitsound_antispam_time</c>, default 0.05s) then plays ONE beep per window with the pitch
///   computed from the WHOLE accumulated amount — damage inside the window is never dropped, it just merges
///   into the next beep (at most one window late). Modes: 1 fixed pitch, 2 pitch falls with damage,
///   3 pitch rises with damage. The ARC hack (QC view.qc:928) bypasses the window at modes ≥ 2 so the
///   beam's pitch shift sounds continuous.</item>
///   <item><b>Typehit / kill</b>: play once per advance of the server-side <c>TYPEHIT_TIME</c> /
///   <c>KILL_TIME</c> stats (two advances within the antispam window collapse into one sound — the compare
///   runs on the SERVER stat times). NOT gated by <c>cl_hitsound</c>, exactly like QC.</item>
/// </list>
/// The server flushes the three stats with typehit &gt; kill &gt; hit priority (world.qc EndFrame), so the
/// killing blow yields ONLY the kill sound — the sounds are mutually exclusive per server frame by design.
/// </summary>
public sealed class HitSoundLogic
{
    /// <summary>QC <c>cl_hitsound_antispam_time</c> default (xonotic-client.cfg:204).</summary>
    public const float DefaultAntispamTime = 0.05f;
    /// <summary>QC <c>cl_hitsound_max_pitch</c> default (view.qh:82).</summary>
    public const float DefaultMaxPitch = 1.5f;
    /// <summary>QC <c>cl_hitsound_min_pitch</c> default (view.qh:83).</summary>
    public const float DefaultMinPitch = 0.75f;
    /// <summary>QC <c>cl_hitsound_nom_damage</c> default (view.qh:84).</summary>
    public const float DefaultNomDamage = 25f;

    private bool _seeded;
    private float _totalPrev;        // QC damage_total_prev (UpdateDamage)
    private float _hitTimePrev;      // QC damage_dealt_time_prev (UpdateDamage)
    private float _typeHitTimePrev;  // QC typehit_time_prev (HitSound)
    private float _killTimePrev;     // QC kill_time_prev (HitSound)
    private float _unaccounted;      // QC unaccounted_damage
    private float _windowPrev;       // QC hitsound_time_prev — LOCAL clock, the free-running beep window
    private int _spectateePrev;      // QC spectatee_status_prev

    /// <summary>The live accumulated-but-unplayed damage (QC <c>unaccounted_damage</c>) — test/debug view.</summary>
    public float UnaccountedDamage => _unaccounted;

    /// <summary>Forget all baselines: the next <see cref="Update"/> re-seeds silently (reconnect/new game).</summary>
    public void Reset()
    {
        _seeded = false;
        _unaccounted = 0f;
    }

    /// <summary>
    /// Advance the state machine one client frame and decide which feedback sounds fire.
    /// </summary>
    /// <param name="now">Local client clock in seconds (monotonic; only differences are used).</param>
    /// <param name="mode"><c>cl_hitsound</c>: 0 off, 1 fixed pitch, 2 decreasing, 3 increasing.</param>
    /// <param name="antispamTime"><c>cl_hitsound_antispam_time</c> (0.05): min spacing of the hit beep.</param>
    /// <param name="maxPitch"><c>cl_hitsound_max_pitch</c> (a in the QC curve).</param>
    /// <param name="minPitch"><c>cl_hitsound_min_pitch</c> (b).</param>
    /// <param name="nomDamage"><c>cl_hitsound_nom_damage</c> (c — the damage that plays at pitch 1).</param>
    /// <param name="haveArc">A viewmodel slot holds the Arc (enables the QC antispam-bypass hack at mode ≥ 2).</param>
    /// <param name="spectatee">Spectatee id/status — accumulated damage is dropped when it changes (QC :907).</param>
    /// <param name="hitTime">Networked/local QC <c>STAT(HIT_TIME)</c>.</param>
    /// <param name="damageTotal">Networked/local QC <c>STAT(HITSOUND_DAMAGE_DEALT_TOTAL)</c>.</param>
    /// <param name="typeHitTime">Networked/local QC <c>STAT(TYPEHIT_TIME)</c>.</param>
    /// <param name="killTime">Networked/local QC <c>STAT(KILL_TIME)</c>.</param>
    public HitSoundCues Update(
        float now, int mode, float antispamTime,
        float maxPitch, float minPitch, float nomDamage,
        bool haveArc, int spectatee,
        float hitTime, float damageTotal, float typeHitTime, float killTime)
    {
        var cues = default(HitSoundCues);
        cues.HitPitch = 1f;

        if (!_seeded)
        {
            // First sample (fresh join / instance rebuild): latch every stat baseline SILENTLY, so joining
            // mid-match against non-zero totals / recent kill times doesn't fire spurious feedback. (QC gets
            // this for free from the per-connection CSQC world reset zeroing its statics alongside the stats.)
            _seeded = true;
            _totalPrev = damageTotal;
            _hitTimePrev = hitTime;
            _typeHitTimePrev = typeHitTime;
            _killTimePrev = killTime;
            _spectateePrev = spectatee;
            _windowPrev = now;
            _unaccounted = 0f;
            return cues;
        }

        // ---- QC UpdateDamage (view.qc:890-912) ----
        // Diff the cumulative damage stat; a BACKWARD jump means the stat reset under us (server restart /
        // tracked-player swap) — re-seed silently instead of QC's wrap arithmetic (its counters never
        // legitimately shrink within a CSQC life).
        float newDamage = damageTotal - _totalPrev;
        if (newDamage < 0f) newDamage = 0f;
        _totalPrev = damageTotal;

        if (hitTime != _hitTimePrev)
        {
            _unaccounted += newDamage;
            if (newDamage > 0f)
                cues.NewDamage = true;
        }
        _hitTimePrev = hitTime;

        // QC :907-911 — prevent a hitsound when switching spectatee.
        if (spectatee != _spectateePrev)
            _unaccounted = 0f;
        _spectateePrev = spectatee;

        // ---- QC HitSound (view.qc:914-961): the antispam-windowed, pitch-shifted hit beep ----
        // HACK (QC :928): the only way to get the ARC to sound consistent with pitch shift is to ignore
        // cl_hitsound_antispam_time while it's held.
        bool arcHack = haveArc && mode >= 2;
        if (arcHack || (now - _windowPrev) > antispamTime)
        {
            if (mode > 0 && _unaccounted > 0f)
            {
                cues.PlayHit = true;
                cues.HitPitch = ComputePitch(mode, _unaccounted, maxPitch, minPitch, nomDamage);
            }
            // QC clears the accumulator and restamps the window whether or not a beep played.
            _unaccounted = 0f;
            _windowPrev = now;
        }

        // ---- QC HitSound (view.qc:963-977): typehit + kill on stat-time advance ----
        // Deliberately NOT gated by cl_hitsound; the antispam compare runs on the SERVER stat times so two
        // same-window advances collapse. A skipped advance leaves the baseline unchanged (exactly QC), so a
        // third event one window later still plays. Backward stat jumps re-seed silently.
        if (typeHitTime < _typeHitTimePrev)
            _typeHitTimePrev = typeHitTime;
        else if ((typeHitTime - _typeHitTimePrev) > antispamTime)
        {
            cues.PlayTypeHit = true;
            _typeHitTimePrev = typeHitTime;
        }

        if (killTime < _killTimePrev)
            _killTimePrev = killTime;
        else if ((killTime - _killTimePrev) > antispamTime)
        {
            cues.PlayKill = true;
            _killTimePrev = killTime;
        }

        return cues;
    }

    /// <summary>
    /// Compute the hit-beep pitch for the given mode and accumulated damage — the QC asymptotic gradient
    /// (view.qc:937-949): a hyperbolic curve crossing (0, a) and (c, 1), asymptotically approaching b as
    /// damage grows; mode 3 mirrors it around <c>(a-b)/2 + b</c> so pitch RISES with damage instead.
    /// <c>pitch = (b·d·(a−1) + a·c·(1−b)) / (d·(a−1) + c·(1−b))</c> with a=max, b=min, c=nominal, d=damage.
    /// </summary>
    public static float ComputePitch(int mode, float damage, float maxPitch, float minPitch, float nomDamage)
    {
        if (mode != 2 && mode != 3)
            return 1.0f; // mode 1 fixed (or unknown)

        float a = maxPitch;
        float b = minPitch;
        float c = nomDamage;
        float d = damage;

        float denom = d * (a - 1f) + c * (1f - b);
        float pitch;
        if (System.MathF.Abs(denom) < 1e-6f)
        {
            // Degenerate cvar values (a==1 and b==1, or c==0): nominal pitch.
            pitch = 1.0f;
        }
        else
        {
            pitch = (b * d * (a - 1f) + a * c * (1f - b)) / denom;
        }

        if (mode == 3)
        {
            // QC view.qc:946-949: mirror in (a-b)/2 + b to reverse the curve direction.
            float mirror = (a - b) * 0.5f + b;
            pitch = mirror + (mirror - pitch);
        }

        // Clamp to [min, max] — the curve approaches but never crosses; degenerate cvars could.
        if (pitch < b) pitch = b;
        if (pitch > a) pitch = a;
        return pitch;
    }
}
