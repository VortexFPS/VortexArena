using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Nades;
using VortexArena.Common.Menu;
using VortexArena.Game.Hud;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Guide tab — <c>XonoticGuideTab_fill</c> (qcsrc/menu/xonotic/dialog_media_guide.qc) on its 17 x 6 grid: the
/// topic list 1.5 columns wide (<see cref="QcGuideTopicList"/>, the TOPICS of guide.qh), the entry list 1.75 wide
/// with its filter box under it (<see cref="QcGuideEntryList"/>), the description pane in the 2.75 left over
/// (<see cref="QcGuideDescription"/>), all from half a row down.
///
/// The topics' sources are the registries of guide/guide.qh. Introduction, Movement and Mods are the free-text pages
/// (<see cref="GuidePages"/>). The other topics read the native gameplay catalogs.
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
    private readonly GuideSelection _selection = new();
    private LineEdit _filterBox = null!;

    private string SelectedTopic => Topics[_topicList.SelectedItem].Name;

    public DialogMediaGuide() : base(rows: 17f, columns: 6f, intendedWidth: 0.96f) { }

    // Handle the Guide shortcuts before LineEdit's normal text editing and navigation.
    public override void _Input(InputEvent @event)
    {
        if (!IsVisibleInTree() || @event is not InputEventKey { Pressed: true } key)
            return;

        if (_entryList.HasFocus() && key.CtrlPressed && key.Keycode == Key.F)
            _filterBox.GrabFocus();
        else if (_entryList.HasFocus() && key.CtrlPressed && key.Keycode == Key.U)
            ClearFilter();
        else if (_filterBox.HasFocus())
        {
            switch (key.Keycode)
            {
                case Key.Delete when key.CtrlPressed:
                    ClearFilter();
                    break;
                case Key.Enter or Key.KpEnter:
                    _entryList.GrabFocus();
                    break;
                case Key.Up:
                    _entryList.SetSelected(_entryList.SelectedItem - 1);
                    break;
                case Key.Down:
                    _entryList.SetSelected(_entryList.SelectedItem + 1);
                    break;
                default:
                    return;
            }
        }
        else
            return;

        GetViewport().SetInputAsHandled();
    }

    private void ClearFilter()
    {
        _filterBox.Text = "";
        Refilter(sourceChanged: false);
    }

    protected override void Fill()
    {
        // The Guide is also accessible before the first match.
        StatusEffectsCatalog.RegisterAll();
        NadeRegistry.RegisterAll();

        float col = 0, width = 1.5f;
        GotoRC(0.5f, col); SetFirstColumn(CurrentColumn);
            TD(Rows - 0.5f, width, _topicList);
                _topicList.ItemSelected += _ => TopicChanged();

        col += width;
        width = 1.75f;
        GotoRC(0.5f, col); SetFirstColumn(CurrentColumn);
            TD(Rows - 1.5f, width, _entryList);
                _entryList.ItemSelected += _ =>
                {
                    RememberEntry();
                    UpdateDescription();
                };
                _entryList.EntryClicked += RememberEntry;
        GotoRC(Rows - 1, col);
            TD(1, 0.3f, Qc.Label("Filter:"));
            _filterBox = TD(1, width - 0.3f, QcLook.InputBox(new LineEdit
            {
                TooltipText = Localization.Tr("Click here or CTRL+F to provide a keyword to narrow down the list; CTRL+DEL to clear; ENTER when done."),
            }));
                _filterBox.TextChanged += _ => Refilter(sourceChanged: false);

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
        _filterBox.Text = "";
        Refilter(sourceChanged: true);
    }

    /// <summary>XonoticEntryList_refilter.</summary>
    private void Refilter(bool sourceChanged)
    {
        string topic = SelectedTopic;
        var entries = Source(topic, _filterBox.Text);
        int selected = _selection.Resolve(topic, entries, static entry => entry.Id);
        _entryList.SetEntries(entries, selected, sourceChanged);
        if (sourceChanged && _entryList.SelectedEntry is { } entry)
            _selection.RememberInitial(topic, entry.Id);

        UpdateDescription();
    }

    private void RememberEntry()
    {
        if (_entryList.SelectedEntry is { } entry)
            _selection.Remember(SelectedTopic, entry.Id);
    }

    /// <summary>entryChangeNotify: the selected entry's describe() into the description pane.</summary>
    private void UpdateDescription() => _description.SetDescription(_entryList.SelectedEntry?.Describe() ?? "");

    /// <summary>The topic's DataSource reload(filter): entries whose name holds the filter, case-insensitively.</summary>
    private static IReadOnlyList<GuideEntry> Source(string topic, string? filter)
    {
        IEnumerable<GuideEntry> entries = topic switch
        {
            "Introduction" => Pages(GuidePages.Introduction),
            "Movement" => Pages(GuidePages.Movement),
            "Mods" => Pages(GuidePages.Mods),
            "Gametypes" => GameTypes.All
                .Where(g => g is not null && g.NetName.Length > 0)
                .Select(g => new GuideEntry(g.NetName, Localization.Tr(EntryName(g.DisplayName, g.NetName)),
                    MenuSkin.SkinImage("gametype_" + (g.NetName == "freezetag" ? "ft" : g.NetName)),
                    () => GuideCatalogText.DescribeGametype(g))),
            "Weapons" => WeaponEntries(),
            "Items" => ItemEntries(),
            "Powerups" => Items.All.Where(item => item.IsPowerup)
                .Select(item => HudEntry(item.NetName, item.DisplayName, item is ExtraLifeItem ? "item_mega_health" : item.Icon,
                    () => GuideCatalogText.DescribePowerup(item))),
            "Buffs" => BuffEntries(),
            "Nades" => NadeRegistry.All.Where(nade => nade.Id != 0)
                .Select(nade => HudEntry(nade.NetName, nade.Name, nade.NetName == "pokenade" ? "nade_monster" : "nade_" + nade.NetName,
                    () => GuideCatalogText.DescribeNade(nade))),
            "Monsters" => Monsters.All.Select(monster => new GuideEntry(
                monster.NetName, Localization.Tr(EntryName(monster.DisplayName, monster.NetName)), null,
                () => GuideCatalogText.DescribeMonster(monster))),
            "Vehicles" => Vehicles.All.Select(vehicle => HudEntry(vehicle.NetName, vehicle.DisplayName,
                GuideCatalogText.VehicleIcon(vehicle.NetName), () => GuideCatalogText.DescribeVehicle(vehicle))),
            "Turrets" => Turrets.All.Select(turret => new GuideEntry(
                turret.NetName, Localization.Tr(EntryName(turret.DisplayName, turret.NetName)), null,
                () => GuideCatalogText.DescribeTurret(turret))),
            "Mutators" => MutatorEntries(),
            _ => Enumerable.Empty<GuideEntry>(),
        };
        if (!string.IsNullOrEmpty(filter))
            entries = entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return GuideCatalogOrder.Order(topic, entries, static entry => entry.Id).ToList();
    }

    private static IEnumerable<GuideEntry> Pages(IReadOnlyList<GuidePages.Page> pages)
        => pages.Select(p => new GuideEntry(p.SourceName, Localization.Tr(p.SourceName), null, p.Describe));

    private static IEnumerable<GuideEntry> BuffEntries()
    {
        foreach (var effect in StatusEffectsCatalog.All.Where(effect => effect.IsBuff && !effect.Hidden))
        {
            string name = effect.DisplayName ?? Humanize(effect.Name["buff_".Length..]);
            yield return HudEntry(effect.Name, name, effect.Icon ?? effect.Name, () => GuideCatalogText.DescribeBuff(effect, name));
        }
    }

    private static IEnumerable<GuideEntry> MutatorEntries()
    {
        foreach (var mutator in Mutators.All)
        {
            string description = GuideCatalogText.DescribeMutator(mutator);
            if (description.Length == 0)
                continue;

            string name = GuideCatalogText.MutatorName(mutator, Humanize(mutator.NetName));
            yield return new GuideEntry(mutator.NetName, Localization.Tr(name), null, () => description);
        }
    }

    /// <summary>ItemSource groups ammo first, then health and armor, excluding powerups.</summary>
    private static IEnumerable<GuideEntry> ItemEntries()
    {
        foreach (var item in Items.All.Where(item => item is (AmmoPickup or HealthPickup or ArmorPickup) && !item.IsPowerup))
        {
            string icon = item is VaporizerCellsItem ? "ammo_supercells" : item.Icon;
            yield return HudEntry(item.NetName, EntryName(item.DisplayName, item.NetName), icon, () => GuideCatalogText.DescribeItem(item));
        }
    }

    /// <summary>WeaponSource excludes hidden special attacks.</summary>
    private static IEnumerable<GuideEntry> WeaponEntries()
    {
        const int HiddenSpecialAttack = WeaponFlags.Hidden | WeaponFlags.SpecialAttack;
        foreach (var w in Weapons.All.Where(w => w.MenuRegistered && (w.SpawnFlags & HiddenSpecialAttack) != HiddenSpecialAttack))
        {
            string name = EntryName(w.DisplayName, w.NetName);
            Func<string> describe = () => GuideCatalogText.DescribeWeapon(w);
            string? icon = w.NetName switch
            {
                "okmachinegun" => "ok_weapon_smg",
                "oknex" => "ok_weapon_rail",
                "okshotgun" => "ok_weapon_shotgun",
                "okhmg" => "weaponhmg",
                "okrpc" => "weaponrpc",
                _ => null,
            };
            yield return icon is null
                ? new GuideEntry(w.NetName, Localization.Tr(name), WeaponHud.Icon(w), describe)
                : HudEntry(w.NetName, name, icon, describe);
        }
    }

    private static GuideEntry HudEntry(string id, string name, string icon, Func<string> describe)
        => new(id, Localization.Tr(name),
            TextureCache.GetFirst($"gfx/hud/{HudSkin.SkinName}/{icon}", $"gfx/hud/default/{icon}"), describe);

    private static string EntryName(string display, string netName) => display.Length > 0 ? display : Humanize(netName);

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
