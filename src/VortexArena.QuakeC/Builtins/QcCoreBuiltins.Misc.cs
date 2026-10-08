// Port of Base/darkplaces/prvm_cmds.c (VM_error, VM_objerror, VM_print, VM_dprint, VM_break, VM_coredump,
// VM_traceon, VM_traceoff, VM_precache_file, VM_gettime, VM_strftime, VM_coverage).
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    /// <summary>What traceon/traceoff last set. DarkPlaces prints every statement while it is on; nothing here reads it.</summary>
    public bool Trace { get; private set; }

    /// <summary>Wall clock for strftime. Defaults to the system's.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>gettime(GETTIME_REALTIME): DarkPlaces' Sys_DirtyTime, a monotonic clock sampled at the call. Defaults to a stopwatch.</summary>
    public Func<double>? DirtyTime { get; set; }

    /// <summary>The <see cref="DirtyTime"/> reading taken when the current frame began, for gettime(GETTIME_HIRES). Unset, HIRES reads 0.</summary>
    public Func<double>? FrameDirtyTime { get; set; }

    /// <summary>gettime(GETTIME_CDTRACK): seconds into the music track. Unset, 0.</summary>
    public Func<float>? CdTrackPosition { get; set; }

    // #339 void(string s, ...) print
    private void Print(QcVm vm) => _host.Print(VarString(0));

    // #25 void(string s, ...) dprint
    private void DPrint(QcVm vm)
    {
        Parms(1, 8, "VM_dprint");
        if (_host.Developer) _host.Print(VarString(0));
    }

    private void PrintSelf()
    {
        QcDef? self = _vm.FindGlobal("self");
        if (self is null) return;
        int edict = _vm.GlobalInt(self.Offset);
        if ((uint)edict < (uint)_vm.MaxEdicts) PrintEdict(edict);
    }

    // #10 void(string e, ...) error: dumps self and kills the running program.
    private void Error(QcVm vm)
    {
        string message = VarString(0), function = CurrentFunctionName();
        _host.Print($"======{vm.Name} ERROR in {function}:\n{message}\n");
        PrintSelf();
        // The stack is attached here because QcVm only adds it to single-line messages.
        throw new QcRuntimeException($"{vm.Name}: Program error in function {function}:\n{message}\nTip: read above for entity information\n{vm.StackTrace()}");
    }

    // #11 void(string e, ...) objerror: dumps and REMOVES self, then carries on. Quake aborted the
    // program here; DarkPlaces only prints, and code after an objerror() call does run.
    private void ObjError(QcVm vm)
    {
        string message = VarString(0);
        _host.Print("======OBJECT ERROR======\n");
        PrintSelf();
        QcDef? self = vm.FindGlobal("self");
        if (self is not null) FreeEdict(vm.GlobalInt(self.Offset));
        _host.Print($"{vm.Name} OBJECT ERROR in {CurrentFunctionName()}:\n{message}\nTip: read above for entity information\n");
    }

    // #6 void() break
    private void Break(QcVm vm) => throw Fault("break statement");

    // #28 void() coredump
    private void CoreDump(QcVm vm)
    {
        Parms(0, "VM_coredump");
        _host.LocalCommand($"prvm_edicts {vm.Name}\n");
    }

    private void TraceOn(QcVm vm) { Parms(0, "VM_traceon"); Trace = true; }
    private void TraceOff(QcVm vm) { Parms(0, "VM_traceoff"); Trace = false; }

    // #68 / #77 string(string s) precache_file: only ever meant something to the compiler's pak builder.
    private void PrecacheFile(QcVm vm)
    {
        Parms(1, "VM_precache_file");
        vm.ReturnInt(vm.ArgInt(0));
    }

    // #642 void() coverage: a profiling mark.
    private void Coverage(QcVm vm) => Parms(0, "VM_coverage");

    // #519 float([float timer]) gettime
    private void GetTime(QcVm vm)
    {
        Parms(0, 1, "VM_gettime");
        if (vm.ArgCount == 0)
        {
            vm.ReturnFloat((float)_host.RealTime);
            return;
        }
        switch (QcVm.FloatToInt(vm.ArgFloat(0)))
        {
            case 0: vm.ReturnFloat((float)(_host.RealTime - StartTime)); break;                         // GETTIME_FRAMESTART
            case 1: vm.ReturnFloat((float)ReadDirtyTime()); break;                                      // GETTIME_REALTIME
            case 2: vm.ReturnFloat(FrameDirtyTime is null ? 0 : (float)(ReadDirtyTime() - FrameDirtyTime())); break; // GETTIME_HIRES
            case 3: vm.ReturnFloat((float)_host.RealTime); break;                                       // GETTIME_UPTIME
            case 4: vm.ReturnFloat(CdTrackPosition?.Invoke() ?? 0); break;                              // GETTIME_CDTRACK
            default:
                Warning("VM_gettime: unsupported timer specified, returning realtime\n");
                vm.ReturnFloat((float)_host.RealTime);
                break;
        }
    }

    private double ReadDirtyTime() => DirtyTime?.Invoke() ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // #478 string(float uselocaltime, string format, ...) strftime
    private void StrFTime(QcVm vm)
    {
        Parms(2, 8, "VM_strftime");
        string format = VarString(1);
        DateTimeOffset now = Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        bool local = vm.ArgFloat(0) != 0;
        ReturnNonNullString(FormatTime(format, local ? now.ToLocalTime() : now.ToUniversalTime(), local, vm.MaxStringLength));
    }

    private static readonly string[] Days = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    /// <summary>
    /// C's strftime in the "C" locale. A result that would not fit <paramref name="limit"/> is the empty
    /// string, as with the C function's zero return. An unknown conversion is copied through.
    /// </summary>
    internal static string FormatTime(string format, DateTimeOffset t, bool local, int limit)
    {
        StringBuilder text = new();
        Expand(format, 0);
        return text.Length >= limit ? "" : text.ToString();

        void Expand(string f, int depth)
        {
            for (int i = 0; i < f.Length && text.Length < limit; i++)
            {
                if (f[i] != '%' || i + 1 >= f.Length)
                {
                    text.Append(f[i]);
                    continue;
                }
                char c = f[++i];
                // POSIX E and O modifiers and MSVC's # select alternative forms the C locale does not have.
                if ((c is 'E' or 'O' or '#') && i + 1 < f.Length) c = f[++i];
                int weekday = (int)t.DayOfWeek;
                switch (c)
                {
                    case 'a': text.Append(Days[weekday], 0, 3); break;
                    case 'A': text.Append(Days[weekday]); break;
                    case 'b' or 'h': text.Append(Months[t.Month - 1], 0, 3); break;
                    case 'B': text.Append(Months[t.Month - 1]); break;
                    case 'c': Nested("%a %b %e %H:%M:%S %Y"); break;
                    case 'C': Number(t.Year / 100, 2); break;
                    case 'd': Number(t.Day, 2); break;
                    case 'D' or 'x': Nested("%m/%d/%y"); break;
                    case 'e': text.Append(t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2)); break;
                    case 'F': Nested("%Y-%m-%d"); break;
                    case 'g': Number(ISOWeek.GetYear(t.DateTime) % 100, 2); break;
                    case 'G': Number(ISOWeek.GetYear(t.DateTime), 1); break;
                    case 'H': Number(t.Hour, 2); break;
                    case 'I': Number(t.Hour % 12 == 0 ? 12 : t.Hour % 12, 2); break;
                    case 'j': Number(t.DayOfYear, 3); break;
                    case 'm': Number(t.Month, 2); break;
                    case 'M': Number(t.Minute, 2); break;
                    case 'n': text.Append('\n'); break;
                    case 'p': text.Append(t.Hour < 12 ? "AM" : "PM"); break;
                    case 'r': Nested("%I:%M:%S %p"); break;
                    case 'R': Nested("%H:%M"); break;
                    case 's': text.Append(t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)); break;
                    case 'S': Number(t.Second, 2); break;
                    case 't': text.Append('\t'); break;
                    case 'T' or 'X': Nested("%H:%M:%S"); break;
                    case 'u': Number(weekday == 0 ? 7 : weekday, 1); break;
                    case 'U': Number((t.DayOfYear - 1 + 7 - weekday) / 7, 2); break;
                    case 'V': Number(ISOWeek.GetWeekOfYear(t.DateTime), 2); break;
                    case 'w': Number(weekday, 1); break;
                    case 'W': Number((t.DayOfYear - 1 + 7 - (weekday + 6) % 7) / 7, 2); break;
                    case 'y': Number(t.Year % 100, 2); break;
                    case 'Y': Number(t.Year, 1); break;
                    case 'z':
                        text.Append(t.Offset < TimeSpan.Zero ? '-' : '+');
                        Number(Math.Abs(t.Offset.Hours), 2);
                        Number(Math.Abs(t.Offset.Minutes), 2);
                        break;
                    case 'Z':
                        text.Append(!local ? "GMT" : TimeZoneInfo.Local.IsDaylightSavingTime(t) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName);
                        break;
                    case '%': text.Append('%'); break;
                    default: text.Append('%').Append(c); break;
                }

                void Nested(string composite)
                {
                    if (depth == 0) Expand(composite, 1);
                }
            }
        }

        void Number(int value, int width) => text.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
    }
}
