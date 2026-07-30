// Port of the world-item bob+spin animation (qcsrc/client/items/items.qc:80-142, the ItemDraw "animate" path).
// In QC this is entirely client-side: the server only sends the ITS_ANIMATE1/ITS_ANIMATE2 status bit, and the
// client floats the item on a sine wave and spins it about yaw. The pure math lives here (free of Godot, like
// ItemDespawnFx) so it's unit-testable; EntityNode applies the result to the render transform each frame.
//
//   ANIMATE1 (powerups & weapons): avelocity_y = 180;  bobheight = 10 + 8 * sin((time) * 2)
//   ANIMATE2 (health & armor):     avelocity_y = -90;   bobheight =  8 + 4 * sin((time) * 3)
//
// The bob's BASE offset (10 / 8 units) is also what lifts the model clear of the floor it rests on — without it
// a tall item (megahealth) renders half-sunk. For a resting item QC leaves anim_start_time at 0, so the wave is
// driven by absolute client time and every item of a class bobs in phase; we do the same here.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The QC <c>ItemDraw</c> bob + yaw-spin for a resting world pickup (client/items/items.qc). Pure (Godot-free)
/// so it can be unit-tested; <c>EntityNode</c> calls <see cref="Sample"/> each frame and applies the result.
/// </summary>
public static class ItemBobAnim
{
    /// <summary>ITS_ANIMATE1 — powerups &amp; weapons (high, fast bob; +180°/s spin).</summary>
    public const byte Animate1 = 1;
    /// <summary>ITS_ANIMATE2 — health &amp; armor (low, faster bob; -90°/s spin).</summary>
    public const byte Animate2 = 2;

    /// <summary>
    /// Sample the bob height (Quake Z units, added to the item's resting origin) and the accumulated yaw spin
    /// (degrees, added to the item's base yaw) for animation class <paramref name="animate"/> at client
    /// <paramref name="time"/> seconds. Returns <c>(0, 0)</c> for the static class (ammo/keys). Faithful to QC:
    /// ANIMATE1 = <c>10 + 8·sin(2t)</c> / <c>180·t</c>; ANIMATE2 = <c>8 + 4·sin(3t)</c> / <c>-90·t</c>.
    /// </summary>
    public static (float bobHeight, float yawDeg) Sample(byte animate, float time)
    {
        switch (animate)
        {
            case Animate1:
                return (10f + 8f * System.MathF.Sin(time * 2f), 180f * time);
            case Animate2:
                return (8f + 4f * System.MathF.Sin(time * 3f), -90f * time);
            default:
                return (0f, 0f);
        }
    }
}
