// Port of qcsrc/common/mutators/mutator/nades/nade/ammo.qc (nade_ammo_boom + nade_ammo_touch).
//
// The ammo nade spawns an orb (nades_spawn_orb) whose touch keeps a teammate's weapon magazines full (and
// drains a foe's), and gives ammo resources to friends / drains them from foes. The orb is created by the
// shared NadeBoom.SpawnOrb helper (part A).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The ammo nade detonation — port of <c>nade_ammo_boom</c>.</summary>
public sealed class NadeAmmoBoom : INadeBoom
{
    public string NadeNetName => "ammo";

    /// <summary>QC <c>nade_ammo_boom</c>: spawn the ammo orb and install <see cref="AmmoTouch"/>.</summary>
    public void Boom(Entity nade)
    {
        Entity orb = NadeBoom.SpawnOrb(nade,
            NadeProjectile.Cvar("g_nades_ammo_time", 4f),
            NadeProjectile.Cvar("g_nades_ammo_radius", 300f));
        orb.Touch = AmmoTouch;
    }

    /// <summary>
    /// Port of <c>nade_ammo_touch(entity this, entity toucher)</c> (ammo.qc:4): refill (friend) or slowly
    /// empty (foe) each reloadable weapon's magazine, then give (friend) or drain (foe) the four ammo
    /// resources. <c>frametime * 0.5</c> matches QC. The per-weapon <c>weapon_load[]</c> persistent store is
    /// collapsed onto each slot's live <see cref="WeaponSlotState.ClipLoad"/> in this port.
    /// </summary>
    private static void AmmoTouch(Entity orb, Entity toucher)
    {
        if (Api.Services is null) return;
        bool isPlayer = (toucher.Flags & EntFlags.Client) != 0;
        bool isMonster = (toucher.Flags & EntFlags.Monster) != 0;
        if ((!isPlayer && !isMonster) || NadeOrbHelper.IsDeadOrFrozen(toucher))
            return;

        Entity? owner = orb.RealOwner;
        float ft = Api.Clock.FrameTime;
        bool friend = NadeOrbHelper.SameTeam(toucher, owner);

        // QC clip refill/empty loop over weaponentities[]. Friends: magazines stay full; foes: bleed clip_load.
        float clipEmptyRate = NadeProjectile.Cvar("g_nades_ammo_clip_empty_rate", 0f);
        toucher.ForEachWeaponSlot(s =>
        {
            if (s.ClipSize <= 0) return;
            int newLoad = friend
                ? s.ClipSize
                : (int)MathF.Max(0f, s.ClipLoad - s.ClipSize * (clipEmptyRate * ft * 0.5f));
            s.ClipLoad = newLoad;
        });

        float ammoFactor = NadeProjectile.Cvar("g_nades_ammo_rate", 30f) * ft * 0.5f;
        if (!ReferenceEquals(toucher, owner))
            ammoFactor *= NadeOrbHelper.SameTeamOrb(toucher, orb)
                ? NadeProjectile.Cvar("g_nades_ammo_friend", 1f)
                : NadeProjectile.Cvar("g_nades_ammo_foe", -2f); // keys on unset, so -2 survives

        if (ammoFactor > 0f)
        {
            GiveAmmo(toucher, ResourceType.Shells, ammoFactor, NadeProjectile.Cvar("g_pickup_shells_max", 60f));
            GiveAmmo(toucher, ResourceType.Bullets, ammoFactor, NadeProjectile.Cvar("g_pickup_nails_max", 320f));
            GiveAmmo(toucher, ResourceType.Rockets, ammoFactor, NadeProjectile.Cvar("g_pickup_rockets_max", 160f));
            GiveAmmo(toucher, ResourceType.Cells, ammoFactor, NadeProjectile.Cvar("g_pickup_cells_max", 180f));
            // QC: if (gave && this.nade_show_particles) Send_Effect(EFFECT_AMMO_REGEN, ...) — render-only.
        }
        else if (ammoFactor < 0f) // foes drop ammo points
        {
            DropAmmo(toucher, ResourceType.Shells, ammoFactor);
            DropAmmo(toucher, ResourceType.Bullets, ammoFactor);
            DropAmmo(toucher, ResourceType.Rockets, ammoFactor);
            DropAmmo(toucher, ResourceType.Cells, ammoFactor);
        }
    }

    private static void GiveAmmo(Entity e, ResourceType res, float amount, float max)
    {
        if (e.GetResource(res) < max)
            e.GiveResourceWithLimit(res, amount, max);
    }

    private static void DropAmmo(Entity e, ResourceType res, float ammoFactor)
    {
        float cur = e.GetResource(res);
        if (cur > 0f)
            e.SetResource(res, MathF.Max(0f, cur + ammoFactor)); // ammoFactor is negative here
    }
}
