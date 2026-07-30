using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Midair mutator — port of common/mutators/mutator/midair/sv_midair.qc. Players can only be hurt
/// while airborne: when on the ground they gain a brief damage shield, and airborne hits are scaled by a
/// damage multiplier. Enabled by the <c>g_midair</c> cvar.
///
/// Ported: the on-ground shield window (PlayerPowerups, gated on the match being live) with the
/// fullbright/additive ground glow, the airborne damage/force scaling (Damage_Calculate), and the
/// PlayerSpawn bot tuning that zeroes a bot's move-skill so it stops bunnyhopping (staying huntable in
/// the air). All three g_midair_* tunables are read LIVE each frame (matching QC autocvar reads), so a
/// mid-match cvar change applies immediately and a configured 0 means 0 (no shield / zero airborne damage).
/// </summary>
[Mutator]
public sealed class MidairMutator : MutatorBase
{
    /// <summary>QC autocvar_g_midair_shieldtime — seconds of post-landing damage immunity. Base default 0.3.</summary>
    private float ShieldTime =>
        Api.Services is not null ? Api.Cvars.GetFloat("g_midair_shieldtime") : 0.3f;

    /// <summary>QC autocvar_g_midair_damagemultiplier — multiplier for airborne hits (Base default 1; 0 = zero damage).</summary>
    private float DamageMultiplier =>
        Api.Services is not null ? Api.Cvars.GetFloat("g_midair_damagemultiplier") : 1f;

    /// <summary>QC autocvar_g_midair_damageforcescale — optional knockback multiplier (Base default 1.5; 0 = leave force alone).</summary>
    private float DamageForceScale =>
        Api.Services is not null ? Api.Cvars.GetFloat("g_midair_damageforcescale") : 0f;

    public MidairMutator() => NetName = "midair";

    // QC: expr_evaluate(autocvar_g_midair).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_midair") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerPowerupsArgs>? _onPlayerPowerups;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerPowerups ??= OnPlayerPowerups;
        _onPlayerSpawn ??= OnPlayerSpawn;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerPowerups.Add(_onPlayerPowerups);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerPowerups is not null) MutatorHooks.PlayerPowerups.Remove(_onPlayerPowerups);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(midair, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (IsPlayer(args.Attacker) && IsPlayer(args.Target))
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            if (now < args.Target.MidairShieldTime)
                args.Damage = 0f;            // grounded recently → shielded
            else
                args.Damage *= DamageMultiplier;

            // QC: if(autocvar_g_midair_damageforcescale) M_ARGV(6) *= ... — truthiness guard, 0 leaves force alone.
            float forceScale = DamageForceScale;
            if (forceScale != 0f)
                args.Force *= forceScale;
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(midair, PlayerPowerups) — refresh the shield while on the ground.
    private bool OnPlayerPowerups(ref MutatorHooks.PlayerPowerupsArgs args)
    {
        Entity player = args.Player;
        // QC gate: if (time >= game_starttime) — the on-ground shield only applies once the match clock has
        // started (so warmup landings don't bank immunity). GameStopped is the available "not live" signal.
        if (!VehicleCommon.GameStopped && player.OnGround)
        {
            // QC: player.effects |= (EF_ADDITIVE | EF_FULLBRIGHT);
            player.Effects |= EffectFlags.Additive | EffectFlags.FullBright;
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            float target = now + ShieldTime;   // 0 shieldtime → deadline == now → no window (matches Base)
            if (target > player.MidairShieldTime)
                player.MidairShieldTime = target;
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(midair, PlayerSpawn) — QC: if(IS_BOT_CLIENT(player)) player.bot_moveskill = 0.
    // Disabling the bot's MOVE-skill keeps skill+moveskill below bot_ai_bunnyhop_skilloffset, so the bot
    // stops bunnyhopping and stays airborne (huntable) instead of hugging the ground permanently shielded.
    // Faithful to Base: we zero BotMoveSkill (the bunnyhop-gate term) and leave BotSkill intact, so the bot's
    // aim/reaction/dodge/role tuning is unaffected — and a high-skill bot whose configured moveskill still keeps
    // skill+moveskill >= offset would keep bunnyhopping, exactly as Base's `skill + bot_moveskill` gate allows.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (args.Player is Player { IsBot: true } bot)
            bot.BotMoveSkill = 0f;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(midair, BuildMutatorsString) — sv_midair.qc:48-51
    public override string BuildMutatorsString(string s) => s + ":midair";

    // MUTATOR_HOOKFUNCTION(midair, BuildMutatorsPrettyString) — sv_midair.qc:53-56
    public override string BuildMutatorsPrettyString(string s) => s + ", Midair";
}
