using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The NIX ("No Items Xonotic") mutator — port of common/mutators/mutator/nix/sv_nix.qc. Removes item
/// pickups; instead every player is forced to use the same single weapon, which rotates for everyone on a
/// timer, with ammo trickled in. Enabled by the <c>g_nix</c> cvar (and only when not instagib/overkill/arena).
///
/// Ported here: the enable gate, the item filtering (FilterItemDefinition strips health/armor/powerups
/// unless the respective <c>g_nix_with_*</c> cvar is set), the no-throwing forbid, the random-start weapon
/// forbid, and the headline weapon-rotation engine (<c>NIX_GiveCurrentWeapon</c> / <c>NIX_ChooseNextWeapon</c>):
/// a weighted-random rotation on a per-round timer, per-weapon ammo refill + trickle, the new-weapon /
/// countdown notifications, and the on-remove restore. The engine forces every live player's inventory to
/// the current weapon (+ optional blaster) each frame via <see cref="Inventory"/>.
/// </summary>
[Mutator]
public sealed class NixMutator : MutatorBase
{
    /// <summary>QC autocvar_g_nix_with_healtharmor — keep health/armor pickups.</summary>
    public bool WithHealthArmor;

    /// <summary>QC autocvar_g_nix_with_powerups — keep powerup pickups.</summary>
    public bool WithPowerups;

    /// <summary>QC autocvar_g_nix_with_blaster — also give the blaster alongside the rotating weapon.</summary>
    public bool WithBlaster;

    /// <summary>QC autocvar_g_balance_nix_roundtime — seconds each weapon stays active before rotating.</summary>
    public float RoundTime = 25f;

    /// <summary>QC autocvar_g_balance_nix_incrtime — seconds between ammo trickle increments.</summary>
    public float IncrTime = 1.6f;

    public NixMutator() => NetName = "nix";

    // QC: expr_evaluate(cvar_string("g_nix")) && !MUTATOR_IS_ENABLED(mutator_instagib)
    //     && !MUTATOR_IS_ENABLED(ok) && !MapInfo_LoadedGametype.m_weaponarena.
    // The port has no cross-mutator MUTATOR_IS_ENABLED helper, so the exclusion is expressed against the
    // underlying cvars (the same stand-in NewToys/Vampire/instagib/melee_only use): a direct `g_nix 1`
    // can't double-activate alongside instagib (g_instagib), overkill (g_overkill) or a weapon-arena
    // gametype (g_weaponarena), not just the menu radio-group.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_nix") != 0f
        && Api.Cvars.GetFloat("g_instagib") == 0f
        && Api.Cvars.GetFloat("g_overkill") == 0f
        && Api.Cvars.GetFloat("g_weaponarena") == 0f;

    // QC globals (one world per process — these are the C# successors to nix_weapon/nix_nextchange/etc.).
    private int _nixWeapon;       // currently-active weapon RegistryId (0/none until first round)
    private int _nixNextWeapon;   // chosen-but-not-yet-active weapon RegistryId (0 = needs choosing)
    private float _nixNextChange; // engine time the current round ends / next rotation happens

    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.OnEntityPreSpawnArgs>? _onEntityPreSpawn;

    public override void Hook()
    {
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onForbidThrow ??= OnForbidThrow;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        _onEntityPreSpawn ??= OnEntityPreSpawn;

        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.OnEntityPreSpawn.Add(_onEntityPreSpawn);

        // MUTATOR_ONADD: reset the rotation clock.
        _nixWeapon = 0;
        _nixNextWeapon = 0;
        _nixNextChange = 0f;

        if (Api.Services is not null)
        {
            WithHealthArmor = Api.Cvars.GetFloat("g_nix_with_healtharmor") != 0f;
            WithPowerups = Api.Cvars.GetFloat("g_nix_with_powerups") != 0f;
            WithBlaster = Api.Cvars.GetFloat("g_nix_with_blaster") != 0f;
            float rt = Api.Cvars.GetFloat("g_balance_nix_roundtime");
            if (rt != 0f) RoundTime = rt;
            float it = Api.Cvars.GetFloat("g_balance_nix_incrtime");
            if (it != 0f) IncrTime = it;

            // QC sv_nix.qc:43: FOREACH(Weapons, it != WEP_Null && NIX_CanChooseWeapon(it.m_id), it.wr_init(it)).
            // In Base this warm pass precaches client-side resources (reticle images, shot origins) for the
            // choosable weapons. The port's equivalents run at asset-load time, so WrInit() is a no-op for all
            // current weapons; the call structure is present here for completeness and future overrides.
            foreach (Weapon w in Weapons.All)
            {
                if (CanChooseWeapon(w)) w.WrInit();
            }
        }
    }

    public override void Unhook()
    {
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onEntityPreSpawn is not null) MutatorHooks.OnEntityPreSpawn.Remove(_onEntityPreSpawn);

        // MUTATOR_ONREMOVE (sv_nix.qc:51): as the PlayerSpawn hook will no longer run, NIX is turned off by
        // this — so restore each live player's NORMAL start loadout. QC re-applies start_ammo_shells/nails/
        // rockets/cells/fuel, sets STAT(WEAPONS)=start_weapons, and switches each slot to the best owned weapon
        // when the current one isn't owned. We source start_ammo_*/start_weapons from the same SetStartItems
        // seam the spawn path uses (ComputeStartItems), so arena/loadout mutators are honoured.
        if (Api.Services is not null)
        {
            StartLoadout start = SpawnSystem.ComputeStartItems();
            foreach (Entity p in Api.Entities.FindByClass("player"))
            {
                if ((p.Flags & EntFlags.Client) == 0 || p.DeadState != DeadFlag.No) continue;

                p.SetResource(ResourceType.Shells,  start.AmmoShells);
                p.SetResource(ResourceType.Bullets, start.AmmoBullets);
                p.SetResource(ResourceType.Rockets, start.AmmoRockets);
                p.SetResource(ResourceType.Cells,   start.AmmoCells);
                p.SetResource(ResourceType.Fuel,    start.AmmoFuel);

                // STAT(WEAPONS, it) = start_weapons — reset the owned set to the start loadout. Keep the
                // canonical WepSet (Entity.OwnedWeaponSet, via Inventory) and the NetName string set (on Player,
                // used by status/HasWeapon checks) in sync, mirroring SpawnSystem.ApplyStartLoadout.
                Inventory.ClearWeapons(p);
                if (p is Player pl) pl.OwnedWeapons.Clear();
                foreach (string wn in start.Weapons)
                {
                    if (p is Player pl2) pl2.OwnedWeapons.Add(wn);
                    if (Weapons.ByName(wn) is { } wep) Inventory.GiveWeapon(p, wep);
                }

                // QC: for each slot, if the current weapon isn't owned, switch to w_getbestweapon.
                Weapon? cur = Inventory.CurrentWeapon(p);
                if (cur is null || !p.OwnedWeaponSet.Has(cur))
                    Inventory.SwitchToBest(p);
            }
        }
    }

    // MUTATOR_HOOKFUNCTION(nix, FilterItemDefinition) — delete all items except optionally health/armor/powerups.
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        // QC inspects definition.instanceOfHealth / instanceOfArmor / instanceOfPowerup. The item-class
        // registry isn't ported yet; we approximate via the definition's classname tag.
        string id = args.Definition.ClassName;
        bool isHealth = id.StartsWith("item_health", System.StringComparison.Ordinal);
        bool isArmor = id.StartsWith("item_armor", System.StringComparison.Ordinal);
        bool isPowerup = id is "item_strength" or "item_shield" or "item_invincible";

        if (isHealth || isArmor)
            return !WithHealthArmor;   // disallow unless kept
        if (isPowerup)
            return !WithPowerups;
        // QC keys off definition.instanceOfHealth/instanceOfArmor/instanceOfPowerup; the classname tags
        // above are the faithful stand-in until the item-class registry exposes those instanceOf flags.
        return true; // delete all other items
    }

    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;
    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;

    // MUTATOR_HOOKFUNCTION(nix, OnEntityPreSpawn) — sv_nix.qc:253. target_items triggers cannot work in NIX
    // (they change weapons/ammo and would fight the rotation), so delete them before they spawn (return true).
    private bool OnEntityPreSpawn(ref MutatorHooks.OnEntityPreSpawnArgs args) =>
        args.Entity.ClassName == "target_items";

    // MUTATOR_HOOKFUNCTION(nix, PlayerSpawn) — overrides the spawn loadout with the current NIX weapon.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        player.NixLastChangeId = -1f;     // force a fresh ammo/weapon sync this frame
        GiveCurrentWeapon(player);
        // QC sv_nix.qc:282 — player.items |= IT_UNLIMITED_SUPERWEAPONS, so a superweapon NIX weapon never
        // expires under the per-frame superweapon-timeout pass (PlayerFrameLogic.SuperweaponTimeout). Latent in
        // stock play (no superweapon enters the rotation — they lack WEP_FLAG_NORMAL), but faithful and cheap.
        player.Items |= (int)ItemFlag.UnlimitedSuperweapons;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(nix, PlayerPreThink) — keep every live player on the current NIX weapon.
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        // QC sv_nix.qc:265-268 gates on !game_stopped && !IS_DEAD && IS_PLAYER. The shared PlayerPreThink
        // dispatch site fires unconditionally, so the game_stopped freeze (intermission / match-ended) has to
        // be honoured here — otherwise the rotation clock would keep advancing and re-forcing the weapon set
        // during intermission. VehicleCommon.GameStopped is the host's game_stopped mirror (same flat-namespace
        // signal instagib/buffs/powerups read).
        if (VehicleCommon.GameStopped) return false;

        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) != 0 && player.DeadState == DeadFlag.No)
            GiveCurrentWeapon(player);
        return false;
    }

    // bool NIX_CanChooseWeapon(int wpn) — only the "normal" non-mutatorblocked weapons rotate.
    // (QC also skips WEP_Null; there is no dummy weapon in the C# registry, so no id-0 guard is needed.)
    private bool CanChooseWeapon(Weapon w)
    {
        if (WithBlaster && w.NetName == "blaster") return false;      // blaster is given separately
        if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) return false;
        if ((w.SpawnFlags & WeaponFlags.Normal) == 0) return false;
        return true;
    }

    // void NIX_ChooseNextWeapon() — weighted-random pick, biased to differ from the current weapon.
    private void ChooseNextWeapon()
    {
        // QC RandomSelection_AddFloat(it.m_id, 1, (it.m_id != nix_weapon)) — every choosable weapon has
        // weight 1, but a non-zero "priority" only for weapons != current, so the current is avoided when
        // any alternative exists. Reproduce: collect candidates, prefer the subset that isn't current.
        List<Weapon> all = new();
        List<Weapon> notCurrent = new();
        foreach (Weapon w in Weapons.All)
        {
            if (!CanChooseWeapon(w)) continue;
            all.Add(w);
            if (w.RegistryId != _nixWeapon) notCurrent.Add(w);
        }
        List<Weapon> pool = notCurrent.Count > 0 ? notCurrent : all;
        _nixNextWeapon = pool.Count > 0 ? pool[Prandom.RangeInt(0, pool.Count)].RegistryId : 0;
    }

    // void NIX_GiveCurrentWeapon(entity this) — the rotation engine core (per-player, per-frame).
    private void GiveCurrentWeapon(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        if (_nixNextWeapon == 0)
            ChooseNextWeapon();

        float dt = MathF.Ceiling(_nixNextChange - now);

        if (dt <= 0f)
        {
            _nixWeapon = _nixNextWeapon;
            _nixNextWeapon = 0;
            if (_nixNextChange == 0f)
                _nixNextChange = now;            // first round starts now
            else
                _nixNextChange = now + RoundTime; // schedule the next rotation
        }

        Weapon? wpn = _nixWeapon > 0 && _nixWeapon < Registry<Weapon>.Count
            ? Registry<Weapon>.ById(_nixWeapon) : null;
        if (wpn is null) return;

        ResourceType ammoType = AmmoTypeOf(wpn.NetName);

        // QC: branch the ammo refill on IT_UNLIMITED_AMMO — when set, fill to g_pickup_<type>_max and skip the
        // trickle entirely; otherwise fill to g_balance_nix_ammo_<type> and trickle. IT_UNLIMITED_AMMO is not set
        // in stock NIX, so default play is unchanged.
        bool unlimitedAmmo = (player.Items & (int)ItemFlag.UnlimitedAmmo) != 0;

        // Once-per-round per-player sync: wipe ammo, refill the current weapon's ammo type, reset the
        // trickle timer, and notify of the new weapon.
        if (_nixNextChange != player.NixLastChangeId)
        {
            player.SetResource(ResourceType.Shells, 0f);
            player.SetResource(ResourceType.Bullets, 0f);
            player.SetResource(ResourceType.Rockets, 0f);
            player.SetResource(ResourceType.Cells, 0f);
            player.SetResource(ResourceType.Fuel, 0f);

            if (ammoType != ResourceType.None)
                player.SetResource(ammoType, unlimitedAmmo ? AmmoPickupMax(ammoType) : AmmoStart(ammoType));

            player.NixNextIncr = now + IncrTime;

            // QC sv_nix.qc:161-164: if this once-per-round sync lands inside the last-5s countdown window
            // (e.g. a respawn near the end of a round), suppress the NEWWEAPON notification AND force the
            // countdown block below to fire by poisoning the de-dupe sentinel (nix_lastinfotime = -42, which
            // can never equal a real dt in [1,5]); otherwise announce the new weapon.
            if (dt >= 1f && dt <= 5f)
                player.NixLastInfoTime = -42f;
            else
                NotificationSystem.Center(player, "NIX_NEWWEAPON", _nixWeapon);

            // QC sv_nix.qc:166: wpn.wr_resetplayer(wpn, this) — reset the weapon's per-player think state
            // on every round flip (same reset SpawnSystem calls on respawn). Hagar clears its loaded-rocket
            // counter, Porto drops the single-portal latch, Vaporizer clears its streak, etc.
            // Slot 0 is always the active weapon slot in the current single-slot model.
            wpn.WrResetPlayer(player, new WeaponSlot(0));

            // QC sv_nix.qc:168-176: a reloadable weapon must start fully loaded when the round flips —
            // weapon_load[nix_weapon] = wpn.reloading_ammo across every slot. The port models wr_resetplayer
            // lazily (the fire driver seeds the slot on raise), so reproduce the observable clip prefill here:
            // seed slot 0's weapon_load[nix_weapon] to the full clip and mark it seeded so the lazy driver
            // doesn't re-seed (or refill it for free) later. If the weapon is the one in hand, top its live clip.
            if ((wpn.SpawnFlags & WeaponFlags.Reloadable) != 0)
            {
                int full = (int)wpn.ReloadingAmmo();
                if (full > 0)
                {
                    WeaponSlotState slot0 = player.WeaponState(new WeaponSlot(0));
                    Weapon.SetWeaponLoad(slot0, _nixWeapon, full);
                    (slot0.WeaponLoadSeeded ??= new HashSet<int>()).Add(_nixWeapon);
                    if (player.ActiveWeaponId == _nixWeapon)
                    {
                        slot0.ClipLoad = full;
                        slot0.ClipSize = full;
                    }
                }
            }

            player.NixLastChangeId = _nixNextChange;
        }

        // Countdown notification (de-duped) during the last 5 seconds before a rotation.
        if (player.NixLastInfoTime != dt)
        {
            player.NixLastInfoTime = dt;
            if (dt >= 1f && dt <= 5f)
                NotificationSystem.Center(player, "NIX_COUNTDOWN", _nixNextWeapon, dt);
        }

        // Ammo trickle: top up the current weapon's ammo a little, every IncrTime seconds. QC gates this on
        // !(items & IT_UNLIMITED_AMMO) — an unlimited-ammo player was max-filled above and never trickles.
        if (!unlimitedAmmo && now > player.NixNextIncr)
        {
            if (ammoType != ResourceType.None)
                player.GiveResource(ammoType, AmmoIncr(ammoType));
            player.NixNextIncr = now + IncrTime;
        }

        // Force the owned set to exactly {current weapon (+ blaster if with_blaster)} every frame, mirroring
        // QC's STAT(WEAPONS) = wpn.m_wepset (+ blaster) rewrite.
        Weapon? blasterWep = WithBlaster ? Weapons.ByName("blaster") : null;

        player.OwnedWeaponSet.Clear();
        if (blasterWep is not null) player.OwnedWeaponSet.Add(blasterWep);
        player.OwnedWeaponSet.Add(wpn);

        // QC sv_nix.qc:207-219 switch guard: only FORCE a switch to the NIX weapon when the player's current
        // switch target is NOT itself an owned weapon AND the player owns wpn. This makes a manual mid-round
        // switch sticky under g_nix_with_blaster (switching to the blaster keeps the blaster, since it's owned),
        // instead of re-asserting the switch every frame.
        int switchId = player.SwitchWeaponId >= 0 ? player.SwitchWeaponId : player.ActiveWeaponId;
        Weapon? switchTarget = switchId >= 0 && switchId < Registry<Weapon>.Count
            ? Registry<Weapon>.ById(switchId) : null;
        bool switchTargetOwned = switchTarget is not null && player.OwnedWeaponSet.Has(switchTarget);
        if ((switchTarget != wpn) && !switchTargetOwned && player.OwnedWeaponSet.Has(wpn))
            Inventory.SwitchWeapon(player, wpn);
    }

    /// <summary>QC wpn.ammo_type — the standard weapon→resource mapping (bal-wep-xonotic ATTRIB ammo_type).</summary>
    private static ResourceType AmmoTypeOf(string netName) => netName switch
    {
        "shotgun" => ResourceType.Shells,
        "machinegun" or "rifle" => ResourceType.Bullets,
        "mortar" or "hagar" or "devastator" or "seeker" or "minelayer" => ResourceType.Rockets,
        "vortex" or "electro" or "crylink" or "hlac" or "arc" or "vaporizer" => ResourceType.Cells,
        "hook" => ResourceType.Fuel,
        _ => ResourceType.None, // blaster / porto / tuba — ammo-less
    };

    private float AmmoStart(ResourceType t) => t switch
    {
        ResourceType.Shells  => Cvar("g_balance_nix_ammo_shells", 60f),
        ResourceType.Bullets => Cvar("g_balance_nix_ammo_nails", 320f),
        ResourceType.Rockets => Cvar("g_balance_nix_ammo_rockets", 160f),
        ResourceType.Cells   => Cvar("g_balance_nix_ammo_cells", 180f),
        ResourceType.Fuel    => Cvar("g_balance_nix_ammo_fuel", 100f),
        _ => 0f,
    };

    /// <summary>QC autocvar_g_pickup_&lt;type&gt;_max — the IT_UNLIMITED_AMMO max-fill target (matches the item
    /// pickup caps in ItemPickupRules / the balance defaults).</summary>
    private float AmmoPickupMax(ResourceType t) => t switch
    {
        ResourceType.Shells  => Cvar("g_pickup_shells_max", 60f),
        ResourceType.Bullets => Cvar("g_pickup_nails_max", 320f),
        ResourceType.Rockets => Cvar("g_pickup_rockets_max", 160f),
        ResourceType.Cells   => Cvar("g_pickup_cells_max", 180f),
        ResourceType.Fuel    => Cvar("g_pickup_fuel_max", 100f),
        _ => 0f,
    };

    private float AmmoIncr(ResourceType t) => t switch
    {
        ResourceType.Shells  => Cvar("g_balance_nix_ammoincr_shells", 2f),
        ResourceType.Bullets => Cvar("g_balance_nix_ammoincr_nails", 6f),
        ResourceType.Rockets => Cvar("g_balance_nix_ammoincr_rockets", 2f),
        ResourceType.Cells   => Cvar("g_balance_nix_ammoincr_cells", 2f),
        ResourceType.Fuel    => Cvar("g_balance_nix_ammoincr_fuel", 2f),
        _ => 0f,
    };

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // MUTATOR_HOOKFUNCTION(nix, BuildMutatorsString) — sv_nix.qc:227.
    public override string BuildMutatorsString(string s) => s + ":NIX";

    // MUTATOR_HOOKFUNCTION(nix, BuildMutatorsPrettyString) — sv_nix.qc:232.
    public override string BuildMutatorsPrettyString(string s) => s + ", NIX";

    // MUTATOR_HOOKFUNCTION(nix, SetModname, CBC_ORDER_LAST) — sv_nix.qc:285: override the server modname
    // to "NIX" so the server browser and client connection banner reflect the active game mode.
    // Returns overridden=true so the chain stops here (QC: return true via CBC_ORDER_ANY early-exit).
    public override (string name, bool overridden) SetModname(string name) => ("NIX", true);
}
