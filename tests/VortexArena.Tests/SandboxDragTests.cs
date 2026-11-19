using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class SandboxDragTests
{
    private sealed class SelectedObject(SandboxMutator.SandboxObject obj) : SandboxMutator.ITraceProvider
    {
        public void TraceForward(Entity player, float distance, out Vector3 endpos,
            out SandboxMutator.SandboxObject? hit)
        {
            endpos = obj.Edict!.Origin;
            hit = obj;
        }
    }

    [Fact]
    public void DefaultDragBindingTracksPressReleaseAndFocusLoss()
    {
        BindTable.Reset();
        try
        {
            BindTable.Bind("V", "+button8");
            BindTable.HandleBind("V", true, command => Assert.Fail(command));
            Assert.True(BindTable.DragHeld);
            BindTable.HandleBind("V", false, command => Assert.Fail(command));
            Assert.False(BindTable.DragHeld);
            BindTable.HandleBind("V", true, command => Assert.Fail(command));
            BindTable.ReleaseAll();
            Assert.False(BindTable.DragHeld);
        }
        finally { BindTable.Reset(); }
    }

    [Fact]
    public void DragHonorsOwnerFreeEditorAndReadonlyAndRestoresPhysics()
    {
        var previous = Api.Services;
        Api.Services = new EngineServices(new CollisionWorld());
        try
        {
            Api.Cvars.Set("g_sandbox", "1");
            Api.Cvars.Set("g_sandbox_editor_free", "1");
            Api.Cvars.Set("g_grab_range", "200");
            Entity owner = Api.Entities.Spawn();
            Entity other = Api.Entities.Spawn();
            var sandbox = new SandboxMutator { CryptoIdfpProvider = client => client == owner ? "owner" : "other" };
            var obj = sandbox.ObjectSpawn(owner, database: false);
            Entity body = obj.Edict!;
            Api.Entities.SetOrigin(body, new Vector3(100f, 0f, 0f));
            body.Gravity = 0.4f;
            body.MoveType = MoveType.None;
            sandbox.Trace = new SelectedObject(obj);
            sandbox.ObjectThink(obj, [owner, other]);

            sandbox.DriveDrag(other, held: true, 0.05f);
            Assert.Equal(MoveType.None, body.MoveType);
            sandbox.DriveDrag(owner, held: true, 0.05f);
            Assert.Equal(MoveType.Walk, body.MoveType);
            Assert.Equal(0.00001f, body.Gravity);
            owner.ViewAngles = new Vector3(0f, 90f, 0f);
            sandbox.DriveDrag(owner, held: true, 0.05f);
            Assert.True(body.Velocity.Y > 0f);
            Assert.Equal(45f, body.Angles.Y);
            sandbox.DriveDrag(owner, held: false, 0.05f);
            Assert.Equal(MoveType.None, body.MoveType);
            Assert.Equal(0.4f, body.Gravity);
            Assert.Equal(Vector3.Zero, body.Velocity);

            Api.Cvars.Set("g_sandbox_editor_free", "2");
            sandbox.ObjectThink(obj, [owner, other]);
            sandbox.DriveDrag(other, held: true, 0.05f);
            Assert.Equal(MoveType.Walk, body.MoveType);
            Api.Cvars.Set("g_sandbox_readonly", "1");
            sandbox.ObjectThink(obj, [owner, other]);
            other.ViewAngles = new Vector3(0f, 90f, 0f);
            sandbox.DriveDrag(other, held: true, 0.05f);
            Assert.Equal(MoveType.Walk, body.MoveType);
            Assert.True(body.Velocity.Y > 0f);
            sandbox.DriveDrag(other, held: false, 0.05f);
            Assert.Equal(MoveType.None, body.MoveType);
            Assert.Equal(0.4f, body.Gravity);
            sandbox.DriveDrag(owner, held: true, 0.05f);
            Assert.Equal(MoveType.None, body.MoveType);
        }
        finally { Api.Services = previous; }
    }
    [Fact]
    public void ObjectSpawnUsesModelBoundsAndSavesCurrentEdictPose()
    {
        var previous = Api.Services;
        Api.Services = new EngineServices(new CollisionWorld());
        try
        {
            Api.Cvars.Set("g_sandbox", "1");
            Api.Cvars.Set("g_sandbox_editor_maxobjects", "4");
            Api.Cvars.Set("g_sandbox_object_scale_min", "0.1");
            Api.Cvars.Set("g_sandbox_object_scale_max", "2");
            Entity player = Api.Entities.Spawn();
            var sandbox = new SandboxMutator
            {
                ModelBoundsProvider = path => path == "models/items/carry.md3"
                    ? (new Vector3(-10, -12, 4), new Vector3(10, 12, 24))
                    : null,
            };

            Assert.True(sandbox.HandleCommand(player, ["g_sandbox", "object_spawn", "models/items/carry.md3"]));
            SandboxMutator.SandboxObject obj = Assert.Single(sandbox.Objects);
            Entity body = obj.Edict!;
            Assert.Equal(new Vector3(-10, -12, 4), body.Mins);

            sandbox.ObjectEditScale(obj, 2);
            sandbox.ObjectEditScale(obj, 0.5f);
            Assert.Equal(new Vector3(-5, -6, 2), body.Mins);
            Assert.Equal(new Vector3(5, 6, 12), body.Maxs);
            sandbox.ObjectEditScale(obj, 0);
            Assert.Equal(new Vector3(-10, -12, 4), body.Mins);

            Api.Entities.SetOrigin(body, new Vector3(7, 11, 13));
            string saved = sandbox.ObjectPortSave(obj, database: true);
            Assert.StartsWith("\"7.000000000 11.000000000 13.000000000\"", saved);
            SandboxMutator.SandboxObject loaded = Assert.IsType<SandboxMutator.SandboxObject>(
                sandbox.ObjectPortLoad(null, saved, database: true));
            Assert.Equal(body.Origin, loaded.Edict!.Origin);
            Assert.Equal(body.Mins, loaded.Edict.Mins);
            Assert.Equal(MoveType.Toss, loaded.Edict.MoveType);
        }
        finally { Api.Services = previous; }
    }

}
