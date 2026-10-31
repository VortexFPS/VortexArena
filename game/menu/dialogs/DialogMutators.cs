using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Mutators" — <c>XonoticMutatorsDialog_fill</c> (qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qc) on its
/// 21 x 6 grid, 0.9 of the menu wide. Left half: "Gameplay mutators:" (Dodging … Vampire, the Blood loss and Low
/// gravity slider checkboxes with their sliders indented beneath) and "Weapon &amp; item mutators:" (Grappling Hook …
/// Weapons stay, New Toys with its Replacement radios). Right half from column 2: radio group 1 — "Regular (no
/// arena)", "Weapon arenas:" (Custom weapons and one checkbox per non-hidden weapon, three to a row; Most; All) and
/// "Special arenas:" (InstaGib, NIX with "with blaster", No start weapons). "OK" on the last row.
///
/// Every control binds the cvar the QC binds, with the QC's <c>setDependent</c> and <c>setDependentWeird</c>
/// (checkCompatibility_*) gates. Group 1's radios each drive a different cvar with its own <c>cvarOffValue</c>,
/// which <see cref="DialogMutatorsArenaRadio"/> keeps; the weapon checkboxes are <see cref="DialogMutatorsWeaponCheckBox"/>
/// (menu_weaponarena, mirrored into g_weaponarena). Tooltips carry each mutator's describe() text where the port
/// had it, else the QC tooltip.
/// </summary>
public partial class DialogMutators : MenuScreen, IQcDialog
{
    // dialog_multiplayer_create_mutators.qh
    public string QcTitle => Localization.Tr("Mutators");
    public float QcIntendedWidth => 0.9f;
    public float QcRows => 21;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_MUTATORS", 1f, 1f, 1f);

    // The g_*_weaponstartoverride cvars the QC "No start weapons" radio drives together via makeMulti(...).
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

    // WEP_FIRST..WEP_LAST without WEP_FLAG_HIDDEN, in registry order (qcsrc/common/weapons/all.inc, then the
    // registered extras: of those only the Nexball Ball Stealer is not hidden), as (netname, m_name).
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
        ("ballstealer", "Ball Stealer"),
    };


    protected override void BuildUi()
    {
        Name = "DialogMutators"; // QC XonoticMutatorsDialog

        var me = new QcGrid(QcRows, 6);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        const string bloodlossTooltip =
            "Blood loss is a mutator in which players below a certain health threshold (for example 25) will " +
            "suffer blood loss. Blood loss makes players stunned, severely impairs their movement, and rapidly " +
            "takes away health points until they either die or gain enough health to go above the health threshold.";
        const string gravityTooltip = "Make things fall to the ground slower (percentage of normal gravity)";

        me.TR();
            me.TD(1, 2, Qc.Label("Gameplay mutators:"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_dodging", "Dodging",
                "Enable dodging (quick acceleration in a given direction). Double-tap a directional key to dodge"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_touchexplode", "Touch explode",
                "The Touch explode mutator causes an explosion when two players collide if it is enabled. " +
                "This is a nice way to add some silly fun to a server, but it also does allow for the use of " +
                "new tactics in some gametypes."));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_cloaked", "Cloaked",
                "The Cloaked mutator makes all players nearly invisible, similar to the Invisibility powerup. " +
                "This adds an extra layer of stealth and strategy to gameplay."));
        me.TR();
            me.TDempty(0.2f);
            // e.cvarOffValue = "-1": unchecked writes -1, not 0.
            me.TD(1, 1.8f, Qc.CheckEx(1, -1, "g_buffs", "Buffs",
                "Enable buff pickups (random bonuses like Medic, Invisible, etc.) on the maps that support it"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_midair", "Midair",
                "Only possible to inflict damage on your enemy while they're airborne"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_vampire", "Vampire",
                "The Vampire mutator gives all players a permanent version of the Vampire buff. However, unlike " +
                "the normal Vampire buff, when this mutator is enabled players' health can go way above the usual " +
                "limit of 200. Additionally the amount of health players get is equal to the damage they deal, " +
                "which isn't normally the case with the Vampire buff."))
                .Dep("g_instagib", 0, 0);
        me.TR();
            me.TDempty(0.2f);
            CvarSlider s = Qc.Slider(10, 50, 1, "g_bloodloss", bloodlossTooltip);
            me.TD(1, 1.8f, new QcSliderCheckBox(0, true, s, "Blood loss"))
                .Dep("g_instagib", 0, 0);
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 1.6f, s);
        me.TR();
            me.TDempty(0.2f);
            // valueDigits 0, valueDisplayMultiplier 0.125: gravity shown in percent (no format string).
            s = QcLook.Slider(Widgets.Slider("sv_gravity", 80, 400, 8, gravityTooltip, v => QcFormat.Decimals(v * 0.125f, 0)));
            me.TD(1, 1.8f, new QcSliderCheckBox(800, true, s, "Low gravity", savedDefault: 200)); // good on silvercity
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 1.6f, s);
        me.TR();
            me.TD(1, 2, Qc.Label("Weapon & item mutators:"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_grappling_hook", "Grappling Hook",
                "The Grappling Hook mutator gives all players a Grappling Hook as their offhand weapon, used with " +
                "the 'hook' key. It has unlimited ammo, but the ordinary secondary fire can't be used. " +
                "Since it's given as an offhand, players can use it to move around and shoot at their enemies at " +
                "the same time, opening up more gameplay possibilities than the regular Grappling Hook. " +
                "Note that it is overridden by the Offhand Blaster mutator."));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_jetpack", "Jetpack",
                "Players spawn with the jetpack. Double-tap 'jump' or press the 'jetpack' key to use it"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_invincible_projectiles", "Invincible Projectiles",
                "Projectiles can't be destroyed. However, you can still explode Electro orbs with the Electro primary fire"))
                .Dep("g_instagib", 0, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f / 2, Qc.Check(0, "g_new_toys", "New Toys",
                "The New Toys mutator, enabled by default, allows the spawning of new gimmicky weapons, " +
                "sometimes replacing a core weapon. Since these weapons can't spawn in InstaGib and Overkill, " +
                "the New Toys mutator can't be enabled concurrently. The current New Toys weapons are: " +
                BuildNewToysWeaponList()))
                .DepFunc(CheckCompatibilityNewToys);
            me.TD(1, 1.8f / 2, Qc.Label("Replacement:"))
                .DepFunc(CheckCompatibilityNewToysAutoreplace);
        const string ntaTooltip = "Automatically replace some normal weapons of the map with their corresponding additional weapons";
        var nta = new QcRadioGroup();
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 1.6f / 3, Qc.Radio(nta, "g_new_toys_autoreplace", "0", "Never", ntaTooltip))
                .DepFunc(CheckCompatibilityNewToysAutoreplace);
            me.TD(1, 1.6f / 3, Qc.Radio(nta, "g_new_toys_autoreplace", "1", "Always", ntaTooltip))
                .DepFunc(CheckCompatibilityNewToysAutoreplace);
            me.TD(1, 1.6f / 3, Qc.Radio(nta, "g_new_toys_autoreplace", "2", "Randomly", ntaTooltip))
                .DepFunc(CheckCompatibilityNewToysAutoreplace);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_rocket_flying", "Rocket Flying",
                "Devastator rockets can be detonated instantly (otherwise, there's a short delay). This allows " +
                "players to fire and detonate a Devastator rocket while in the air for a strong mid-air boost even " +
                "while moving fast"))
                .Dep("g_instagib", 0, 0);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_pinata", "Piñata",
                "Piñata is a mutator that makes players drop all their weapons when they die. " +
                "Without this mutator, players normally drop only their equipped weapon"))
                .DepFunc(CheckCompatibilityPinata);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Qc.Check(0, "g_weapon_stay", "Weapons stay", "Weapons stay after they are picked up"))
                .DepFunc(CheckCompatibilityPinata); // checkCompatibility_weaponstay
        me.TR();

        const string arenaTooltip =
            "Players will be given a set of weapons at spawn as well as unlimited ammo, without weapon pickups";
        var group = new ButtonGroup();
        me.GotoRC(0, 2); me.SetFirstColumn(me.CurrentColumn);
            me.TD(1, 2, Radio(DialogMutatorsArenaRadio.Regular("Regular (no arena)", group)));
        me.TR();
            me.TD(1, 2, Qc.Label("Weapon arenas:"));
        me.TR();
            me.TDempty(0.2f);
            // cvarValueIsAnotherCvar: g_weaponarena = "$menu_weaponarena"; cvarOffValue "0".
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.Cvar("g_weaponarena", "menu_weaponarena", "Custom weapons", group, arenaTooltip)));
            me.TDempty(0.1f); // fix initial position
        for (int j = 0; j < ArenaWeapons.Length; ++j)
        {
            if (j % 3 == 0)
            {
                me.TR();
                me.TDempty(0.4f);
            }
            (string net, string name) = ArenaWeapons[j];
            me.TD(1, 1.2f, QcLook.CheckBox(new DialogMutatorsWeaponCheckBox(net, Localization.Tr(name))))
                .DepFunc(CheckCompatibilityWeaponArenaWeapon);
        }
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.Cvar("g_weaponarena", "most", "Most weapons", group, arenaTooltip)));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.Cvar("g_weaponarena", "all", "All weapons", group, arenaTooltip)));
        me.TR();
            me.TD(1, 2, Qc.Label("Special arenas:"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.Cvar("g_instagib", "1", "InstaGib", group,
                "Players will be given only one weapon, which can instantly kill the opponent with a single shot. " +
                "If the player runs out of ammo, they will have 10 seconds to find some or if they fail to do so, " +
                "face death. The secondary fire mode does not inflict any damage but is good for doing trickjumps.")));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.Cvar("g_nix", "1", "NIX", group,
                "No items Xonotic - instead of pickup items, everyone plays with the same weapon. After some time, " +
                "a countdown will start, after which everyone will switch to another weapon.")));
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 1.6f, Qc.Check(0, "g_nix_with_blaster", "with blaster",
                "Always carry the blaster as an additional weapon in Nix"))
                .Dep("g_nix", 1, 1);
        me.TR();
            me.TDempty(0.2f);
            // cvarOffValue "-1", makeMulti over every g_balance_*_weaponstartoverride.
            me.TD(1, 1.8f, Radio(DialogMutatorsArenaRadio.StartWeapons("No start weapons", group, StartWeaponMulti)));

        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns, Qc.Button("OK", GoBack));
    }

    private static DialogMutatorsArenaRadio Radio(DialogMutatorsArenaRadio r) => QcLook.CheckBox(r, "radiobutton");

    // checkCompatibility_* (dialog_multiplayer_create_mutators.qc).
    private static float Cvar(string name) => MenuState.Cvars.GetFloat(name);
    private static string CvarString(string name) => MenuState.Cvars.GetString(name);

    private static bool CheckCompatibilityPinata()
        => Cvar("g_instagib") == 0 && Cvar("g_nix") == 0 && Cvar("g_overkill") == 0 && Cvar("g_melee_only") == 0
           && CvarString("g_weaponarena") == "0";

    private static bool CheckCompatibilityNewToys()
    {
        if (Cvar("g_instagib") != 0) return false;
        string arena = CvarString("g_weaponarena");
        return arena is "most" or "all" or "1" or "0";
    }

    private static bool CheckCompatibilityNewToysAutoreplace()
        => Cvar("g_new_toys") != 0 && CheckCompatibilityNewToys();

    private static bool CheckCompatibilityWeaponArenaWeapon()
    {
        if (Cvar("g_instagib") != 0) return false;
        string arena = CvarString("g_weaponarena");
        if (arena is "most" or "all" or "1" or "0") return false;
        return CvarString("g_balance_blaster_weaponstartoverride") != "0";
    }

    /// <summary>The New Toys weapons by name (FOREACH(Weapons, nt_IsNewToy)), for the describe() tooltip.</summary>
    private static string BuildNewToysWeaponList()
    {
        var names = new List<string>();
        foreach (Weapon w in Weapons.All)
        {
            if (NewToysMutator.IsNewToy(w.NetName))
                names.Add(string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName);
        }
        return names.Count > 0
            ? string.Join(", ", names)
            : "T.A.G. Seeker, Mine Layer, Heavy Laser Assault Cannon, Rifle, Arc";
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
        Text = Localization.Tr(label);
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
