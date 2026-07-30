// Port of qcsrc/common/mutators/mutator/nades/nade/veil.qc (nade_veil_boom + nade_veil_touch).
//
// The veil nade spawns an orb (nades_spawn_orb) that makes teammates inside it invisible to players outside
// it: a SAME_TEAM toucher's alpha is saved (nade_veil_prevalpha) and set to -1 (fully hidden), and every
// real client inside is flagged with nade_veil_time. The restore on lapse (nade_veil_Apply) is already
// driven by part-A's NadesMutator PlayerPreThink — this file supplies the orb touch only.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The veil nade detonation — port of <c>nade_veil_boom</c>.</summary>
public sealed class NadeVeilBoom : INadeBoom
{
    public string NadeNetName => "veil";

    /// <summary>QC <c>nade_veil_boom</c>: spawn the veil orb and install <see cref="VeilTouch"/>.</summary>
    public void Boom(Entity nade)
    {
        Entity orb = NadeBoom.SpawnOrb(nade,
            NadeProjectile.Cvar("g_nades_veil_time", 8f),
            NadeProjectile.Cvar("g_nades_veil_radius", 250f));
        orb.Touch = VeilTouch;
    }

    /// <summary>
    /// Port of <c>nade_veil_touch(entity this, entity toucher)</c> (veil.qc:6): a real-client teammate is
    /// hidden (its alpha saved to <see cref="Entity.NadeVeilPrevAlpha"/>, then set to -1) the first time it
    /// enters; enemies are merely "tinted" (a render concern, omitted). Every affected real client gets its
    /// <see cref="Entity.NadeVeilTime"/> bumped so the lapse-restore (NadesMutator.ApplyVeilLapse) fires.
    /// </summary>
    private static void VeilTouch(Entity orb, Entity toucher)
    {
        if (Api.Services is null) return;
        if (!NadeOrbHelper.IsRealClient(toucher))
            return;

        float now = Api.Clock.Time;
        Entity? owner = orb.RealOwner;

        // QC: tint_alpha (0.45 friend / 0.75 foe) is the render colormod — omitted headless. The gameplay
        // effect is the alpha-hide for teammates, which actually conceals the player from clients.
        if (NadeOrbHelper.SameTeam(toucher, owner))
        {
            if (toucher.NadeVeilTime == 0f)
            {
                toucher.NadeVeilPrevAlpha = toucher.Alpha;
                toucher.Alpha = -1f;
            }
        }

        toucher.NadeVeilTime = now + 0.1f;
    }
}
