// Port of common/mutators/mutator/campcheck/sv_campcheck.qc

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Camp Check mutator — port of common/mutators/mutator/campcheck/sv_campcheck.qc. Discourages camping:
/// every <c>g_campcheck_interval</c> seconds, a player who has moved less than <c>g_campcheck_distance</c>
/// units (measured in 2D, so jumping in place doesn't count) gets a "Don't camp!" centerprint and takes
/// <c>g_campcheck_damage</c> damage (capped so it can't instantly kill — health + armor*blockpercent + 5).
/// Enabled by the string cvar <c>g_campcheck</c> (expr_evaluate).
///
/// Ported: the per-frame 2D distance accumulation + the interval check + the bounded DEATH_CAMP damage
/// (PlayerPreThink), the camp-distance reset for the combatants on a hit (Damage_Calculate), the
/// CPID_CAMPCHECK death centerprint (PlayerDies), and the per-spawn timer/distance init (PlayerSpawn). The
/// CENTER_CAMPCHECK / CPID_CAMPCHECK notifications already exist in NotificationsList.
///
/// REAL-CLIENTS ONLY (parity §11): QC restricts the check to <c>IS_REAL_CLIENT</c> (bots may "camp" only
/// because the map lacks waypoints, and clones can't move), so bots are explicitly exempt here via
/// <see cref="Player.IsBot"/>. The warmup_stage / game_starttime gate collapses to the live-match check
/// (<see cref="VehicleCommon.GameStopped"/>) in the headless sim, which has no warmup state — documented as a
/// deliberate simplification. The chat-button gate (<c>g_campcheck_typecheck</c> || !PHYS_INPUT_BUTTON_CHAT) is
/// modeled via <see cref="Entity.ButtonChat"/> (written from the input <c>Typing</c> intent in PlayerPhysics),
/// and the <c>weaponLocked</c> gate via the freeze subset (<see cref="WeaponLocked"/>, mirroring
/// WeaponFireDriver). <c>:CampCheck</c> is advertised via <see cref="BuildMutatorsString"/>. The separate
/// pre-match re-grace block is ported for the <c>time &lt; game_starttime</c> portion (via
/// <see cref="StartItem.GameStartTimeProvider"/>); the campaign-bot-wait and round-active-but-not-started
/// portions still need cross-file seams that don't exist yet, as does the CopyBody clone-origin copy (the port
/// reuses the player edict on respawn instead of cloning a corpse, so there is no clone to copy the field onto).
/// </summary>
[Mutator]
public sealed class CampcheckMutator : MutatorBase
{
    /// <summary>QC autocvar_g_campcheck_damage.</summary>
    public float Damage = 100f;
    /// <summary>QC autocvar_g_campcheck_distance.</summary>
    public float Distance = 1800f;
    /// <summary>QC autocvar_g_campcheck_interval.</summary>
    public float Interval = 10f;
    /// <summary>QC autocvar_g_campcheck_typecheck — when true, camp-check players even while they are typing in chat.</summary>
    public bool TypeCheck;

    public CampcheckMutator() => NetName = "campcheck";

    /// <summary>
    /// QC <c>(autocvar_g_campaign &amp;&amp; !campaign_bots_may_start)</c> (sv_campcheck.qc:51): in a campaign, hold the
    /// camp-check re-grace until the human spawns. The server wires this to <c>g_campaign &amp;&amp; !Campaign.BotsMayStart</c>;
    /// unset (non-campaign / headless test) → never holds.
    /// </summary>
    public static System.Func<bool>? CampaignBotHold;

    // QC: REGISTER_MUTATOR(campcheck, expr_evaluate(autocvar_g_campcheck)) — g_campcheck is a string.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_campcheck"));

    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onPreThink ??= OnPlayerPreThink;
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        _onPlayerSpawn ??= OnPlayerSpawn;

        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);

        if (Api.Services is not null)
        {
            Damage = Api.Cvars.GetFloat("g_campcheck_damage");
            Distance = Api.Cvars.GetFloat("g_campcheck_distance");
            Interval = Api.Cvars.GetFloat("g_campcheck_interval");
            TypeCheck = Api.Cvars.GetFloat("g_campcheck_typecheck") != 0f;
        }
    }

    public override void Unhook()
    {
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    private static bool IsPlayer(Entity e) => (e.Flags & EntFlags.Client) != 0;
    private static bool IsFrozen(Entity e)
        => StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(e, f);

    /// <summary>QC <c>weaponLocked(player)</c> (weaponsystem.qc): the player can't fire so the camp check is skipped.
    /// QC's full predicate is <c>(time &lt; game_starttime &amp;&amp; !sv_ready_restart_after_countdown) || game_stopped ||
    /// player_blocked || StatusEffects_active(Frozen) || LockWeapon</c>. The game-start/game-stopped halves are
    /// already covered by the gate (<see cref="VehicleCommon.GameStopped"/> + the time &lt; game_starttime re-grace);
    /// <c>player_blocked</c> and the <c>LockWeapon</c> mutator hook aren't modeled in the port yet. The reachable
    /// piece — mirroring <c>WeaponFireDriver.WeaponLocked</c> — is the freeze: the gametype freeze stat
    /// (<see cref="Entity.FrozenStat"/>, e.g. Freeze Tag) OR the <c>STATUSEFFECT_Frozen</c> status effect.</summary>
    private static bool WeaponLocked(Entity e) => e.FrozenStat != 0 || IsFrozen(e);

    // MUTATOR_HOOKFUNCTION(campcheck, PlayerPreThink)
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        if (Api.Services is null) return false;
        Entity player = args.Player;
        float time = Api.Clock.Time;
        bool checked_ = false;

        // QC guards: interval set; match live; alive unfrozen player; (typecheck || !chat); real client; !weaponLocked.
        // The headless sim has no warmup/game_starttime, so that half collapses to the live-match check; the chat
        // (PHYS_INPUT_BUTTON_CHAT via player.ButtonChat) and weaponLocked gates ARE modeled below.
        // IS_REAL_CLIENT: bots are exempt — they may "camp" only because the map lacks waypoints, and clones can't
        // move; killing them for it is wrong (QC sv_campcheck.qc:44, matches the mutator's own docstring).
        if (Interval != 0f
            && !VehicleCommon.GameStopped
            && IsPlayer(player) && player is not Player { IsBot: true }
            && player.DeadState == DeadFlag.No && !IsFrozen(player)
            && (TypeCheck || !player.ButtonChat) // QC: autocvar_g_campcheck_typecheck || !PHYS_INPUT_BUTTON_CHAT
            && !WeaponLocked(player))             // QC: !weaponLocked(player)
        {
            // 2D distance traveled since last frame (jumping in place doesn't count).
            Vector3 delta = player.CampcheckPrevOrigin - player.Origin;
            delta.Z = 0f;
            player.CampcheckTraveledDistance += MathF.Abs(delta.Length());

            // QC sv_campcheck.qc:51 — pre-match / campaign-wait / round-not-started re-grace: zero the accumulator
            // and push the next check out by interval*2 so a player who held still across the round/level start
            // transition isn't camp-checked on the first post-start interval. Full QC condition:
            //   (autocvar_g_campaign && !campaign_bots_may_start) || (time < game_starttime)
            //   || (round_handler_IsActive() && !round_handler_IsRoundStarted())
            // All three portions are now wired: the campaign-bot wait via CampaignBotHold (host seam), the prematch
            // clock via StartItem.GameStartTimeProvider, and the round-not-started via RoundHandler's Common seam.
            float gameStartTime = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
            bool campaignWait = CampaignBotHold?.Invoke() ?? false;
            if (campaignWait || time < gameStartTime || RoundHandler.RoundGateBlocks())
            {
                player.CampcheckNextCheck = time + Interval * 2f;
                player.CampcheckTraveledDistance = 0f;
            }

            if (time > player.CampcheckNextCheck)
            {
                if (player.CampcheckTraveledDistance < Distance)
                {
                    NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "CAMPCHECK");

                    if (player.Vehicle is not null)
                    {
                        Combat.Damage(player.Vehicle, null, null, Damage * 2f, "camp",
                            player.Vehicle.Origin, Vector3.Zero);
                    }
                    else
                    {
                        float blockPercent = Cvar("g_balance_armor_blockpercent", 0.7f);
                        float maxDmg = player.GetResource(ResourceType.Health)
                                     + player.GetResource(ResourceType.Armor) * blockPercent + 5f;
                        float dmg = QMath.Bound(0f, Damage, maxDmg); // QC bound(0, damage, max_dmg)
                        Combat.Damage(player, null, null, dmg, "camp", player.Origin, Vector3.Zero);
                    }
                }
                player.CampcheckNextCheck = time + Interval;
                player.CampcheckTraveledDistance = 0f;
            }
            checked_ = true;
        }

        if (!checked_)
            player.CampcheckNextCheck = time + Interval; // keep the timer up to date if a check failed

        player.CampcheckPrevOrigin = player.Origin;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(campcheck, Damage_Calculate) — combatants reset their camp distance on a fight.
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;
        if (attacker is not null && !ReferenceEquals(attacker, target) && IsPlayer(target) && IsPlayer(attacker))
        {
            target.CampcheckTraveledDistance = Distance;
            attacker.CampcheckTraveledDistance = Distance;
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(campcheck, PlayerDies) — clear the camp centerprint on death.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        // QC: Kill_Notification(NOTIF_ONE, frag_target, MSG_CENTER, CPID_CAMPCHECK) — retract the "Don't camp!"
        // centerprint for the victim so a stale warning doesn't linger past death. SendCenterKill is the port's
        // MSG_CENTER_KILL successor; NOTIF_ONE -> NotifBroadcast.One, target = the frag victim, group CPID_CAMPCHECK.
        NotificationSystem.SendCenterKill(NotifBroadcast.One, args.Target, "CPID_CAMPCHECK");
        return false;
    }

    // MUTATOR_HOOKFUNCTION(campcheck, BuildMutatorsString) — sv_campcheck.qc:99-102.
    // Appends ":CampCheck" to the active-mutators string reported in server info / scoreboard. The
    // MutatorActivation.BuildMutatorsString chain (run from GameWorld.cs gamelog init) calls this for each
    // active mutator. CampCheck has no BuildMutatorsPrettyString hook in Base, so none is overridden.
    public override string BuildMutatorsString(string s) => s + ":CampCheck";

    // MUTATOR_HOOKFUNCTION(campcheck, PlayerSpawn) — init the camp timer/distance.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        float time = Api.Services is null ? 0f : Api.Clock.Time;
        player.CampcheckNextCheck = time + Interval * 2f;
        player.CampcheckTraveledDistance = 0f;
        return false;
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
