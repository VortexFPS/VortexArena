// Port of qcsrc/common/weapons/weapon/*.qc wr_killmessage / wr_suicidemessage (the per-weapon obituary
// message selection) + the special-death (DEATH_SELF_* / DEATH_MURDER_*) mapping from
// qcsrc/server/damage.qc Obituary_SpecialDeath. In QC each weapon CLASS owns its own wr_killmessage /
// wr_suicidemessage METHOD that branches on the global w_deathtype HITTYPE bits and returns the
// Notification to send (e.g. Devastator returns WEAPON_DEVASTATOR_MURDER_SPLASH when
// (w_deathtype & (HITTYPE_BOUNCE|HITTYPE_SPLASH))). This port houses that selection CENTRALLY (the weapon
// classes are owned by another task and must not carry message logic): SelectKillMessage / SelectSuicideMessage
// key on the weapon NetName carried by the deathtype string and sub-select on the HITTYPE_* suffixes
// (DeathTypes.HasHitType), returning the BARE notification name (without the "WEAPON_"/"DEATH_" prefix's
// type — the caller picks MSG_MULTI vs MSG_INFO). SelectSpecial maps the non-weapon environment/special
// deaths to their DEATH_SELF_* (suicide) / DEATH_MURDER_* (murder) bare names.
//
// Faithfulness: every branch below is copied 1:1 from the cited wr_killmessage/wr_suicidemessage in
// Base/.../qcsrc/common/weapons/weapon. An unknown weapon falls back to the generic FRAG line (QC would
// LOG_TRACEF "has no notification for weapon" and Obituary then drops to Obituary_SpecialDeath; the generic
// DEATH_MURDER_FRAG / DEATH_SELF_GENERIC is the closest faithful default).

using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The central kill/suicide-message selector — the C# successor to every weapon's QC
/// <c>wr_killmessage</c>/<c>wr_suicidemessage</c> METHOD (common/weapons/weapon/*.qc), plus the special-death
/// name mapping from <c>Obituary_SpecialDeath</c> (server/damage.qc). Returns the BARE notification name; the
/// caller (Scores.EmitObituary) sends it as a MSG_MULTI (to the victim) and the matching MSG_INFO sub (to
/// everyone else), mirroring <c>Obituary_WeaponDeath</c>.
/// </summary>
public static class DeathMessages
{
    /// <summary>
    /// QC <c>wr_killmessage(thiswep)</c>: the kill-feed/obituary notification for a MURDER by a weapon. Keys on
    /// the weapon NetName carried by <paramref name="deathType"/> and sub-selects on its HITTYPE suffixes,
    /// reproducing each weapon class's branch. Returns the bare <c>WEAPON_*_MURDER*</c> name, or the generic
    /// <c>DEATH_MURDER_FRAG</c> for an unknown/empty weapon (QC's no-notification fallback).
    /// </summary>
    public static string SelectKillMessage(string weaponNetName, string? deathType)
    {
        bool sec = DeathTypes.HasHitType(deathType, DeathTypes.Secondary);
        bool bounce = DeathTypes.HasHitType(deathType, DeathTypes.Bounce);
        bool splash = DeathTypes.HasHitType(deathType, DeathTypes.Splash);

        switch (weaponNetName)
        {
            // arc.qc:680 — (HITTYPE_SECONDARY) ? WEAPON_ARC_MURDER_SPRAY : WEAPON_ARC_MURDER
            case "arc": return sec ? "WEAPON_ARC_MURDER_SPRAY" : "WEAPON_ARC_MURDER";

            // blaster.qc:133 — WEAPON_BLASTER_MURDER
            case "blaster": return "WEAPON_BLASTER_MURDER";

            // crylink.qc:580 — WEAPON_CRYLINK_MURDER
            case "crylink": return "WEAPON_CRYLINK_MURDER";

            // devastator.qc:558 — (HITTYPE_BOUNCE | HITTYPE_SPLASH) ? *_MURDER_SPLASH : *_MURDER_DIRECT
            case "devastator": return (bounce || splash) ? "WEAPON_DEVASTATOR_MURDER_SPLASH" : "WEAPON_DEVASTATOR_MURDER_DIRECT";

            // electro.qc:703 — SECONDARY ? *_MURDER_ORBS : (BOUNCE ? *_MURDER_COMBO : *_MURDER_BOLT)
            case "electro": return sec ? "WEAPON_ELECTRO_MURDER_ORBS" : (bounce ? "WEAPON_ELECTRO_MURDER_COMBO" : "WEAPON_ELECTRO_MURDER_BOLT");

            // fireball.qc:399 — SECONDARY ? *_MURDER_FIREMINE : *_MURDER_BLAST
            case "fireball": return sec ? "WEAPON_FIREBALL_MURDER_FIREMINE" : "WEAPON_FIREBALL_MURDER_BLAST";

            // hagar.qc:483 — SECONDARY ? *_MURDER_BURST : *_MURDER_SPRAY
            case "hagar": return sec ? "WEAPON_HAGAR_MURDER_BURST" : "WEAPON_HAGAR_MURDER_SPRAY";

            // hlac.qc:212 — WEAPON_HLAC_MURDER
            case "hlac": return "WEAPON_HLAC_MURDER";

            // hook.qc:251 — WEAPON_HOOK_MURDER
            case "hook": return "WEAPON_HOOK_MURDER";

            // machinegun.qc:398 — SECONDARY ? *_MURDER_SNIPE : *_MURDER_SPRAY
            case "machinegun": return sec ? "WEAPON_MACHINEGUN_MURDER_SNIPE" : "WEAPON_MACHINEGUN_MURDER_SPRAY";

            // minelayer.qc:526 — WEAPON_MINELAYER_MURDER
            case "minelayer": return "WEAPON_MINELAYER_MURDER";

            // mortar.qc:371 — SECONDARY ? *_MURDER_BOUNCE : *_MURDER_EXPLODE
            case "mortar": return sec ? "WEAPON_MORTAR_MURDER_BOUNCE" : "WEAPON_MORTAR_MURDER_EXPLODE";

            // rifle.qc:187 — SECONDARY ? (BOUNCE ? *_HAIL_PIERCING : *_HAIL) : (BOUNCE ? *_PIERCING : *_MURDER)
            case "rifle":
                return sec
                    ? (bounce ? "WEAPON_RIFLE_MURDER_HAIL_PIERCING" : "WEAPON_RIFLE_MURDER_HAIL")
                    : (bounce ? "WEAPON_RIFLE_MURDER_PIERCING" : "WEAPON_RIFLE_MURDER");

            // seeker.qc:635 — SECONDARY ? *_MURDER_TAG : *_MURDER_SPRAY
            case "seeker": return sec ? "WEAPON_SEEKER_MURDER_TAG" : "WEAPON_SEEKER_MURDER_SPRAY";

            // shotgun.qc:388 — SECONDARY ? *_MURDER_SLAP : *_MURDER
            case "shotgun": return sec ? "WEAPON_SHOTGUN_MURDER_SLAP" : "WEAPON_SHOTGUN_MURDER";

            // tuba.qc:410 — BOUNCE ? KLEINBOTTLE_MURDER : (SECONDARY ? ACCORDEON_MURDER : TUBA_MURDER)
            case "tuba": return bounce ? "WEAPON_KLEINBOTTLE_MURDER" : (sec ? "WEAPON_ACCORDEON_MURDER" : "WEAPON_TUBA_MURDER");

            // vaporizer.qc:399 — WEAPON_VAPORIZER_MURDER
            case "vaporizer": return "WEAPON_VAPORIZER_MURDER";

            // vortex.qc:320 — WEAPON_VORTEX_MURDER
            case "vortex": return "WEAPON_VORTEX_MURDER";

            default:
                // QC: DEATH_WEAPONOF == WEP_Null OR a weapon with no kill-message -> Obituary falls back to the
                // generic special-death murder line.
                return "DEATH_MURDER_FRAG";
        }
    }

    /// <summary>
    /// QC <c>wr_suicidemessage(thiswep)</c>: the obituary notification for a SUICIDE with a weapon. Most weapons
    /// return a fixed <c>WEAPON_*_SUICIDE</c>; the hitscan family returns the shared
    /// <c>WEAPON_THINKING_WITH_PORTALS</c> easter-egg line (you can't really suicide with a hitscan). Returns the
    /// bare name, or <c>DEATH_SELF_GENERIC</c> for an unknown weapon.
    /// </summary>
    public static string SelectSuicideMessage(string weaponNetName, string? deathType)
    {
        bool sec = DeathTypes.HasHitType(deathType, DeathTypes.Secondary);
        bool bounce = DeathTypes.HasHitType(deathType, DeathTypes.Bounce);

        switch (weaponNetName)
        {
            // arc.qc:649 — (SECONDARY) ? WEAPON_ARC_SUICIDE_BOLT : WEAPON_THINKING_WITH_PORTALS
            case "arc": return sec ? "WEAPON_ARC_SUICIDE_BOLT" : "WEAPON_THINKING_WITH_PORTALS";

            // blaster.qc:128 — WEAPON_BLASTER_SUICIDE
            case "blaster": return "WEAPON_BLASTER_SUICIDE";

            // crylink.qc:575 — WEAPON_CRYLINK_SUICIDE
            case "crylink": return "WEAPON_CRYLINK_SUICIDE";

            // devastator.qc:553 — WEAPON_DEVASTATOR_SUICIDE
            case "devastator": return "WEAPON_DEVASTATOR_SUICIDE";

            // electro.qc:695 — SECONDARY ? *_SUICIDE_ORBS : *_SUICIDE_BOLT
            case "electro": return sec ? "WEAPON_ELECTRO_SUICIDE_ORBS" : "WEAPON_ELECTRO_SUICIDE_BOLT";

            // fireball.qc:391 — SECONDARY ? *_SUICIDE_FIREMINE : *_SUICIDE_BLAST
            case "fireball": return sec ? "WEAPON_FIREBALL_SUICIDE_FIREMINE" : "WEAPON_FIREBALL_SUICIDE_BLAST";

            // hagar.qc:478 — WEAPON_HAGAR_SUICIDE
            case "hagar": return "WEAPON_HAGAR_SUICIDE";

            // hlac.qc:207 — WEAPON_HLAC_SUICIDE
            case "hlac": return "WEAPON_HLAC_SUICIDE";

            // hook.qc has no wr_suicidemessage -> generic.
            case "hook": return "DEATH_SELF_GENERIC";

            // machinegun.qc:393 — WEAPON_THINKING_WITH_PORTALS
            case "machinegun": return "WEAPON_THINKING_WITH_PORTALS";

            // minelayer.qc:521 — WEAPON_MINELAYER_SUICIDE
            case "minelayer": return "WEAPON_MINELAYER_SUICIDE";

            // mortar.qc:363 — SECONDARY ? *_SUICIDE_BOUNCE : *_SUICIDE_EXPLODE
            case "mortar": return sec ? "WEAPON_MORTAR_SUICIDE_BOUNCE" : "WEAPON_MORTAR_SUICIDE_EXPLODE";

            // rifle.qc:182 — WEAPON_THINKING_WITH_PORTALS
            case "rifle": return "WEAPON_THINKING_WITH_PORTALS";

            // seeker.qc:630 — WEAPON_SEEKER_SUICIDE
            case "seeker": return "WEAPON_SEEKER_SUICIDE";

            // shotgun.qc:383 — WEAPON_THINKING_WITH_PORTALS
            case "shotgun": return "WEAPON_THINKING_WITH_PORTALS";

            // tuba.qc:401 — BOUNCE ? KLEINBOTTLE_SUICIDE : (SECONDARY ? ACCORDEON_SUICIDE : TUBA_SUICIDE)
            case "tuba": return bounce ? "WEAPON_KLEINBOTTLE_SUICIDE" : (sec ? "WEAPON_ACCORDEON_SUICIDE" : "WEAPON_TUBA_SUICIDE");

            // vaporizer.qc:394 — WEAPON_THINKING_WITH_PORTALS
            case "vaporizer": return "WEAPON_THINKING_WITH_PORTALS";

            // vortex.qc:316 — WEAPON_THINKING_WITH_PORTALS
            case "vortex": return "WEAPON_THINKING_WITH_PORTALS";

            default:
                return "DEATH_SELF_GENERIC";
        }
    }

    /// <summary>
    /// The non-weapon (special) death name (QC <c>Obituary_SpecialDeath</c>, server/damage.qc): maps an
    /// environment/special deathtype to its bare notification. <paramref name="murder"/> picks the
    /// <c>DEATH_MURDER_*</c> family (an enemy pushed you in) vs the <c>DEATH_SELF_*</c> family (you did it / the
    /// world did). Falls back to GENERIC for an unrecognized special death (QC default branch).
    /// </summary>
    public static string SelectSpecial(string? deathType, bool murder)
    {
        // QC Obituary_SpecialDeath reads the REGISTERED death_msgself / death_msgmurder off the deathtype entity
        // (server/damage.qc:142-143). The categorized monster/turret/vehicle rows (deathtypes/all.inc) carry
        // category-specific names that do NOT follow the shared-suffix scheme below: a monster murder is the
        // generic DEATH_MURDER_MONSTER, a turret murder is DEATH_MURDER_CHEAT, a vehicle death has per-vehicle
        // self+murder lines, and each monster/turret has its own DEATH_SELF_MON_*/DEATH_SELF_TURRET_* self line.
        // Consult the registry first so those categories resolve to their registered notification (matching Base);
        // a NULL-registered message for that direction falls through to the generic name (QC: death_message NULL →
        // Obituary sends nothing, but for the port's flat selector we emit the generic family as the closest send).
        var def = DeathTypes.Lookup(deathType);
        if (def is not null && def.Category != DeathCategory.None)
        {
            string? registered = murder ? def.MurderMessage : def.SelfMessage;
            if (!string.IsNullOrEmpty(registered))
                return registered;
            // No registered message for this direction (e.g. a vehicle GUN has no self line): generic fallback.
            return murder ? "DEATH_MURDER_FRAG" : "DEATH_SELF_GENERIC";
        }

        string b = DeathTypes.BaseOf(deathType);
        // QC: the special deathtypes share the SAME suffix in both families (DEATH_SELF_FALL / DEATH_MURDER_FALL).
        string name = b switch
        {
            DeathTypes.Fall => "FALL",
            DeathTypes.Drown => "DROWN",
            DeathTypes.Lava => "LAVA",
            DeathTypes.Slime => "SLIME",
            DeathTypes.Swamp => "SWAMP",
            DeathTypes.Void => "VOID",
            DeathTypes.Fire => "FIRE",
            DeathTypes.Camp => "CAMP",             // campcheck: self-only (no DEATH_MURDER_CAMP); murder -> GENERIC below
            DeathTypes.Telefrag => "TELEFRAG",     // murder-only in QC; self falls to GENERIC below
            DeathTypes.BuffInferno => "BUFF_INFERNO",
            DeathTypes.BuffVengeance => "BUFF_VENGEANCE",
            // touchexplode (all.inc:35): registers BOTH DEATH_SELF_TOUCHEXPLODE and DEATH_MURDER_TOUCHEXPLODE,
            // shared suffix scheme -> "died in an accident" (self) / "died in an accident with" (murder).
            DeathTypes.TouchExplode => "TOUCHEXPLODE",
            // NADE family (all.inc:23-27): each registers BOTH DEATH_SELF_NADE* and DEATH_MURDER_NADE*, the
            // shared-suffix scheme. The live nade booms (NadeNormalBoom/Ice/Heal/Darkness/Napalm) emit these
            // through Combat.Damage when g_nades is on, so they were falling to GENERIC/FRAG before.
            DeathTypes.Nade => "NADE",
            DeathTypes.NadeNapalm => "NADE_NAPALM",
            DeathTypes.NadeIce => "NADE_ICE",
            DeathTypes.NadeHeal => "NADE_HEAL",
            DeathTypes.NadeDarkness => "NADE_DARKNESS",
            // cheat (all.inc:5): DEATH_SELF_CHEAT / DEATH_MURDER_CHEAT (the r00t cheat radius nuke).
            DeathTypes.Cheat => "CHEAT",
            // shooting_star (all.inc:30): DEATH_SELF_SHOOTING_STAR / DEATH_MURDER_SHOOTING_STAR.
            DeathTypes.ShootingStar => "SHOOTING_STAR",
            // rot (all.inc:29): DEATH_SELF_ROT self-only (NULL murder -> FRAG below).
            DeathTypes.Rot => "ROT",
            DeathTypes.NoAmmo => "NOAMMO",         // self-only (NOAMMO has no murder line)
            // kill (all.inc:12): DEATH_KILL registers DEATH_SELF_SUICIDE ("couldn't take it anymore"), NULL murder.
            DeathTypes.Kill => "SUICIDE",
            // mirrordamage (all.inc:14): DEATH_MIRRORDAMAGE registers DEATH_SELF_BETRAYAL ("you were betrayed"), NULL murder.
            DeathTypes.MirrorDamage => "BETRAYAL",
            _ => "GENERIC",
        };

        // QC: TELEFRAG only has a DEATH_MURDER_TELEFRAG (a telefrag is always by another player); a
        // self-telefrag is impossible, so the self family has no TELEFRAG -> generic.
        if (!murder && name == "TELEFRAG")
            name = "GENERIC";
        // QC: NOAMMO is a self-death only; there is no DEATH_MURDER_NOAMMO.
        if (murder && name == "NOAMMO")
            name = "FRAG";
        // QC: these specials register a NULL murder line (death_msgmurder == NULL); the murder direction has no
        // line, so the obituary falls back to the generic frag. CAMP (all.inc:4), ROT (all.inc:29),
        // SUICIDE/KILL (all.inc:12), BETRAYAL/MIRRORDAMAGE (all.inc:14).
        if (murder && (name == "CAMP" || name == "ROT" || name == "SUICIDE" || name == "BETRAYAL"))
            name = "FRAG";

        return (murder ? "DEATH_MURDER_" : "DEATH_SELF_") + name;
    }

    // ---------------------------------------------------------------------------------------------
    // CHOICE / MULTI arg-count parity backfill
    // (QC Create_Notification_Entity_Choice all.qc:672-673 + Create_Notification_Entity Multi all.qc:639-640)
    // ---------------------------------------------------------------------------------------------

    private static bool _choiceCountsFixed;

    /// <summary>
    /// Back-fill the <c>StringCount</c>/<c>FloatCount</c> of every MSG_CHOICE and MSG_MULTI notification to the
    /// <c>max</c> of its sub-notifications' counts — exactly what QC does at registration
    /// (<c>Create_Notification_Entity_Choice</c>, all.qc:672-673, and the MULTI branch of
    /// <c>Create_Notification_Entity</c>, all.qc:639-640). The C# <c>Choice()</c>/<c>Multi()</c> builders leave
    /// those counts 0/0, so a CHOICE or MULTI <c>Send</c> carrying the union arg shape (e.g. CHOICE_FRAG =
    /// s1+spree_cen+ping = 1s/2f, or the WEAPON_*_MURDER MULTI = 3s/2f) would otherwise fail the send-time
    /// arg-count check and silently drop. Idempotent (one-shot latch); the obituary emitter calls it once at
    /// <c>SubscribeToDeaths</c> before any kill-feed/centerprint emits. Named for the CHOICE case it was added
    /// for; covers MULTI too since both share the same gap. T40.
    /// </summary>
    public static void EnsureChoiceArgCounts()
    {
        if (_choiceCountsFixed) return;
        _choiceCountsFixed = true;
        foreach (var n in Notifications.All)
        {
            int s, f;
            if (n.Type == MsgType.Choice)
            {
                var a = n.ChoiceOptionA is null ? null : Notifications.ByName(n.ChoiceType, n.ChoiceOptionA);
                var b = n.ChoiceOptionB is null ? null : Notifications.ByName(n.ChoiceType, n.ChoiceOptionB);
                s = System.Math.Max(a?.StringCount ?? 0, b?.StringCount ?? 0);
                f = System.Math.Max(a?.FloatCount ?? 0, b?.FloatCount ?? 0);
            }
            else if (n.Type == MsgType.Multi)
            {
                // QC: max(infoname, centername) counts (the annce sub carries no args).
                var info = n.MultiInfo is null ? null : Notifications.ByName(MsgType.Info, n.MultiInfo);
                var center = n.MultiCenter is null ? null : Notifications.ByName(MsgType.Center, n.MultiCenter);
                s = System.Math.Max(info?.StringCount ?? 0, center?.StringCount ?? 0);
                f = System.Math.Max(info?.FloatCount ?? 0, center?.FloatCount ?? 0);
            }
            else continue;

            if (n.StringCount < s) n.StringCount = s;
            if (n.FloatCount < f) n.FloatCount = f;
        }
    }

    /// <summary>Reset the one-shot CHOICE/MULTI backfill latch (test support).</summary>
    public static void ResetChoiceArgCounts() => _choiceCountsFixed = false;
}
