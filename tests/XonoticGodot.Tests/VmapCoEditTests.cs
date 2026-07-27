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
    private static VmapDocument DocWithBox(Vector3 mins, Vector3 maxs, int id = 1)
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

        var doc = new VmapDocument();
        doc.Brushes.Add(b);
        return doc;
    }

    // ---------------------------------------------------------------- wire round-trip

    [Fact]
    public void EveryWiredOp_RoundTripsThroughTheWire()
    {
        var ops = new IVmapOp[]
        {
            new TranslateBrushesOp(new[] { 1, 7 }, new Vector3(64f, -32.5f, 8f)),
            new MoveFaceOp(3, 2, -17.25f),
            new MoveVerticesOp(5, new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) }, new Vector3(0, 0, -12f)),
            new RotateBrushesOp(new[] { 2 }, new Vector3(16, 16, 0), new Vector3(0, 0, 1), 37.5f),
            new DeleteBrushesOp(new[] { 9, 10 }),
        };

        foreach (IVmapOp op in ops)
        {
            string? line = VmapOpWire.Serialize(op);
            Assert.NotNull(line);

            IVmapOp? decoded = VmapOpWire.Deserialize(line!);
            Assert.NotNull(decoded);
            Assert.Equal(op.GetType(), decoded!.GetType());
            Assert.Equal(op.TouchedBrushIds, decoded.TouchedBrushIds);

            // Re-encoding the decoded op must give the identical line: that is what proves no payload was
            // lost or rounded on the way through.
            Assert.Equal(line, VmapOpWire.Serialize(decoded));
        }
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
                 })
        {
            Assert.Null(VmapOpWire.Deserialize(line));
        }
    }

    [Fact]
    public void OpsThatAllocateIds_AreNotWiredYet()
    {
        // Create and clip mint brush ids during Apply, so replicating them needs a server-assigns-id handshake.
        // Until that exists they must report "no wire form" rather than silently desynchronizing ids.
        Assert.Null(VmapOpWire.Serialize(new CreateBoxBrushOp(Vector3.Zero, new Vector3(64, 64, 64), "t")));
        Assert.Null(VmapOpWire.Serialize(new ClipBrushOp(1, new VmapPlane(new Vector3(1, 0, 0), 32f))));
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
            server.Submit(clientId: 1, VmapOpWire.Serialize(new MoveFaceOp(1, 0, 32f))!));

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

        Assert.Equal(VmapEditServer.Result.Locked, server.Submit(clientId: 1, new MoveFaceOp(1, 0, 32f)));
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

        Assert.Equal(VmapEditServer.Result.Rejected, server.Submit(clientId: 1, new MoveFaceOp(1, 0, -256f)));
        Assert.Equal(0, server.Locks.LockedCount);
    }

    [Fact]
    public void Server_ReportsMalformedForGarbage()
    {
        VmapDocument doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Malformed, server.Submit(clientId: 1, "not-an-op 1 2 3"));
    }

    [Fact]
    public void TwoClientsEditingDifferentBrushes_BothSucceed()
    {
        var doc = DocWithBox(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        VmapDocument second = DocWithBox(new Vector3(128, 0, 0), new Vector3(192, 64, 64), id: 2);
        doc.Brushes.Add(second.Brushes[0]);

        var server = new VmapEditServer(new VmapEditSession(doc));

        Assert.Equal(VmapEditServer.Result.Applied, server.Submit(1, new MoveFaceOp(1, 0, 16f)));
        Assert.Equal(VmapEditServer.Result.Applied, server.Submit(2, new MoveFaceOp(2, 0, 16f)));
    }
}
