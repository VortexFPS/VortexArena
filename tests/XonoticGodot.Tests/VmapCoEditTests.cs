using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers co-editing (phase E6): the op wire format and the server-authoritative apply path with per-brush
/// locks (design doc §11.7).
///
/// The property that matters is that an op means the same thing on both ends. A drag encoded on one machine
/// and applied on another must produce identical geometry — otherwise two mappers in a session slowly diverge
/// and neither can tell which one is looking at the real map.
/// </summary>
public class VmapCoEditTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), maxs.X);
        Face(new Vector3(-1, 0, 0), -mins.X);
        Face(new Vector3(0, 1, 0), maxs.Y);
        Face(new Vector3(0, -1, 0), -mins.Y);
        Face(new Vector3(0, 0, 1), maxs.Z);
        Face(new Vector3(0, 0, -1), -mins.Z);
        return b;
    }

    private static VmapDocument DocWithBox(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(mins, maxs, id));
        return doc;
    }

    private static VmapPatch FlatPatch(int id = 1, float half = 32f)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * half, (row - 1) * half, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    // ---------------------------------------------------------------- wire round-trip

    /// <summary>
    /// One entry per replicable op. Re-encoding a decoded op has to give back the identical line: any field
    /// the codec forgot, truncated or rounded shows up as a mismatch here rather than as two mappers watching
    /// different maps.
    /// </summary>
    public static TheoryData<IVmapOp> WiredOps() => new()
    {
        new TranslateBrushesOp(new[] { 1, 7 }, new Vector3(64f, -32.5f, 8f)),
        new MoveFaceOp(3, 2, -17.25f),
        new MoveVerticesOp(5, new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) }, new Vector3(0, 0, -12f)),
        new RotateBrushesOp(new[] { 2 }, new Vector3(16, 16, 0), new Vector3(0, 0, 1), 37.5f),
        new DeleteBrushesOp(new[] { 9, 10 }),
        new ScaleSelectionOp(new[] { 1, 2 }, new[] { 4 }, new Vector3(8, 8, 8), new Vector3(2f, 0.5f, 1.25f),
            textureLock: true, entityIds: new[] { 6 }),
        new RotateSelectionOp(new[] { 1 }, new[] { 3, 4 }, Vector3.Zero, new Vector3(0, 0, 1), -90f),
        new TranslatePatchesOp(new[] { 3 }, new Vector3(0f, 0f, 12.75f)),
        new DeletePatchesOp(new[] { 3, 5 }),
        new MovePatchControlOp(2, 4, new Vector3(0f, 0f, 33.5f)),
        new ModifyPatchOp(2, PatchOperation.InsertRows),
        new SetFaceMaterialOp(1, 2, "textures/exx/floor01"),
        new SetFaceProjectionOp(1, 0, new VmapTexProjection(
            new Vector3(0.015625f, 0f, 0f), new Vector3(0f, -0.015625f, 0f), 3.5f, -7.25f)),
        new SetFaceFlagsOp(1, 3, 0x4, 0x1),
        new BevelEdgeOp(1, new Vector3(0, 0, 64), new Vector3(64, 0, 64), 8f),
        new SnapBrushToGridOp(new[] { 1, 2, 3 }, 16f),
        new SetEntityKeyOp(5, "target", "door_1"),
        new MoveEntitiesOp(new[] { 5, 6 }, new Vector3(0f, 128f, 0f)),
        new RotateEntitiesOp(new[] { 5 }, new Vector3(64, 64, 0), 45f),
        new DeleteEntitiesOp(new[] { 7 }),
        new CreateBoxBrushOp(Vector3.Zero, new Vector3(64, 64, 64), "textures/test/wall"),
        new CreatePatchOp(PatchPrimitive.Cylinder, Vector3.Zero, new Vector3(64, 64, 128), "textures/test/curve", 9, 3),
        new CreateEntityOp("info_player_deathmatch", new Vector3(0, 0, 24)),
        new CreateBrushEntityOp("func_door", new[] { 1, 2 }, new[] { 3 },
            new Dictionary<string, string> { ["speed"] = "100", ["targetname"] = "big door" }),
        new DissolveBrushEntityOp(new[] { 4, 5 }),
        new ExtrudeFaceOp(1, 4, 32f),
        new ClipSelectionOp(new[] { 1, 2 }, new VmapPlane(new Vector3(1, 0, 0), 32f), ClipKeep.Both),
    };

    [Theory]
    [MemberData(nameof(WiredOps))]
    public void EveryWiredOp_RoundTripsThroughTheWire(IVmapOp op)
    {
        string? line = VmapOpWire.Serialize(op);
        Assert.NotNull(line);

        IVmapOp? decoded = VmapOpWire.Deserialize(line!);
        Assert.NotNull(decoded);
        Assert.Equal(op.GetType(), decoded!.GetType());
        Assert.Equal(op.Describe(), decoded.Describe());
        Assert.Equal(line, VmapOpWire.Serialize(decoded));
    }

    [Fact]
    public void AnEncodedDrag_AppliesIdenticallyOnAnotherDocument()
    {
        // The real co-editing invariant: same op, same starting geometry, same result on both machines.
        VmapDocument local = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        VmapDocument remote = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));

        var op = new MoveVerticesOp(1, new[] { new Vector3(64, 64, 64) }, new Vector3(-8f, 0f, -19.5f));
        Assert.True(op.Apply(local));

        IVmapOp? decoded = VmapOpWire.Deserialize(VmapOpWire.Serialize(op)!);
        Assert.True(decoded!.Apply(remote));

        Vector3[] a = VmapWinding.BrushPoints(local.Brushes[0]).OrderBy(Key).ToArray();
        Vector3[] b = VmapWinding.BrushPoints(remote.Brushes[0]).OrderBy(Key).ToArray();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True((a[i] - b[i]).Length() < 1e-4f, $"geometry diverged at vertex {i}: {a[i]} vs {b[i]}");

        static float Key(Vector3 v) => v.X * 1e6f + v.Y * 1e3f + v.Z;
    }

    [Fact]
    public void MalformedAndUnknownLines_DecodeToNullRatherThanThrowing()
    {
        // A peer can send anything. Rejecting the op must never fault the session.
        foreach (string line in new[]
                 {
                     "", "   ", "face", "face 1", "wobble 1 2 3",
                     "move 2 1", "move notanumber 1 2 3", "verts 1 -5 0 0 0",
                     "rotate 1 1 0 0 0 0 0 1 notanangle",
                     "mkent 0 0 0 0", "mkent 0 0 0 0 cls 3 a b", "add 2 1 6",
                     "mkbent", "mkbent 0", "mkbent 0 1 1", "mkbent 0 1 1 0 cls", "entdissolve",
                     "add 1 1 1 1 0 0 0", "clip 1 1 1 0 0 0 0",
                 })
        {
            Assert.Null(VmapOpWire.Deserialize(line));
        }
    }

    [Fact]
    public void AHugeDeclaredCount_IsRejectedRatherThanAllocated()
    {
        // Every one of these declares a list far longer than the line that carries it. The obvious bounds
        // check — `at + count * stride > tok.Length` — OVERFLOWS for a large count, wraps negative, and sails
        // through the comparison, after which the count is used to size an array. A peer that picks the number
        // gets an out-of-memory abort out of it, which is a remote kill on the editing session.
        foreach (string line in new[]
                 {
                     "move 2147483647 1 2 3",                       // TryReadIds
                     "delete 2147483647",                           // TryReadIds
                     "verts 1 715827883 0 0 0 0 0 0",               // vertex array
                     "mkent 0 0 0 0 cls 1073741824 k v",            // entity field pairs
                     "add 2147483647",                              // brush list
                     "add 0 2147483647",                            // patch list
                     "add 0 0 2147483647",                          // entity list
                     "set 0 0 2147483647",                          // entity list
                     "add 0 1 1 2147483647 2147483647 0 0 t",       // patch cell count (Width*Height overflow)
                     "clip 2147483647 1 0 0 0 0",                   // touched-id list
                     "mkbent 0 2147483647",                         // brush list of a brush entity
                     "mkbent 0 0 2147483647",                       // its patch list
                     "mkbent 0 0 0 cls 1073741824 k v",             // its field pairs
                     "entdissolve 2147483647",                      // dissolve id list
                 })
        {
            Assert.Null(VmapOpWire.Deserialize(line));
        }
    }

    [Fact]
    public void StringsWithSpacesAndEmptyValues_SurviveTheWire()
    {
        // Shader names and spawn values are user text. A space would split into two tokens and shift every
        // field after it; an empty value would vanish entirely, which is exactly what CLEARING a key is.
        var mat = new SetFaceMaterialOp(1, 0, "textures/my map/wall 01");
        var decodedMat = (SetFaceMaterialOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(mat)!)!;
        Assert.Equal("textures/my map/wall 01", decodedMat.Material);

        var clear = new SetEntityKeyOp(5, "message", "");
        var decodedClear = (SetEntityKeyOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(clear)!)!;
        Assert.Equal("message", decodedClear.Key);
        Assert.Equal("", decodedClear.Value);

        // A literal backslash must not decode as an escape of whatever follows it.
        var slash = new SetEntityKeyOp(5, "message", @"a\sb c");
        var decodedSlash = (SetEntityKeyOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(slash)!)!;
        Assert.Equal(@"a\sb c", decodedSlash.Value);
    }

    // ---------------------------------------------------------------- the id handshake

    [Fact]
    public void ACreate_CarriesTheServerAssignedIdInItsEcho()
    {
        // A client asks for "a brush" (id 0). The server mints one and re-encodes; the echoed line names the
        // id, so every peer replaying it numbers the brush the same way.
        VmapDocument server = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        VmapDocument peer = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));

        string request = VmapOpWire.Serialize(
            new CreateBoxBrushOp(new Vector3(128, 0, 0), new Vector3(192, 64, 64), "textures/test/wall"))!;
        Assert.Contains("mkbrush 0 ", request);

        var op = (CreateBoxBrushOp)VmapOpWire.Deserialize(request)!;
        Assert.True(op.Apply(server));
        Assert.NotEqual(0, op.CreatedBrushId);

        string echo = VmapOpWire.SerializeAfterApply(op, server)!;
        Assert.Contains($"mkbrush {op.CreatedBrushId} ", echo);

        Assert.True(VmapOpWire.Deserialize(echo)!.Apply(peer));
        Assert.Equal(server.Brushes.Select(b => b.Id), peer.Brushes.Select(b => b.Id));
    }

    [Fact]
    public void ACreateReplayedTwice_LandsTheSameIdBothTimes()
    {
        // Undo then redo of a replicated create must not renumber the brush: an id that moved would leave every
        // later op in the journal pointing at the wrong solid.
        VmapDocument doc = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        var op = (CreateBoxBrushOp)VmapOpWire.Deserialize(
            "mkbrush 42 128 0 0 192 64 64 textures/test/wall")!;

        Assert.True(op.Apply(doc));
        Assert.Equal(42, op.CreatedBrushId);

        doc.Brushes.RemoveAll(b => b.Id == 42);
        Assert.True(op.Apply(doc));
        Assert.Equal(42, op.CreatedBrushId);
    }

    [Fact]
    public void ASplit_CarriesTheOffCutIdsSoBothSidesNumberThemAlike()
    {
        var server = new VmapDocument();
        server.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        server.Brushes.Add(Box(new Vector3(0, 128, 0), new Vector3(64, 192, 64), id: 2));

        var peer = new VmapDocument();
        peer.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        peer.Brushes.Add(Box(new Vector3(0, 128, 0), new Vector3(64, 192, 64), id: 2));

        var op = new ClipSelectionOp(new[] { 1, 2 }, new VmapPlane(new Vector3(1, 0, 0), 32f), ClipKeep.Both);
        Assert.True(op.Apply(server));
        Assert.Equal(2, op.CreatedBrushIds.Count);

        Assert.True(VmapOpWire.Deserialize(VmapOpWire.SerializeAfterApply(op, server)!)!.Apply(peer));
        Assert.Equal(server.Brushes.Select(b => b.Id).Order(), peer.Brushes.Select(b => b.Id).Order());
    }

    // ---------------------------------------------------------------- paste, as an add

    [Fact]
    public void APaste_ReplicatesAsItsResultRatherThanAsTheGesture()
    {
        // A paste's output is an arbitrary pile of geometry with freshly minted ids, so what crosses the wire
        // is the RESULT — these solids, these ids — not "run my clipboard against your document".
        var source = new VmapDocument();
        source.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        source.Patches.Add(FlatPatch(id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.Fields["message"] = "the big door";
        door.BrushIds.Add(1);
        door.PatchIds.Add(1);
        source.Entities.Add(door);

        var clip = new VmapClipboard();
        Assert.Equal(3, clip.CopyFrom(source, new[]
        {
            VmapSelection.OfBrush(1), VmapSelection.OfPatch(1), VmapSelection.OfEntity(1),
        }));

        VmapDocument server = DocWithBox(new Vector3(-256, -256, -256), new Vector3(-192, -192, -192), id: 9);
        VmapDocument peer = DocWithBox(new Vector3(-256, -256, -256), new Vector3(-192, -192, -192), id: 9);

        var paste = new PasteOp(clip, new Vector3(512, 0, 0));
        Assert.True(paste.Apply(server));

        string echo = VmapOpWire.SerializeAfterApply(paste, server)!;
        Assert.StartsWith("add ", echo);
        Assert.True(VmapOpWire.Deserialize(echo)!.Apply(peer));

        Assert.Equal(server.Brushes.Select(b => b.Id), peer.Brushes.Select(b => b.Id));
        Assert.Equal(server.Patches.Select(p => p.Id), peer.Patches.Select(p => p.Id));
        Assert.Equal(server.Entities.Select(e => e.Id), peer.Entities.Select(e => e.Id));

        // Ownership travelled as indices, so the pasted door owns the PASTED geometry on both machines.
        VmapEntity pastedHere = server.Entities[^1], pastedThere = peer.Entities[^1];
        Assert.Equal(pastedHere.BrushIds, pastedThere.BrushIds);
        Assert.Equal(pastedHere.PatchIds, pastedThere.PatchIds);
        Assert.Equal("the big door", pastedThere.Fields["message"]);
        Assert.Equal("func_door", pastedThere.ClassName);

        // And the geometry itself, not just the numbering.
        Assert.True(VmapWinding.TryGetBounds(server.Brushes[^1], out Vector3 aMins, out _));
        Assert.True(VmapWinding.TryGetBounds(peer.Brushes[^1], out Vector3 bMins, out _));
        Assert.True((aMins - bMins).Length() < 1e-3f);
        Assert.Equal(server.Patches[^1].Controls, peer.Patches[^1].Controls);
    }

    [Fact]
    public void AGuestPaste_TravelsAsItsClipboardWithIdsLeftForTheServer()
    {
        // A guest has applied nothing, so it cannot capture a result out of its document the way the host does.
        // It describes the clipboard instead, ids at zero. Without this a guest simply cannot paste — PasteOp
        // has no wire verb of its own.
        var source = new VmapDocument();
        source.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        source.Patches.Add(FlatPatch(id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        door.PatchIds.Add(1);
        source.Entities.Add(door);

        var clip = new VmapClipboard();
        clip.CopyFrom(source, new[]
        {
            VmapSelection.OfBrush(1), VmapSelection.OfPatch(1), VmapSelection.OfEntity(1),
        });

        AddObjectsOp submitted = clip.ToAddObjects(new Vector3(512, 0, 0));
        Assert.All(submitted.Brushes, b => Assert.Equal(0, b.Id));
        Assert.All(submitted.Patches, p => Assert.Equal(0, p.Id));
        Assert.All(submitted.Entities, e => Assert.Equal(0, e.Id));

        // It survives the wire, and the SERVER's apply is what names everything.
        var decoded = (AddObjectsOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(submitted)!)!;
        VmapDocument server = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64), id: 9);
        Assert.True(decoded.Apply(server));

        Assert.Equal(2, server.Brushes.Count);
        Assert.NotEqual(0, server.Brushes[^1].Id);
        Assert.Single(server.Entities);

        // Ownership survived as indices.
        Assert.Equal(new[] { server.Brushes[^1].Id }, server.Entities[0].BrushIds);
        Assert.Equal(new[] { server.Patches[^1].Id }, server.Entities[0].PatchIds);

        // And it landed exactly where a host-side paste of the same clipboard would have put it — the property
        // that matters, and the one a hand-computed coordinate gets wrong (the pivot averages the box AND the
        // patch, not just the box).
        VmapDocument host = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64), id: 9);
        Assert.True(new PasteOp(clip, new Vector3(512, 0, 0)).Apply(host));

        Assert.True(VmapWinding.TryGetBounds(server.Brushes[^1], out Vector3 guestMins, out _));
        Assert.True(VmapWinding.TryGetBounds(host.Brushes[^1], out Vector3 hostMins, out _));
        Assert.True((guestMins - hostMins).Length() < 1e-3f, $"{guestMins} vs {hostMins}");
        Assert.Equal(host.Patches[^1].Controls, server.Patches[^1].Controls);
    }

    [Fact]
    public void AnAddOp_RoundTripsEveryFaceAndControlPoint()
    {
        var doc = new VmapDocument();
        VmapBrush brush = Box(Vector3.Zero, new Vector3(64, 64, 64), id: 3);
        brush.Faces[0].SurfaceFlags = 0x8;
        brush.Faces[0].ContentFlags = 0x2;
        brush.Faces[0].Material = "textures/spaced name/wall";
        doc.Brushes.Add(brush);
        doc.Patches.Add(FlatPatch(id: 4));

        var add = AddObjectsOp.Capture(doc, new[] { 3 }, new[] { 4 }, Array.Empty<int>());
        var decoded = (AddObjectsOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(add)!)!;

        Assert.Equal(VmapOpWire.Serialize(add), VmapOpWire.Serialize(decoded));
        Assert.Equal(0x8, decoded.Brushes[0].Faces[0].SurfaceFlags);
        Assert.Equal(0x2, decoded.Brushes[0].Faces[0].ContentFlags);
        Assert.Equal("textures/spaced name/wall", decoded.Brushes[0].Faces[0].Material);
        Assert.Equal(doc.Patches[0].Controls, decoded.Patches[0].Controls);
        Assert.Equal(doc.Patches[0].ControlUvs, decoded.Patches[0].ControlUvs);
    }

    // ---------------------------------------------------------------- undo, as a restored state

    [Fact]
    public void AnUndo_ReplicatesAsTheStateItPutBack()
    {
        // Undo restores a snapshot rather than replaying an inverse gesture, so there is no op to send. What
        // has to cross the wire is the outcome — or a co-editing session diverges the first time anyone
        // presses Ctrl+Z, which in a map editor is within the first minute.
        VmapDocument server = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        VmapDocument peer = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));

        var session = new VmapEditSession(server);
        SetObjectsOp? captured = null;
        session.Restored += (b, p, e) => captured = SetObjectsOp.Capture(server, b, p, e);

        var move = new TranslateBrushesOp(new[] { 1 }, new Vector3(0, 0, 128f));
        Assert.True(session.Apply(move));
        Assert.True(VmapOpWire.Deserialize(VmapOpWire.Serialize(move)!)!.Apply(peer));

        Assert.True(session.Undo());
        Assert.NotNull(captured);
        Assert.True(VmapOpWire.Deserialize(VmapOpWire.Serialize(captured!)!)!.Apply(peer));

        Assert.True(VmapWinding.TryGetBounds(server.Brushes[0], out Vector3 a, out _));
        Assert.True(VmapWinding.TryGetBounds(peer.Brushes[0], out Vector3 b2, out _));
        Assert.True((a - b2).Length() < 1e-3f, $"undo did not replicate: {a} vs {b2}");
    }

    [Fact]
    public void UndoingACreate_RemovesTheBrushOnTheReceiverToo()
    {
        // The other half of a restore: an id that no longer exists must be named, or the peer keeps a solid
        // the author has already taken back.
        VmapDocument server = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        VmapDocument peer = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));

        var session = new VmapEditSession(server);
        SetObjectsOp? captured = null;
        session.Restored += (b, p, e) => captured = SetObjectsOp.Capture(server, b, p, e);

        var create = new CreateBoxBrushOp(new Vector3(128, 0, 0), new Vector3(192, 64, 64), "t");
        Assert.True(session.Apply(create));
        Assert.True(VmapOpWire.Deserialize(VmapOpWire.SerializeAfterApply(create, server)!)!.Apply(peer));
        Assert.Equal(2, peer.Brushes.Count);

        Assert.True(session.Undo());
        Assert.True(VmapOpWire.Deserialize(VmapOpWire.Serialize(captured!)!)!.Apply(peer));

        Assert.Equal(server.Brushes.Select(x => x.Id), peer.Brushes.Select(x => x.Id));
        Assert.Single(peer.Brushes);
    }

    [Fact]
    public void ARestore_CarriesEntityFieldsAndOwnership()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        doc.Patches.Add(FlatPatch(id: 2));
        var door = new VmapEntity { Id = 3, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.Fields["message"] = "mind the gap";
        door.BrushIds.Add(1);
        door.PatchIds.Add(2);
        doc.Entities.Add(door);

        var op = SetObjectsOp.Capture(doc, new[] { 1 }, new[] { 2 }, new[] { 3, 99 });
        var decoded = (SetObjectsOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(op)!)!;

        Assert.Equal(VmapOpWire.Serialize(op), VmapOpWire.Serialize(decoded));
        Assert.Equal(new[] { 99 }, decoded.RemovedEntityIds);       // id 99 was never in the document
        Assert.Equal("mind the gap", decoded.Entities[0].Fields["message"]);
        Assert.Equal(new[] { 1 }, decoded.Entities[0].BrushIds);
        Assert.Equal(new[] { 2 }, decoded.Entities[0].PatchIds);

        // And it applies onto a document that has drifted, putting every field back.
        var target = new VmapDocument();
        target.Brushes.Add(Box(new Vector3(500, 500, 500), new Vector3(564, 564, 564), id: 1));
        Assert.True(decoded.Apply(target));
        Assert.True(VmapWinding.TryGetBounds(target.Brushes[0], out Vector3 mins, out _));
        Assert.True(mins.Length() < 1e-3f);
        Assert.Single(target.Entities);
        Assert.Equal("func_door", target.Entities[0].ClassName);
    }

    // ---------------------------------------------------------------- locks

    [Fact]
    public void Locks_AreAllOrNothingAcrossTheTouchedSet()
    {
        var locks = new VmapEditLocks();

        Assert.True(locks.TryAcquire(clientId: 1, new[] { 10, 11 }));

        // Client 2 wants 11 and 12; 11 is taken, so it must get NEITHER — a partially-locked op could begin
        // and then be blocked halfway through its own touched set.
        Assert.False(locks.TryAcquire(clientId: 2, new[] { 11, 12 }));
        Assert.Null(locks.OwnerOf(12));

        Assert.True(locks.TryAcquire(clientId: 2, new[] { 12 }));
        Assert.Equal(2, locks.OwnerOf(12));
    }

    [Fact]
    public void Locks_AreReentrantForTheSameClientAndReleasedOnDisconnect()
    {
        var locks = new VmapEditLocks();

        Assert.True(locks.TryAcquire(1, new[] { 10 }));
        Assert.True(locks.TryAcquire(1, new[] { 10, 11 }));   // extending your own lock is fine
        Assert.True(locks.IsLockedByOther(clientId: 2, brushId: 10));
        Assert.False(locks.IsLockedByOther(clientId: 1, brushId: 10));

        locks.ReleaseAll(1);
        Assert.Equal(0, locks.LockedCount);
        Assert.True(locks.TryAcquire(2, new[] { 10, 11 }));
    }

    // ---------------------------------------------------------------- authoritative apply

    [Fact]
    public void Server_AppliesAValidOpAndReleasesTheLock()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Applied,
            server.Submit(clientId: 1, VmapOpWire.Serialize(new MoveFaceOp(1, 0, 32f))!, out string? echo));
        Assert.NotNull(echo);

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(96f, maxs.X, 3);

        // The lock is scoped to the edit it guarded; holding it past the commit would let a client that never
        // sends an explicit release own a brush forever.
        Assert.Equal(0, server.Locks.LockedCount);
    }

    [Fact]
    public void Server_RefusesAnOpOnABrushAnotherClientHolds()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.True(server.Locks.TryAcquire(clientId: 2, new[] { 1 }));   // client 2 is mid-drag

        Assert.Equal(VmapEditServer.Result.Locked,
            server.Submit(clientId: 1, new MoveFaceOp(1, 0, 32f), out string? echo));
        Assert.Null(echo);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(64f, maxs.X, 3);   // untouched
    }

    [Fact]
    public void Server_ReportsRejectedForWellFormedButInvalidGeometry()
    {
        // A push that collapses the solid is not malformed and not locked — it is simply invalid, and the
        // sender needs to know the difference so it can roll back its optimistic local preview.
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Rejected,
            server.Submit(clientId: 1, new MoveFaceOp(1, 0, -256f), out string? echo));
        Assert.Null(echo);
        Assert.Equal(0, server.Locks.LockedCount);
    }

    [Fact]
    public void Server_ReportsMalformedForGarbage()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Malformed, server.Submit(clientId: 1, "not-an-op 1 2 3", out _));
    }

    [Fact]
    public void TwoClientsEditingDifferentBrushes_BothSucceed()
    {
        var doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        VmapDocument second = DocWithBox(new Vector3(128, 0, 0), new Vector3(192, 64, 64), id: 2);
        doc.Brushes.Add(second.Brushes[0]);

        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Applied, server.Submit(1, new MoveFaceOp(1, 0, 16f), out _));
        Assert.Equal(VmapEditServer.Result.Applied, server.Submit(2, new MoveFaceOp(2, 0, 16f), out _));
    }

    [Fact]
    public void AReplicatedBrushEntityMove_IsUndoableOnTheReceiver()
    {
        // MoveEntitiesOp has to know which brushes an entity owns BEFORE it runs, so the journal can snapshot
        // them. Only the document knows that, so a decode without one applies the move and then cannot undo
        // the geometry half of it.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var session = new VmapEditSession(doc);
        IVmapOp? op = VmapOpWire.Deserialize(
            VmapOpWire.Serialize(new MoveEntitiesOp(new[] { 1 }, new Vector3(0, 0, 64f), doc))!, doc);

        Assert.True(session.Apply(op!));
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 moved, out _));
        Assert.Equal(64f, moved.Z, 3);

        Assert.True(session.Undo());
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 back, out _));
        Assert.Equal(0f, back.Z, 3);
    }
}
