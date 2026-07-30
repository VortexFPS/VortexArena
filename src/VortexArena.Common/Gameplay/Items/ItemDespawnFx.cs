// Client-side loot-despawn animation state — the C# successor to the reused .wait/.delay/.pointtime fields
// QuakeC's ItemDraw drives while ITS_EXPIRING is set (qcsrc/client/items/items.qc:191-210).
//
// QC handles loot despawn as a CONTINUOUS client-side animation, NOT a single server event: during the last
// IT_DESPAWNFX_TIME seconds of a dropped item's life the client fades the item's alpha (cl_items_animate & 2)
// AND repeatedly emits EFFECT_ITEM_DESPAWN at the item origin + '0 0 16' on an ACCELERATING cadence
// (cl_items_animate & 4) — this.delay halves each emit from 0.25 down to 0.0625. Reproducing that single
// server-side would be a fidelity regression; instead the server only flags ITS_EXPIRING (networked) and the
// client renderer (ClientWorld) drives ONE of these per expiring item, calling EffectSystem.Spawn for each puff.
//
// This type is intentionally pure (Godot-free): the bit-decoding + timing live here so they're unit-testable
// (ClientWorld in game/ isn't visible to the test project), and the renderer just supplies the current time +
// the cl_items_animate cvar value and consumes the (alpha, emitPuff) it returns.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The per-item loot-despawn animation timer (QC <c>ItemDraw</c>'s <c>ITS_EXPIRING</c> branch,
/// client/items/items.qc:191-210). The renderer holds one per expiring item and calls <see cref="Tick"/> each
/// frame; it returns the despawn alpha multiplier and whether to emit an <c>EFFECT_ITEM_DESPAWN</c> puff this
/// frame. Stateful (mirrors QC's reused <c>.wait</c>/<c>.delay</c>/<c>.pointtime</c> entity fields) but
/// Godot-free, so it is exercised directly by tests.
/// </summary>
public sealed class ItemDespawnFx
{
    // QC autocvar_cl_items_animate bits (xonotic-client.cfg default 7): 1 = bob/spin 3D items, 2 = fade out
    // despawning loot, 4 = glowing particles for despawning loot. Only 2 and 4 matter for the despawn FX.
    /// <summary>cl_items_animate bit 2 — fade the despawning loot item's alpha out.</summary>
    public const int AnimateAlphaBit = 2;
    /// <summary>cl_items_animate bit 4 — emit the glowing EFFECT_ITEM_DESPAWN particles.</summary>
    public const int AnimateParticlesBit = 4;

    // QC: this.delay starts at 0.25 and halves each puff until it reaches IT_DESPAWNFX_MIN_DELAY (0.0625), so the
    // puffs accelerate as the item nears removal. (The minimum equals IT_UPDATE_INTERVAL by coincidence of value.)
    private const float InitialDelay = 0.25f;
    private const float MinDelay = 0.0625f;

    private float _wait;       // QC .wait: absolute time the item despawns (0 = window not started yet)
    private float _delay;      // QC .delay: current inter-puff interval (halves InitialDelay -> MinDelay)
    private float _pointtime;  // QC .pointtime: absolute time the next puff is due

    /// <summary>True once the first expiring tick has seeded the window (QC <c>this.wait != 0</c>).</summary>
    public bool Started => _wait != 0f;

    /// <summary>The absolute time the item despawns (QC <c>this.wait</c>); 0 until <see cref="Started"/>.</summary>
    public float DespawnTime => _wait;

    /// <summary>The current inter-puff interval (QC <c>this.delay</c>); 0 until <see cref="Started"/>. For tests.</summary>
    public float CurrentDelay => _delay;

    /// <summary>
    /// Advance one render frame at absolute client <paramref name="time"/> (QC global <c>time</c>), honoring the
    /// <paramref name="animateFlags"/> = <c>cl_items_animate</c> bitmask. On the first call the window is seeded
    /// (QC: <c>this.wait = time + IT_DESPAWNFX_TIME; this.delay = 0.25;</c>) regardless of the bits, so the alpha
    /// reference is fixed even when only particles are enabled.
    /// </summary>
    /// <param name="alpha">
    /// The despawn alpha MULTIPLIER (QC <c>this.alpha *= (this.wait - time) / IT_DESPAWNFX_TIME</c>): 1 at the
    /// window start fading to 0 at despawn when bit 2 is set; a flat 1 when bit 2 is clear (no fade). Clamped to
    /// [0,1] (defensive against a late tick past the wait time).
    /// </param>
    /// <param name="emitPuff">
    /// True when an <c>EFFECT_ITEM_DESPAWN</c> puff is due THIS frame (QC <c>time &gt;= this.pointtime</c> with
    /// bit 4 set). The caller emits it at the item origin + (0,0,16). The cadence accelerates: the first puff is
    /// immediate, then the interval halves from 0.25 toward 0.0625.
    /// </param>
    public void Tick(float time, int animateFlags, out float alpha, out bool emitPuff)
    {
        // QC: if(!this.wait) — seed the despawn window on the first expiring frame (outside the cvar gates).
        if (_wait == 0f)
        {
            _wait = time + ItemPickupRules.DespawnFxTime;
            _delay = InitialDelay;
        }

        bool animateAlpha = (animateFlags & AnimateAlphaBit) != 0;
        bool animateParticles = (animateFlags & AnimateParticlesBit) != 0;

        // QC: if(cl_items_animate & 2) this.alpha *= (this.wait - time) / IT_DESPAWNFX_TIME;
        alpha = animateAlpha
            ? System.Math.Clamp((_wait - time) / ItemPickupRules.DespawnFxTime, 0f, 1f)
            : 1f;

        // QC: if((cl_items_animate & 4) && time >= this.pointtime) { pointparticles(...); halve delay; schedule next }
        emitPuff = false;
        if (animateParticles && time >= _pointtime)
        {
            emitPuff = true;
            if (_delay > MinDelay)
                _delay *= 0.5f;
            _pointtime = time + _delay;
        }
    }
}
