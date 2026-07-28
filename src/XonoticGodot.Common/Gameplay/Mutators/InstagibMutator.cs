using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The InstaGib mutator — port of common/mutators/mutator/instagib/sv_instagib.qc. Everyone spawns with
/// the Vaporizer and (almost) every weapon one-shots; armor acts as extra "lives", the blaster does no
/// damage, and there's no health/armor regen. Enabled by the <c>g_instagib</c> cvar.
///
/// This port wires the balance-defining hooks (the armor-as-lives subtraction with the lives notification,
/// the blaster damage/force nullification, always-gib-on-vaporizer death, regen disable, the
/// weapon-throw/arena forbids and the start loadout). It also drives the deep behaviour: a per-spawn
/// Vaporizer-only loadout applied through <see cref="Inventory"/>, the no-ammo countdown
/// (<c>instagib_ammocheck</c>) on PlayerPreThink that bleeds a cell-less player out, and the
/// extra-life / vaporizer-cells pickup effects (<see cref="OnExtraLifeTouch"/> / <see cref="OnCellsTouch"/>).
/// The CSQC random-powerup item replacement is a client/item-registry concern left to the item pipeline.
/// </summary>
[Mutator]
public sealed class InstagibMutator : MutatorBase
{
    /// <summary>QC autocvar_g_instagib_blaster_keepdamage — let the blaster deal real damage.</summary>
    public bool BlasterKeepDamage;

    /// <summary>QC autocvar_g_instagib_blaster_keepforce — let the blaster keep knockback.</summary>
    public bool BlasterKeepForce;

    /// <summary>QC autocvar_g_instagib_mirrordamage — support real mirror damage instead of the lives hack.</summary>
    public bool MirrorDamage;

    /// <summary>QC cvar("g_instagib_ammo_start") — vaporizer cells granted on spawn.</summary>
    public float AmmoStart = 10f;

    /// <summary>QC autocvar_g_instagib_extralives — armor "lives" granted by an ExtraLife pickup.</summary>
    public float ExtraLives = 1f;

    /// <summary>QC autocvar_g_instagib_damagedbycontents (default true) — lava/slime/drown still hurt.</summary>
    public bool DamagedByContents = true;

    /// <summary>QC autocvar_g_instagib_friendlypush (default true) — Vaporizer knockback affects teammates.</summary>
    public bool FriendlyPush = true;

    /// <summary>QC autocvar_g_instagib_allow_jetpacks — keep Jetpack/FuelRegen pickups on the map.</summary>
    public bool AllowJetpacks;

    /// <summary>QC autocvar_g_instagib_ammo_convert_cells — turn cell packs into Vaporizer cells.</summary>
    public bool AmmoConvertCells;

    /// <summary>QC autocvar_g_instagib_ammo_convert_rockets.</summary>
    public bool AmmoConvertRockets;

    /// <summary>QC autocvar_g_instagib_ammo_convert_shells.</summary>
    public bool AmmoConvertShells;

    /// <summary>QC autocvar_g_instagib_ammo_convert_bullets.</summary>
    public bool AmmoConvertBullets;

    /// <summary>QC autocvar_g_instagib_ammo_drop (default 5) — cells seeded on loot drops and VaporizerCells replacements.</summary>
    public float AmmoDrop = 5f;

    /// <summary>QC autocvar_g_instagib_invisibility_time (default 30) — duration of the Invisibility powerup under instagib.</summary>
    public float InvisibilityTime = 30f;

    /// <summary>QC autocvar_g_instagib_speed_time (default 30) — duration of the Speed powerup under instagib.</summary>
    public float SpeedTime = 30f;

    // --- random-powerup deck (sv_instagib.qc:291-312 instagib_replace_item_with_random_powerup) ---
    // QC cycles a 3-slot array {Invisibility, ExtraLife, Speed}; once all 3 are consumed the deck refills.
    // The port mirrors this with a parallel index list reset on depletion.
    private readonly string[] _powerupDeck = { "invisibility", "extralife", "speed" };
    private int _powerupDeckCount = 3; // remaining items (0 → refill to 3)

    public InstagibMutator() => NetName = "instagib";

    // QC: REGISTER_MUTATOR(mutator_instagib, autocvar_g_instagib && !MapInfo_LoadedGametype.m_weaponarena).
    // The `!m_weaponarena` guard keeps instagib from co-enabling with a weapon-arena gametype; the port reads
    // it as `g_weaponarena == 0` (same stand-in melee_only uses for its identical guard) so a console
    // `g_instagib 1` can't double-activate alongside an arena gametype, not just the menu radio-group.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_instagib") != 0f
        && Api.Cvars.GetFloat("g_weaponarena") == 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _onPlayerRegen;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _onSetWeaponArena;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<GameHooks.PlayerDamageArgs>? _onSplitHealthArmor;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.FilterItemArgs>? _onFilterItem;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _onItemTouch;
    private HookHandler<MutatorHooks.MakePlayerObserverArgs>? _onMakeObserver;
    private HookHandler<MutatorHooks.MonsterDropItemArgs>? _onMonsterDropItem;
    private HookHandler<MutatorHooks.MonsterSpawnArgs>? _onMonsterSpawn;
    private HookHandler<MutatorHooks.RandomItemsClassNameArgs>? _onRandomItemsGetClassName;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        _onPlayerRegen ??= OnPlayerRegen;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onSetWeaponArena ??= OnSetWeaponArena;
        _onSetStartItems ??= OnSetStartItems;
        _onSplitHealthArmor ??= OnPlayerDamageSplitHealthArmor;
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onFilterItem ??= OnFilterItem;
        _onItemTouch ??= OnItemTouch;
        _onMakeObserver ??= OnMakePlayerObserver;
        _onMonsterDropItem ??= OnMonsterDropItem;
        _onMonsterSpawn ??= OnMonsterSpawn;
        _onRandomItemsGetClassName ??= OnRandomItemsGetClassName;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.PlayerRegen.Add(_onPlayerRegen);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.SetStartItems.Add(_onSetStartItems);
        // QC PlayerDamage_SplitHealthArmor: armor never absorbs damage in instagib (take = damage, save = 0)
        // — without this the port's 70% armor block corrupts the armor-as-lives model + the no-ammo bleed.
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onSplitHealthArmor);
        // QC FilterItem: convert/replace/remove map weapons, powerups, ammo and jetpacks.
        // The definition-level hook handles pure deletes; the entity-level hook handles replacements +
        // in-place modifications (VaporizerCells cells-seed, Devastator→VaporizerCells, powerup deck).
        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        MutatorHooks.FilterItem.Add(_onFilterItem);
        // QC ItemTouch: cells pickup full-heals; ExtraLife grants armor "lives".
        MutatorHooks.ItemTouch.Add(_onItemTouch);
        // QC MakePlayerObserver: stop a demoted player's no-ammo countdown (clear the FINDAMMO centerprint).
        MutatorHooks.MakePlayerObserver.Add(_onMakeObserver);
        // QC MonsterDropItem (sv_instagib.qc:109-112): monsters always drop vaporizer_cells in instagib.
        MutatorHooks.MonsterDropItem.Add(_onMonsterDropItem);
        // QC MonsterSpawn (sv_instagib.qc:114-121): give the Mage skin=1 on spawn.
        MutatorHooks.MonsterSpawn.Add(_onMonsterSpawn);
        // QC RandomItems_GetRandomItemClassName (sv_instagib.qc:102-107): substitute the instagib item pool
        // (VaporizerCells / ExtraLife / Invisibility / Speed) weighted by probability cvars.
        MutatorHooks.RandomItemsGetClassName.Add(_onRandomItemsGetClassName);

        if (Api.Services is not null)
        {
            BlasterKeepDamage = Api.Cvars.GetFloat("g_instagib_blaster_keepdamage") != 0f;
            BlasterKeepForce = Api.Cvars.GetFloat("g_instagib_blaster_keepforce") != 0f;
            MirrorDamage = Api.Cvars.GetFloat("g_instagib_mirrordamage") != 0f;
            float start = Api.Cvars.GetFloat("g_instagib_ammo_start");
            if (start != 0f) AmmoStart = start;
            float xl = Api.Cvars.GetFloat("g_instagib_extralives");
            if (xl != 0f) ExtraLives = xl;
            // QC defaults (mutators.cfg): damagedbycontents/friendlypush both default 1, the rest default 0.
            // The cvars are registered with those defaults, so GetFloat returns the live/default value.
            DamagedByContents = Api.Cvars.GetFloat("g_instagib_damagedbycontents") != 0f;
            FriendlyPush = Api.Cvars.GetFloat("g_instagib_friendlypush") != 0f;
            AllowJetpacks = Api.Cvars.GetFloat("g_instagib_allow_jetpacks") != 0f;
            AmmoConvertCells = Api.Cvars.GetFloat("g_instagib_ammo_convert_cells") != 0f;
            AmmoConvertRockets = Api.Cvars.GetFloat("g_instagib_ammo_convert_rockets") != 0f;
            AmmoConvertShells = Api.Cvars.GetFloat("g_instagib_ammo_convert_shells") != 0f;
            AmmoConvertBullets = Api.Cvars.GetFloat("g_instagib_ammo_convert_bullets") != 0f;
            float ammoDrop = Api.Cvars.GetFloat("g_instagib_ammo_drop");
            if (ammoDrop != 0f) AmmoDrop = ammoDrop;
            float invTime = Api.Cvars.GetFloat("g_instagib_invisibility_time");
            if (invTime != 0f) InvisibilityTime = invTime;
            float spdTime = Api.Cvars.GetFloat("g_instagib_speed_time");
            if (spdTime != 0f) SpeedTime = spdTime;
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onPlayerRegen is not null) MutatorHooks.PlayerRegen.Remove(_onPlayerRegen);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onSetWeaponArena is not null) MutatorHooks.SetWeaponArena.Remove(_onSetWeaponArena);
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onSplitHealthArmor is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onSplitHealthArmor);
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onFilterItem is not null) MutatorHooks.FilterItem.Remove(_onFilterItem);
        if (_onItemTouch is not null) MutatorHooks.ItemTouch.Remove(_onItemTouch);
        if (_onMakeObserver is not null) MutatorHooks.MakePlayerObserver.Remove(_onMakeObserver);
        if (_onMonsterDropItem is not null) MutatorHooks.MonsterDropItem.Remove(_onMonsterDropItem);
        if (_onMonsterSpawn is not null) MutatorHooks.MonsterSpawn.Remove(_onMonsterSpawn);
        if (_onRandomItemsGetClassName is not null) MutatorHooks.RandomItemsGetClassName.Remove(_onRandomItemsGetClassName);
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        Entity? attacker = args.Attacker;

        // QC FIRST branch: g_friendlyfire == 0 && SAME_TEAM && both players → no damage at all.
        if (Api.Services is not null
            && Api.Cvars.GetFloat("g_friendlyfire") == 0f
            && IsPlayer(target) && IsPlayer(attacker)
            && Teams.SameTeam(target, attacker!))
            args.Damage = 0f;

        if (!IsPlayer(target))
            return false;

        // QC: never count fall damage in instagib.
        if (args.DeathType == DeathTypes.Fall)
            args.Damage = 0f;

        // QC: lava/slime/drown damage only counts when g_instagib_damagedbycontents is set (default true).
        if (!DamagedByContents)
        {
            string baseDeath = DeathTypes.BaseOf(args.DeathType);
            if (baseDeath == DeathTypes.Drown || baseDeath == DeathTypes.Slime || baseDeath == DeathTypes.Lava)
                args.Damage = 0f;
        }

        string wep = DeathTypes.WeaponNetNameOf(args.DeathType);
        if (IsPlayer(attacker))
        {
            // QC: vaporizer hit on a player with armor → eat a "life" (armor point) and do no damage.
            if (wep == "vaporizer")
            {
                // QC: !friendlypush && SAME_TEAM → no knockback on teammates.
                if (!FriendlyPush && Teams.SameTeam(target, attacker!))
                    args.Force = Vector3.Zero;

                float armor = target.GetResource(ResourceType.Armor);
                if (armor > 0f)
                {
                    armor -= 1f;
                    target.SetResource(ResourceType.Armor, armor);
                    args.Damage = 0f;
                    // QC: ++hitsound_damage_dealt on both (the PER-FRAME accumulator, flushed to the stats by
                    // EndFrame — the zeroed damage would otherwise skip the count block, and the TARGET also
                    // hears a beep for the eaten life); CENTER_INSTAGIB_LIVES_REMAINING tells the target how
                    // many lives are left.
                    target.HitSoundDamageDealt += 1f;
                    attacker!.HitSoundDamageDealt += 1f;
                    NotificationSystem.Center(target, "INSTAGIB_LIVES_REMAINING", armor);
                }
            }

            // QC: blaster generally does no damage/force in instagib (unless keep* cvars say otherwise).
            if (wep == "blaster")
            {
                if (!BlasterKeepDamage || ReferenceEquals(attacker, target))
                {
                    args.Damage = 0f;
                    if (!MirrorDamage)
                        args.MirrorDamage = 0f;
                }
                if (!ReferenceEquals(target, attacker) && !BlasterKeepForce)
                    args.Force = Vector3.Zero;
            }
        }

        // QC: mirror damage just costs the attacker extra LIVES rather than killing them.
        if (!MirrorDamage && IsPlayer(attacker) && args.MirrorDamage > 0f)
        {
            float armor = attacker!.GetResource(ResourceType.Armor);
            if (armor > 0f)
            {
                armor -= 1f;
                attacker.SetResource(ResourceType.Armor, armor);
                NotificationSystem.Center(attacker, "INSTAGIB_LIVES_REMAINING", armor);
                // QC: frag_attacker.hitsound_damage_dealt += frag_mirrordamage (per-frame accumulator).
                attacker.HitSoundDamageDealt += args.MirrorDamage;
            }
            args.MirrorDamage = 0f;
        }

        // QC sv_instagib.qc:245-247: if the target is partially transparent (alpha in (0,1)) and a player,
        // set the yoda flag so a Vaporizer hit on a cloaked/invisible player can announce ACHIEVEMENT_YODA.
        // The QC `yoda` global is per-shot; the port stores it on the target as InstagibAlphaYoda so
        // Vaporizer.Announce can read it for the same attack frame. Alpha==0 means default (fully visible in QC),
        // so only a non-zero fractional alpha triggers it.
        if (IsPlayer(target) && target.Alpha != 0f && target.Alpha < 1f)
            target.InstagibAlphaYoda = true;

        return false; // CBC_ORDER_ANY — not exclusive.
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerDies) — always gib on a vaporizer death.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (DeathTypes.WeaponNetNameOf(args.DeathType) == "vaporizer")
            args.Damage = 1000f; // M_ARGV(4, float) = 1000
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerRegen) — no regeneration in instagib.
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args) => true;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerSpawn) — players glow fullbright.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        // QC: player.effects |= EF_FULLBRIGHT;
        player.Effects |= EffectFlags.FullBright;

        // QC's SetStartItems gives start_weapons = WEPSET(VAPORIZER); applied per-spawn through the
        // weapon inventory. Make the arena-loadout concrete: only the Vaporizer, and switch to it.
        Weapon? vaporizer = Weapons.ByName("vaporizer");
        if (vaporizer is not null)
        {
            Inventory.ClearWeapons(player);
            Inventory.GiveWeapon(player, vaporizer);
            Inventory.SwitchToBest(player);
        }

        // restart the no-ammo countdown bookkeeping for this life.
        player.InstagibNeedAmmo = false;
        player.InstagibNextThink = 0f;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerPreThink) — instagib_ammocheck.
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        AmmoCheck(args.Player);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, MakePlayerObserver) — sv_instagib.qc:123-128. A player demoted to
    // observer has their no-ammo countdown stopped (and the FINDAMMO centerprint retracted) immediately, rather
    // than only being reset on their next spawn.
    private bool OnMakePlayerObserver(ref MutatorHooks.MakePlayerObserverArgs args)
    {
        StopCountdown(args.Player);
        return false;
    }

    // void instagib_ammocheck(entity this) — sv_instagib.qc
    private void AmmoCheck(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        if (now < player.InstagibNextThink) return;
        if (!IsPlayer(player)) return;

        bool godmode = (player.Flags & EntFlags.GodMode) != 0;
        bool unlimitedAmmo = (player.Items & (int)ItemFlag.UnlimitedAmmo) != 0;
        // QC global game_stopped — host-driven freeze (warmup-over / match-ended / intermission). Read the real
        // host flag (VehicleCommon.GameStopped, set by the match loop), falling back to the cvar mirror — same
        // pattern as VehicleCommon.FreezeIfGameStopped. The bare cvar alone is never written, so it read as 0.
        bool gameStopped = VehicleCommon.GameStopped
            || Api.Cvars.GetFloat("g_game_stopped") != 0f;
        // QC autocvar_g_rm && autocvar_g_rm_laser — rocketminsta downgrade mode.
        bool rocketMinsta = Api.Cvars.GetFloat("g_rm") != 0f && Api.Cvars.GetFloat("g_rm_laser") != 0f;

        if (player.DeadState != DeadFlag.No || gameStopped)
            StopCountdown(player);
        else if (player.GetResource(ResourceType.Cells) > 0f || unlimitedAmmo || godmode)
            StopCountdown(player);
        else if (rocketMinsta)
        {
            // QC: under rocketminsta the player's weapon is downgraded instead of bleeding out.
            if (!player.InstagibNeedAmmo)
            {
                NotificationSystem.Center(player, "INSTAGIB_DOWNGRADE");
                player.InstagibNeedAmmo = true;
            }
        }
        else
        {
            player.InstagibNeedAmmo = true;
            Countdown(player);
        }
        player.InstagibNextThink = now + 1f;
    }

    // void instagib_stop_countdown(entity e)
    private static void StopCountdown(Entity e)
    {
        if (!e.InstagibNeedAmmo) return;
        // QC: Kill_Notification(NOTIF_ONE_ONLY, e, MSG_CENTER, CPID_INSTAGIB_FINDAMMO) — actively retract the
        // lingering "find ammo" / DOWNGRADE centerprint (both share the CPID_INSTAGIB_FINDAMMO group) on this
        // client so it doesn't persist after the player re-arms / dies / spectates.
        NotificationSystem.SendCenterKill(NotifBroadcast.OneOnly, e, "CPID_INSTAGIB_FINDAMMO");
        e.InstagibNeedAmmo = false;
    }

    // void instagib_countdown(entity this) — bleed the player out when they have no cells.
    private void Countdown(Entity player)
    {
        float hp = player.GetResource(ResourceType.Health);

        // QC: dmg = (hp <= 10) ? 5 : 10; Damage(... DEATH_NOAMMO ...).
        float dmg = hp <= 10f ? 5f : 10f;
        Combat.Damage(player, player, player, dmg, "noammo", player.Origin, System.Numerics.Vector3.Zero);

        // QC: annce = (hp <= 5) ? ANNCE_INSTAGIB_TERMINATED : Announcer_PickNumber(CNT_NORMAL, ceil(hp/10)).
        // Announcer_PickNumber(CNT_NORMAL, n) plays the NUM_<n> spoken-number announcer (the "1".."10" clips).
        if (hp <= 5f)
        {
            NotificationSystem.Announce(player, "INSTAGIB_TERMINATED");
        }
        else
        {
            int num = (int)System.Math.Ceiling(hp / 10f);
            if (num < 1) num = 1;
            if (num > 10) num = 10;
            NotificationSystem.Announce(player, $"NUM_{num}");
        }

        // QC "find ammo" centerprint past 80hp; the >90 variant is the louder MULTI ("get some ammo or you'll be dead").
        if (hp > 80f)
        {
            if (hp <= 90f)
                NotificationSystem.Center(player, "INSTAGIB_FINDAMMO");
            else
                NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Multi, "MULTI_INSTAGIB_FINDAMMO");
        }
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, ItemTouch) — sv_instagib.qc:379. Fired (notify-style) from the
    // real item-pickup path (ItemPickupRules.ItemTouch -> MutatorHooks.FireItemTouch) just before the give, so
    // a cells pickup full-heals the toucher and an ExtraLife pickup grants armor "lives".
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
    {
        Entity item = args.Item;
        Entity toucher = args.Toucher;

        // QC: if (GetResource(item, RES_CELLS)) — any item that carries cells (the VaporizerCells/cells pack).
        if (item.Pickup is AmmoPickup ammo && ammo.Resource == ResourceType.Cells)
        {
            OnCellsTouch(toucher);
            return false; // MUT_ITEMTOUCH_CONTINUE
        }

        // QC: if (item.itemdef == ITEM_ExtraLife).
        if (item.ClassName is "item_extralife" || item.NetName == "extralife"
            || (item.Pickup is not null && item.Pickup.NetName == "extralife"))
            OnExtraLifeTouch(toucher);

        // Deliberately CONTINUE (false), NOT OnExtraLifeTouch's "granted" bool: this port's ItemTouch hook is
        // BINARY — ItemPickupRules.FireItemTouch treats true as MUT_ITEMTOUCH_RETURN, which ABORTS the pickup and
        // leaves the item in the world. ExtraLife has no normal give path (ItemId=None), so the base pickup is what
        // REMOVES the item; returning true here would strand it → repeatable ExtraLife (infinite lives). Base's
        // MUT_ITEMTOUCH_PICKUP "consumed-and-removed" distinction has no equivalent in the binary port hook yet.
        return false;
    }

    /// <summary>
    /// QC ItemTouch for a vaporizer-cells item: refill to full health (the cells themselves are given by the
    /// item's normal resource path). Routed live from <see cref="OnItemTouch"/> on a cells pickup.
    /// </summary>
    public void OnCellsTouch(Entity toucher)
    {
        if (!IsPlayer(toucher)) return;
        float hp = toucher.GetResource(ResourceType.Health);
        if (hp <= 5f)
            NotificationSystem.Announce(toucher, "INSTAGIB_LASTSECOND");
        else if (hp < 50f)
            NotificationSystem.Announce(toucher, "INSTAGIB_NARROWLY");
        if (hp < 100f)
            toucher.SetResource(ResourceType.Health, 100f);
    }

    /// <summary>
    /// QC ItemTouch for ITEM_ExtraLife: grant <see cref="ExtraLives"/> armor "lives". Exposed so the item
    /// pipeline can route an instagib extra-life pickup here (returns true = item consumed).
    /// </summary>
    public bool OnExtraLifeTouch(Entity toucher)
    {
        if (!IsPlayer(toucher)) return false;
        toucher.GiveResource(ResourceType.Armor, ExtraLives);
        NotificationSystem.Center(toucher, "EXTRALIVES", ExtraLives);
        return true;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, PlayerDamage_SplitHealthArmor) — armor never absorbs damage:
    // take = damage, save = 0. (QC M_ARGV(4) = M_ARGV(7); M_ARGV(5) = 0.) This is what makes armor act as
    // discrete "lives" instead of a 70% damage sponge, and keeps the no-ammo bleed at its raw 10/5 per tick.
    private bool OnPlayerDamageSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        args.DamageTake = args.DamageSave + args.DamageTake; // = the full incoming damage (take + save split)
        args.DamageSave = 0f;
        return false; // CBC_ORDER_ANY
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, FilterItem) — (definition-level) handle the pure-delete cases.
    // The port separates instagib's single QC FilterItem hook into two C# hooks:
    //   (1) FilterItemDefinition (here) — definition-level, handles pure deletes BEFORE the entity is spawned.
    //   (2) FilterItem (OnFilterItem, below) — entity-level, handles replacements + in-place modifications.
    // Returns true to delete/suppress the spawn of this definition entirely.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity def = args.Definition;
        string cls = def.ClassName;
        string net = def.NetName;

        // QC: Invisibility / ExtraLife / Speed are KEPT (return false = allowed) — they appear via the
        // random-powerup replacement deck (OnFilterItem below handles that) and must not be pre-deleted.
        if (cls is "item_invisible" or "item_invisibility" or "item_extralife" or "item_speed"
            || net is "invisibility" or "extralife" or "speed")
            return false;

        // QC: the VaporizerCells economy item is always kept — it only appears as a replacement or map item.
        if (cls is "item_vaporizer_cells" or "item_minst_cells" || net == "vaporizer_cells")
            return false;

        // QC: Jetpack / FuelRegen removed unless g_instagib_allow_jetpacks.
        if (cls is "item_jetpack" or "item_fuel_regen" || net is "jetpack" or "fuel_regen")
            return !AllowJetpacks;

        // Big powerups, ammo packs, Devastator/Vortex, and the generic fallthrough are handled at the entity
        // level (OnFilterItem) so replacements can be spawned at the item's in-world origin. Let them through
        // the definition gate so their entity gets built; OnFilterItem will delete or replace them.
        // The cases below that are "always delete AND never replaced" can still be short-circuited here.

        // QC fallthrough (sv_instagib.qc:368): everything else (stock weapons/items that don't carry cells) is
        // DELETED by default at definition level — saves spawning an entity only to delete it.
        // EXCEPTION: big powerups (strength/shield/mega*), ammo, Devastator/Vortex, and weapon_* items
        // pass through here so their live entity is available for replacement in OnFilterItem.
        if (cls is "item_strength" or "item_shield" or "item_health_mega" or "item_armor_mega"
            || net is "strength" or "shield" or "health_mega" or "armor_mega")
            return false; // let entity-level hook handle (replacement or delete based on g_powerups)

        if (cls is "item_cells" or "item_rockets" or "item_shells" or "item_bullets"
            || net is "cells" or "rockets" or "shells" or "bullets")
            return false; // entity-level hook handles (ammo_convert_* replace or delete)

        if (cls is "weapon_devastator" or "weapon_vortex" || net is "devastator" or "vortex")
            return false; // entity-level hook handles (replace with VaporizerCells)

        // QC FilterItem `case WEP_VAPORIZER.m_id: if (ITEM_IS_LOOT(item)) { cells = ammo_drop; return false; }`
        // (sv_instagib.qc:347-352): a Vaporizer LOOT drop (the weapon dropped on death — ForbidThrow blocks the
        // throw, "weapon dropping on death handled by FilterItem") is KEPT and cells-seeded at entity level.
        // Let it through the definition gate (ItemIsLoot is already set on the edict before this hook fires).
        if ((cls is "weapon_vaporizer" || net is "vaporizer") && def.ItemIsLoot)
            return false; // entity-level hook seeds cells = g_instagib_ammo_drop and keeps it

        // weapon_* that aren't Devastator/Vortex/Vaporizer-loot — always deleted; no replacement.
        if (cls.StartsWith("weapon_", System.StringComparison.Ordinal)
            || (def.Pickup?.IsWeaponPickup == true))
            return true;

        // QC generic fallthrough: anything not explicitly kept is deleted.
        return true;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, FilterItem) — (entity-level) handle replacements and in-place
    // modifications. Fires after the item entity is fully set up (ItemInit + seeding). Returns true to delete
    // this entity (replacement was already spawned). Mirrors QC's single FilterItem switch statement body.
    private bool OnFilterItem(ref MutatorHooks.FilterItemArgs args)
    {
        if (Api.Services is null) return false;
        Entity item = args.Item;
        string cls = item.ClassName;
        string net = item.NetName;

        // QC FilterItem (sv_instagib.qc:322): `case ITEM_Invisibility: case ITEM_ExtraLife: case ITEM_Speed:
        // return false;` — these are explicitly KEPT (they appear via the random-powerup replacement deck or as
        // map items). Without this guard they'd fall through to the generic tail below and be deleted (they carry
        // no cells and aren't weapons), so the replacement powerups the deck just spawned would be removed.
        if (cls is "item_invisible" or "item_invisibility" or "item_extralife" or "item_speed"
            || net is "invisibility" or "extralife" or "speed")
            return false;

        // QC: the VaporizerCells economy item (sv_instagib.qc generic tail keeps it: cells > 0 && !weapon →
        // return false). Kept explicitly so a freshly-spawned replacement isn't deleted by the tail's clamp path.
        if (cls is "item_vaporizer_cells" or "item_minst_cells" || net == "vaporizer_cells")
            return false;

        // QC: `case ITEM_Jetpack: case ITEM_FuelRegen: return !autocvar_g_instagib_allow_jetpacks;`
        // (sv_instagib.qc:342-344). When jetpacks are allowed this returns false (keep); the definition gate
        // already let it through, so the generic tail below would otherwise delete the (cells-less) jetpack.
        if (cls is "item_jetpack" or "item_fuel_regen" || net is "jetpack" or "fuel_regen")
            return !AllowJetpacks;

        // QC: Strength/Shield/HealthMega/ArmorMega → instagib_replace_item_with_random_powerup when g_powerups,
        // then return true (delete original). When g_powerups is off, just delete.
        if (cls is "item_strength" or "item_shield" or "item_health_mega" or "item_armor_mega"
            || net is "strength" or "shield" or "health_mega" or "armor_mega")
        {
            if (ItemPickupRules.CvarBoolOr("g_powerups", true))
                ReplaceItemWithRandomPowerup(item);
            return true; // always delete the original (QC: return true after both branches)
        }

        // QC: ammo packs — if matching g_instagib_ammo_convert_* is set, replace with VaporizerCells, then
        // always delete the original pack (QC: return true after both branches; 326-341).
        if (net is "cells" || cls is "item_cells")
        {
            if (AmmoConvertCells) ReplaceItemWithVaporizerCells(item);
            return true;
        }
        if (net is "rockets" || cls is "item_rockets")
        {
            if (AmmoConvertRockets) ReplaceItemWithVaporizerCells(item);
            return true;
        }
        if (net is "shells" || cls is "item_shells")
        {
            if (AmmoConvertShells) ReplaceItemWithVaporizerCells(item);
            return true;
        }
        if (net is "bullets" || cls is "item_bullets")
        {
            if (AmmoConvertBullets) ReplaceItemWithVaporizerCells(item);
            return true;
        }

        // QC: Devastator / Vortex weapon pickups → replace with VaporizerCells, delete original (sv_instagib.qc:356-359).
        if (cls is "weapon_devastator" or "weapon_vortex" || net is "devastator" or "vortex")
        {
            ReplaceItemWithVaporizerCells(item);
            return true;
        }

        // QC: Vaporizer loot drop (sv_instagib.qc:349-355): weapon == WEP_VAPORIZER && ITEM_IS_LOOT →
        // set cells = g_instagib_ammo_drop, keep the item (return false). The weapon field on the item entity
        // is set via item.OwnedWeaponSet (WeaponPickup.ItemInit); item.ItemIsLoot distinguishes loot from map.
        if (item.ItemIsLoot && item.Pickup is WeaponPickup wp && wp.NetName == "vaporizer")
        {
            item.SetResource(ResourceType.Cells, AmmoDrop);
            return false; // keep the loot with seeded cells
        }

        // QC generic tail (sv_instagib.qc:361-368): clamp cells > g_instagib_ammo_drop, then if has cells
        // and no weapon → keep (return false), else delete (return true).
        float cells = item.GetResource(ResourceType.Cells);
        if (cells > AmmoDrop && cls != "item_vaporizer_cells")
            item.SetResource(ResourceType.Cells, AmmoDrop);

        if (cells > 0f && item.Pickup?.IsWeaponPickup != true)
            return false; // keep a cells-carrying non-weapon item

        return true; // delete anything else that reached here
    }

    // QC instagib_replace_item_with(this, def) — spawn a new item of def at this item's origin, copying the
    // map-placement fields, seeding any powerup timers, and running StartItem. If the spawn fails, nothing is
    // emitted (QC's StartItem free path). Called for the Devastator/Vortex→VaporizerCells path.
    private void ReplaceItemWithVaporizerCells(Entity original)
    {
        if (Api.Services is null) return;
        Pickup? def = Items.ByName("vaporizer_cells");
        if (def is null) return;
        Entity newItem = Api.Entities.Spawn();
        CopyMapPlacement(original, newItem);
        // QC: ammo_vaporizercells_init seeds cells = g_instagib_ammo_drop (handled by VaporizerCellsItem.ItemInit).
        StartItem.Spawn(newItem, def);
    }

    // QC instagib_replace_item_with_random_powerup(item) — cycles the 3-slot deck {Invisibility, ExtraLife,
    // Speed}, picks a random remaining entry, spawns that powerup in place of the big powerup, and advances
    // the deck; when the deck is empty it refills (sv_instagib.qc:295-312).
    private void ReplaceItemWithRandomPowerup(Entity original)
    {
        if (Api.Services is null) return;

        // QC: if (remaining_powerups_count == 0) remaining_powerups_count = INSTAGIB_POWERUP_COUNT (refill).
        if (_powerupDeckCount == 0)
        {
            _powerupDeck[0] = "invisibility";
            _powerupDeck[1] = "extralife";
            _powerupDeck[2] = "speed";
            _powerupDeckCount = 3;
        }

        // QC: r = floor(random() * remaining_powerups_count); pick slot r.
        int r = (int)System.MathF.Floor(Prandom.Float() * _powerupDeckCount);
        if (r >= _powerupDeckCount) r = _powerupDeckCount - 1; // clamp (rounding safety)
        string chosenNetName = _powerupDeck[r];

        // QC: shift remaining slots left to fill the gap (remove slot r).
        for (int i = r; i < _powerupDeckCount - 1; i++)
            _powerupDeck[i] = _powerupDeck[i + 1];
        _powerupDeckCount--;

        Pickup? def = Items.ByName(chosenNetName);
        if (def is null) return;

        Entity newItem = Api.Entities.Spawn();
        CopyMapPlacement(original, newItem);
        // QC instagib_replace_item_with: for Invisibility set invisibility_finished; for Speed set speed_finished.
        // These are the instagib-specific override durations (default 30s, Base autocvar_g_instagib_*_time).
        if (chosenNetName == "invisibility")
            newItem.InvisibilityFinished = InvisibilityTime;
        else if (chosenNetName == "speed")
            newItem.SpeedFinished = SpeedTime;
        StartItem.Spawn(newItem, def);
    }

    // Copy the placement fields the new map item inherits from the one it replaces (mirrors QC Item_CopyFields
    // and RandomItemsMutator.CopyMapPlacement).
    private static void CopyMapPlacement(Entity from, Entity to)
    {
        to.Origin = from.Origin;
        to.OldOrigin = from.Origin;
        if (Api.Services is not null)
            Api.Entities.SetOrigin(to, to.Origin);
        to.Angles = from.Angles;
        to.SpawnFlags = from.SpawnFlags;
        to.Target = from.Target;
        to.TargetName = from.TargetName;
        to.Team = from.Team;
        to.NoAlign = from.NoAlign;
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    // MUTATOR_HOOKFUNCTION(mutator_instagib, SetWeaponArena) — turn weapon arena off.
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        args.Arena = "off";
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, SetStartItems, CBC_ORDER_LAST)
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.Health = 100f;
        l.Armor = 0f;
        l.AmmoShells = 0f;
        l.AmmoBullets = 0f;
        l.AmmoRockets = 0f;
        l.AmmoCells = AmmoStart;
        l.SetWeapons("vaporizer");
        // QC: start_items |= IT_UNLIMITED_SUPERWEAPONS; (the vaporizer never runs out of "super" charge).
        // StartLoadout has no separate warmup_* twins — its live values already mirror QC's warmup_start_*.
        l.ItemFlags.Add("UNLIMITED_SUPERWEAPONS");
        return false;
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, MonsterDropItem) — sv_instagib.qc:109-112.
    // EV_MonsterDropItem(monster, itemlist[in/out], attacker): set the drop-item list to "vaporizer_cells"
    // so any monster killed during an instagib match always drops vaporizer cells instead of its normal loot.
    // QC: M_ARGV(1, string) = "vaporizer_cells"; (no return value — not CBC_ORDER_EXCLUSIVE).
    private bool OnMonsterDropItem(ref MutatorHooks.MonsterDropItemArgs args)
    {
        args.ItemList = "vaporizer_cells";
        return false; // CBC_ORDER_ANY — does not cancel the drop
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, MonsterSpawn) — sv_instagib.qc:114-121.
    // EV_MonsterSpawn(monster): if the spawning monster is a Mage, give it skin 1.
    // QC: entity mon = M_ARGV(0, entity); if (mon.monsterdef == MON_MAGE) mon.skin = 1;
    // The comment "always refill ammo" in the original is a stale TODO — the actual change is only skin=1.
    private bool OnMonsterSpawn(ref MutatorHooks.MonsterSpawnArgs args)
    {
        Entity mon = args.Monster;
        // QC: mon.monsterdef == MON_MAGE — in the port the descriptor is stored in MonsterAI.StateOf(mon).Def;
        // the Mage's netname is "mage" (Monsters.ByName("mage")). A null state means the entity isn't a monster.
        if (MonsterAI.StateOf(mon)?.Def.NetName == "mage")
            mon.Skin = 1f;
        return false; // CBC_ORDER_ANY — does not cancel the spawn
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, RandomItems_GetRandomItemClassName) — sv_instagib.qc:102-107.
    // Port of RandomItems_GetRandomInstagibItemClassName(prefix) (sv_instagib.qc:24-39): weighted reservoir
    // pick over the g_instagib_items pool {VaporizerCells, ExtraLife, Invisibility, Speed}, keyed by
    // g_{prefix}_{m_canonical_spawnfunc}_probability cvars (same cvar naming as the vanilla random-items and
    // overkill pools). A handler returning true signals the caller (RandomItemsMutator.GetRandomItemClassName)
    // that the instagib pool was consumed and the vanilla weighted pick should NOT run.
    // QC: IL_EACH(g_instagib_items, Item_IsDefinitionAllowed(it), { ... RandomSelection_AddString(spawnfunc, cvar(prob_cvar), 1); });
    //     M_ARGV(1, string) = RandomSelection_chosen_string; return true;
    private static readonly string[] InstagibItemNetNames =
        { "vaporizer_cells", "extralife", "invisibility", "speed" };

    private bool OnRandomItemsGetClassName(ref MutatorHooks.RandomItemsClassNameArgs args)
    {
        if (Api.Services is null) { args.ClassName = ""; return true; }
        string prefix = args.Prefix;
        string chosen = "";
        float total = 0f;

        // QC IL_EACH(g_instagib_items, Item_IsDefinitionAllowed(it)) — iterate the fixed 4-item instagib pool.
        foreach (string netName in InstagibItemNetNames)
        {
            Pickup? def = Items.ByName(netName);
            if (def is null) continue;
            // QC Item_IsDefinitionAllowed: not MutatorBlocked and allowed for the current gametype.
            if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.MutatorBlocked) != 0) continue;
            if (!def.ItemDef.IsAllowed) continue;
            // QC: cvar_name = sprintf("g_%s_%s_probability", prefix, it.m_canonical_spawnfunc).
            string canonical = ItemSpawnFuncs.CanonicalSpawnFunc(def);
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{canonical}_probability");
            if (prob <= 0f) continue;
            // QC RandomSelection_AddString: reservoir pick — replace chosen with probability prob/total.
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = canonical;
        }

        args.ClassName = chosen; // QC: M_ARGV(1, string) = RandomSelection_chosen_string.
        return true;             // QC: return true — the instagib pool was consumed (skip vanilla pick).
    }

    // MUTATOR_HOOKFUNCTION(mutator_instagib, BuildMutatorsString) — sv_instagib.qc:415-418: append ":instagib"
    // to the machine-readable mutators token (the :gameinfo:mutators:LIST event-log line / server-browser field).
    public override string BuildMutatorsString(string s) => s + ":instagib";

    // MUTATOR_HOOKFUNCTION(mutator_instagib, BuildMutatorsPrettyString) — sv_instagib.qc:420-423: append the
    // human-readable ", InstaGib" token to the active-mutators line shown to joining clients / the scoreboard.
    public override string BuildMutatorsPrettyString(string s) => s + ", InstaGib";

    // MUTATOR_HOOKFUNCTION(mutator_instagib, SetModname) — sv_instagib.qc:425-429: override the server modname
    // to "InstaGib" so the server browser and client connection banner reflect the active game mode.
    // Returns true (overridden) so the chain stops here (QC: return true from the hook, CBC_ORDER_ANY).
    public override (string name, bool overridden) SetModname(string name) => ("InstaGib", true);
}
