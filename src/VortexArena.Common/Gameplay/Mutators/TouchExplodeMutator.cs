using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Touch Explode mutator — port of common/mutators/mutator/touchexplode/sv_touchexplode.qc. When two
/// living players' bounding boxes overlap, a small explosion goes off between them (with a short per-pair
/// cooldown). Enabled by the <c>g_touchexplode</c> cvar.
///
/// Ported: the per-frame pairwise overlap scan (PlayerPreThink) with the game-stopped / frozen /
/// independent-player guards, the impact sound, and the radius blast at the midpoint via the shared
/// <see cref="WeaponSplash.RadiusDamage"/> helper. (The DEATH_TOUCHEXPLODE death id and the networked
/// explosion particle belong to the deathtype-registry / effects phases; the blast itself is faithful.)
/// </summary>
[Mutator]
public sealed class TouchExplodeMutator : MutatorBase
{
    // Defaults mirror mutators.cfg (radius 50 / damage 20 / edgedamage 0 / force 300) so a headless run
    // without the cfg still matches Base; Hook() reads the live cvars unconditionally like QC autocvar_*.

    /// <summary>QC autocvar_g_touchexplode_radius.</summary>
    public float Radius = 50f;

    /// <summary>QC autocvar_g_touchexplode_damage.</summary>
    public float DamageAmount = 20f;

    /// <summary>QC autocvar_g_touchexplode_edgedamage.</summary>
    public float EdgeDamage = 0f;

    /// <summary>QC autocvar_g_touchexplode_force.</summary>
    public float Force = 300f;

    public TouchExplodeMutator() => NetName = "touchexplode";

    // QC: expr_evaluate(autocvar_g_touchexplode).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_touchexplode") != 0f;

    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onPreThink ??= OnPlayerPreThink;
        MutatorHooks.PlayerPreThink.Add(_onPreThink);

        // QC reads autocvar_g_touchexplode_* directly each blast. These cvars are registered (mutators.cfg
        // 163-166) so we read them unconditionally — a non-zero guard would wrongly pin edgedamage at its
        // default when the cfg legitimately sets it to 0.
        if (Api.Services is not null)
        {
            Radius = Api.Cvars.GetFloat("g_touchexplode_radius");
            DamageAmount = Api.Cvars.GetFloat("g_touchexplode_damage");
            EdgeDamage = Api.Cvars.GetFloat("g_touchexplode_edgedamage");
            Force = Api.Cvars.GetFloat("g_touchexplode_force");
        }
    }

    public override void Unhook()
    {
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
    }

    private static bool IsLivePlayer(Entity e) =>
        (e.Flags & EntFlags.Client) != 0 && e.DeadState == DeadFlag.No && !e.IsFreed;

    private static bool IsFrozen(Entity e)
    {
        var frozen = StatusEffectsCatalog.Frozen;
        return frozen is not null && StatusEffectsCatalog.Has(e, frozen);
    }

    // MUTATOR_HOOKFUNCTION(touchexplode, PlayerPreThink)
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;

        // QC: !game_stopped && IS_PLAYER && !IS_DEAD && !STAT(FROZEN) && !IS_INDEPENDENT_PLAYER.
        if (VehicleCommon.GameStopped) return false;
        float now = Api.Clock.Time;

        if (now <= player.TouchExplodeTime || !IsLivePlayer(player)
            || IsFrozen(player) || player.IsIndependentPlayer)
            return false;

        foreach (Entity other in Api.Entities.FindByClass("player"))
        {
            if (ReferenceEquals(other, player) || !IsLivePlayer(other))
                continue;
            if (now <= other.TouchExplodeTime || IsFrozen(other) || other.IsIndependentPlayer)
                continue;
            if (!BoxesOverlap(player, other))
                continue;

            PlayerTouchExplode(player, other);
            player.TouchExplodeTime = other.TouchExplodeTime = now + 0.2f;
        }
        return false;
    }

    // void PlayerTouchExplode(entity p1, entity p2)
    private void PlayerTouchExplode(Entity p1, Entity p2)
    {
        Vector3 org = (p1.Origin + p2.Origin) * 0.5f;
        org.Z += (p1.Mins.Z + p2.Mins.Z) * 0.5f;

        // QC: sound(p1, CH_TRIGGER, SND_TOUCHEXPLODE) + Send_Effect(EFFECT_EXPLOSION_SMALL, org, '0 0 0', 1).
        // SND_TOUCHEXPLODE is the grenade-impact sample; CH_TRIGGER == SoundChannel.TriggerAuto (-3).
        Api.Sound.Play(p1, SoundChannel.TriggerAuto, "weapons/grenade_impact.wav");
        EffectEmitter.Emit("EXPLOSION_SMALL", org);

        // QC spawns a temp inflictor at the midpoint and RadiusDamage's around it, tagging the blast
        // DEATH_TOUCHEXPLODE (DMG_NOWEP, no attacker credit). The deathTag DeathTypes.TouchExplode resolves
        // to DEATH_SELF_/DEATH_MURDER_TOUCHEXPLODE ("died in an accident") via DeathMessages.SelectSpecial.
        Entity e = Api.Entities.Spawn();
        e.ClassName = "touchexplode";
        Api.Entities.SetOrigin(e, org);
        WeaponSplash.RadiusDamage(e, org, DamageAmount, EdgeDamage, Radius, attacker: null,
            deathType: 0, force: Force, deathTag: DeathTypes.TouchExplode);
        Api.Entities.Remove(e);
    }

    // NOTE: Base touchexplode registers NO BuildMutatorsString / BuildMutatorsPrettyString hook
    // (verified: it is absent from the list of mutators that do — vampire, cloaked, hook, etc.). It is
    // advertised ONLY via the global active_mutators() X-table (common/util.qc:315,
    // X("Touch explode", _("Touch explode"), MUT_TOUCHEXPLODE, cvar("g_touchexplode") > 0)), a separate
    // bitfield-string builder that the port has not ported yet. So we deliberately do NOT override the
    // BuildMutatorsString chain here — doing so would inject touchexplode into the per-mutator server-info
    // token chain where Base never places it. The active_mutators() X-table is a systemic, shared-subsystem
    // gap (no active_mutators() builder exists in the port) tracked by the registry, not this mutator.

    /// <summary>QC boxesoverlap(p1.absmin, p1.absmax, p2.absmin, p2.absmax).</summary>
    private static bool BoxesOverlap(Entity a, Entity b)
        => a.AbsMin.X <= b.AbsMax.X && a.AbsMax.X >= b.AbsMin.X
        && a.AbsMin.Y <= b.AbsMax.Y && a.AbsMax.Y >= b.AbsMin.Y
        && a.AbsMin.Z <= b.AbsMax.Z && a.AbsMax.Z >= b.AbsMin.Z;
}
