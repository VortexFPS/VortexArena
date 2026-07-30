using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// <see cref="ServerServices"/> is the decorator that becomes the ambient <see cref="Api.Services"/> on a
/// server/listen host. It MUST forward every <see cref="IEngineServices"/> member to the wrapped
/// <see cref="EngineServices"/> — any member it forgets silently falls through to the interface's DEFAULT
/// implementation (e.g. <see cref="ISurfaceService"/> defaults to <see cref="NullSurfaceService"/>).
///
/// The bug this guards: <c>Surfaces</c> was NOT forwarded, so every server-side <c>getsurface*</c> query
/// returned empty. That broke the warpzone brush→plane auto-derivation (WarpzoneManager.DerivePlaneFromBrush),
/// so NO map warpzones ever spawned (0 zones) — they neither teleported nor rendered.
/// </summary>
public class ServerServicesForwardingTests
{
    private static (EngineServices inner, ServerServices server) Make()
    {
        var inner = new EngineServices(new CollisionWorld());
        return (inner, new ServerServices(inner));
    }

    [Fact]
    public void ForwardsSurfacesToTheRealService_NotTheNullStub()
    {
        (EngineServices inner, ServerServices server) = Make();
        Assert.Same(inner.Surfaces, server.Surfaces);
        Assert.IsNotType<NullSurfaceService>(server.Surfaces);
    }

    [Fact]
    public void ForwardsEveryEngineServiceMember()
    {
        (EngineServices inner, ServerServices server) = Make();
        Assert.Same(inner.Trace, server.Trace);
        Assert.Same(inner.Cvars, server.Cvars);
        Assert.Same(inner.Sound, server.Sound);
        Assert.Same(inner.Models, server.Models);
        Assert.Same(inner.Clock, server.Clock);
        Assert.Same(inner.Surfaces, server.Surfaces);
        // Entities is intentionally wrapped (ServerEntityService) rather than forwarded verbatim, so it is not
        // Same(inner.Entities) — but it must still be present + backed by the inner table.
        Assert.NotNull(server.Entities);
        Assert.Same(inner.EntityTable, server.ServerEntities.Inner);
    }
}
