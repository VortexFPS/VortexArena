using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The <c>editor_op</c> command bus (phase E6): a client's submitted op travelling through
/// <see cref="Commands"/> to the authoritative document, and the echo that comes back out.
///
/// The wire codec is covered in <see cref="VmapCoEditTests"/>; what is tested here is the SEAM — that the
/// command exists, is gated on an editing session, defers rather than mutating on whatever thread it landed
/// on, and produces exactly one echo per applied op. A codec that round-trips into a command nobody drains is
/// the failure this file exists to catch.
/// </summary>
[Collection("GlobalState")]
public class VmapEditorOpCommandTests
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

    private static GameWorld EditorWorld()
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("editor");
        return world;
    }

    private static Player NewCaller(string name = "mapper")
        => new() { NetName = name, Flags = EntFlags.Client, PlayerId = 1 };

    [Fact]
    public void EditorOp_IsInertWithoutASession()
    {
        // No session open: the command must say so and change nothing, rather than faulting on a null document.
        GameWorld world = EditorWorld();
        Assert.Null(world.Commands.EditorOps);

        var result = world.Commands.Execute(
            "editor_op move 1 1 0 0 64", isServerConsole: false, caller: NewCaller());
        Assert.Contains("no editing session", result.Output);
    }

    [Fact]
    public void EditorOp_IsInertOutsideTheEditorGametype()
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        world.Commands.EditorOps = new VmapEditServer(
            new VmapEditSession(DocWithBox(Vector3.Zero, new Vector3(64, 64, 64))));

        var result = world.Commands.Execute(
            "editor_op move 1 1 0 0 64", isServerConsole: false, caller: NewCaller());
        Assert.Contains("not in the editor gametype", result.Output);
    }

    [Fact]
    public void ASubmittedOp_DoesNotLandUntilItIsDrained()
    {
        // The command runs on whichever thread read the packet; the document belongs to the editor on the main
        // thread. Applying where it landed would be a second writer on shared geometry.
        GameWorld world = EditorWorld();
        VmapDocument doc = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        var ops = new VmapEditServer(new VmapEditSession(doc));
        world.Commands.EditorOps = ops;

        world.Commands.Execute("editor_op move 1 1 0 0 64", isServerConsole: false, caller: NewCaller());

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 stillThere, out _));
        Assert.Equal(0f, stillThere.Z, 3);

        Assert.Equal(1, ops.Drain());
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 moved, out _));
        Assert.Equal(64f, moved.Z, 3);
    }

    [Fact]
    public void DrainingProducesOneEchoPerAppliedOp()
    {
        GameWorld world = EditorWorld();
        VmapDocument doc = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        var ops = new VmapEditServer(new VmapEditSession(doc));
        world.Commands.EditorOps = ops;
        Player caller = NewCaller();

        world.Commands.Execute("editor_op move 1 1 0 0 16", isServerConsole: false, caller: caller);
        world.Commands.Execute("editor_op move 1 1 0 0 16", isServerConsole: false, caller: caller);
        world.Commands.Execute("editor_op wobble 3 4 5", isServerConsole: false, caller: caller);   // garbage

        var echoes = new List<string>();
        Assert.Equal(2, ops.Drain(echoes.Add));
        Assert.Equal(2, echoes.Count);
        Assert.All(echoes, e => Assert.StartsWith("move ", e));

        // Both moves landed; the malformed line was dropped without disturbing them.
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        Assert.Equal(32f, mins.Z, 3);
    }

    [Fact]
    public void ASubmittedCreate_IsEchoedWithTheIdTheServerChose()
    {
        // The handshake, over the real command bus: the client asks for "a brush", the server names it, and
        // the echo is what every other peer replays.
        GameWorld world = EditorWorld();
        VmapDocument doc = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        var ops = new VmapEditServer(new VmapEditSession(doc));
        world.Commands.EditorOps = ops;

        world.Commands.Execute(
            "editor_op mkbrush 0 128 0 0 192 64 64 textures/test/wall",
            isServerConsole: false, caller: NewCaller());

        string? echo = null;
        Assert.Equal(1, ops.Drain(line => echo = line));
        Assert.Equal(2, doc.Brushes.Count);

        int assigned = doc.Brushes[^1].Id;
        Assert.NotEqual(0, assigned);
        Assert.StartsWith($"mkbrush {assigned} ", echo);

        // A peer replaying that echo ends up with the same numbering, which is the whole point of the field.
        VmapDocument peer = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        Assert.True(VmapOpWire.Deserialize(echo!)!.Apply(peer));
        Assert.Equal(doc.Brushes.Select(b => b.Id), peer.Brushes.Select(b => b.Id));
    }

    [Fact]
    public void ARefusedOp_ProducesNoEchoAndLeavesTheDocumentAlone()
    {
        // A push that would collapse the solid is neither malformed nor locked; it simply must not happen, and
        // must not be broadcast as though it had.
        GameWorld world = EditorWorld();
        VmapDocument doc = DocWithBox(Vector3.Zero, new Vector3(64, 64, 64));
        var ops = new VmapEditServer(new VmapEditSession(doc));
        world.Commands.EditorOps = ops;

        world.Commands.Execute("editor_op face 1 0 -256", isServerConsole: false, caller: NewCaller());

        var echoes = new List<string>();
        Assert.Equal(0, ops.Drain(echoes.Add));
        Assert.Empty(echoes);
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(64f, maxs.X, 3);
    }
}
