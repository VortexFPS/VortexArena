using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// Button bits sampled into an <see cref="InputCommand"/>. Mirrors the QC <c>input_buttons</c> bitfield
/// (BIT(0) attack, BIT(1) jump, BIT(4) crouch, …). Exact bit assignments are gameplay-defined and will
/// be finalised with the movement port; these cover the movement-relevant ones the predictor needs.
/// </summary>
[Flags]
public enum InputButtons
{
    None = 0,
    Attack = 1 << 0,   // BIT(0)
    Jump = 1 << 1,     // BIT(1) — read by the bob/refdef code (input_buttons & BIT(1))
    Attack2 = 1 << 2,  // BIT(2)
    Zoom = 1 << 3,     // BIT(3)
    Crouch = 1 << 4,   // BIT(4)
    Use = 1 << 5,      // BIT(5)
    Hook = 1 << 6,     // BIT(6) — PHYS_INPUT_BUTTON_HOOK (the +hook / offhand-fire button). Drives the
                       // offhand-weapon think (grapple hook, offhand blaster, nade prime/throw).
    Chat = 1 << 7,     // PHYS_INPUT_BUTTON_CHAT — the player is typing (chat prompt / console open). Carried
                       // so the server knows to exempt the typist from camp-check / type-frag etc. The client
                       // sets it even while ordinary input is suppressed (movement keys are zeroed while typing,
                       // but the typing FLAG itself must still reach the server). Fits the existing 1-byte field.
}

/// <summary>
/// One sampled frame of client input — the unit the client predicts on and the server applies
/// authoritatively. The C# successor to the engine's <c>usercmd</c> as consumed by
/// <c>cl_player.qc</c> (<c>getinputstate</c> / <c>CSQCPlayer_PredictTo</c>): a sequence number plus the
/// movement intent for one tick.
///
/// A plain mutable <c>struct</c> on purpose: these live in a ring buffer (<see cref="PredictionBuffer"/>),
/// are copied by value, and never individually heap-allocate. Keep it blittable-ish and small.
/// </summary>
public struct InputCommand
{
    /// <summary>Monotonic command sequence number (the client's <c>clientcommandframe</c>). The server acks
    /// the last one it processed; the client replays all unacked commands after that ack.</summary>
    public uint Seq;

    /// <summary>View angles in degrees (pitch, yaw, roll) — the aim direction for this frame
    /// (<c>PHYS_INPUT_ANGLES</c> / <c>v_angle</c>).</summary>
    public Vector3 ViewAngles;

    /// <summary>Forward move axis (+forward / -back), in the engine's input-scale units.</summary>
    public float Forward;

    /// <summary>Side move axis (+right / -left).</summary>
    public float Side;

    /// <summary>Up move axis (+jump/swim-up / -crouch/swim-down).</summary>
    public float Up;

    /// <summary>Pressed buttons this frame (see <see cref="InputButtons"/>); stored as raw int for wire compactness.</summary>
    public int Buttons;

    /// <summary>
    /// The one-shot client impulse riding this command (QC <c>usercmd.impulse</c> → <c>CS(this).impulse</c>) — a
    /// weapon-switch / reload number (common/impulses/all.qh: group 1..9/14, by-id 230..253, next/prev/last/best/
    /// reload 10..20). 0 = no impulse. Edge-triggered: the client stamps it on the NEXT command after a weapon key
    /// press, then clears its pending value, so it is carried (and processed) exactly once. The server dispatches
    /// it through the gated <c>impulse</c> command path (<see cref="WeaponImpulses"/>) and then zeroes it on the
    /// cached command, mirroring QC's set-then-zero (impulse.qc:375-377) so the starve-repeat doesn't re-fire it.
    /// Sent as a byte (0..253) on the wire.
    /// </summary>
    public int Impulse;

    /// <summary>Frame duration in seconds the movement step integrates over (<c>PHYS_INPUT_TIMELENGTH</c> /
    /// <c>input_timelength</c>). At a fixed 72 Hz tick this is ~0.0139s, but it is sent so the server applies the
    /// exact dt the client predicted with.</summary>
    public float DeltaTime;

    public readonly InputButtons TypedButtons => (InputButtons)Buttons;

    /// <summary>
    /// Serialize as the C2S input payload. View angles use the float path (aim precision matters for
    /// hit registration / lag-comp); move axes are bytes scaled by <see cref="MoveScale"/> (the QC input
    /// scale clamps movement to roughly ±sv_maxspeed, so a signed byte grid is plenty); dt rides the
    /// approx-past-time-style nonlinearity is overkill here, so we send it as a float for fidelity.
    /// </summary>
    public readonly void Serialize(BitWriter w)
    {
        w.WriteULong(Seq);
        w.WriteAngles(ViewAngles, NetPrecision.Float); // full-precision aim
        w.WriteSByte(EncodeMove(Forward));
        w.WriteSByte(EncodeMove(Side));
        w.WriteSByte(EncodeMove(Up));
        w.WriteByte(Buttons & 0xFF);
        // The impulse rides as a byte (QC usercmd.impulse is one byte; valid weapon impulses are 0..253). Clamp
        // defensively so an out-of-range value never corrupts the byte stream. ANY change to this layout requires
        // bumping NetProtocol.ProtocolVersion (it folds into the build-parity handshake).
        w.WriteByte(Impulse < 0 ? 0 : (Impulse > 255 ? 255 : Impulse));
        w.WriteFloat(DeltaTime);
    }

    /// <summary>Read back an input payload written by <see cref="Serialize"/>.</summary>
    public static InputCommand Deserialize(ref BitReader r)
    {
        InputCommand c = default;
        c.Seq = r.ReadULong();
        c.ViewAngles = r.ReadAngles(NetPrecision.Float);
        c.Forward = DecodeMove(r.ReadSByte());
        c.Side = DecodeMove(r.ReadSByte());
        c.Up = DecodeMove(r.ReadSByte());
        c.Buttons = r.ReadByte();
        c.Impulse = r.ReadByte();
        c.DeltaTime = r.ReadFloat();
        return c;
    }

    // Movement axes are quantized to a signed byte. 127 maps to full input deflection; the movement
    // code re-scales against sv_maxspeed, so finer resolution buys nothing.
    private const float MoveScale = 127.0f / 1.0f; // input axes are normalised to [-1, 1] before sampling

    private static int EncodeMove(float v)
    {
        int q = Quantize.Rint(v * MoveScale);
        return q < -127 ? -127 : (q > 127 ? 127 : q);
    }

    private static float DecodeMove(int q) => q / MoveScale;
}

/// <summary>
/// Owner-replicated player state — the C# successor to the Xonotic *stat set* (<c>qcsrc/lib/stats.qh</c>),
/// modelling the stat *definitions* (health/armor/ammo/movevars) as plain typed fields. Per the
/// networking spec we deliberately <b>drop</b> the engine's fixed 256-slot stat array and the
/// <c>MAGIC_STATS</c> reserved-index mechanism — there is no such cap in XonoticGodot; this is just the data
/// the server replicates to the owning client (and the prediction reads movement vars from).
///
/// This is intentionally not the full stat catalogue — it is the movement/HUD-critical subset needed by
/// the prediction + reconcile loop. The complete, registry-driven stat table (with <c>[NetProperty]</c>
/// quantization tables) is a later deliverable.
/// </summary>
public struct PlayerState
{
    // --- HUD / gameplay stats (stats.qh STAT_HEALTH, STAT_ARMOR, ammo) ---
    public int Health;
    public int Armor;
    public int Ammo;

    // --- movement vars the predictor must match the server on (stats.qh MOVEVARS_*) ---
    // These are replicated so client prediction integrates with identical constants (determinism).
    public float MaxSpeed;        // MOVEVARS_MAXSPEED
    public float Accelerate;      // MOVEVARS_ACCELERATE
    public float AirAccelerate;   // MOVEVARS_AIRACCELERATE
    public float Friction;        // MOVEVARS_FRICTION
    public float StopSpeed;       // MOVEVARS_STOPSPEED
    public float JumpVelocity;    // MOVEVARS_JUMPVELOCITY
    public float Gravity;         // MOVEVARS_GRAVITY
    public float StepHeight;      // MOVEVARS_STEPHEIGHT

    // --- predicted pmove flags snapshot (cl_player.qc PMF_* / pmove_flags) ---
    public bool OnGround;         // PMF_ONGROUND
    public bool Ducked;           // PMF_DUCKED
    public bool JumpHeld;         // PMF_JUMP_HELD
}
