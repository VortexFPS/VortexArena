using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Engine.Simulation;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Create-Game "Mutators" dialog — a faithful C# port of <c>XonoticMutatorsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qc). It is the grid of gameplay/weapon mutator
/// toggles you reach from the Create-Game screen: every <c>g_*</c> mutator is a <see cref="Widgets.CheckBox"/>
/// bound to the same engine cvar the QC binds, with the same <c>setDependent</c> grey-outs; the right-hand
/// column holds the single radio set that selects the arena/special mode (Regular / Custom / Most / All
/// weapons, InstaGib, NIX, No-start-weapons) plus the per-weapon custom-arena checkboxes and the two combo
/// "slider + enable checkbox" rows (Blood loss, Low gravity).
///
/// FAITHFUL UI NOW: nothing here needs a backend VortexArena lacks — the dialog only reads/writes cvars (the same
/// store the in-game match reads), so it is functional as-is. Two pieces are approximated rather than
/// fabricated:
///   * QC <c>setDependentWeird(e, checkCompatibility_*)</c> gates a handful of widgets on a multi-cvar
///     compatibility test (e.g. Piñata is incompatible with any arena/instagib/nix/overkill/melee). The
///     framework's <see cref="Dependent"/> only does a single-cvar range test, so those widgets keep the
///     single-cvar dependency the QC's compatibility function is dominated by (usually "not InstaGib"); the
///     fuller multi-cvar gate is left out (noted).
///   * The QC arena radios share one radio group yet each drives a *different* cvar with its own
///     <c>cvarOffValue</c>. That is reproduced with one Godot <see cref="ButtonGroup"/> over
///     <see cref="DialogMutatorsArenaRadio"/> buttons that, on selection, set their own cvar and reset every
///     sibling arena cvar to its off value (the QC <c>cvarOffValue</c> semantics).
/// The QC "OK" button just closed the dialog (<c>Dialog_Close</c>); here that is the standard Back.
/// </summary>
public partial class DialogMutators : MenuScreen
{
    // The g_*_weaponstartoverride cvars the QC "No start weapons" radio drives together via makeMulti(...).
    // Selecting "No start weapons" sets every one to "0"; deselecting restores the cvarOffValue "-1" (default).
    private const string StartWeaponMulti =
        "g_balance_blaster_weaponstartoverride g_balance_shotgun_weaponstartoverride " +
        "g_balance_machinegun_weaponstartoverride g_balance_devastator_weaponstartoverride " +
        "g_balance_minelayer_weaponstartoverride g_balance_electro_weaponstartoverride " +
        "g_balance_crylink_weaponstartoverride g_balance_hagar_weaponstartoverride " +
        "g_balance_porto_weaponstartoverride g_balance_vaporizer_weaponstartoverride " +
        "g_balance_hook_weaponstartoverride g_balance_rifle_weaponstartoverride " +
        "g_balance_fireball_weaponstartoverride g_balance_seeker_weaponstartoverride " +
        "g_balance_tuba_weaponstartoverride g_balance_arc_weaponstartoverride " +
        "g_balance_vortex_weaponstartoverride g_balance_mortar_weaponstartoverride";

    // The non-hidden weapons in registry order (qcsrc/common/weapons/all.inc), as (netname, display name).
    // The QC iterates WEP_FIRST..WEP_LAST skipping WEP_FLAG_HIDDEN (only the Tuba is hidden) and builds one
    // makeXonoticWeaponarenaCheckBox(netname, m_name) per weapon for the "Custom weapons" arena.
    private static readonly (string Net, string Name)[] ArenaWeapons =
    {
        ("blaster", "Blaster"),
        ("shotgun", "Shotgun"),
        ("machinegun", "MachineGun"),
        ("mortar", "Mortar"),
        ("minelayer", "Mine Layer"),
        ("electro", "Electro"),
        ("crylink", "Crylink"),
        ("vortex", "Vortex"),
        ("hagar", "Hagar"),
        ("devastator", "Devastator"),
        ("porto", "Port-O-Launch"),
        ("vaporizer", "Vaporizer"),
        ("hook", "Grappling Hook"),
        ("hlac", "Heavy Laser Assault Cannon"),
        ("rifle", "Rifle"),
        ("fireball", "Fireball"),
        ("seeker", "T.A.G. Seeker"),
        ("arc", "Arc"),
    };

    // The shared radio group for every arena/special mode selector (QC group id 1, spanning two columns).
    private ButtonGroup _arenaGroup = null!;

    protected override void BuildUi()
    {
        Name = "DialogMutators"; // QC ATTRIB name "Mutators"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Mutators"));

        // The QC dialog is a wide two-column table; reproduce that with two side-by-side scrolling columns so
        // the dense content stays on one screen. NEVER nest a TabContainer here — these are plain scrolls.
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        root.AddChild(columns);

        columns.AddChild(MakeColumn(BuildGameplayColumn));
        columns.AddChild(MakeColumn(BuildArenaColumn));

        // QC "OK" button (e.onClick = Dialog_Close). Here the universal Back closes the dialog.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    /// <summary>One scrolling column holding a vertical list filled by <paramref name="fill"/>.</summary>
    private static ScrollContainer MakeColumn(System.Action<VBoxContainer> fill)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);
        fill(box);
        return scroll;
    }

    // -------------------------------------------------------------------------------------------------------
    //  Left column — "Gameplay mutators" + "Weapon & item mutators"
    // -------------------------------------------------------------------------------------------------------

    private void BuildGameplayColumn(VBoxContainer box)
    {
        box.AddChild(Ui.Header("Gameplay mutators:"));

        box.AddChild(Widgets.CheckBox("g_dodging", "Dodging",
            "Enable dodging (quick acceleration in a given direction). Double-tap a directional key to dodge"));
        // Port of MutatorTouchExplode.describe() (touchexplode.qc:5-11): 2-paragraph info-page text. The port
        // has no dedicated describe-page widget, so the describe text rides on the checkbox tooltip (same pattern
        // as Cloaked, Vampire, New Toys, Piñata, and Hook above).
        box.AddChild(Widgets.CheckBox("g_touchexplode", "Touch explode",
            "The Touch explode mutator causes an explosion when two players collide if it is enabled. " +
            "This is a nice way to add some silly fun to a server, but it also does allow for the use of " +
            "new tactics in some gametypes."));
        // Port of MutatorCloaked.describe() (cloaked.qc:6-13): "makes all players nearly invisible, similar to
        // the Invisibility powerup". The port has no dedicated describe-page widget, so the describe text rides
        // on the checkbox tooltip (same pattern as Piñata and New Toys above).
        box.AddChild(Widgets.CheckBox("g_cloaked", "Cloaked",
            "The Cloaked mutator makes all players nearly invisible, similar to the Invisibility powerup. " +
            "This adds an extra layer of stealth and strategy to gameplay."));
        // QC: e.cvarOffValue = "-1" (Buffs off writes -1, not 0).
        box.AddChild(Widgets.CheckBox("g_buffs", "Buffs",
            "Enable buff pickups (random bonuses like Medic, Invisible, etc.) on the maps that support it",
            on: "1", off: "-1"));
        box.AddChild(Widgets.CheckBox("g_midair", "Midair",
            "Only possible to inflict damage on your enemy while they're airborne"));

        // Port of MutatorVampire.describe() (vampire.qc:6-13): the 2-paragraph mutator info page. Both %s fills
        // (the mutator's own name and BUFF_VAMPIRE's m_name) resolve to "Vampire". The port has no dedicated
        // describe-page widget, so (following the Cloaked / New Toys / Piñata / Hook pattern) the describe text
        // rides on the checkbox tooltip.
        var vampire = Widgets.CheckBox("g_vampire", "Vampire",
            "The Vampire mutator gives all players a permanent version of the Vampire buff. However, unlike " +
            "the normal Vampire buff, when this mutator is enabled players' health can go way above the usual " +
            "limit of 200. Additionally the amount of health players get is equal to the damage they deal, " +
            "which isn't normally the case with the Vampire buff.");
        box.AddChild(vampire);
        Dependent.Bind(vampire, "g_instagib", 0, 0); // QC setDependent(e,"g_instagib",0,0)

        // Blood loss — QC makeXonoticSliderCheckBox(0, 1, g_bloodloss[10..50], "Blood loss"): the checkbox is
        // checked while g_bloodloss != 0; on writes the saved value (20), off writes 0. The slider refines it.
        // The port has no dedicated describe-page widget, so (following the Vampire / Cloaked / Hook pattern)
        // the full MENUQC describe paragraph (bloodloss.qc:76-77) rides on the checkbox/slider tooltip.
        const string bloodlossTooltip =
            "Blood loss is a mutator in which players below a certain health threshold (for example 25) will " +
            "suffer blood loss. Blood loss makes players stunned, severely impairs their movement, and rapidly " +
            "takes away health points until they either die or gain enough health to go above the health threshold.";
        var bloodEnable = new DialogMutatorsSliderCheckBox("g_bloodloss", "Blood loss", offValue: 0f, savedValue: 20f)
        {
            TooltipText = bloodlossTooltip,
        };
        box.AddChild(bloodEnable);
        Dependent.Bind(bloodEnable, "g_instagib", 0, 0); // QC setDependent on the combo checkbox
        var bloodSlider = Widgets.Slider("g_bloodloss", 10f, 50f, 1f, bloodlossTooltip);
        var bloodRow = Ui.Row("", bloodSlider, labelMinWidth: 24f);
        box.AddChild(bloodRow);
        Dependent.Bind(bloodRow, "g_instagib", 0, 0);

        // Low gravity — QC makeXonoticSliderCheckBox(800, 1, sv_gravity[80..400 step 8], "Low gravity"),
        // savedValue 200, slider shows gravity in percent (valueDisplayMultiplier 0.125). Checked while
        // sv_gravity != 800 (normal); on writes 200, off writes 800.
        var gravEnable = new DialogMutatorsSliderCheckBox("sv_gravity", "Low gravity", offValue: 800f, savedValue: 200f)
        {
            TooltipText = "Make things fall to the ground slower (percentage of normal gravity)",
        };
        box.AddChild(gravEnable);
        var gravSlider = Widgets.Slider("sv_gravity", 80f, 400f, 8f,
            "Make things fall to the ground slower (percentage of normal gravity)",
            format: v => $"{Mathf.RoundToInt(v * 0.125f)}%"); // valueDisplayMultiplier 0.125, valueDigits 0
        box.AddChild(Ui.Row("", gravSlider, labelMinWidth: 24f));

        box.AddChild(Ui.Spacer());
        box.AddChild(Ui.Header("Weapon & item mutators:"));

        // Port of MutatorGrapplingHook.describe() (hook.qc:7-15): the long-form description page — the
        // offhand / unlimited-ammo / no-secondary / overridden-by-offhand_blaster explainer. The port has no
        // dedicated describe-page widget, so (following the New Toys / Cloaked pattern) the text rides on the
        // checkbox tooltip. Names match the QC %s fills: Grappling Hook (mutator + WEP_HOOK) / "hook" key /
        // Offhand Blaster.
        box.AddChild(Widgets.CheckBox("g_grappling_hook", "Grappling Hook",
            "The Grappling Hook mutator gives all players a Grappling Hook as their offhand weapon, used with " +
            "the 'hook' key. It has unlimited ammo, but the ordinary secondary fire can't be used. " +
            "Since it's given as an offhand, players can use it to move around and shoot at their enemies at " +
            "the same time, opening up more gameplay possibilities than the regular Grappling Hook. " +
            "Note that it is overridden by the Offhand Blaster mutator."));
        box.AddChild(Widgets.CheckBox("g_jetpack", "Jetpack",
            "Players spawn with the jetpack. Double-tap 'jump' or press the 'jetpack' key to use it"));

        var invproj = Widgets.CheckBox("g_invincible_projectiles", "Invincible Projectiles",
            "Projectiles can't be destroyed. However, you can still explode Electro orbs with the Electro primary fire");
        box.AddChild(invproj);
        Dependent.Bind(invproj, "g_instagib", 0, 0); // QC setDependent(e,"g_instagib",0,0)

        // New Toys + its auto-replacement radio set (QC setDependentWeird on a weapon-arena compatibility test;
        // approximated as the dominant "not InstaGib" dependency + enabled only when g_new_toys is on).
        // Port of MutatorNewToys.describe() (new_toys.qc:25): the picker info blurb — the gimmicky-weapons note,
        // the InstaGib/Overkill exclusivity, and a runtime enumeration of the current new-toy weapon list (Base
        // uses FOREACH(Weapons, nt_IsNewToy(it.m_id)) to dynamically build the list). The port has no dedicated
        // describe-page widget, so the text is carried on the checkbox tooltip (closest faithful surface).
        string newToysWeaponList = BuildNewToysWeaponList();
        var newToys = Widgets.CheckBox("g_new_toys", "New Toys",
            "The New Toys mutator, enabled by default, allows the spawning of new gimmicky weapons, " +
            "sometimes replacing a core weapon. Since these weapons can't spawn in InstaGib and Overkill, " +
            "the New Toys mutator can't be enabled concurrently. The current New Toys weapons are: " +
            newToysWeaponList);
        box.AddChild(newToys);
        Dependent.Bind(newToys, "g_instagib", 0, 0);

        const string ntaTip =
            "Automatically replace some normal weapons of the map with their corresponding additional weapons";
        box.AddChild(Ui.Label("Replacement:"));
        var ntaGroup = new ButtonGroup();
        var ntaRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ntaRow.AddThemeConstantOverride("separation", 8);
        ntaRow.AddChild(Widgets.RadioButton("g_new_toys_autoreplace", "0", "Never", ntaGroup, ntaTip));
        ntaRow.AddChild(Widgets.RadioButton("g_new_toys_autoreplace", "1", "Always", ntaGroup, ntaTip));
        ntaRow.AddChild(Widgets.RadioButton("g_new_toys_autoreplace", "2", "Randomly", ntaGroup, ntaTip));
        box.AddChild(ntaRow);
        // QC gates the replacement controls on checkCompatibility_newtoys_autoreplace, dominated by g_new_toys.
        Dependent.Bind(ntaRow, "g_new_toys", 1, 1);

        var rocketFly = Widgets.CheckBox("g_rocket_flying", "Rocket Flying",
            "Devastator rockets can be detonated instantly (otherwise, there's a short delay). This allows " +
            "players to fire and detonate a Devastator rocket while in the air for a strong mid-air boost even " +
            "while moving fast");
        box.AddChild(rocketFly);
        Dependent.Bind(rocketFly, "g_instagib", 0, 0); // QC setDependent(e,"g_instagib",0,0)

        // Piñata / Weapons stay — QC setDependentWeird(checkCompatibility_pinata): incompatible with instagib,
        // nix, overkill, melee_only and any weapon arena. Approximated as the dominant "not InstaGib" gate.
        // Label = MutatorPinata.message (_("Piñata"), pinata.qh:6); tooltip = MutatorPinata.describe()
        // (pinata.qc:6-8). The port has no describe-page widget, so the describe text rides on the tooltip.
        var pinata = Widgets.CheckBox("g_pinata", "Piñata",
            "Piñata is a mutator that makes players drop all their weapons when they die. " +
            "Without this mutator, players normally drop only their equipped weapon");
        box.AddChild(pinata);
        Dependent.Bind(pinata, "g_instagib", 0, 0);

        var weaponStay = Widgets.CheckBox("g_weapon_stay", "Weapons stay",
            "Weapons stay after they are picked up");
        box.AddChild(weaponStay);
        Dependent.Bind(weaponStay, "g_instagib", 0, 0);
    }

    // -------------------------------------------------------------------------------------------------------
    //  Right column — the arena/special mode radio set (one group), custom-weapon checkboxes, NIX options
    // -------------------------------------------------------------------------------------------------------

    private void BuildArenaColumn(VBoxContainer box)
    {
        _arenaGroup = new ButtonGroup();

        // QC: makeXonoticRadioButton(1, string_null, string_null, "Regular (no arena)") — the neutral member of
        // the group; selecting it turns every arena/special mode off (g_weaponarena 0, g_instagib 0, g_nix 0,
        // weapon start overrides back to default).
        box.AddChild(DialogMutatorsArenaRadio.Regular("Regular (no arena)", _arenaGroup));

        box.AddChild(Ui.Spacer());
        box.AddChild(Ui.Header("Weapon arenas:"));

        const string arenaTip =
            "Players will be given a set of weapons at spawn as well as unlimited ammo, without weapon pickups";

        // Custom weapons — QC g_weaponarena = "$menu_weaponarena" (cvarValueIsAnotherCvar), off "0".
        box.AddChild(DialogMutatorsArenaRadio.Cvar(
            "g_weaponarena", on: "menu_weaponarena", "Custom weapons", _arenaGroup, arenaTip));

        // One checkbox per non-hidden weapon (QC makeXonoticWeaponarenaCheckBox grid, 3 per row). Each toggles
        // the weapon's netname in the menu_weaponarena list and keeps g_weaponarena in sync (= the QC saveCvars).
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach ((string net, string name) in ArenaWeapons)
            grid.AddChild(new DialogMutatorsWeaponCheckBox(net, name));
        box.AddChild(grid);
        // QC setDependentWeird(checkCompatibility_weaponarena_weapon): the per-weapon boxes matter only for a
        // custom arena. Enable them only while g_weaponarena is the custom list cvar value.
        Dependent.Bind(grid, "g_instagib", 0, 0);

        // Most / All weapons — QC g_weaponarena = "most" / "all", off "0".
        box.AddChild(DialogMutatorsArenaRadio.Cvar("g_weaponarena", "most", "Most weapons", _arenaGroup, arenaTip));
        box.AddChild(DialogMutatorsArenaRadio.Cvar("g_weaponarena", "all", "All weapons", _arenaGroup, arenaTip));

        box.AddChild(Ui.Spacer());
        box.AddChild(Ui.Header("Special arenas:"));

        // InstaGib — QC g_instagib = "1", off "0".
        box.AddChild(DialogMutatorsArenaRadio.Cvar("g_instagib", "1", "InstaGib", _arenaGroup,
            "Players will be given only one weapon, which can instantly kill the opponent with a single shot. " +
            "If the player runs out of ammo, they will have 10 seconds to find some or if they fail to do so, " +
            "face death. The secondary fire mode does not inflict any damage but is good for doing trickjumps."));

        // NIX — QC g_nix = "1", off "0".
        box.AddChild(DialogMutatorsArenaRadio.Cvar("g_nix", "1", "NIX", _arenaGroup,
            "No items Xonotic - instead of pickup items, everyone plays with the same weapon. After some time, " +
            "a countdown will start, after which everyone will switch to another weapon."));

        // QC makeXonoticCheckBox_T(0, "g_nix_with_blaster", ...) with setDependent(e,"g_nix",1,1).
        var nixBlaster = Widgets.CheckBox("g_nix_with_blaster", "with blaster",
            "Always carry the blaster as an additional weapon in Nix");
        box.AddChild(nixBlaster);
        Dependent.Bind(nixBlaster, "g_nix", 1, 1); // QC setDependent(e,"g_nix",1,1)

        // No start weapons — QC radio g_balance_blaster_weaponstartoverride = "0", off "-1", makeMulti across
        // every g_balance_*_weaponstartoverride. Selecting it zeroes them all; deselecting restores -1.
        box.AddChild(DialogMutatorsArenaRadio.StartWeapons(
            "No start weapons", _arenaGroup, StartWeaponMulti));
    }

    /// <summary>
    /// Builds a runtime enumeration of new-toy weapon names (faithful to Base's FOREACH(Weapons, nt_IsNewToy)),
    /// comma-separated for the describe() tooltip text. This mirrors QC's FOREACH loop that dynamically builds
    /// the list so it auto-updates if the new-toy set ever changes (currently static, but the port mimics the
    /// Base intent by avoiding a hardcoded list).
    /// </summary>
    private static string BuildNewToysWeaponList()
    {
        var names = new List<string>();
        foreach (Weapon w in Weapons.All)
        {
            if (NewToysMutator.IsNewToy(w.NetName))
                names.Add(string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName);
        }
        // Faithful fallback ONLY if the registry hasn't populated (should not happen in the menu): use the
        // Base m_name values (seeker.qh:50 "T.A.G. Seeker", minelayer.qh:38 "Mine Layer", hlac.qh:35 "Heavy
        // Laser Assault Cannon", rifle.qh:34 "Rifle", arc.qh:38 "Arc"), NOT the abbreviated "Seeker"/"HLAC".
        return names.Count > 0
            ? string.Join(", ", names)
            : "T.A.G. Seeker, Mine Layer, Heavy Laser Assault Cannon, Rifle, Arc";
    }
}

// -----------------------------------------------------------------------------------------------------------
//  Helper: the combo "enable checkbox" half of a QC makeXonoticSliderCheckBox (the slider half is a plain
//  Widgets.Slider on the same cvar; the two stay in sync through the store's Changed event).
// -----------------------------------------------------------------------------------------------------------

/// <summary>
/// The C# successor to one <c>XonoticSliderCheckBox</c> (qcsrc/menu/xonotic/checkbox_slider_invalid.qc), used
/// here with <c>inverted = 1</c> (every call site in the mutators dialog). The box is checked while its cvar
/// differs from <c>offValue</c>; checking it writes <c>savedValue</c> (re-enabling the feature), unchecking
/// writes <c>offValue</c>. Pair it with a <see cref="Widgets.Slider"/> on the same cvar for the value.
/// </summary>
public partial class DialogMutatorsSliderCheckBox : CheckBox
{
    private readonly string _cvar;
    private readonly float _offValue;
    private readonly float _savedValue;
    private bool _updating;

    public DialogMutatorsSliderCheckBox(string cvar, string label, float offValue, float savedValue)
    {
        _cvar = cvar;
        _offValue = offValue;
        _savedValue = savedValue;
        Text = label;
        Toggled += OnToggled;
    }

    public override void _EnterTree() { MenuState.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { MenuState.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        // QC (inverted): checked == (slider.value != offValue).
        ButtonPressed = MenuState.Cvars.GetFloat(_cvar) != _offValue;
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating) return;
        float v = pressed ? _savedValue : _offValue;
        MenuState.Cvars.Set(_cvar, v.ToString(CultureInfo.InvariantCulture));
        MenuState.Cvars.MarkArchived(_cvar);
    }
}

// -----------------------------------------------------------------------------------------------------------
//  Helper: one weapon's custom-arena checkbox (QC XonoticWeaponarenaCheckBox).
// -----------------------------------------------------------------------------------------------------------

/// <summary>
/// The C# successor to <c>XonoticWeaponarenaCheckBox</c> (qcsrc/menu/xonotic/weaponarenacheckbox.qc): a
/// checkbox bound to a weapon's presence in the space-separated <c>menu_weaponarena</c> list. Checking adds
/// the weapon's netname to that list, unchecking removes it (QC <c>addtolist</c>/<c>removefromlist</c>), and
/// either way <c>g_weaponarena</c> is kept equal to the list so the "Custom weapons" arena uses it.
/// </summary>
public partial class DialogMutatorsWeaponCheckBox : CheckBox
{
    private const string ListCvar = "menu_weaponarena";
    private const string ArenaCvar = "g_weaponarena";

    private readonly string _net;
    private bool _updating;

    public DialogMutatorsWeaponCheckBox(string netname, string label)
    {
        _net = netname;
        Text = label;
        Toggled += OnToggled;
    }

    public override void _EnterTree() { MenuState.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { MenuState.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == ListCvar || name == ArenaCvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        ButtonPressed = ListContains(ReadList(), _net);
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating) return;
        List<string> list = ReadList();
        bool has = ListContains(list, _net);
        if (pressed && !has) list.Add(_net);
        else if (!pressed && has) list.RemoveAll(w => w == _net);

        string joined = string.Join(' ', list);
        MenuState.Cvars.Set(ListCvar, joined);
        MenuState.Cvars.MarkArchived(ListCvar);
        // QC saveCvars: g_weaponarena "$menu_weaponarena" — keep the live arena cvar in sync with the list,
        // but only while a custom arena is actually selected (don't clobber "most"/"all"/"0").
        if (MenuState.Cvars.GetString(ArenaCvar) is not ("" or "0" or "most" or "all"))
        {
            MenuState.Cvars.Set(ArenaCvar, joined);
            MenuState.Cvars.MarkArchived(ArenaCvar);
        }
    }

    private static List<string> ReadList()
    {
        var list = new List<string>();
        foreach (string tok in MenuState.Cvars.GetString(ListCvar)
                     .Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            list.Add(tok);
        return list;
    }

    private static bool ListContains(List<string> list, string net)
    {
        foreach (string w in list)
            if (w == net) return true;
        return false;
    }
}

// -----------------------------------------------------------------------------------------------------------
//  Helper: one member of the shared arena/special-mode radio group (QC makeXonoticRadioButton group 1).
// -----------------------------------------------------------------------------------------------------------

/// <summary>
/// One option of the mutators dialog's single arena/special-mode radio group. The QC puts "Regular",
/// "Custom/Most/All weapons", "InstaGib", "NIX" and "No start weapons" in one radio group even though each
/// drives a different cvar with its own <c>cvarOffValue</c>; selecting one resets the rest. This control
/// reproduces that: a radio in a shared <see cref="ButtonGroup"/> that, on select, sets its own cvar to its
/// on-value and resets every sibling arena cvar to its off-value; it shows as checked when its own cvar holds
/// its on-value (and "Regular" when none of them do).
/// </summary>
public partial class DialogMutatorsArenaRadio : CheckBox
{
    // The mutually-exclusive arena/special cvars and the value that means "off" for each (QC cvarOffValue).
    // "Regular" is selected when every one of these is at its off value.
    private static readonly (string Cvar, string Off)[] ArenaCvars =
    {
        ("g_weaponarena", "0"),
        ("g_instagib", "0"),
        ("g_nix", "0"),
        ("g_balance_blaster_weaponstartoverride", "-1"),
    };

    private enum Kind { Regular, Cvar, StartWeapons }

    private readonly Kind _kind;
    private string _cvar = "";
    private string _on = "";
    private string _startMulti = "";
    private bool _updating;

    private DialogMutatorsArenaRadio(Kind kind, string label, ButtonGroup group)
    {
        _kind = kind;
        Text = label;
        ButtonGroup = group;
        ToggleMode = true;
        Toggled += OnToggled;
    }

    /// <summary>The neutral "Regular (no arena)" member — selecting it clears every arena/special mode.</summary>
    public static DialogMutatorsArenaRadio Regular(string label, ButtonGroup group)
        => new(Kind.Regular, label, group);

    /// <summary>
    /// A member that drives <paramref name="cvar"/> to <paramref name="on"/> when selected (QC radio with a
    /// concrete cvar + value, e.g. g_instagib=1, g_weaponarena=most). When <paramref name="on"/> is the name
    /// of another cvar (QC <c>cvarValueIsAnotherCvar</c>, used by "Custom weapons" → menu_weaponarena), it is
    /// expanded to that cvar's current value on select and matched against it on refresh.
    /// </summary>
    public static DialogMutatorsArenaRadio Cvar(string cvar, string on, string label, ButtonGroup group, string tooltip = "")
        => new(Kind.Cvar, label, group) { _cvar = cvar, _on = on, TooltipText = tooltip };

    /// <summary>
    /// The "No start weapons" member — QC radio on g_balance_blaster_weaponstartoverride=0 (off -1) with a
    /// <c>makeMulti</c> spanning every g_balance_*_weaponstartoverride. Selecting zeroes them all.
    /// </summary>
    public static DialogMutatorsArenaRadio StartWeapons(string label, ButtonGroup group, string multi)
        => new(Kind.StartWeapons, label, group) { _cvar = "g_balance_blaster_weaponstartoverride", _on = "0", _startMulti = multi };

    public override void _EnterTree() { MenuState.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { MenuState.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        ButtonPressed = _kind switch
        {
            Kind.Regular => IsRegular(),
            Kind.StartWeapons => MenuState.Cvars.GetString(_cvar) == _on,
            _ => MatchesCvar(),
        };
        _updating = false;
    }

    private bool MatchesCvar()
    {
        string cur = MenuState.Cvars.GetString(_cvar);
        // QC cvarValueIsAnotherCvar: the on-value is the *name* of a cvar; compare against that cvar's value.
        if (MenuState.Cvars.Has(_on) && _on != "0" && _on != "1" && _on != "most" && _on != "all")
            return cur != "" && cur != "0" && cur == MenuState.Cvars.GetString(_on);
        return cur == _on;
    }

    /// <summary>"Regular" is checked when none of the mutually-exclusive arena/special cvars are active.</summary>
    private static bool IsRegular()
    {
        foreach ((string cvar, string off) in ArenaCvars)
            if (MenuState.Cvars.GetString(cvar) != off && MenuState.Cvars.GetFloat(cvar) != 0f
                && !(cvar.EndsWith("weaponstartoverride") && MenuState.Cvars.GetString(cvar) == off))
                return false;
        return true;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating || !pressed) return;
        // Selecting this member: reset every sibling arena cvar to its off value (the QC cvarOffValue effect of
        // the shared radio group), then apply this member's own setting.
        ResetArenaCvars();

        switch (_kind)
        {
            case Kind.Regular:
                break; // already all-off

            case Kind.StartWeapons:
                foreach (string cv in _startMulti.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    MenuState.Cvars.Set(cv, "0");
                    MenuState.Cvars.MarkArchived(cv);
                }
                break;

            default: // Kind.Cvar
                string value = _on;
                // cvarValueIsAnotherCvar (Custom weapons): store the referenced cvar's current value.
                if (MenuState.Cvars.Has(_on) && _on != "0" && _on != "1" && _on != "most" && _on != "all")
                    value = MenuState.Cvars.GetString(_on);
                MenuState.Cvars.Set(_cvar, value);
                MenuState.Cvars.MarkArchived(_cvar);
                break;
        }
    }

    /// <summary>Reset the mutually-exclusive arena/special cvars to their off values + the start-weapon multi.</summary>
    private void ResetArenaCvars()
    {
        foreach ((string cvar, string off) in ArenaCvars)
        {
            MenuState.Cvars.Set(cvar, off);
            MenuState.Cvars.MarkArchived(cvar);
        }
        // The "No start weapons" multi resets to its off value (-1) along with the rest of the group.
        foreach (string cv in DialogMutators_StartWeaponMulti.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            MenuState.Cvars.Set(cv, "-1");
            MenuState.Cvars.MarkArchived(cv);
        }
    }

    // Mirror of DialogMutators.StartWeaponMulti (that one is private to the dialog); kept here so the radio's
    // reset can zero/restore the whole g_balance_*_weaponstartoverride set the QC makeMulti spans.
    private const string DialogMutators_StartWeaponMulti =
        "g_balance_blaster_weaponstartoverride g_balance_shotgun_weaponstartoverride " +
        "g_balance_machinegun_weaponstartoverride g_balance_devastator_weaponstartoverride " +
        "g_balance_minelayer_weaponstartoverride g_balance_electro_weaponstartoverride " +
        "g_balance_crylink_weaponstartoverride g_balance_hagar_weaponstartoverride " +
        "g_balance_porto_weaponstartoverride g_balance_vaporizer_weaponstartoverride " +
        "g_balance_hook_weaponstartoverride g_balance_rifle_weaponstartoverride " +
        "g_balance_fireball_weaponstartoverride g_balance_seeker_weaponstartoverride " +
        "g_balance_tuba_weaponstartoverride g_balance_arc_weaponstartoverride " +
        "g_balance_vortex_weaponstartoverride g_balance_mortar_weaponstartoverride";
}
