using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Monster Tools" — <c>XonoticMonsterToolsDialog_fill</c> (qcsrc/menu/xonotic/dialog_monstertools.qc) on its
/// 16 x 4 grid, 0.8 of the menu wide: the monster radios (group 2, <c>menu_monsters_edit_spawn</c>), Spawn /
/// Remove, the "Move target:" command button beside the move-target radios (group 3,
/// <c>menu_monsters_edit_movetarget</c>), "Colors:" with "Set skin:" and its 0..99 slider, and "OK" on the last row.
///
/// The cvar bindings are real (the shared <see cref="MenuState.Cvars"/> store); the command buttons
/// (<c>spawnmob</c>, <c>killmob</c>, <c>editmob …</c>) run through <see cref="MenuCommand"/>, which logs them inert
/// until a server-side monster cheat backend exists.
/// </summary>
public partial class DialogMonsterTools : MenuScreen, IQcDialog
{
    // Each monster's MENUQC describe() prose (qcsrc/common/monsters/monster/<m>.qc), carried on its radio's
    // tooltip (the QC radios have none; Xonotic shows the text only in its guide).
    private static readonly (string Value, string Label, string Describe)[] Monsters =
    {
        ("zombie", "Zombie",
            "Zombies are the undead remains of deceased soldiers, risen with a ravenous hunger and no sense of self-preservation. " +
            "When a Zombie senses a nearby player it will begin to charge its target at high speeds. " +
            "While charging, a Zombie may leap towards the player, dealing massive damage on contact. " +
            "If it gets close, the Zombie will punch and bite repeatedly. " +
            "When threatened the Zombie may hold up its hands to block incoming attacks briefly. " +
            "It is no small task to kill that which is already dead. Once a Zombie is defeated, destroy its corpse to prevent it from rising again!"),
        ("spider", "Spider",
            "The Spider is a large mechanically-enhanced arachnoid adept at hunting speedy enemies. " +
            "To slow down its target, the Spider launches a synthetic web-like substance from its cannons. " +
            "Approaching its enwebbed prey, the Spider will inflict a series of high damage bites."),
        ("golem",  "Golem",
            "Golems are large powerful brutes capable of taking and dealing a beating. Keeping your distance is advised. " +
            "The Golem's primary melee attack is a series of punches. " +
            "On occasion the Golem may jump into the air, dealing massive damage in an area as it slams the ground. " +
            "To deal with distant foes, the Golem may throw a chunk of its electrified rocky exterior, zapping nearby targets on impact."),
        ("mage",   "Mage",
            "Wielding nanotechnology as if it were sorcery, the Mage employs a range of unique abilities of its own creation in combat. " +
            "As a primary attack, the Mage throws a homing electric sphere towards the player. " +
            "This sphere will track its target at high speed, exploding on impact or if it does not reach its target in time. " +
            "When threatened, the Mage may deploy an energy shield to protect itself from damage briefly. " +
            "Enemies approaching too closely during this time may be pushed away with explosive force! " +
            "Defensively the Mage is capable of healing itself and nearby allies, with some variants also providing armor and ammunition. " +
            "The Mage may sometimes appear to blink out of existence as it teleports behind its target for a sneak attack."),
        ("wyvern", "Wyvern",
            "The Wyvern is a flying reptilian monster that glides around hunting for fresh prey. " +
            "While fragile, the Wyvern is capable of launching deadly fireballs at the player from a distance, inflicting high damage and causing burning."),
    };

    // dialog_monstertools.qh (name "MonsterTools")
    public string QcTitle => Localization.Tr("Monster Tools");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 16;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_SANDBOXTOOLS", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "DialogMonsterTools"; // QC ATTRIB name "MonsterTools"

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        var monsterGroup = new QcRadioGroup();
        var moveGroup = new QcRadioGroup();

        me.TR();
            me.TD(1, 4, Qc.Label("Monster:"));
        me.TR();
        foreach ((string value, string label, string describe) in Monsters)
            me.TD(1, 4f / 5f, Qc.Radio(monsterGroup, "menu_monsters_edit_spawn", value, label, describe));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 0.75f, Qc.Command("Spawn", "spawnmob $menu_monsters_edit_spawn $menu_monsters_edit_movetarget"));
            me.TD(1, 0.75f, Qc.Command("Remove", "killmob"));
        me.TR();
            me.TD(1, 0.75f, Qc.Command("Move target:", "editmob movetarget $menu_monsters_edit_movetarget"));
            me.TD(1, 3.25f / 4f, Qc.Radio(moveGroup, "menu_monsters_edit_movetarget", "1", "Follow"));
            me.TD(1, 3.25f / 4f, Qc.Radio(moveGroup, "menu_monsters_edit_movetarget", "2", "Wander"));
            me.TD(1, 3.25f / 4f, Qc.Radio(moveGroup, "menu_monsters_edit_movetarget", "3", "Spawnpoint"));
            me.TD(1, 3.25f / 4f, Qc.Radio(moveGroup, "menu_monsters_edit_movetarget", "4", "No moving"));
        me.TR();
            me.TD(1, 4, Qc.Label("Colors:"));
        me.TR();
            me.TD(1, 0.75f, Qc.Command("Set skin:", "editmob skin $menu_monsters_edit_skin"));
            me.TD(1, 1.25f, Qc.Slider(0, 99, 1, "menu_monsters_edit_skin"));

        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns, Qc.Button("OK", GoBack));
    }
}
