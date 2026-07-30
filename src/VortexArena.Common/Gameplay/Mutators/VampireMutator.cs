using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vampire mutator — port of common/mutators/mutator/vampire/sv_vampire.qc. When enabled, an attacker
/// is healed for the damage they deal to other players. This is the sample that demonstrates the C# hook
/// system replacing QC's MUTATOR_HOOKFUNCTION + M_ARGV slots: it subscribes a <see cref="HookHandler{TArgs}"/>
/// to <see cref="GameHooks.PlayerDamageSplitHealthArmor"/> on enable and removes it on disable, reading and
/// (potentially) writing the <c>ref</c> args struct instead of global argv slots.
/// </summary>
[Mutator]
public sealed class VampireMutator : MutatorBase
{
    public VampireMutator() => NetName = "vampire";

    /// <summary>
    /// QC: expr_evaluate(autocvar_g_vampire) &amp;&amp; !MUTATOR_IS_ENABLED(mutator_instagib).
    /// The instagib exclusion is expressed against g_instagib (same pattern as NewToysMutator) — without it
    /// a direct `g_vampire 1; g_instagib 1` would activate both; the menu Dependent.Bind only greys the box.
    /// </summary>
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_vampire") != 0f
        && Api.Cvars.GetFloat("g_instagib") == 0f;

    // The handler instance must be stored so Unhook removes the same delegate it added.
    private HookHandler<GameHooks.PlayerDamageArgs>? _onPlayerDamage;

    public override void Hook()
    {
        _onPlayerDamage ??= OnPlayerDamage;
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onPlayerDamage);
    }

    public override void Unhook()
    {
        if (_onPlayerDamage is not null)
            GameHooks.PlayerDamageSplitHealthArmor.Remove(_onPlayerDamage);
    }

    // MUTATOR_HOOKFUNCTION(vampire, PlayerDamage_SplitHealthArmor) — sv_vampire.qc
    private bool OnPlayerDamage(ref GameHooks.PlayerDamageArgs args)
    {
        Entity attacker = args.Attacker;
        Entity target = args.Target;

        // QC reads autocvar_g_vampire_factor / autocvar_g_vampire_use_total_damage LIVE on every damage event,
        // so a mid-match cvar change takes effect; g_vampire_factor 0 means heal-for-nothing (not swallowed).
        float factor = Api.Services is not null ? Api.Cvars.GetFloat("g_vampire_factor") : 1.0f;
        bool useTotalDamage = Api.Services is not null && Api.Cvars.GetFloat("g_vampire_use_total_damage") != 0f;

        // QC: health_take = bound(0, M_ARGV(4), GetResource(target, RES_HEALTH));
        float healthTake = QMath.Bound(0f, args.DamageTake, target.GetResource(ResourceType.Health));
        float armorTake = QMath.Bound(0f, args.DamageSave, target.GetResource(ResourceType.Armor));
        float damageTake = useTotalDamage ? healthTake + armorTake : healthTake;

        // QC guards: target not spawn-shielded, attacker != target, attacker is a player, target alive/unfrozen.
        // Spawn-shield: QC StatusEffects_active(STATUSEFFECT_SpawnShield, target). The port keeps the expiry on
        // the entity (Entity.SpawnShieldExpire, mirrored from DamageSystem.HasSpawnShield) rather than the catalog.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        bool targetSpawnShielded = target.SpawnShieldExpire > now;
        bool attackerIsPlayer = (attacker.Flags & EntFlags.Client) != 0;
        bool targetAlive = target.DeadState == DeadFlag.No;
        bool targetFrozen = StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(target, fz);

        if (!targetSpawnShielded && target != attacker && attackerIsPlayer && targetAlive && !targetFrozen)
        {
            // QC: GiveResource(attacker, RES_HEALTH, factor * damageTake);
            // Heal caps at the normal health limit (g_balance_health_limit, default 200) via GiveResource;
            // current Base GetResourceLimit has no vampire override, so this matches (the old "above 200" note
            // in both Base MENUQC and here is stale documentation).
            attacker.GiveResource(ResourceType.Health, factor * damageTake);
        }

        return false; // not "exclusive" — let other handlers run (QC CBC_ORDER_ANY).
    }

    // MUTATOR_HOOKFUNCTION(vampire, BuildMutatorsString) — sv_vampire.qc:25
    public override string BuildMutatorsString(string s) => s + ":Vampire";

    // MUTATOR_HOOKFUNCTION(vampire, BuildMutatorsPrettyString) — sv_vampire.qc:30
    public override string BuildMutatorsPrettyString(string s) => s + ", Vampire";
}
