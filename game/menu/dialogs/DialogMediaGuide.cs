using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// Guide tab — a faithful C# port of <c>XonoticGuideTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_guide.qc). The QC is a three-pane layout: a topic list (the
/// <c>TOPICS</c> macro in guide/guide.qh — Introduction, Movement, Gametypes, Weapons, Items,
/// Powerups, Buffs, Nades, Monsters, Vehicles, Turrets, Mutators, Mods), an entry list with a
/// keyword filter box, and a description pane fed by the selected entry's <c>describe()</c>.
///
/// FAITHFUL UI NOW: the topic list is real and complete (it's a fixed set, so we populate it
/// faithfully). The entry list and descriptions come from the in-game registries (weapons, items,
/// monsters, …) which VortexArena's menu can't yet enumerate, so the entry list is an empty
/// <see cref="ItemList"/> with an honest note, the filter box is inert, and the description pane
/// shows a short placeholder.
/// </summary>
public partial class DialogMediaGuide : Control
{
    // QC TOPICS(X) in guide/guide.qh — the fixed topic set, in order (labels are the _() strings).
    private static readonly string[] Topics =
    {
        "Introduction", "Movement", "Gametypes", "Weapons", "Items", "Powerups",
        "Buffs", "Nades", "Monsters", "Vehicles", "Turrets", "Mutators", "Mods",
    };

    private Label _description = null!;
    private ItemList _entryList = null!;
    private Label _entryNote = null!;
    // The Weapons topic's entries, parallel to _entryList rows (so a row index maps back to its weapon).
    private readonly List<Weapon> _weaponEntries = new();
    // The Mutators topic's entries, parallel to _entryList rows (so a row index maps back to its mutator).
    private readonly List<MutatorBase> _mutatorEntries = new();

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        // Three side-by-side panes (QC widths 1.5 / 1.75 / 2.75).
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12);
        margin.AddChild(columns);

        columns.AddChild(BuildTopicPane());
        columns.AddChild(BuildEntryPane());
        columns.AddChild(BuildDescriptionPane());
    }

    /// <summary>Left pane: the topic list (QC me.topicList) — faithfully populated.</summary>
    private Control BuildTopicPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Topics"));

        var topicList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(150, 240),
        };
        foreach (string t in Topics)
            topicList.AddItem(t);
        topicList.ItemSelected += OnTopicSelected;
        pane.AddChild(topicList);
        return pane;
    }

    /// <summary>Middle pane: the entry list (QC me.entryList) + keyword filter (QC stringFilterBox).</summary>
    private Control BuildEntryPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Entries"));

        _entryList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 200),
        };
        _entryList.ItemSelected += OnEntrySelected;
        pane.AddChild(_entryList);
        _entryNote = Ui.Label("(select the Weapons topic to browse weapon descriptions)");
        pane.AddChild(_entryNote);

        // QC: stringFilterBox (keyword filter), inert without the registry.
        var filter = new LineEdit { PlaceholderText = "Filter…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pane.AddChild(Ui.Row("Filter:", filter, 60f));
        return pane;
    }

    /// <summary>Right pane: the description pane (QC me.descriptionPane).</summary>
    private Control BuildDescriptionPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);
        pane.AddChild(Ui.Header("Description"));

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _description = Ui.Label("Select a topic, then an entry, to read its description.\n\n(entry descriptions — guide registry pending)");
        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _description.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_description);
        pane.AddChild(scroll);
        return pane;
    }

    // QC topicChangeNotify reloads the entry list from the topic's source. The Weapons topic enumerates the
    // weapon registry (Registry<Weapon>.All, like WeaponPriorityList) and shows each weapon's ported describe()
    // text; the other topics' registry sources (items/monsters/…) aren't enumerated here yet.
    private void OnTopicSelected(long index)
    {
        _entryList.Clear();
        _weaponEntries.Clear();
        _mutatorEntries.Clear();

        if (index < 0 || index >= Topics.Length)
            return;

        if (Topics[index] == "Mutators")
        {
            // The QC Mutators topic lists the mutator registry; each entry's describe() feeds the description
            // pane. We only have ported describe() text for some mutators, so list those that provide it.
            foreach (MutatorBase m in Mutators.All)
            {
                if (m is null || m.NetName.Length == 0 || m.GuideDescription is not { Length: > 0 })
                    continue;
                _mutatorEntries.Add(m);
                _entryList.AddItem(Humanize(m.NetName));
            }
            _entryNote.Text = "(select a mutator to read its description)";
            _description.Text = "Mutators\n\nSelect a mutator from the entry list to read its description.";
            return;
        }

        if (Topics[index] == "Weapons")
        {
            // The QC Weapons topic lists the in-game weapon registry, sorted; each entry's describe() feeds the
            // description pane. Mirror that with Registry<Weapon>.All.
            foreach (Weapon w in Registry<Weapon>.All)
            {
                if (w is null || w.NetName.Length == 0)
                    continue;
                _weaponEntries.Add(w);
                _entryList.AddItem(w.DisplayName.Length > 0 ? w.DisplayName : w.NetName);
            }
            _entryNote.Text = "(select a weapon to read its description)";
            _description.Text = "Weapons\n\nSelect a weapon from the entry list to read its description.";
            return;
        }

        _entryNote.Text = "(entry list / descriptions for this topic — guide registry enumeration pending)";
        _description.Text = $"{Topics[index]}\n\n(entry list / descriptions for this topic — guide registry enumeration pending)";
    }

    // An entry was picked: show the selected entry's ported describe() prose (or a note if none is ported yet).
    private void OnEntrySelected(long index)
    {
        if (index < 0)
            return;

        if (_mutatorEntries.Count > 0)
        {
            if (index >= _mutatorEntries.Count)
                return;
            MutatorBase m = _mutatorEntries[(int)index];
            string mname = Humanize(m.NetName);
            _description.Text = m.GuideDescription is { Length: > 0 } mdesc
                ? $"{mname}\n\n{mdesc}"
                : $"{mname}\n\n(no guide description ported for this mutator yet)";
            return;
        }

        if (index >= _weaponEntries.Count)
            return;
        Weapon w = _weaponEntries[(int)index];
        string name = w.DisplayName.Length > 0 ? w.DisplayName : w.NetName;
        _description.Text = w.GuideDescription is { Length: > 0 } desc
            ? $"{name}\n\n{desc}"
            : $"{name}\n\n(no guide description ported for this weapon yet)";
    }

    // Render a registry NetName (e.g. "offhand_blaster") as a readable label ("Offhand Blaster") for the
    // entry list — the port has no per-mutator localized display-name field (QC's `message` ATTRIB).
    private static string Humanize(string netName)
    {
        string[] parts = netName.Split('_');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }
}
