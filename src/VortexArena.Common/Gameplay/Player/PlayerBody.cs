using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

namespace VortexArena.Common.Gameplay;

/// <summary>A separate edict that preserves a player's appearance and physical state.</summary>
public static class PlayerBody
{
    public const string ClassName = "body";
    private const float CorpseScale = 0.235f;

    /// <summary>Resize at the end of the death tick, after same-tick damage has used the live hull.</summary>
    public static void FinishDeathFrame(Player player)
    {
        if (!player.CorpseHullPending)
            return;
        player.CorpseHullPending = false;
        if (!player.IsCorpse || player.IsObserver)
            return;

        // world.qc: round the scaled sum of the vertical extents up to one decimal place.
        Vector3 maxs = player.Maxs;
        float height = MathF.Ceiling((player.Mins.Z + maxs.Z) * CorpseScale * 10f) / 10f;
        maxs.Z = MathF.Max(height, player.Mins.Z + 1f);
        Api.Entities.SetSize(player, player.Mins, maxs);
    }

    public static int GetCloneOrdinal(Entity body) => body.BodyCloneOrdinal;

    public static Entity? Copy(Player player, Vector3 velocity)
    {
        if ((player.Effects & EffectFlags.NoDraw) != 0)
            return null;

        var body = Api.Entities.Spawn();
        body.ClassName = ClassName;
        body.Enemy = player;
        body.BodyCloneOrdinal = player.Lip;
        body.Colors = player.ClientColors;
        body.Team = player.Team;
        body.Model = player.Model;
        body.ModelIndex = player.ModelIndex;
        body.Skin = player.Skin;
        body.Species = player.Species;
        body.Frame = player.Frame;
        body.Effects = player.Effects;
        body.Alpha = player.Alpha;
        body.Angles = player.Angles;
        body.AVelocity = player.AVelocity;
        body.MoveType = player.MoveType;
        body.Solid = player.Solid;
        body.TakeDamage = player.TakeDamage;
        // CopyBody keeps the source's live-player or corpse damage callback.
        body.IsCorpse = player.IsCorpse || player.DeadState != DeadFlag.No;
        body.DpHitContentsMask = player.DpHitContentsMask;
        body.DamagedByContents = player.DamagedByContents;
        body.ContentsTransition = player.ContentsTransition;
        body.DamageForceScale = player.DamageForceScale;
        body.PainFinished = player.PainFinished;
        body.Velocity = velocity;
        body.OldVelocity = velocity;
        body.SetResourceExplicit(ResourceType.Health, player.GetResource(ResourceType.Health));
        body.SetResourceExplicit(ResourceType.Armor, player.GetResource(ResourceType.Armor));
        body.Reset = static self => Api.Entities.Remove(self);
        Api.Entities.SetSize(body, player.Mins, player.Maxs);
        Api.Entities.SetOrigin(body, player.Origin);
        return body;
    }
}
