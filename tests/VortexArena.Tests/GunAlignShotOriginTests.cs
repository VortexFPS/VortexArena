using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Playtest #62: the authoritative shot origin follows Base's <c>shotorg_adjust</c> (common/weapons/all.qc:260) —
/// <c>g_shootfromeye</c> fires from the eye line, <c>g_shootfromcenter</c> centres the gun, and with both off the
/// shooter's replicated <c>cl_gunalign</c> picks the side (centre = Y 0 and Z −2, left = mirrored Y). Before the
/// fix the server always fired from the raw right-hand <c>tag_shot</c>, whatever the alignment.
/// </summary>
[Collection("GlobalState")]
public class GunAlignShotOriginTests
{
    private const int WeaponId = 11;
    private static readonly Vector3 Tag = new(20f, -6f, -4f); // a right-hand tag_shot: forward, +left, up

    private static Entity MakePlayer() => new()
    {
        Flags = EntFlags.Client,
        Origin = Vector3.Zero,
        Angles = Vector3.Zero, // forward +X, left +Y, up +Z: the offset reads straight off in world axes
        ViewOfs = new Vector3(0, 0, 20),
        ActiveWeaponId = WeaponId,
    };

    private static Vector3 ShotOffset(Dictionary<string, string> cvars, int gunalign)
    {
        IEngineServices prev = Api.Services;
        Func<Entity, int>? prevProvider = WeaponFiring.GunAlignProvider;
        try
        {
            Api.Services = new Services(cvars);
            WeaponFiring.ClearMuzzleOffsets();
            WeaponFiring.RegisterMuzzleOffset(WeaponId, Tag);
            WeaponFiring.GunAlignProvider = _ => gunalign;
            Entity actor = MakePlayer();
            ShotInfo shot = WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0));
            return shot.Origin - (actor.Origin + actor.ViewOfs);
        }
        finally
        {
            WeaponFiring.ClearMuzzleOffsets();
            WeaponFiring.GunAlignProvider = prevProvider;
            Api.Services = prev;
        }
    }

    private static Dictionary<string, string> ClientDecides() => new()
    {
        ["g_shootfromeye"] = "0", ["g_shootfromcenter"] = "0", ["g_shootfromfixedorigin"] = "",
    };

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void Right_Alignment_Fires_From_The_Authored_Tag()
        => AssertVec(Tag, ShotOffset(ClientDecides(), 3));

    [Fact]
    public void Left_Alignment_Mirrors_The_Side()
        => AssertVec(new Vector3(20f, 6f, -4f), ShotOffset(ClientDecides(), 4));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Centre_Alignment_Moves_The_Shot_To_The_Middle(int algn)
        => AssertVec(new Vector3(20f, 0f, -6f), ShotOffset(ClientDecides(), algn));

    [Fact]
    public void Out_Of_Range_Alignment_Reads_As_Right()
        => AssertVec(Tag, ShotOffset(ClientDecides(), 0));

    [Fact]
    public void ShootFromEye_Fires_From_The_Eye_Line_Whatever_The_Alignment()
    {
        var cv = ClientDecides();
        cv["g_shootfromeye"] = "1";
        AssertVec(new Vector3(20f, 0f, 0f), ShotOffset(cv, 3));
        AssertVec(new Vector3(20f, 0f, 0f), ShotOffset(cv, 4));
    }

    [Fact]
    public void ShootFromCenter_Centres_The_Gun_Whatever_The_Alignment()
    {
        var cv = ClientDecides();
        cv["g_shootfromcenter"] = "1";
        AssertVec(new Vector3(20f, 0f, -6f), ShotOffset(cv, 4));
    }

    [Fact]
    public void ShootFromFixedOrigin_Forces_Side_And_Height()
    {
        var cv = ClientDecides();
        cv["g_shootfromfixedorigin"] = "0 3 -9";
        AssertVec(new Vector3(20f, 3f, -9f), ShotOffset(cv, 4));
        cv["g_shootfromfixedorigin"] = "15 3 -9";
        AssertVec(new Vector3(15f, 3f, -9f), ShotOffset(cv, 3));
    }

    [Fact]
    public void ShotOrgAdjustFromClient_Matches_Base()
    {
        AssertVec(Tag, WeaponFiring.ShotOrgAdjustFromClient(Tag, 3));
        AssertVec(new Vector3(20f, 6f, -4f), WeaponFiring.ShotOrgAdjustFromClient(Tag, 4));
        AssertVec(new Vector3(20f, 0f, -6f), WeaponFiring.ShotOrgAdjustFromClient(Tag, 1));
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    private sealed class Services : IEngineServices
    {
        public Services(Dictionary<string, string> cvars) => Cvars = new DictCvars(cvars);
        public ITraceService Trace { get; } = new FreeTrace();
        public IGameClock Clock { get; } = new ZeroClock();
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
    }

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v;
        public DictCvars(Dictionary<string, string> v) => _v = v;
        public float GetFloat(string name)
            => _v.TryGetValue(name, out string? s) && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out string? s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
    }

    private sealed class FreeTrace : ITraceService
    {
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
            => TraceResult.Miss(end);
        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }

    private sealed class NullEntities : IEntityService
    {
        public Entity Spawn() => new();
        public void Remove(Entity e) { }
        public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
        public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
        public void SetModel(Entity e, string model) { }
        public IEnumerable<Entity> FindByClass(string className) => Array.Empty<Entity>();
        public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => Array.Empty<Entity>();
    }

    private sealed class NullSound : ISoundService
    {
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
        public void Stop(Entity e, SoundChannel channel) { }
    }

    private sealed class NullModels : IModelService
    {
        public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
        { origin = forward = right = up = Vector3.Zero; return false; }
        public void SetAttachment(Entity e, Entity parent, string tagName) { }
    }
}
