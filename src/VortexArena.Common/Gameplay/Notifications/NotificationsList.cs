// Registration of the notifications this port implements — the C# successor to the REGISTER_NOTIFICATION
// table in Base/.../qcsrc/common/notifications/all.inc.
//
// The lead calls Notifications.RegisterAll() once from GameInit. Self-registering catalog (no [attribute]
// bootstrap). Format strings, arg counts and names are copied verbatim from all.inc so the send-time
// validation and the eventual networking stay faithful. Color codes (^BG, ^K1, ^F1, ^COUNT, ^BOLD …)
// are left in the strings; their expansion is a client-side concern (NotificationSystem expands the basic
// %s/%d and the documented arg tokens). BOLD_OPERATOR is left as the literal "^BOLD" marker QC ships.
//
// Coverage: a large slice of the ~727-entry all.inc table —
//   - MSG_ANNCE : the full announcer set (achievements, killstreaks, every countdown family —
//                 gamestart/roundstart/kill/respawn, remaining frags/minutes, votes, instagib, etc.).
//   - MSG_INFO  : the complete self-death (DEATH_SELF_*) + murder (DEATH_MURDER_*) obituary families
//                 (environment, monsters, turrets, vehicles, nades, buffs), every weapon murder/suicide
//                 line (WEAPON_*_MURDER / *_SUICIDE), CTF pickup/capture/return/flagreturn, items,
//                 connect/leave, frag milestones.
//   - MSG_CENTER: countdown/round/overtime, CTF (incl. team-keyed via MULTITEAM), item pickups, the
//                 self-death and murder (frag/typefrag, verbose) centerprints.
//   - MSG_MULTI : the death/weapon bundles (announcer? + info + center) and the CTF bundles.
//   - MSG_CHOICE: the verbose/terse frag + CTF-pickup choices.
//   - MULTITEAM : the CTF capture/pickup/return team expansions (RED/BLUE/YELLOW/PINK + NEUTRAL).
// The few remaining niche entries (per-monster-variant centerprints, race split-times, some onslaught
// lines) follow the identical macro forms below and can be appended the same way.

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Installs the notifications into <see cref="Notifications"/>. Idempotent (by typed name); call
/// <see cref="RegisterAll"/> once at boot. Names/format strings mirror QC <c>all.inc</c>.
/// </summary>
public static class NotificationsList
{
    // QC announcer channel/volume/atten constants (sounds/sound.qh).
    private const int CH_INFO = 0;
    private const float VOL_BASEVOICE = 1f;
    private const float ATTEN_NONE = 0f;

    // QC BOLD_OPERATOR marker (notifications start bold strings with this; client expands it).
    private const string BOLD = "^BOLD";

    public static void RegisterAll()
    {
        RegisterAnnouncers();
        RegisterInfoSelfDeaths();
        RegisterInfoMurders();
        RegisterInfoWeapons();
        RegisterInfoCtf();
        RegisterInfoMisc();
        RegisterCenter();
        RegisterCenterCtf();
        RegisterChoice();
        RegisterMulti();

        // The remainder of all.inc (gametype/round/race/onslaught/keyhunt/freezetag/vehicle/powerup/
        // join/quit/version/teamchange/timeout families, the FIRE/FREEZE frag choices, the full death &
        // weapon MULTI bundles, the team-keyed CTF info/center/choice variants, …) ported verbatim.
        RegisterGeneratedInfo();
        RegisterGeneratedCenter();
        RegisterGeneratedMulti();
        RegisterGeneratedChoice();

        // Apply the MSG_CENTER durcnt (duration/count) specs from Base all.inc onto the registered center
        // notifications (the .Center builder defaults durcnt to "" == "0 0" == default panel time).
        ApplyCenterDurcnt();

        // Deterministic CL/SV ordering + ids (mirrors QC REGISTRY_SORT(Notifications) at boot).
        Notifications.Sort();
    }

    /// <summary>
    /// The MSG_CENTER durcnt ("DURATION COUNT") column from Base <c>common/notifications/all.inc</c> — the only
    /// center notifications whose durcnt differs from the default "0 0" (which means "use the panel default
    /// time, no countdown"). Token 0 is the display duration, token 1 the ^COUNT count; tokens are a literal
    /// number, an <c>fN</c> float-arg reference, or <c>item_centime</c> (== notification_item_centerprinttime).
    /// Resolved at centerprint time by <c>HudNotifications.ShowCenter</c>. Keyed by bare notification name.
    /// </summary>
    private static readonly (string Name, string Durcnt)[] CenterDurcnt =
    {
        ("COUNTDOWN_BEGIN",            "2 0"),
        ("COUNTDOWN_GAMESTART",        "1 f1"),
        ("COUNTDOWN_ROUNDSTART",       "1 f2"),
        ("COUNTDOWN_ROUNDSTOP",        "2 0"),
        ("COUNTDOWN_STOP_MINPLAYERS",  "4 0"),
        ("DISCONNECT_IDLING",          "1 f1"),
        ("INSTAGIB_DOWNGRADE",         "5 0"),
        ("INSTAGIB_FINDAMMO",          "1 9"),
        ("INSTAGIB_FINDAMMO_FIRST",    "1 10"),
        ("ITEM_BUFF_DROP",             "item_centime 0"),
        ("ITEM_BUFF_GOT",              "item_centime 0"),
        ("ITEM_FUELREGEN_GOT",         "item_centime 0"),
        ("ITEM_JETPACK_GOT",           "item_centime 0"),
        ("ITEM_WEAPON_DONTHAVE",       "item_centime 0"),
        ("ITEM_WEAPON_DROP",           "item_centime 0"),
        ("ITEM_WEAPON_GOT",            "item_centime 0"),
        ("ITEM_WEAPON_NOAMMO",         "item_centime 0"),
        ("ITEM_WEAPON_PRIMORSEC",      "item_centime 0"),
        ("ITEM_WEAPON_UNAVAILABLE",    "item_centime 0"),
        ("KEYHUNT_ROUNDSTART",         "1 f1"),
        ("KEYHUNT_SCAN",               "f1 0"),
        ("MOVETOSPEC_IDLING",          "1 f1"),
        ("MOVETOSPEC_REMOVE",          "1 f1"),
        ("NIX_COUNTDOWN",              "1 f2"),
        ("OVERTIME_CONTROLPOINT",      "5 0"),
        ("SURVIVAL_HUNTER",            "5 0"),
        ("SURVIVAL_SURVIVOR",          "5 0"),
        ("TEAMCHANGE_RED",             "1 f1"),
        ("TEAMCHANGE_BLUE",            "1 f1"),
        ("TEAMCHANGE_YELLOW",          "1 f1"),
        ("TEAMCHANGE_PINK",            "1 f1"),
        ("TEAMCHANGE_SPECTATE",        "1 f1"),
        ("TEAMCHANGE_SUICIDE",         "1 f1"),
        ("TIMEOUT_BEGINNING",          "1 f1"),
        ("TIMEOUT_ENDING",             "1 f1"),
        ("VEHICLE_STEAL_SELF",         "4 0"),
    };

    /// <summary>Stamp the Base durcnt specs onto the registered center notifications (see <see cref="CenterDurcnt"/>).</summary>
    private static void ApplyCenterDurcnt()
    {
        foreach ((string name, string durcnt) in CenterDurcnt)
        {
            Notification? n = Notifications.ByName(MsgType.Center, name);
            if (n is not null)
                n.Durcnt = durcnt;
        }
    }

    // The four team suffixes used by the MULTITEAM_* expansions (NUM_TEAM_1..4).
    private static readonly string[] TeamSuffix = { "RED", "BLUE", "YELLOW", "PINK" };

    // =====================================================================================
    //  MSG_ANNCE — announcer sounds (full set; the default flag governs gentle-mode/always)
    // =====================================================================================
    private static void RegisterAnnouncers()
    {
        // Annce(name, sound, channel, volume, attenuation, enabled). N___NEVER -> enabled:false.
        void A(string name, string sound, bool enabled = true)
            => Notifications.Annce(name, sound, CH_INFO, VOL_BASEVOICE, ATTEN_NONE, enabled);

        // achievements
        A("ACHIEVEMENT_AIRSHOT", "airshot");
        A("ACHIEVEMENT_AMAZING", "amazing");
        A("ACHIEVEMENT_AWESOME", "awesome");
        A("ACHIEVEMENT_BOTLIKE", "botlike");
        A("ACHIEVEMENT_ELECTROBITCH", "electrobitch");
        A("ACHIEVEMENT_IMPRESSIVE", "impressive");
        A("ACHIEVEMENT_YODA", "yoda");

        A("BEGIN", "begin");
        A("HEADSHOT", "headshot");

        // kill streaks
        A("KILLSTREAK_03", "03kills");
        A("KILLSTREAK_05", "05kills");
        A("KILLSTREAK_10", "10kills");
        A("KILLSTREAK_15", "15kills");
        A("KILLSTREAK_20", "20kills");
        A("KILLSTREAK_25", "25kills");
        A("KILLSTREAK_30", "30kills");

        // instagib
        A("INSTAGIB_LASTSECOND", "lastsecond");
        A("INSTAGIB_NARROWLY", "narrowly");
        A("INSTAGIB_TERMINATED", "terminated");

        A("MULTIFRAG", "multifrag", enabled: false);

        // countdown families. Each shares the numeric sound files "1".."10".
        // NUM_* (generic), GAMESTART_*, ROUNDSTART_* are mostly on; KILL_*/RESPAWN_* default off.
        for (int n = 1; n <= 10; n++)
        {
            A($"NUM_{n}", n.ToString());
            A($"NUM_GAMESTART_{n}", n.ToString(), enabled: n <= 5);
            A($"NUM_ROUNDSTART_{n}", n.ToString(), enabled: n <= 3);
            A($"NUM_KILL_{n}", n.ToString(), enabled: false);
            A($"NUM_RESPAWN_{n}", n.ToString(), enabled: false);
        }

        A("PREPARE", "prepareforbattle");

        // remaining frags / minutes
        A("REMAINING_FRAG_1", "1fragleft");
        A("REMAINING_FRAG_2", "2fragsleft");
        A("REMAINING_FRAG_3", "3fragsleft");
        A("REMAINING_MIN_1", "1minuteremains");
        A("REMAINING_MIN_5", "5minutesremain");

        A("TIMEOUT", "timeoutcalled");
        A("VOTE_ACCEPT", "voteaccept");
        A("VOTE_CALL", "votecall");
        A("VOTE_FAIL", "votefail");
    }

    // =====================================================================================
    //  MSG_INFO — self-death obituaries (DEATH_SELF_*): s1 victim, s2loc, spree_lost. 2 strings, 1 float.
    // =====================================================================================
    private static void RegisterInfoSelfDeaths()
    {
        const string Args = "s1 s2loc spree_lost"; // shared by all non-teamchange self deaths
        const int S = 2, F = 1;

        void D(string name, string icon, string normal, string gentle = "")
            => Notifications.Info("DEATH_SELF_" + name, S, F, Args, icon, normal, gentle, enabled: false);

        D("GENERIC", "notify_selfkill", "^BG%s^K1 died%s%s");
        D("SUICIDE", "notify_selfkill", "^BG%s^K1 couldn't take it anymore%s%s");
        D("CHEAT", "notify_selfkill", "^BG%s^K1 unfairly eliminated themselves%s%s");
        D("FALL", "notify_fall", "^BG%s^K1 hit the ground with a crunch%s%s",
            "^BG%s^K1 hit the ground with a bit too much force%s%s");
        D("DROWN", "notify_water", "^BG%s^K1 couldn't catch their breath%s%s",
            "^BG%s^K1 was in the water for too long%s%s");
        D("LAVA", "notify_lava", "^BG%s^K1 turned into hot slag%s%s", "^BG%s^K1 found a hot place%s%s");
        D("FIRE", "notify_death", "^BG%s^K1 became a bit too crispy%s%s", "^BG%s^K1 felt a little hot%s%s");
        D("SLIME", "notify_slime", "^BG%s^K1 was slimed%s%s");
        D("SWAMP", "notify_slime", "^BG%s^K1 is now preserved for centuries to come%s%s");
        D("VOID", "notify_void", "^BG%s^K1 ended up in the wrong place%s%s");
        D("CAMP", "notify_camping", "^BG%s^K1 thought they found a nice camping ground%s%s");
        D("BETRAYAL", "notify_teamkill_red", "^BG%s^K1 became enemies with the Lord of Teamplay%s%s");
        D("NOAMMO", "notify_outofammo", "^BG%s^K1 died%s%s. What's the point of living without ammo?",
            "^BG%s^K1 ran out of ammo%s%s");
        D("ROT", "notify_death", "^BG%s^K1 rotted away%s%s");
        D("SHOOTING_STAR", "notify_shootingstar", "^BG%s^K1 became a shooting star%s%s");
        D("TOUCHEXPLODE", "notify_death", "^BG%s^K1 died in an accident%s%s");

        // nades
        D("NADE", "nade_normal", "^BG%s^K1 mastered the art of self-nading%s%s");
        D("NADE_DARKNESS", "nade_darkness", "^BG%s^K1 mastered the art of self-nading%s%s");
        D("NADE_HEAL", "nade_heal", "^BG%s^K1's Healing Nade didn't quite heal them%s%s");
        D("NADE_ICE", "nade_ice", "^BG%s^K1 mastered the art of self-nading%s%s");
        D("NADE_NAPALM", "nade_napalm", "^BG%s^K1 was burned to death by their own Napalm Nade%s%s",
            "^BG%s^K1 decided to take a look at the results of their napalm explosion%s%s");

        // monsters
        D("MON_MAGE", "notify_death", "^BG%s^K1 was exploded by a Mage%s%s");
        D("MON_GOLEM_CLAW", "notify_death", "^BG%s^K1's innards became outwards by a Golem%s%s");
        D("MON_GOLEM_SMASH", "notify_death", "^BG%s^K1 was smashed by a Golem%s%s");
        D("MON_GOLEM_ZAP", "notify_death", "^BG%s^K1 was zapped to death by a Golem%s%s");
        D("MON_SPIDER", "notify_death", "^BG%s^K1 was bitten by a Spider%s%s");
        D("MON_WYVERN", "notify_death", "^BG%s^K1 was fireballed by a Wyvern%s%s");
        D("MON_ZOMBIE_JUMP", "notify_death", "^BG%s^K1 joins the Zombies%s%s");
        D("MON_ZOMBIE_MELEE", "notify_death", "^BG%s^K1 was given kung fu lessons by a Zombie%s%s");

        // turrets
        D("TURRET", "notify_death", "^BG%s^K1 ran into a turret%s%s");
        D("TURRET_EWHEEL", "notify_death", "^BG%s^K1 was blasted away by an eWheel turret%s%s");
        D("TURRET_FLAC", "notify_death", "^BG%s^K1 got caught up in the FLAC turret fire%s%s");
        D("TURRET_HELLION", "notify_death", "^BG%s^K1 was blasted away by a Hellion turret%s%s");
        D("TURRET_HK", "notify_death", "^BG%s^K1 could not hide from the Hunter turret%s%s");
        D("TURRET_MACHINEGUN", "notify_death", "^BG%s^K1 was riddled full of holes by a Machinegun turret%s%s");
        D("TURRET_MLRS", "notify_death", "^BG%s^K1 got turned into smoldering gibs by an MLRS turret%s%s");
        D("TURRET_PHASER", "notify_death", "^BG%s^K1 was phased out by a turret%s%s");
        D("TURRET_PLASMA", "notify_death", "^BG%s^K1 got served some superheated plasma from a turret%s%s");
        D("TURRET_TESLA", "notify_death", "^BG%s^K1 was electrocuted by a Tesla turret%s%s");
        D("TURRET_WALK_GUN", "notify_death", "^BG%s^K1 got served a lead enrichment by a Walker turret%s%s");
        D("TURRET_WALK_MELEE", "notify_death", "^BG%s^K1 was impaled by a Walker turret%s%s");
        D("TURRET_WALK_ROCKET", "notify_death", "^BG%s^K1 was blasted away by a Walker turret%s%s");

        // vehicles
        D("VH_BUMB_DEATH", "notify_death", "^BG%s^K1 got caught in the blast of a Bumblebee explosion%s%s");
        D("VH_CRUSH", "notify_death", "^BG%s^K1 was crushed by a vehicle%s%s");
        D("VH_RAPT_BOMB", "notify_death", "^BG%s^K1 was caught in a Raptor cluster bomb%s%s");
        D("VH_RAPT_DEATH", "notify_death", "^BG%s^K1 got caught in the blast of a Raptor explosion%s%s");
        D("VH_SPID_DEATH", "notify_death", "^BG%s^K1 got caught in the blast of a Spiderbot explosion%s%s");
        D("VH_SPID_ROCKET", "notify_death", "^BG%s^K1 was blasted to bits by a Spiderbot rocket%s%s");
        D("VH_WAKI_DEATH", "notify_death", "^BG%s^K1 got caught in the blast of a Racer explosion%s%s");
        D("VH_WAKI_ROCKET", "notify_death", "^BG%s^K1 couldn't find shelter from a Racer rocket%s%s");

        // team-change variants use death_team as the float arg.
        Notifications.Info("DEATH_SELF_TEAMCHANGE", 2, 1, "s1 death_team s2loc", "",
            "^BG%s^K1 switched to the %s%s", enabled: false);
        Notifications.Info("DEATH_SELF_AUTOTEAMCHANGE", 2, 1, "s1 death_team s2loc", "",
            "^BG%s^K1 was moved into the %s%s", enabled: false);
    }

    // =====================================================================================
    //  MSG_INFO — murder obituaries (DEATH_MURDER_*): spree_inf s1 s2 s3loc spree_end. 3 strings, 2 floats.
    // =====================================================================================
    private static void RegisterInfoMurders()
    {
        const string Args = "spree_inf s1 s2 s3loc spree_end";
        const int S = 3, F = 2;

        void M(string name, string icon, string normal, string gentle = "")
            => Notifications.Info("DEATH_MURDER_" + name, S, F, Args, icon, normal, gentle, enabled: false);

        M("FRAG", "notify_death", "^BG%s%s^K1 was killed by ^BG%s^K1%s%s");
        M("CHEAT", "notify_death", "^BG%s%s^K1 was unfairly eliminated by ^BG%s^K1%s%s");
        M("DROWN", "notify_water", "^BG%s%s^K1 was drowned by ^BG%s^K1%s%s");
        M("FALL", "notify_fall", "^BG%s%s^K1 was grounded by ^BG%s^K1%s%s");
        M("FIRE", "notify_death", "^BG%s%s^K1 was burnt up into a crisp by ^BG%s^K1%s%s",
            "^BG%s%s^K1 felt a little hot from ^BG%s^K1's fire^K1%s%s");
        M("LAVA", "notify_lava", "^BG%s%s^K1 was cooked by ^BG%s^K1%s%s");
        M("SLIME", "notify_slime", "^BG%s%s^K1 was slimed by ^BG%s^K1%s%s");
        M("SWAMP", "notify_slime", "^BG%s%s^K1 was preserved by ^BG%s^K1%s%s");
        M("VOID", "notify_void", "^BG%s%s^K1 was thrown into a world of hurt by ^BG%s^K1%s%s");
        M("MONSTER", "notify_death", "^BG%s%s^K1 was pushed in front of a monster by ^BG%s^K1%s%s");
        M("SHOOTING_STAR", "notify_shootingstar", "^BG%s%s^K1 was shot into space by ^BG%s^K1%s%s");
        M("TELEFRAG", "notify_telefrag", "^BG%s%s^K1 was telefragged by ^BG%s^K1%s%s",
            "^BG%s%s^K1 tried to occupy ^BG%s^K1's teleport destination space%s%s");
        M("TOUCHEXPLODE", "notify_death", "^BG%s%s^K1 died in an accident with ^BG%s^K1%s%s");

        // buffs
        M("BUFF_INFERNO", "notify_death", "^BG%s%s^K1 burned at the mercy of ^BG%s^K1's Inferno buff %s%s",
            "^BG%s%s^K1 felt a little hot thanks to ^BG%s^K1's Inferno buff %s%s");
        M("BUFF_VENGEANCE", "notify_death", "^BG%s%s^K1 felt the karma of ^BG%s^K1's Vengeance buff %s%s");

        // nades
        M("NADE", "nade_normal", "^BG%s%s^K1 was blown up by ^BG%s^K1's Nade%s%s");
        M("NADE_DARKNESS", "nade_darkness", "^BG%s%s^K1 couldn't find the light in ^BG%s^K1's Darkness Nade%s%s");
        M("NADE_HEAL", "nade_heal", "^BG%s%s^K1 has not been healed by ^BG%s^K1's Healing Nade%s%s");
        M("NADE_ICE", "nade_ice", "^BG%s%s^K1 was blown up by ^BG%s^K1's Ice Nade%s%s");
        M("NADE_NAPALM", "nade_napalm", "^BG%s%s^K1 was burned to death by ^BG%s^K1's Napalm Nade%s%s",
            "^BG%s%s^K1 got too close to a napalm explosion%s%s");

        // vehicles
        M("VH_BUMB_DEATH", "notify_death", "^BG%s%s^K1 got caught in the blast when ^BG%s^K1's Bumblebee exploded%s%s");
        M("VH_BUMB_GUN", "notify_death", "^BG%s%s^K1 saw the pretty lights of ^BG%s^K1's Bumblebee gun%s%s");
        M("VH_CRUSH", "notify_death", "^BG%s%s^K1 was crushed by ^BG%s^K1%s%s");
        M("VH_RAPT_BOMB", "notify_death", "^BG%s%s^K1 was cluster bombed by ^BG%s^K1's Raptor%s%s");
        M("VH_RAPT_CANNON", "notify_death", "^BG%s%s^K1 couldn't resist ^BG%s^K1's purple blobs%s%s");
        M("VH_RAPT_DEATH", "notify_death", "^BG%s%s^K1 got caught in the blast when ^BG%s^K1's Raptor exploded%s%s");
        M("VH_SPID_DEATH", "notify_death", "^BG%s%s^K1 got caught in the blast when ^BG%s^K1's Spiderbot exploded%s%s");
        M("VH_SPID_MINIGUN", "notify_death", "^BG%s%s^K1 got shredded by ^BG%s^K1's Spiderbot%s%s");
        M("VH_SPID_ROCKET", "notify_death", "^BG%s%s^K1 was blasted to bits by ^BG%s^K1's Spiderbot%s%s");
        M("VH_WAKI_DEATH", "notify_death", "^BG%s%s^K1 got caught in the blast when ^BG%s^K1's Racer exploded%s%s");
        M("VH_WAKI_GUN", "notify_death", "^BG%s%s^K1 was bolted down by ^BG%s^K1's Racer%s%s");
        M("VH_WAKI_ROCKET", "notify_death", "^BG%s%s^K1 couldn't find shelter from ^BG%s^K1's Racer%s%s");
    }

    // =====================================================================================
    //  MSG_INFO — weapon obituaries (WEAPON_*_MURDER spree_inf 3s/2f; WEAPON_*_SUICIDE 2s/1f).
    // =====================================================================================
    private static void RegisterInfoWeapons()
    {
        void Murder(string name, string icon, string normal, string gentle = "")
            => Notifications.Info("WEAPON_" + name, 3, 2, "spree_inf s1 s2 s3loc spree_end", icon,
                normal, gentle, enabled: false);
        void Suicide(string name, string icon, string normal, string gentle = "")
            => Notifications.Info("WEAPON_" + name, 2, 1, "s1 s2loc spree_lost", icon,
                normal, gentle, enabled: false);

        Murder("ACCORDEON_MURDER", "weapontuba", "^BG%s%s^K1 died of ^BG%s^K1's great playing on the @!#%'n Accordeon%s%s");
        Suicide("ACCORDEON_SUICIDE", "weapontuba", "^BG%s^K1 hurt their own ears with the @!#%'n Accordeon%s%s");
        Murder("ARC_MURDER", "weaponarc", "^BG%s%s^K1 was electrocuted by ^BG%s^K1's Arc%s%s");
        Murder("ARC_MURDER_SPRAY", "weaponarc", "^BG%s%s^K1 was blasted by ^BG%s^K1's Arc bolts%s%s");
        Suicide("ARC_SUICIDE_BOLT", "weaponarc", "^BG%s^K1 played with Arc bolts%s%s");
        Murder("BLASTER_MURDER", "weaponlaser", "^BG%s%s^K1 was shot to death by ^BG%s^K1's Blaster%s%s");
        Suicide("BLASTER_SUICIDE", "weaponlaser", "^BG%s^K1 shot themselves to hell with their Blaster%s%s");
        Murder("CRYLINK_MURDER", "weaponcrylink", "^BG%s%s^K1 felt the strong pull of ^BG%s^K1's Crylink%s%s");
        Suicide("CRYLINK_SUICIDE", "weaponcrylink", "^BG%s^K1 felt the strong pull of their Crylink%s%s");
        Murder("DEVASTATOR_MURDER_DIRECT", "weaponrocketlauncher", "^BG%s%s^K1 ate ^BG%s^K1's rocket%s%s");
        Murder("DEVASTATOR_MURDER_SPLASH", "weaponrocketlauncher", "^BG%s%s^K1 got too close to ^BG%s^K1's rocket%s%s");
        Suicide("DEVASTATOR_SUICIDE", "weaponrocketlauncher", "^BG%s^K1 blew themselves up with their Devastator%s%s");
        Murder("ELECTRO_MURDER_BOLT", "weaponelectro", "^BG%s%s^K1 was blasted by ^BG%s^K1's Electro bolt%s%s");
        Murder("ELECTRO_MURDER_COMBO", "weaponelectro", "^BG%s%s^K1 felt the electrifying air of ^BG%s^K1's Electro combo%s%s");
        Murder("ELECTRO_MURDER_ORBS", "weaponelectro", "^BG%s%s^K1 got too close to ^BG%s^K1's Electro orb%s%s");
        Suicide("ELECTRO_SUICIDE_BOLT", "weaponelectro", "^BG%s^K1 played with Electro bolts%s%s");
        Suicide("ELECTRO_SUICIDE_ORBS", "weaponelectro", "^BG%s^K1 could not remember where they put their Electro orb%s%s");
        Murder("FIREBALL_MURDER_BLAST", "weaponfireball", "^BG%s%s^K1 got too close to ^BG%s^K1's fireball%s%s");
        Murder("FIREBALL_MURDER_FIREMINE", "weaponfireball", "^BG%s%s^K1 got burnt by ^BG%s^K1's firemine%s%s");
        Suicide("FIREBALL_SUICIDE_BLAST", "weaponfireball", "^BG%s^K1 should have used a smaller gun%s%s");
        Suicide("FIREBALL_SUICIDE_FIREMINE", "weaponfireball", "^BG%s^K1 forgot about their firemine%s%s");
        Murder("HAGAR_MURDER_BURST", "weaponhagar", "^BG%s%s^K1 was pummeled by a burst of ^BG%s^K1's Hagar rockets%s%s");
        Murder("HAGAR_MURDER_SPRAY", "weaponhagar", "^BG%s%s^K1 was pummeled by ^BG%s^K1's Hagar rockets%s%s");
        Suicide("HAGAR_SUICIDE", "weaponhagar", "^BG%s^K1 played with tiny Hagar rockets%s%s");
        Murder("HLAC_MURDER", "weaponhlac", "^BG%s%s^K1 was cut down with ^BG%s^K1's HLAC%s%s");
        Suicide("HLAC_SUICIDE", "weaponhlac", "^BG%s^K1 got a little jumpy with their HLAC%s%s");
        Murder("HOOK_MURDER", "weaponhook", "^BG%s%s^K1 was caught in ^BG%s^K1's Hook gravity bomb%s%s");
        Murder("KLEINBOTTLE_MURDER", "weapontuba", "^BG%s%s^K1 died of ^BG%s^K1's great playing on the @!#%'n Klein Bottle%s%s");
        Suicide("KLEINBOTTLE_SUICIDE", "weapontuba", "^BG%s^K1 hurt their own ears with the @!#%'n Klein Bottle%s%s");
        Murder("MACHINEGUN_MURDER_SNIPE", "weaponuzi", "^BG%s%s^K1 was sniped by ^BG%s^K1's Machine Gun%s%s");
        Murder("MACHINEGUN_MURDER_SPRAY", "weaponuzi", "^BG%s%s^K1 was riddled full of holes by ^BG%s^K1's Machine Gun%s%s");
        Murder("MINELAYER_MURDER", "weaponminelayer", "^BG%s%s^K1 got too close to ^BG%s^K1's mine%s%s");
        Suicide("MINELAYER_SUICIDE", "weaponminelayer", "^BG%s^K1 forgot about their mine%s%s");
        Murder("MORTAR_MURDER_BOUNCE", "weapongrenadelauncher", "^BG%s%s^K1 got too close to ^BG%s^K1's Mortar grenade%s%s");
        Murder("MORTAR_MURDER_EXPLODE", "weapongrenadelauncher", "^BG%s%s^K1 ate ^BG%s^K1's Mortar grenade%s%s");
        Suicide("MORTAR_SUICIDE_BOUNCE", "weapongrenadelauncher", "^BG%s^K1 didn't see their own Mortar grenade%s%s");
        Suicide("MORTAR_SUICIDE_EXPLODE", "weapongrenadelauncher", "^BG%s^K1 blew themselves up with their own Mortar%s%s");
        Murder("RIFLE_MURDER", "weaponrifle", "^BG%s%s^K1 was sniped with a Rifle by ^BG%s^K1%s%s");
        Murder("RIFLE_MURDER_HAIL", "weaponrifle", "^BG%s%s^K1 died in ^BG%s^K1's Rifle bullet hail%s%s");
        Murder("RIFLE_MURDER_HAIL_PIERCING", "weaponrifle", "^BG%s%s^K1 failed to hide from ^BG%s^K1's Rifle bullet hail%s%s");
        Murder("RIFLE_MURDER_PIERCING", "weaponrifle", "^BG%s%s^K1 got hit through ^BG%s^K1's Rifle%s%s");
        Murder("SEEKER_MURDER_SPRAY", "weaponseeker", "^BG%s%s^K1 was tagged by ^BG%s^K1's Seeker%s%s");
        Murder("SEEKER_MURDER_TAG", "weaponseeker", "^BG%s%s^K1 played with tiny Seeker rockets%s%s");
        Suicide("SEEKER_SUICIDE", "weaponseeker", "^BG%s^K1 played with tiny Seeker rockets%s%s");
        Murder("SHOCKWAVE_MURDER", "weaponshockwave", "^BG%s%s^K1 was gunned down by ^BG%s^K1's Shockwave%s%s");
        Murder("SHOCKWAVE_MURDER_SLAP", "weaponshockwave", "^BG%s%s^K1 slapped ^BG%s^K1 around a bit with a large Shockwave%s%s");
        Murder("SHOTGUN_MURDER", "weaponshotgun", "^BG%s%s^K1 was sprayed with bullets by ^BG%s^K1%s%s");
        Murder("SHOTGUN_MURDER_SLAP", "notify_melee_shotgun", "^BG%s%s^K1 slapped ^BG%s^K1 around a bit with a large Shotgun%s%s");
        Murder("TUBA_MURDER", "weapontuba", "^BG%s%s^K1 died of ^BG%s^K1's great playing on the @!#%'n Tuba%s%s");
        Suicide("TUBA_SUICIDE", "weapontuba", "^BG%s^K1 hurt their own ears with the @!#%'n Tuba%s%s");
        Murder("VAPORIZER_MURDER", "weaponminstanex", "^BG%s%s^K1 was sniped by ^BG%s^K1's Vaporizer%s%s");
        Murder("VORTEX_MURDER", "weaponnex", "^BG%s%s^K1 has been vaporized by ^BG%s^K1's Vortex%s%s");

        // a non-obituary weapon-limit info line (0s, 1f).
        Notifications.Info("WEAPON_MINELAYER_LIMIT", 0, 1, "f1", "",
            "^BGYou cannot place more than ^F2%s^BG mines at a time", enabled: false);
    }

    // =====================================================================================
    //  MSG_INFO — CTF (kill feed / console). Team variants via MULTITEAM expansion.
    // =====================================================================================
    private static void RegisterInfoCtf()
    {
        // neutral + per-team capture/lost/pickup
        InfoCtfTeamed("CTF_CAPTURE", 1, 0, "s1", "notify_{0}_captured", "^BG%s^BG captured the {1}flag");
        InfoCtfTeamed("CTF_LOST", 1, 0, "s1", "notify_{0}_lost", "^BG%s^BG lost the {1}flag");
        InfoCtfTeamed("CTF_PICKUP", 1, 0, "s1", "notify_{0}_taken", "^BG%s^BG got the {1}flag");

        // flag-return reasons (neutral). Team variants follow the identical pattern.
        Notifications.Info("CTF_FLAGRETURN_ABORTRUN_NEUTRAL", 0, 0, "", "",
            "^BGThe flag was returned by its owner", enabled: false);
        Notifications.Info("CTF_FLAGRETURN_DAMAGED_NEUTRAL", 0, 0, "", "",
            "^BGThe flag was destroyed and returned to base", enabled: false);
        Notifications.Info("CTF_FLAGRETURN_DROPPED_NEUTRAL", 0, 0, "", "",
            "^BGThe flag was dropped in the base and returned itself", enabled: false);
        Notifications.Info("CTF_FLAGRETURN_NEEDKILL_NEUTRAL", 0, 0, "", "",
            "^BGThe flag fell somewhere it couldn't be reached and returned to base", enabled: false);
        Notifications.Info("CTF_FLAGRETURN_SPEEDRUN_NEUTRAL", 0, 1, "f1dtime", "",
            "^BGThe flag became impatient after ^F1%.2f^BG seconds and returned itself", enabled: false);
        Notifications.Info("CTF_FLAGRETURN_TIMEOUT_NEUTRAL", 0, 0, "", "",
            "^BGThe flag has returned to the base", enabled: false);
    }

    /// <summary>Register a CTF info notif for NEUTRAL + the four teams (MULTITEAM_INFO equivalent).</summary>
    private static void InfoCtfTeamed(string baseName, int s, int f, string args, string iconFmt, string normalFmt)
    {
        // {0} = lowercase team token for the icon ("red"/.../"neutral"); {1} = colored flag prefix ("^1RED ", …) or "".
        Notifications.Info(baseName + "_NEUTRAL", s, f, args, string.Format(iconFmt, "neutral"),
            string.Format(normalFmt, "neutral", ""), enabled: false);
        string[] tokens = { "red", "blue", "yellow", "pink" };
        string[] colored = { "^1RED^BG ", "^4BLUE^BG ", "^3YELLOW^BG ", "^6PINK^BG " };
        for (int i = 0; i < 4; i++)
            Notifications.Info(baseName + "_" + TeamSuffix[i], s, f, args, string.Format(iconFmt, tokens[i]),
                string.Format(normalFmt, tokens[i], colored[i]), enabled: false);
    }

    // =====================================================================================
    //  MSG_INFO — items, connect/leave, frag milestones
    // =====================================================================================
    private static void RegisterInfoMisc()
    {
        Notifications.Info("ITEM_WEAPON_GOT", 0, 1, "item_wepname", "",
            "^BGYou got the ^F1%s", enabled: false);
        Notifications.Info("ITEM_WEAPON_DROP", 0, 2, "item_wepname item_wepammo", "",
            "^BGYou dropped the ^F1%s^BG%s", enabled: false);
        Notifications.Info("ITEM_WEAPON_DONTHAVE", 0, 1, "item_wepname", "",
            "^BGYou do not have the ^F1%s", enabled: false);
        Notifications.Info("ITEM_WEAPON_NOAMMO", 0, 1, "item_wepname", "",
            "^BGYou don't have enough ammo for the ^F1%s", enabled: false);
        Notifications.Info("ITEM_WEAPON_PRIMORSEC", 0, 3, "item_wepname f2primsec f3primsec", "",
            "^F1%s^BG is ^F4not available^BG on this map", enabled: false);
        Notifications.Info("ITEM_BUFF_GOT", 1, 1, "s1 item_buffname", "",
            "^BG%s^BG got the %s^BG buff!");
        Notifications.Info("ITEM_BUFF_LOST", 1, 1, "s1 item_buffname", "",
            "^BG%s^BG lost the %s^BG buff!");

        Notifications.Info("CHAT_CONNECT", 1, 0, "s1", "", "^BG%s^F3 connected");
        Notifications.Info("CHAT_DISCONNECT", 1, 0, "s1", "", "^BG%s^F3 disconnected");
        Notifications.Info("CHAT_JOIN", 1, 0, "s1", "", "^BG%s^F3 is now playing");
        Notifications.Info("CHAT_SPECTATE", 1, 0, "s1", "", "^BG%s^F3 is now spectating");

        Notifications.Info("SCORES", 2, 2, "s1 s2", "", "^BG%s^BG wins the game with %s points");
    }

    // =====================================================================================
    //  MSG_CENTER — countdown / round / item / self+murder centerprints
    // =====================================================================================
    private static void RegisterCenter()
    {
        Notifications.Center("COUNTDOWN_BEGIN", 0, 0, "", "CPID_ROUND", BOLD + "^BGBegin!");
        Notifications.Center("COUNTDOWN_GAMESTART", 0, 1, "", "CPID_ROUND", "^BGGame starts in\n" + BOLD + "^COUNT");
        Notifications.Center("COUNTDOWN_ROUNDSTART", 0, 2, "f1", "CPID_ROUND", "^BGRound %s starts in\n" + BOLD + "^COUNT");
        Notifications.Center("COUNTDOWN_ROUNDSTOP", 0, 0, "", "CPID_ROUND", "^F4Round cannot start");
        Notifications.Center("COUNTDOWN_STOP_MINPLAYERS", 0, 1, "f1", "CPID_MISSING_PLAYERS",
            BOLD + "^F4Countdown stopped!\n^BG%s players are needed for this match.");
        Notifications.Center("OVERTIME_FRAG", 0, 0, "", "CPID_OVERTIME", "^F2Overtime has begun!");
        Notifications.Center("OVERTIME_TIME", 0, 1, "f1time", "CPID_OVERTIME",
            "^F2Overtime!\n^BGAdded ^F4%s^BG to the game!");

        // item pickups (centerprint)
        Notifications.Center("ITEM_WEAPON_GOT", 0, 1, "item_wepname", "CPID_ITEM", "^BGYou got the ^F1%s");
        Notifications.Center("ITEM_WEAPON_DONTHAVE", 0, 1, "item_wepname", "CPID_ITEM", "^BGYou do not have the ^F1%s");
        Notifications.Center("ITEM_WEAPON_NOAMMO", 0, 1, "item_wepname", "CPID_ITEM",
            "^F1%s^BG doesn't have enough ammo to use this %s");
        Notifications.Center("ITEM_WEAPON_PRIMORSEC", 0, 3, "item_wepname f2primsec f3primsec", "CPID_ITEM",
            "^F1%s^BG ^F4doesn't work^BG without ^F1%s^BG, ^F1%s^BG or ^F1%s");
        Notifications.Center("ITEM_BUFF_GOT", 0, 1, "item_buffname", "CPID_ITEM", "^BGYou got the %s^BG buff!");
        Notifications.Center("ITEM_BUFF_DROP", 0, 1, "item_buffname", "CPID_ITEM", "^BGYou lost the %s^BG buff!");
        Notifications.Center("ITEM_JETPACK_GOT", 0, 0, "", "CPID_ITEM", "^BGYou got the ^F1Jetpack");
        Notifications.Center("ITEM_FUELREGEN_GOT", 0, 0, "", "CPID_ITEM", "^BGYou got the ^F1Fuel regenerator");

        // nades mutator: banking a bonus grenade (Base all.inc:654 MSG_CENTER_NOTIF(NADE_BONUS), CPID_NADES).
        Notifications.Center("NADE_BONUS", 0, 0, "", "CPID_NADES", "^F2You got a ^K1BONUS GRENADE^F2!");

        // self-death centerprints (shown to the victim)
        CenterSelf("GENERIC", BOLD + "^K1You fragged yourself!");
        CenterSelf("SUICIDE", BOLD + "^K1You committed suicide!");
        CenterSelf("CHEAT", BOLD + "^K1You unfairly eliminated yourself!");
        CenterSelf("FALL", BOLD + "^K1You hit the ground with a crunch!");
        CenterSelf("DROWN", BOLD + "^K1You couldn't catch your breath!");
        CenterSelf("LAVA", BOLD + "^K1You couldn't stand the heat!");
        CenterSelf("FIRE", BOLD + "^K1You got a little bit too crispy!");
        CenterSelf("SLIME", BOLD + "^K1You melted away in slime!");
        CenterSelf("VOID", BOLD + "^K1Watch your step!");
        CenterSelf("CAMP", BOLD + "^K1Die camper!");
        CenterSelf("NOAMMO", BOLD + "^K1You were killed for running out of ammo...");
        CenterSelf("ROT", BOLD + "^K1You grew too old without taking your medicine");
        CenterSelf("SHOOTING_STAR", BOLD + "^K1You became a shooting star!");
        CenterSelf("NADE", BOLD + "^K1You forgot to put the pin back in!");
        CenterSelf("BETRAYAL", BOLD + "^K1You were punished for attacking your teammates!");
        CenterSelf("MONSTER", BOLD + "^K1You were killed by a monster!");
        CenterSelf("TURRET", BOLD + "^K1You got killed by a turret!");
        Notifications.Center("DEATH_SELF_TEAMCHANGE", 0, 1, "death_team", "",
            BOLD + "^BGYou are now on: %s", enabled: false);
        Notifications.Center("DEATH_SELF_AUTOTEAMCHANGE", 0, 1, "death_team", "",
            BOLD + "^BGYou have been moved into a different team\nYou are now on: %s", enabled: false);

        // murder centerprints (frag/typefrag + verbose variants). spree_cen + s1. (1 string, 1 float;
        // verbose adds frag_ping/frag_stats floats.)
        CenterMurder("FRAG", "spree_cen s1", 1, BOLD + "^K3%sYou fragged ^BG%s", BOLD + "^K3%sYou scored against ^BG%s");
        CenterMurder("FRAGGED", "spree_cen s1", 1, BOLD + "^K1%sYou were fragged by ^BG%s", BOLD + "^K1%sYou were scored against by ^BG%s");
        CenterMurder("FRAG_VERBOSE", "spree_cen s1 frag_ping", 2, BOLD + "^K3%sYou fragged ^BG%s^BG%s", BOLD + "^K3%sYou scored against ^BG%s^BG%s");
        CenterMurder("FRAGGED_VERBOSE", "spree_cen s1 frag_stats", 4, BOLD + "^K1%sYou were fragged by ^BG%s^BG%s", BOLD + "^K1%sYou were scored against by ^BG%s^BG%s");
        CenterMurder("TYPEFRAG", "spree_cen s1", 1, BOLD + "^K1%sYou typefragged ^BG%s", BOLD + "^K1%sYou scored against ^BG%s^BG while they were typing");
        CenterMurder("TYPEFRAGGED", "spree_cen s1", 1, BOLD + "^K1%sYou were typefragged by ^BG%s", BOLD + "^K1%sYou were scored against by ^BG%s^BG while typing");
        CenterMurder("TYPEFRAG_VERBOSE", "spree_cen s1 frag_ping", 2, BOLD + "^K1%sYou typefragged ^BG%s^BG%s", BOLD + "^K1%sYou scored against ^BG%s^BG%s while they were typing");
        CenterMurder("TYPEFRAGGED_VERBOSE", "spree_cen s1 frag_stats", 4, BOLD + "^K1%sYou were typefragged by ^BG%s^BG%s", BOLD + "^K1%sYou were scored against by ^BG%s^BG%s while typing");
    }

    private static void CenterSelf(string name, string normal)
        => Notifications.Center("DEATH_SELF_" + name, 0, 0, "", "", normal);

    private static void CenterMurder(string name, string args, int floatCount, string normal, string gentle)
        => Notifications.Center("DEATH_MURDER_" + name, 1, floatCount, args, "", normal, gentle);

    // =====================================================================================
    //  MSG_CENTER — CTF (team-keyed via MULTITEAM). The flag prefix differs per team.
    // =====================================================================================
    private static void RegisterCenterCtf()
    {
        // "you got the flag" — neutral + per-team
        CenterCtfTeamed("CTF_PICKUP", 0, 0, "", BOLD + "^BGYou got the {0}flag!");
        // "you captured the flag"
        CenterCtfTeamed("CTF_CAPTURE", 0, 0, "", "^BGYou captured the {0}flag!");

        // enemy got our flag (their team is the arg name)
        Notifications.Center("CTF_PICKUP_ENEMY_NEUTRAL", 1, 0, "s1", "CPID_CTF_LOWPRIO",
            "^BGThe %senemy^BG got the flag! Retrieve it!", enabled: false);
        for (int i = 0; i < 4; i++)
            Notifications.Center("CTF_PICKUP_ENEMY_" + TeamSuffix[i], 1, 0, "s1", "CPID_CTF_LOWPRIO",
                "^BGThe %senemy^BG got your flag! Retrieve it!", enabled: false);

        // pass / receive
        Notifications.Center("CTF_PASS_SENT_NEUTRAL", 1, 0, "s1", "CPID_CTF_PASS",
            "^BGYou passed the flag to %s", enabled: false);
        Notifications.Center("CTF_PASS_RECEIVED_NEUTRAL", 1, 0, "s1", "CPID_CTF_PASS",
            "^BGYou received the flag from %s", enabled: false);
        Notifications.Center("CTF_PASS_REQUESTING", 1, 0, "s1", "CPID_CTF_PASS",
            "^BG%s^BG requests you to pass the flag%s", enabled: false);

        Notifications.Center("CTF_CAPTURESHIELD_SHIELDED", 0, 0, "", "CPID_CTF_CAPSHIELD",
            "^BGYou are now ^F1shielded^BG from the flag(s)\n^BGfor ^F2too many unsuccessful attempts^BG to capture.\n^BGMake some defensive scores before trying again.", enabled: false);
        Notifications.Center("CTF_FLAG_RETURNED", 0, 0, "", "CPID_CTF_LOWPRIO",
            "^BGThe flag has returned to the base", enabled: false);
    }

    private static void CenterCtfTeamed(string baseName, int s, int f, string args, string normalFmt)
    {
        Notifications.Center(baseName + "_NEUTRAL", s, f, args, "CPID_CTF_LOWPRIO",
            string.Format(normalFmt, ""), enabled: false);
        string[] colored = { "^1RED ", "^4BLUE ", "^3YELLOW ", "^6PINK " };
        for (int i = 0; i < 4; i++)
            Notifications.Center(baseName + "_" + TeamSuffix[i], s, f, args, "CPID_CTF_LOWPRIO",
                string.Format(normalFmt, colored[i]), enabled: false);
    }

    // =====================================================================================
    //  MSG_CHOICE — pick optiona vs optionb from a per-client choice value (verbose/terse).
    // =====================================================================================
    private static void RegisterChoice()
    {
        // Choice(name, allowed, chType, optionA bare-name, optionB bare-name).
        // A_ALWAYS == 2 (always allowed), A_WARMUP == 1 (warmup only).
        Notifications.Choice("FRAG", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAG", "DEATH_MURDER_FRAG_VERBOSE");
        Notifications.Choice("FRAGGED", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAGGED", "DEATH_MURDER_FRAGGED_VERBOSE");
        Notifications.Choice("TYPEFRAG", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_TYPEFRAG", "DEATH_MURDER_TYPEFRAG_VERBOSE");
        Notifications.Choice("TYPEFRAGGED", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_TYPEFRAGGED", "DEATH_MURDER_TYPEFRAGGED_VERBOSE");

        Notifications.Choice("CTF_PICKUP_ENEMY_NEUTRAL", NotifAllowed.Always, MsgType.Center,
            "CTF_PICKUP_ENEMY_NEUTRAL", "CTF_PICKUP_ENEMY_NEUTRAL");
    }

    // =====================================================================================
    //  MSG_MULTI — bundles that fan out to announcer + info + center
    // =====================================================================================
    private static void RegisterMulti()
    {
        // Self deaths: info + matching center.
        foreach (var n in new[]
        {
            "GENERIC", "SUICIDE", "CHEAT", "FALL", "DROWN", "LAVA", "FIRE", "SLIME", "SWAMP", "VOID",
            "CAMP", "BETRAYAL", "NOAMMO", "ROT", "SHOOTING_STAR", "TOUCHEXPLODE",
            "NADE", "NADE_DARKNESS", "NADE_HEAL", "NADE_ICE", "NADE_NAPALM",
        })
        {
            // Center sub-notif (QC MSG_MULTI_NOTIF centername): usually CENTER_DEATH_SELF_<n>, but the
            // NADE_DARKNESS / NADE_ICE variants share the generic CENTER_DEATH_SELF_NADE centerprint
            // (all.inc lines for DEATH_SELF_NADE_DARKNESS / _NADE_ICE), so map those two accordingly.
            string center = (n == "NADE_DARKNESS" || n == "NADE_ICE") ? "DEATH_SELF_NADE" : "DEATH_SELF_" + n;
            Notifications.Multi("DEATH_SELF_" + n, null, "DEATH_SELF_" + n, center);
        }
        Notifications.Multi("DEATH_SELF_TEAMCHANGE", null, "DEATH_SELF_TEAMCHANGE", "DEATH_SELF_TEAMCHANGE");
        Notifications.Multi("DEATH_SELF_AUTOTEAMCHANGE", null, "DEATH_SELF_AUTOTEAMCHANGE", "DEATH_SELF_AUTOTEAMCHANGE");

        // Murders: info only (the center half is delivered via the FRAG/FRAGGED choices, not the multi).
        foreach (var n in new[]
        {
            "FRAG", "CHEAT", "DROWN", "FALL", "FIRE", "LAVA", "SLIME", "SWAMP", "VOID", "MONSTER",
            "SHOOTING_STAR", "TELEFRAG", "TOUCHEXPLODE", "NADE", "NADE_DARKNESS", "NADE_HEAL",
            "NADE_ICE", "NADE_NAPALM", "BUFF_INFERNO", "BUFF_VENGEANCE",
            "VH_BUMB_DEATH", "VH_BUMB_GUN", "VH_CRUSH", "VH_RAPT_BOMB", "VH_RAPT_CANNON", "VH_RAPT_DEATH",
            "VH_SPID_DEATH", "VH_SPID_MINIGUN", "VH_SPID_ROCKET", "VH_WAKI_DEATH", "VH_WAKI_GUN", "VH_WAKI_ROCKET",
        })
            Notifications.Multi("DEATH_MURDER_" + n, null, "DEATH_MURDER_" + n, null);

        // CTF bundles (info + center) — neutral + per-team.
        foreach (var t in new[] { "NEUTRAL", "RED", "BLUE", "YELLOW", "PINK" })
        {
            Notifications.Multi("CTF_CAPTURE_" + t, null, "CTF_CAPTURE_" + t, "CTF_CAPTURE_" + t);
            Notifications.Multi("CTF_PICKUP_" + t, null, "CTF_PICKUP_" + t, "CTF_PICKUP_" + t);
        }

        // items
        Notifications.Multi("ITEM_WEAPON_GOT", null, "ITEM_WEAPON_GOT", "ITEM_WEAPON_GOT");
        Notifications.Multi("ITEM_BUFF_GOT", null, "ITEM_BUFF_GOT", "ITEM_BUFF_GOT");

        // a pure-announcer bundle is common too (announce + center cue).
        Notifications.Multi("BEGIN", "BEGIN", null, "COUNTDOWN_BEGIN");
    }

    // =====================================================================================
    //  Remaining all.inc entries ported verbatim (machine-generated to guarantee 1:1 fidelity).
    //  Counts: INFO=196, CENTER=228, MULTI=158, CHOICE=28.
    // =====================================================================================
    private static void RegisterGeneratedInfo()
    {
        Notifications.Info("CHAT_DISABLED", 0, 0, "", "", "^F4NOTE: ^BGChat is currently disabled on this server", "");
        Notifications.Info("CHAT_NOSPECTATORS", 0, 0, "", "", "^F4NOTE: ^BGSpectator chat is not sent to players during the match", "");
        Notifications.Info("CHAT_PRIVATE_DISABLED", 0, 0, "", "", "^F4NOTE: ^BGPrivate chat is currently disabled on this server", "");
        Notifications.Info("CHAT_SPECTATOR_DISABLED", 0, 0, "", "", "^F4NOTE: ^BGSpectator chat is currently disabled on this server", "");
        Notifications.Info("CHAT_TEAM_DISABLED", 0, 0, "", "", "^F4NOTE: ^BGTeam chat is currently disabled on this server", "");
        Notifications.Info("CTF_CAPTURE_BROKEN_RED", 2, 2, "s1 f1dtime s2 f2dtime", "notify_red_captured", "^BG%s^BG captured the ^1RED^BG flag in ^F1%s^BG seconds, breaking ^BG%s^BG's previous record of ^F2%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_BROKEN_BLUE", 2, 2, "s1 f1dtime s2 f2dtime", "notify_blue_captured", "^BG%s^BG captured the ^4BLUE^BG flag in ^F1%s^BG seconds, breaking ^BG%s^BG's previous record of ^F2%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_BROKEN_YELLOW", 2, 2, "s1 f1dtime s2 f2dtime", "notify_yellow_captured", "^BG%s^BG captured the ^3YELLOW^BG flag in ^F1%s^BG seconds, breaking ^BG%s^BG's previous record of ^F2%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_BROKEN_PINK", 2, 2, "s1 f1dtime s2 f2dtime", "notify_pink_captured", "^BG%s^BG captured the ^6PINK^BG flag in ^F1%s^BG seconds, breaking ^BG%s^BG's previous record of ^F2%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_TIME_RED", 1, 1, "s1 f1dtime", "notify_red_captured", "^BG%s^BG captured the ^1RED^BG flag in ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_TIME_BLUE", 1, 1, "s1 f1dtime", "notify_blue_captured", "^BG%s^BG captured the ^4BLUE^BG flag in ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_TIME_YELLOW", 1, 1, "s1 f1dtime", "notify_yellow_captured", "^BG%s^BG captured the ^3YELLOW^BG flag in ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_TIME_PINK", 1, 1, "s1 f1dtime", "notify_pink_captured", "^BG%s^BG captured the ^6PINK^BG flag in ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_UNBROKEN_RED", 2, 2, "s1 f1dtime s2 f2dtime", "notify_red_captured", "^BG%s^BG captured the ^1RED^BG flag in ^F2%s^BG seconds, failing to break ^BG%s^BG's previous record of ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_UNBROKEN_BLUE", 2, 2, "s1 f1dtime s2 f2dtime", "notify_blue_captured", "^BG%s^BG captured the ^4BLUE^BG flag in ^F2%s^BG seconds, failing to break ^BG%s^BG's previous record of ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_UNBROKEN_YELLOW", 2, 2, "s1 f1dtime s2 f2dtime", "notify_yellow_captured", "^BG%s^BG captured the ^3YELLOW^BG flag in ^F2%s^BG seconds, failing to break ^BG%s^BG's previous record of ^F1%s^BG seconds", "");
        Notifications.Info("CTF_CAPTURE_UNBROKEN_PINK", 2, 2, "s1 f1dtime s2 f2dtime", "notify_pink_captured", "^BG%s^BG captured the ^6PINK^BG flag in ^F2%s^BG seconds, failing to break ^BG%s^BG's previous record of ^F1%s^BG seconds", "");
        Notifications.Info("CTF_FLAGRETURN_ABORTRUN_RED", 0, 0, "", "", "^BGThe ^1RED^BG flag was returned to base by its owner", "");
        Notifications.Info("CTF_FLAGRETURN_ABORTRUN_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG flag was returned to base by its owner", "");
        Notifications.Info("CTF_FLAGRETURN_ABORTRUN_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG flag was returned to base by its owner", "");
        Notifications.Info("CTF_FLAGRETURN_ABORTRUN_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG flag was returned to base by its owner", "");
        Notifications.Info("CTF_FLAGRETURN_DAMAGED_RED", 0, 0, "", "", "^BGThe ^1RED^BG flag was destroyed and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_DAMAGED_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG flag was destroyed and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_DAMAGED_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG flag was destroyed and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_DAMAGED_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG flag was destroyed and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_DROPPED_RED", 0, 0, "", "", "^BGThe ^1RED^BG flag was dropped in the base and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_DROPPED_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG flag was dropped in the base and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_DROPPED_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG flag was dropped in the base and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_DROPPED_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG flag was dropped in the base and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_NEEDKILL_RED", 0, 0, "", "", "^BGThe ^1RED^BG flag fell somewhere it couldn't be reached and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_NEEDKILL_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG flag fell somewhere it couldn't be reached and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_NEEDKILL_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG flag fell somewhere it couldn't be reached and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_NEEDKILL_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG flag fell somewhere it couldn't be reached and returned to base", "");
        Notifications.Info("CTF_FLAGRETURN_SPEEDRUN_RED", 0, 1, "f1dtime", "", "^BGThe ^1RED^BG flag became impatient after ^F1%.2f^BG seconds and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_SPEEDRUN_BLUE", 0, 1, "f1dtime", "", "^BGThe ^4BLUE^BG flag became impatient after ^F1%.2f^BG seconds and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_SPEEDRUN_YELLOW", 0, 1, "f1dtime", "", "^BGThe ^3YELLOW^BG flag became impatient after ^F1%.2f^BG seconds and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_SPEEDRUN_PINK", 0, 1, "f1dtime", "", "^BGThe ^6PINK^BG flag became impatient after ^F1%.2f^BG seconds and returned itself", "");
        Notifications.Info("CTF_FLAGRETURN_TIMEOUT_RED", 0, 0, "", "", "^BGThe ^1RED^BG flag has returned to the base", "");
        Notifications.Info("CTF_FLAGRETURN_TIMEOUT_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG flag has returned to the base", "");
        Notifications.Info("CTF_FLAGRETURN_TIMEOUT_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG flag has returned to the base", "");
        Notifications.Info("CTF_FLAGRETURN_TIMEOUT_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG flag has returned to the base", "");
        Notifications.Info("CTF_RETURN_RED", 1, 0, "s1", "notify_red_returned", "^BG%s^BG returned the ^1RED^BG flag", "");
        Notifications.Info("CTF_RETURN_BLUE", 1, 0, "s1", "notify_blue_returned", "^BG%s^BG returned the ^4BLUE^BG flag", "");
        Notifications.Info("CTF_RETURN_YELLOW", 1, 0, "s1", "notify_yellow_returned", "^BG%s^BG returned the ^3YELLOW^BG flag", "");
        Notifications.Info("CTF_RETURN_PINK", 1, 0, "s1", "notify_pink_returned", "^BG%s^BG returned the ^6PINK^BG flag", "");
        Notifications.Info("CTF_RETURN_MONSTER_RED", 1, 0, "s1", "notify_red_returned", "^BG%s^BG returned the ^1RED^BG flag", "");
        Notifications.Info("CTF_RETURN_MONSTER_BLUE", 1, 0, "s1", "notify_blue_returned", "^BG%s^BG returned the ^4BLUE^BG flag", "");
        Notifications.Info("CTF_RETURN_MONSTER_YELLOW", 1, 0, "s1", "notify_yellow_returned", "^BG%s^BG returned the ^3YELLOW^BG flag", "");
        Notifications.Info("CTF_RETURN_MONSTER_PINK", 1, 0, "s1", "notify_pink_returned", "^BG%s^BG returned the ^6PINK^BG flag", "");
        Notifications.Info("COINTOSS", 1, 0, "s1", "", "^F2Throwing coin... Result: %s^F2!", "");
        Notifications.Info("JETPACK_NOFUEL", 0, 0, "", "", "^BGYou don't have any fuel for the ^F1Jetpack", "");
        Notifications.Info("SUPERSPEC_MISSING_UID", 0, 0, "", "", "^F2You lack a UID, superspec options will not be saved/restored", "");
        Notifications.Info("CA_JOIN_LATE", 0, 0, "", "", "^F1Round already started, you will join the game in the next round", "");
        Notifications.Info("CA_LEAVE", 0, 0, "", "", "^F2You will spectate in the next round", "");
        Notifications.Info("COUNTDOWN_RESTART", 0, 0, "", "", "^F2Match is restarting...", "");
        Notifications.Info("COUNTDOWN_STOP_MINPLAYERS", 0, 1, "f1", "", "^F4Countdown stopped!\n^BG%s players are needed for this match.", "");
        Notifications.Info("COUNTDOWN_STOP_BADTEAMS", 0, 0, "", "", "^F4Countdown stopped!\n^BGTeams are too unbalanced.", "");
        Notifications.Info("DEATH_MURDER_VOID_ENT", 4, 2, "spree_inf s1 s3#s2 #s2 s4loc spree_end", "notify_void", "^BG%s%s^K1 %s%s%s%s", "");
        Notifications.Info("DEATH_SELF_CUSTOM", 3, 1, "s1 s2 s3loc spree_lost", "notify_void", "^BG%s^K1 %s^K1%s%s", "");
        Notifications.Info("DEATH_SELF_VOID_ENT", 3, 1, "s1 s2 s3loc spree_lost", "notify_void", "^BG%s^K1 %s%s%s", "");
        Notifications.Info("DEATH_TEAMKILL_RED", 3, 1, "s1 s2 s3loc spree_end", "notify_teamkill_red", "^BG%s^K1 was betrayed by ^BG%s^K1%s%s", "");
        Notifications.Info("DEATH_TEAMKILL_BLUE", 3, 1, "s1 s2 s3loc spree_end", "notify_teamkill_blue", "^BG%s^K1 was betrayed by ^BG%s^K1%s%s", "");
        Notifications.Info("DEATH_TEAMKILL_YELLOW", 3, 1, "s1 s2 s3loc spree_end", "notify_teamkill_yellow", "^BG%s^K1 was betrayed by ^BG%s^K1%s%s", "");
        Notifications.Info("DEATH_TEAMKILL_PINK", 3, 1, "s1 s2 s3loc spree_end", "notify_teamkill_pink", "^BG%s^K1 was betrayed by ^BG%s^K1%s%s", "");
        Notifications.Info("DOMINATION_CAPTURE_TIME", 2, 2, "s1 s2 f1points f2", "", "^BG%s^BG%s^BG (%s every %s seconds)", "");
        Notifications.Info("FREEZETAG_FREEZE", 2, 0, "s1 s2", "", "^BG%s^K1 was frozen by ^BG%s", "");
        Notifications.Info("FREEZETAG_REVIVED", 2, 0, "s1 s2", "", "^BG%s^K3 was revived by ^BG%s", "");
        Notifications.Info("FREEZETAG_REVIVED_FALL", 1, 0, "s1", "", "^BG%s^K3 was revived by falling", "");
        Notifications.Info("FREEZETAG_REVIVED_NADE", 1, 0, "s1", "", "^BG%s^K3 was revived by their Nade explosion", "");
        Notifications.Info("FREEZETAG_AUTO_REVIVED", 1, 1, "s1 f1", "", "^BG%s^K3 was automatically revived after %s seconds", "");
        Notifications.Info("FREEZETAG_SELF", 1, 0, "s1", "", "^BG%s^K1 froze themselves", "");
        Notifications.Info("ROUND_TEAM_WIN_RED", 0, 0, "", "", "^1RED^BG team wins the round", "");
        Notifications.Info("ROUND_TEAM_WIN_BLUE", 0, 0, "", "", "^4BLUE^BG team wins the round", "");
        Notifications.Info("ROUND_TEAM_WIN_YELLOW", 0, 0, "", "", "^3YELLOW^BG team wins the round", "");
        Notifications.Info("ROUND_TEAM_WIN_PINK", 0, 0, "", "", "^6PINK^BG team wins the round", "");
        Notifications.Info("ROUND_PLAYER_WIN", 1, 0, "s1", "", "^BG%s^BG wins the round", "");
        Notifications.Info("ROUND_TIED", 0, 0, "", "", "^BGRound tied", "");
        Notifications.Info("ROUND_OVER", 0, 0, "", "", "^BGRound over, there's no winner", "");
        Notifications.Info("GODMODE_OFF", 0, 1, "f1", "", "^BGGodmode saved you %s units of damage, cheater!", "");
        Notifications.Info("ITEM_BUFF", 1, 1, "s1 item_buffname", "", "^BG%s^BG got the %s^BG buff!", "");
        Notifications.Info("ITEM_BUFF_DROP", 0, 1, "item_buffname", "", "^BGYou dropped the %s^BG buff!", "");
        Notifications.Info("ITEM_WEAPON_UNAVAILABLE", 0, 1, "item_wepname", "", "^F1%s^BG is ^F4not available^BG on this map", "", enabled: false);
        Notifications.Info("CONNECTING", 1, 0, "s1", "", "^BG%s^BG is connecting...", "");
        Notifications.Info("JOIN_CONNECT", 1, 0, "s1", "", "^BG%s^F3 connected", "");
        Notifications.Info("JOIN_PLAY", 1, 0, "s1", "", "^BG%s^F3 is now playing", "");
        Notifications.Info("JOIN_PLAY_TEAM_RED", 1, 0, "s1", "", "^BG%s^F3 is now playing on the ^1RED team", "");
        Notifications.Info("JOIN_PLAY_TEAM_BLUE", 1, 0, "s1", "", "^BG%s^F3 is now playing on the ^4BLUE team", "");
        Notifications.Info("JOIN_PLAY_TEAM_YELLOW", 1, 0, "s1", "", "^BG%s^F3 is now playing on the ^3YELLOW team", "");
        Notifications.Info("JOIN_PLAY_TEAM_PINK", 1, 0, "s1", "", "^BG%s^F3 is now playing on the ^6PINK team", "");
        Notifications.Info("JOIN_WANTS_TEAM_RED", 1, 0, "s1", "", "^BG%s^F3 wants to play on the ^1RED team", "");
        Notifications.Info("JOIN_WANTS_TEAM_BLUE", 1, 0, "s1", "", "^BG%s^F3 wants to play on the ^4BLUE team", "");
        Notifications.Info("JOIN_WANTS_TEAM_YELLOW", 1, 0, "s1", "", "^BG%s^F3 wants to play on the ^3YELLOW team", "");
        Notifications.Info("JOIN_WANTS_TEAM_PINK", 1, 0, "s1", "", "^BG%s^F3 wants to play on the ^6PINK team", "");
        Notifications.Info("JOIN_WANTS", 1, 0, "s1", "", "^BG%s^F3 wants to play", "");
        Notifications.Info("KEEPAWAY_DROPPED", 1, 0, "s1", "notify_balldropped", "^BG%s^BG has dropped the ball!", "");
        Notifications.Info("KEEPAWAY_PICKUP", 1, 0, "s1", "notify_ballpickedup", "^BG%s^BG has picked up the ball!", "");
        Notifications.Info("KEYHUNT_CAPTURE_RED", 1, 0, "s1", "", "^BG%s^BG captured the keys for the ^1RED team", "");
        Notifications.Info("KEYHUNT_CAPTURE_BLUE", 1, 0, "s1", "", "^BG%s^BG captured the keys for the ^4BLUE team", "");
        Notifications.Info("KEYHUNT_CAPTURE_YELLOW", 1, 0, "s1", "", "^BG%s^BG captured the keys for the ^3YELLOW team", "");
        Notifications.Info("KEYHUNT_CAPTURE_PINK", 1, 0, "s1", "", "^BG%s^BG captured the keys for the ^6PINK team", "");
        Notifications.Info("KEYHUNT_DROP_RED", 1, 0, "s1", "", "^BG%s^BG dropped the ^1RED Key", "");
        Notifications.Info("KEYHUNT_DROP_BLUE", 1, 0, "s1", "", "^BG%s^BG dropped the ^4BLUE Key", "");
        Notifications.Info("KEYHUNT_DROP_YELLOW", 1, 0, "s1", "", "^BG%s^BG dropped the ^3YELLOW Key", "");
        Notifications.Info("KEYHUNT_DROP_PINK", 1, 0, "s1", "", "^BG%s^BG dropped the ^6PINK Key", "");
        Notifications.Info("KEYHUNT_LOST_RED", 1, 0, "s1", "", "^BG%s^BG lost the ^1RED Key", "");
        Notifications.Info("KEYHUNT_LOST_BLUE", 1, 0, "s1", "", "^BG%s^BG lost the ^4BLUE Key", "");
        Notifications.Info("KEYHUNT_LOST_YELLOW", 1, 0, "s1", "", "^BG%s^BG lost the ^3YELLOW Key", "");
        Notifications.Info("KEYHUNT_LOST_PINK", 1, 0, "s1", "", "^BG%s^BG lost the ^6PINK Key", "");
        Notifications.Info("KEYHUNT_PUSHED_RED", 2, 0, "s1 s2", "", "^BG%s^BG pushed %s^BG causing the ^1RED Key ^BGdestruction", "");
        Notifications.Info("KEYHUNT_PUSHED_BLUE", 2, 0, "s1 s2", "", "^BG%s^BG pushed %s^BG causing the ^4BLUE Key ^BGdestruction", "");
        Notifications.Info("KEYHUNT_PUSHED_YELLOW", 2, 0, "s1 s2", "", "^BG%s^BG pushed %s^BG causing the ^3YELLOW Key ^BGdestruction", "");
        Notifications.Info("KEYHUNT_PUSHED_PINK", 2, 0, "s1 s2", "", "^BG%s^BG pushed %s^BG causing the ^6PINK Key ^BGdestruction", "");
        Notifications.Info("KEYHUNT_DESTROYED_RED", 1, 0, "s1", "", "^BG%s^BG destroyed the ^1RED Key", "");
        Notifications.Info("KEYHUNT_DESTROYED_BLUE", 1, 0, "s1", "", "^BG%s^BG destroyed the ^4BLUE Key", "");
        Notifications.Info("KEYHUNT_DESTROYED_YELLOW", 1, 0, "s1", "", "^BG%s^BG destroyed the ^3YELLOW Key", "");
        Notifications.Info("KEYHUNT_DESTROYED_PINK", 1, 0, "s1", "", "^BG%s^BG destroyed the ^6PINK Key", "");
        Notifications.Info("KEYHUNT_PICKUP_RED", 1, 0, "s1", "", "^BG%s^BG picked up the ^1RED Key", "");
        Notifications.Info("KEYHUNT_PICKUP_BLUE", 1, 0, "s1", "", "^BG%s^BG picked up the ^4BLUE Key", "");
        Notifications.Info("KEYHUNT_PICKUP_YELLOW", 1, 0, "s1", "", "^BG%s^BG picked up the ^3YELLOW Key", "");
        Notifications.Info("KEYHUNT_PICKUP_PINK", 1, 0, "s1", "", "^BG%s^BG picked up the ^6PINK Key", "");
        Notifications.Info("LMS_NOLIVES", 1, 0, "s1", "", "^BG%s^F3 has no more lives left", "");
        Notifications.Info("MONSTERS_DISABLED", 0, 0, "", "", "^BGMonsters are currently disabled", "");
        Notifications.Info("NEXBALL_RETURN_HELD_RED", 0, 0, "", "", "^BGThe ^1RED^BG team held the ball for too long", "");
        Notifications.Info("NEXBALL_RETURN_HELD_BLUE", 0, 0, "", "", "^BGThe ^4BLUE^BG team held the ball for too long", "");
        Notifications.Info("NEXBALL_RETURN_HELD_YELLOW", 0, 0, "", "", "^BGThe ^3YELLOW^BG team held the ball for too long", "");
        Notifications.Info("NEXBALL_RETURN_HELD_PINK", 0, 0, "", "", "^BGThe ^6PINK^BG team held the ball for too long", "");
        // QC GoalTouch bprint announcements (sv_nexball.qc:390-414) ported as INFO lines (the port collapses
        // server bprints to INFO, like Domination). s1 = the scorer's netname. The team name is baked into the
        // per-team variant (the established KEYHUNT/ONSLAUGHT/CTF convention), matching Team_ColoredFullName.
        Notifications.Info("NEXBALL_GOAL_RED", 1, 0, "s1", "", "^BGGoaaaaal! ^BG%s^BG scored a point for the ^1RED^BG team", "");
        Notifications.Info("NEXBALL_GOAL_BLUE", 1, 0, "s1", "", "^BGGoaaaaal! ^BG%s^BG scored a point for the ^4BLUE^BG team", "");
        Notifications.Info("NEXBALL_GOAL_YELLOW", 1, 0, "s1", "", "^BGGoaaaaal! ^BG%s^BG scored a point for the ^3YELLOW^BG team", "");
        Notifications.Info("NEXBALL_GOAL_PINK", 1, 0, "s1", "", "^BGGoaaaaal! ^BG%s^BG scored a point for the ^6PINK^BG team", "");
        // QC own-goal: "Boo! <name>^7 scored a goal against their own team!"
        Notifications.Info("NEXBALL_OWNGOAL", 1, 0, "s1", "", "^BGBoo! ^BG%s^BG scored a goal against their own team!", "");
        // QC fault (two-team): "<otherteam> gets a point due to <name>^7's silliness." The team is the one that GAINS.
        Notifications.Info("NEXBALL_FAULT_RED", 1, 0, "s1", "", "^1RED^BG gets a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_BLUE", 1, 0, "s1", "", "^4BLUE^BG gets a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_YELLOW", 1, 0, "s1", "", "^3YELLOW^BG gets a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_PINK", 1, 0, "s1", "", "^6PINK^BG gets a point due to ^BG%s^BG's silliness", "");
        // QC fault (>2-team): "<ballteam> loses a point due to <name>^7's silliness." The team is the one that LOSES.
        Notifications.Info("NEXBALL_FAULT_LOSE_RED", 1, 0, "s1", "", "^1RED^BG loses a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_LOSE_BLUE", 1, 0, "s1", "", "^4BLUE^BG loses a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_LOSE_YELLOW", 1, 0, "s1", "", "^3YELLOW^BG loses a point due to ^BG%s^BG's silliness", "");
        Notifications.Info("NEXBALL_FAULT_LOSE_PINK", 1, 0, "s1", "", "^6PINK^BG loses a point due to ^BG%s^BG's silliness", "");
        // QC out: a carried ball out-of-bounds names the carrier; a loose ball just "was returned".
        Notifications.Info("NEXBALL_OUT_PLAYER", 1, 0, "s1", "", "^BG%s^BG went out of bounds", "");
        Notifications.Info("NEXBALL_OUT", 0, 0, "", "", "^BGThe ball was returned", "");
        Notifications.Info("ONSLAUGHT_CAPTURE", 2, 0, "s1 s2", "", "^BG%s^BG captured %s^BG control point", "");
        Notifications.Info("ONSLAUGHT_CAPTURE_NONAME", 1, 0, "s1", "", "^BG%s^BG captured a control point", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_RED", 2, 0, "s1 s2", "", "^1RED^BG team %s^BG control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_BLUE", 2, 0, "s1 s2", "", "^4BLUE^BG team %s^BG control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_YELLOW", 2, 0, "s1 s2", "", "^3YELLOW^BG team %s^BG control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_PINK", 2, 0, "s1 s2", "", "^6PINK^BG team %s^BG control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_NONAME_RED", 1, 0, "s1", "", "^1RED^BG team control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_NONAME_BLUE", 1, 0, "s1", "", "^4BLUE^BG team control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_NONAME_YELLOW", 1, 0, "s1", "", "^3YELLOW^BG team control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_CPDESTROYED_NONAME_PINK", 1, 0, "s1", "", "^6PINK^BG team control point has been destroyed by %s", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_RED", 0, 0, "", "", "^1RED^BG generator has been destroyed", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_BLUE", 0, 0, "", "", "^4BLUE^BG generator has been destroyed", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_YELLOW", 0, 0, "", "", "^3YELLOW^BG generator has been destroyed", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_PINK", 0, 0, "", "", "^6PINK^BG generator has been destroyed", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_OVERTIME_RED", 0, 0, "", "", "^1RED^BG generator spontaneously combusted due to overtime!", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_OVERTIME_BLUE", 0, 0, "", "", "^4BLUE^BG generator spontaneously combusted due to overtime!", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_OVERTIME_YELLOW", 0, 0, "", "", "^3YELLOW^BG generator spontaneously combusted due to overtime!", "");
        Notifications.Info("ONSLAUGHT_GENDESTROYED_OVERTIME_PINK", 0, 0, "", "", "^6PINK^BG generator spontaneously combusted due to overtime!", "");
        Notifications.Info("POWERUP_INVISIBILITY", 1, 0, "s1", "buff_invisible", "^BG%s^K1 picked up Invisibility", "");
        Notifications.Info("POWERUP_SHIELD", 1, 0, "s1", "shield", "^BG%s^K1 picked up Shield", "");
        Notifications.Info("POWERUP_SPEED", 1, 0, "s1", "buff_speed", "^BG%s^K1 picked up Speed", "");
        Notifications.Info("POWERUP_STRENGTH", 1, 0, "s1", "strength", "^BG%s^K1 picked up Strength", "");
        Notifications.Info("QUIT_DISCONNECT", 1, 0, "s1", "", "^BG%s^F3 disconnected", "");
        Notifications.Info("QUIT_KICK_IDLING", 1, 1, "s1 f1", "", "^BG%s^F3 was kicked after idling for %s seconds", "");
        Notifications.Info("MOVETOSPEC_IDLING", 1, 1, "s1 f1", "", "^BG%s^F3 was moved to^BG spectators^F3 after idling for %s seconds", "");
        Notifications.Info("MOVETOSPEC_IDLING_QUEUE", 1, 1, "s1 f1", "", "^BG%s^F3 has left the join queue after idling for %s seconds", "");
        Notifications.Info("MOVETOSPEC_REMOVE", 1, 0, "s1", "", "^BG%s^F3 was moved to^BG spectators^F3 for balance reasons", "");
        Notifications.Info("QUIT_KICK_SPECTATING", 0, 0, "", "", "^F2You were kicked from the server because you are a spectator and spectators aren't allowed at the moment.", "");
        Notifications.Info("QUIT_KICK_TEAMKILL", 1, 0, "s1", "", "^BG%s^F3 was kicked for excessive teamkilling", "");
        Notifications.Info("QUIT_PLAYBAN_TEAMKILL", 1, 0, "s1", "", "^BG%s^F3 was forced to spectate for excessive teamkilling", "");
        Notifications.Info("QUIT_SPECTATE", 1, 0, "s1", "", "^BG%s^F3 is now^BG spectating", "");
        Notifications.Info("QUIT_QUEUE", 1, 0, "s1", "", "^BG%s^F3 has left the join queue", "");
        Notifications.Info("RACE_ABANDONED", 1, 0, "s1", "", "^BG%s^BG has abandoned the race", "");
        Notifications.Info("RACE_FAIL_RANKED", 1, 3, "s1 race_col f1ord race_col f3race_time race_diff", "race_newfail", "^BG%s^BG couldn't break their %s%s^BG place record of %s%s %s", "");
        Notifications.Info("RACE_FAIL_UNRANKED", 1, 3, "s1 race_col f1ord race_col f3race_time race_diff", "race_newfail", "^BG%s^BG couldn't break the %s%s^BG place record of %s%s %s", "");
        Notifications.Info("RACE_FINISHED", 1, 0, "s1", "", "^BG%s^BG has finished the race", "");
        Notifications.Info("RACE_NEW_BROKEN", 2, 3, "s1 s2 race_col f1ord race_col f2race_time race_diff", "race_newrankyellow", "^BG%s^BG broke %s^BG's %s%s^BG place record with %s%s %s", "");
        Notifications.Info("RACE_NEW_IMPROVED", 1, 3, "s1 race_col f1ord race_col f2race_time race_diff", "race_newtime", "^BG%s^BG improved their %s%s^BG place record with %s%s %s", "");
        Notifications.Info("RACE_NEW_MISSING_UID", 1, 1, "s1 f1race_time", "race_newfail", "^BG%s^BG scored a new record with ^F2%s^BG, but unfortunately lacks a UID and will be lost.", "");
        Notifications.Info("RACE_NEW_MISSING_NAME", 1, 1, "s1 f1race_time", "race_newfail", "^BG%s^BG scored a new record with ^F2%s^BG, but is anonymous and will be lost.", "");
        Notifications.Info("RACE_NEW_SET", 1, 2, "s1 race_col f1ord race_col f2race_time", "race_newrecordserver", "^BG%s^BG set the %s%s^BG place record with %s%s", "");
        Notifications.Info("MINIGAME_INVITE", 2, 0, "s2 minigame1_name s1", "minigames/%s/icon_notif", "^F4You have been invited by ^BG%s^F4 to join their game of ^F2%s^F4 (^F1%s^F4)", "");
        Notifications.Info("SCORES_RED", 0, 0, "", "", "^1RED ^BGteam scores!", "");
        Notifications.Info("SCORES_BLUE", 0, 0, "", "", "^4BLUE ^BGteam scores!", "");
        Notifications.Info("SCORES_YELLOW", 0, 0, "", "", "^3YELLOW ^BGteam scores!", "");
        Notifications.Info("SCORES_PINK", 0, 0, "", "", "^6PINK ^BGteam scores!", "");
        Notifications.Info("SPECTATE_WARNING", 0, 1, "f1secs", "", "^F2You have to become a player within the next %s, otherwise you will be kicked, because spectating isn't allowed at this time!", "");
        Notifications.Info("SPECTATE_NOTALLOWED", 0, 0, "", "", "^F2Spectating isn't allowed at this time!", "");
        Notifications.Info("SPECTATE_SPEC_NOTALLOWED", 0, 0, "", "", "^F2Spectating specific players isn't allowed at this time!", "");
        Notifications.Info("SUPERWEAPON_PICKUP", 1, 0, "s1", "superweapons", "^BG%s^K1 picked up a Superweapon", "");
        Notifications.Info("SURVIVAL_HUNTER_WIN", 0, 0, "", "", "^K1Hunters^BG win the round", "");
        Notifications.Info("SURVIVAL_SURVIVOR_WIN", 0, 0, "", "", "^F1Survivors^BG win the round", "");
        Notifications.Info("TEAMCHANGE_STRONGERTEAM", 0, 0, "", "", "^K2You're not allowed to join a stronger team!", "");
        Notifications.Info("TEAMCHANGE_NOTALLOWED", 0, 0, "", "", "^K2You're not allowed to join that team!", "");
        Notifications.Info("TEAMCHANGE_LOCKED", 0, 0, "", "", "^K2Teams are locked, you can't join or change teams until they're unlocked or the map changes.", "");
        Notifications.Info("TEAMCHANGE_SAME", 0, 0, "", "", "^K2You're already on that team!", "");
        Notifications.Info("TEAMS_LOCKED", 0, 0, "", "", "^F4The teams are now locked.", "");
        Notifications.Info("TEAMS_UNLOCKED", 0, 0, "", "", "^F1The teams are now unlocked.", "");
        Notifications.Info("VERSION_BETA", 2, 0, "s1 s2", "", "^F4NOTE: ^BGThe server is running ^F1Xonotic %s (beta)^BG, you have ^F2Xonotic %s", "");
        Notifications.Info("VERSION_OLD", 2, 0, "s1 s2", "", "^F4NOTE: ^BGThe server is running ^F1Xonotic %s^BG, you have ^F2Xonotic %s", "");
        Notifications.Info("VERSION_OUTDATED", 2, 0, "s1 s2", "", "^F4NOTE: ^F1Xonotic %s^BG is out, and you still have ^F2Xonotic %s^BG - get the update from ^F3https://xonotic.org^BG!", "");
        Notifications.Info("WEAPON_OVERKILL_HMG_MURDER_SPRAY", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponhmg", "^BG%s%s^K1 was torn to bits by ^BG%s^K1's Overkill Heavy MachineGun%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_MACHINEGUN_MURDER", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponuzi", "^BG%s%s^K1 was riddled full of holes by ^BG%s^K1's Overkill Machine Gun%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_NEX_MURDER", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponnex", "^BG%s%s^K1 has been vaporized by ^BG%s^K1's Overkill Nex%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_RPC_MURDER_DIRECT", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponrpc", "^BG%s%s^K1 was sawn in half by ^BG%s^K1's Overkill Rocket Propelled Chainsaw%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_RPC_MURDER_SPLASH", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponrpc", "^BG%s%s^K1 almost dodged ^BG%s^K1's Overkill Rocket Propelled Chainsaw%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_RPC_SUICIDE_DIRECT", 2, 1, "s1 s2loc spree_lost", "weaponrpc", "^BG%s^K1 was sawn in half by their own Overkill Rocket Propelled Chainsaw%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_RPC_SUICIDE_SPLASH", 2, 1, "s1 s2loc spree_lost", "weaponrpc", "^BG%s^K1 blew themselves up with their Overkill Rocket Propelled Chainsaw%s%s", "");
        Notifications.Info("WEAPON_OVERKILL_SHOTGUN_MURDER", 3, 2, "spree_inf s1 s2 s3loc spree_end", "weaponshotgun", "^BG%s%s^K1 was gunned down by ^BG%s^K1's Overkill Shotgun%s%s", "");
        Notifications.Info("WEAPON_THINKING_WITH_PORTALS", 2, 1, "s1 s2loc spree_lost", "notify_selfkill", "^BG%s^K1 is now thinking with portals%s%s", "");
    }

    private static void RegisterGeneratedCenter()
    {
        Notifications.Center("ALONE", 0, 0, "", "", "^F4You are now alone!", "");
        Notifications.Center("ASSAULT_ATTACKING", 0, 0, "", "CPID_ASSAULT_ROLE", "^BGYou are attacking!", "");
        Notifications.Center("ASSAULT_DEFENDING", 0, 0, "", "CPID_ASSAULT_ROLE", "^BGYou are defending!", "");
        Notifications.Center("ASSAULT_OBJ_DESTROYED", 0, 1, "f1time", "CPID_ASSAULT_ROLE", "^BGObjective destroyed in ^F4%s^BG!", "");
        Notifications.Center("COUNTDOWN_STOP_BADTEAMS", 0, 0, "", "CPID_MISSING_PLAYERS", "^BOLD^F4Countdown stopped!\n^BGTeams are too unbalanced.", "");
        Notifications.Center("ROUND_TIED", 0, 0, "", "CPID_ROUND", "^BGRound tied", "");
        Notifications.Center("ROUND_OVER", 0, 0, "", "CPID_ROUND", "^BGRound over, there's no winner", "");
        Notifications.Center("CAMPCHECK", 0, 0, "", "CPID_CAMPCHECK", "^F2Don't camp!", "");
        Notifications.Center("COINTOSS", 1, 0, "s1", "", "^F2Throwing coin... Result: %s^F2!", "");
        Notifications.Center("CTF_CAPTURESHIELD_FREE", 0, 0, "", "CPID_CTF_CAPSHIELD", "^BGYou are now free.\n^BGFeel free to ^F2try to capture^BG the flag again\n^BGif you think you will succeed.", "");
        Notifications.Center("CTF_CAPTURESHIELD_INACTIVE", 0, 0, "", "CPID_CTF_CAPSHIELD", "^BGThis flag is currently inactive", "");
        Notifications.Center("CTF_FLAG_THROW_PUNISH", 0, 1, "f1secs", "CPID_CTF_LOWPRIO", "^BGToo many flag throws! Throwing disabled for %s.", "");
        Notifications.Center("CTF_PASS_OTHER_RED", 2, 0, "s1 s2", "CPID_CTF_PASS", "^BG%s^BG passed the ^1RED^BG flag to %s", "");
        Notifications.Center("CTF_PASS_OTHER_BLUE", 2, 0, "s1 s2", "CPID_CTF_PASS", "^BG%s^BG passed the ^4BLUE^BG flag to %s", "");
        Notifications.Center("CTF_PASS_OTHER_YELLOW", 2, 0, "s1 s2", "CPID_CTF_PASS", "^BG%s^BG passed the ^3YELLOW^BG flag to %s", "");
        Notifications.Center("CTF_PASS_OTHER_PINK", 2, 0, "s1 s2", "CPID_CTF_PASS", "^BG%s^BG passed the ^6PINK^BG flag to %s", "");
        Notifications.Center("CTF_PASS_OTHER_NEUTRAL", 2, 0, "s1 s2", "CPID_CTF_PASS", "^BG%s^BG passed the flag to %s", "");
        Notifications.Center("CTF_PASS_RECEIVED_RED", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou received the ^1RED^BG flag from %s", "");
        Notifications.Center("CTF_PASS_RECEIVED_BLUE", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou received the ^4BLUE^BG flag from %s", "");
        Notifications.Center("CTF_PASS_RECEIVED_YELLOW", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou received the ^3YELLOW^BG flag from %s", "");
        Notifications.Center("CTF_PASS_RECEIVED_PINK", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou received the ^6PINK^BG flag from %s", "");
        Notifications.Center("CTF_PASS_REQUESTED", 1, 0, "pass_key s1", "CPID_CTF_PASS", "^BGPress ^F2%s^BG to receive the flag from %s^BG", "");
        Notifications.Center("CTF_PASS_SENT_RED", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou passed the ^1RED^BG flag to %s", "");
        Notifications.Center("CTF_PASS_SENT_BLUE", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou passed the ^4BLUE^BG flag to %s", "");
        Notifications.Center("CTF_PASS_SENT_YELLOW", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou passed the ^3YELLOW^BG flag to %s", "");
        Notifications.Center("CTF_PASS_SENT_PINK", 1, 0, "s1", "CPID_CTF_PASS", "^BGYou passed the ^6PINK^BG flag to %s", "");
        Notifications.Center("CTF_PICKUP_RETURN", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BOLD^BGYou got your %steam^BG's flag, return it!", "");
        Notifications.Center("CTF_PICKUP_RETURN_ENEMY", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BOLD^BGYou got the %senemy^BG's flag, return it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got your flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_VERBOSE", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got your flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_NEUTRAL_VERBOSE", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_TEAM", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got their flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_TEAM_VERBOSE", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got their flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_RED", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got the ^1RED^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_BLUE", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got the ^4BLUE^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_YELLOW", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got the ^3YELLOW^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_PINK", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got the ^6PINK^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_VERBOSE_RED", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the ^1RED^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_VERBOSE_BLUE", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the ^4BLUE^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_VERBOSE_YELLOW", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the ^3YELLOW^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_VERBOSE_PINK", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the ^6PINK^BG flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_NEUTRAL", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy^BG got the flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_ENEMY_OTHER_VERBOSE_NEUTRAL", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGThe %senemy (^BG%s%s)^BG got the flag! Retrieve it!", "");
        Notifications.Center("CTF_PICKUP_TEAM_RED", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate^BG got the ^1RED^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_BLUE", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate^BG got the ^4BLUE^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_YELLOW", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate^BG got the ^3YELLOW^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_PINK", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate^BG got the ^6PINK^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_VERBOSE_RED", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate (^BG%s%s)^BG got the ^1RED^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_VERBOSE_BLUE", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate (^BG%s%s)^BG got the ^4BLUE^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_VERBOSE_YELLOW", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate (^BG%s%s)^BG got the ^3YELLOW^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_VERBOSE_PINK", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate (^BG%s%s)^BG got the ^6PINK^BG flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_NEUTRAL", 1, 0, "s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate^BG got the flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_TEAM_VERBOSE_NEUTRAL", 2, 0, "s1 s2 s1", "CPID_CTF_LOWPRIO", "^BGYour %steammate (^BG%s%s)^BG got the flag! Protect them!", "");
        Notifications.Center("CTF_PICKUP_VISIBLE", 0, 0, "", "CPID_STALEMATE", "^BGEnemies can now see you on radar!", "");
        Notifications.Center("CTF_RETURN_RED", 0, 0, "", "CPID_CTF_LOWPRIO", "^BGYou returned the ^1RED^BG flag!", "");
        Notifications.Center("CTF_RETURN_BLUE", 0, 0, "", "CPID_CTF_LOWPRIO", "^BGYou returned the ^4BLUE^BG flag!", "");
        Notifications.Center("CTF_RETURN_YELLOW", 0, 0, "", "CPID_CTF_LOWPRIO", "^BGYou returned the ^3YELLOW^BG flag!", "");
        Notifications.Center("CTF_RETURN_PINK", 0, 0, "", "CPID_CTF_LOWPRIO", "^BGYou returned the ^6PINK^BG flag!", "");
        Notifications.Center("CTF_STALEMATE_CARRIER", 0, 0, "", "CPID_STALEMATE", "^BGStalemate! Enemies can now see you on radar!", "");
        Notifications.Center("CTF_STALEMATE_OTHER", 0, 0, "", "CPID_STALEMATE", "^BGStalemate! Flag carriers can now be seen by enemies on radar!", "");
        Notifications.Center("DEATH_MURDER_FRAG_FIRE", 1, 1, "spree_cen s1", "", "^BOLD^K3%sYou burned ^BG%s", "^BOLD^K3%sYou scored against ^BG%s");
        Notifications.Center("DEATH_MURDER_FRAGGED_FIRE", 1, 1, "spree_cen s1", "", "^BOLD^K1%sYou were burned by ^BG%s", "^BOLD^K1%sYou were scored against by ^BG%s");
        Notifications.Center("DEATH_MURDER_FRAGGED_FIRE_VERBOSE", 1, 4, "spree_cen s1 frag_stats", "", "^BOLD^K1%sYou were burned by ^BG%s^BG%s", "^BOLD^K1%sYou were scored against by ^BG%s^BG%s");
        Notifications.Center("DEATH_MURDER_FRAG_FIRE_VERBOSE", 1, 2, "spree_cen s1 frag_ping", "", "^BOLD^K3%sYou burned ^BG%s^BG%s", "^BOLD^K3%sYou scored against ^BG%s^BG%s");
        Notifications.Center("DEATH_MURDER_FRAG_FREEZE", 1, 1, "spree_cen s1", "", "^BOLD^K3%sYou froze ^BG%s", "^BOLD^K3%sYou scored against ^BG%s");
        Notifications.Center("DEATH_MURDER_FRAGGED_FREEZE", 1, 1, "spree_cen s1", "", "^BOLD^K1%sYou were frozen by ^BG%s", "^BOLD^K1%sYou were scored against by ^BG%s");
        Notifications.Center("DEATH_MURDER_FRAGGED_FREEZE_VERBOSE", 1, 4, "spree_cen s1 frag_stats", "", "^BOLD^K1%sYou were frozen by ^BG%s^BG%s", "^BOLD^K1%sYou were scored against by ^BG%s^BG%s");
        Notifications.Center("DEATH_MURDER_FRAG_FREEZE_VERBOSE", 1, 2, "spree_cen s1 frag_ping", "", "^BOLD^K3%sYou froze ^BG%s^BG%s", "^BOLD^K3%sYou scored against ^BG%s^BG%s");
        Notifications.Center("NADE_THROW", 0, 0, "nade_key", "CPID_NADES", "^BGPress ^F2%s^BG again to toss the nade!", "");
        Notifications.Center("NADE_BONUS", 0, 0, "", "CPID_NADES", "^F2You got a ^K1BONUS GRENADE^F2!", "");
        Notifications.Center("DEATH_SELF_CUSTOM", 2, 0, "s2", "", "^BOLD^K1You were %s", "");
        Notifications.Center("DEATH_SELF_NADE_NAPALM", 0, 0, "", "", "^BOLD^K1Hanging around a napalm explosion is bad!", "");
        Notifications.Center("DEATH_SELF_NADE_HEAL", 0, 0, "", "", "^BOLD^K1Your Healing Nade is a bit defective", "");
        Notifications.Center("DEATH_SELF_SWAMP", 0, 0, "", "", "^BOLD^K1You got stuck in a swamp!", "");
        Notifications.Center("DEATH_SELF_TOUCHEXPLODE", 0, 0, "", "", "^BOLD^K1You died in an accident!", "");
        Notifications.Center("DEATH_SELF_TURRET_EWHEEL", 0, 0, "", "", "^BOLD^K1You were fragged by an eWheel turret!", "^BOLD^K1You had an unfortunate run in with an eWheel turret!");
        Notifications.Center("DEATH_SELF_TURRET_WALK", 0, 0, "", "", "^BOLD^K1You were fragged by a Walker turret!", "^BOLD^K1You had an unfortunate run in with a Walker turret!");
        Notifications.Center("DEATH_SELF_VH_BUMB_DEATH", 0, 0, "", "", "^BOLD^K1You got caught in the blast of a Bumblebee explosion!", "");
        Notifications.Center("DEATH_SELF_VH_CRUSH", 0, 0, "", "", "^BOLD^K1You were crushed by a vehicle!", "");
        Notifications.Center("DEATH_SELF_VH_RAPT_BOMB", 0, 0, "", "", "^BOLD^K1You were caught in a Raptor cluster bomb!", "");
        Notifications.Center("DEATH_SELF_VH_RAPT_DEATH", 0, 0, "", "", "^BOLD^K1You got caught in the blast of a Raptor explosion!", "");
        Notifications.Center("DEATH_SELF_VH_SPID_DEATH", 0, 0, "", "", "^BOLD^K1You got caught in the blast of a Spiderbot explosion!", "");
        Notifications.Center("DEATH_SELF_VH_SPID_ROCKET", 0, 0, "", "", "^BOLD^K1You were blasted to bits by a Spiderbot rocket!", "");
        Notifications.Center("DEATH_SELF_VH_WAKI_DEATH", 0, 0, "", "", "^BOLD^K1You got caught in the blast of a Racer explosion!", "");
        Notifications.Center("DEATH_SELF_VH_WAKI_ROCKET", 0, 0, "", "", "^BOLD^K1You couldn't find shelter from a Racer rocket!", "");
        Notifications.Center("DEATH_TEAMKILL_FRAG", 1, 0, "s1", "", "^BOLD^K1Traitor! You team killed ^BG%s", "^BOLD^K1Traitor! You betrayed teammate ^BG%s");
        Notifications.Center("DEATH_TEAMKILL_FRAGGED", 1, 0, "s1", "", "^BOLD^K1You were team killed by ^BG%s", "^BOLD^K1You were betrayed by teammate ^BG%s");
        Notifications.Center("DISCONNECT_IDLING", 0, 1, "", "CPID_IDLING", "^BOLD^K1Stop idling!\n^BGDisconnecting in ^COUNT...", "");
        Notifications.Center("MOVETOSPEC_IDLING", 0, 1, "", "CPID_IDLING", "^BOLD^K1Stop idling!\n^BGMoving to spectators in ^COUNT...", "");
        Notifications.Center("MOVETOSPEC_REMOVE", 1, 1, "s1", "CPID_REMOVE", "^BOLD^K1Teams unbalanced!\n^BGMoving %s^BG to spectators in ^COUNT...", "");
        Notifications.Center("DOOR_LOCKED_NEED", 1, 0, "s1", "", "^BGYou need %s^BG!", "");
        Notifications.Center("DOOR_LOCKED_ALSONEED", 1, 0, "s1", "", "^BGYou also need %s^BG!", "");
        Notifications.Center("DOOR_UNLOCKED", 0, 0, "", "", "^BGDoor unlocked!", "");
        Notifications.Center("EXTRALIVES", 0, 1, "f1", "", "^F2Extra lives taken: ^K1%s", "");
        Notifications.Center("FREEZETAG_REVIVE", 1, 0, "s1", "", "^K3You revived ^BG%s", "");
        Notifications.Center("FREEZETAG_REVIVE_SELF", 0, 0, "", "", "^K3You revived yourself", "");
        Notifications.Center("FREEZETAG_REVIVED", 1, 0, "s1", "", "^K3You were revived by ^BG%s", "");
        Notifications.Center("FREEZETAG_AUTO_REVIVED", 0, 1, "f1", "", "^BGYou were automatically revived after %s seconds", "");
        Notifications.Center("GENERATOR_UNDERATTACK", 0, 0, "", "", "^BGThe generator is under attack!", "");
        Notifications.Center("ROUND_TEAM_LOSS_RED", 0, 0, "", "CPID_ROUND", "^1RED^BG team loses the round", "");
        Notifications.Center("ROUND_TEAM_LOSS_BLUE", 0, 0, "", "CPID_ROUND", "^4BLUE^BG team loses the round", "");
        Notifications.Center("ROUND_TEAM_LOSS_YELLOW", 0, 0, "", "CPID_ROUND", "^3YELLOW^BG team loses the round", "");
        Notifications.Center("ROUND_TEAM_LOSS_PINK", 0, 0, "", "CPID_ROUND", "^6PINK^BG team loses the round", "");
        Notifications.Center("ROUND_TEAM_WIN_RED", 0, 0, "", "CPID_ROUND", "^1RED^BG team wins the round", "");
        Notifications.Center("ROUND_TEAM_WIN_BLUE", 0, 0, "", "CPID_ROUND", "^4BLUE^BG team wins the round", "");
        Notifications.Center("ROUND_TEAM_WIN_YELLOW", 0, 0, "", "CPID_ROUND", "^3YELLOW^BG team wins the round", "");
        Notifications.Center("ROUND_TEAM_WIN_PINK", 0, 0, "", "CPID_ROUND", "^6PINK^BG team wins the round", "");
        Notifications.Center("ROUND_PLAYER_WIN", 1, 0, "s1", "CPID_ROUND", "^BG%s^BG wins the round", "");
        Notifications.Center("FREEZETAG_SELF", 0, 0, "", "", "^K1You froze yourself", "");
        Notifications.Center("FREEZETAG_SPAWN_LATE", 0, 0, "", "", "^K1Round already started, you spawn as frozen", "");
        Notifications.Center("INVASION_SUPERMONSTER", 1, 0, "s1", "", "^K1A %s has arrived!", "");
        Notifications.Center("ITEM_WEAPON_DROP", 0, 2, "item_wepname item_wepammo", "CPID_ITEM", "^BGYou dropped the ^F1%s^BG%s", "");
        Notifications.Center("ITEM_WEAPON_UNAVAILABLE", 0, 1, "item_wepname", "CPID_ITEM", "^F1%s^BG is ^F4not available^BG on this map", "");
        Notifications.Center("JOIN_PREVENT_VERSIONMISMATCH", 0, 0, "", "CPID_PREVENT_JOIN", "^K1Your Xonotic version is incompatible with the server's version!", "");
        Notifications.Center("JOIN_NOSPAWNS", 0, 0, "", "CPID_PREVENT_JOIN", "^K1No spawnpoints available!\nHope your team can fix it...", "");
        Notifications.Center("JOIN_PLAYBAN", 0, 0, "", "CPID_PREVENT_JOIN", "^BOLD^K1You aren't allowed to play because you are banned in this server", "");
        Notifications.Center("JOIN_PREVENT", 0, 1, "f1", "CPID_PREVENT_JOIN", "^K1You may not join the game at this time.\nThis match is limited to ^F2%s^BG players.", "");
        Notifications.Center("JOIN_PREVENT_MINIGAME", 0, 0, "", "", "^K1Cannot join given minigame session!", "");
        Notifications.Center("JOIN_PREVENT_PING", 0, 0, "", "CPID_PREVENT_JOIN", "^K2Your ping (connection latency) is currently too high to play here.\nPlease consider joining a server closer to you.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE", 0, 0, "", "CPID_PREVENT_JOIN", "^BGYou're queued to join any available team.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_RED", 0, 0, "", "CPID_PREVENT_JOIN", "^BGYou're queued to join the ^1RED^BG team.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_BLUE", 0, 0, "", "CPID_PREVENT_JOIN", "^BGYou're queued to join the ^4BLUE^BG team.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_YELLOW", 0, 0, "", "CPID_PREVENT_JOIN", "^BGYou're queued to join the ^3YELLOW^BG team.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_PINK", 0, 0, "", "CPID_PREVENT_JOIN", "^BGYou're queued to join the ^6PINK^BG team.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_CONFLICT_RED", 1, 0, "s1", "CPID_PREVENT_JOIN", "^K2You're queued to join any available team.\n%s^K2 chose ^1RED^K2 first.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_CONFLICT_BLUE", 1, 0, "s1", "CPID_PREVENT_JOIN", "^K2You're queued to join any available team.\n%s^K2 chose ^4BLUE^K2 first.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_CONFLICT_YELLOW", 1, 0, "s1", "CPID_PREVENT_JOIN", "^K2You're queued to join any available team.\n%s^K2 chose ^3YELLOW^K2 first.", "");
        Notifications.Center("JOIN_PREVENT_QUEUE_TEAM_CONFLICT_PINK", 1, 0, "s1", "CPID_PREVENT_JOIN", "^K2You're queued to join any available team.\n%s^K2 chose ^6PINK^K2 first.", "");
        Notifications.Center("JOIN_PLAY_TEAM_QUEUECONFLICT_RED", 1, 0, "s1", "", "^K2You're now playing on ^1RED^K2 team!\n%s^K2 chose your preferred team first.", "");
        Notifications.Center("JOIN_PLAY_TEAM_QUEUECONFLICT_BLUE", 1, 0, "s1", "", "^K2You're now playing on ^4BLUE^K2 team!\n%s^K2 chose your preferred team first.", "");
        Notifications.Center("JOIN_PLAY_TEAM_QUEUECONFLICT_YELLOW", 1, 0, "s1", "", "^K2You're now playing on ^3YELLOW^K2 team!\n%s^K2 chose your preferred team first.", "");
        Notifications.Center("JOIN_PLAY_TEAM_QUEUECONFLICT_PINK", 1, 0, "s1", "", "^K2You're now playing on ^6PINK^K2 team!\n%s^K2 chose your preferred team first.", "");
        Notifications.Center("JOIN_PLAY_TEAM_RED", 0, 0, "", "", "^BGYou're now playing on ^1RED^BG team!", "");
        Notifications.Center("JOIN_PLAY_TEAM_BLUE", 0, 0, "", "", "^BGYou're now playing on ^4BLUE^BG team!", "");
        Notifications.Center("JOIN_PLAY_TEAM_YELLOW", 0, 0, "", "", "^BGYou're now playing on ^3YELLOW^BG team!", "");
        Notifications.Center("JOIN_PLAY_TEAM_PINK", 0, 0, "", "", "^BGYou're now playing on ^6PINK^BG team!", "");
        Notifications.Center("KEEPAWAY_DROPPED", 1, 0, "s1", "CPID_KEEPAWAY", "^BG%s^BG has dropped the ball!", "");
        Notifications.Center("KEEPAWAY_PICKUP", 1, 0, "s1", "CPID_KEEPAWAY", "^BG%s^BG has picked up the ball!", "");
        Notifications.Center("KEEPAWAY_PICKUP_SELF", 0, 0, "", "CPID_KEEPAWAY", "^BGYou picked up the ball", "");
        Notifications.Center("KEEPAWAY_WARN", 0, 0, "", "CPID_KEEPAWAY_WARN", "^BGGet the ball to score points for frags!", "");
        Notifications.Center("KEYHUNT_HELP", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in your team's hands!\nHelp the key carriers to meet!", "");
        Notifications.Center("KEYHUNT_INTERFERE_RED", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in ^1RED team^BG's hands!\nInterfere ^F4NOW^BG!", "");
        Notifications.Center("KEYHUNT_INTERFERE_BLUE", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in ^4BLUE team^BG's hands!\nInterfere ^F4NOW^BG!", "");
        Notifications.Center("KEYHUNT_INTERFERE_YELLOW", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in ^3YELLOW team^BG's hands!\nInterfere ^F4NOW^BG!", "");
        Notifications.Center("KEYHUNT_INTERFERE_PINK", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in ^6PINK team^BG's hands!\nInterfere ^F4NOW^BG!", "");
        Notifications.Center("KEYHUNT_MEET", 0, 0, "", "CPID_KEYHUNT", "^BGAll keys are in your team's hands!\nMeet the other key carriers ^F4NOW^BG!", "");
        Notifications.Center("KEYHUNT_ROUNDSTART", 0, 1, "", "CPID_KEYHUNT_OTHER", "^F4Round will start in ^COUNT", "");
        Notifications.Center("KEYHUNT_SCAN", 0, 1, "", "CPID_KEYHUNT_OTHER", "^BGScanning frequency range...", "");
        Notifications.Center("KEYHUNT_START_RED", 0, 0, "", "CPID_KEYHUNT", "^BGYou are starting with the ^1RED Key", "");
        Notifications.Center("KEYHUNT_START_BLUE", 0, 0, "", "CPID_KEYHUNT", "^BGYou are starting with the ^4BLUE Key", "");
        Notifications.Center("KEYHUNT_START_YELLOW", 0, 0, "", "CPID_KEYHUNT", "^BGYou are starting with the ^3YELLOW Key", "");
        Notifications.Center("KEYHUNT_START_PINK", 0, 0, "", "CPID_KEYHUNT", "^BGYou are starting with the ^6PINK Key", "");
        Notifications.Center("LMS_NOLIVES", 0, 0, "", "CPID_LMS", "^BGYou have no lives left, you must wait until the next match", "");
        Notifications.Center("LMS_VISIBLE_LEADER", 0, 0, "", "CPID_LMS", "^BGEnemies can now see you on radar!", "");
        Notifications.Center("LMS_VISIBLE_OTHER", 0, 0, "", "CPID_LMS", "^BGLeaders can now be seen by enemies on radar!", "");
        Notifications.Center("INSTAGIB_DOWNGRADE", 0, 0, "", "CPID_INSTAGIB_FINDAMMO", "^BGYour weapon has been downgraded until you find some ammo!", "");
        Notifications.Center("INSTAGIB_FINDAMMO", 0, 0, "", "CPID_INSTAGIB_FINDAMMO", "^F4^COUNT^BG left to find some ammo!", "");
        Notifications.Center("INSTAGIB_FINDAMMO_FIRST", 0, 0, "", "CPID_INSTAGIB_FINDAMMO", "^BGGet some ammo or you'll be dead in ^F4^COUNT^BG!", "^BGGet some ammo! ^F4^COUNT^BG left!");
        Notifications.Center("INSTAGIB_LIVES_REMAINING", 0, 1, "f1", "", "^F2Extra lives remaining: ^K1%s", "");
        Notifications.Center("NIX_COUNTDOWN", 0, 2, "item_wepname", "CPID_NIX", "^F2^COUNT^BG until weapon change...\nNext weapon: ^F1%s", "");
        Notifications.Center("NIX_NEWWEAPON", 0, 1, "item_wepname", "CPID_NIX", "^F2Active weapon: ^F1%s", "");
        Notifications.Center("ONS_CAPTURE", 1, 0, "s1", "CPID_ONSLAUGHT", "^BGYou captured %s^BG control point", "");
        Notifications.Center("ONS_CAPTURE_NONAME", 0, 0, "", "CPID_ONSLAUGHT", "^BGYou captured a control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_RED", 1, 0, "s1", "CPID_ONSLAUGHT", "^1RED^BG team captured %s^BG control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_BLUE", 1, 0, "s1", "CPID_ONSLAUGHT", "^4BLUE^BG team captured %s^BG control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_YELLOW", 1, 0, "s1", "CPID_ONSLAUGHT", "^3YELLOW^BG team captured %s^BG control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_PINK", 1, 0, "s1", "CPID_ONSLAUGHT", "^6PINK^BG team captured %s^BG control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_NONAME_RED", 0, 0, "", "CPID_ONSLAUGHT", "^1RED^BG team captured a control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_NONAME_BLUE", 0, 0, "", "CPID_ONSLAUGHT", "^4BLUE^BG team captured a control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_NONAME_YELLOW", 0, 0, "", "CPID_ONSLAUGHT", "^3YELLOW^BG team captured a control point", "");
        Notifications.Center("ONS_CAPTURE_TEAM_NONAME_PINK", 0, 0, "", "CPID_ONSLAUGHT", "^6PINK^BG team captured a control point", "");
        Notifications.Center("ONS_CONTROLPOINT_SHIELDED", 0, 0, "", "CPID_ONS_CAPSHIELD", "^BGThis control point currently cannot be captured", "");
        Notifications.Center("ONS_GENERATOR_SHIELDED", 0, 0, "", "CPID_ONS_CAPSHIELD", "^BGThe enemy generator cannot be destroyed yet\n^F2Capture some control points to unshield it", "");
        Notifications.Center("ONS_NOTSHIELDED_RED", 0, 0, "", "CPID_ONSLAUGHT", "^BGThe ^1enemy^BG generator is no longer shielded!", "");
        Notifications.Center("ONS_NOTSHIELDED_BLUE", 0, 0, "", "CPID_ONSLAUGHT", "^BGThe ^4enemy^BG generator is no longer shielded!", "");
        Notifications.Center("ONS_NOTSHIELDED_YELLOW", 0, 0, "", "CPID_ONSLAUGHT", "^BGThe ^3enemy^BG generator is no longer shielded!", "");
        Notifications.Center("ONS_NOTSHIELDED_PINK", 0, 0, "", "CPID_ONSLAUGHT", "^BGThe ^6enemy^BG generator is no longer shielded!", "");
        Notifications.Center("ONS_NOTSHIELDED_TEAM", 0, 0, "", "CPID_ONSLAUGHT", "^K1Your generator is NOT shielded!\n^BGRe-capture control points to shield it!", "");
        Notifications.Center("ONS_TELEPORT", 0, 0, "pass_key", "CPID_ONSLAUGHT", "^BGPress ^F2%s^BG to teleport", "");
        Notifications.Center("ONS_TELEPORT_ANTISPAM", 0, 1, "f1secs", "CPID_ONSLAUGHT", "^BGTeleporting disabled for %s", "");
        Notifications.Center("OVERTIME_CONTROLPOINT", 0, 0, "", "CPID_OVERTIME", "^F2Now playing ^F4OVERTIME^F2!\n\nGenerators are now decaying.\nThe more control points your team holds,\nthe faster the enemy generator decays", "");
        Notifications.Center("PORTO_CREATED_IN", 0, 0, "", "", "^K1In^BG-portal created", "");
        Notifications.Center("PORTO_CREATED_OUT", 0, 0, "", "", "^F3Out^BG-portal created", "");
        Notifications.Center("PORTO_FAILED", 0, 0, "", "", "^F1Portal creation failed", "");
        Notifications.Center("POWERUP_STRENGTH", 0, 0, "", "CPID_POWERUP", "^F2Strength infuses your weapons with devastating power", "");
        Notifications.Center("POWERDOWN_STRENGTH", 0, 0, "", "CPID_POWERUP", "^F2Strength has worn off", "");
        Notifications.Center("POWERUP_SHIELD", 0, 0, "", "CPID_POWERUP", "^F2Shield surrounds you", "");
        Notifications.Center("POWERDOWN_SHIELD", 0, 0, "", "CPID_POWERUP", "^F2Shield has worn off", "");
        Notifications.Center("POWERUP_SPEED", 0, 0, "", "CPID_POWERUP", "^F2You are on speed", "");
        Notifications.Center("POWERDOWN_SPEED", 0, 0, "", "CPID_POWERUP", "^F2Speed has worn off", "");
        Notifications.Center("POWERUP_INVISIBILITY", 0, 0, "", "CPID_POWERUP", "^F2You are invisible", "");
        Notifications.Center("POWERDOWN_INVISIBILITY", 0, 0, "", "CPID_POWERUP", "^F2Invisibility has worn off", "");
        Notifications.Center("QUIT_PLAYBAN_TEAMKILL", 0, 0, "", "", "^BOLD^K1You are forced to spectate and you aren't allowed to play because you are banned in this server", "");
        Notifications.Center("RACE_FINISHLAP", 0, 0, "", "CPID_RACE_FINISHLAP", "^F2The race is over, finish your lap!", "");
        Notifications.Center("SEQUENCE_COMPLETED", 0, 0, "", "", "^BGSequence completed!", "");
        Notifications.Center("SEQUENCE_COUNTER", 0, 0, "", "", "^BGThere are more to go...", "");
        Notifications.Center("SEQUENCE_COUNTER_FEWMORE", 0, 1, "f1", "", "^BGOnly %s^BG more to go...", "");
        Notifications.Center("SPECTATE_WARNING", 0, 1, "f1secs", "CPID_PREVENT_JOIN", "^F2You have to become a player within the next %s, otherwise you will be kicked, because spectating isn't allowed at this time!", "");
        Notifications.Center("SPECTATE_NOTALLOWED", 0, 0, "", "", "^F2Spectating isn't allowed at this time!", "");
        Notifications.Center("SPECTATE_SPEC_NOTALLOWED", 0, 0, "", "", "^F2Spectating specific players isn't allowed at this time!", "");
        Notifications.Center("SUPERWEAPON_BROKEN", 0, 0, "", "CPID_POWERUP", "^F2Superweapons have broken down", "");
        Notifications.Center("SUPERWEAPON_LOST", 0, 0, "", "CPID_POWERUP", "^F2Superweapons have been lost", "");
        Notifications.Center("SUPERWEAPON_PICKUP", 0, 0, "", "CPID_POWERUP", "^F2You now have a superweapon", "");
        Notifications.Center("SURVIVAL_HUNTER", 0, 0, "", "CPID_SURVIVAL", "^BOLD^BGYou are a ^K1hunter^BG! Eliminate the survivor(s) without raising suspicion!", "");
        Notifications.Center("SURVIVAL_HUNTER_WIN", 0, 0, "", "CPID_ROUND", "^K1Hunters^BG win the round", "");
        Notifications.Center("SURVIVAL_SURVIVOR", 0, 0, "", "CPID_SURVIVAL", "^BOLD^BGYou are a ^F1survivor^BG! Identify and eliminate the hunter(s)!", "");
        Notifications.Center("SURVIVAL_SURVIVOR_WIN", 0, 0, "", "CPID_ROUND", "^F1Survivors^BG win the round", "");
        Notifications.Center("TEAMCHANGE_RED", 0, 1, "", "CPID_TEAMCHANGE", "^K1Changing to ^1RED^K1 in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_BLUE", 0, 1, "", "CPID_TEAMCHANGE", "^K1Changing to ^4BLUE^K1 in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_YELLOW", 0, 1, "", "CPID_TEAMCHANGE", "^K1Changing to ^3YELLOW^K1 in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_PINK", 0, 1, "", "CPID_TEAMCHANGE", "^K1Changing to ^6PINK^K1 in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_SPECTATE", 0, 1, "", "CPID_TEAMCHANGE", "^K1Spectating in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_SUICIDE", 0, 1, "", "CPID_TEAMCHANGE", "^K1Suicide in ^COUNT", "");
        Notifications.Center("TEAMCHANGE_ALREADYBEST", 0, 0, "", "CPID_PREVENT_JOIN", "^K2Your current team choice seems fine.", "");
        Notifications.Center("TEAMCHANGE_STRONGERTEAM", 0, 0, "", "CPID_PREVENT_JOIN", "^K2You're not allowed to join a stronger team!", "");
        Notifications.Center("TEAMCHANGE_NOTALLOWED", 0, 0, "", "CPID_PREVENT_JOIN", "^K2You're not allowed to join that team!", "");
        Notifications.Center("TEAMCHANGE_LOCKED", 0, 0, "", "CPID_PREVENT_JOIN", "^K2Teams are locked, you can't join or change teams until they're unlocked or the map changes.", "");
        Notifications.Center("TEAMCHANGE_SAME", 0, 0, "", "CPID_PREVENT_JOIN", "^K2You're already on that team!", "");
        Notifications.Center("TIMEOUT_BEGINNING", 0, 1, "", "CPID_TIMEOUT", "^F4Timeout begins in ^COUNT", "");
        Notifications.Center("TIMEOUT_ENDING", 0, 1, "", "CPID_TIMEIN", "^F4Timeout ends in ^COUNT", "");
        Notifications.Center("VEHICLE_ENTER", 0, 0, "pass_key", "CPID_VEHICLES", "^BGPress ^F2%s^BG to enter/exit the vehicle", "");
        Notifications.Center("VEHICLE_ENTER_GUNNER", 0, 0, "pass_key", "CPID_VEHICLES", "^BGPress ^F2%s^BG to enter the vehicle gunner", "");
        Notifications.Center("VEHICLE_ENTER_STEAL", 0, 0, "pass_key", "CPID_VEHICLES", "^BGPress ^F2%s^BG to steal this vehicle", "");
        Notifications.Center("VEHICLE_STEAL", 0, 0, "", "CPID_VEHICLES_OTHER", "^F2The enemy is stealing one of your vehicles!\n^F4Stop them!", "");
        Notifications.Center("VEHICLE_STEAL_SELF", 0, 0, "", "CPID_VEHICLES_OTHER", "^F2Intruder detected, disabling shields!", "");
        Notifications.Center("VOTEBAN", 0, 0, "", "", "^BOLD^K1You aren't allowed to call a vote because you are banned in this server", "");
        Notifications.Center("VOTEBANYN", 0, 0, "", "", "^BOLD^K1You aren't allowed to vote because you are banned in this server", "");
        Notifications.Center("WEAPON_MINELAYER_LIMIT", 0, 1, "f1", "", "^BGYou cannot place more than ^F2%s^BG mines at a time", "");
    }

    private static void RegisterGeneratedMulti()
    {
        Notifications.Multi("COUNTDOWN_BEGIN", "BEGIN", null, "COUNTDOWN_BEGIN");
        Notifications.Multi("COUNTDOWN_STOP_MINPLAYERS", null, "COUNTDOWN_STOP_MINPLAYERS", "COUNTDOWN_STOP_MINPLAYERS");
        Notifications.Multi("COUNTDOWN_STOP_BADTEAMS", null, "COUNTDOWN_STOP_BADTEAMS", "COUNTDOWN_STOP_BADTEAMS");
        Notifications.Multi("DEATH_MURDER_BUFF_INFERNO", null, "DEATH_MURDER_BUFF_INFERNO", null);
        Notifications.Multi("DEATH_MURDER_BUFF_VENGEANCE", null, "DEATH_MURDER_BUFF_VENGEANCE", null);
        Notifications.Multi("DEATH_MURDER_CHEAT", null, "DEATH_MURDER_CHEAT", null);
        Notifications.Multi("DEATH_MURDER_DROWN", null, "DEATH_MURDER_DROWN", null);
        Notifications.Multi("DEATH_MURDER_FALL", null, "DEATH_MURDER_FALL", null);
        Notifications.Multi("DEATH_MURDER_FIRE", null, "DEATH_MURDER_FIRE", null);
        Notifications.Multi("DEATH_MURDER_LAVA", null, "DEATH_MURDER_LAVA", null);
        Notifications.Multi("DEATH_MURDER_MONSTER", null, "DEATH_MURDER_MONSTER", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_MURDER_NADE", null, "DEATH_MURDER_NADE", null);
        Notifications.Multi("DEATH_MURDER_NADE_DARKNESS", null, "DEATH_MURDER_NADE_DARKNESS", null);
        Notifications.Multi("DEATH_MURDER_NADE_HEAL", null, "DEATH_MURDER_NADE_HEAL", null);
        Notifications.Multi("DEATH_MURDER_NADE_ICE", null, "DEATH_MURDER_NADE_ICE", null);
        Notifications.Multi("DEATH_MURDER_NADE_NAPALM", null, "DEATH_MURDER_NADE_NAPALM", null);
        Notifications.Multi("DEATH_MURDER_SHOOTING_STAR", null, "DEATH_MURDER_SHOOTING_STAR", null);
        Notifications.Multi("DEATH_MURDER_SLIME", null, "DEATH_MURDER_SLIME", null);
        Notifications.Multi("DEATH_MURDER_SWAMP", null, "DEATH_MURDER_SWAMP", null);
        Notifications.Multi("DEATH_MURDER_TELEFRAG", null, "DEATH_MURDER_TELEFRAG", null);
        Notifications.Multi("DEATH_MURDER_TOUCHEXPLODE", null, "DEATH_MURDER_TOUCHEXPLODE", null);
        Notifications.Multi("DEATH_MURDER_VH_BUMB_DEATH", null, "DEATH_MURDER_VH_BUMB_DEATH", null);
        Notifications.Multi("DEATH_MURDER_VH_BUMB_GUN", null, "DEATH_MURDER_VH_BUMB_GUN", null);
        Notifications.Multi("DEATH_MURDER_VH_CRUSH", null, "DEATH_MURDER_VH_CRUSH", null);
        Notifications.Multi("DEATH_MURDER_VH_RAPT_BOMB", null, "DEATH_MURDER_VH_RAPT_BOMB", null);
        Notifications.Multi("DEATH_MURDER_VH_RAPT_CANNON", null, "DEATH_MURDER_VH_RAPT_CANNON", null);
        Notifications.Multi("DEATH_MURDER_VH_RAPT_DEATH", null, "DEATH_MURDER_VH_RAPT_DEATH", null);
        Notifications.Multi("DEATH_MURDER_VH_SPID_DEATH", null, "DEATH_MURDER_VH_SPID_DEATH", null);
        Notifications.Multi("DEATH_MURDER_VH_SPID_MINIGUN", null, "DEATH_MURDER_VH_SPID_MINIGUN", null);
        Notifications.Multi("DEATH_MURDER_VH_SPID_ROCKET", null, "DEATH_MURDER_VH_SPID_ROCKET", null);
        Notifications.Multi("DEATH_MURDER_VH_WAKI_DEATH", null, "DEATH_MURDER_VH_WAKI_DEATH", null);
        Notifications.Multi("DEATH_MURDER_VH_WAKI_GUN", null, "DEATH_MURDER_VH_WAKI_GUN", null);
        Notifications.Multi("DEATH_MURDER_VH_WAKI_ROCKET", null, "DEATH_MURDER_VH_WAKI_ROCKET", null);
        Notifications.Multi("DEATH_MURDER_VOID", null, "DEATH_MURDER_VOID", null);
        Notifications.Multi("DEATH_MURDER_VOID_ENT", null, "DEATH_MURDER_VOID_ENT", null);
        Notifications.Multi("DEATH_SELF_BETRAYAL", null, "DEATH_SELF_BETRAYAL", "DEATH_SELF_BETRAYAL");
        Notifications.Multi("DEATH_SELF_CAMP", null, "DEATH_SELF_CAMP", "DEATH_SELF_CAMP");
        Notifications.Multi("DEATH_SELF_CHEAT", null, "DEATH_SELF_CHEAT", "DEATH_SELF_CHEAT");
        Notifications.Multi("DEATH_SELF_CUSTOM", null, "DEATH_SELF_GENERIC", "DEATH_SELF_CUSTOM");
        Notifications.Multi("DEATH_SELF_DROWN", null, "DEATH_SELF_DROWN", "DEATH_SELF_DROWN");
        Notifications.Multi("DEATH_SELF_FALL", null, "DEATH_SELF_FALL", "DEATH_SELF_FALL");
        Notifications.Multi("DEATH_SELF_FIRE", null, "DEATH_SELF_FIRE", "DEATH_SELF_FIRE");
        Notifications.Multi("DEATH_SELF_GENERIC", null, "DEATH_SELF_GENERIC", "DEATH_SELF_GENERIC");
        Notifications.Multi("DEATH_SELF_LAVA", null, "DEATH_SELF_LAVA", "DEATH_SELF_LAVA");
        Notifications.Multi("DEATH_SELF_MON_MAGE", null, "DEATH_SELF_MON_MAGE", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_GOLEM_CLAW", null, "DEATH_SELF_MON_GOLEM_CLAW", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_GOLEM_SMASH", null, "DEATH_SELF_MON_GOLEM_SMASH", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_GOLEM_ZAP", null, "DEATH_SELF_MON_GOLEM_ZAP", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_SPIDER", null, "DEATH_SELF_MON_SPIDER", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_WYVERN", null, "DEATH_SELF_MON_WYVERN", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_ZOMBIE_JUMP", null, "DEATH_SELF_MON_ZOMBIE_JUMP", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_MON_ZOMBIE_MELEE", null, "DEATH_SELF_MON_ZOMBIE_MELEE", "DEATH_SELF_MONSTER");
        Notifications.Multi("DEATH_SELF_NADE", null, "DEATH_SELF_NADE", "DEATH_SELF_NADE");
        Notifications.Multi("DEATH_SELF_NADE_DARKNESS", null, "DEATH_SELF_NADE_DARKNESS", "DEATH_SELF_NADE");
        Notifications.Multi("DEATH_SELF_NADE_HEAL", null, "DEATH_SELF_NADE_HEAL", "DEATH_SELF_NADE_HEAL");
        Notifications.Multi("DEATH_SELF_NADE_ICE", null, "DEATH_SELF_NADE_ICE", "DEATH_SELF_NADE");
        Notifications.Multi("DEATH_SELF_NADE_NAPALM", null, "DEATH_SELF_NADE_NAPALM", "DEATH_SELF_NADE_NAPALM");
        Notifications.Multi("DEATH_SELF_NOAMMO", null, "DEATH_SELF_NOAMMO", "DEATH_SELF_NOAMMO");
        Notifications.Multi("DEATH_SELF_ROT", null, "DEATH_SELF_ROT", "DEATH_SELF_ROT");
        Notifications.Multi("DEATH_SELF_SHOOTING_STAR", null, "DEATH_SELF_SHOOTING_STAR", "DEATH_SELF_SHOOTING_STAR");
        Notifications.Multi("DEATH_SELF_SLIME", null, "DEATH_SELF_SLIME", "DEATH_SELF_SLIME");
        Notifications.Multi("DEATH_SELF_SUICIDE", null, "DEATH_SELF_SUICIDE", "DEATH_SELF_SUICIDE");
        Notifications.Multi("DEATH_SELF_SWAMP", null, "DEATH_SELF_SWAMP", "DEATH_SELF_SWAMP");
        Notifications.Multi("DEATH_SELF_TOUCHEXPLODE", null, "DEATH_SELF_TOUCHEXPLODE", "DEATH_SELF_TOUCHEXPLODE");
        Notifications.Multi("DEATH_SELF_TURRET", null, "DEATH_SELF_TURRET", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_EWHEEL", null, "DEATH_SELF_TURRET_EWHEEL", "DEATH_SELF_TURRET_EWHEEL");
        Notifications.Multi("DEATH_SELF_TURRET_FLAC", null, "DEATH_SELF_TURRET_FLAC", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_HELLION", null, "DEATH_SELF_TURRET_HELLION", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_HK", null, "DEATH_SELF_TURRET_HK", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_MACHINEGUN", null, "DEATH_SELF_TURRET_MACHINEGUN", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_MLRS", null, "DEATH_SELF_TURRET_MLRS", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_PHASER", null, "DEATH_SELF_TURRET_PHASER", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_PLASMA", null, "DEATH_SELF_TURRET_PLASMA", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_TESLA", null, "DEATH_SELF_TURRET_TESLA", "DEATH_SELF_TURRET");
        Notifications.Multi("DEATH_SELF_TURRET_WALK_GUN", null, "DEATH_SELF_TURRET_WALK_GUN", "DEATH_SELF_TURRET_WALK");
        Notifications.Multi("DEATH_SELF_TURRET_WALK_MELEE", null, "DEATH_SELF_TURRET_WALK_MELEE", "DEATH_SELF_TURRET_WALK");
        Notifications.Multi("DEATH_SELF_TURRET_WALK_ROCKET", null, "DEATH_SELF_TURRET_WALK_ROCKET", "DEATH_SELF_TURRET_WALK");
        Notifications.Multi("DEATH_SELF_VH_BUMB_DEATH", null, "DEATH_SELF_VH_BUMB_DEATH", "DEATH_SELF_VH_BUMB_DEATH");
        Notifications.Multi("DEATH_SELF_VH_CRUSH", null, "DEATH_SELF_VH_CRUSH", "DEATH_SELF_VH_CRUSH");
        Notifications.Multi("DEATH_SELF_VH_RAPT_BOMB", null, "DEATH_SELF_VH_RAPT_BOMB", "DEATH_SELF_VH_RAPT_BOMB");
        Notifications.Multi("DEATH_SELF_VH_RAPT_DEATH", null, "DEATH_SELF_VH_RAPT_DEATH", "DEATH_SELF_VH_RAPT_DEATH");
        Notifications.Multi("DEATH_SELF_VH_SPID_DEATH", null, "DEATH_SELF_VH_SPID_DEATH", "DEATH_SELF_VH_SPID_DEATH");
        Notifications.Multi("DEATH_SELF_VH_SPID_ROCKET", null, "DEATH_SELF_VH_SPID_ROCKET", "DEATH_SELF_VH_SPID_ROCKET");
        Notifications.Multi("DEATH_SELF_VH_WAKI_DEATH", null, "DEATH_SELF_VH_WAKI_DEATH", "DEATH_SELF_VH_WAKI_DEATH");
        Notifications.Multi("DEATH_SELF_VH_WAKI_ROCKET", null, "DEATH_SELF_VH_WAKI_ROCKET", "DEATH_SELF_VH_WAKI_ROCKET");
        Notifications.Multi("DEATH_SELF_VOID", null, "DEATH_SELF_VOID", "DEATH_SELF_VOID");
        Notifications.Multi("DEATH_SELF_VOID_ENT", null, "DEATH_SELF_VOID_ENT", "DEATH_SELF_VOID");
        Notifications.Multi("ITEM_BUFF_DROP", null, "ITEM_BUFF_DROP", "ITEM_BUFF_DROP");
        Notifications.Multi("ITEM_WEAPON_DONTHAVE", null, "ITEM_WEAPON_DONTHAVE", "ITEM_WEAPON_DONTHAVE");
        Notifications.Multi("ITEM_WEAPON_DROP", null, "ITEM_WEAPON_DROP", "ITEM_WEAPON_DROP");
        Notifications.Multi("ITEM_WEAPON_NOAMMO", null, "ITEM_WEAPON_NOAMMO", "ITEM_WEAPON_NOAMMO");
        Notifications.Multi("ITEM_WEAPON_PRIMORSEC", null, "ITEM_WEAPON_PRIMORSEC", "ITEM_WEAPON_PRIMORSEC");
        Notifications.Multi("ITEM_WEAPON_UNAVAILABLE", null, "ITEM_WEAPON_UNAVAILABLE", "ITEM_WEAPON_UNAVAILABLE");
        Notifications.Multi("MULTI_COINTOSS", null, "COINTOSS", "COINTOSS");
        Notifications.Multi("MULTI_INSTAGIB_FINDAMMO", "NUM_10", null, "INSTAGIB_FINDAMMO_FIRST");
        Notifications.Multi("SPECTATE_WARNING", null, "SPECTATE_WARNING", "SPECTATE_WARNING");
        Notifications.Multi("SPECTATE_NOTALLOWED", null, "SPECTATE_NOTALLOWED", "SPECTATE_NOTALLOWED");
        Notifications.Multi("SPECTATE_SPEC_NOTALLOWED", null, "SPECTATE_SPEC_NOTALLOWED", "SPECTATE_SPEC_NOTALLOWED");
        Notifications.Multi("WEAPON_ACCORDEON_MURDER", null, "WEAPON_ACCORDEON_MURDER", null);
        Notifications.Multi("WEAPON_ACCORDEON_SUICIDE", null, "WEAPON_ACCORDEON_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_ARC_MURDER", null, "WEAPON_ARC_MURDER", null);
        Notifications.Multi("WEAPON_ARC_MURDER_SPRAY", null, "WEAPON_ARC_MURDER_SPRAY", null);
        Notifications.Multi("WEAPON_ARC_SUICIDE_BOLT", null, "WEAPON_ARC_SUICIDE_BOLT", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_BLASTER_MURDER", null, "WEAPON_BLASTER_MURDER", null);
        Notifications.Multi("WEAPON_BLASTER_SUICIDE", null, "WEAPON_BLASTER_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_CRYLINK_MURDER", null, "WEAPON_CRYLINK_MURDER", null);
        Notifications.Multi("WEAPON_CRYLINK_SUICIDE", null, "WEAPON_CRYLINK_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_DEVASTATOR_MURDER_DIRECT", null, "WEAPON_DEVASTATOR_MURDER_DIRECT", null);
        Notifications.Multi("WEAPON_DEVASTATOR_MURDER_SPLASH", null, "WEAPON_DEVASTATOR_MURDER_SPLASH", null);
        Notifications.Multi("WEAPON_DEVASTATOR_SUICIDE", null, "WEAPON_DEVASTATOR_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_ELECTRO_MURDER_BOLT", null, "WEAPON_ELECTRO_MURDER_BOLT", null);
        Notifications.Multi("WEAPON_ELECTRO_MURDER_COMBO", null, "WEAPON_ELECTRO_MURDER_COMBO", null);
        Notifications.Multi("WEAPON_ELECTRO_MURDER_ORBS", null, "WEAPON_ELECTRO_MURDER_ORBS", null);
        Notifications.Multi("WEAPON_ELECTRO_SUICIDE_BOLT", null, "WEAPON_ELECTRO_SUICIDE_BOLT", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_ELECTRO_SUICIDE_ORBS", null, "WEAPON_ELECTRO_SUICIDE_ORBS", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_FIREBALL_MURDER_BLAST", null, "WEAPON_FIREBALL_MURDER_BLAST", null);
        Notifications.Multi("WEAPON_FIREBALL_MURDER_FIREMINE", null, "WEAPON_FIREBALL_MURDER_FIREMINE", null);
        Notifications.Multi("WEAPON_FIREBALL_SUICIDE_BLAST", null, "WEAPON_FIREBALL_SUICIDE_BLAST", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_FIREBALL_SUICIDE_FIREMINE", null, "WEAPON_FIREBALL_SUICIDE_FIREMINE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_HAGAR_MURDER_BURST", null, "WEAPON_HAGAR_MURDER_BURST", null);
        Notifications.Multi("WEAPON_HAGAR_MURDER_SPRAY", null, "WEAPON_HAGAR_MURDER_SPRAY", null);
        Notifications.Multi("WEAPON_HAGAR_SUICIDE", null, "WEAPON_HAGAR_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_HLAC_MURDER", null, "WEAPON_HLAC_MURDER", null);
        Notifications.Multi("WEAPON_HLAC_SUICIDE", null, "WEAPON_HLAC_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_HOOK_MURDER", null, "WEAPON_HOOK_MURDER", null);
        Notifications.Multi("WEAPON_KLEINBOTTLE_MURDER", null, "WEAPON_KLEINBOTTLE_MURDER", null);
        Notifications.Multi("WEAPON_KLEINBOTTLE_SUICIDE", null, "WEAPON_KLEINBOTTLE_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_MACHINEGUN_MURDER_SNIPE", null, "WEAPON_MACHINEGUN_MURDER_SNIPE", null);
        Notifications.Multi("WEAPON_MACHINEGUN_MURDER_SPRAY", null, "WEAPON_MACHINEGUN_MURDER_SPRAY", null);
        Notifications.Multi("WEAPON_MINELAYER_LIMIT", null, "WEAPON_MINELAYER_LIMIT", "WEAPON_MINELAYER_LIMIT");
        Notifications.Multi("WEAPON_MINELAYER_MURDER", null, "WEAPON_MINELAYER_MURDER", null);
        Notifications.Multi("WEAPON_MINELAYER_SUICIDE", null, "WEAPON_MINELAYER_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_MORTAR_MURDER_BOUNCE", null, "WEAPON_MORTAR_MURDER_BOUNCE", null);
        Notifications.Multi("WEAPON_MORTAR_MURDER_EXPLODE", null, "WEAPON_MORTAR_MURDER_EXPLODE", null);
        Notifications.Multi("WEAPON_MORTAR_SUICIDE_BOUNCE", null, "WEAPON_MORTAR_SUICIDE_BOUNCE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_MORTAR_SUICIDE_EXPLODE", null, "WEAPON_MORTAR_SUICIDE_EXPLODE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_OVERKILL_HMG_MURDER_SPRAY", null, "WEAPON_OVERKILL_HMG_MURDER_SPRAY", null);
        Notifications.Multi("WEAPON_OVERKILL_MACHINEGUN_MURDER", null, "WEAPON_OVERKILL_MACHINEGUN_MURDER", null);
        Notifications.Multi("WEAPON_OVERKILL_NEX_MURDER", null, "WEAPON_OVERKILL_NEX_MURDER", null);
        Notifications.Multi("WEAPON_OVERKILL_RPC_MURDER_DIRECT", null, "WEAPON_OVERKILL_RPC_MURDER_DIRECT", null);
        Notifications.Multi("WEAPON_OVERKILL_RPC_MURDER_SPLASH", null, "WEAPON_OVERKILL_RPC_MURDER_SPLASH", null);
        Notifications.Multi("WEAPON_OVERKILL_RPC_SUICIDE_DIRECT", null, "WEAPON_OVERKILL_RPC_SUICIDE_DIRECT", null);
        Notifications.Multi("WEAPON_OVERKILL_RPC_SUICIDE_SPLASH", null, "WEAPON_OVERKILL_RPC_SUICIDE_SPLASH", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_OVERKILL_SHOTGUN_MURDER", null, "WEAPON_OVERKILL_SHOTGUN_MURDER", null);
        Notifications.Multi("WEAPON_RIFLE_MURDER", null, "WEAPON_RIFLE_MURDER", null);
        Notifications.Multi("WEAPON_RIFLE_MURDER_HAIL", null, "WEAPON_RIFLE_MURDER_HAIL", null);
        Notifications.Multi("WEAPON_RIFLE_MURDER_HAIL_PIERCING", null, "WEAPON_RIFLE_MURDER_HAIL_PIERCING", null);
        Notifications.Multi("WEAPON_RIFLE_MURDER_PIERCING", null, "WEAPON_RIFLE_MURDER_PIERCING", null);
        Notifications.Multi("WEAPON_SEEKER_MURDER_SPRAY", null, "WEAPON_SEEKER_MURDER_SPRAY", null);
        Notifications.Multi("WEAPON_SEEKER_MURDER_TAG", null, "WEAPON_SEEKER_MURDER_TAG", null);
        Notifications.Multi("WEAPON_SEEKER_SUICIDE", null, "WEAPON_SEEKER_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_SHOTGUN_MURDER", null, "WEAPON_SHOTGUN_MURDER", null);
        Notifications.Multi("WEAPON_SHOTGUN_MURDER_SLAP", null, "WEAPON_SHOTGUN_MURDER_SLAP", null);
        Notifications.Multi("WEAPON_THINKING_WITH_PORTALS", null, "WEAPON_THINKING_WITH_PORTALS", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_TUBA_MURDER", null, "WEAPON_TUBA_MURDER", null);
        Notifications.Multi("WEAPON_TUBA_SUICIDE", null, "WEAPON_TUBA_SUICIDE", "DEATH_SELF_GENERIC");
        Notifications.Multi("WEAPON_VAPORIZER_MURDER", null, "WEAPON_VAPORIZER_MURDER", null);
        Notifications.Multi("WEAPON_VORTEX_MURDER", null, "WEAPON_VORTEX_MURDER", null);
    }

    private static void RegisterGeneratedChoice()
    {
        Notifications.Choice("CTF_CAPTURE_BROKEN_RED", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_RED", "CTF_CAPTURE_BROKEN_RED");
        Notifications.Choice("CTF_CAPTURE_BROKEN_BLUE", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_BLUE", "CTF_CAPTURE_BROKEN_BLUE");
        Notifications.Choice("CTF_CAPTURE_BROKEN_YELLOW", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_YELLOW", "CTF_CAPTURE_BROKEN_YELLOW");
        Notifications.Choice("CTF_CAPTURE_BROKEN_PINK", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_PINK", "CTF_CAPTURE_BROKEN_PINK");
        Notifications.Choice("CTF_CAPTURE_TIME_RED", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_RED", "CTF_CAPTURE_TIME_RED");
        Notifications.Choice("CTF_CAPTURE_TIME_BLUE", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_BLUE", "CTF_CAPTURE_TIME_BLUE");
        Notifications.Choice("CTF_CAPTURE_TIME_YELLOW", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_YELLOW", "CTF_CAPTURE_TIME_YELLOW");
        Notifications.Choice("CTF_CAPTURE_TIME_PINK", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_PINK", "CTF_CAPTURE_TIME_PINK");
        Notifications.Choice("CTF_CAPTURE_UNBROKEN_RED", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_RED", "CTF_CAPTURE_UNBROKEN_RED");
        Notifications.Choice("CTF_CAPTURE_UNBROKEN_BLUE", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_BLUE", "CTF_CAPTURE_UNBROKEN_BLUE");
        Notifications.Choice("CTF_CAPTURE_UNBROKEN_YELLOW", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_YELLOW", "CTF_CAPTURE_UNBROKEN_YELLOW");
        Notifications.Choice("CTF_CAPTURE_UNBROKEN_PINK", NotifAllowed.Always, MsgType.Info, "CTF_CAPTURE_PINK", "CTF_CAPTURE_UNBROKEN_PINK");
        Notifications.Choice("CTF_PICKUP_TEAM_RED", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_TEAM_RED", "CTF_PICKUP_TEAM_VERBOSE_RED");
        Notifications.Choice("CTF_PICKUP_TEAM_BLUE", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_TEAM_BLUE", "CTF_PICKUP_TEAM_VERBOSE_BLUE");
        Notifications.Choice("CTF_PICKUP_TEAM_YELLOW", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_TEAM_YELLOW", "CTF_PICKUP_TEAM_VERBOSE_YELLOW");
        Notifications.Choice("CTF_PICKUP_TEAM_PINK", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_TEAM_PINK", "CTF_PICKUP_TEAM_VERBOSE_PINK");
        Notifications.Choice("CTF_PICKUP_ENEMY_OTHER_RED", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_OTHER_RED", "CTF_PICKUP_ENEMY_OTHER_VERBOSE_RED");
        Notifications.Choice("CTF_PICKUP_ENEMY_OTHER_BLUE", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_OTHER_BLUE", "CTF_PICKUP_ENEMY_OTHER_VERBOSE_BLUE");
        Notifications.Choice("CTF_PICKUP_ENEMY_OTHER_YELLOW", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_OTHER_YELLOW", "CTF_PICKUP_ENEMY_OTHER_VERBOSE_YELLOW");
        Notifications.Choice("CTF_PICKUP_ENEMY_OTHER_PINK", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_OTHER_PINK", "CTF_PICKUP_ENEMY_OTHER_VERBOSE_PINK");
        Notifications.Choice("CTF_PICKUP_TEAM_NEUTRAL", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_TEAM_NEUTRAL", "CTF_PICKUP_TEAM_VERBOSE_NEUTRAL");
        Notifications.Choice("CTF_PICKUP_ENEMY", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY", "CTF_PICKUP_ENEMY_VERBOSE");
        Notifications.Choice("CTF_PICKUP_ENEMY_TEAM", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_TEAM", "CTF_PICKUP_ENEMY_TEAM_VERBOSE");
        Notifications.Choice("CTF_PICKUP_ENEMY_OTHER_NEUTRAL", NotifAllowed.Always, MsgType.Center, "CTF_PICKUP_ENEMY_OTHER_NEUTRAL", "CTF_PICKUP_ENEMY_OTHER_VERBOSE_NEUTRAL");
        Notifications.Choice("FRAG_FIRE", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAG_FIRE", "DEATH_MURDER_FRAG_FIRE_VERBOSE");
        Notifications.Choice("FRAGGED_FIRE", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAGGED_FIRE", "DEATH_MURDER_FRAGGED_FIRE_VERBOSE");
        Notifications.Choice("FRAG_FREEZE", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAG_FREEZE", "DEATH_MURDER_FRAG_FREEZE_VERBOSE");
        Notifications.Choice("FRAGGED_FREEZE", NotifAllowed.Warmup, MsgType.Center, "DEATH_MURDER_FRAGGED_FREEZE", "DEATH_MURDER_FRAGGED_FREEZE_VERBOSE");
    }
}
