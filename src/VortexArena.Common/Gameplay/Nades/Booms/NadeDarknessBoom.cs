// Port of qcsrc/common/mutators/mutator/nades/nade/darkness.qc (nade_darkness_boom + nade_darkness_think).
//
// The darkness nade spawns a dark-FIELD fountain that, every 0.1s for g_nades_darkness_time seconds, blinds
// every live real client in g_nades_darkness_radius (per the teamcheck) by bumping STAT(NADE_DARKNESS_TIME).
// The actual blind overlay is a CSQC HUD effect (darkness.qc HUD_DarkBlinking) — out of scope for the
// headless sim; the server-side gameplay (setting the darkness deadline + the optional explode) is ported.
// Structurally the mirror of the ice fountain.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The darkness nade detonation — port of <c>nade_darkness_boom</c>.</summary>
public sealed class NadeDarknessBoom : INadeBoom
{
    public string NadeNetName => "darkness";

    /// <summary>
    /// Port of <c>nade_darkness_boom(entity this)</c> (darkness.qc:66): spawn a MOVETYPE_TOSS dark-field
    /// fountain running <see cref="DarknessThink"/> until <c>time + g_nades_darkness_time</c>.
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        Entity fountain = Api.Entities.Spawn();
        fountain.ClassName = "nade_darkness_fountain";
        fountain.Owner = nade.RealOwner;
        fountain.Team = nade.Team;
        Api.Entities.SetOrigin(fountain, nade.Origin);
        Api.Entities.SetSize(fountain, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        fountain.MoveType = MoveType.Toss;
        fountain.Flags = EntFlags.Item;             // QC FL_PROJECTILE
        fountain.Angles = nade.Angles;

        float darkTime = NadeProjectile.Cvar("g_nades_darkness_time", 4f);
        fountain.NadeOrbExpire = now + darkTime;     // QC .ltime
        fountain.NadeSpecialTime = now + 0.3f;
        fountain.Think = DarknessThink;
        fountain.NextThink = now;
    }

    /// <summary>
    /// Port of <c>nade_darkness_think(entity this)</c> (darkness.qc:5): each 0.1s tick, blind every eligible
    /// live real client in g_nades_darkness_radius (per the teamcheck). At <c>ltime</c> the field expires,
    /// optionally doing a final normal-nade explosion (g_nades_darkness_explode) first.
    /// </summary>
    private static void DarknessThink(Entity self)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        // QC darkness.qc:7 — round_handler_IsActive() && !round_handler_IsRoundStarted(): a dark field spawned
        // before the round starts is silently deleted (no final explode), distinct from the ltime expiry below.
        if (RoundHandler.RoundGateBlocks())
        {
            Api.Entities.Remove(self);
            return;
        }

        if (now >= self.NadeOrbExpire)
        {
            if (NadeProjectile.Cvar("g_nades_darkness_explode", 0f) != 0f)
            {
                Api.Sound.Play(self, SoundChannel.Auto, "weapons/rocket_impact.wav");
                self.Enemy = null;
                NadeBlast.RadiusDamage(self,
                    NadeProjectile.Cvar("g_nades_nade_damage", 225f),
                    NadeProjectile.Cvar("g_nades_nade_edgedamage", 90f),
                    NadeProjectile.Cvar("g_nades_nade_radius", 300f),
                    NadeProjectile.Cvar("g_nades_nade_force", 650f),
                    NadeDeathTypes.Darkness);
            }
            // QC else: Send_Effect(EFFECT_SPAWN, ...) — render-only, omitted.
            Api.Entities.Remove(self);
            return;
        }

        self.NextThink = now + 0.1f;

        float radius = NadeProjectile.Cvar("g_nades_darkness_radius", 300f);
        float currentDarkTime = self.NadeOrbExpire - now - 0.1f;
        if (currentDarkTime <= 0f)
            return;

        int teamcheck = (int)NadeProjectile.Cvar("g_nades_darkness_teamcheck", 2f);
        Entity? owner = self.RealOwner;

        foreach (Entity it in Api.Entities.FindInRadius(self.Origin, radius).ToList())
        {
            if (ReferenceEquals(it, self) || it.TakeDamage == DamageMode.No) continue;
            if (it.DeadState != DeadFlag.No) continue;
            if (it.GetResource(ResourceType.Health) <= 0f) continue;
            if (!NadeOrbHelper.IsRealClient(it)) continue;

            if (teamcheck != 0)
            {
                if (teamcheck != 1 && NadeOrbHelper.SameTeam(it, owner)) continue;
                if (ReferenceEquals(it, owner)) continue;
            }

            // QC: STAT(NADE_DARKNESS_TIME, it) = time + 0.1.
            it.NadeDarknessTime = now + 0.1f;
        }
    }
}
