// Port of qcsrc/common/mutators/mutator/nades/nade/ice.qc (nade_ice_boom + nade_ice_think + nade_ice_freeze).
//
// The ice nade spawns a freeze-FIELD fountain entity that, every 0.1s for g_nades_ice_freeze_time seconds,
// freezes every live player/monster in g_nades_ice_radius (subject to the teamcheck) by applying the
// STATUSEFFECT_Frozen status effect. When the field expires it optionally explodes again as a normal nade
// (g_nades_ice_explode). Modelled on the Fireball fountain pattern (a Think loop on a spawned entity).
//
// Render-only pieces omitted: the muzzleflash/icefield particle gate, the timer model on the explode variant.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The ice nade detonation — port of <c>nade_ice_boom</c>.</summary>
public sealed class NadeIceBoom : INadeBoom
{
    public string NadeNetName => "ice";

    /// <summary>
    /// Port of <c>nade_ice_boom(entity this)</c> (ice.qc:74): spawn a MOVETYPE_TOSS freeze-field fountain at
    /// the nade origin, carrying the thrower's team/owner, that runs <see cref="IceThink"/> until
    /// <c>time + g_nades_ice_freeze_time</c>.
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        Entity fountain = Api.Entities.Spawn();
        fountain.ClassName = "nade_ice_fountain";
        fountain.Owner = nade.RealOwner;            // QC realowner (RealOwner aliases Owner)
        fountain.Team = nade.Team;
        Api.Entities.SetOrigin(fountain, nade.Origin);
        Api.Entities.SetSize(fountain, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        fountain.MoveType = MoveType.Toss;
        fountain.Flags = EntFlags.Item;             // QC FL_PROJECTILE
        fountain.Angles = nade.Angles;

        float freezeTime = NadeProjectile.Cvar("g_nades_ice_freeze_time", 3f);
        fountain.NadeOrbExpire = now + freezeTime;   // QC .ltime
        fountain.NadeSpecialTime = now + 0.3f;
        fountain.Think = IceThink;
        fountain.NextThink = now;
    }

    /// <summary>
    /// Port of <c>nade_ice_think(entity this)</c> (ice.qc:12): on each 0.1s tick, freeze every eligible live
    /// player/monster in g_nades_ice_radius (per the teamcheck). At <c>ltime</c> the field expires, optionally
    /// doing a final normal-nade explosion (g_nades_ice_explode) first.
    /// </summary>
    private static void IceThink(Entity self)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        // QC ice.qc:14 — round_handler_IsActive() && !round_handler_IsRoundStarted(): a freeze field spawned
        // while a round hasn't started yet is silently deleted (NO final explode), distinct from the ltime expiry.
        if (RoundHandler.RoundGateBlocks())
        {
            Api.Entities.Remove(self);
            return;
        }

        if (now >= self.NadeOrbExpire)
        {
            if (NadeProjectile.Cvar("g_nades_ice_explode", 0f) != 0f)
            {
                // QC: sound + nade_normal_boom(this). Route the explode through the shared blast helper with
                // the ice deathtype so the obituary reads "ice".
                Api.Sound.Play(self, SoundChannel.Auto, "weapons/rocket_impact.wav");
                self.Enemy = null;
                NadeBlast.RadiusDamage(self,
                    NadeProjectile.Cvar("g_nades_nade_damage", 225f),
                    NadeProjectile.Cvar("g_nades_nade_edgedamage", 90f),
                    NadeProjectile.Cvar("g_nades_nade_radius", 300f),
                    NadeProjectile.Cvar("g_nades_nade_force", 650f),
                    NadeDeathTypes.Ice);
            }
            Api.Entities.Remove(self);
            return;
        }

        self.NextThink = now + 0.1f;

        float radius = NadeProjectile.Cvar("g_nades_ice_radius", 300f);
        float currentFreezeTime = self.NadeOrbExpire - now - 0.1f;
        if (currentFreezeTime <= 0f)
            return;

        int teamcheck = (int)NadeProjectile.Cvar("g_nades_ice_teamcheck", 2f);
        Entity? owner = self.RealOwner;
        var frozen = StatusEffectsCatalog.Frozen;
        if (frozen is null) return;

        foreach (Entity it in Api.Entities.FindInRadius(self.Origin, radius).ToList())
        {
            if (ReferenceEquals(it, self) || it.TakeDamage == DamageMode.No) continue;
            bool isCreature = (it.Flags & (EntFlags.Client | EntFlags.Monster)) != 0;
            if (!isCreature || it.DeadState != DeadFlag.No) continue;
            if (it.GetResource(ResourceType.Health) <= 0f) continue;
            // QC ice.qc:59: skip a just-revived player for 1.5s so a freshly thawed player gets a grace window
            // before they can be re-frozen. (!it.revival_time || ((time - it.revival_time) >= 1.5))
            if (it.RevivalTime != 0f && (now - it.RevivalTime) < 1.5f) continue;
            if (StatusEffectsCatalog.Has(it, frozen)) continue;

            // QC teamcheck: 0 = everyone, 2 = skip teammates (and self), 1 = skip only self.
            if (teamcheck != 0)
            {
                if (teamcheck != 1 && NadeOrbHelper.SameTeam(it, owner)) continue; // case 2: spare teammates
                if (ReferenceEquals(it, owner)) continue;                          // case 1+2: spare the thrower
            }

            // QC nade_ice_freeze: StatusEffects_apply(STATUSEFFECT_Frozen, target, time + current_freeze_time, 0).
            StatusEffectsCatalog.Apply(it, frozen, currentFreezeTime, strength: 0f, source: owner);
        }
    }
}
