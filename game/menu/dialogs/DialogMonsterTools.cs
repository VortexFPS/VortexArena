using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Monster-tools cheat dialog — a faithful C# port of <c>XonoticMonsterToolsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_monstertools.qc). It is the small in-game tool that lets a host with cheats on
/// spawn/edit monsters: pick a monster type, Spawn/Remove it, choose its move-target behaviour, and recolor
/// its skin. Every control binds the same engine cvar the QC binds and every button issues the same console
/// command string through <see cref="Widgets.CommandButton"/>.
///
/// FAITHFUL UI NOW: the cvar bindings are real (they write the shared <see cref="MenuState.Cvars"/> store),
/// but the command buttons (<c>spawnmob</c>, <c>killmob</c>, <c>editmob …</c>) drive the server-side monster
/// cheat backend VortexArena does not have yet, so they route through <see cref="MenuCommand"/> and are logged
/// inert until that backend exists. The QC "OK" button just closed the dialog (<c>Dialog_Close</c>); here that
/// is the standard Back.
/// </summary>
public partial class DialogMonsterTools : MenuScreen
{
    // QC radio group 2 over menu_monsters_edit_spawn — the monster type to spawn (netname value).
    // Tooltip = each monster's MENUQC describe() flavor prose (qcsrc/common/monsters/monster/<m>.qc). Base
    // surfaces describe() only on a dedicated monster-book/page widget, which the port lacks; following the
    // same pattern as the mutator dialog (DialogMutators), the describe text rides on the radio tooltip — the
    // closest faithful surface for it.
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

    // QC radio group 3 over menu_monsters_edit_movetarget — what a spawned monster does.
    private static readonly (string Value, string Label)[] MoveTargets =
    {
        ("1", "Follow"),
        ("2", "Wander"),
        ("3", "Spawnpoint"),
        ("4", "No moving"),
    };

    protected override void BuildUi()
    {
        Name = "DialogMonsterTools"; // QC ATTRIB name "MonsterTools"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Monster Tools"));

        // The dialog is short; a single scrolling column keeps it consistent with the other tool dialogs.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // ---- Monster: -----------------------------------------------------------------------------------
        box.AddChild(Ui.Label("Monster:")); // QC makeXonoticTextLabel(0, _("Monster:"))

        var monsterGroup = new ButtonGroup();
        var monsterRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        monsterRow.AddThemeConstantOverride("separation", 8);
        foreach ((string value, string label, string describe) in Monsters)
            monsterRow.AddChild(Widgets.RadioButton("menu_monsters_edit_spawn", value, label, monsterGroup, describe));
        box.AddChild(monsterRow);

        // Spawn / Remove — QC "spawnmob $menu_monsters_edit_spawn $menu_monsters_edit_movetarget" / "killmob".
        var spawnRemove = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        spawnRemove.AddThemeConstantOverride("separation", 8);
        spawnRemove.AddChild(Widgets.CommandButton("Spawn",
            "spawnmob $menu_monsters_edit_spawn $menu_monsters_edit_movetarget"));
        spawnRemove.AddChild(Widgets.CommandButton("Remove", "killmob"));
        box.AddChild(spawnRemove);

        // ---- Move target: -------------------------------------------------------------------------------
        // QC: a "Move target:" command button (editmob movetarget ...) followed by the group-3 radio set.
        var moveRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        moveRow.AddThemeConstantOverride("separation", 8);
        var moveBtn = Widgets.CommandButton("Move target:", "editmob movetarget $menu_monsters_edit_movetarget");
        moveBtn.CustomMinimumSize = new Vector2(160, 36);
        moveBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        moveRow.AddChild(moveBtn);

        var moveGroup = new ButtonGroup();
        foreach ((string value, string label) in MoveTargets)
            moveRow.AddChild(Widgets.RadioButton("menu_monsters_edit_movetarget", value, label, moveGroup));
        box.AddChild(moveRow);

        box.AddChild(Ui.Spacer());

        // ---- Colors: ------------------------------------------------------------------------------------
        box.AddChild(Ui.Label("Colors:")); // QC makeXonoticTextLabel(0, _("Colors:"))

        // Set skin: command button + skin slider (QC "editmob skin $menu_monsters_edit_skin", slider 0..99).
        var skinRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        skinRow.AddThemeConstantOverride("separation", 8);
        var skinBtn = Widgets.CommandButton("Set skin:", "editmob skin $menu_monsters_edit_skin");
        skinBtn.CustomMinimumSize = new Vector2(160, 36);
        skinBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        skinRow.AddChild(skinBtn);
        skinRow.AddChild(Widgets.Slider("menu_monsters_edit_skin", 0f, 99f, 1f));
        box.AddChild(skinRow);

        // QC "OK" button (onClick = Dialog_Close). Here the universal Back closes the dialog.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
