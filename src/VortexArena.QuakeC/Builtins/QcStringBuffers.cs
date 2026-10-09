// Port of Base/darkplaces/prvm_cmds.c BufStr_Expand, BufStr_Shrink, BufStr_SortStringsUP/DOWN,
// VM_buf_create, VM_buf_del, VM_buf_getsize, VM_buf_copy, VM_buf_sort, VM_buf_implode, VM_bufstr_get,
// VM_bufstr_set, VM_bufstr_add, VM_bufstr_free, VM_buf_loadfile, VM_buf_writefile, detect_match_rule,
// match_rule, VM_bufstr_find, VM_matchpattern, VM_buf_cvarlist and filematch.c
// matchpattern_with_separator.
using System.Text;
using static VortexArena.QuakeC.QcStringUtf8;

namespace VortexArena.QuakeC;

public sealed partial class QcStringBuiltins
{
    /// <summary>
    /// A string buffer: a sparse, growable array of strings the program addresses by index. A slot is
    /// either a string (possibly empty) or nothing; <see cref="Count"/> is one past the last slot that
    /// holds a string.
    /// </summary>
    private sealed class StringBuffer
    {
        public string?[] Strings = Array.Empty<string?>();
        public int Count;

        // No slot below this is empty. It is what keeps bufstr_add's search for the first empty slot
        // from costing the length of the buffer on every call - the C scans from 0 each time, which a
        // program filling a large buffer that way turns into a quadratic stall.
        public int FirstEmpty;
    }

    private readonly List<StringBuffer?> _buffers = new();
    private readonly SortedSet<int> _freeBuffers = new();
    private int _bufferCount;
    private long _bufferMemory;

    // DarkPlaces refuses a bufstr_set index at or past this ("huge number of strings").
    private const int MaxStringIndex = 1_000_000;

    /// <summary>
    /// Most string buffers that may exist at once. DarkPlaces has no limit but its allocator; a
    /// program that reaches this one gets -1 from buf_create, the failure it is documented to return.
    /// </summary>
    public int MaxStringBuffers { get; init; } = 65536;

    /// <summary>
    /// Bytes all string buffers together may hold, counting 2 per character and 8 per slot. DarkPlaces
    /// again has no limit of its own; passing this one is a program fault, so a hostile program cannot
    /// make the client allocate without bound a megabyte-sized index at a time.
    /// </summary>
    public long MaxStringBufferMemory { get; init; } = 256L << 20;

    /// <summary>
    /// Resolves a handle from the fopen builtin to its open stream, for buf_writefile. File handles
    /// belong to whoever registers the file builtins; until this is set buf_writefile fails with a
    /// warning.
    /// </summary>
    public Func<int, Stream?>? OpenFile { get; set; }

    /// <summary>Number of string buffers the program currently has.</summary>
    public int StringBufferCount => _bufferCount;

    /// <summary>Deletes every string buffer and forgets the tokenizer's state. For when the program is unloaded.</summary>
    public void Reset()
    {
        _buffers.Clear();
        _freeBuffers.Clear();
        _bufferCount = 0;
        _bufferMemory = 0;
        _tokens.Clear();
        _tokenStart.Clear();
        _tokenEnd.Clear();
    }

    // Mem_ExpandableArray_RecordAtIndex takes a size_t, so a negative handle is simply not found.
    private StringBuffer? Buffer(float handle)
    {
        int index = QcVm.FloatToInt(handle);
        return (uint)index < (uint)_buffers.Count ? _buffers[index] : null;
    }

    /// <summary>
    /// A buffer's strings joined by <paramref name="separator"/> (empty slots contribute nothing but their
    /// separator), for a host builtin that sends a buffer somewhere (the POST body of uri_get). Null if there
    /// is no such buffer. The joining stops once the text is longer than <paramref name="maxLength"/>
    /// characters: a result longer than that is incomplete.
    /// </summary>
    public string? Implode(float handle, string separator, int maxLength)
    {
        if (Buffer(handle) is not { } buffer) return null;
        StringBuilder text = new();
        for (int i = 0; i < buffer.Count; i++)
        {
            if (i > 0) text.Append(separator);
            if (buffer.Strings[i] is { } s) text.Append(s);
            if (text.Length > maxLength) break;
        }
        return text.ToString();
    }

    private StringBuffer? Buffer(QcVm vm, int arg, string name)
    {
        StringBuffer? buffer = Buffer(vm.ArgFloat(arg));
        if (buffer is null) Warning($"{name}: invalid buffer {QcVm.FloatToInt(vm.ArgFloat(arg))}\n");
        return buffer;
    }

    private void Charge(long bytes)
    {
        _bufferMemory += bytes;
        if (bytes > 0 && _bufferMemory > MaxStringBufferMemory)
        {
            _bufferMemory -= bytes;
            throw new QcRuntimeException($"{_vm.Name}: string buffers would exceed {MaxStringBufferMemory} bytes");
        }
    }

    private static long Cost(string? text) => text is null ? 0 : 2L * text.Length;

    // BufStr_Expand: the slot array doubles, starting at 128.
    private void Expand(StringBuffer buffer, int index)
    {
        if (buffer.Strings.Length > index) return;
        int size = Math.Max(buffer.Strings.Length * 2, 128);
        while (size <= index) size *= 2;
        Charge(8L * (size - buffer.Strings.Length));
        Array.Resize(ref buffer.Strings, size);
    }

    // BufStr_Shrink: empty slots at the end do not count, and an empty buffer gives its array back.
    private void Shrink(StringBuffer buffer)
    {
        while (buffer.Count > 0 && buffer.Strings[buffer.Count - 1] is null) buffer.Count--;
        if (buffer.Count == 0 && buffer.Strings.Length > 0)
        {
            Charge(-8L * buffer.Strings.Length);
            buffer.Strings = Array.Empty<string?>();
        }
    }

    private void Store(StringBuffer buffer, int index, string? text)
    {
        // Charge first: if it faults, the buffer is unchanged.
        Charge(Cost(text) - Cost(buffer.Strings[index]));
        buffer.Strings[index] = text;
        if (text is null && index < buffer.FirstEmpty) buffer.FirstEmpty = index;
    }

    private void Clear(StringBuffer buffer)
    {
        long freed = 8L * buffer.Strings.Length;
        for (int i = 0; i < buffer.Count; i++) freed += Cost(buffer.Strings[i]);
        Charge(-freed);
        buffer.Strings = Array.Empty<string?>();
        buffer.Count = 0;
        buffer.FirstEmpty = 0;
    }

    // float buf_create() = #460
    private void BufCreate(QcVm vm)
    {
        Parms(0, 2, "VM_buf_create");
        // The optional first argument is a buffer format, and "string" is the only one there is.
        if (vm.ArgCount >= 1 && vm.ArgString(0) != "string")
        {
            vm.ReturnFloat(-1);
            return;
        }
        if (_bufferCount >= MaxStringBuffers)
        {
            Warning($"VM_buf_create: too many string buffers ({MaxStringBuffers})\n");
            vm.ReturnFloat(-1);
            return;
        }
        // The lowest free handle is reused, as DarkPlaces' expandable array does.
        int index;
        if (_freeBuffers.Count > 0)
        {
            index = _freeBuffers.Min;
            _freeBuffers.Remove(index);
            _buffers[index] = new StringBuffer();
        }
        else
        {
            index = _buffers.Count;
            _buffers.Add(new StringBuffer());
        }
        _bufferCount++;
        vm.ReturnFloat(index);
    }

    // void buf_del(float bufhandle) = #461
    private void BufDel(QcVm vm)
    {
        Parms(1, 1, "VM_buf_del");
        StringBuffer? buffer = Buffer(vm, 0, "VM_buf_del");
        if (buffer is null) return;
        Clear(buffer);
        int index = QcVm.FloatToInt(vm.ArgFloat(0));
        _buffers[index] = null;
        _freeBuffers.Add(index);
        _bufferCount--;
    }

    // float buf_getsize(float bufhandle) = #462
    private void BufGetSize(QcVm vm)
    {
        Parms(1, 1, "VM_buf_getsize");
        StringBuffer? buffer = Buffer(vm, 0, "VM_buf_getsize");
        vm.ReturnFloat(buffer?.Count ?? -1);
    }

    // void buf_copy(float bufhandle_from, float bufhandle_to) = #463
    private void BufCopy(QcVm vm)
    {
        Parms(2, 2, "VM_buf_copy");
        StringBuffer? source = Buffer(vm.ArgFloat(0));
        if (source is null)
        {
            Warning($"VM_buf_copy: invalid source buffer {QcVm.FloatToInt(vm.ArgFloat(0))}\n");
            return;
        }
        if (QcVm.FloatToInt(vm.ArgFloat(1)) == QcVm.FloatToInt(vm.ArgFloat(0)))
        {
            Warning($"VM_buf_copy: source == destination ({QcVm.FloatToInt(vm.ArgFloat(0))})\n");
            return;
        }
        // DEVIATION: the C looks the destination up with the source's handle (OFS_PARM0 twice), so it
        // frees the source's strings and copies nothing anywhere. This does what the builtin is for.
        StringBuffer? destination = Buffer(vm.ArgFloat(1));
        if (destination is null)
        {
            Warning($"VM_buf_copy: invalid destination buffer {QcVm.FloatToInt(vm.ArgFloat(1))}\n");
            return;
        }

        long cost = 8L * source.Strings.Length;
        for (int i = 0; i < source.Count; i++) cost += Cost(source.Strings[i]);
        Clear(destination);
        Charge(cost);
        destination.Strings = (string?[])source.Strings.Clone();
        destination.Count = source.Count;
        destination.FirstEmpty = source.FirstEmpty;
    }

    // void buf_sort(float bufhandle, float cmplength, float backward) = #464
    private void BufSort(QcVm vm)
    {
        Parms(3, 3, "VM_buf_sort");
        StringBuffer? buffer = Buffer(vm, 0, "VM_buf_sort");
        if (buffer is null) return;
        if (buffer.Count <= 0)
        {
            Warning($"VM_buf_sort: tried to sort empty buffer {QcVm.FloatToInt(vm.ArgFloat(0))}\n");
            return;
        }
        int sortLength = QcVm.FloatToInt(vm.ArgFloat(1));
        if (sortLength <= 0) sortLength = int.MaxValue;
        bool backward = vm.ArgFloat(2) != 0;

        // strncmp over the bytes, limited to the first cmplength of them. Missing and empty strings go
        // last in either direction. The C comparator is not a consistent ordering where those are
        // concerned (two empties each sort after the other), so qsort's result for them is whatever the
        // C library does; here the sort is stable, with empty strings ahead of missing ones, which keeps
        // the count the same as any qsort would.
        int count = buffer.Count;
        (byte[]? Key, int Rank, string? Text)[] items = new (byte[]?, int, string?)[count];
        for (int i = 0; i < count; i++)
        {
            string? text = buffer.Strings[i];
            items[i] = (string.IsNullOrEmpty(text) ? null : Encoding.UTF8.GetBytes(text), text is null ? 2 : text.Length == 0 ? 1 : 0, text);
        }
        int[] order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int result = items[a].Rank != 0 || items[b].Rank != 0
                ? items[a].Rank.CompareTo(items[b].Rank)
                : CompareBytes(items[a].Key!, items[b].Key!, sortLength) * (backward ? -1 : 1);
            return result != 0 ? result : a.CompareTo(b);
        });
        for (int i = 0; i < count; i++) buffer.Strings[i] = items[order[i]].Text;
        buffer.FirstEmpty = 0;
        Shrink(buffer);
    }

    private static int CompareBytes(byte[] a, byte[] b, int limit)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), limit);
        int result = a.AsSpan(0, n).SequenceCompareTo(b.AsSpan(0, n));
        if (result != 0 || n == limit) return Math.Sign(result);
        return a.Length.CompareTo(b.Length);
    }

    // string buf_implode(float bufhandle, string glue) = #465
    private void BufImplode(QcVm vm)
    {
        Parms(2, 2, "VM_buf_implode");
        vm.ReturnInt(0);
        StringBuffer? buffer = Buffer(vm, 0, "VM_buf_implode");
        if (buffer is null || buffer.Count == 0) return;
        string glue = vm.ArgString(1);
        int glueBytes = Encoding.UTF8.GetByteCount(glue);
        StringBuilder text = new();
        long length = 0;
        for (int i = 0; i < buffer.Count; i++)
        {
            string? item = buffer.Strings[i];
            if (item is null) continue;
            // Two quirks kept from the C: the glue is written before every string, the first included
            // (the length check is what forgets to count it), and the first string that would not fit
            // ends the result rather than being cut.
            length += (i > 0 ? glueBytes : 0) + Encoding.UTF8.GetByteCount(item);
            if (length >= _size - 1) break;
            text.Append(glue).Append(item);
        }
        vm.ReturnString(Limit(text.ToString()));
    }

    // string bufstr_get(float bufhandle, float string_index) = #466
    private void BufStrGet(QcVm vm)
    {
        Parms(2, 2, "VM_bufstr_get");
        vm.ReturnInt(0);
        StringBuffer? buffer = Buffer(vm, 0, "VM_bufstr_get");
        if (buffer is null) return;
        int index = QcVm.FloatToInt(vm.ArgFloat(1));
        if (index >= 0 && index < buffer.Count && buffer.Strings[index] is { } text) vm.ReturnString(text);
    }

    // void bufstr_set(float bufhandle, float string_index, string str) = #467
    private void BufStrSet(QcVm vm)
    {
        Parms(3, 3, "VM_bufstr_set");
        StringBuffer? buffer = Buffer(vm, 0, "VM_bufstr_set");
        if (buffer is null) return;
        int index = QcVm.FloatToInt(vm.ArgFloat(1));
        if (index < 0 || index >= MaxStringIndex)
        {
            Warning($"VM_bufstr_set: invalid string index {index}\n");
            return;
        }
        // Even the null string is stored as an empty string here, never as an empty slot.
        Expand(buffer, index);
        Store(buffer, index, vm.ArgString(2));
        buffer.Count = Math.Max(buffer.Count, index + 1);
    }

    // float bufstr_add(float bufhandle, string str, float order) = #468
    private void BufStrAdd(QcVm vm)
    {
        Parms(3, 3, "VM_bufstr_add");
        vm.ReturnFloat(-1);
        StringBuffer? buffer = Buffer(vm, 0, "VM_bufstr_add");
        if (buffer is null) return;
        if (vm.ArgInt(1) == 0)
        {
            Warning($"VM_bufstr_add: can not add an empty string to buffer {QcVm.FloatToInt(vm.ArgFloat(0))}\n");
            return;
        }
        // "order" appends; otherwise the first empty slot is filled.
        int index = buffer.Count;
        if (QcVm.FloatToInt(vm.ArgFloat(2)) == 0)
        {
            for (index = Math.Min(buffer.FirstEmpty, buffer.Count); index < buffer.Count; index++)
                if (buffer.Strings[index] is null) break;
            buffer.FirstEmpty = index;
        }
        Expand(buffer, index);
        Store(buffer, index, vm.ArgString(1));
        buffer.Count = Math.Max(buffer.Count, index + 1);
        vm.ReturnFloat(index);
    }

    // void bufstr_free(float bufhandle, float string_index) = #469
    private void BufStrFree(QcVm vm)
    {
        Parms(2, 2, "VM_bufstr_free");
        StringBuffer? buffer = Buffer(vm, 0, "VM_bufstr_free");
        if (buffer is null) return;
        int index = QcVm.FloatToInt(vm.ArgFloat(1));
        if (index < 0)
        {
            Warning($"VM_bufstr_free: invalid string index {index}\n");
            return;
        }
        if (index < buffer.Count) Store(buffer, index, null);
        Shrink(buffer);
    }

    // float buf_loadfile(string filename, float bufhandle) = #535
    private void BufLoadFile(QcVm vm)
    {
        Parms(2, 2, "VM_buf_loadfile");
        string filename = vm.ArgString(0);
        vm.ReturnFloat(0);
        // QuakeC's files live under data/; a path that is not found there is tried as given.
        using Stream? raw = _host.OpenRead("data/" + filename) ?? _host.OpenRead(filename);
        if (raw is null)
        {
            if (_host.Developer) Warning($"VM_buf_loadfile: failed to open file {filename}\n");
            return;
        }
        StringBuffer? buffer = Buffer(vm, 1, "VM_buf_loadfile");
        if (buffer is null) return;

        // Lines are appended after the buffer's last string. A line ends at \n, \r or \r\n; one longer
        // than a tempstring loses its tail; a final line with no terminator still counts.
        using BufferedStream file = new(raw, 16384);
        byte[] line = new byte[_size - 1];
        int pushedBack = -2;
        while (true)
        {
            int end = 0, c;
            while (true)
            {
                c = pushedBack != -2 ? pushedBack : file.ReadByte();
                pushedBack = -2;
                if (c == '\r' || c == '\n' || c < 0) break;
                if (end < line.Length) line[end++] = (byte)c;
            }
            if (c == '\r')
            {
                int next = file.ReadByte();
                if (next != '\n') pushedBack = next;
            }
            if (c < 0 && end == 0) break;
            // A NUL in the file ends the line for C's strlen.
            int nul = Array.IndexOf(line, (byte)0, 0, end);
            int index = buffer.Count;
            Expand(buffer, index);
            Store(buffer, index, Text(line.AsSpan(0, nul < 0 ? end : nul)));
            buffer.Count = index + 1;
            if (c < 0) break;
        }
        vm.ReturnFloat(1);
    }

    // float buf_writefile(float filehandle, float bufhandle[, float startpos, float numstrings]) = #536
    private void BufWriteFile(QcVm vm)
    {
        Parms(2, 4, "VM_buf_writefile");
        vm.ReturnFloat(0);
        int handle = QcVm.FloatToInt(vm.ArgFloat(0));
        Stream? file = handle >= 0 ? OpenFile?.Invoke(handle) : null;
        if (file is null)
        {
            Warning($"VM_buf_writefile: no such file handle {handle} (or file has been closed)\n");
            return;
        }
        StringBuffer? buffer = Buffer(vm, 1, "VM_buf_writefile");
        if (buffer is null) return;

        int index = vm.ArgCount > 2 ? QcVm.FloatToInt(vm.ArgFloat(2)) : 0;
        int count = vm.ArgCount > 3 ? QcVm.FloatToInt(vm.ArgFloat(3)) : buffer.Count - index;
        if (index < 0 || index >= buffer.Count)
        {
            Warning($"VM_buf_writefile: wrong start string index {index}\n");
            return;
        }
        if (count < 0)
        {
            Warning($"VM_buf_writefile: wrong strings count {count}\n");
            return;
        }
        // One line per string; empty slots are skipped, not written as blank lines.
        for (; index < buffer.Count && count > 0; index++, count--)
        {
            if (buffer.Strings[index] is not { } text) continue;
            file.Write(Encoding.UTF8.GetBytes(text));
            file.WriteByte((byte)'\n');
        }
        vm.ReturnFloat(1);
    }

    // ---- pattern matching --------------------------------------------------------------------------

    private const int RuleAuto = 0, RuleWhole = 1, RuleLeft = 2, RuleRight = 3, RuleMiddle = 4, RulePattern = 5;

    /// <summary>
    /// detect_match_rule: picks the rule a pattern implies. The C means to recognise "abc*" as a
    /// prefix match and "*abc*" as a substring match, but compares the position of a "*" it found
    /// with the pattern's length, which it can never equal; so those fall through to the general
    /// pattern matcher - with the same answers - and only "whole" and "*abc" are ever detected.
    /// </summary>
    private static int DetectMatchRule(ref byte[] pattern)
    {
        int length = pattern.Length - 1;
        if (Array.IndexOf(pattern, (byte)'?', 0, length) >= 0) return RulePattern;
        int star = Array.IndexOf(pattern, (byte)'*', 0, length);
        if (star < 0) return RuleWhole;
        if (star == 0 && Array.IndexOf(pattern, (byte)'*', 1, length - 1) < 0)
        {
            pattern = pattern[1..];
            return RuleRight;
        }
        return RulePattern;
    }

    // match_rule. Not UTF-8 aware in C either: "?" matches one byte.
    private bool MatchRule(byte[] text, int at, byte[] pattern, int rule)
    {
        ReadOnlySpan<byte> s = text.AsSpan(at, text.Length - 1 - at);
        ReadOnlySpan<byte> p = pattern.AsSpan(0, pattern.Length - 1);
        switch (rule)
        {
            case RuleWhole:
                // strncmp over a tempstring's worth of bytes.
                return s.Length >= _size && p.Length >= _size ? s[.._size].SequenceEqual(p[.._size]) : s.SequenceEqual(p);
            case RuleLeft:
                return s.StartsWith(p);
            case RuleRight:
            {
                // Only the first occurrence is looked at, so "abab" does not end with "ab" here.
                int found = s.IndexOf(p);
                return found >= 0 && found + p.Length == s.Length;
            }
            case RuleMiddle:
                return s.IndexOf(p) >= 0;
            default:
                return Glob(s, p);
        }
    }

    /// <summary>
    /// matchpattern_with_separator with no separators: "?" is any one byte, "*" any run of bytes, and
    /// the whole text must match. The C recurses on every "*", which is exponential on a pattern built
    /// to make it so; this is the standard backtrack-to-the-last-star form, which accepts exactly the
    /// same strings in time proportional to text times pattern.
    /// </summary>
    internal static bool Glob(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // float bufstr_find(float bufhandle, string match, float matchrule[, float startpos[, float step]]) = #537
    private void BufStrFind(QcVm vm)
    {
        Parms(3, 5, "VM_bufstr_find");
        vm.ReturnFloat(-1);
        StringBuffer? buffer = Buffer(vm, 0, "VM_bufstr_find");
        if (buffer is null) return;

        int rule = QcVm.FloatToInt(vm.ArgFloat(2));
        if (rule < 0 || rule > 5)
        {
            Warning($"VM_bufstr_find: invalid match rule {rule}\n");
            return;
        }
        byte[] match = rule != RuleAuto ? Z(vm.ArgString(1)) : Z(Limit(vm.ArgString(1)));
        if (rule == RuleAuto) rule = DetectMatchRule(ref match);

        long i = vm.ArgCount > 3 ? QcVm.FloatToInt(vm.ArgFloat(3)) : 0;
        int step = vm.ArgCount > 4 ? QcVm.FloatToInt(vm.ArgFloat(4)) : 1;
        if (i < 0 || step < 1)
        {
            Warning($"VM_bufstr_find: invalid start index {i} or step {step}\n");
            return;
        }
        for (; i < buffer.Count; i += step)
        {
            if (buffer.Strings[i] is { } text && MatchRule(Z(text), 0, match, rule))
            {
                vm.ReturnFloat(i);
                return;
            }
        }
    }

    // float matchpattern(string s, string pattern, float matchrule[, float startpos]) = #538
    private void MatchPattern(QcVm vm)
    {
        Parms(2, 4, "VM_matchpattern");
        // (C leaves the return value alone on a bad rule; "no match" is the only sensible reading.)
        vm.ReturnFloat(0);
        byte[] s = Z(vm.ArgString(0));
        int rule = vm.ArgCount > 2 ? QcVm.FloatToInt(vm.ArgFloat(2)) : RuleAuto;
        if (rule < 0 || rule > 5)
        {
            Warning($"VM_matchpattern: invalid match rule {rule}\n");
            return;
        }
        byte[] match = rule != RuleAuto ? Z(vm.ArgString(1)) : Z(Limit(vm.ArgString(1)));
        if (rule == RuleAuto) rule = DetectMatchRule(ref match);

        // The start offset is in bytes and cannot pass the last one.
        int at = 0, length = s.Length - 1;
        if (vm.ArgCount > 3)
        {
            float start = vm.ArgFloat(3);
            if (length > 0 && start > 0) at = start < length - 1 ? (int)start : length - 1;
        }
        vm.ReturnFloat(MatchRule(s, at, match, rule) ? 1 : 0);
    }

    // void buf_cvarlist(float buf, string prefix, string antiprefix) = #517
    private void BufCvarList(QcVm vm)
    {
        Parms(2, 3, "VM_buf_cvarlist");
        StringBuffer? buffer = Buffer(vm, 0, "VM_buf_cvarlist");
        if (buffer is null) return;
        string prefix = vm.ArgString(1);
        string antiPrefix = vm.ArgCount == 3 ? vm.ArgString(2) : "";

        // The buffer is replaced by the names of the cvars that match the prefix and do not match the
        // anti-prefix. Either may instead be a pattern, if it has a "*" or "?" in it. The host is asked
        // with both and the rule is applied again here, so a host that returns too much is still right.
        byte[] include = Z(prefix), exclude = Z(antiPrefix);
        bool includePattern = prefix.Contains('*') || prefix.Contains('?');
        bool excludePattern = antiPrefix.Contains('*') || antiPrefix.Contains('?');
        List<string> names = new();
        long cost = 0;
        foreach (string name in _host.CvarNames(prefix, antiPrefix))
        {
            ReadOnlySpan<byte> n = Encoding.UTF8.GetBytes(name);
            if (prefix.Length > 0 && !(includePattern ? Glob(n, include.AsSpan(0, include.Length - 1)) : n.StartsWith(include.AsSpan(0, include.Length - 1)))) continue;
            if (antiPrefix.Length > 0 && (excludePattern ? Glob(n, exclude.AsSpan(0, exclude.Length - 1)) : n.StartsWith(exclude.AsSpan(0, exclude.Length - 1)))) continue;
            if (names.Count >= MaxStringIndex) break;
            names.Add(name);
            cost += 8 + Cost(name);
        }
        Clear(buffer);
        Charge(cost);
        buffer.Strings = names.ToArray();
        buffer.Count = names.Count;
    }
}
