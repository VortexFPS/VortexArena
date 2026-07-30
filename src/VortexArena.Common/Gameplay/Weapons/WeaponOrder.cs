// Port of qcsrc/common/weapons/all.qc (W_FixWeaponOrder / W_NameWeaponOrder / W_NumberWeaponOrder /
// W_FixWeaponOrder_ForceComplete) + qcsrc/common/util.qc (fixPriorityList / mapPriorityList /
// swapInPriorityList). The reorderable cl_weaponpriority backend the menu weapons-list edits.
//
// This is the Godot-free home for the weapon-priority helpers so the menu's WeaponPriorityList widget
// (game/menu/framework/WeaponPriorityList.cs) and the unit tests (tests can't see game/) share one
// implementation. Only READS the Weapon registry (NetName / SpawnFlags) — no weapon-state mutation.
using System;
using System.Collections.Generic;
using System.Text;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Weapon priority-list string helpers — the C# successors to the QuakeC <c>W_*WeaponOrder</c> /
/// <c>*PriorityList</c> functions. A "weapon order" is a space-separated list, in either NAME form
/// (<c>"shotgun machinegun blaster"</c> — what <c>cl_weaponpriority</c> stores and the user edits) or NUMBER
/// form (<c>"15 9 1"</c> — registry ids, what the list widget tokenizes for display/reorder). The menu widget
/// numbers the cvar (<see cref="NumberWeaponOrder"/>), fixes/completes it (<see cref="FixWeaponOrder"/>), and
/// names it back (<see cref="NameWeaponOrder"/>) when writing the cvar.
///
/// PORT DIVERGENCE (registry id space): QC has a Null weapon at id 0 so <c>WEP_FIRST == 1</c> and a second
/// id space for impulse-named weapons (<c>WEP_IMPULSE_BEGIN == 230</c>, hence <c>subtract = 229</c>). This
/// port registers NO Null weapon — every id in <c>[0, Count)</c> is a real weapon — and has no separate
/// impulse-id space, so <see cref="FixWeaponOrder"/> uses <c>from = 0</c>, <c>to = Count-1</c>,
/// <c>subtract = 0</c>. This mirrors <see cref="Inventory.WeaponOrderById"/>, which likewise documents that
/// id 0 is real here.
/// </summary>
public static class WeaponOrder
{
    /// <summary>QC <c>WEP_FIRST</c> — the first weapon id. 0 here (no Null weapon); QC uses 1.</summary>
    public static int WepFirst => 0;

    /// <summary>QC <c>WEP_LAST</c> — the last weapon id (<c>REGISTRY_COUNT(Weapons) - 1</c>).</summary>
    public static int WepLast => Registry<Weapon>.Count - 1;

    /// <summary>
    /// Port of <c>fixPriorityList</c> (util.qc:598): tokenize <paramref name="order"/> on whitespace; keep only
    /// integer ids in <c>[from, to]</c> (or <c>id - subtract</c> when that lands in range — the QC alternate
    /// impulse-id space); when <paramref name="complete"/>, append every missing id from <paramref name="to"/>
    /// DOWN to <paramref name="from"/>, SKIPPING any weapon whose spawnflags have
    /// <see cref="WeaponFlags.SpecialAttack"/> (QC <c>WEP_FLAG_SPECIALATTACK</c>). Returns the rebuilt
    /// space-separated id list. Unknown / non-integer tokens are dropped (matching QC's integer-range filter).
    ///
    /// <para><paramref name="menuRegistryView"/> = run as Base's MENU program would: a weapon absent from the
    /// menu registry (<see cref="Weapon.MenuRegistered"/> false, i.e. the Nexball BallStealer) is neither kept
    /// nor appended — in Base its name can't even resolve to an id there, so the menu fix-up drops a stray
    /// token and never adds one. The default (false) is the client/server view, which sees every weapon.</para>
    /// </summary>
    public static string FixPriorityList(string order, int from, int to, int subtract, bool complete,
        bool menuRegistryView = false)
    {
        // A weapon the MENU program's registry doesn't contain (Base compiles menu QC with no WEP_NEXBALL).
        bool MenuHidden(int id) =>
            menuRegistryView && id >= 0 && id < Registry<Weapon>.Count && !Registry<Weapon>.ById(id).MenuRegistered;

        var sb = new StringBuilder();
        // First pass: keep in-range integer ids (QC: w == floor(w) && in range, else try w - subtract).
        foreach (string tok in Tokenize(order))
        {
            if (!int.TryParse(tok, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int w))
                continue; // non-integer (QC: w != floor(w) drops it)
            if (w >= from && w <= to)
            {
                if (MenuHidden(w)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
            }
            else
            {
                int w2 = w - subtract;
                if (w2 >= from && w2 <= to)
                {
                    if (MenuHidden(w2)) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(w2);
                }
            }
        }

        if (complete)
        {
            // Second pass: append any missing id, descending to..from, skipping special-attack weapons. QC
            // re-tokenizes neworder each time; we keep a HashSet of what's already present (same membership test).
            var present = new HashSet<int>();
            foreach (string t in Tokenize(sb.ToString()))
                if (int.TryParse(t, out int v)) present.Add(v);

            for (int w = to; w >= from; --w)
            {
                if (w < 0 || w >= Registry<Weapon>.Count) continue;
                int wflags = Registry<Weapon>.ById(w).SpawnFlags;
                if ((wflags & WeaponFlags.SpecialAttack) != 0)
                    continue;
                if (MenuHidden(w))
                    continue;
                if (present.Contains(w))
                    continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
                present.Add(w);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>W_FixWeaponOrder</c> (all.qc:99): <c>fixPriorityList(order, WEP_FIRST, WEP_LAST,
    /// WEP_IMPULSE_BEGIN - WEP_FIRST, complete)</c>. Here <c>WEP_FIRST = 0</c>, <c>WEP_LAST = Count-1</c>,
    /// <c>subtract = 0</c> (see the class-level divergence note).
    /// </summary>
    public static string FixWeaponOrder(string order, bool complete, bool menuRegistryView = false)
        => FixPriorityList(order, WepFirst, WepLast, 0, complete, menuRegistryView);

    /// <summary>
    /// Port of <c>W_FixWeaponOrder_ForceComplete</c> (all.qc:171): if <paramref name="order"/> is empty, seed it
    /// from the cvar's DEFAULT (<c>cvar_defstring("cl_weaponpriority")</c>, numbered), then
    /// <c>FixWeaponOrder(order, true)</c>. <paramref name="defaultPriorityNames"/> is the shipped default in NAME
    /// form (the caller passes <c>Cvars.GetDefault("cl_weaponpriority")</c>).
    /// </summary>
    public static string FixWeaponOrderForceComplete(string order, string defaultPriorityNames)
    {
        if (string.IsNullOrEmpty(order))
            order = NumberWeaponOrder(defaultPriorityNames);
        return FixWeaponOrder(order, true);
    }

    /// <summary>
    /// Port of <c>mapPriorityList</c> (util.qc:640): tokenize on whitespace, apply <paramref name="map"/> to each
    /// token, re-join with single spaces. (QC builds <c>strcat(neworder, mapfunc(argv(i)), " ")</c> then trims
    /// the trailing space.)
    /// </summary>
    public static string MapPriorityList(string order, Func<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (string tok in Tokenize(order))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(map(tok));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>W_NameWeaponOrder_MapFunc</c> (all.qc:103): a numeric id maps to its weapon's NetName; a
    /// non-numeric token (or an out-of-range id) passes through unchanged.
    /// </summary>
    public static string NameWeaponOrderMapFunc(string s)
    {
        // QC: if (s == "0" || stof(s)) { wi = REGISTRY_GET(Weapons,i); if (wi != WEP_Null) return wi.netname; }
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int i)
            && i >= 0 && i < Registry<Weapon>.Count)
        {
            return Registry<Weapon>.ById(i).NetName;
        }
        return s;
    }

    /// <summary>Port of <c>W_NameWeaponOrder</c> (all.qc:115): number form → name form.</summary>
    public static string NameWeaponOrder(string order) => MapPriorityList(order, NameWeaponOrderMapFunc);

    /// <summary>
    /// Port of <c>W_NumberWeaponOrder_MapFunc</c> (all.qc:119): an already-numeric token passes through; else
    /// look the name up in the registry by NetName and return its id. An UNKNOWN name (e.g. the Overkill "ok*"
    /// tokens in the shipped default, absent from this registry) passes through UNCHANGED — and is then dropped
    /// by <see cref="FixPriorityList"/>'s integer-range filter, exactly as QC does.
    /// </summary>
    public static string NumberWeaponOrderMapFunc(string s)
    {
        // QC: if (s == "0" || stof(s)) return s;  (token already numeric → keep)
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return s;
        // FOREACH(Weapons, it.netname == s || it.m_deprecated_netname == s, return ftos(i));
        Weapon? w = Registry<Weapon>.ByName(s);
        return w is not null ? w.RegistryId.ToString(System.Globalization.CultureInfo.InvariantCulture) : s;
    }

    /// <summary>Port of <c>W_NumberWeaponOrder</c> (all.qc:126): name form → number form.</summary>
    public static string NumberWeaponOrder(string order) => MapPriorityList(order, NumberWeaponOrderMapFunc);

    /// <summary>
    /// Port of <c>swapInPriorityList</c> (util.qc:651): swap the tokens at positions <paramref name="i"/> and
    /// <paramref name="j"/> in <paramref name="order"/>, re-joining with single spaces. Out-of-range indices or
    /// <c>i == j</c> return <paramref name="order"/> unchanged (the QC bounds guard).
    /// </summary>
    public static string SwapInPriorityList(string order, int i, int j)
    {
        List<string> toks = Tokenize(order);
        int n = toks.Count;
        if (i >= 0 && i < n && j >= 0 && j < n && i != j)
        {
            (toks[i], toks[j]) = (toks[j], toks[i]);
            return string.Join(' ', toks);
        }
        return order;
    }

    /// <summary>QC <c>tokenize_console</c> on a space list: split on any whitespace, drop empties.</summary>
    private static List<string> Tokenize(string s)
        => new(s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // =================================================================================================
    //  QC unique-impulse / by-id order (all.qh:353 REGISTRY_SORT + the m_unique_impulse allocation)
    // =================================================================================================

    /// <summary>
    /// The QC weapon DEFINITION order (common/weapons/all.inc) for the 19 "hardcoded-impulse" core weapons,
    /// in the exact include order. QC's <c>REGISTRY_SORT(Weapons, WEP_HARDCODED_IMPULSES + 1)</c> (all.qh:353,
    /// <c>skip = 20</c>) heapsorts only registry indices &gt;= 20, leaving index 0 (<c>WEP_Null</c>) and indices
    /// 1..19 (these weapons) in definition order. <c>m_unique_impulse</c> is then assigned in registry-index
    /// order from <c>WEP_IMPULSE_BEGIN == 230</c> (all.qh:359-382), so <c>weapon_byid_0</c> (impulse 230) is
    /// blaster, <c>weapon_byid_1</c> shotgun, and so on. The port has no Null weapon, so id 0 here maps straight
    /// onto blaster (see <see cref="ByIdOrder"/>).
    /// </summary>
    private static readonly string[] CoreDefinitionOrder =
    {
        "blaster", "shotgun", "machinegun", "mortar", "minelayer", "electro", "crylink", "vortex", "hagar",
        "devastator", "porto", "vaporizer", "hook", "hlac", "tuba", "rifle", "fireball", "seeker", "arc",
    };

    /// <summary>
    /// Port of the QC <c>m_unique_impulse</c> allocation ORDER (all.qh:359-382): the registry walked in
    /// <c>FOREACH(Weapons)</c> (registry-index) order, skipping <c>WEP_Null</c>, any
    /// <see cref="WeaponFlags.SpecialAttack"/> weapon, and the Ball-Stealer special case
    /// (<see cref="WeaponFlags.MutatorBlocked"/> AND <see cref="WeaponFlags.TypeOther"/>). The Nth surviving
    /// weapon gets impulse <c>230 + N</c>, i.e. is reachable as <c>weapon_byid_N</c>.
    ///
    /// <para>The QC registry order is: index 0 = Null (absent in this port), indices 1..19 = the core weapons
    /// in <see cref="CoreDefinitionOrder"/>, indices 20+ = every other weapon STRCMP-sorted by
    /// <c>registered_id</c> (the <c>WEP_*</c> token). For the port's Overkill tail the <c>registered_id</c>
    /// ordinal order (<c>WEP_OVERKILL_HMG/MACHINEGUN/NEX/RPC/SHOTGUN</c>) coincides with the NetName ordinal
    /// order (okhmg/okmachinegun/oknex/okrpc/okshotgun), which is the <see cref="Registry{T}.Sort"/> order the
    /// port already imposes — so the tail is taken straight from the live registry, filtered the same way.</para>
    ///
    /// <para>Returns the weapons in by-id order; index <c>N</c> is the weapon <c>weapon_byid_N</c> selects. Any
    /// core name absent from the registry is skipped (defensive; all 19 exist), and registry weapons not in the
    /// core list are appended in registry (Sort) order after the core block, mirroring the QC tail.</para>
    /// </summary>
    public static IReadOnlyList<Weapon> ByIdOrder()
    {
        var ordered = new List<Weapon>(Registry<Weapon>.Count);
        var taken = new HashSet<Weapon>();

        // Core block: the 19 hardcoded-impulse weapons in QC definition order.
        foreach (string name in CoreDefinitionOrder)
        {
            Weapon? w = Registry<Weapon>.ByName(name);
            if (w is null) continue;          // defensive — all 19 are registered
            if (IsImpulseSkipped(w)) continue; // none of the core weapons are skipped, but stay faithful
            ordered.Add(w);
            taken.Add(w);
        }

        // Tail: every remaining weapon in registry (ordinal NetName == strcmp registered_id) order, applying
        // the same QC skip rules (Null/specialattack/Ball-Stealer). This is the heapsorted >= 20 region.
        foreach (Weapon w in Registry<Weapon>.All)
        {
            if (taken.Contains(w)) continue;
            if (IsImpulseSkipped(w)) continue;
            ordered.Add(w);
        }

        return ordered;
    }

    /// <summary>
    /// QC's <c>m_unique_impulse</c> skip rules (all.qh:367-372): <c>WEP_Null</c> (no Null weapon in this port),
    /// any <see cref="WeaponFlags.SpecialAttack"/> weapon, and the Ball-Stealer special case
    /// (<see cref="WeaponFlags.MutatorBlocked"/> AND <see cref="WeaponFlags.TypeOther"/>) never receive an
    /// impulse, so they are unreachable by id.
    /// </summary>
    private static bool IsImpulseSkipped(Weapon w)
    {
        if ((w.SpawnFlags & WeaponFlags.SpecialAttack) != 0) return true;
        if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0
            && (w.SpawnFlags & WeaponFlags.TypeOther) != 0) return true;
        return false;
    }

    /// <summary>
    /// Resolve <c>weapon_byid_N</c> (<paramref name="idx"/> = the impulse minus <c>WEP_IMPULSE_BEGIN</c>) to its
    /// weapon, mirroring QC <c>Weapon_from_impulse(230 + idx)</c>. Returns null when <paramref name="idx"/> is out
    /// of range of the impulse-reachable weapons.
    /// </summary>
    public static Weapon? WeaponByIdIndex(int idx)
    {
        IReadOnlyList<Weapon> order = ByIdOrder();
        return idx >= 0 && idx < order.Count ? order[idx] : null;
    }
}
