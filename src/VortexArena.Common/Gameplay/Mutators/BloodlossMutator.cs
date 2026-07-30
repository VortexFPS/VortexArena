using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Bloodloss mutator — port of common/mutators/mutator/bloodloss/bloodloss.qc. While a player's health
/// is at or below a threshold, they bleed out: they take periodic rot damage, are forced to crouch, and
/// can't jump. Enabled by the <c>g_bloodloss</c> cvar (whose value is also the health threshold).
///
/// Ported: the periodic 1-hp rot tick (PlayerPreThink) with the randomized timer and the game-stopped
/// guard, the vehicle eject on each rot tick, the forced crouch (PlayerCanCrouch), and the jump block
/// (PlayerJump).
/// </summary>
[Mutator]
public sealed class BloodlossMutator : MutatorBase
{
    /// <summary>
    /// QC autocvar_g_bloodloss — the health threshold (and the enable cvar) for bleeding.
    /// Read live on every hook call (Base reads autocvar_g_bloodloss directly in each
    /// HOOKFUNCTION), so a mid-match g_bloodloss change takes effect immediately.
    /// </summary>
    private float Threshold => Api.Services is not null ? Api.Cvars.GetFloat("g_bloodloss") : 0f;

    public BloodlossMutator() => NetName = "bloodloss";

    // QC SVQC: REGISTER_MUTATOR(bloodloss, autocvar_g_bloodloss);
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_bloodloss") != 0f;

    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.PlayerCanCrouchArgs>? _onCanCrouch;
    private HookHandler<MutatorHooks.PlayerJumpArgs>? _onJump;

    public override void Hook()
    {
        _onPreThink ??= OnPlayerPreThink;
        _onCanCrouch ??= OnPlayerCanCrouch;
        _onJump ??= OnPlayerJump;

        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.PlayerCanCrouch.Add(_onCanCrouch);
        MutatorHooks.PlayerJump.Add(_onJump);
    }

    public override void Unhook()
    {
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onCanCrouch is not null) MutatorHooks.PlayerCanCrouch.Remove(_onCanCrouch);
        if (_onJump is not null) MutatorHooks.PlayerJump.Remove(_onJump);
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(bloodloss, PlayerPreThink) — bleed out one hp at a time.
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;

        // QC: during intermission the engine reports strange health values — don't cause damage then.
        if (VehicleCommon.GameStopped) return false;

        float now = Api.Clock.Time;

        if (IsPlayer(player)
            && player.GetResource(ResourceType.Health) <= Threshold
            && player.DeadState == DeadFlag.No
            && now >= player.BloodlossTimer)
        {
            // QC: if the player is in a vehicle, eject them (vehicles_exit VHEF_RELEASE) each rot tick.
            if (player.Vehicle is not null)
                VehicleCommon.ExitVehicle(player.Vehicle, player, VehicleExitFlag.Release);

            // QC: player.event_damage(player, player, player, 1, DEATH_ROT.m_id, DMG_NOWEP, origin, '0 0 0');
            // "rot" is the QC DEATH_ROT special deathtype (bypasses weapon factors).
            Combat.Damage(player, player, player, 1f, "rot", player.Origin, Vector3.Zero);

            // QC: bloodloss_timer = time + 0.5 + random() * 0.5;
            player.BloodlossTimer = now + 0.5f + XonoticGodot.Common.Math.Prandom.Float() * 0.5f;
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(bloodloss, PlayerCanCrouch) — force crouch while bleeding.
    private bool OnPlayerCanCrouch(ref MutatorHooks.PlayerCanCrouchArgs args)
    {
        if (args.Player.GetResource(ResourceType.Health) <= Threshold)
            args.DoCrouch = true;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(bloodloss, PlayerJump) — can't jump while bleeding (return true = forbid).
    private bool OnPlayerJump(ref MutatorHooks.PlayerJumpArgs args)
    {
        if (args.Player.GetResource(ResourceType.Health) <= Threshold)
            return true; // QC: return true (block the jump)
        return false;
    }

    // MUTATOR_HOOKFUNCTION(bloodloss, BuildMutatorsString) — bloodloss.qc:46-49
    public override string BuildMutatorsString(string s) => s + ":bloodloss";

    // MUTATOR_HOOKFUNCTION(bloodloss, BuildMutatorsPrettyString) — bloodloss.qc:51-54
    public override string BuildMutatorsPrettyString(string s) => s + ", Blood loss";
}
