using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Hud;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Guide tab — <c>XonoticGuideTab_fill</c> (qcsrc/menu/xonotic/dialog_media_guide.qc) on its 17 x 6 grid: the
/// topic list 1.5 columns wide (<see cref="QcGuideTopicList"/>, the TOPICS of guide.qh), the entry list 1.75 wide
/// with its filter box under it (<see cref="QcGuideEntryList"/>), the description pane in the 2.75 left over
/// (<see cref="QcGuideDescription"/>), all from half a row down.
///
/// The topics' sources are the registries of guide/guide.qh. Introduction, Movement and Mods are the free-text pages
/// (<see cref="GuidePages"/>); Gametypes, Weapons and Mutators list the port's own registries with whatever
/// description each ported thing carries. The other registries (items, powerups, buffs, nades, monsters, vehicles,
/// turrets) have no guide text in the port yet, so those topics list nothing.
/// </summary>
public partial class DialogMediaGuide : QcTab
{
    /// <summary>TOPICS(X) in guide/guide.qh: name and icon.</summary>
    private static readonly (string Name, string Icon)[] Topics =
    {
        ("Introduction", "gametype_tdm"), ("Movement", "gametype_inv"), ("Gametypes", "gametype_dm"),
        ("Weapons", "gametype_duel"), ("Items", "gametype_kh"), ("Powerups", "gametype_dom"), ("Buffs", "gametype_ka"),
        ("Nades", "gametype_ft"), ("Monsters", "gametype_lms"), ("Vehicles", "gametype_rc"), ("Turrets", "gametype_as"),
        ("Mutators", "gametype_nb"), ("Mods", "gametype_ons"),
    };

    private readonly QcGuideTopicList _topicList = new(Topics.Select(t => (Localization.Tr(t.Name), t.Icon)).ToArray());
    private readonly QcGuideEntryList _entryList = new();
    private readonly QcGuideDescription _description = new();
    private LineEdit _filterBox = null!;
    private string? _filter;

    public DialogMediaGuide() : base(rows: 17f, columns: 6f, intendedWidth: 0.96f) { }

    protected override void Fill()
    {
        float col = 0, width = 1.5f;
        GotoRC(0.5f, col); SetFirstColumn(CurrentColumn);
            TD(Rows - 0.5f, width, _topicList);
                _topicList.ItemSelected += _ => TopicChanged();

        col += width;
        width = 1.75f;
        GotoRC(0.5f, col); SetFirstColumn(CurrentColumn);
            TD(Rows - 1.5f, width, _entryList);
                _entryList.ItemSelected += _ => EntryChanged();
        GotoRC(Rows - 1, col);
            TD(1, 0.3f, Qc.Label("Filter:"));
            _filterBox = TD(1, width - 0.3f, QcLook.InputBox(new LineEdit
            {
                TooltipText = Localization.Tr("Click here or CTRL+F to provide a keyword to narrow down the list; CTRL+DEL to clear; ENTER when done."),
            }));
                _filterBox.TextChanged += t => { _filter = t.Length > 0 ? t : null; Refilter(sourceChanged: false); };

        col += width;
        width = 2.75f;
        GotoRC(0.5f, col); SetFirstColumn(CurrentColumn);
            TD(Rows - 0.5f, width, _description);

        _topicList.SetSelected(0);
        TopicChanged();
    }

    /// <summary>topicChangeNotify: the topic's source into the entry list, the filter cleared.</summary>
    private void TopicChanged()
    {
        _filterBox.SetBlockSignals(true);
        _filterBox.Text = "";
        _filterBox.SetBlockSignals(false);
        _filter = null;
        Refilter(sourceChanged: true);
    }

    /// <summary>XonoticEntryList_refilter.</summary>
    private void Refilter(bool sourceChanged)
    {
        _entryList.SetEntries(Source(_topicList.SelectedItem, _filter), sourceChanged);
        EntryChanged();
    }

    /// <summary>entryChangeNotify: the selected entry's describe() into the description pane.</summary>
    private void EntryChanged()
    {
        int i = _entryList.SelectedItem;
        if (_entryList.Entries.Count <= 0 || i < 0 || i >= _entryList.Entries.Count) return;
        _description.SetDescription(_entryList.Entries[i].Describe());
    }

    /// <summary>The topic's DataSource reload(filter): entries whose name holds the filter, case-insensitively.</summary>
    private static IReadOnlyList<GuideEntry> Source(int topic, string? filter)
    {
        IEnumerable<GuideEntry> entries = (topic >= 0 && topic < Topics.Length ? Topics[topic].Name : "") switch
        {
            "Introduction" => Pages(GuidePages.Introduction),
            "Movement" => Pages(GuidePages.Movement),
            "Mods" => Pages(GuidePages.Mods),
            "Gametypes" => GameTypes.All
                .Where(g => g is not null && g.NetName.Length > 0)
                .Select(g => new GuideEntry(Name(g.DisplayName, g.NetName), MenuSkin.SkinImage("gametype_" + g.NetName), () => g.MenuDescription ?? "")),
            "Weapons" => WeaponEntries(),
            "Mutators" => Mutators.All
                .Where(m => m is not null && m.NetName.Length > 0 && m.GuideDescription is { Length: > 0 })
                .Select(m => new GuideEntry(Humanize(m.NetName), null, () => m.GuideDescription ?? "")),
            _ => Enumerable.Empty<GuideEntry>(),
        };
        if (!string.IsNullOrEmpty(filter))
            entries = entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return entries.ToList();
    }

    private static IEnumerable<GuideEntry> Pages(IReadOnlyList<GuidePages.Page> pages)
        => pages.Select(p => new GuideEntry(p.Name, null, p.Describe));

    /// <summary>WeaponSource: the regular weapons, then Overkill's, then Overkill's superweapons; hidden ones left out.</summary>
    private static IEnumerable<GuideEntry> WeaponEntries()
    {
        static bool Ok(Weapon w) => w.NetName.StartsWith("ok", StringComparison.Ordinal) && w.NetName != "ok";
        var shown = Registry<Weapon>.All.Where(w => w is not null && w.NetName.Length > 0 && w.MenuRegistered
            && (w.SpawnFlags & WeaponFlags.Hidden) == 0).ToList();
        IEnumerable<Weapon> ordered = shown.Where(w => !Ok(w))
            .Concat(shown.Where(w => Ok(w) && !w.IsSuperWeapon))
            .Concat(shown.Where(w => Ok(w) && w.IsSuperWeapon));
        foreach (Weapon w in ordered)
        {
            Weapon weapon = w;
            yield return new GuideEntry(Name(w.DisplayName, w.NetName), WeaponHud.Icon(w), () => weapon.GuideDescription ?? "");
        }
    }

    private static string Name(string display, string netName) => display.Length > 0 ? display : Humanize(netName);

    // A registry NetName ("offhand_blaster") as a readable label ("Offhand Blaster"): the port keeps no localized
    // display name (QC's `message`) for mutators.
    private static string Humanize(string netName)
    {
        string[] parts = netName.Split('_');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(" ", parts);
    }
}
