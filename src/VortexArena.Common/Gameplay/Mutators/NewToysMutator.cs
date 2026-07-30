// Port of common/mutators/mutator/new_toys/sv_new_toys.qc (+ new_toys/new_toys.qc nt_IsNewToy/nt_GetReplacement)

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The New Toys mutator — port of common/mutators/mutator/new_toys/sv_new_toys.qc. Enabled by default in QC
/// (<c>expr_evaluate(cvar_string("g_new_toys"))</c>) but only when neither instagib nor overkill is active, it
/// lets a set of "gimmicky" weapons spawn — Seeker, Mine Layer, HLAC, Rifle, Arc — sometimes replacing a core
/// weapon (Hagar→Seeker, Devastator→Mine Layer, Machinegun→HLAC, Vortex→Rifle). The replacement runs on the
/// DEFAULT start-weapon selection and on map weapon entities.
///
/// Ported: the enable gate (with the instagib/overkill exclusions), the new-toy ↔ core mapping
/// (<see cref="GetFullReplacement"/> / <see cref="GetReplacement"/>), the new-toy weapon set
/// (<see cref="IsNewToy"/>), the MUTATOR_ONADD/REMOVE weapon-unblock (clear <see cref="WeaponFlags.MutatorBlocked"/>
/// on the new-toy weapons so they're givable, restore it when the mutator drops), and the SetStartItems rearrange
/// — for each start weapon with a replacement, OR in its resolved tokens per <c>g_new_toys_autoreplace</c>
/// (NEVER keeps it, ALWAYS swaps it for the new-toy variant, RANDOM grants BOTH the core and the new-toy — faithful
/// to QC's bit-OR of both <c>nt_GetReplacement</c> tokens into <c>newdefault</c>, not a 50/50 coin-flip).
///
/// Also ported (Wave-6b): QC's <c>SetWeaponreplace</c> hook (rewrite the weapon a map <c>weapon_*</c> entity
/// spawns as — per the map <c>"new_toys"</c> BSP key, else the global <c>g_new_toys_autoreplace</c> mapping, with
/// a random_items early-out and a <c>W_Apply_Weaponreplace</c> post-pass) and the <c>FilterItem</c> hook (swap a
/// new-toy weapon pickup's sound to the "New toys, new toys!" roflsound when <c>g_new_toys_use_pickupsound</c> is
/// set). The weapon-spawn pipeline (<c>ItemSpawnFuncs.WeaponSpawn</c>) fires <see cref="MutatorHooks.SetWeaponreplace"/>
/// and tokenizes the result; <c>StartItem.Spawn</c> fires <see cref="MutatorHooks.FilterItemDefinition"/>, which this
/// mutator reuses for the pickup-sound override (<c>Entity.ItemPickupSoundOverride</c>).
/// </summary>
[Mutator]
public sealed class NewToysMutator : MutatorBase
{
    /// <summary>QC NT_AUTOREPLACE_NEVER — keep the core weapon (no replacement).</summary>
    public const int AutoReplaceNever = 0;

    /// <summary>QC NT_AUTOREPLACE_ALWAYS — always replace the core weapon with its new-toy variant.</summary>
    public const int AutoReplaceAlways = 1;

    /// <summary>QC NT_AUTOREPLACE_RANDOM — replace or keep, chosen at random per weapon.</summary>
    public const int AutoReplaceRandom = 2;

    /// <summary>QC autocvar_g_new_toys_autoreplace.</summary>
    public int AutoReplace = AutoReplaceNever;

    public NewToysMutator() => NetName = "new_toys";

    // QC: REGISTER_MUTATOR(nt, expr_evaluate(cvar_string("g_new_toys")) && !instagib && !ok).
    public override bool IsEnabled =>
        Api.Services is not null
        && ExprEvaluate(Api.Cvars.GetString("g_new_toys"))
        && Api.Cvars.GetFloat("g_instagib") == 0f
        && Api.Cvars.GetFloat("g_overkill") == 0f;

    /// <summary>QC nt_IsNewToy(w): the weapons this mutator unlocks (by NetName — the port's m_id stand-in).</summary>
    private static readonly HashSet<string> NewToyNames = new(StringComparer.Ordinal)
    {
        "seeker", "minelayer", "mine_layer", "hlac", "rifle", "arc",
    };

    /// <summary>QC nt_IsNewToy(int w) — true for a new-toy weapon by NetName.</summary>
    public static bool IsNewToy(string netName) => NewToyNames.Contains(netName);

    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<MutatorHooks.SetWeaponreplaceArgs>? _onSetWeaponreplace;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItem;

    public override void Hook()
    {
        _onSetStartItems ??= OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_onSetStartItems);
        _onSetWeaponreplace ??= OnSetWeaponreplace;
        MutatorHooks.SetWeaponreplace.Add(_onSetWeaponreplace);
        _onFilterItem ??= OnFilterItem;
        MutatorHooks.FilterItemDefinition.Add(_onFilterItem);

        if (Api.Services is not null)
            AutoReplace = (int)Api.Cvars.GetFloat("g_new_toys_autoreplace");

        // MUTATOR_ONADD: mark the new-toy guns ok to spawn / give (clear WEP_FLAG_MUTATORBLOCKED).
        SetNewToyBlocked(false);
    }

    public override void Unhook()
    {
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onSetWeaponreplace is not null) MutatorHooks.SetWeaponreplace.Remove(_onSetWeaponreplace);
        if (_onFilterItem is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItem);

        // MUTATOR_ONROLLBACK_OR_REMOVE: re-block the new-toy guns. (QC also returns -1 from ONREMOVE because it
        // "cannot be removed at runtime"; the port has no live-remove guard, so just restore the flags.)
        SetNewToyBlocked(true);
    }

    /// <summary>
    /// QC MUTATOR_ONADD/ONROLLBACK FOREACH(Weapons, nt_IsNewToy(it.m_id), it.spawnflags ^= WEP_FLAG_MUTATORBLOCKED).
    /// Sets or clears <see cref="WeaponFlags.MutatorBlocked"/> on every new-toy weapon so it can / can't be given.
    /// </summary>
    private static void SetNewToyBlocked(bool blocked)
    {
        foreach (Weapon w in Weapons.All)
        {
            if (!IsNewToy(w.NetName)) continue;
            if (blocked) w.SpawnFlags |= WeaponFlags.MutatorBlocked;
            else w.SpawnFlags &= ~WeaponFlags.MutatorBlocked;
        }
    }

    /// <summary>QC nt_GetFullReplacement(w): the core→new-toy mapping (string_null = no replacement).</summary>
    public static string? GetFullReplacement(string w) => w switch
    {
        "hagar" => "seeker",
        "devastator" => "minelayer",
        "machinegun" => "hlac",
        "vortex" => "rifle",
        _ => null,
    };

    /// <summary>
    /// QC nt_GetReplacement(w, m): the replacement token list for a weapon under autoreplace mode <paramref name="m"/>.
    /// NEVER → the weapon itself; no mapping → itself; ALWAYS → the new-toy variant; RANDOM → "w newtoy" (pick one).
    /// </summary>
    public static string GetReplacement(string w, int m)
    {
        if (m == AutoReplaceNever) return w;
        string? s = GetFullReplacement(w);
        if (s is null) return w;
        if (m == AutoReplaceRandom) return w + " " + s;   // QC strcat(w, " ", s)
        return s;
    }

    // MUTATOR_HOOKFUNCTION(nt, SetStartItems) — rearrange the default start weapons through GetReplacement.
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;

        // QC walks every weapon, tokenizes nt_GetReplacement(it.netname, autoreplace), and ORs EVERY resolved
        // token's weapon-bit into newdefault whenever the source weapon's bit is set in start_weapons. So for a
        // start weapon `w` the resolved set replaces it:
        //   NEVER (or no mapping) → {w}            (unchanged)
        //   ALWAYS               → {newtoy}        (core dropped, new-toy added)
        //   RANDOM               → {w, newtoy}     (BOTH bits OR'd in — player receives both, NOT a coin-flip)
        // The port's loadout is a NetName set (HashSet), so we mirror that: gather the resolved token set for each
        // current start weapon, then rebuild Weapons from the union. (No start_weapons_defaultmask in the port —
        // the whole start set is treated as the default mask, matching the stock blaster-only loadout.)
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (string have in l.Weapons)
        {
            // nt_GetReplacement returns the space-joined token list for this autoreplace mode; OR every token in.
            string repl = GetReplacement(have, AutoReplace);
            foreach (string tok in repl.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                resolved.Add(tok);
        }

        l.Weapons.Clear();
        foreach (string tok in resolved) l.Weapons.Add(tok);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(nt, SetWeaponreplace) — rewrite a map weapon_* entity's spawn (sv_new_toys.qc:181).
    private bool OnSetWeaponreplace(ref MutatorHooks.SetWeaponreplaceArgs args)
    {
        // QC: if (MUTATOR_IS_ENABLED(random_items)) return; — do not replace weapons when random items are on.
        if (Mutators.ByName("random_items") is { IsEnabled: true })
            return false;

        if (Api.Services is not null)
            AutoReplace = (int)Api.Cvars.GetFloat("g_new_toys_autoreplace");

        // QC: if (wep.new_toys) ret = wep.new_toys; else ret = nt_GetReplacement(wepinfo.netname, autoreplace);
        string ret = !string.IsNullOrEmpty(args.Item.NewToys)
            ? args.Item.NewToys!
            : GetReplacement(args.Weapon.NetName, AutoReplace);

        // QC: ret = W_Apply_Weaponreplace(ret); M_ARGV(2, string) = ret;
        args.Replacement = ItemSpawnFuncs.W_Apply_Weaponreplace(ret);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(nt, FilterItem) — swap a new-toy weapon's pickup sound (sv_new_toys.qc:210).
    private bool OnFilterItem(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity item = args.Definition;
        // QC: if (nt_IsNewToy(item.weapon) && autocvar_g_new_toys_use_pickupsound) { item_pickupsound = null;
        //         item_pickupsound_ent = SND_WEAPONPICKUP_NEW_TOYS; }
        // The port's world item carries its weapon via the def NetName (weapon pickups set item.NetName to the
        // weapon name); gate on that + the weapon-pickup flag so non-weapon items are untouched.
        bool isWeaponPickup = item.Pickup?.IsWeaponPickup == true;
        if (isWeaponPickup && IsNewToy(item.NetName)
            && Api.Services is not null && Api.Cvars.GetFloat("g_new_toys_use_pickupsound") != 0f)
        {
            item.ItemPickupSoundOverride = "WEAPONPICKUP_NEW_TOYS"; // SND_WEAPONPICKUP_NEW_TOYS roflsound
        }
        return false; // never forbid the spawn (QC FilterItem here only swaps the sound)
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
