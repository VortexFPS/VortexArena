using System.Globalization;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Serializes edit ops for the wire (design doc §11.7, phase E6). Co-editing needs the server to be the
/// authority on geometry, so a client does not mutate its own document and hope: it sends the OP, the server
/// validates and applies it, and echoes it to everyone including the sender.
///
/// The encoding is a compact text line — <c>verb arg arg ...</c> — rather than a binary struct, for three
/// reasons: ops are rare (a drag emits one on release, not per frame, so density does not matter), a text
/// line is readable in a packet log when co-editing misbehaves, and the same line is what an autosave journal
/// replays after a crash. Floats use round-trip formatting and the invariant culture so a op crossing between
/// machines reconstructs bit-identically.
///
/// Unknown verbs decode to null rather than throwing: a newer client editing against an older server should
/// have its unsupported op rejected, not drop the connection.
/// </summary>
public static class VmapOpWire
{
    /// <summary>Encode an op as a single line. Returns null for an op type that has no wire form yet.</summary>
    public static string? Serialize(IVmapOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        var sb = new StringBuilder(64);

        switch (op)
        {
            case TranslateBrushesOp t:
                sb.Append("move ");
                AppendIds(sb, t.TouchedBrushIds);
                sb.Append(' ');
                AppendVec(sb, t.Delta);
                return sb.ToString();

            case MoveFaceOp f:
                return string.Create(CultureInfo.InvariantCulture,
                    $"face {f.TouchedBrushIds[0]} {f.FaceIndex} {Fmt(f.Distance)}");

            case MoveVerticesOp v:
                sb.Append("verts ").Append(v.TouchedBrushIds[0]).Append(' ');
                IReadOnlyList<Vector3> targets = v.Targets;
                sb.Append(targets.Count);
                foreach (Vector3 p in targets)
                {
                    sb.Append(' ');
                    AppendVec(sb, p);
                }
                sb.Append(' ');
                AppendVec(sb, v.Delta);
                return sb.ToString();

            case RotateBrushesOp r:
                sb.Append("rotate ");
                AppendIds(sb, r.TouchedBrushIds);
                sb.Append(' ');
                AppendVec(sb, r.Pivot);
                sb.Append(' ');
                AppendVec(sb, r.Axis);
                sb.Append(' ').Append(Fmt(r.Degrees));
                return sb.ToString();

            case DeleteBrushesOp d:
                sb.Append("delete ");
                AppendIds(sb, d.TouchedBrushIds);
                return sb.ToString();

            default:
                // CreateBoxBrushOp / ClipBrushOp allocate ids during Apply, so replicating them needs the
                // server to assign the id and echo it back. Deliberately not wired until that handshake exists
                // (see the E6 note in the design doc) rather than shipping an op that desynchronizes ids.
                return null;
        }
    }

    /// <summary>Decode a line produced by <see cref="Serialize"/>. Returns null for a malformed/unknown line.</summary>
    public static IVmapOp? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        string[] tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length < 2)
            return null;

        try
        {
            switch (tok[0])
            {
                case "move":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 3)
                        return null;
                    return new TranslateBrushesOp(ids, ReadVec(tok, next));
                }
                case "face":
                {
                    if (tok.Length < 4)
                        return null;
                    return new MoveFaceOp(int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture), ReadFloat(tok[3]));
                }
                case "verts":
                {
                    if (tok.Length < 4)
                        return null;
                    int brushId = int.Parse(tok[1], CultureInfo.InvariantCulture);
                    int count = int.Parse(tok[2], CultureInfo.InvariantCulture);
                    if (count < 0 || tok.Length < 3 + count * 3 + 3)
                        return null;
                    var targets = new Vector3[count];
                    for (int i = 0; i < count; i++)
                        targets[i] = ReadVec(tok, 3 + i * 3);
                    return new MoveVerticesOp(brushId, targets, ReadVec(tok, 3 + count * 3));
                }
                case "rotate":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 7)
                        return null;
                    Vector3 pivot = ReadVec(tok, next);
                    Vector3 axis = ReadVec(tok, next + 3);
                    return new RotateBrushesOp(ids, pivot, axis, ReadFloat(tok[next + 6]));
                }
                case "delete":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out _))
                        return null;
                    return new DeleteBrushesOp(ids);
                }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
        {
            return null;   // malformed payload from a peer: reject the op, never fault the session
        }
    }

    // ---- id list: "<count> <id> <id> ..." ----

    private static void AppendIds(StringBuilder sb, IReadOnlyList<int> ids)
    {
        sb.Append(ids.Count);
        foreach (int id in ids)
            sb.Append(' ').Append(id);
    }

    private static bool TryReadIds(string[] tok, int start, out int[] ids, out int next)
    {
        ids = Array.Empty<int>();
        next = start;
        if (start >= tok.Length)
            return false;
        int count = int.Parse(tok[start], CultureInfo.InvariantCulture);
        if (count < 0 || start + 1 + count > tok.Length)
            return false;
        ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = int.Parse(tok[start + 1 + i], CultureInfo.InvariantCulture);
        next = start + 1 + count;
        return true;
    }

    private static void AppendVec(StringBuilder sb, Vector3 v)
        => sb.Append(Fmt(v.X)).Append(' ').Append(Fmt(v.Y)).Append(' ').Append(Fmt(v.Z));

    private static Vector3 ReadVec(string[] tok, int i)
        => new(ReadFloat(tok[i]), ReadFloat(tok[i + 1]), ReadFloat(tok[i + 2]));

    private static float ReadFloat(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Round-trip ("R") formatting so a decoded float is bit-identical to the encoded one.</summary>
    private static string Fmt(float f) => f.ToString("R", CultureInfo.InvariantCulture);

}

/// <summary>
/// Per-brush editing locks for a co-editing session (design doc §11.7).
///
/// Two mappers dragging the same wall would otherwise interleave their ops and produce geometry neither of
/// them asked for — and because undo is per-client, neither could cleanly back it out. A lock is taken for
/// the brushes an op touches, held for the duration of a drag, and released on commit or disconnect.
///
/// Deliberately coarse (whole brushes, not faces): a face push changes the brush's shape, so face-level
/// locking would not actually make concurrent edits safe, only make them look safe.
/// </summary>
public sealed class VmapEditLocks
{
    private readonly Dictionary<int, int> _ownerByBrush = new();   // brush id -> client id

    /// <summary>
    /// Take locks for every brush in <paramref name="brushIds"/> on behalf of <paramref name="clientId"/>.
    /// All-or-nothing: if any brush is held by someone else nothing is taken, so a partially-locked op can
    /// never begin.
    /// </summary>
    public bool TryAcquire(int clientId, IReadOnlyList<int> brushIds)
    {
        ArgumentNullException.ThrowIfNull(brushIds);

        foreach (int id in brushIds)
            if (_ownerByBrush.TryGetValue(id, out int owner) && owner != clientId)
                return false;

        foreach (int id in brushIds)
            _ownerByBrush[id] = clientId;
        return true;
    }

    /// <summary>Release every lock held by a client — called on drag commit and on disconnect.</summary>
    public void ReleaseAll(int clientId)
    {
        var mine = new List<int>();
        foreach ((int brush, int owner) in _ownerByBrush)
            if (owner == clientId)
                mine.Add(brush);
        foreach (int brush in mine)
            _ownerByBrush.Remove(brush);
    }

    /// <summary>Release locks on specific brushes held by this client.</summary>
    public void Release(int clientId, IReadOnlyList<int> brushIds)
    {
        foreach (int id in brushIds)
            if (_ownerByBrush.TryGetValue(id, out int owner) && owner == clientId)
                _ownerByBrush.Remove(id);
    }

    /// <summary>True when another client currently holds this brush (drives the "locked" highlight).</summary>
    public bool IsLockedByOther(int clientId, int brushId)
        => _ownerByBrush.TryGetValue(brushId, out int owner) && owner != clientId;

    /// <summary>The client holding a brush, or null when it is free.</summary>
    public int? OwnerOf(int brushId)
        => _ownerByBrush.TryGetValue(brushId, out int owner) ? owner : null;

    /// <summary>Number of brushes currently locked (diagnostics).</summary>
    public int LockedCount => _ownerByBrush.Count;
}

/// <summary>
/// The server-authoritative apply path for a co-editing session: check the locks, apply the op, and report
/// whether it should be echoed to the other clients.
/// </summary>
public sealed class VmapEditServer
{
    private readonly VmapEditSession _session;

    public VmapEditServer(VmapEditSession session)
        => _session = session ?? throw new ArgumentNullException(nameof(session));

    public VmapEditLocks Locks { get; } = new();

    /// <summary>The authoritative document every client mirrors.</summary>
    public VmapDocument Document => _session.Document;

    /// <summary>Outcome of a submitted op.</summary>
    public enum Result
    {
        /// <summary>Applied; echo the op to every client.</summary>
        Applied,

        /// <summary>Another client holds one of the brushes.</summary>
        Locked,

        /// <summary>Well-formed but invalid geometry (e.g. the drag broke convexity) — the sender must roll back.</summary>
        Rejected,

        /// <summary>Undecodable payload.</summary>
        Malformed,
    }

    /// <summary>Apply a wire-encoded op submitted by a client.</summary>
    public Result Submit(int clientId, string wireLine)
    {
        IVmapOp? op = VmapOpWire.Deserialize(wireLine);
        if (op is null)
            return Result.Malformed;
        return Submit(clientId, op);
    }

    /// <summary>Apply an already-decoded op on behalf of a client.</summary>
    public Result Submit(int clientId, IVmapOp op)
    {
        ArgumentNullException.ThrowIfNull(op);

        if (!Locks.TryAcquire(clientId, op.TouchedBrushIds))
            return Result.Locked;

        bool ok = _session.Apply(op);

        // A lock is only held for the duration of the edit it guarded; holding it past the commit would let a
        // client silently own a brush forever if it never sent an explicit release.
        Locks.Release(clientId, op.TouchedBrushIds);

        return ok ? Result.Applied : Result.Rejected;
    }
}
