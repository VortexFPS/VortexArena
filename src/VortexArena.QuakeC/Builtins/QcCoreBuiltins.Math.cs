// Port of Base/darkplaces/prvm_cmds.c (VM_normalize, VM_vlen, VM_vectoyaw, VM_vectoangles, VM_random,
// VM_randomvec, VM_rint, VM_floor, VM_ceil, VM_fabs, VM_sin .. VM_tan, VM_min, VM_max, VM_bound, VM_pow,
// VM_log, VM_modulo, VM_bitshift, VM_vectorvectors, VM_changeyaw, VM_changepitch), clvm_cmds.c
// VM_CL_makevectors, and mathlib.c AngleVectors, AnglesFromVectors, VectorVectors with the mathlib.h macros.
namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    // Where the C stores into a float (prvm_vec_t, vec3_t) the port narrows at the same point; where it
    // computes in double (libm, M_PI constants) so does the port. The results are compared by the
    // program with ==, so "close" is not the standard.

    /// <summary>Source of random() and randomvec(). Replace it to make a run reproducible.</summary>
    public Random Random { get; set; } = new();

    private int _ofsForward = -1, _ofsRight = -1, _ofsUp = -1;

    private void WriteVectors(QcVector forward, QcVector right, QcVector up)
    {
        if (_ofsForward < 0)
        {
            QcDef? f = _vm.FindGlobal("v_forward"), r = _vm.FindGlobal("v_right"), u = _vm.FindGlobal("v_up");
            if (f is null || r is null || u is null) throw Fault("program has no v_forward, v_right and v_up globals");
            (_ofsForward, _ofsRight, _ofsUp) = (f.Offset, r.Offset, u.Offset);
        }
        _vm.GlobalVector(_ofsForward) = forward;
        _vm.GlobalVector(_ofsRight) = right;
        _vm.GlobalVector(_ofsUp) = up;
    }

    // #1 void(vector ang) makevectors
    private void MakeVectors(QcVm vm)
    {
        Parms(1, "VM_CL_makevectors");
        AngleVectors(vm.ArgVector(0), out QcVector forward, out QcVector right, out QcVector up);
        WriteVectors(forward, right, up);
    }

    /// <summary>mathlib.c AngleVectors: angles are (pitch, yaw, roll) in degrees.</summary>
    public static void AngleVectors(QcVector angles, out QcVector forward, out QcVector right, out QcVector up)
    {
        const double ToRadians = Math.PI * 2 / 360;
        double angle = angles.Y * ToRadians;
        double sy = Math.Sin(angle), cy = Math.Cos(angle);
        angle = angles.X * ToRadians;
        double sp = Math.Sin(angle), cp = Math.Cos(angle);
        forward = new QcVector((float)(cp * cy), (float)(cp * sy), (float)-sp);
        if (angles.Z != 0)
        {
            angle = angles.Z * ToRadians;
            double sr = Math.Sin(angle), cr = Math.Cos(angle);
            right = new QcVector((float)(-1 * (sr * sp * cy + cr * -sy)), (float)(-1 * (sr * sp * sy + cr * cy)), (float)(-1 * (sr * cp)));
            up = new QcVector((float)(cr * sp * cy + -sr * -sy), (float)(cr * sp * sy + -sr * cy), (float)(cr * cp));
        }
        else
        {
            right = new QcVector((float)sy, (float)-cy, 0);
            up = new QcVector((float)(sp * cy), (float)(sp * sy), (float)cp);
        }
    }

    // #7 float() random. lhrandom(0, 1): strictly between 0 and 1 as a double...
    private void RandomFloat(QcVm vm)
    {
        Parms(0, "VM_random");
        vm.ReturnFloat(LhRandom(0, 1));
    }

    // ...but with a 31-bit rand() the largest results round UP to 1.0f when stored in a float, and
    // QuakeC indexes arrays with floor(random() * n). DarkPlaces on Linux has that one-in-2^25 fault;
    // here the result is held below the upper bound.
    private float LhRandom(double min, double max)
    {
        float value = (float)((Random.Next() + 0.5) / 2147483648.0 * (max - min) + min);
        return value >= (float)max ? MathF.BitDecrement((float)max) : value;
    }

    // #91 vector() randomvec: a point inside the unit sphere, by rejection (mathlib.h VectorRandom).
    private void RandomVec(QcVm vm)
    {
        Parms(0, "VM_randomvec");
        QcVector v;
        do v = new QcVector(LhRandom(-1, 1), LhRandom(-1, 1), LhRandom(-1, 1));
        while (v.X * v.X + v.Y * v.Y + v.Z * v.Z > 1);
        vm.ReturnVector(v);
    }

    // #9 vector(vector v) normalize
    private void Normalize(QcVm vm)
    {
        Parms(1, "VM_normalize");
        QcVector v = vm.ArgVector(0);
        double f = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        if (f != 0)
        {
            f = 1.0 / Math.Sqrt(f);
            vm.ReturnVector(new QcVector((float)(v.X * f), (float)(v.Y * f), (float)(v.Z * f)));
        }
        else vm.ReturnVector(default);
    }

    // #12 float(vector v) vlen
    private void VLen(QcVm vm)
    {
        Parms(1, "VM_vlen");
        QcVector v = vm.ArgVector(0);
        vm.ReturnFloat((float)Math.Sqrt((double)(v.X * v.X + v.Y * v.Y + v.Z * v.Z)));
    }

    // #13 float(vector v) vectoyaw. The (int) cast truncates toward zero BEFORE the negative wrap, so a
    // yaw of -0.5 degrees is 0, not 359.
    private void VecToYaw(QcVm vm)
    {
        Parms(1, "VM_vectoyaw");
        QcVector v = vm.ArgVector(0);
        float yaw;
        if (v.Y == 0 && v.X == 0) yaw = 0;
        else
        {
            yaw = (int)(Math.Atan2(v.Y, v.X) * 180 / Math.PI);
            if (yaw < 0) yaw += 360;
        }
        vm.ReturnFloat(yaw);
    }

    // #51 vector(vector forward[, vector up]) vectoangles. Unlike vectoyaw nothing is truncated.
    private void VecToAngles(QcVm vm)
    {
        Parms(1, 2, "VM_vectoangles");
        vm.ReturnVector(vm.ArgCount >= 2 ? AnglesFromVectors(vm.ArgVector(0), vm.ArgVector(1)) : AnglesFromVectors(vm.ArgVector(0), null));
    }

    /// <summary>mathlib.c AnglesFromVectors with flippitch set, as VM_vectoangles calls it.</summary>
    public static QcVector AnglesFromVectors(QcVector forward, QcVector? upOrNull)
    {
        float pitch, yaw, roll;
        if (forward.X == 0 && forward.Y == 0)
        {
            if (forward.Z > 0)
            {
                pitch = (float)(-Math.PI * 0.5);
                yaw = upOrNull is QcVector up ? (float)Math.Atan2(-up.Y, -up.X) : 0;
            }
            else
            {
                pitch = (float)(Math.PI * 0.5);
                yaw = upOrNull is QcVector up ? (float)Math.Atan2(up.Y, up.X) : 0;
            }
            roll = 0;
        }
        else
        {
            yaw = (float)Math.Atan2(forward.Y, forward.X);
            pitch = (float)-Math.Atan2(forward.Z, Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
            if (upOrNull is QcVector up)
            {
                float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
                float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
                float leftDot = up.X * -sy + up.Y * cy + up.Z * 0;
                float upDot = up.X * (sp * cy) + up.Y * (sp * sy) + up.Z * cp;
                roll = (float)-Math.Atan2(leftDot, upDot);
            }
            else roll = 0;
        }

        const double ToDegrees = 180.0 / Math.PI;
        pitch = (float)(pitch * ToDegrees);
        yaw = (float)(yaw * ToDegrees);
        roll = (float)(roll * ToDegrees);
        pitch *= -1;
        if (pitch < 0) pitch += 360;
        if (yaw < 0) yaw += 360;
        if (roll < 0) roll += 360;
        return new QcVector(pitch, yaw, roll);
    }

    // #432 void(vector dir) vectorvectors
    private void VectorVectors(QcVm vm)
    {
        Parms(1, "VM_vectorvectors");
        QcVector forward = Normalize2(vm.ArgVector(0));
        QcVector right, up;
        if (forward.X == 0 && forward.Y == 0)
        {
            right = new QcVector(0, -1, 0);
            up = new QcVector(forward.Z > 0 ? -1 : 1, 0, 0);
        }
        else
        {
            right = Normalize2(new QcVector(forward.Y, -forward.X, 0));
            up = Normalize2(new QcVector(-forward.Z * forward.X, -forward.Z * forward.Y, forward.X * forward.X + forward.Y * forward.Y));
        }
        WriteVectors(forward, right, up);
    }

    // mathlib.h VectorNormalize / VectorNormalize2: the reciprocal length is narrowed to float first.
    private static QcVector Normalize2(QcVector v)
    {
        float inverse = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        if (inverse != 0) inverse = (float)(1.0f / Math.Sqrt(inverse));
        return new QcVector(v.X * inverse, v.Y * inverse, v.Z * inverse);
    }

    // #36 float(float v) rint: halves round away from zero, not to even as C's rint() would.
    private void Rint(QcVm vm)
    {
        Parms(1, "VM_rint");
        float f = vm.ArgFloat(0);
        vm.ReturnFloat(f > 0 ? (float)Math.Floor(f + 0.5) : (float)Math.Ceiling(f - 0.5));
    }

    private void Floor(QcVm vm) { Parms(1, "VM_floor"); vm.ReturnFloat((float)Math.Floor(vm.ArgFloat(0))); }
    private void Ceil(QcVm vm) { Parms(1, "VM_ceil"); vm.ReturnFloat((float)Math.Ceiling(vm.ArgFloat(0))); }
    private void FAbs(QcVm vm) { Parms(1, "VM_fabs"); vm.ReturnFloat(Math.Abs(vm.ArgFloat(0))); }
    private void Sin(QcVm vm) { Parms(1, "VM_sin"); vm.ReturnFloat((float)Math.Sin(vm.ArgFloat(0))); }
    private void Cos(QcVm vm) { Parms(1, "VM_cos"); vm.ReturnFloat((float)Math.Cos(vm.ArgFloat(0))); }
    private void Sqrt(QcVm vm) { Parms(1, "VM_sqrt"); vm.ReturnFloat((float)Math.Sqrt(vm.ArgFloat(0))); }
    private void ASin(QcVm vm) { Parms(1, "VM_asin"); vm.ReturnFloat((float)Math.Asin(vm.ArgFloat(0))); }
    private void ACos(QcVm vm) { Parms(1, "VM_acos"); vm.ReturnFloat((float)Math.Acos(vm.ArgFloat(0))); }
    private void ATan(QcVm vm) { Parms(1, "VM_atan"); vm.ReturnFloat((float)Math.Atan(vm.ArgFloat(0))); }
    private void ATan2(QcVm vm) { Parms(2, "VM_atan2"); vm.ReturnFloat((float)Math.Atan2(vm.ArgFloat(0), vm.ArgFloat(1))); }
    private void Tan(QcVm vm) { Parms(1, "VM_tan"); vm.ReturnFloat((float)Math.Tan(vm.ArgFloat(0))); }
    private void Pow(QcVm vm) { Parms(2, "VM_pow"); vm.ReturnFloat((float)Math.Pow(vm.ArgFloat(0), vm.ArgFloat(1))); }
    private void Log(QcVm vm) { Parms(1, "VM_log"); vm.ReturnFloat((float)Math.Log(vm.ArgFloat(0))); }

    // #94 / #95 float(float a, float b, ...) min / max. Written as the C macros are, because that fixes
    // what a NaN argument does: the comparison is false, so the SECOND operand wins.
    private void Min(QcVm vm)
    {
        Parms(2, 8, "VM_min");
        float f = vm.ArgFloat(0);
        if (vm.ArgCount >= 3)
        {
            for (int i = 1; i < vm.ArgCount; i++)
                if (f > vm.ArgFloat(i)) f = vm.ArgFloat(i);
        }
        else f = f < vm.ArgFloat(1) ? f : vm.ArgFloat(1);
        vm.ReturnFloat(f);
    }

    private void Max(QcVm vm)
    {
        Parms(2, 8, "VM_max");
        float f = vm.ArgFloat(0);
        if (vm.ArgCount >= 3)
        {
            for (int i = 1; i < vm.ArgCount; i++)
                if (f < vm.ArgFloat(i)) f = vm.ArgFloat(i);
        }
        else f = f > vm.ArgFloat(1) ? f : vm.ArgFloat(1);
        vm.ReturnFloat(f);
    }

    // #96 float(float minimum, float val, float maximum) bound. The lower bound wins when they cross.
    private void Bound(QcVm vm)
    {
        Parms(3, "VM_bound");
        float min = vm.ArgFloat(0), num = vm.ArgFloat(1), max = vm.ArgFloat(2);
        vm.ReturnFloat(num >= min ? (num < max ? num : max) : min);
    }

    // #245 float(float val, float m) mod: truncated, so the result takes the sign of val.
    private void Modulo(QcVm vm)
    {
        Parms(2, "VM_modulo");
        float val = vm.ArgFloat(0), m = vm.ArgFloat(1);
        if (m != 0) vm.ReturnFloat(val - m * QcVm.FloatToInt(val / m));
        else
        {
            Warning($"Attempted modulo of {val.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} by zero\n");
            vm.ReturnFloat(0);
        }
    }

    // #218 float(float number, float quantity) bitshift. The number's sign is discarded; a negative
    // quantity shifts right. The count wraps at 32 as the x86 shift instructions do (C# agrees).
    private void BitShift(QcVm vm)
    {
        Parms(2, "VM_bitshift");
        int n1 = QcVm.FloatToInt(Math.Abs((float)QcVm.FloatToInt(vm.ArgFloat(0))));
        int n2 = QcVm.FloatToInt(vm.ArgFloat(1));
        if (n1 == 0) vm.ReturnFloat(0);
        else if (n2 < 0) vm.ReturnFloat(n1 >> unchecked(-n2));
        else vm.ReturnFloat(n1 << n2);
    }

    // #49 void() ChangeYaw: turns self.angles_y toward self.ideal_yaw by at most self.yaw_speed.
    private void ChangeYaw(QcVm vm)
    {
        // No parameter check: DarkPlaces calls this from movetogoal with that builtin's arguments.
        QcDef? self = vm.FindGlobal("self");
        if (self is null) throw Fault("changeyaw: program has no self global");
        TurnToward(vm.GlobalInt(self.Offset), 1, "ideal_yaw", "yaw_speed", "changeyaw");
    }

    // #63 void(entity ent) changepitch
    private void ChangePitch(QcVm vm)
    {
        Parms(1, "VM_changepitch");
        TurnToward(vm.ArgEdict(0), 0, "idealpitch", "pitch_speed", "changepitch");
    }

    private void TurnToward(int edict, int axis, string idealField, string speedField, string name)
    {
        if (edict == 0) { Warning($"{name}: can not modify world entity\n"); return; }
        if (_vm.IsFree(edict)) { Warning($"{name}: can not modify free entity\n"); return; }
        QcDef? angles = _vm.FindField("angles"), idealDef = _vm.FindField(idealField), speedDef = _vm.FindField(speedField);
        if (angles is null || idealDef is null || speedDef is null) throw Fault($"{name}: program has no .angles, .{idealField} and .{speedField} fields");

        ref float angle = ref _vm.FieldFloat(edict, angles.Offset + axis);
        float current = AngleMod(angle);
        float ideal = _vm.FieldFloat(edict, idealDef.Offset), speed = _vm.FieldFloat(edict, speedDef.Offset);
        if (current == ideal) return;

        float move = ideal - current;
        if (ideal > current) { if (move >= 180) move -= 360; }
        else if (move <= -180) move += 360;
        if (move > 0) { if (move > speed) move = speed; }
        else if (move < -speed) move = -speed;

        current += move;
        angle = AngleMod(current);
    }

    // mathlib.h ANGLEMOD
    private static float AngleMod(float a) => (float)(a - 360.0 * Math.Floor(a / 360.0));
}
