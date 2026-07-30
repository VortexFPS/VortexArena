// Port of Base/.../qcsrc/common/deathtypes/all.{qh,inc} (REGISTER_DEATHTYPE registry + .message category
//      + DEATH_ISMONSTER/ISTURRET/ISVEHICLE macros).
using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay.Damage;

/// <summary>
/// The category an "extra" deathtype message carries (QC <c>.message</c> on a registered deathtype,
/// deathtypes/all.qh:12 + all.inc). Drives obituary phrasing: a monster/turret/vehicle kill uses the
/// generic DEATH_MURDER_MONSTER / DEATH_SELF_TURRET / … lines instead of a weapon line. The QC macros
/// <c>DEATH_ISMONSTER(t)</c>/<c>ISTURRET(t)</c>/<c>ISVEHICLE(t)</c> are string compares on this field.
/// </summary>
public enum DeathCategory
{
    /// <summary>No extra category — a plain environment/weapon death (QC <c>message == ""</c>).</summary>
    None = 0,
    /// <summary>QC <c>message == "monster"</c> — DEATH_ISMONSTER (MONSTER_* deathtypes).</summary>
    Monster = 1,
    /// <summary>QC <c>message == "turret"</c> — DEATH_ISTURRET (TURRET_* deathtypes).</summary>
    Turret = 2,
    /// <summary>QC <c>message == "vehicle"</c> — DEATH_ISVEHICLE (VH_* deathtypes).</summary>
    Vehicle = 3,
}

/// <summary>
/// One registered "special" (non-weapon) deathtype — the C# successor to a QC <c>REGISTER_DEATHTYPE</c>
/// entry (deathtypes/all.qh:18 + all.inc). Carries the bare name (QC <c>m_name</c>, e.g. "MONSTER_SPIDER")
/// and its <see cref="Category"/> (QC <c>.message</c>). The self/murder message NAMES are resolved by
/// <c>DeathMessages</c> from the name + category, so they're not duplicated here.
/// </summary>
public sealed class DeathTypeDef
{
    /// <summary>QC <c>m_name</c> — the registered deathtype id, e.g. "MONSTER_SPIDER", "TURRET_MLRS", "VH_WAKI_GUN".</summary>
    public string Name { get; }

    /// <summary>QC <c>.message</c> category (monster/turret/vehicle), or <see cref="DeathCategory.None"/>.</summary>
    public DeathCategory Category { get; }

    /// <summary>QC <c>death_msgself</c> (all.inc col 2): the bare DEATH_SELF_* notification name for a SUICIDE/
    /// self-death by this deathtype, or null when the row registers no self message (QC NULL → generic fallback).</summary>
    public string? SelfMessage { get; }

    /// <summary>QC <c>death_msgmurder</c> (all.inc col 3): the bare DEATH_MURDER_* notification name for a MURDER
    /// by this deathtype, or null when the row registers no murder message (QC NULL → generic fallback).</summary>
    public string? MurderMessage { get; }

    public DeathTypeDef(string name, DeathCategory category, string? selfMessage = null, string? murderMessage = null)
    {
        Name = name;
        Category = category;
        SelfMessage = selfMessage;
        MurderMessage = murderMessage;
    }
}

/// <summary>
/// Lightweight stand-in for the QuakeC deathtype registry
/// (common/deathtypes/all.{qh,inc}, REGISTER_DEATHTYPE). In QC a deathtype is a packed integer:
/// the low 8 bits are a weapon id (DEATH_WEAPONMASK), HITTYPE_* live in bits 8-13, and "special"
/// (non-weapon) deaths start at DT_FIRST (BIT(14)) and index the Deathtypes registry.
///
/// For this phase the pipeline carries the deathtype as a plain string tag (see
/// <see cref="DamageInfo.DeathType"/>): the attacking weapon's NetName for weapon kills, or one of the
/// named environment/special constants below. The HITTYPE_* bitflags (which can be OR'd onto either a
/// weapon or a special death — e.g. a SPLASH rocket hit, an ARMORPIERCE shot, a SOUND attack) are
/// encoded as <c>"|flag"</c> suffixes appended to the base tag (see <see cref="WithHitType"/> /
/// <see cref="HasHitType"/>). This keeps obituary/hook routing readable while modelling the bit math
/// the damage pipeline needs (armorpierce, sound, splash, spam) faithfully.
/// </summary>
public static class DeathTypes
{
    // --- environment / "accident-trap" deaths (DEATH_* specials in all.inc) ---

    /// <summary>QC DEATH_FALL — fatal fall / impact damage.</summary>
    public const string Fall = "fall";

    /// <summary>QC DEATH_DROWN — out of air. (Armor never helps here: see healtharmor_applydamage.)</summary>
    public const string Drown = "drown";

    /// <summary>QC DEATH_HURTTRIGGER used as DEATH_VOID — fell out of the world / into a kill volume.</summary>
    public const string Void = "void";

    /// <summary>QC DEATH_LAVA.</summary>
    public const string Lava = "lava";

    /// <summary>QC DEATH_SLIME.</summary>
    public const string Slime = "slime";

    /// <summary>QC DEATH_SWAMP.</summary>
    public const string Swamp = "swamp";

    /// <summary>QC DEATH_TELEFRAG — telefrag (ALWAYS lethal in QC, ignores teamplay nullify).</summary>
    public const string Telefrag = "telefrag";

    /// <summary>QC DEATH_KILL — explicit suicide (the /kill command). ALWAYS lethal.</summary>
    public const string Kill = "kill";

    /// <summary>QC DEATH_TEAMCHANGE — forced death on team change. ALWAYS lethal (sets health 0.9).</summary>
    public const string TeamChange = "teamchange";

    /// <summary>QC DEATH_AUTOTEAMCHANGE — auto team-balance death. ALWAYS lethal, does not negate frags.</summary>
    public const string AutoTeamChange = "autoteamchange";

    /// <summary>QC DEATH_GENERIC — unattributed damage.</summary>
    public const string Generic = "generic";

    /// <summary>QC DEATH_CHEAT (deathtypes/all.inc:5) — the r00t cheat's radius nuke (server/cheats.qc CHIMPULSE_R00T).
    /// Registered with DEATH_SELF_CHEAT / DEATH_MURDER_CHEAT and an empty .message (a plain murder, no special
    /// category); the obituary uses the generic cheat murder line.</summary>
    public const string Cheat = "cheat";

    /// <summary>QC DEATH_CAMP — campcheck mutator anti-camp damage. Self-only (no murder line); the obituary
    /// is the camp-specific DEATH_SELF_CAMP ("thought they found a nice camping ground" / "Die camper!").</summary>
    public const string Camp = "camp";

    /// <summary>QC DEATH_MIRRORDAMAGE — teamdamage reflected back at the attacker.</summary>
    public const string MirrorDamage = "mirrordamage";

    /// <summary>QC DEATH_NOAMMO — drowning-style "no ammo" death (no damage processing in Damage()).</summary>
    public const string NoAmmo = "noammo";

    /// <summary>QC DEATH_FIRE — burning damage tick (no armor-impact pain sound; special pain handling).</summary>
    public const string Fire = "fire";

    /// <summary>QC DEATH_BUFF_INFERNO — inferno-buff fire (excluded from the hit-sound credit, like fire).</summary>
    public const string BuffInferno = "buff_inferno";

    /// <summary>QC DEATH_BUFF_VENGEANCE — vengeance-buff reflected damage (excluded from hit-sound credit).</summary>
    public const string BuffVengeance = "buff_vengeance";

    /// <summary>QC DEATH_TOUCHEXPLODE — the touch-explode mutator blast ("died in an accident"). Registered with
    /// DEATH_SELF_TOUCHEXPLODE / DEATH_MURDER_TOUCHEXPLODE and empty .message (all.inc:35). Also used by the
    /// nade spawn/translocate booms (NadeSpawnBoom / NadeTranslocateBoom) which tag their blast the same way.</summary>
    public const string TouchExplode = "touchexplode";

    // --- nade-family deaths (QC DEATH_NADE*; all.inc:23-27). The live nade booms emit these via
    // Combat.Damage when g_nades is enabled; mirrored here (== NadeDeathTypes.*) so DeathMessages can map them
    // without depending on the Nades subsystem. Each registers BOTH a self and a murder notification line.
    /// <summary>QC DEATH_NADE — a normal nade explosion / held-nade throw.</summary>
    public const string Nade = "nade";
    /// <summary>QC DEATH_NADE_NAPALM — napalm-nade fire damage.</summary>
    public const string NadeNapalm = "nade_napalm";
    /// <summary>QC DEATH_NADE_ICE — ice-nade explode / ice-field damage.</summary>
    public const string NadeIce = "nade_ice";
    /// <summary>QC DEATH_NADE_HEAL — heal-nade harm-to-foe damage.</summary>
    public const string NadeHeal = "nade_heal";
    /// <summary>QC DEATH_NADE_DARKNESS — darkness-nade explode.</summary>
    public const string NadeDarkness = "nade_darkness";

    /// <summary>QC DEATH_ROT (all.inc:29) — the regen/rot "health rotted away" tick death. Self-only
    /// (DEATH_SELF_ROT; NULL murder line → generic frag fallback).</summary>
    public const string Rot = "rot";

    /// <summary>QC DEATH_SHOOTING_STAR (all.inc:30) — the "shooting star" launch death (e.g. a jumppad/launch
    /// mutator). Registered with DEATH_SELF_SHOOTING_STAR / DEATH_MURDER_SHOOTING_STAR.</summary>
    public const string ShootingStar = "shooting_star";

    /// <summary>QC DEATH_CUSTOM (all.inc:6) — a mapper-supplied custom death message (server/damage.qc ACCIDENT
    /// branch formats <c>deathmessage</c> into DEATH_SELF_CUSTOM, "You were %s"). Self-only (NULL murder).
    /// The inflictor/trigger carries the mapper string; see Scores.EmitObituary's CUSTOM handling.</summary>
    public const string Custom = "custom";

    /// <summary>Prefix marking a deathtype string that names a weapon (vs. a special death).</summary>
    public const string WeaponPrefix = "weapon/";

    // --- HITTYPE flag suffixes (QC HITTYPE_* bits, all.qh) -----------------------------------------
    // These OR onto a base tag via "|flag". The string scheme can't pack them into bits, so we keep them
    // as ordered suffix tokens; HasHitType scans for the token. The set mirrors common/deathtypes/all.qh.

    /// <summary>QC HITTYPE_SECONDARY (BIT 8) — alt-fire of the weapon.</summary>
    public const string Secondary = "secondary";
    /// <summary>QC HITTYPE_SPLASH (BIT 9) — set automatically by RadiusDamage for indirect (blast) hits.</summary>
    public const string Splash = "splash";
    /// <summary>QC HITTYPE_BOUNCE (BIT 10) — set after a projectile has bounced.</summary>
    public const string Bounce = "bounce";
    /// <summary>QC HITTYPE_ARMORPIERCE (BIT 11) — ignore armor in the health/armor split.</summary>
    public const string ArmorPierce = "armorpierce";
    /// <summary>QC HITTYPE_SOUND (BIT 12) — sound-based attack (causes ear bleeding; never hits teammates).</summary>
    public const string Sound = "sound";
    /// <summary>QC HITTYPE_SPAM (BIT 13) — set after the first RadiusDamage to stop effect spam.</summary>
    public const string Spam = "spam";

    private const char HitTypeSeparator = '|';

    /// <summary>
    /// Build a weapon deathtype tag from a weapon's NetName (QC DEATH_WEAPONOF — the weapon id packed into
    /// the deathtype). Falls back to <see cref="Generic"/> when the NetName is empty.
    /// </summary>
    public static string FromWeapon(string weaponNetName)
        => string.IsNullOrEmpty(weaponNetName) ? Generic : WeaponPrefix + weaponNetName;

    /// <summary>The portion of a deathtype before any HITTYPE suffixes (the weapon/special base tag).</summary>
    public static string BaseOf(string? deathType)
    {
        if (string.IsNullOrEmpty(deathType))
            return Generic;
        int bar = deathType.IndexOf(HitTypeSeparator);
        return bar < 0 ? deathType : deathType[..bar];
    }

    /// <summary>True if <paramref name="deathType"/> was produced by <see cref="FromWeapon"/> (QC DEATH_WEAPONOF != WEP_Null).</summary>
    public static bool IsWeapon(string? deathType)
        => BaseOf(deathType).StartsWith(WeaponPrefix, System.StringComparison.Ordinal);

    /// <summary>The weapon NetName carried by a weapon deathtype, or "" for special/empty deaths.</summary>
    public static string WeaponNetNameOf(string? deathType)
    {
        string b = BaseOf(deathType);
        return b.StartsWith(WeaponPrefix, System.StringComparison.Ordinal) ? b[WeaponPrefix.Length..] : "";
    }

    /// <summary>
    /// QC DEATH_ISSPECIAL(t): a "special" (non-weapon) deathtype — anything that is NOT a weapon hit.
    /// The global weapon damage/force factors only apply when this is false.
    /// </summary>
    public static bool IsSpecial(string? deathType) => !IsWeapon(deathType);

    /// <summary>OR a HITTYPE flag onto a deathtype tag (QC <c>deathtype | HITTYPE_*</c>). Idempotent.</summary>
    public static string WithHitType(string deathType, string hitType)
        => HasHitType(deathType, hitType) ? deathType : deathType + HitTypeSeparator + hitType;

    /// <summary>True if the deathtype carries the given HITTYPE flag (QC <c>deathtype &amp; HITTYPE_*</c>).</summary>
    public static bool HasHitType(string? deathType, string hitType)
    {
        if (string.IsNullOrEmpty(deathType))
            return false;
        int idx = 0;
        while ((idx = deathType.IndexOf(HitTypeSeparator, idx)) >= 0)
        {
            idx++;
            int end = deathType.IndexOf(HitTypeSeparator, idx);
            if (end < 0) end = deathType.Length;
            if (string.CompareOrdinal(deathType, idx, hitType, 0, System.Math.Max(end - idx, hitType.Length)) == 0
                && (end - idx) == hitType.Length)
                return true;
            idx = end;
        }
        return false;
    }

    /// <summary>
    /// QC DEATH_IS(deathtype, DEATH_DROWN) || (deathtype &amp; HITTYPE_ARMORPIERCE): drowning and any
    /// armor-piercing hit bypass armor entirely in healtharmor_applydamage (armorblock forced to 0).
    /// </summary>
    public static bool BypassesArmor(string? deathType)
        => BaseOf(deathType) == Drown || HasHitType(deathType, ArmorPierce);

    /// <summary>
    /// QC <c>deathtype == DEATH_FIRE || DEATH_BUFF_INFERNO || DEATH_BUFF_VENGEANCE</c>: these burn/buff
    /// deaths are excluded from the normal hit-sound / damage-credit accounting in Damage().
    /// </summary>
    public static bool IsFireOrBuff(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == Fire || b == BuffInferno || b == BuffVengeance;
    }

    /// <summary>
    /// QC <c>ITEM_DAMAGE_NEEDKILL(dt)</c> (server/items/items.qh:123): the environmental-kill deathtypes a
    /// dropped objective (CTF flag / Keepaway ball / KeyHunt key) must respawn on — DEATH_HURTTRIGGER
    /// (= the port's <see cref="Void"/>), DEATH_SLIME, DEATH_LAVA, DEATH_SWAMP. Used by ka_DamageEvent to
    /// teleport a ball out of a hurt/lava/slime/swamp volume it fell into.
    /// </summary>
    public static bool ItemDamageNeedKill(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == Void || b == Slime || b == Lava || b == Swamp;
    }

    /// <summary>QC: DEATH_KILL / DEATH_TEAMCHANGE / DEATH_AUTOTEAMCHANGE — the unconditionally-lethal deaths.</summary>
    public static bool IsAlwaysLethal(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == Kill || b == TeamChange || b == AutoTeamChange;
    }

    /// <summary>QC: DEATH_TEAMCHANGE / DEATH_AUTOTEAMCHANGE — the team-change family (armor/health pre-zeroed).</summary>
    public static bool IsTeamChange(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == TeamChange || b == AutoTeamChange;
    }

    /// <summary>QC: DEATH_AUTOTEAMCHANGE only — the auto-balance team move (server/damage.qc:304 special-cases it:
    /// the suicide frag is NOT negated when the player was auto-switched, unlike a manual DEATH_TEAMCHANGE).</summary>
    public static bool IsAutoTeamChange(string? deathType) => BaseOf(deathType) == AutoTeamChange;

    // ============================================================================================
    //  Special deathtype registry + .message category (QC deathtypes/all.qh DEATH_ISMONSTER/
    //  ISTURRET/ISVEHICLE + all.inc). Replaces brittle substring obituary checks with a registry
    //  keyed by the special deathtype's base name.
    // ============================================================================================

    // Base-name tags for the categorized special deaths (QC m_name; lower-cased to match the BaseOf scheme).
    // These mirror the REGISTER_DEATHTYPE rows in all.inc that carry "monster"/"turret"/"vehicle".

    /// <summary>QC DEATH_MONSTER_MAGE — Mage monster kill ("monster").</summary>
    public const string MonsterMage = "monster_mage";
    /// <summary>QC DEATH_MONSTER_GOLEM_CLAW — Golem claw ("monster").</summary>
    public const string MonsterGolemClaw = "monster_golem_claw";
    /// <summary>QC DEATH_MONSTER_GOLEM_SMASH — Golem smash ("monster").</summary>
    public const string MonsterGolemSmash = "monster_golem_smash";
    /// <summary>QC DEATH_MONSTER_GOLEM_ZAP — Golem zap ("monster").</summary>
    public const string MonsterGolemZap = "monster_golem_zap";
    /// <summary>QC DEATH_MONSTER_SPIDER — Spider monster ("monster").</summary>
    public const string MonsterSpider = "monster_spider";
    /// <summary>QC DEATH_MONSTER_WYVERN — Wyvern monster ("monster").</summary>
    public const string MonsterWyvern = "monster_wyvern";
    /// <summary>QC DEATH_MONSTER_ZOMBIE_JUMP — Zombie jump ("monster").</summary>
    public const string MonsterZombieJump = "monster_zombie_jump";
    /// <summary>QC DEATH_MONSTER_ZOMBIE_MELEE — Zombie melee ("monster").</summary>
    public const string MonsterZombieMelee = "monster_zombie_melee";

    /// <summary>QC DEATH_TURRET — generic turret ("turret").</summary>
    public const string Turret = "turret";

    // Per-turret deathtypes (QC DEATH_TURRET_*; all carry .message "turret"). Each turret attack tags its
    // damage with its own row so the obituary self line is turret-specific (the murder line is the shared
    // DEATH_MURDER_CHEAT). Mirror the all.inc rows; used by the turret attack call sites + BuildRegistry.
    /// <summary>QC DEATH_TURRET_EWHEEL — eWheel turret blaster ("turret").</summary>
    public const string TurretEwheel = "turret_ewheel";
    /// <summary>QC DEATH_TURRET_FLAC — Flac turret air-burst ("turret").</summary>
    public const string TurretFlac = "turret_flac";
    /// <summary>QC DEATH_TURRET_HELLION — Hellion turret rocket ("turret").</summary>
    public const string TurretHellion = "turret_hellion";
    /// <summary>QC DEATH_TURRET_HK — Hunter-Killer turret rocket ("turret").</summary>
    public const string TurretHk = "turret_hk";
    /// <summary>QC DEATH_TURRET_MACHINEGUN — Machinegun turret bullet ("turret").</summary>
    public const string TurretMachinegun = "turret_machinegun";
    /// <summary>QC DEATH_TURRET_MLRS — MLRS turret rocket ("turret").</summary>
    public const string TurretMlrs = "turret_mlrs";
    /// <summary>QC DEATH_TURRET_PHASER — Phaser turret beam ("turret").</summary>
    public const string TurretPhaser = "turret_phaser";
    /// <summary>QC DEATH_TURRET_PLASMA — Plasma (+ dual plasma) turret bolt / instagib rail ("turret").</summary>
    public const string TurretPlasma = "turret_plasma";
    /// <summary>QC DEATH_TURRET_TESLA — Tesla coil turret zap ("turret").</summary>
    public const string TurretTesla = "turret_tesla";
    /// <summary>QC DEATH_TURRET_WALK_GUN — Walker turret gun ("turret").</summary>
    public const string TurretWalkGun = "turret_walk_gun";
    /// <summary>QC DEATH_TURRET_WALK_MELEE — Walker turret melee bite ("turret").</summary>
    public const string TurretWalkMelee = "turret_walk_melee";
    /// <summary>QC DEATH_TURRET_WALK_ROCKET — Walker turret rocket ("turret").</summary>
    public const string TurretWalkRocket = "turret_walk_rocket";

    /// <summary>QC DEATH_VH_* — generic vehicle death ("vehicle"). Use the registry for the full per-vehicle set.</summary>
    public const string Vehicle = "vehicle";

    // Per-vehicle deathtypes (QC DEATH_VH_*; all carry .message "vehicle"). Each vehicle weapon/death tags its
    // damage with its own row so the obituary picks that vehicle's self/murder line. Mirror the all.inc rows;
    // used by the vehicle attack call sites + BuildRegistry. (Racer == QC "waki", Spiderbot == "spid".)
    /// <summary>QC DEATH_VH_BUMB_DEATH — Bumblebee death explosion ("vehicle").</summary>
    public const string VhBumbDeath = "vh_bumb_death";
    /// <summary>QC DEATH_VH_BUMB_GUN — Bumblebee side-gunner plasma cannon ("vehicle").</summary>
    public const string VhBumbGun = "vh_bumb_gun";
    /// <summary>QC DEATH_VH_CRUSH — vehicle ram/crush ("vehicle").</summary>
    public const string VhCrush = "vh_crush";
    /// <summary>QC DEATH_VH_RAPT_BOMB — Raptor cluster bomblet ("vehicle").</summary>
    public const string VhRaptBomb = "vh_rapt_bomb";
    /// <summary>QC DEATH_VH_RAPT_CANNON — Raptor twin cannon ("vehicle").</summary>
    public const string VhRaptCannon = "vh_rapt_cannon";
    /// <summary>QC DEATH_VH_RAPT_DEATH — Raptor death explosion ("vehicle").</summary>
    public const string VhRaptDeath = "vh_rapt_death";
    /// <summary>QC DEATH_VH_RAPT_FRAGMENT — Raptor bomb fragment ("vehicle").</summary>
    public const string VhRaptFragment = "vh_rapt_fragment";
    /// <summary>QC DEATH_VH_SPID_DEATH — Spiderbot death explosion ("vehicle").</summary>
    public const string VhSpidDeath = "vh_spid_death";
    /// <summary>QC DEATH_VH_SPID_MINIGUN — Spiderbot minigun ("vehicle").</summary>
    public const string VhSpidMinigun = "vh_spid_minigun";
    /// <summary>QC DEATH_VH_SPID_ROCKET — Spiderbot rocket ("vehicle").</summary>
    public const string VhSpidRocket = "vh_spid_rocket";
    /// <summary>QC DEATH_VH_WAKI_DEATH — Racer (waki) death explosion ("vehicle").</summary>
    public const string VhWakiDeath = "vh_waki_death";
    /// <summary>QC DEATH_VH_WAKI_GUN — Racer (waki) energy cannon ("vehicle").</summary>
    public const string VhWakiGun = "vh_waki_gun";
    /// <summary>QC DEATH_VH_WAKI_ROCKET — Racer (waki) rocket ("vehicle").</summary>
    public const string VhWakiRocket = "vh_waki_rocket";

    /// <summary>
    /// The categorized special-deathtype registry — the C# successor to the QC <c>Deathtypes</c> registry
    /// rows that carry a non-empty <c>.message</c> (deathtypes/all.inc). Keyed by the base-name tag (lower
    /// case, matching <see cref="BaseOf"/>). The monster set is enumerated explicitly (each MONSTER_* row);
    /// the turret/vehicle families are enumerated and ALSO matched by their "turret"/"vh_" base-name prefix
    /// so a per-turret/vehicle tag (e.g. "turret_mlrs", "vh_waki_gun") is recognized without listing all 30+
    /// QC rows. This is what <see cref="IsMonster"/>/<see cref="IsTurret"/>/<see cref="IsVehicle"/> consult,
    /// replacing the brittle substring scans the obituary code used before.
    /// </summary>
    private static readonly Dictionary<string, DeathTypeDef> _registry = BuildRegistry();

    private static Dictionary<string, DeathTypeDef> BuildRegistry()
    {
        var d = new Dictionary<string, DeathTypeDef>(System.StringComparer.Ordinal);
        // QC REGISTER_DEATHTYPE(id, death_msgself, death_msgmurder, …, extra) — the all.inc cols 2/3 carry the
        // bare DEATH_SELF_*/DEATH_MURDER_* notification each row sends (NULL → generic fallback). We store those
        // names verbatim so SelectSpecial reproduces Obituary_SpecialDeath's registered selection exactly,
        // rather than guessing from the tag.
        void Reg(string name, DeathCategory cat, string? self, string? murder)
            => d[name] = new DeathTypeDef(name, cat, self, murder);

        // QC all.inc: special deaths that register both self and murder notification (or one of them).
        // camp (all.inc:4): DEATH_SELF_CAMP self-only (NULL murder -> FRAG below), the campcheck anti-camping mutator.
        Reg(Camp, DeathCategory.None, "DEATH_SELF_CAMP", null);

        // QC all.inc monster rows (message == "monster"): each monster has its OWN DEATH_SELF_MON_* self line;
        // ALL monsters share DEATH_MURDER_MONSTER for the murder line.
        Reg(MonsterMage,       DeathCategory.Monster, "DEATH_SELF_MON_MAGE",         "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemClaw,  DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_CLAW",   "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemSmash, DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_SMASH",  "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemZap,   DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_ZAP",    "DEATH_MURDER_MONSTER");
        Reg(MonsterSpider,     DeathCategory.Monster, "DEATH_SELF_MON_SPIDER",       "DEATH_MURDER_MONSTER");
        Reg(MonsterWyvern,     DeathCategory.Monster, "DEATH_SELF_MON_WYVERN",       "DEATH_MURDER_MONSTER");
        Reg(MonsterZombieJump, DeathCategory.Monster, "DEATH_SELF_MON_ZOMBIE_JUMP",  "DEATH_MURDER_MONSTER");
        Reg(MonsterZombieMelee,DeathCategory.Monster, "DEATH_SELF_MON_ZOMBIE_MELEE", "DEATH_MURDER_MONSTER");

        // QC all.inc turret rows (message == "turret"): each turret has its OWN DEATH_SELF_TURRET_* self line;
        // ALL turrets share DEATH_MURDER_CHEAT for the murder line. The bare "turret" tag uses DEATH_SELF_TURRET;
        // the per-turret variants below carry their own self line (resolved through the explicit rows here, with
        // the "turret" base-name prefix in Lookup catching any unlisted variant → bare DEATH_SELF_TURRET).
        Reg(Turret,            DeathCategory.Turret, "DEATH_SELF_TURRET",               "DEATH_MURDER_CHEAT");
        Reg(TurretEwheel,      DeathCategory.Turret, "DEATH_SELF_TURRET_EWHEEL",        "DEATH_MURDER_CHEAT");
        Reg(TurretFlac,        DeathCategory.Turret, "DEATH_SELF_TURRET_FLAC",          "DEATH_MURDER_CHEAT");
        Reg(TurretHellion,     DeathCategory.Turret, "DEATH_SELF_TURRET_HELLION",       "DEATH_MURDER_CHEAT");
        Reg(TurretHk,          DeathCategory.Turret, "DEATH_SELF_TURRET_HK",            "DEATH_MURDER_CHEAT");
        Reg(TurretMachinegun,  DeathCategory.Turret, "DEATH_SELF_TURRET_MACHINEGUN",    "DEATH_MURDER_CHEAT");
        Reg(TurretMlrs,        DeathCategory.Turret, "DEATH_SELF_TURRET_MLRS",          "DEATH_MURDER_CHEAT");
        Reg(TurretPhaser,      DeathCategory.Turret, "DEATH_SELF_TURRET_PHASER",        "DEATH_MURDER_CHEAT");
        Reg(TurretPlasma,      DeathCategory.Turret, "DEATH_SELF_TURRET_PLASMA",        "DEATH_MURDER_CHEAT");
        Reg(TurretTesla,       DeathCategory.Turret, "DEATH_SELF_TURRET_TESLA",         "DEATH_MURDER_CHEAT");
        Reg(TurretWalkGun,     DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_GUN",      "DEATH_MURDER_CHEAT");
        Reg(TurretWalkMelee,   DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_MELEE",    "DEATH_MURDER_CHEAT");
        Reg(TurretWalkRocket,  DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_ROCKET",   "DEATH_MURDER_CHEAT");

        // QC all.inc vehicle rows (message == "vehicle"): each VH_* row carries its OWN self AND murder line
        // (some are NULL — e.g. a vehicle GUN has murder-only). The generic "vehicle" tag falls back to GENERIC
        // (no QC row registers a bare "vehicle"); the per-vehicle rows below are the real all.inc entries, and
        // the "vh_" base-name prefix in Lookup catches any unlisted variant → generic.
        Reg(Vehicle,        DeathCategory.Vehicle, null, null);
        Reg(VhBumbDeath,    DeathCategory.Vehicle, "DEATH_SELF_VH_BUMB_DEATH",  "DEATH_MURDER_VH_BUMB_DEATH");
        Reg(VhBumbGun,      DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_BUMB_GUN");
        Reg(VhCrush,        DeathCategory.Vehicle, "DEATH_SELF_VH_CRUSH",       "DEATH_MURDER_VH_CRUSH");
        Reg(VhRaptBomb,     DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_BOMB",   "DEATH_MURDER_VH_RAPT_BOMB");
        Reg(VhRaptCannon,   DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_RAPT_CANNON");
        Reg(VhRaptDeath,    DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_DEATH",  "DEATH_MURDER_VH_RAPT_DEATH");
        Reg(VhRaptFragment, DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_BOMB",   "DEATH_MURDER_VH_RAPT_BOMB");
        Reg(VhSpidDeath,    DeathCategory.Vehicle, "DEATH_SELF_VH_SPID_DEATH",  "DEATH_MURDER_VH_SPID_DEATH");
        Reg(VhSpidMinigun,  DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_SPID_MINIGUN");
        Reg(VhSpidRocket,   DeathCategory.Vehicle, "DEATH_SELF_VH_SPID_ROCKET", "DEATH_MURDER_VH_SPID_ROCKET");
        Reg(VhWakiDeath,    DeathCategory.Vehicle, "DEATH_SELF_VH_WAKI_DEATH",  "DEATH_MURDER_VH_WAKI_DEATH");
        Reg(VhWakiGun,      DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_WAKI_GUN");
        Reg(VhWakiRocket,   DeathCategory.Vehicle, "DEATH_SELF_VH_WAKI_ROCKET", "DEATH_MURDER_VH_WAKI_ROCKET");

        return d;
    }

    /// <summary>Prefix marking the per-turret deathtype variants (QC TURRET_* rows, message "turret").</summary>
    private const string TurretPrefix = "turret";

    /// <summary>Prefix marking the per-vehicle deathtype variants (QC VH_* rows, message "vehicle").</summary>
    private const string VehiclePrefix = "vh_";

    /// <summary>
    /// QC the registered deathtype entity for a tag (DEATH_ENT(t)): the <see cref="DeathTypeDef"/> if the
    /// base name is a registered special death, else null. The per-turret/vehicle variants resolve to the
    /// generic turret/vehicle def via the name-prefix match.
    /// </summary>
    public static DeathTypeDef? Lookup(string? deathType)
    {
        string b = BaseOf(deathType);
        if (_registry.TryGetValue(b, out var def))
            return def;
        if (b.StartsWith(TurretPrefix, System.StringComparison.Ordinal))
            return _registry[Turret];
        if (b.StartsWith(VehiclePrefix, System.StringComparison.Ordinal))
            return _registry[Vehicle];
        return null;
    }

    /// <summary>QC the <c>.message</c> category for a deathtype, or <see cref="DeathCategory.None"/>.</summary>
    public static DeathCategory CategoryOf(string? deathType) => Lookup(deathType)?.Category ?? DeathCategory.None;

    /// <summary>
    /// QC <c>DEATH_ISMONSTER(t)</c> (deathtypes/all.qh:46): the deathtype's <c>.message == "monster"</c>.
    /// Replaces the old substring scan for monster kills in the obituary phrasing.
    /// </summary>
    public static bool IsMonster(string? deathType) => CategoryOf(deathType) == DeathCategory.Monster;

    /// <summary>
    /// QC <c>DEATH_ISTURRET(t)</c> (deathtypes/all.qh:45): the deathtype's <c>.message == "turret"</c>.
    /// Recognizes the bare "turret" tag and every "turret_*" variant.
    /// </summary>
    public static bool IsTurret(string? deathType) => CategoryOf(deathType) == DeathCategory.Turret;

    /// <summary>
    /// QC <c>DEATH_ISVEHICLE(t)</c> (deathtypes/all.qh:44): the deathtype's <c>.message == "vehicle"</c>.
    /// Recognizes the bare "vehicle" tag and every "vh_*" variant.
    /// </summary>
    public static bool IsVehicle(string? deathType) => CategoryOf(deathType) == DeathCategory.Vehicle;
}
