// Port of Base/darkplaces/prvm_cmds.c (VM_Files_Init, VM_Files_CloseAll, VM_GetFileHandle, VM_fopen,
// VM_fclose, VM_fgets, VM_fputs, VM_search_begin, VM_search_end, VM_search_getsize,
// VM_search_getfilename, VM_whichpack) and fs.c FS_CheckNastyPath.
using System.Text;

namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    // progsvm.h PRVM_MAX_OPENFILES / PRVM_MAX_OPENSEARCHES
    public const int MaxOpenFiles = 256;
    public const int MaxOpenSearches = 128;

    /// <summary>
    /// Total bytes the program may write through fputs and writetofile over this object's life. DarkPlaces
    /// has no such limit; a downloaded program filling the disk is not a behaviour worth porting.
    /// </summary>
    public long MaxWriteBytes { get; set; } = 64L << 20;

    private readonly OpenFile?[] _files = new OpenFile?[MaxOpenFiles];
    private readonly IReadOnlyList<string>?[] _searches = new IReadOnlyList<string>?[MaxOpenSearches];
    private long _bytesWritten;

    /// <summary>One fopen handle: a host stream, read through a small buffer so fgets can take a byte back.</summary>
    private sealed class OpenFile : IDisposable
    {
        private readonly Stream _stream;
        private byte[]? _buffer;
        private int _position, _length;

        public OpenFile(Stream stream) => _stream = stream;

        public Stream Stream => _stream;

        /// <summary>FS_Getc: the next byte, or -1 at the end (or for a file opened for writing).</summary>
        public int GetC()
        {
            if (_position >= _length)
            {
                if (!_stream.CanRead) return -1;
                _buffer ??= new byte[4096];
                try { _length = _stream.Read(_buffer, 0, _buffer.Length); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { _length = 0; }
                _position = 0;
                if (_length <= 0) { _length = 0; return -1; }
            }
            return _buffer![_position++];
        }

        /// <summary>FS_UnGetc of the byte <see cref="GetC"/> just returned.</summary>
        public void UnGetC()
        {
            if (_position > 0) _position--;
        }

        public bool Write(byte[] bytes)
        {
            if (!_stream.CanWrite) return false;
            try { _stream.Write(bytes, 0, bytes.Length); return true; }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { return false; }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
        }
    }

    /// <summary>
    /// fs.c FS_CheckNastyPath (non-gamedir form): true for a path the virtual filesystem must never be
    /// asked for - empty, absolute, drive-qualified, or able to climb out of the data area.
    /// </summary>
    public static bool IsNastyPath(string path)
    {
        if (path.Length == 0) return true;
        if (path.Contains('\\') || path.Contains(':') || path.Contains("//", StringComparison.Ordinal)) return true;
        if (path.Contains("..", StringComparison.Ordinal) || path[0] == '/') return true;
        if (path.Contains("./", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal)) return true;
        // Not in the C, which measures strings by their terminator: nothing below a space is a file name.
        foreach (char c in path)
            if (c < ' ') return true;
        return path.Length > 1024; // the C formats paths into a 1024-byte buffer
    }

    private Stream? OpenRead(string path)
    {
        if (IsNastyPath(path)) return null;
        try { return _host.OpenRead(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private Stream? OpenWrite(string path, bool append)
    {
        if (IsNastyPath(path)) return null;
        try { return _host.OpenWrite(path, append); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // An empty result must be a real, non-null handle (PRVM_SetTempString): "while ((s = fgets(f)))" has to
    // survive a blank line and stop only at the null string of end-of-file. QcVm.ReturnString guarantees
    // that; the name is kept at the call sites because that guarantee is what each of them depends on.
    private void ReturnNonNullString(string text) => _vm.ReturnString(text);

    /// <summary>
    /// The stream behind an fopen handle, for the one builtin outside this class that writes through one
    /// (buf_writefile, in the string builtins). Null for a handle that is not open.
    /// </summary>
    public Stream? FileStream(int handle) =>
        (uint)handle < MaxOpenFiles ? _files[handle]?.Stream : null;

    // VM_GetFileHandle plus the range checks every file builtin repeats.
    private int FileHandle(int argument, string name)
    {
        int handle = QcVm.FloatToInt(_vm.ArgFloat(argument));
        if (handle < 0 || handle >= MaxOpenFiles)
        {
            Warning($"{name}: invalid file handle {handle}\n");
            return -1;
        }
        if (_files[handle] is not null) return handle;
        Warning($"{name}: no such file handle {handle} (or file has been closed)\n");
        return -1;
    }

    private OpenFile? FileArg(int argument, string name)
    {
        int handle = FileHandle(argument, name);
        return handle < 0 ? null : _files[handle];
    }

    // #110 float(string filename, float mode) fopen. Everything the program opens lives under data/;
    // only reading falls back to the bare name, so a program can read (never write) the game's own files.
    private void FOpen(QcVm vm)
    {
        Parms(2, "VM_fopen");
        int handle = Array.IndexOf(_files, null);
        if (handle < 0)
        {
            vm.ReturnFloat(-2);
            Warning($"VM_fopen: ran out of file handles (max {MaxOpenFiles})\n");
            return;
        }

        string filename = vm.ArgString(0);
        int mode = QcVm.FloatToInt(vm.ArgFloat(1));
        Stream? stream;
        switch (mode)
        {
            case 0: stream = OpenRead("data/" + filename) ?? OpenRead(filename); break; // FILE_READ
            case 1: stream = OpenWrite("data/" + filename, append: true); break;        // FILE_APPEND
            case 2: stream = OpenWrite("data/" + filename, append: false); break;       // FILE_WRITE
            default:
                vm.ReturnFloat(-3);
                Warning($"VM_fopen: no such mode {mode} (valid: 0 = read, 1 = append, 2 = write)\n");
                return;
        }

        if (stream is null)
        {
            vm.ReturnFloat(-1);
            return;
        }
        _files[handle] = new OpenFile(stream);
        vm.ReturnFloat(handle);
    }

    // #111 void(float fhandle) fclose
    private void FClose(QcVm vm)
    {
        Parms(1, "VM_fclose");
        int handle = FileHandle(0, "VM_fclose");
        if (handle < 0) return;
        _files[handle]!.Dispose();
        _files[handle] = null;
    }

    // #112 string(float fhandle) fgets: one line without its terminator (\n, \r or \r\n). The null string
    // only at end of file; an empty line is an empty, non-null string.
    private void FGets(QcVm vm)
    {
        Parms(1, "VM_fgets");
        vm.ReturnInt(0);
        OpenFile? file = FileArg(0, "VM_fgets");
        if (file is null) return;

        // The rest of an over-long line is read and dropped, as in the C.
        byte[] line = new byte[256];
        int end = 0, c;
        while (true)
        {
            c = file.GetC();
            if (c == '\r' || c == '\n' || c < 0) break;
            if (end >= vm.MaxStringLength - 1) continue;
            if (end == line.Length) Array.Resize(ref line, Math.Min(line.Length * 2, vm.MaxStringLength));
            line[end++] = (byte)c;
        }
        if (c == '\r')
        {
            // c is reused for the look-ahead, so a blank line ended by a lone \r at the very end of the
            // file reads as end-of-file. Kept: it is what programs were written against.
            c = file.GetC();
            if (c != '\n' && c >= 0) file.UnGetC();
        }
        if (c >= 0 || end > 0) ReturnNonNullString(Encoding.UTF8.GetString(line, 0, end));
    }

    // #113 void(float fhandle, string s) fputs
    private void FPuts(QcVm vm)
    {
        Parms(2, "VM_fputs");
        OpenFile? file = FileArg(0, "VM_fputs");
        if (file is null) return;
        string text = VarString(1);
        if (text.Length > 0) WriteText(file, text, "VM_fputs");
    }

    private bool WriteText(OpenFile file, string text, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (_bytesWritten + bytes.Length > MaxWriteBytes)
        {
            Warning($"{name}: the program has written its limit of {MaxWriteBytes} bytes\n");
            return false;
        }
        if (!file.Write(bytes)) return false;
        _bytesWritten += bytes.Length;
        return true;
    }

    // ---- DP_QC_FS_SEARCH -----------------------------------------------------------------------------

    private int SearchHandle(string name)
    {
        int handle = QcVm.FloatToInt(_vm.ArgFloat(0));
        if (handle < 0 || handle >= MaxOpenSearches)
        {
            Warning($"{name}: invalid handle {handle}\n");
            return -1;
        }
        if (_searches[handle] is not null) return handle;
        Warning($"{name}: no such handle {handle}\n");
        return -1;
    }

    private IReadOnlyList<string>? SearchArg(string name)
    {
        int handle = SearchHandle(name);
        return handle < 0 ? null : _searches[handle];
    }

    // #444 float(string pattern, float caseinsensitive, float quiet[, string packfile]) search_begin
    private void SearchBegin(QcVm vm)
    {
        Parms(3, 4, "VM_search_begin");
        string pattern = vm.ArgString(0);
        CheckEmptyString(pattern);
        bool caseInsensitive = QcVm.FloatToInt(vm.ArgFloat(1)) != 0;
        string? packFile = vm.ArgCount >= 4 ? vm.ArgString(3) : null;

        int handle = Array.IndexOf(_searches, null);
        if (handle < 0)
        {
            vm.ReturnFloat(-2);
            Warning($"VM_search_begin: ran out of search handles (max {MaxOpenSearches})\n");
            return;
        }

        // FS_Search takes everything up to the last separator as a directory to list, unchecked. The
        // same rules as for a file name keep a pattern inside the data area.
        IReadOnlyList<string>? found = null;
        if (!IsNastyPath(pattern))
        {
            try { found = _host.Search(pattern, caseInsensitive, packFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (found is null || found.Count == 0)
        {
            vm.ReturnFloat(-1);
            return;
        }
        _searches[handle] = found;
        vm.ReturnFloat(handle);
    }

    // #445 void(float handle) search_end
    private void SearchEnd(QcVm vm)
    {
        Parms(1, "VM_search_end");
        int handle = SearchHandle("VM_search_end");
        if (handle >= 0) _searches[handle] = null;
    }

    // #446 float(float handle) search_getsize
    private void SearchGetSize(QcVm vm)
    {
        Parms(1, "VM_search_getsize");
        IReadOnlyList<string>? search = SearchArg("VM_search_getsize");
        if (search is not null) vm.ReturnFloat(search.Count);
    }

    // #447 string(float handle, float num) search_getfilename
    private void SearchGetFilename(QcVm vm)
    {
        Parms(2, "VM_search_getfilename");
        IReadOnlyList<string>? search = SearchArg("VM_search_getfilename");
        if (search is null) return;
        int index = QcVm.FloatToInt(vm.ArgFloat(1));
        if (index < 0 || index >= search.Count)
        {
            Warning($"VM_search_getfilename: invalid filenum {index}\n");
            return;
        }
        ReturnNonNullString(search[index]);
    }

    // #503 string(string filename) whichpack: "" for a loose or missing file.
    private void WhichPack(QcVm vm)
    {
        Parms(1, "VM_whichpack");
        string path = vm.ArgString(0);
        vm.ReturnString(IsNastyPath(path) ? "" : _host.WhichPack(path));
    }
}
