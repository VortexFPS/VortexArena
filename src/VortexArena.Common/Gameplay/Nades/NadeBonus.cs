// Port of qcsrc/common/mutators/mutator/nades/sv_nades.qc — the bonus-nade economy:
//   nades_GiveBonus (445), nades_RemoveBonus (465), and the PlayerDies killcount/spree bonus award
//   (836-874) + the MonsterDies bonus (896-904).
//
// Bonus nades accrue from score (time / frags / objectives). A frag awards a killcount-scaled bonus; a
// teamkill/suicide wipes the attacker's bonus; spree milestones award a flat spree bonus. The accrual is a
// fractional score that, when it crosses 1.0, banks one bonus nade (decremented when a bonus nade is primed).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>The bonus-nade economy (QC nades_GiveBonus / nades_RemoveBonus + the kill-bonus award).</summary>
public static class NadeBonus
{
    // mutators.cfg bonus defaults.
    private const float DefBonusMax = 3f;          // g_nades_bonus_max
    private const float DefBonusScoreMax = 120f;   // g_nades_bonus_score_max
    private const float DefBonusScoreMinor = 5f;   // g_nades_bonus_score_minor
    private const float DefBonusScoreMedium = 30f; // g_nades_bonus_score_medium
    private const float DefBonusScoreSpree = 40f;  // g_nades_bonus_score_spree

    // KILL_SPREE_LIST thresholds (notifications/all.qh): the killcounts that award a spree bonus.
    private static readonly int[] SpreeThresholds = { 3, 5, 10, 15, 20, 25, 30 };

    /// <summary>
    /// Port of <c>nades_GiveBonus(entity player, float score)</c> (sv_nades.qc:445): accrue
    /// <paramref name="score"/> toward the next bonus nade (normalized by g_nades_bonus_score_max), and when
    /// it crosses 1.0 bank a bonus nade (capped at g_nades_bonus_max). Gated by g_nades + g_nades_bonus, a
    /// live unfrozen player, and the bonus cap.
    /// </summary>
    public static void GiveBonus(Entity player, float score)
    {
        if (Api.Services is null) return;
        if (Cvar("g_nades", 0f) == 0f || Cvar("g_nades_bonus", 0f) == 0f) return;
        if ((player.Flags & EntFlags.Client) == 0) return;
        if (player.DeadState != DeadFlag.No) return;
        if (player.NadeBonus >= (int)Cvar("g_nades_bonus_max", DefBonusMax)) return;
        if (IsFrozen(player)) return;

        float scoreMax = Cvar("g_nades_bonus_score_max", DefBonusScoreMax);
        if (scoreMax <= 0f) return;

        // QC: accrue only while below 1 (one bonus nade per crossing), then bank it.
        if (player.NadeBonusScore < 1f)
            player.NadeBonusScore += score / scoreMax;

        if (player.NadeBonusScore >= 1f)
        {
            // QC sv_nades.qc:456-457: Send_Notification(NOTIF_ONE, player, MSG_CENTER, CENTER_NADE_BONUS) +
            // play2(player, SND(NADE_BONUS)) (= SND_KH_ALARM) — tell the player they banked a bonus grenade.
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "NADE_BONUS");
            // QC play2(player, SND(NADE_BONUS)) — a per-recipient 2D send (CH_INFO / VOL_BASE 0.7 / ATTEN_NONE),
            // NOT a positional emit. Play2 matches the channel/volume/attenuation Base uses for this banked-bonus
            // cue (PlayOn would play it spatialized at the registered Item atten).
            SoundSystem.Play2(player, "NADE_BONUS");
            ++player.NadeBonus;
            --player.NadeBonusScore;
        }
    }

    /// <summary>Port of <c>nades_RemoveBonus(entity player)</c> (sv_nades.qc:465): clear all banked bonus + accrual.</summary>
    public static void RemoveBonus(Entity player)
    {
        player.NadeBonus = 0;
        player.NadeBonusScore = 0f;
    }

    /// <summary>
    /// Port of the <c>PlayerDies</c> bonus award (sv_nades.qc:845-873): on a frag, a same-team/self kill wipes
    /// the attacker's bonus; a VIP (flag/ball carrier) kill awards the medium bonus; otherwise the attacker
    /// gains a killcount-scaled bonus (or a flat spree bonus at a spree milestone). Always wipes the victim's
    /// bonus. <paramref name="attacker"/> is the crediting player.
    /// </summary>
    public static void OnPlayerDies(Entity? attacker, Entity victim)
    {
        if (attacker is not null && (attacker.Flags & EntFlags.Client) != 0)
        {
            int killcount = attacker.GtKillCount;
            float minor = Cvar("g_nades_bonus_score_minor", DefBonusScoreMinor);
            float medium = Cvar("g_nades_bonus_score_medium", DefBonusScoreMedium);

            float killcountBonus = killcount >= 1
                ? Clamp(minor * killcount, 0f, medium)
                : minor;

            if (SameTeamOrSelf(attacker, victim))
            {
                RemoveBonus(attacker);
            }
            // QC sv_nades.qc:852: killing a VIP (CTF flag / KA-NB ball / TKA key carrier) awards the medium bonus.
            else if (GametypeEntities.ScoringIsVip(victim))
            {
                GiveBonus(attacker, medium);
            }
            else if (Cvar("g_nades_bonus_score_spree", DefBonusScoreSpree) != 0f && killcount > 1)
            {
                // QC: a spree milestone awards the spree bonus; otherwise the minor bonus.
                if (IsSpreeMilestone(killcount))
                    GiveBonus(attacker, Cvar("g_nades_bonus_score_spree", DefBonusScoreSpree));
                else
                    GiveBonus(attacker, minor);
            }
            else
            {
                GiveBonus(attacker, killcountBonus);
            }
        }

        RemoveBonus(victim);
    }

    /// <summary>
    /// Port of the <c>MonsterDies</c> bonus (sv_nades.qc:896): a player killing a non-spawned enemy monster
    /// gains the minor bonus. (Spawned/summoned monsters award nothing.)
    /// </summary>
    public static void OnMonsterDies(Entity? attacker, Entity monster, bool monsterWasSpawned)
    {
        if (attacker is null || (attacker.Flags & EntFlags.Client) == 0) return;
        if (monsterWasSpawned) return;
        if (Teams.SameTeam(attacker, monster)) return;
        GiveBonus(attacker, Cvar("g_nades_bonus_score_minor", DefBonusScoreMinor));
    }

    // ===================================================================================================
    //  helpers
    // ===================================================================================================

    private static bool IsSpreeMilestone(int killcount)
    {
        foreach (int t in SpreeThresholds)
            if (t == killcount) return true;
        return false;
    }

    private static bool SameTeamOrSelf(Entity a, Entity b)
        => ReferenceEquals(a, b) || Teams.SameTeam(a, b);

    private static bool IsFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return fz is not null && StatusEffectsCatalog.Has(e, fz);
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        // bonus cvars can be 0 legitimately (disable), but the defaults here are non-zero; treat a 0 read as
        // "unset → default" for the magnitude cvars, matching the rest of the nade code.
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
