namespace XonoticGodot.Common.Gameplay;

// The sound table — a C# port of QuakeC's SOUND(...) tables and the GlobalSound / PlayerSound / announcer
// registries. Sources:
//   * common/sounds/all.inc                      — the global SOUND() table (weapons, gametypes, vehicles…)
//   * common/effects/qc/globalsound.qh           — REGISTER_GLOBALSOUND, REGISTER_PLAYERSOUND, REGISTER_VOICEMSG
//   * common/notifications/all.inc               — MSG_ANNCE_NOTIF announcer samples
//
// Path conventions reproduced from QC:
//   W_Sound("x")    => "weapons/x"   (common/weapons/all.qc)
//   Item_Sound("x") => "misc/x"      (common/items/all.qc)
//   announcer       => "announcer/default/<name>"  (default voice pack; AnnouncerFilename in cl_announcer)
//   player sounds   => "sound/<model>/<id>"; the model dir is resolved per character at runtime — see
//                      SoundSystem.PlayPlayerSound / Sounds.PlayerSoundSample.
//
// Coverage: the full all.inc SOUND() set (weapons, every gametype: CTF incl. per-team capture/dropped/
// returned/taken, DOM, KA, KH, NB, ONS; vehicles/turrets/monsters; items; misc/world), the GlobalSound
// "<base> <count>" variant entries, the explicit random-variant GROUPS (RIC/NEXWHOOSH/GRENADE_BOUNCE/
// FLACEXP/GIB_SPLAT) wired through SoundSystem's group pickers, the player-sound set, the voice-message
// set, and the announcer set.

/// <summary>
/// Registers the sound set into <see cref="Sounds"/>. Called once via <see cref="Sounds.RegisterAll"/>
/// (which the lead wires into GameInit). Each <c>Add</c> mirrors one QC <c>SOUND(...)</c> / <c>REGISTER_*</c> line.
/// </summary>
public static class SoundsList
{
    // path-prefix helpers, matching QC W_Sound / Item_Sound
    private static string W(string s) => "weapons/" + s;
    private static string Item(string s) => "misc/" + s;
    private static string Ann(string s) => "announcer/default/" + s;

    private static GameSound Add(
        string name,
        string sample,
        SoundChannelHint ch = SoundChannelHint.Info,
        float vol = SoundLevels.VolBase,
        float atten = SoundLevels.AttenNorm,
        bool single = false)
        => Sounds.Register(new GameSound(name, sample, ch, vol, atten, single));

    public static void RegisterAll()
    {
        RegisterWeaponSounds();
        RegisterGametypeSounds();
        RegisterVehicleTurretMonsterSounds();
        RegisterNadeSounds();
        RegisterItemSounds();
        RegisterMiscWorldSounds();
        RegisterGlobalSounds();
        RegisterPlayerSounds();
        RegisterVoiceMessages();
        RegisterAnnouncerSounds();
    }

    // --- weapon sounds (all.inc, W_Sound) ---
    private static void RegisterWeaponSounds()
    {
        Add("DRYFIRE", W("dryfire"), SoundChannelHint.Weapon);
        // grenade bounce variants 1..6 (SND_GRENADE_BOUNCE_RANDOM picks one — SoundVariantGroups.GrenadeBounce)
        for (int i = 1; i <= 6; i++)
            Add($"GRENADE_BOUNCE{i}", W($"grenade_bounce{i}"), SoundChannelHint.Weapon);
        Add("LASERIMPACT", W("laserimpact"), SoundChannelHint.Weapon);
        for (int i = 1; i <= 3; i++)
            Add($"NEXWHOOSH{i}", W($"nexwhoosh{i}"), SoundChannelHint.Weapon);
        Add("RELOAD", W("reload"), SoundChannelHint.Weapon);
        for (int i = 1; i <= 3; i++)
            Add($"RIC{i}", W($"ric{i}"), SoundChannelHint.Weapon);
        Add("ROCKET_IMPACT", W("rocket_impact"), SoundChannelHint.Weapon); // generic explosion
        Add("STRENGTH_FIRE", W("strength_fire"), SoundChannelHint.Weapon);
        Add("UNAVAILABLE", W("unavailable"), SoundChannelHint.Weapon);
        Add("WEAPONPICKUP", W("weaponpickup"), SoundChannelHint.Item);
        Add("WEAPONPICKUP_NEW_TOYS", W("weaponpickup_new_toys"), SoundChannelHint.Item);
        Add("WEAPON_SWITCH", W("weapon_switch"), SoundChannelHint.Weapon);
        // FLAC explosion variants 1..3 (SND_FLACEXP_RANDOM)
        for (int i = 1; i <= 3; i++)
            Add($"FLACEXP{i}", W($"hagexp{i}"), SoundChannelHint.Weapon);
    }

    // --- gametype sounds (all.inc: CTF / DOM / KA / KH / NB / ONS) — complete ---
    private static void RegisterGametypeSounds()
    {
        // CTF — neutral + per-team for capture/dropped/returned/taken (teams 1..4 = red/blue/yellow/pink).
        foreach (var t in new[] { "NEUTRAL", "RED", "BLUE", "YELLOW", "PINK" })
        {
            string p = t.ToLowerInvariant();
            Add($"CTF_CAPTURE_{t}", t == "NEUTRAL" ? "ctf/capture" : $"ctf/{p}_capture");
            Add($"CTF_DROPPED_{t}", t == "NEUTRAL" ? "ctf/neutral_dropped" : $"ctf/{p}_dropped");
            Add($"CTF_RETURNED_{t}", t == "NEUTRAL" ? "ctf/return" : $"ctf/{p}_returned");
            Add($"CTF_TAKEN_{t}", t == "NEUTRAL" ? "ctf/neutral_taken" : $"ctf/{p}_taken");
        }
        Add("CTF_PASS", "ctf/pass");
        Add("CTF_RESPAWN", "ctf/flag_respawn");
        Add("CTF_TOUCH", "ctf/touch");

        Add("DOM_CLAIM", "domination/claim");

        Add("KA_DROPPED", "keepaway/dropped");
        Add("KA_PICKEDUP", "keepaway/pickedup");
        Add("KA_RESPAWN", "keepaway/respawn");
        Add("KA_TOUCH", "keepaway/touch");

        Add("KH_ALARM", "kh/alarm");
        Add("KH_CAPTURE", "kh/capture");
        Add("KH_COLLECT", "kh/collect");
        Add("KH_DESTROY", "kh/destroy");
        Add("KH_DROP", "kh/drop");

        Add("NB_BOUNCE", "nexball/bounce");
        Add("NB_DROP", "nexball/drop");
        // QC SpawnGoal default goal noise (sv_nexball.qc): this.noise = "ctf/respawn.wav" — the score cue. Faithful
        // to the QC default sample (NOT kh/capture). fault/out goals override noise with SND(TYPEHIT).
        Add("NB_GOAL", "ctf/respawn");
        Add("NB_SHOOT1", "nexball/shoot1");
        Add("NB_SHOOT2", "nexball/shoot2");
        Add("NB_STEAL", "nexball/steal");

        // Onslaught — full set. (ONS_GENERATOR_ALARM aliases KH_ALARM in QC; we keep the alias as a copy.)
        Add("ONS_CONTROLPOINT_BUILD", "onslaught/controlpoint_build");
        Add("ONS_CONTROLPOINT_BUILT", "onslaught/controlpoint_built");
        Add("ONS_CONTROLPOINT_UNDERATTACK", "onslaught/controlpoint_underattack");
        Add("ONS_DAMAGEBLOCKEDBYSHIELD", "onslaught/damageblockedbyshield");
        Add("ONS_ELECTRICITY_EXPLODE", "onslaught/electricity_explode");
        Add("ONS_GENERATOR_ALARM", "kh/alarm");      // #define SND_ONS_GENERATOR_ALARM SND_KH_ALARM
        Add("ONS_GENERATOR_DECAY", "onslaught/generator_decay");
        Add("ONS_GENERATOR_UNDERATTACK", "onslaught/generator_underattack");
        Add("ONS_GENERATOR_EXPLODE", W("grenade_impact"));
        Add("ONS_HIT1", "onslaught/ons_hit1");
        Add("ONS_HIT2", "onslaught/ons_hit2");
        Add("ONS_SPARK1", "onslaught/ons_spark1");
        Add("ONS_SPARK2", "onslaught/ons_spark2");
        Add("ONS_SHOCKWAVE", "onslaught/shockwave");

        // status-effect removal sounds (status_effect/*.qh SOUND() defs). m_sound_rm references these:
        // burning -> SND_Burning_Remove (the steam-burst hiss), stunned -> SND_Stunned_Remove (the spark snap,
        // same sample as ONS_SPARK1). Registered under their Base SND_* def names.
        Add("BURNING_REMOVE", "desertfactory/steam_burst", SoundChannelHint.Item); // SOUND(Burning_Remove, ...)
        Add("STUNNED_REMOVE", "onslaught/ons_spark1", SoundChannelHint.Item);       // SOUND(Stunned_Remove, ...)
    }

    // --- vehicles / turrets / monsters (all.inc) ---
    private static void RegisterVehicleTurretMonsterSounds()
    {
        Add("MON_GOLEM_LIGHTNING_IMPACT", W("electro_impact"), SoundChannelHint.Weapon);

        Add("TUR_PHASER", "turrets/phaser", SoundChannelHint.Weapon);
        Add("TUR_PLASMA_IMPACT", W("electro_impact"), SoundChannelHint.Weapon);
        Add("TUR_WALKER_FIRE", W("hagar_fire"), SoundChannelHint.Weapon);

        Add("VEH_ALARM", "vehicles/alarm");
        Add("VEH_ALARM_SHIELD", "vehicles/alarm_shield");
        Add("VEH_MISSILE_ALARM", "vehicles/missile_alarm");

        Add("VEH_BUMBLEBEE_FIRE", W("flacexp3"), SoundChannelHint.Weapon);
        Add("VEH_BUMBLEBEE_IMPACT", W("fireball_impact2"), SoundChannelHint.Weapon);

        Add("VEH_RACER_BOOST", "vehicles/racer_boost");
        Add("VEH_RACER_IDLE", "vehicles/racer_idle");
        Add("VEH_RACER_MOVE", "vehicles/racer_move");
        Add("VEH_RACER_ROCKET_FLY", W("tag_rocket_fly"), SoundChannelHint.Weapon);

        Add("VEH_RAPTOR_FLY", "vehicles/raptor_fly");
        Add("VEH_RAPTOR_SPEED", "vehicles/raptor_speed");

        Add("VEH_SPIDERBOT_DIE", "vehicles/spiderbot_die");
        Add("VEH_SPIDERBOT_IDLE", "vehicles/spiderbot_idle");
        Add("VEH_SPIDERBOT_JUMP", "vehicles/spiderbot_jump");
        Add("VEH_SPIDERBOT_LAND", "vehicles/spiderbot_land");
        Add("VEH_SPIDERBOT_STRAFE", "vehicles/spiderbot_strafe");
        Add("VEH_SPIDERBOT_WALK", "vehicles/spiderbot_walk");
        Add("VEH_SPIDERBOT_MINIGUN_FIRE", W("uzi_fire"), SoundChannelHint.Weapon);
        Add("VEH_SPIDERBOT_ROCKET_FLY", W("tag_rocket_fly"), SoundChannelHint.Weapon);
        Add("VEH_SPIDERBOT_ROCKET_FIRE", W("rocket_fire"), SoundChannelHint.Weapon);
    }

    // --- nades (all.inc; some alias other sounds in QC) ---
    private static void RegisterNadeSounds()
    {
        Add("NADE_BEEP", "overkill/grenadebip", SoundChannelHint.Item);
        Add("NADE_BONUS", "kh/alarm", SoundChannelHint.Item);             // #define SND_NADE_BONUS SND_KH_ALARM
        Add("NADE_NAPALM_FIRE", W("fireball_fire"), SoundChannelHint.Weapon); // alias SND_FIREBALL_FIRE
        Add("NADE_NAPALM_FLY", W("fireball_fly2"), SoundChannelHint.Weapon);  // alias SND_FIREBALL_FLY2
        Add("BUFF_LOST", "relics/relic_effect", SoundChannelHint.Item);
    }

    // --- items / powerups (all.inc, Item_Sound => misc/) ---
    private static void RegisterItemSounds()
    {
        Add("POWEROFF", Item("poweroff"), SoundChannelHint.Item);
        Add("POWERUP", Item("powerup"), SoundChannelHint.Item);
        Add("SHIELD_RESPAWN", Item("shield_respawn"), SoundChannelHint.Item);
        Add("STRENGTH_RESPAWN", Item("strength_respawn"), SoundChannelHint.Item);
        Add("ARMORIMPACT", "misc/armorimpact", SoundChannelHint.Body);
        Add("BODYIMPACT1", "misc/bodyimpact1", SoundChannelHint.Body);
        Add("BODYIMPACT2", "misc/bodyimpact2", SoundChannelHint.Body);
        Add("ITEMPICKUP", Item("itempickup"), SoundChannelHint.Item);
        Add("ITEMRESPAWNCOUNTDOWN", Item("itemrespawncountdown"), SoundChannelHint.Item);
        Add("ITEMRESPAWN", Item("itemrespawn"), SoundChannelHint.Item);

        // Per-size health/armor pickup sounds — the SOUND() tables in common/items/item/health.qh + armor.qh.
        // Each item def (HealthSmall.., ArmorSmall..) sets ItemDef.PickupSound to one of these NAMES; the pickup
        // path resolves the name to a GameSound and plays its sample. Without the registration the name resolves
        // to nothing and the pickup is SILENT — which is why only HealthMega made a sound (its def uses the
        // legacy "MEGAHEALTH" alias kept below); small/medium/big health & armor and mega armor were all silent.
        // All eight samples ship under sound/misc/.
        Add("HealthSmall",  Item("minihealth"),   SoundChannelHint.Item); // SOUND(HealthSmall,  "minihealth")
        Add("HealthMedium", Item("mediumhealth"), SoundChannelHint.Item); // SOUND(HealthMedium, "mediumhealth")
        Add("HealthBig",    Item("mediumhealth"), SoundChannelHint.Item); // SOUND(HealthBig,    "mediumhealth")
        Add("HealthMega",   Item("megahealth"),   SoundChannelHint.Item); // SOUND(HealthMega,   "megahealth")
        Add("ArmorSmall",   Item("armor1"),       SoundChannelHint.Item); // SOUND(ArmorSmall,   "armor1")
        Add("ArmorMedium",  Item("armor10"),      SoundChannelHint.Item); // SOUND(ArmorMedium,  "armor10")
        Add("ArmorBig",     Item("armor17_5"),    SoundChannelHint.Item); // SOUND(ArmorBig,     "armor17_5")
        Add("ArmorMega",    Item("armor25"),      SoundChannelHint.Item); // SOUND(ArmorMega,    "armor25")

        // Powerup pickup sounds — the SOUND() tables in common/mutators/mutator/powerups/powerup/*.qh. The
        // powerup item defs (StrengthItem/ShieldItem/SpeedItem/InvisibilityItem) set ItemDef.PickupSound to one
        // of these NAMES; unregistered => silent pickup (same trap as the health/armor sizes). Both samples ship
        // under sound/misc/. (Jetpack + FuelRegen use the default "ITEMPICKUP", already registered above.)
        Add("Strength",     Item("powerup"),        SoundChannelHint.Item); // SOUND(Strength,     "powerup")
        Add("Shield",       Item("powerup_shield"), SoundChannelHint.Item); // SOUND(Shield,       "powerup_shield")
        Add("Speed",        Item("powerup_shield"), SoundChannelHint.Item); // SOUND(Speed,        "powerup_shield")
        Add("Invisibility", Item("powerup"),        SoundChannelHint.Item); // SOUND(Invisibility, "powerup")

        // Legacy alias: HealthMega's item def + ItemSpawnTouchTests reference "MEGAHEALTH" (same sample as the
        // faithful "HealthMega" name above).
        Add("MEGAHEALTH",   Item("megahealth"),   SoundChannelHint.Item);
    }

    // --- misc world sounds (all.inc: misc/, player/) ---
    private static void RegisterMiscWorldSounds()
    {
        Add("LAVA", "player/lava", SoundChannelHint.Body);
        Add("SLIME", "player/slime", SoundChannelHint.Body);
        Add("GIB", "misc/gib", SoundChannelHint.Body);
        for (int i = 1; i <= 4; i++)
            Add($"GIB_SPLAT0{i}", $"misc/gib_splat0{i}", SoundChannelHint.Body);
        Add("HIT", "misc/hit", SoundChannelHint.Info);
        Add("TYPEHIT", "misc/typehit", SoundChannelHint.Info);
        Add("KILL", "misc/kill", SoundChannelHint.Info);
        Add("SPAWN", "misc/spawn", SoundChannelHint.Info);
        Add("TALK", "misc/talk", SoundChannelHint.Info);
        Add("TALK2", "misc/talk2", SoundChannelHint.Info);
        Add("BLIND", "misc/blind", SoundChannelHint.Info);
        Add("TELEPORT", "misc/teleport", SoundChannelHint.Item);
        Add("INVSHOT", "misc/invshot", SoundChannelHint.Weapon);
        Add("JETPACK_FLY", "misc/jetpack_fly", SoundChannelHint.Body);
    }

    // --- GlobalSounds (globalsound.qh: REGISTER_GLOBALSOUND, "<base> <count>" pairs) ---
    // The base path plus a count of numbered variants; the GlobalSound machinery picks "<base><n>.wav"
    // for a random n in 1..count. We register the descriptor with the bare base path and carry the count
    // so SoundSystem.PlayGlobalVariant can build the variant name (SoundVariantGroups.GlobalCounts).
    private static void RegisterGlobalSounds()
    {
        Add("STEP", "misc/footstep0", SoundChannelHint.Body);          // count 6
        Add("STEP_METAL", "misc/metalfootstep0", SoundChannelHint.Body); // count 6
        Add("FALL", "misc/hitground", SoundChannelHint.Body);          // count 4
        Add("FALL_METAL", "misc/metalhitground", SoundChannelHint.Body); // count 4
    }

    // --- PlayerSounds (globalsound.qh: REGISTER_PLAYERSOUND) ---
    // The real file is "sound/<player-model-dir>/<id>"; the model dir is resolved per character at runtime
    // (LoadPlayerSounds reads a model's .sounds file). We register a logical "PLAYER_<id>" with the default
    // pack path; SoundSystem.PlayPlayerSound rebinds the model dir per emitter. See Sounds.PlayerSoundIds.
    private static void RegisterPlayerSounds()
    {
        foreach (var id in Sounds.PlayerSoundIds)
            Add($"PLAYER_{id.ToUpperInvariant()}", $"{Sounds.DefaultPlayerSoundDir}/{id}",
                SoundChannelHint.Body, vol: SoundLevels.VolBaseVoice);
    }

    // --- VoiceMessages (globalsound.qh: REGISTER_VOICEMSG) — team radio + taunts ---
    private static void RegisterVoiceMessages()
    {
        foreach (var m in Sounds.VoiceMessageIds)
            Add($"VOICE_{m.ToUpperInvariant()}", $"{Sounds.DefaultPlayerSoundDir}/{m}",
                SoundChannelHint.Voice, vol: SoundLevels.VolBaseVoice);
    }

    // --- Announcer (notifications/all.inc: MSG_ANNCE_NOTIF) — default voice pack ---
    private static void RegisterAnnouncerSounds()
    {
        void A(string name) => Add($"ANNCE_{name.ToUpperInvariant()}", Ann(name),
            SoundChannelHint.Info, vol: SoundLevels.VolBaseVoice, atten: SoundLevels.AttenNone);

        A("begin");
        A("prepareforbattle");
        for (int n = 1; n <= 10; n++) A(n.ToString());

        foreach (var a in new[]
        {
            "airshot", "amazing", "awesome", "botlike", "electrobitch", "impressive", "yoda",
            "headshot", "multifrag", "lastsecond", "narrowly", "terminated",
        })
            A(a);

        foreach (var k in new[] { "03kills", "05kills", "10kills", "15kills", "20kills", "25kills", "30kills" })
            A(k);

        foreach (var r in new[] { "1fragleft", "2fragsleft", "3fragsleft", "1minuteremains", "5minutesremain" })
            A(r);

        foreach (var v in new[] { "timeoutcalled", "voteaccept", "votecall", "votefail" })
            A(v);
    }
}
