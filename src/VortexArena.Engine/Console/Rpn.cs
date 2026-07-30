// Port of Base/data/xonotic-data.pk3dir/qcsrc/common/command/rpn.qc (GenericCommand_rpn, by divVerent).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using XonoticGodot.Common.Math; // QMath.Bound (DP-exact bound macro)
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation; // CvarService

namespace XonoticGodot.Engine.Console;

/// <summary>
/// The RPN calculator VM — the C# successor to <c>GenericCommand_rpn</c> (common/command/rpn.qc). A stack-based
/// reverse-Polish evaluator over strings, used by the stock cfgs/quickmenu to compute cvar values
/// (<c>rpn /x 2 3 add def</c>), do set arithmetic on word lists (<c>rpn "a b" "b c" union</c>), and read cvars
/// by bare name. Pure and Godot-free: it reads/writes cvars through the injected <see cref="CvarService"/> and
/// prints diagnostics through a <c>print</c> sink, exactly mirroring QC's <c>cvar_string</c>/<c>cvar_set</c>/
/// <c>registercvar</c> + <c>LOG_INFO</c>.
///
/// <para><b>%.9g parity (trap R2):</b> QC pushes floats with <c>sprintf("%.9g", f)</c> and pops them with
/// <c>stof</c>; the exact string format is load-bearing (round-trips through the stack into cvars). This port
/// reproduces C's <c>%.9g</c> in <see cref="Format9g"/> (9 significant digits, trailing zeros stripped,
/// %e vs %f chosen by exponent) rather than .NET's <c>G9</c> (which differs on exponent form / casing).</para>
///
/// <para><b>Scope (deviation, trap R3):</b> the arithmetic / stack / logic / compare / min-max-bound-when /
/// def-defs-load-dup-exch-pop-clear / set ops (union/intersection/difference/shuffle) / sprintf1s / crc16 /
/// bare-cvar default / underflow+leftover prints are ALL ported (covering every shipped cfg/quickmenu use).
/// The persistent-DB family (<c>put/get/dbpush…dbgoto</c>) and <c>localtime/gmtime/digest/fexists/
/// fexists_assert/eval</c> are NOT implemented — there is no QC-style hashtable DB / file layer here.
/// A cfg that calls one of those falls through to the DEFAULT branch (push <c>cvar_string(token)</c>, like any
/// unknown token — DP's documented fallback), and logs an "unsupported rpn op" line when <c>developer</c>&gt;0,
/// so the behaviour is faithful (no crash, no mis-push) and visible rather than silent. <c>time</c> IS ported
/// (it pushes the sim clock, QC rpn.qc:547-548 <c>rpn_pushf(time)</c>) since stock demoseeking.cfg needs it.</para>
/// </summary>
public static class Rpn
{
    private const int MaxStack = 128; // QC MAX_RPN_STACK

    private sealed class State
    {
        public readonly string[] Stack = new string[MaxStack];
        public int Sp;
        public bool Error;
        public readonly CvarService Cvars;
        public readonly Action<string> Print;

        public State(CvarService cvars, Action<string> print) { Cvars = cvars; Print = print; }

        // QC rpn_pop/push/get/set — string views.
        public string Pop()
        {
            if (Sp > 0) { --Sp; return Stack[Sp]; }
            Print("rpn: stack underflow");
            Error = true;
            return "";
        }
        public void Push(string s)
        {
            if (Sp < MaxStack) { Stack[Sp] = s; ++Sp; }
            else { Print("rpn: stack overflow"); Error = true; }
        }
        public string Get()
        {
            if (Sp > 0) return Stack[Sp - 1];
            Print("rpn: empty stack");
            Error = true;
            return "";
        }
        public void Set(string s)
        {
            if (Sp > 0) Stack[Sp - 1] = s;
            else { Print("rpn: empty stack"); Error = true; }
        }

        // QC rpn_getf/popf/pushf/setf — float views (stof / sprintf("%.9g", f)).
        public float GetF() => Stof(Get());
        public float PopF() => Stof(Pop());
        public void PushF(float f) => Push(Format9g(f));
        public void SetF(float f) => Set(Format9g(f));
    }

    /// <summary>
    /// QC <c>GenericCommand_rpn</c> command branch: evaluate <paramref name="argv"/>[1..] as an RPN expression.
    /// <paramref name="argv"/>[0] is the command name (<c>rpn</c>). Mutates cvars (via def/defs/load) and prints
    /// underflow/leftover/unsupported diagnostics. Mirrors QC: reset the stack, run each token, break on error,
    /// then drain the leftover stack with "rpn: still on stack: &lt;s&gt;".
    /// </summary>
    public static void Run(IReadOnlyList<string> argv, CvarService cvars, Action<string> print)
    {
        if (argv is null || argv.Count < 2)
            return; // QC: argc < 2 → nothing to do (the usage branch is the console's help)

        var st = new State(cvars, print);
        st.Sp = 0;
        st.Error = false;

        for (int pos = 1; pos < argv.Count; ++pos)
        {
            string cmd = argv[pos] ?? "";
            Step(st, cmd);
            if (st.Error)
                break;
        }

        // QC: drain anything left on the stack, reporting each entry.
        while (st.Sp > 0)
            print($"rpn: still on stack: {st.Pop()}");
    }

    private static void Step(State st, string cmd)
    {
        int len = cmd.Length;

        // ---- literal pushes (QC rpn.qc:88-98) ----
        if (cmd == "")
            return;
        // first char is a digit > 0, or '0' → push the token literally (a number literal)
        char c0 = cmd[0];
        if (Stof(cmd.Substring(0, 1)) > 0f) { st.Push(cmd); return; }
        if (c0 == '0') { st.Push(cmd); return; }
        if (len >= 2 && c0 == '+') { st.Push(cmd); return; }   // signed number "+5"
        if (len >= 2 && c0 == '-') { st.Push(cmd); return; }   // signed number "-5"
        if (len >= 2 && c0 == '/') { st.Push(cmd.Substring(1)); return; } // quoted string literal "/abc"

        switch (cmd)
        {
            // ---- stack ops ----
            case "clear":
                st.Sp = 0;
                return;
            case "def":
            case "=":
            {
                string s = st.Pop();
                string name = st.Pop();
                if (name != "")
                {
                    st.Cvars.Register(name, "");
                    if (!st.Error) // don't change cvars if a stack error had happened
                        st.Cvars.Set(name, s);
                }
                else
                {
                    st.Print("rpn: empty cvar name for 'def'");
                    st.Error = true;
                }
                return;
            }
            case "defs":
            case "@":
            {
                string s = "";
                float i = st.PopF();
                bool j = (i == 0f);
                while (st.Sp > 1 && (j || i > 0f))
                {
                    s = "/" + st.Pop() + " " + s;
                    --i;
                }
                string name = st.Pop();
                if (name != "")
                {
                    st.Cvars.Register(name, "");
                    if (!st.Error)
                        st.Cvars.Set(name, s);
                }
                else
                {
                    st.Print("rpn: empty cvar name for 'defs'");
                    st.Error = true;
                }
                return;
            }
            case "load":
            {
                string s = st.Get();
                if (s != "") st.Set(st.Cvars.GetString(s));
                else { st.Print("rpn: empty cvar name for 'load'"); st.Error = true; }
                return;
            }
            case "exch":
            {
                string s = st.Pop();
                string s2 = st.Get();
                st.Set(s);
                st.Push(s2);
                return;
            }
            case "dup":
                st.Push(st.Get());
                return;
            case "pop":
                st.Pop();
                return;

            // ---- arithmetic / bitwise / logic (QC uses float ops on stof-views) ----
            case "add": case "+": { float f = st.PopF(); st.SetF(st.GetF() + f); return; }
            case "sub": case "-": { float f = st.PopF(); st.SetF(st.GetF() - f); return; }
            case "mul": case "*": { float f = st.PopF(); st.SetF(st.GetF() * f); return; }
            case "div": case "/": { float f = st.PopF(); st.SetF(st.GetF() / f); return; }
            case "mod": case "%": { float f = st.PopF(); float f2 = st.GetF(); st.SetF(f2 - f * MathF.Floor(f2 / f)); return; }
            case "pow": case "**": { float f = st.PopF(); st.SetF(MathF.Pow(st.GetF(), f)); return; }
            case "bitand": case "&": { float f = st.PopF(); st.SetF(BitOp(st.GetF(), f, '&')); return; }
            case "bitor": case "|": { float f = st.PopF(); st.SetF(BitOp(st.GetF(), f, '|')); return; }
            case "bitxor": case "^": { float f = st.PopF(); st.SetF(BitOp(st.GetF(), f, '^')); return; }
            case "and": case "&&": { float f = st.PopF(); st.SetF(Bool(st.GetF()) && Bool(f) ? 1f : 0f); return; }
            case "or": case "||": { float f = st.PopF(); st.SetF(Bool(st.GetF()) || Bool(f) ? 1f : 0f); return; }
            case "xor": case "^^": { float f = st.PopF(); st.SetF((!Bool(st.GetF())) != (!Bool(f)) ? 1f : 0f); return; }
            case "bitnot": st.SetF(~(int)st.GetF()); return;
            case "not": st.SetF(Bool(st.GetF()) ? 0f : 1f); return;
            case "abs": st.SetF(MathF.Abs(st.GetF())); return;
            case "sgn":
            {
                float f = st.GetF();
                st.Set(f < 0f ? "-1" : (f > 0f ? "1" : "0"));
                return;
            }
            case "neg": case "~": st.SetF(-st.GetF()); return;
            case "floor": case "f": st.SetF(MathF.Floor(st.GetF())); return;
            case "ceil": case "c": st.SetF(MathF.Ceiling(st.GetF())); return;
            case "exp": st.SetF(MathF.Exp(st.GetF())); return;
            case "log": st.SetF(MathF.Log(st.GetF())); return;
            case "sin": st.SetF(MathF.Sin(st.GetF())); return;
            case "cos": st.SetF(MathF.Cos(st.GetF())); return;
            case "max": { float f = st.PopF(); float f2 = st.GetF(); st.SetF(MathF.Max(f2, f)); return; }
            case "min": { float f = st.PopF(); float f2 = st.GetF(); st.SetF(MathF.Min(f2, f)); return; }
            case "bound":
            {
                // QC: f=pop (top), f2=pop (second), f3=get (third, peeked); rpn_setf(bound(f3, f2, f)).
                // bound(min, num, max) clamps the SECOND-popped value (f2) between the deepest (f3) and the
                // top (f); the result lands in f3's (peeked) slot. The "bounds the middle number" usage line.
                float f = st.PopF();   // max
                float f2 = st.PopF();  // num (the value being clamped)
                float f3 = st.GetF();  // min
                st.SetF(BoundQc(f3, f2, f)); // QC bound(f3, f2, f) == bound(min, num, max)
                return;
            }
            case "when":
            {
                float f = st.PopF();    // cond
                string s = st.Pop();    // value if false
                string s2 = st.Get();   // value if true (peeked)
                st.Set(Bool(f) ? s2 : s);
                return;
            }

            // ---- comparisons (push 1/0) ----
            case ">": case "gt": { float f = st.PopF(); st.SetF(st.GetF() > f ? 1f : 0f); return; }
            case "<": case "lt": { float f = st.PopF(); st.SetF(st.GetF() < f ? 1f : 0f); return; }
            case "==": case "eq": { float f = st.PopF(); st.SetF(st.GetF() == f ? 1f : 0f); return; }
            case ">=": case "ge": { float f = st.PopF(); st.SetF(st.GetF() >= f ? 1f : 0f); return; }
            case "<=": case "le": { float f = st.PopF(); st.SetF(st.GetF() <= f ? 1f : 0f); return; }
            case "!=": case "ne": { float f = st.PopF(); st.SetF(st.GetF() != f ? 1f : 0f); return; }

            case "rand":
                // QC: ceil(random() * top) - 1. Uses the shared RNG.
                st.SetF(MathF.Ceiling(Rng() * st.GetF()) - 1f);
                return;
            case "crc16":
                st.SetF(Crc16(st.Get()));
                return;

            // ---- set/string ops over space-separated word lists ----
            case "union":
            {
                string s2 = st.Pop();
                string s = st.Get();
                List<string> a = WordList.Words(s);
                List<string> b = WordList.Words(s2);
                // UNION: all of a, plus those of b not already in a.
                List<string> outw = new(a);
                foreach (string w in b)
                    if (!a.Contains(w)) outw.Add(w);
                st.Set(WordList.Join(outw));
                return;
            }
            case "intersection":
            {
                string s2 = st.Pop();
                string s = st.Get();
                List<string> a = WordList.Words(s);
                List<string> b = WordList.Words(s2);
                // INTERSECTION: keep only the words of a that are also in b (a's order).
                List<string> outw = new();
                foreach (string w in a)
                    if (b.Contains(w)) outw.Add(w);
                st.Set(WordList.Join(outw));
                return;
            }
            case "difference":
            {
                string s2 = st.Pop();
                string s = st.Get();
                List<string> a = WordList.Words(s);
                List<string> b = WordList.Words(s2);
                // DIFFERENCE: keep only the words of a that are NOT in b.
                List<string> outw = new();
                foreach (string w in a)
                    if (!b.Contains(w)) outw.Add(w);
                st.Set(WordList.Join(outw));
                return;
            }
            case "shuffle":
                st.Set(WordList.Shuffle(st.Get(), SharedRng));
                return;

            case "sprintf1s":
            {
                string fmt = st.Pop();
                st.Set(Sprintf1s(fmt, st.Get()));
                return;
            }

            // ---- time: push the VM clock (QC rpn.qc:547-548 rpn_pushf(time)). stock demoseeking.cfg depends on
            //      this; it is NOT a documented fallback like the DB/digest/eval family below. Push the sim clock
            //      (Api.Clock.Time) when the services facade is wired, else 0 (no clock outside a live world). ----
            case "time":
                st.PushF(Api.Services is not null ? Api.Clock.Time : 0f);
                return;

            // ---- deferred ops (DB family + digest/fexists/eval): no analog in this port. Fall to the
            //      default cvar-push (DP's unknown-token behaviour) and warn when developer>0. See class doc. ----
            case "put": case "get":
            case "dbpush": case "dbpop": case "dbget": case "dblen": case "dbclr":
            case "dbsave": case "dbload": case "dbins": case "dbext": case "dbread":
            case "dbat": case "dbmov": case "dbgoto":
            case "fexists_assert": case "fexists":
            case "localtime": case "gmtime":
            case "digest": case "eval":
                if (Developer())
                    st.Print($"rpn: unsupported op '{cmd}' (DB/time/file/eval family is not ported); reading it as a cvar");
                st.Push(st.Cvars.GetString(cmd));
                return;

            // ---- default: push the cvar value (QC reads an unknown token as a cvar by bare name) ----
            default:
                st.Push(st.Cvars.GetString(cmd));
                return;
        }
    }

    // =================================================================================================
    // %.9g formatter (C printf %g, precision 9) — parity-critical for the stack/cvar round-trip.
    // =================================================================================================

    /// <summary>
    /// Format <paramref name="f"/> exactly as QC's <c>sprintf("%.9g", f)</c> (C printf <c>%.9g</c>): 9
    /// significant digits, choose %f vs %e by the decimal exponent, then strip trailing zeros and a dangling
    /// decimal point (the <c>#</c> flag is NOT set). This is the string every numeric rpn result becomes.
    /// </summary>
    public static string Format9g(float f)
    {
        const int precision = 9;

        if (float.IsNaN(f)) return "nan";
        if (float.IsPositiveInfinity(f)) return "inf";
        if (float.IsNegativeInfinity(f)) return "-inf";
        if (f == 0f) return "0"; // covers +0 and -0 (C prints "0" for %g of 0)

        // %g rule: let P = precision (>=1). Format with %e to find the exponent X. If P > X >= -4, use
        // %f with precision P-1-X; else use %e with precision P-1. Then strip trailing zeros.
        double d = f;
        bool negative = d < 0;
        double ad = System.Math.Abs(d);

        // exponent X of the value when written as a.bbb e X (the %e exponent).
        int x = (int)System.Math.Floor(System.Math.Log10(ad));
        // Guard against log10 rounding at powers of ten (e.g. 1000 → 2.9999.. → 2).
        // Re-derive by comparing the rounded mantissa.
        {
            double scaled = ad / System.Math.Pow(10, x);
            if (scaled >= 10.0) x++;
            else if (scaled < 1.0) x--;
        }

        string body;
        if (precision > x && x >= -4)
        {
            // %f-style with (P-1-X) fractional digits.
            int fracDigits = System.Math.Max(0, precision - 1 - x);
            body = ad.ToString("F" + fracDigits, CultureInfo.InvariantCulture);
            body = StripTrailingZeros(body);
        }
        else
        {
            // %e-style with (P-1) fractional digits, then strip mantissa trailing zeros.
            body = FormatExp(ad, precision - 1);
        }

        return negative ? "-" + body : body;
    }

    /// <summary>C <c>%e</c> with <paramref name="fracDigits"/> mantissa digits, trailing zeros stripped,
    /// exponent printed as <c>e±NN</c> (at least two exponent digits) — matching glibc's <c>%g</c> output.</summary>
    private static string FormatExp(double ad, int fracDigits)
    {
        int x = (int)System.Math.Floor(System.Math.Log10(ad));
        double mant = ad / System.Math.Pow(10, x);
        // round the mantissa to fracDigits and fix a carry into the next power of ten.
        string mantStr = mant.ToString("F" + fracDigits, CultureInfo.InvariantCulture);
        if (double.TryParse(mantStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double mr) && mr >= 10.0)
        {
            x++;
            mant = mr / 10.0;
            mantStr = mant.ToString("F" + fracDigits, CultureInfo.InvariantCulture);
        }
        mantStr = StripTrailingZeros(mantStr);
        string sign = x < 0 ? "-" : "+";
        string exp = System.Math.Abs(x).ToString(CultureInfo.InvariantCulture);
        if (exp.Length < 2) exp = "0" + exp;
        return $"{mantStr}e{sign}{exp}";
    }

    private static string StripTrailingZeros(string s)
    {
        if (s.IndexOf('.') < 0)
            return s;
        int end = s.Length;
        while (end > 0 && s[end - 1] == '0') end--;
        if (end > 0 && s[end - 1] == '.') end--;
        return s.Substring(0, end);
    }

    // =================================================================================================
    // helpers
    // =================================================================================================

    private static float Stof(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    private static bool Bool(float f) => f != 0f;

    /// <summary>QC <c>bound(min, num, max)</c>: the DP macro (darkplaces/mathlib.h:34)
    /// <c>((num)>=(min) ? ((num)&lt;(max) ? (num) : (max)) : (min))</c>. NOT <c>min(max(min,num),max)</c> —
    /// that nesting diverges from the DP macro for reversed bounds (min&gt;max): the macro tests against
    /// <c>min</c> FIRST, so <c>bound(10,5,0)==10</c> and <c>bound(5,3,1)==5</c>, whereas <c>min(max(...))</c>
    /// returns 0 / 1 there. <see cref="QMath.Bound"/> is the DP-exact form.</summary>
    private static float BoundQc(float min, float num, float max) => QMath.Bound(min, num, max);

    /// <summary>Integer bitwise op on float operands (QC the <c>&amp;</c>/<c>|</c>/<c>^</c> on stof-views; QC's
    /// bitops truncate to int).</summary>
    private static float BitOp(float a, float b, char op)
    {
        int ia = (int)a, ib = (int)b;
        return op switch { '&' => ia & ib, '|' => ia | ib, '^' => ia ^ ib, _ => 0 };
    }

    /// <summary>
    /// QC <c>sprintf(fmt, arg)</c> for the single-string <c>sprintf1s</c> op: supports the common
    /// width/precision <c>%s</c> form the cfgs use (<c>%-10s</c>, <c>%.5s</c>) plus literal <c>%%</c>. A format
    /// with no <c>%s</c> returns itself (DP would print it verbatim too).
    /// </summary>
    private static string Sprintf1s(string fmt, string arg)
    {
        if (string.IsNullOrEmpty(fmt))
            return "";
        var sb = new StringBuilder(fmt.Length + arg.Length);
        bool consumed = false;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%') { sb.Append(c); continue; }
            // parse a conversion spec
            int j = i + 1;
            if (j < fmt.Length && fmt[j] == '%') { sb.Append('%'); i = j; continue; }
            bool leftAlign = false;
            while (j < fmt.Length && (fmt[j] == '-' || fmt[j] == '0' || fmt[j] == '+' || fmt[j] == ' '))
            {
                if (fmt[j] == '-') leftAlign = true;
                j++;
            }
            int width = 0; bool hasWidth = false;
            while (j < fmt.Length && char.IsDigit(fmt[j])) { width = width * 10 + (fmt[j] - '0'); hasWidth = true; j++; }
            int prec = -1;
            if (j < fmt.Length && fmt[j] == '.')
            {
                j++; prec = 0;
                while (j < fmt.Length && char.IsDigit(fmt[j])) { prec = prec * 10 + (fmt[j] - '0'); j++; }
            }
            if (j < fmt.Length && fmt[j] == 's')
            {
                string v = consumed ? "" : arg;   // only the first %s gets the single argument
                consumed = true;
                if (prec >= 0 && v.Length > prec) v = v.Substring(0, prec); // precision = max width (cut)
                if (hasWidth && v.Length < width)
                    v = leftAlign ? v.PadRight(width) : v.PadLeft(width);
                sb.Append(v);
                i = j;
            }
            else
            {
                // not a %s spec we model — emit the '%' literally and continue scanning from i+1.
                sb.Append('%');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// QC <c>crc16(false, s)</c>: the CRC-16/CCITT (XMODEM) checksum DP exposes as the <c>crc16</c> builtin
    /// (poly 0x1021, init 0xFFFF, final xor 0x0000, no reflection — the <c>caseinsensitive=false</c> path). The
    /// seed MUST be 0xFFFF: DP's <c>CRC_Block</c> (darkplaces/com_crc16.c:36 CRC_INIT_VALUE 0xffff,
    /// CRC_XOR_VALUE 0x0000). Used by a couple of rpn callers; kept here so <c>rpn x crc16</c> matches DP
    /// byte-for-byte (crc16("")==65535, crc16("test")==8134).
    /// </summary>
    private static int Crc16(string s)
    {
        ushort crc = 0xFFFF; // darkplaces/com_crc16.c:36 CRC_INIT_VALUE
        foreach (char ch in s)
        {
            crc ^= (ushort)(ch << 8);
            for (int k = 0; k < 8; k++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc; // CRC_XOR_VALUE 0x0000 → no final xor
    }

    private static readonly Random SharedRng = new();
    private static float Rng() => (float)SharedRng.NextDouble();

    private static bool Developer()
        => Api.Services is not null && Api.Cvars.GetFloat("developer") != 0f;
}
