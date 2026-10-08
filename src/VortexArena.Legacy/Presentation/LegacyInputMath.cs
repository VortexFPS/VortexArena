// Port of Base/darkplaces/cl_input.c CL_Input: the keyboard part of the move ("cl.cmd.forwardmove +=
// cl_forwardspeed * CL_KeyState(&in_forward)" and its siblings), the mouse-look block ("cl.viewangles[YAW]
// -= m_yaw * in_mouse_x * modulatedsensitivity * cl.viewzoom") with the pitch clamp of CL_AdjustAngles,
// and the button bit layout that follows it ("if (in_attack.state & 3) bits |= 1" ...).
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Presentation;

/// <summary>
/// What is held down, in DarkPlaces' terms. Xonotic's configuration aliases its own names onto these
/// (xonotic-client.cfg): +fire is +attack, +fire2 is +button3, +zoom is +button4, +crouch is +button5,
/// +hook is +button6, +jetpack is +button10.
/// </summary>
public struct LegacyHeldButtons
{
    public bool Forward, Back, MoveLeft, MoveRight, MoveUp, MoveDown;
    public bool Attack, Jump, Button3, Button4, Button5, Button6, Button7, Button8, Use;
    public bool Button9, Button10, Button11, Button12, Button13, Button14, Button15, Button16;
    /// <summary>+speed: multiplies the keyboard move by cl_movespeedkey.</summary>
    public bool Speed;
}

/// <summary>cl_forwardspeed and friends, as the session's configuration has them.</summary>
public struct LegacyMoveSpeeds
{
    public float Forward, Back, Side, Up, SpeedKey;

    /// <summary>DarkPlaces' defaults (cl_forwardspeed 400, cl_backspeed 400, cl_sidespeed 350, cl_upspeed 400, cl_movespeedkey 2).</summary>
    public static LegacyMoveSpeeds Default => new() { Forward = 400, Back = 400, Side = 350, Up = 400, SpeedKey = 2 };
}

public static class LegacyInputMath
{
    /// <summary>
    /// The buttons field of the input command. Bit 512 is set whenever the game does not have the
    /// keyboard ("key_dest != key_game || key_consoleactive || !vid_activewindow"): it is what puts
    /// the chat bubble over a player with the console or a menu open. Bit 1024 (the prydon cursor) is
    /// never set.
    /// </summary>
    public static int Buttons(in LegacyHeldButtons held, bool gameHasKeyFocus)
    {
        int bits = 0;
        if (held.Attack) bits |= 1;
        if (held.Jump) bits |= 2;
        if (held.Button3) bits |= 4;
        if (held.Button4) bits |= 8;
        if (held.Button5) bits |= 16;
        if (held.Button6) bits |= 32;
        if (held.Button7) bits |= 64;
        if (held.Button8) bits |= 128;
        if (held.Use) bits |= 256;
        if (!gameHasKeyFocus) bits |= 512;
        if (held.Button9) bits |= 2048;
        if (held.Button10) bits |= 4096;
        if (held.Button11) bits |= 8192;
        if (held.Button12) bits |= 16384;
        if (held.Button13) bits |= 32768;
        if (held.Button14) bits |= 65536;
        if (held.Button15) bits |= 131072;
        if (held.Button16) bits |= 262144;
        return bits;
    }

    /// <summary>The keyboard's share of forwardmove / sidemove / upmove, in units per second.</summary>
    public static void Move(in LegacyHeldButtons held, in LegacyMoveSpeeds speeds, out float forward, out float side, out float up)
    {
        side = speeds.Side * ((held.MoveRight ? 1 : 0) - (held.MoveLeft ? 1 : 0));
        up = speeds.Up * ((held.MoveUp ? 1 : 0) - (held.MoveDown ? 1 : 0));
        forward = (held.Forward ? speeds.Forward : 0) - (held.Back ? speeds.Back : 0);
        if (held.Speed)
        {
            forward *= speeds.SpeedKey;
            side *= speeds.SpeedKey;
            up *= speeds.SpeedKey;
        }
    }

    /// <summary>
    /// Mouse look: yaw turns against the mouse's X, pitch with its Y, both scaled by the sensitivity,
    /// the program's setsensitivityscale and the zoom; then pitch is held to in_pitch_min..in_pitch_max.
    /// </summary>
    /// <param name="dx">Mouse movement since the last frame, in counts (right and down positive).</param>
    /// <param name="yawPerCount">m_yaw (0.022). <paramref name="pitchPerCount"/> is m_pitch, negative for an inverted mouse.</param>
    public static QcVector MouseLook(QcVector angles, float dx, float dy, float sensitivity, float sensitivityScale, float viewZoom,
        float yawPerCount = 0.022f, float pitchPerCount = 0.022f, float pitchMin = -90, float pitchMax = 90)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) return angles;
        float modulated = sensitivity * sensitivityScale;
        angles.Y -= yawPerCount * dx * modulated * viewZoom;
        angles.X += pitchPerCount * dy * modulated * viewZoom;
        // CL_AdjustAngles: "cl.viewangles[PITCH] = bound(in_pitch_min, pitch, in_pitch_max)". Yaw is left
        // unwrapped, as in the C, where the wire encoding takes it modulo 360.
        angles.X = angles.X >= pitchMin ? (angles.X < pitchMax ? angles.X : pitchMax) : pitchMin;
        return angles;
    }
}
