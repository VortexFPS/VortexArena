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
/// have its unsupported op rejected, not drop the connection. The same goes for a truncated or garbled
/// payload — every list is length-prefixed so a short line fails its length check rather than decoding into
/// something plausible.
///
/// Ops that MINT ids (create, clip, extrude, paste) carry the id in the line. A client sends zero, meaning
/// "you choose"; the server assigns during Apply and re-encodes with <see cref="SerializeAfterApply"/> before
/// broadcasting, so every peer replays the op with the id the server actually used.
/// </summary>
public static class VmapOpWire
{
    /// <summary>
    /// Largest control-grid side a decoded patch may claim. Not a format limit — it is the point past which a
    /// declared dimension is certainly hostile rather than a patch someone built, and it keeps the two sides
    /// small enough that their product cannot overflow.
    /// </summary>
    private const int MaxGridSide = 1024;

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
                // Texture lock rides as a TRAILING token, which is what makes it compatible in both
                // directions: an older decoder stops before it and reads the move exactly as it always did,
                // and a newer one treats its absence as off — which is the behaviour that predates the flag.
                sb.Append(' ').Append(t.TextureLock ? 1 : 0);
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
                sb.Append(' ').Append(Fmt(r.Degrees)).Append(' ').Append(r.TextureLock ? 1 : 0);
                return sb.ToString();

            case DeleteBrushesOp d:
                sb.Append("delete ");
                AppendIds(sb, d.TouchedBrushIds);
                return sb.ToString();

            case ScaleSelectionOp sc:
                sb.Append("scale ");
                AppendIds(sb, sc.TouchedBrushIds);
                sb.Append(' ');
                AppendIds(sb, sc.TouchedPatchIds);
                sb.Append(' ');
                AppendVec(sb, sc.Pivot);
                sb.Append(' ');
                AppendVec(sb, sc.Scale);
                sb.Append(' ').Append(sc.TextureLock ? 1 : 0).Append(' ');
                // Trailing, like the lock flag, so a line from before entity scaling still decodes.
                AppendIds(sb, sc.EntityIds);
                return sb.ToString();

            case RotateSelectionOp rs:
                sb.Append("rotsel ");
                AppendIds(sb, rs.TouchedBrushIds);
                sb.Append(' ');
                AppendIds(sb, rs.TouchedPatchIds);
                sb.Append(' ');
                AppendVec(sb, rs.Pivot);
                sb.Append(' ');
                AppendVec(sb, rs.Axis);
                sb.Append(' ').Append(Fmt(rs.Degrees)).Append(' ').Append(rs.TextureLock ? 1 : 0);
                return sb.ToString();

            case DeletePatchesOp dp:
                sb.Append("patchdel ");
                AppendIds(sb, dp.TouchedPatchIds);
                return sb.ToString();

            case TranslatePatchesOp tp:
                sb.Append("patchmove ");
                AppendIds(sb, tp.PatchIds);
                sb.Append(' ');
                AppendVec(sb, tp.Delta);
                return sb.ToString();

            case MovePatchControlOp mc:
                sb.Append("patchctrl ").Append(mc.TouchedPatchIds[0]).Append(' ')
                    .Append(mc.ControlIndex).Append(' ');
                AppendVec(sb, mc.Delta);
                return sb.ToString();

            case ModifyPatchOp mp:
                return string.Create(CultureInfo.InvariantCulture,
                    $"patchop {mp.TouchedPatchIds[0]} {(int)mp.Operation}");

            case SetFaceMaterialOp sm:
                return $"mat {sm.TouchedBrushIds[0]} {sm.FaceIndex} {Escape(sm.Material)}";

            case SetFaceProjectionOp sp:
                sb.Append("proj ").Append(sp.TouchedBrushIds[0]).Append(' ').Append(sp.FaceIndex).Append(' ');
                AppendProjection(sb, sp.Projection);
                return sb.ToString();

            case SetFaceLayersOp sl:
                sb.Append("layers ").Append(sl.TouchedBrushIds[0]).Append(' ').Append(sl.FaceIndex)
                  .Append(' ').Append(sl.Layers.Count);
                foreach (VmapFaceLayer l in sl.Layers)
                {
                    sb.Append(' ');
                    AppendProjection(sb, l.Projection);
                    sb.Append(' ').Append((int)l.Blend).Append(' ').Append(l.WeightChannel)
                      .Append(' ').Append(Escape(l.Material));
                }
                return sb.ToString();

            case SetFaceFlagsOp sf:
                return string.Create(CultureInfo.InvariantCulture,
                    $"flags {sf.TouchedBrushIds[0]} {sf.FaceIndex} {sf.SurfaceFlags} {sf.ContentFlags}");

            case BevelEdgeOp bv:
                sb.Append("bevel ").Append(bv.TouchedBrushIds[0]).Append(' ');
                AppendVec(sb, bv.EdgeA);
                sb.Append(' ');
                AppendVec(sb, bv.EdgeB);
                sb.Append(' ').Append(Fmt(bv.Size));
                return sb.ToString();

            case SnapBrushToGridOp sg:
                sb.Append("snap ");
                AppendIds(sb, sg.TouchedBrushIds);
                sb.Append(' ').Append(Fmt(sg.Grid));
                return sb.ToString();

            case SetEntityKeyOp sk:
                return $"entkey {sk.TouchedEntityIds[0]} {Escape(sk.Key)} {Escape(sk.Value)}";

            case MoveEntitiesOp me:
                sb.Append("entmove ");
                AppendIds(sb, me.TouchedEntityIds);
                sb.Append(' ');
                AppendVec(sb, me.Delta);
                sb.Append(' ').Append(me.TextureLock ? 1 : 0);
                return sb.ToString();

            case RotateEntitiesOp rot:
                sb.Append("entrot ");
                AppendIds(sb, rot.TouchedEntityIds);
                sb.Append(' ');
                AppendVec(sb, rot.Pivot);
                sb.Append(' ').Append(Fmt(rot.Degrees));
                return sb.ToString();

            case DeleteEntitiesOp de:
                sb.Append("entdel ");
                AppendIds(sb, de.TouchedEntityIds);
                return sb.ToString();

            // ---- creates: the id field IS the handshake ----
            //
            // A create mints an id during Apply, and two machines minting independently would diverge the
            // moment either of them created anything. So the id travels in the op: a client sends 0 ("you
            // choose"), the server assigns and applies, and the echo carries the assigned id, which every peer
            // then replays verbatim. One field, and the whole class of id divergence goes away.

            case CreateBoxBrushOp cb:
                sb.Append("mkbrush ").Append(cb.WireId).Append(' ');
                AppendVec(sb, cb.Mins);
                sb.Append(' ');
                AppendVec(sb, cb.Maxs);
                sb.Append(' ').Append(Escape(cb.Material));
                return sb.ToString();

            case CreatePatchOp cp:
                sb.Append("mkpatch ").Append(cp.WireId).Append(' ').Append((int)cp.Kind).Append(' ');
                AppendVec(sb, cp.Mins);
                sb.Append(' ');
                AppendVec(sb, cp.Maxs);
                sb.Append(' ').Append(cp.GridWidth).Append(' ').Append(cp.GridHeight)
                  .Append(' ').Append(Escape(cp.Material));
                return sb.ToString();

            case CreateBrushEntityOp cbe:
                sb.Append("mkbent ").Append(cbe.WireId).Append(' ');
                AppendIds(sb, cbe.BrushIds);
                sb.Append(' ');
                AppendIds(sb, cbe.PatchIds);
                sb.Append(' ').Append(Escape(cbe.ClassName)).Append(' ').Append(cbe.Fields.Count);
                foreach (KeyValuePair<string, string> kv in cbe.Fields)
                    sb.Append(' ').Append(Escape(kv.Key)).Append(' ').Append(Escape(kv.Value));
                return sb.ToString();

            case DissolveBrushEntityOp dbe:
                sb.Append("entdissolve ");
                AppendIds(sb, dbe.TouchedEntityIds);
                return sb.ToString();

            case CreateEntityOp ce:
                sb.Append("mkent ").Append(ce.WireId).Append(' ');
                AppendVec(sb, ce.Origin);
                sb.Append(' ').Append(Escape(ce.ClassName)).Append(' ').Append(ce.Fields.Count);
                foreach (KeyValuePair<string, string> kv in ce.Fields)
                    sb.Append(' ').Append(Escape(kv.Key)).Append(' ').Append(Escape(kv.Value));
                return sb.ToString();

            case ExtrudeFaceOp ex:
                return string.Create(CultureInfo.InvariantCulture,
                    $"extrude {ex.WireId} {ex.SourceBrushId} {ex.FaceIndex} {Fmt(ex.Distance)}");

            case SetGroupOp grp:
                sb.Append("group ").Append(grp.WireId).Append(' ').Append(grp.Hidden ? 1 : 0)
                    .Append(' ').Append(Escape(grp.Name)).Append(' ');
                AppendIds(sb, grp.BrushIds);
                sb.Append(' ');
                AppendIds(sb, grp.PatchIds);
                sb.Append(' ');
                AppendIds(sb, grp.EntityIds);
                return sb.ToString();

            case SubtractBrushesOp sub:
                sb.Append("csgsub ").Append(sub.CutterBrushId).Append(' ');
                AppendIds(sb, sub.TargetBrushIds);
                sb.Append(' ');
                AppendIds(sb, sub.WireIds);
                return sb.ToString();

            case HollowBrushesOp hol:
                sb.Append("csghollow ");
                AppendIds(sb, hol.TouchedBrushIds);
                sb.Append(' ').Append(Fmt(hol.Thickness)).Append(' ').Append(hol.Outward ? 1 : 0).Append(' ');
                AppendIds(sb, hol.WireIds);
                return sb.ToString();

            case MergeBrushesOp mrg:
                sb.Append("csgmerge ");
                AppendIds(sb, mrg.TouchedBrushIds);
                return sb.ToString();

            case ClipSelectionOp cl:
                sb.Append("clip ");
                AppendIds(sb, cl.TouchedBrushIds);
                sb.Append(' ');
                AppendVec(sb, cl.Plane.Normal);
                sb.Append(' ').Append(Fmt(cl.Plane.Dist)).Append(' ').Append((int)cl.Keep).Append(' ');
                AppendIds(sb, cl.WireIds);
                return sb.ToString();

            case AddObjectsOp add:
                AppendAddObjects(sb, add);
                return sb.ToString();

            case SetObjectsOp set:
                AppendSetObjects(sb, set);
                return sb.ToString();

            default:
                // PasteOp is the one op with no verb of its own: its RESULT is what replicates, encoded as an
                // AddObjectsOp by SerializeAfterApply once the ids exist.
                return null;
        }
    }

    /// <summary>
    /// Encode an op that has already been applied to <paramref name="doc"/>, so a create carries the id the
    /// apply assigned rather than a request for one. This is the form the server broadcasts.
    ///
    /// A paste has no gesture worth replaying — its result is an arbitrary pile of geometry — so it is
    /// captured out of the document as an <see cref="AddObjectsOp"/> instead.
    /// </summary>
    public static string? SerializeAfterApply(IVmapOp op, VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(doc);

        if (op is PasteOp paste)
            return Serialize(AddObjectsOp.Capture(
                doc, paste.CreatedBrushIds, paste.CreatedPatchIds, paste.CreatedEntityIds));

        return Serialize(op);
    }

    /// <summary>
    /// Decode a line produced by <see cref="Serialize"/>. Returns null for a malformed or unknown line.
    /// </summary>
    /// <param name="doc">
    /// The document the op is about to be applied to, where one is available. Two entity ops need it at
    /// CONSTRUCTION to work out which brushes an entity owns — that set has to be known before Apply so the
    /// journal can snapshot it, and only the document knows it. Without it a replicated brush-entity move is
    /// applied but not undoable, so pass it whenever you have it.
    /// </param>
    public static IVmapOp? Deserialize(string line, VmapDocument? doc = null)
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
                    return new TranslateBrushesOp(ids, ReadVec(tok, next), ReadFlag(tok, next + 3));
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
                    if (!Fits(tok, 3, count, stride: 3, trailing: 3))
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
                    return new RotateBrushesOp(
                        ids, pivot, axis, ReadFloat(tok[next + 6]), ReadFlag(tok, next + 7));
                }
                case "delete":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out _))
                        return null;
                    return new DeleteBrushesOp(ids);
                }
                case "scale":
                {
                    if (!TryReadIds(tok, 1, out int[] brushIds, out int n1)
                        || !TryReadIds(tok, n1, out int[] patchIds, out int n2)
                        || tok.Length < n2 + 6)
                        return null;
                    int[] scaleEntities = Array.Empty<int>();
                    if (n2 + 7 < tok.Length && TryReadIds(tok, n2 + 7, out int[] se, out _))
                        scaleEntities = se;
                    return new ScaleSelectionOp(
                        brushIds, patchIds, ReadVec(tok, n2), ReadVec(tok, n2 + 3), ReadFlag(tok, n2 + 6),
                        scaleEntities, doc);
                }
                case "rotsel":
                {
                    if (!TryReadIds(tok, 1, out int[] brushIds, out int n1)
                        || !TryReadIds(tok, n1, out int[] patchIds, out int n2)
                        || tok.Length < n2 + 7)
                        return null;
                    return new RotateSelectionOp(
                        brushIds, patchIds, ReadVec(tok, n2), ReadVec(tok, n2 + 3), ReadFloat(tok[n2 + 6]),
                        ReadFlag(tok, n2 + 7));
                }
                case "patchdel":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out _))
                        return null;
                    return new DeletePatchesOp(ids);
                }
                case "patchmove":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 3)
                        return null;
                    return new TranslatePatchesOp(ids, ReadVec(tok, next));
                }
                case "patchctrl":
                {
                    if (tok.Length < 6)
                        return null;
                    return new MovePatchControlOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture), ReadVec(tok, 3));
                }
                case "patchop":
                {
                    if (tok.Length < 3)
                        return null;
                    return new ModifyPatchOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        (PatchOperation)int.Parse(tok[2], CultureInfo.InvariantCulture));
                }
                case "mat":
                {
                    if (tok.Length < 4)
                        return null;
                    return new SetFaceMaterialOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture), Unescape(tok[3]));
                }
                case "proj":
                {
                    if (tok.Length < 11)
                        return null;
                    return new SetFaceProjectionOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture), ReadProjection(tok, 3));
                }
                case "layers":
                {
                    if (tok.Length < 4)
                        return null;
                    int at = 4;
                    int count = int.Parse(tok[3], CultureInfo.InvariantCulture);
                    if (count <= 0 || !Fits(tok, at, count, stride: 11))
                        return null;

                    var layers = new List<VmapFaceLayer>(count);
                    for (int i = 0; i < count; i++, at += 11)
                        layers.Add(new VmapFaceLayer
                        {
                            Projection = ReadProjection(tok, at),
                            Blend = (VmapBlend)int.Parse(tok[at + 8], CultureInfo.InvariantCulture),
                            WeightChannel = int.Parse(tok[at + 9], CultureInfo.InvariantCulture),
                            Material = Unescape(tok[at + 10]),
                        });

                    return new SetFaceLayersOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture), layers);
                }
                case "flags":
                {
                    if (tok.Length < 5)
                        return null;
                    return new SetFaceFlagsOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        int.Parse(tok[2], CultureInfo.InvariantCulture),
                        int.Parse(tok[3], CultureInfo.InvariantCulture),
                        int.Parse(tok[4], CultureInfo.InvariantCulture));
                }
                case "bevel":
                {
                    if (tok.Length < 9)
                        return null;
                    return new BevelEdgeOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture),
                        ReadVec(tok, 2), ReadVec(tok, 5), ReadFloat(tok[8]));
                }
                case "snap":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 1)
                        return null;
                    return new SnapBrushToGridOp(ids, ReadFloat(tok[next]));
                }
                case "entkey":
                {
                    if (tok.Length < 4)
                        return null;
                    return new SetEntityKeyOp(
                        int.Parse(tok[1], CultureInfo.InvariantCulture), Unescape(tok[2]), Unescape(tok[3]));
                }
                case "entmove":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 3)
                        return null;
                    // Trailing flag, absent on a line from a build that predates texture lock: decoding it as
                    // false is what keeps those lines meaning what they meant.
                    return new MoveEntitiesOp(ids, ReadVec(tok, next), doc, ReadFlag(tok, next + 3));
                }
                case "entrot":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 4)
                        return null;
                    return new RotateEntitiesOp(ids, ReadVec(tok, next), ReadFloat(tok[next + 3]));
                }
                case "entdel":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out _))
                        return null;
                    return new DeleteEntitiesOp(ids, doc);
                }
                case "mkbrush":
                {
                    if (tok.Length < 9)
                        return null;
                    return new CreateBoxBrushOp(
                        ReadVec(tok, 2), ReadVec(tok, 5), Unescape(tok[8]),
                        int.Parse(tok[1], CultureInfo.InvariantCulture));
                }
                case "mkpatch":
                {
                    if (tok.Length < 12)
                        return null;
                    return new CreatePatchOp(
                        (PatchPrimitive)int.Parse(tok[2], CultureInfo.InvariantCulture),
                        ReadVec(tok, 3), ReadVec(tok, 6), Unescape(tok[11]),
                        int.Parse(tok[9], CultureInfo.InvariantCulture),
                        int.Parse(tok[10], CultureInfo.InvariantCulture),
                        int.Parse(tok[1], CultureInfo.InvariantCulture));
                }
                case "mkbent":
                {
                    // Three untrusted counts on one line — two id lists and a field bag — so three Fits
                    // guards. A count multiplied by a stride overflows and wraps negative, and the wrapped
                    // value then sizes an array.
                    if (tok.Length < 2)
                        return null;
                    int forced = int.Parse(tok[1], CultureInfo.InvariantCulture);
                    if (!TryReadIds(tok, 2, out int[] brushIds, out int afterBrushes)
                        || !TryReadIds(tok, afterBrushes, out int[] patchIds, out int afterPatches)
                        || tok.Length < afterPatches + 2)
                        return null;

                    string cls = Unescape(tok[afterPatches]);
                    int keyCount = int.Parse(tok[afterPatches + 1], CultureInfo.InvariantCulture);
                    if (!Fits(tok, afterPatches + 2, keyCount, stride: 2))
                        return null;

                    var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < keyCount; i++)
                        keys[Unescape(tok[afterPatches + 2 + i * 2])] = Unescape(tok[afterPatches + 3 + i * 2]);
                    return new CreateBrushEntityOp(cls, brushIds, patchIds, keys, forced);
                }
                case "entdissolve":
                {
                    // doc, like entdel/entmove: the op resolves the geometry it frees at construction, and on
                    // the receiving side that is the only chance to know what the entity owned.
                    if (!TryReadIds(tok, 1, out int[] dissolveIds, out _))
                        return null;
                    return new DissolveBrushEntityOp(dissolveIds, doc);
                }
                case "mkent":
                {
                    if (tok.Length < 7)
                        return null;
                    int fieldCount = int.Parse(tok[6], CultureInfo.InvariantCulture);
                    if (!Fits(tok, 7, fieldCount, stride: 2))
                        return null;
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < fieldCount; i++)
                        fields[Unescape(tok[7 + i * 2])] = Unescape(tok[8 + i * 2]);
                    return new CreateEntityOp(
                        Unescape(tok[5]), ReadVec(tok, 2), fields,
                        int.Parse(tok[1], CultureInfo.InvariantCulture));
                }
                case "extrude":
                {
                    if (tok.Length < 5)
                        return null;
                    return new ExtrudeFaceOp(
                        int.Parse(tok[2], CultureInfo.InvariantCulture),
                        int.Parse(tok[3], CultureInfo.InvariantCulture), ReadFloat(tok[4]),
                        int.Parse(tok[1], CultureInfo.InvariantCulture));
                }
                case "group":
                {
                    if (tok.Length < 4)
                        return null;
                    int groupId = int.Parse(tok[1], CultureInfo.InvariantCulture);
                    bool groupHidden = int.Parse(tok[2], CultureInfo.InvariantCulture) != 0;
                    string groupName = Unescape(tok[3]);
                    if (!TryReadIds(tok, 4, out int[] gBrushes, out int afterGb)
                        || !TryReadIds(tok, afterGb, out int[] gPatches, out int afterGp)
                        || !TryReadIds(tok, afterGp, out int[] gEntities, out _))
                        return null;
                    return new SetGroupOp(groupName, groupHidden, gBrushes, gPatches, gEntities, doc, groupId);
                }
                case "csgsub":
                {
                    if (tok.Length < 3)
                        return null;
                    int cutter = int.Parse(tok[1], CultureInfo.InvariantCulture);
                    if (!TryReadIds(tok, 2, out int[] csgTargets, out int afterTargets)
                        || !TryReadIds(tok, afterTargets, out int[] csgCreated, out _))
                        return null;
                    return new SubtractBrushesOp(cutter, csgTargets, csgCreated);
                }
                case "csghollow":
                {
                    if (!TryReadIds(tok, 1, out int[] hollowIds, out int afterIds)
                        || tok.Length < afterIds + 3)
                        return null;
                    float thickness = ReadFloat(tok[afterIds]);
                    bool outward = int.Parse(tok[afterIds + 1], CultureInfo.InvariantCulture) != 0;
                    if (!TryReadIds(tok, afterIds + 2, out int[] hollowCreated, out _))
                        return null;
                    return new HollowBrushesOp(hollowIds, thickness, outward, hollowCreated);
                }
                case "csgmerge":
                {
                    if (!TryReadIds(tok, 1, out int[] mergeIds, out _))
                        return null;
                    return new MergeBrushesOp(mergeIds);
                }
                case "clip":
                {
                    if (!TryReadIds(tok, 1, out int[] ids, out int next) || tok.Length < next + 5)
                        return null;
                    var plane = new VmapPlane(ReadVec(tok, next), ReadFloat(tok[next + 3]));
                    var keep = (ClipKeep)int.Parse(tok[next + 4], CultureInfo.InvariantCulture);
                    if (!TryReadIds(tok, next + 5, out int[] created, out _))
                        return null;
                    return new ClipSelectionOp(ids, plane, keep, created);
                }
                case "add":
                    return ReadAddObjects(tok);

                case "set":
                    return ReadSetObjects(tok);

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

    /// <summary>
    /// True when <paramref name="count"/> items of <paramref name="stride"/> tokens each, starting at
    /// <paramref name="at"/> and followed by <paramref name="trailing"/> more tokens, actually fit in the line.
    ///
    /// A division rather than the obvious <c>at + count * stride + trailing &lt;= tok.Length</c>, because that
    /// product OVERFLOWS for a large count, wraps negative, and sails through the comparison — after which the
    /// count is used to size an array. A peer that picks the number gets an out-of-memory abort out of it, and
    /// <see cref="Deserialize"/>'s catch does not cover OOM, so it takes the editing session with it. Every
    /// count that reaches an allocation goes through here.
    /// </summary>
    private static bool Fits(string[] tok, int at, int count, int stride, int trailing = 0)
    {
        if (count < 0 || stride <= 0 || at < 0 || at > tok.Length)
            return false;
        int room = tok.Length - at - trailing;
        return room >= 0 && count <= room / stride;
    }

    private static bool TryReadIds(string[] tok, int start, out int[] ids, out int next)
    {
        ids = Array.Empty<int>();
        next = start;
        if (start >= tok.Length)
            return false;
        int count = int.Parse(tok[start], CultureInfo.InvariantCulture);
        if (!Fits(tok, start + 1, count, 1))
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

    /// <summary>
    /// An optional trailing boolean. Absent means false, which is how a line written before the flag existed
    /// decodes to the behaviour it had at the time.
    /// </summary>
    private static bool ReadFlag(string[] tok, int at) => at < tok.Length && tok[at] == "1";

    /// <summary>Round-trip ("R") formatting so a decoded float is bit-identical to the encoded one.</summary>
    private static string Fmt(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    // ---- strings: shader names and spawn values are user text, so they can hold spaces ----

    /// <summary>
    /// Make a string safe to carry as one space-separated token. Empty becomes a sentinel rather than nothing,
    /// because a zero-length token vanishes under the split and would silently shift every field after it —
    /// which is exactly what clearing a spawn key with <c>entkey</c> would otherwise do.
    /// </summary>
    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "\\e";

        var sb = new StringBuilder(s.Length + 4);
        foreach (char c in s)
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ' ': sb.Append("\\s"); break;
                default: sb.Append(c); break;
            }
        return sb.ToString();
    }

    /// <summary>
    /// Inverse of <see cref="Escape"/>. A single left-to-right pass, not two Replace calls: replacing "\s"
    /// first would corrupt an escaped backslash that happened to be followed by an 's'.
    /// </summary>
    private static string Unescape(string s)
    {
        if (s == "\\e")
            return string.Empty;
        if (s.IndexOf('\\') < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            sb.Append(s[++i] switch { 's' => ' ', '\\' => '\\', char other => other });
        }
        return sb.ToString();
    }

    // ---- texture projection: "<axisU> <axisV> <offU> <offV>", 8 tokens ----

    private static void AppendProjection(StringBuilder sb, VmapTexProjection p)
    {
        AppendVec(sb, p.AxisU);
        sb.Append(' ');
        AppendVec(sb, p.AxisV);
        sb.Append(' ').Append(Fmt(p.OffsetU)).Append(' ').Append(Fmt(p.OffsetV));
    }

    private static VmapTexProjection ReadProjection(string[] tok, int i)
        => new(ReadVec(tok, i), ReadVec(tok, i + 3), ReadFloat(tok[i + 6]), ReadFloat(tok[i + 7]));

    // ---- the add payload: whole objects rather than a gesture ----
    //
    // Long by the standards of the other verbs, and that is the point: an AddObjectsOp is what a paste becomes,
    // so the line has to carry every plane, control point and spawn key rather than an instruction to rebuild
    // them. Counts precede every list so a truncated line fails the length check instead of decoding as
    // something plausible.

    private static void AppendAddObjects(StringBuilder sb, AddObjectsOp add)
    {
        sb.Append("add ");
        AppendBrushes(sb, add.Brushes);
        sb.Append(' ');
        AppendPatches(sb, add.Patches);

        sb.Append(' ').Append(add.Entities.Count);
        for (int i = 0; i < add.Entities.Count; i++)
        {
            AppendEntity(sb, add.Entities[i]);
            sb.Append(' ');
            AppendIds(sb, add.EntityBrushIndices[i]);
            sb.Append(' ');
            AppendIds(sb, add.EntityPatchIndices[i]);
        }
    }

    private static void AppendSetObjects(StringBuilder sb, SetObjectsOp set)
    {
        sb.Append("set ");
        AppendBrushes(sb, set.Brushes);
        sb.Append(' ');
        AppendPatches(sb, set.Patches);

        // Ownership by real id here, not by index: every object in a restore already exists on both sides.
        sb.Append(' ').Append(set.Entities.Count);
        foreach (VmapEntity e in set.Entities)
        {
            AppendEntity(sb, e);
            sb.Append(' ');
            AppendIds(sb, e.BrushIds);
            sb.Append(' ');
            AppendIds(sb, e.PatchIds);
        }

        sb.Append(' ');
        AppendIds(sb, set.RemovedBrushIds);
        sb.Append(' ');
        AppendIds(sb, set.RemovedPatchIds);
        sb.Append(' ');
        AppendIds(sb, set.RemovedEntityIds);
    }

    private static void AppendBrushes(StringBuilder sb, IReadOnlyList<VmapBrush> brushes)
    {
        sb.Append(brushes.Count);
        foreach (VmapBrush b in brushes)
        {
            sb.Append(' ').Append(b.Id).Append(' ').Append(b.Faces.Count);
            foreach (VmapFace f in b.Faces)
            {
                sb.Append(' ');
                AppendVec(sb, f.Plane.Normal);
                sb.Append(' ').Append(Fmt(f.Plane.Dist)).Append(' ');
                AppendProjection(sb, f.Projection);
                sb.Append(' ').Append(f.SurfaceFlags).Append(' ').Append(f.ContentFlags)
                  .Append(' ').Append(Escape(f.Material));

                // Then the layers ABOVE the base, count first. A plain face writes a single extra token, so
                // the common case stays as cheap as it was; a layered face replicates in full rather than
                // arriving flattened, which is what a receiver would otherwise silently render.
                sb.Append(' ').Append(f.Layers.Count - 1);
                for (int i = 1; i < f.Layers.Count; i++)
                {
                    VmapFaceLayer l = f.Layers[i];
                    sb.Append(' ');
                    AppendProjection(sb, l.Projection);
                    sb.Append(' ').Append((int)l.Blend).Append(' ').Append(l.WeightChannel)
                      .Append(' ').Append(Escape(l.Material));
                }
            }
        }
    }

    private static void AppendPatches(StringBuilder sb, IReadOnlyList<VmapPatch> patches)
    {
        sb.Append(patches.Count);
        foreach (VmapPatch p in patches)
        {
            sb.Append(' ').Append(p.Id).Append(' ').Append(p.Width).Append(' ').Append(p.Height)
              .Append(' ').Append(p.SurfaceFlags).Append(' ').Append(p.ContentFlags)
              .Append(' ').Append(Escape(p.Material));
            foreach (Vector3 c in p.Controls)
            {
                sb.Append(' ');
                AppendVec(sb, c);
            }
            foreach (Vector2 uv in p.ControlUvs)
                sb.Append(' ').Append(Fmt(uv.X)).Append(' ').Append(Fmt(uv.Y));
        }
    }

    private static void AppendEntity(StringBuilder sb, VmapEntity e)
    {
        sb.Append(' ').Append(e.Id).Append(' ').Append(e.Fields.Count);
        foreach (KeyValuePair<string, string> kv in e.Fields)
            sb.Append(' ').Append(Escape(kv.Key)).Append(' ').Append(Escape(kv.Value));
    }

    private static AddObjectsOp? ReadAddObjects(string[] tok)
    {
        int at = 1;
        if (!TryReadBrushes(tok, ref at, out List<VmapBrush> brushes)
            || !TryReadPatches(tok, ref at, out List<VmapPatch> patches)
            || !TryCount(tok, ref at, out int entityCount))
            return null;

        // Sized as it goes, never from the declared count: the smallest possible entity is 4 tokens (id, field
        // count, and the two ownership lists), so a count larger than the line can hold is refused up front
        // rather than pre-allocating for a number a peer chose.
        if (!Fits(tok, at, entityCount, stride: 4))
            return null;

        var entities = new List<VmapEntity>();
        var ownedBrushes = new List<int[]>();
        var ownedPatches = new List<int[]>();
        for (int i = 0; i < entityCount; i++)
        {
            if (!TryReadEntity(tok, ref at, out VmapEntity e)
                || !TryReadIds(tok, at, out int[] bi, out at)
                || !TryReadIds(tok, at, out int[] pi, out at))
                return null;
            entities.Add(e);
            ownedBrushes.Add(bi);
            ownedPatches.Add(pi);
        }

        return new AddObjectsOp(brushes, patches, entities, ownedBrushes, ownedPatches);
    }

    private static SetObjectsOp? ReadSetObjects(string[] tok)
    {
        int at = 1;
        if (!TryReadBrushes(tok, ref at, out List<VmapBrush> brushes)
            || !TryReadPatches(tok, ref at, out List<VmapPatch> patches)
            || !TryCount(tok, ref at, out int entityCount))
            return null;

        if (!Fits(tok, at, entityCount, stride: 4))
            return null;

        var entities = new List<VmapEntity>();
        for (int i = 0; i < entityCount; i++)
        {
            if (!TryReadEntity(tok, ref at, out VmapEntity e)
                || !TryReadIds(tok, at, out int[] bi, out at)
                || !TryReadIds(tok, at, out int[] pi, out at))
                return null;
            e.BrushIds.AddRange(bi);
            e.PatchIds.AddRange(pi);
            entities.Add(e);
        }

        if (!TryReadIds(tok, at, out int[] goneBrushes, out at)
            || !TryReadIds(tok, at, out int[] gonePatches, out at)
            || !TryReadIds(tok, at, out int[] goneEntities, out _))
            return null;

        return new SetObjectsOp(brushes, patches, entities, goneBrushes, gonePatches, goneEntities);
    }

    private static bool TryReadBrushes(string[] tok, ref int at, out List<VmapBrush> brushes)
    {
        brushes = new List<VmapBrush>();
        if (!TryCount(tok, ref at, out int brushCount))
            return false;

        // The smallest possible brush is 2 tokens (its id and a face count of zero).
        if (!Fits(tok, at, brushCount, stride: 2))
            return false;

        for (int i = 0; i < brushCount; i++)
        {
            if (!TryCount(tok, ref at, out int id) || !TryCount(tok, ref at, out int faceCount))
                return false;

            // 16, not 15: the smallest face is its fixed fields plus an extra-layer count of zero. The stack
            // makes a face variable-length, so this bounds the COUNT and each face re-checks as it is read.
            if (!Fits(tok, at, faceCount, stride: 16))
                return false;

            var brush = new VmapBrush { Id = id };
            for (int f = 0; f < faceCount; f++)
            {
                if (at + 15 > tok.Length)
                    return false;
                var face = new VmapFace
                {
                    Plane = new VmapPlane(ReadVec(tok, at), ReadFloat(tok[at + 3])),
                    Projection = ReadProjection(tok, at + 4),
                    SurfaceFlags = int.Parse(tok[at + 12], CultureInfo.InvariantCulture),
                    ContentFlags = int.Parse(tok[at + 13], CultureInfo.InvariantCulture),
                    Material = Unescape(tok[at + 14]),
                };
                at += 15;

                if (!TryCount(tok, ref at, out int extraLayers) || !Fits(tok, at, extraLayers, stride: 11))
                    return false;
                for (int l = 0; l < extraLayers; l++)
                {
                    face.Layers.Add(new VmapFaceLayer
                    {
                        Projection = ReadProjection(tok, at),
                        Blend = (VmapBlend)int.Parse(tok[at + 8], CultureInfo.InvariantCulture),
                        WeightChannel = int.Parse(tok[at + 9], CultureInfo.InvariantCulture),
                        Material = Unescape(tok[at + 10]),
                    });
                    at += 11;
                }
                brush.Faces.Add(face);
            }
            brush.IsToolBrush = brush.ClassifyToolBrush();
            brushes.Add(brush);
        }
        return true;
    }

    private static bool TryReadPatches(string[] tok, ref int at, out List<VmapPatch> patches)
    {
        patches = new List<VmapPatch>();
        if (!TryCount(tok, ref at, out int patchCount))
            return false;

        // The smallest possible patch is its 6 header tokens with a degenerate grid behind them.
        if (!Fits(tok, at, patchCount, stride: 6))
            return false;

        for (int i = 0; i < patchCount; i++)
        {
            if (at + 6 > tok.Length)
                return false;
            var patch = new VmapPatch
            {
                Id = int.Parse(tok[at], CultureInfo.InvariantCulture),
                Width = int.Parse(tok[at + 1], CultureInfo.InvariantCulture),
                Height = int.Parse(tok[at + 2], CultureInfo.InvariantCulture),
                SurfaceFlags = int.Parse(tok[at + 3], CultureInfo.InvariantCulture),
                ContentFlags = int.Parse(tok[at + 4], CultureInfo.InvariantCulture),
                Material = Unescape(tok[at + 5]),
            };
            at += 6;

            // Each dimension is bounded BEFORE they are multiplied: the product of two large ints overflows
            // and can come back small (or negative), which would let a claimed grid past a check on the
            // product alone. MaxGridSide is far above any patch a mapper would build.
            if (patch.Width < 0 || patch.Height < 0
                || patch.Width > MaxGridSide || patch.Height > MaxGridSide)
                return false;

            int cells = patch.Width * patch.Height;
            if (!Fits(tok, at, cells, stride: 5))
                return false;
            for (int c = 0; c < cells; c++, at += 3)
                patch.Controls.Add(ReadVec(tok, at));
            for (int c = 0; c < cells; c++, at += 2)
                patch.ControlUvs.Add(new Vector2(ReadFloat(tok[at]), ReadFloat(tok[at + 1])));
            patches.Add(patch);
        }
        return true;
    }

    private static bool TryReadEntity(string[] tok, ref int at, out VmapEntity entity)
    {
        entity = new VmapEntity();
        if (!TryCount(tok, ref at, out int id) || !TryCount(tok, ref at, out int fieldCount))
            return false;
        if (!Fits(tok, at, fieldCount, stride: 2))
            return false;

        entity.Id = id;
        for (int k = 0; k < fieldCount; k++, at += 2)
            entity.Fields[Unescape(tok[at])] = Unescape(tok[at + 1]);

        // The hoisted property and the key have to stay in step — that is the whole contract of VmapEntity.
        entity.ClassName = entity.Fields.TryGetValue("classname", out string? cn) ? cn : string.Empty;
        return true;
    }

    /// <summary>Read one non-negative count/id and step past it, refusing a line that ended early.</summary>
    private static bool TryCount(string[] tok, ref int at, out int value)
    {
        value = 0;
        if (at >= tok.Length)
            return false;
        value = int.Parse(tok[at++], CultureInfo.InvariantCulture);
        return value >= 0;
    }
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
    /// <param name="echo">
    /// On <see cref="Result.Applied"/>, the line to broadcast: the same op re-encoded AFTER the apply, so any
    /// id the apply minted is in it. Broadcasting the submitted line instead would send "you choose an id" to
    /// every peer and let each of them choose differently.
    /// </param>
    public Result Submit(int clientId, string wireLine, out string? echo)
    {
        echo = null;
        IVmapOp? op = VmapOpWire.Deserialize(wireLine, _session.Document);
        if (op is null)
            return Result.Malformed;
        return Submit(clientId, op, out echo);
    }

    /// <summary>Apply an already-decoded op on behalf of a client.</summary>
    /// <inheritdoc cref="Submit(int, string, out string?)" path="/param[@name='echo']"/>
    public Result Submit(int clientId, IVmapOp op, out string? echo)
    {
        ArgumentNullException.ThrowIfNull(op);
        echo = null;

        if (!Locks.TryAcquire(clientId, op.TouchedBrushIds))
            return Result.Locked;

        bool ok = _session.Apply(op);

        // A lock is only held for the duration of the edit it guarded; holding it past the commit would let a
        // client silently own a brush forever if it never sent an explicit release.
        Locks.Release(clientId, op.TouchedBrushIds);

        if (!ok)
            return Result.Rejected;

        echo = VmapOpWire.SerializeAfterApply(op, _session.Document);
        return Result.Applied;
    }

    // ---- deferred submission ----
    //
    // A client's op arrives on whichever thread reads packets, and the document is owned by the editor on the
    // main thread. Applying it where it lands would be a second writer on shared geometry, which is the same
    // class of bug as the cross-thread transport race — so an incoming op is queued here and applied by the
    // owner when it drains.

    private readonly System.Collections.Concurrent.ConcurrentQueue<(int ClientId, string Line)> _pending = new();

    /// <summary>Queue a client's op for the owning thread to apply. Safe from any thread.</summary>
    public void Enqueue(int clientId, string wireLine)
    {
        if (!string.IsNullOrWhiteSpace(wireLine))
            _pending.Enqueue((clientId, wireLine));
    }

    /// <summary>
    /// Apply every queued op, calling <paramref name="echo"/> with the line to broadcast for each one that
    /// lands. Call from the thread that owns the document. Returns how many were applied.
    /// </summary>
    public int Drain(Action<string>? echo = null)
    {
        int applied = 0;
        while (_pending.TryDequeue(out (int ClientId, string Line) item))
        {
            if (Submit(item.ClientId, item.Line, out string? line) != Result.Applied)
                continue;
            applied++;
            if (line is not null)
                echo?.Invoke(line);
        }
        return applied;
    }
}
