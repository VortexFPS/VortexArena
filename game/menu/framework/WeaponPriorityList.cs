// Port of qcsrc/menu/xonotic/weaponslist.qc (XonoticWeaponsList).
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The reorderable weapon-priority list — a faithful C# port of <c>XonoticWeaponsList</c>
/// (qcsrc/menu/xonotic/weaponslist.qc). Bound to <c>cl_weaponpriority</c> via <see cref="WeaponOrder"/>: on
/// (re)build it numbers the cvar (<see cref="WeaponOrder.NumberWeaponOrder"/>), fixes+completes it
/// (<see cref="WeaponOrder.FixWeaponOrder"/> with <c>complete:true</c>), and — when the fix changed it — writes
/// the named result back (QC draw: <c>if (t != s) cvar_set(cl_weaponpriority, W_NameWeaponOrder(t))</c>). Each
/// row shows the weapon's icon + display name, with a <c>*</c> suffix for a mutator-blocked weapon
/// (<see cref="WeaponFlags.MutatorBlocked"/>). The Up/Down buttons swap the selected row with its neighbour
/// (<see cref="WeaponOrder.SwapInPriorityList"/>) and write the cvar in NAME form, exactly as
/// <c>WeaponsList_MoveUp_Click</c>/<c>MoveDown_Click</c> do.
/// </summary>
public partial class WeaponPriorityList : VBoxContainer
{
    private const string Cvar = "cl_weaponpriority";

    private static CvarService Cvars => MenuState.Cvars;

    private ItemList _list = null!;
    private List<int> _ids = new();    // the current id order (number form, what QC tokenizes for nItems)
    private bool _updating;

    public WeaponPriorityList()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 6);
    }

    public override void _Ready()
    {
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 220),
            AllowReselect = true,
        };
        AddChild(_list);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 10);
        buttons.AddChild(Ui.Button("Move up", MoveUp));
        buttons.AddChild(Ui.Button("Move down", MoveDown));
        AddChild(buttons);

        Rebuild();
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == Cvar && !_updating) Rebuild(); }

    /// <summary>QC draw: normalize the cvar (number → fix+complete → name back if changed), then list the ids.</summary>
    private void Rebuild()
    {
        if (_list is null) return;
        string s = WeaponOrder.NumberWeaponOrder(Cvars.GetString(Cvar));
        // menuRegistryView: run the fix-up as Base's MENU program would — its registry has no BallStealer, so
        // the completion can't append it (and a stray token from an old config is dropped, self-healing the
        // cvar). Without this, one visit to this dialog permanently archived a port-mutated cl_weaponpriority.
        string t = WeaponOrder.FixWeaponOrder(s, complete: true, menuRegistryView: true);
        if (t != s)
        {
            _updating = true;
            Cvars.Set(Cvar, WeaponOrder.NameWeaponOrder(t)); // QC writes the NAME form
            Cvars.MarkArchived(Cvar);
            _updating = false;
        }

        int prevSel = _list.IsAnythingSelected() ? _list.GetSelectedItems()[0] : -1;

        _ids = new List<int>();
        _list.Clear();
        foreach (string tok in t.Split(' '))
        {
            if (!int.TryParse(tok, out int id) || id < 0 || id >= Registry<Weapon>.Count) continue;
            Weapon w = Registry<Weapon>.ById(id);
            bool blocked = (w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0;
            string label = w.DisplayName.Length > 0 ? w.DisplayName : w.NetName;
            if (blocked) label += " *"; // QC: draw a "*" suffix for WEP_FLAG_MUTATORBLOCKED
            _list.AddItem(label, IconFor(w));
            _ids.Add(id);
        }

        if (prevSel >= 0 && prevSel < _list.ItemCount)
            _list.Select(prevSel);
    }

    private static Texture2D? IconFor(Weapon w)
        => MenuSkin.Image($"gfx/weapons/weapon{w.NetName}"); // QC weapon .display icon path; null → text only

    private void MoveUp()
    {
        if (!_list.IsAnythingSelected()) return;
        int sel = _list.GetSelectedItems()[0];
        if (sel <= 0) return;
        Swap(sel - 1, sel);
        _list.Select(sel - 1);
    }

    private void MoveDown()
    {
        if (!_list.IsAnythingSelected()) return;
        int sel = _list.GetSelectedItems()[0];
        if (sel >= _ids.Count - 1) return;
        Swap(sel, sel + 1);
        _list.Select(sel + 1);
    }

    /// <summary>Swap two list positions and write the cvar in NAME form (QC swapInPriorityList + cvar_set).</summary>
    private void Swap(int a, int b)
    {
        // QC swaps in the NUMBER-form list, then the next draw names it back; do both atomically here so the
        // stored cvar value stays NAME form (what cl_weaponpriority holds).
        string numbered = string.Join(' ', _ids);
        string swapped = WeaponOrder.SwapInPriorityList(numbered, a, b);
        _updating = true;
        Cvars.Set(Cvar, WeaponOrder.NameWeaponOrder(swapped));
        Cvars.MarkArchived(Cvar);
        _updating = false;
        Rebuild();
    }
}
