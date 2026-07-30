using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Offhand Blaster mutator — port of common/mutators/mutator/offhand_blaster/sv_offhand_blaster.qc.
/// Gives every player a blaster they can fire "offhand" (without switching away from their current
/// weapon). Enabled by the <c>g_offhand_blaster</c> cvar.
///
/// Ported: on spawn the player's offhand slot is set to the blaster (PlayerSpawn), and the offhand-fire
/// path runs each frame (PlayerPreThink) — when the offhand fire button is held it fires a Blaster primary
/// shot from the player without switching weapons, gated by the blaster's refire. The input layer flags the
/// press via <see cref="Entity.OffhandFirePressed"/> (or calls <see cref="FireOffhand"/> directly from a bind).
/// </summary>
[Mutator]
public sealed class OffhandBlasterMutator : MutatorBase
{
    public OffhandBlasterMutator() => NetName = "offhand_blaster";

    // QC: expr_evaluate(autocvar_g_offhand_blaster) (string cvar defaulting to "0").
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_offhand_blaster") != 0f;

    // QC: MENUQC describe() — the per-mutator guide page text (common/mutators/mutator/offhand_blaster/offhand_blaster.qc:7-16).
    // Three paragraphs: what it does (mutator+offhand+bind), what you can do with it (laser jumping), precedence vs hook.
    public override string? GuideDescription =>
        "The Offhand blaster mutator gives all players a Blaster as their offhand weapon, used with hook. " +
        "It can be fired at any time, but the ordinary secondary fire can't be used.\n\n" +
        "Since it's given as an offhand, you can fire it at the same time as the ordinary Blaster while laser jumping to achieve even more height.\n\n" +
        "Note that it overrides the Grappling Hook mutator, since they use the same bind.";

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
    }

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
    }

    // MUTATOR_HOOKFUNCTION(offhand_blaster, BuildMutatorsString) — sv_offhand_blaster.qc:7-10:
    // append ":offhand_blaster" to the machine-readable mutators token (:gameinfo:mutators:LIST event-log
    // line / server-browser field), run via the MutatorActivation.BuildMutatorsString chain.
    public override string BuildMutatorsString(string s) => s + ":offhand_blaster";

    // MUTATOR_HOOKFUNCTION(offhand_blaster, BuildMutatorsPrettyString) — sv_offhand_blaster.qc:12-15:
    // append the human-readable ", Offhand blaster" token to the scoreboard/votescreen mutator line.
    public override string BuildMutatorsPrettyString(string s) => s + ", Offhand blaster";

    // MUTATOR_HOOKFUNCTION(offhand_blaster, PlayerSpawn) — player.offhand = OFFHAND_BLASTER;
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        args.Player.OffhandWeapon = "blaster";
        return false;
    }

    // OFFHAND_BLASTER.offhand_think: when the offhand button is held, fire the blaster independently of the
    // currently-selected weapon. Driven each frame from PlayerPreThink (the headless analogue of the offhand
    // think the offhand framework runs).
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0 || player.DeadState != DeadFlag.No) return false;
        if (player.OffhandWeapon != "blaster") return false;
        if (player.OffhandFirePressed)
            FireOffhand(player);
        return false;
    }

    /// <summary>
    /// Fire the offhand blaster once (respecting its refire gate) without switching the player's active
    /// weapon — the C# successor to W_Blaster's offhand fire mode. Exposed so a +offhand bind can call it.
    /// </summary>
    public void FireOffhand(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        if (now < player.OffhandNextThink) return;

        if (Weapons.ByName("blaster") is not Blaster blaster) return;

        // Fire a Blaster primary shot from the player; the offhand uses a dedicated slot so it never
        // disturbs the held weapon's state. QC OffhandBlaster.offhand_think calls makevectors(actor.v_angle)
        // then W_Blaster_Attack(actor, weaponentities[1]); WrThink aims from the view angles and fires on the
        // dedicated high slot, matching that.
        blaster.WrThink(player, new WeaponSlot(MutatorConstants.MaxWeaponSlots), FireMode.Primary);

        // QC: actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) * W_WeaponRateFactor(actor);
        float refire = blaster.Primary.Refire > 0f ? blaster.Primary.Refire : 0.7f;
        player.OffhandNextThink = now + refire * WeaponRateFactor(player);
    }

    /// <summary>
    /// Port of <c>W_WeaponRateFactor(entity actor)</c> (common/weapons/weapon.qh): the base
    /// <c>1/g_weaponratefactor</c> with the <c>WeaponRateFactor</c> mutator hook applied for the actor (so a
    /// Speed powerup/buff scales the offhand refire just like an in-hand shot). Mirrors
    /// WeaponFireGate.WeaponRateFactor, which this mutator can't reach (protected on the weapon hierarchy).
    /// </summary>
    private static float WeaponRateFactor(Entity actor)
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponratefactor");
        f = f > 0f ? 1f / f : 1f;
        // QC: MUTATOR_CALLHOOK(WeaponRateFactor, f, actor); f = M_ARGV(0, float);
        var rateArgs = new MutatorHooks.WeaponRateFactorArgs(f, actor);
        MutatorHooks.WeaponRateFactor.Call(ref rateArgs);
        return rateArgs.Factor;
    }
}
