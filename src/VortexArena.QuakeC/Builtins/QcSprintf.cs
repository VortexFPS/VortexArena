// Port of Base/darkplaces/prvm_cmds.c VM_sprintf and utf8lib.c u8_strpad, u8_strpad_colorcodes.
using static VortexArena.QuakeC.QcStringUtf8;

namespace VortexArena.QuakeC;

public sealed partial class QcStringBuiltins
{
    // VM_sprintf's outbuf. Reused between calls: sprintf cannot re-enter the VM.
    private readonly byte[] _sprintfOut;
    private int _sprintfLength;

    /// <summary>
    /// string sprintf(string format, ...) = #627
    ///
    /// DarkPlaces parses the directive itself and hands each one to the C library, so the grammar is
    /// its own - <c>%[arg$][flags][width][.precision][length]conversion</c>, where width and precision
    /// may be <c>*</c> or <c>*arg$</c> - and differs from C in what the arguments are:
    /// every argument is a float unless the directive says otherwise, so <c>%d</c> prints a float as an
    /// integer; <c>l</c> (or the conversion <c>%i</c>) reads the cell as a raw integer instead and
    /// <c>h</c> forces float; <c>%v</c> prints a vector as three <c>%g</c>; <c>%s</c> measures its width
    /// and precision in visible characters, skipping colour codes (<c>%+s</c> counts them, <c>%#s</c>
    /// is C's plain byte-counting form); <c>%c</c> takes a code point.
    ///
    /// A directive it cannot parse ends the output there, with a warning. The result is whatever fits
    /// the buffer.
    /// </summary>
    private void Sprintf(QcVm vm)
    {
        byte[] f = Z(vm.ArgString(0));
        int s = 0, argpos = 1;
        _sprintfLength = 0;
        // "o < end - 1": room for at least one more byte before the terminator.
        int capacity = _size - 1;
        bool utf8 = Utf8;

        while (f[s] != 0)
        {
            int s0 = s;
            if (f[s] != '%' || f[s + 1] == '%')
            {
                if (f[s] == '%') s++;
                Put(f[s++]);
                continue;
            }
            s++;

            // The complete form is %3$*1$.*2$ld.
            int width = -1, precision = -1, thisarg = -1, isFloat = -1;
            QcPrintfFlags flags = QcPrintfFlags.None;

            // A leading number is an argument position if "$" follows, and otherwise the width - whose
            // leading zero, if it has one, is the zero-padding flag.
            if (IsDigit(f[s]))
            {
                int after = s;
                width = Strtol(f, ref after);
                if (f[after] == '$')
                {
                    thisarg = width;
                    width = -1;
                    s = after + 1;
                }
                else
                {
                    if (f[s] == '0')
                    {
                        flags |= QcPrintfFlags.ZeroPad;
                        if (width == 0) width = -1; // it was just a flag
                    }
                    s = after;
                }
            }

            if (width < 0)
            {
                for (bool more = true; more;)
                {
                    switch (f[s])
                    {
                        case (byte)'#': flags |= QcPrintfFlags.Alternate; s++; break;
                        case (byte)'0': flags |= QcPrintfFlags.ZeroPad; s++; break;
                        case (byte)'-': flags |= QcPrintfFlags.Left; s++; break;
                        case (byte)' ': flags |= QcPrintfFlags.SpacePositive; s++; break;
                        case (byte)'+': flags |= QcPrintfFlags.SignPositive; s++; break;
                        default: more = false; break;
                    }
                }

                if (f[s] == '*')
                {
                    s++;
                    if (IsDigit(f[s]))
                    {
                        width = Strtol(f, ref s);
                        if (f[s] != '$') { Invalid(f, s0); break; }
                        s++;
                    }
                    else width = argpos++;
                    width = QcVm.FloatToInt(ArgFloat(vm, width));
                    if (width < 0)
                    {
                        flags |= QcPrintfFlags.Left;
                        width = unchecked(-width);
                    }
                }
                else if (IsDigit(f[s])) width = Strtol(f, ref s);
            }

            if (f[s] == '.')
            {
                s++;
                if (f[s] == '*')
                {
                    s++;
                    if (IsDigit(f[s]))
                    {
                        precision = Strtol(f, ref s);
                        if (f[s] != '$') { Invalid(f, s0); break; }
                        s++;
                    }
                    else precision = argpos++;
                    precision = QcVm.FloatToInt(ArgFloat(vm, precision));
                }
                else if (IsDigit(f[s])) precision = Strtol(f, ref s);
                else { Invalid(f, s0); break; }
            }

            for (bool more = true; more;)
            {
                switch (f[s])
                {
                    case (byte)'h': isFloat = 1; s++; break;
                    case (byte)'l': case (byte)'L': isFloat = 0; s++; break;
                    case (byte)'j': case (byte)'z': case (byte)'t': s++; break;
                    default: more = false; break;
                }
            }

            byte conversion = f[s];
            if (isFloat < 0) isFloat = conversion == 'i' ? 0 : 1;
            if (thisarg < 0) thisarg = argpos++;

            if (_sprintfLength < capacity)
            {
                if (width < 0) width = 0; // not set (or the one negative int that has no positive)
                bool asFloat = isFloat != 0;
                switch ((char)conversion)
                {
                    case 'd':
                    case 'i':
                        Put(QcSprintfNumbers.Signed(asFloat ? QcSprintfNumbers.ToInt64(ArgFloat(vm, thisarg)) : ArgInt(vm, thisarg), flags, width, precision));
                        break;
                    case 'o':
                    case 'u':
                    case 'x':
                    case 'X':
                        // A raw integer is sign-extended first, so a negative one prints as sixteen hex digits.
                        Put(QcSprintfNumbers.Unsigned((char)conversion, asFloat ? QcSprintfNumbers.ToUInt64(ArgFloat(vm, thisarg)) : unchecked((ulong)(long)ArgInt(vm, thisarg)), flags, width, precision));
                        break;
                    case 'e':
                    case 'E':
                    case 'f':
                    case 'F':
                    case 'g':
                    case 'G':
                        Put(QcSprintfNumbers.Float((char)conversion, asFloat ? ArgFloat(vm, thisarg) : ArgInt(vm, thisarg), flags, width, precision));
                        break;
                    case 'v':
                    case 'V':
                    {
                        QcVector v = thisarg >= 1 && thisarg < vm.ArgCount ? vm.ArgVector(thisarg) : default;
                        char g = conversion == 'v' ? 'g' : 'G';
                        Put(QcSprintfNumbers.Float(g, Component(v.X, asFloat), flags, width, precision));
                        Put((byte)' ');
                        Put(QcSprintfNumbers.Float(g, Component(v.Y, asFloat), flags, width, precision));
                        Put((byte)' ');
                        Put(QcSprintfNumbers.Float(g, Component(v.Z, asFloat), flags, width, precision));
                        break;
                    }
                    case 'c':
                    {
                        uint c = asFloat ? unchecked((uint)QcSprintfNumbers.ToInt64(ArgFloat(vm, thisarg))) : unchecked((uint)ArgInt(vm, thisarg));
                        Span<byte> encoded = stackalloc byte[5];
                        if ((flags & QcPrintfFlags.Alternate) != 0)
                        {
                            // C's "%c": one byte. A zero byte would end the string; it is left out here.
                            encoded[0] = (byte)c;
                            PutPadded(encoded[..(encoded[0] == 0 ? 0 : 1)], (flags & QcPrintfFlags.Left) != 0, width);
                        }
                        else
                        {
                            int n = FromChar(unchecked((int)c), encoded, utf8);
                            encoded[n] = 0;
                            PadString(encoded.ToArray(), (flags & QcPrintfFlags.Left) != 0, width, precision < 0 ? capacity - _sprintfLength : precision, false, utf8);
                        }
                        break;
                    }
                    case 's':
                    {
                        byte[] text = Z(thisarg >= 1 && thisarg < vm.ArgCount ? vm.ArgString(thisarg) : "");
                        bool left = (flags & QcPrintfFlags.Left) != 0;
                        if ((flags & QcPrintfFlags.Alternate) != 0)
                        {
                            // C's "%s": width and precision in bytes.
                            int take = precision < 0 ? text.Length - 1 : Math.Min(precision, text.Length - 1);
                            PutPadded(text.AsSpan(0, take), left, width);
                        }
                        else
                        {
                            PadString(text, left, width, precision < 0 ? capacity - _sprintfLength : precision,
                                (flags & QcPrintfFlags.SignPositive) == 0, utf8);
                        }
                        break;
                    }
                    default:
                        Invalid(f, s0);
                        goto finished;
                }
            }
            // With the buffer already full the directive is skipped without being looked at, and "%"
            // as the very last character would then step over the terminator.
            if (conversion == 0) break;
            s++;
        }

    finished:
        ReturnBytes(_sprintfOut.AsSpan(0, _sprintfLength));
    }

    private void Invalid(byte[] format, int at) =>
        Warning($"VM_sprintf: invalid directive: {Text(format.AsSpan(at, format.Length - 1 - at))}\n");

    private static bool IsDigit(byte c) => c >= '0' && c <= '9';

    // strtol over digits only (the callers have checked the first). Saturates the way a 32-bit long does.
    private static int Strtol(byte[] s, ref int p)
    {
        long value = 0;
        for (; IsDigit(s[p]); p++) value = Math.Min(value * 10 + (s[p] - '0'), int.MaxValue);
        return (int)value;
    }

    // GETARG_*: a position outside the arguments actually passed reads as 0, not as whatever the cell holds.
    private static float ArgFloat(QcVm vm, int index) => index >= 1 && index < vm.ArgCount ? vm.ArgFloat(index) : 0;
    private static int ArgInt(QcVm vm, int index) => index >= 1 && index < vm.ArgCount ? vm.ArgInt(index) : 0;

    private static double Component(float cell, bool asFloat) => asFloat ? cell : BitConverter.SingleToInt32Bits(cell);

    // ---- output: everything is cut at the buffer, which is how snprintf truncation behaves here ----

    private void Put(byte b)
    {
        if (_sprintfLength < _size - 1) _sprintfOut[_sprintfLength++] = b;
    }

    private void Put(ReadOnlySpan<byte> bytes)
    {
        int n = Math.Min(bytes.Length, _size - 1 - _sprintfLength);
        bytes[..n].CopyTo(_sprintfOut.AsSpan(_sprintfLength));
        _sprintfLength += n;
    }

    // Number conversions produce ASCII only.
    private void Put(string ascii)
    {
        int n = Math.Min(ascii.Length, _size - 1 - _sprintfLength);
        for (int i = 0; i < n; i++) _sprintfOut[_sprintfLength++] = (byte)ascii[i];
    }

    private void PutSpaces(long count)
    {
        int n = (int)Math.Clamp(count, 0, _size - 1 - _sprintfLength);
        _sprintfOut.AsSpan(_sprintfLength, n).Fill((byte)' ');
        _sprintfLength += n;
    }

    private void PutPadded(ReadOnlySpan<byte> text, bool left, int width)
    {
        long pad = (long)width - text.Length;
        if (!left) PutSpaces(pad);
        Put(text);
        if (left) PutSpaces(pad);
    }

    /// <summary>
    /// u8_strpad and u8_strpad_colorcodes: at most <paramref name="maxWidth"/> characters of the text,
    /// padded to <paramref name="minWidth"/>. With <paramref name="colourCodes"/> the codes take no
    /// width, so a coloured name lines up in a column the same as a plain one.
    /// </summary>
    private void PadString(byte[] text, bool left, int minWidth, int maxWidth, bool colourCodes, bool utf8)
    {
        // Both widths are size_t parameters: a negative precision from "*" is no limit at all.
        long max = Size(maxWidth);
        int bytes, actual;
        if (colourCodes)
        {
            bytes = ByteLenColorCodes(text, max, utf8);
            actual = StrNLenColorCodes(text, bytes, utf8);
        }
        else
        {
            // Without UTF-8 this is "%*.*s", which measures bytes - and so do these two.
            bytes = ByteLen(text, 0, max, utf8);
            actual = StrNLen(text, bytes, utf8);
        }
        PutWithPad(text.AsSpan(0, bytes), left, (long)minWidth - actual);
    }

    private void PutWithPad(ReadOnlySpan<byte> text, bool left, long pad)
    {
        if (!left) PutSpaces(pad);
        Put(text);
        if (left) PutSpaces(pad);
    }
}
