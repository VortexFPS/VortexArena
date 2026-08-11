using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using VortexArena.Server.Bot.Neural;

namespace VortexArena.Game.Menu;

/// <summary>In-match laboratory for bot defaults, learned-policy selection, and HERE-directed test bots.</summary>
public partial class DialogBotPolicies : MenuScreen
{
    private const string BookmarkCvar = "menu_bot_policy_bookmarks";
    private const string SortCvar = "menu_bot_policy_sort";
    private Tree _tree = null!;
    private Label _details = null!;
    private OptionButton _sort = null!;
    private IReadOnlyList<PolicyCatalog.Entry> _policies = Array.Empty<PolicyCatalog.Entry>();

    protected override void BuildUi()
    {
        Name = "DialogBotPolicies";
        MenuState.Cvars.Register(BookmarkCvar, "");
        MenuState.Cvars.Register(SortCvar, "ranking");

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 28);
        AddChild(margin);
        var root = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 9);
        margin.AddChild(root);
        root.AddChild(MakeTitle("Bot Movement Lab"));

        var defaults = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        defaults.AddThemeConstantOverride("separation", 16);
        defaults.AddChild(MakeRow("Bots:", Widgets.Slider("bot_number", 0, 16, 1), 75));
        defaults.AddChild(MakeRow("Skill:", Widgets.Slider("skill", 0, 20, 1), 75));
        defaults.AddChild(MakeRow("Policy Hz:", Widgets.Slider("bot_neural_hz", 10, 36, 1), 95));
        defaults.AddChild(Widgets.CheckBox("bot_neural_tracefan", "Dynamic obstacle vision"));
        defaults.AddChild(Widgets.CheckBox("bot_nofire", "No combat fire"));
        root.AddChild(defaults);

        var modeBar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        modeBar.AddThemeConstantOverride("separation", 8);
        modeBar.AddChild(MakeButton("Use classic bot movement", () => MenuCommand.Run("cmd bot_policy_apply classic")));
        modeBar.AddChild(MakeButton("Use selected policy", ApplySelected));
        modeBar.AddChild(MakeButton("Bookmark / unbookmark", ToggleBookmark));
        modeBar.AddChild(MakeButton("Refresh policies", RefreshPolicies));
        root.AddChild(modeBar);

        var sortRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sortRow.AddChild(MakeLabel("Sort policies: "));
        _sort = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sort.AddItem("Evaluation ranking");
        _sort.AddItem("Bookmarks first");
        _sort.AddItem("Newest artifact");
        _sort.AddItem("Run / filename");
        _sort.ItemSelected += i =>
        {
            MenuState.Cvars.Set(SortCvar, i switch { 1 => "bookmarks", 2 => "newest", 3 => "name", _ => "ranking" });
            MenuState.Cvars.MarkArchived(SortCvar);
            Populate();
        };
        sortRow.AddChild(_sort);
        root.AddChild(sortRow);

        var columns = new HBoxContainer();
        columns.AddChild(MakeHeader("★   Policy / artifact"));
        var scoreHeader = MakeHeader("Evaluation       Stage / update");
        scoreHeader.HorizontalAlignment = HorizontalAlignment.Right;
        scoreHeader.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(scoreHeader);
        root.AddChild(columns);

        _tree = new Tree
        {
            Columns = 4, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _tree.SetColumnExpand(0, false); _tree.SetColumnCustomMinimumWidth(0, 30);
        _tree.SetColumnExpand(1, true); _tree.SetColumnExpandRatio(1, 60);
        _tree.SetColumnExpand(2, true); _tree.SetColumnExpandRatio(2, 22);
        _tree.SetColumnExpand(3, true); _tree.SetColumnExpandRatio(3, 18);
        _tree.ItemSelected += UpdateDetails;
        _tree.ItemActivated += ApplySelected;
        root.AddChild(_tree);

        _details = MakeLabel("Double-click a policy to apply it. Invalid/incompatible weights fall back to classic movement.");
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_details);

        var directed = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        directed.AddThemeConstantOverride("separation", 8);
        directed.AddChild(MakeButton("Spawn HERE-directed bot", () => MenuCommand.Run("cmd bot_directed_add")));
        directed.AddChild(Widgets.CheckBox("bot_directed_weapon_movement", "Allow movement weapons"));
        var hint = MakeLabel("Press P to place/retarget its HERE marker. It ignores enemies, items, roles, aiming, and combat.");
        hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        directed.AddChild(hint);
        root.AddChild(directed);
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
        RefreshPolicies();
    }

    private HashSet<string> Bookmarks() => new(MenuState.Cvars.GetString(BookmarkCvar)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    private void RefreshPolicies() { _policies = PolicyCatalog.Scan(); Populate(); }

    private void Populate()
    {
        if (_tree is null) return;
        HashSet<string> marks = Bookmarks();
        string sort = MenuState.Cvars.GetString(SortCvar);
        IEnumerable<PolicyCatalog.Entry> rows = sort switch
        {
            "bookmarks" => _policies.OrderByDescending(p => marks.Contains(p.Path)).ThenByDescending(p => p.ArrivalRate ?? -1).ThenBy(p => p.MeanArrivalSeconds ?? double.MaxValue),
            "newest" => _policies.OrderByDescending(p => p.ModifiedUtc),
            "name" => _policies.OrderBy(p => p.Run, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Artifact, StringComparer.OrdinalIgnoreCase),
            _ => _policies.OrderByDescending(p => p.ArrivalRate ?? -1).ThenBy(p => p.MeanArrivalSeconds ?? double.MaxValue).ThenByDescending(p => p.ModifiedUtc),
        };
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        TreeItem? first = null;
        foreach (PolicyCatalog.Entry p in rows)
        {
            TreeItem item = _tree.CreateItem(root); first ??= item;
            item.SetText(0, marks.Contains(p.Path) ? "★" : "");
            item.SetText(1, p.DisplayName);
            string rate = p.ArrivalRate is double r ? $"{r:P1}" : "not evaluated";
            string time = p.MeanArrivalSeconds is double t ? $" / {t:0.00}s" : "";
            item.SetText(2, rate + time); item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetText(3, $"S{p.Stage} / U{p.Update}"); item.SetTextAlignment(3, HorizontalAlignment.Right);
            item.SetMetadata(0, p.Path);
        }
        first?.Select(0);
        UpdateDetails();
        int selected = sort switch { "bookmarks" => 1, "newest" => 2, "name" => 3, _ => 0 };
        if (_sort.Selected != selected) _sort.Select(selected);
    }

    private string? SelectedPath()
    {
        TreeItem? selected = _tree.GetSelected();
        if (selected is null) return null;
        Variant meta = selected.GetMetadata(0);
        return meta.VariantType == Variant.Type.String ? meta.AsString() : null;
    }

    private void ApplySelected()
    {
        string? path = SelectedPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        MenuState.Cvars.Set("bot_neural_weights", path); MenuState.Cvars.Set("bot_neural", "1");
        MenuCommand.Run($"cmd bot_policy_apply \"{path.Replace("\"", "")}\"");
        _details.Text = $"Requested {path}. Use bot_neural_status in the console for load/bake details.";
    }

    private void ToggleBookmark()
    {
        string? path = SelectedPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        HashSet<string> marks = Bookmarks();
        if (!marks.Add(path)) marks.Remove(path);
        MenuState.Cvars.Set(BookmarkCvar, string.Join(';', marks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        MenuState.Cvars.MarkArchived(BookmarkCvar); Populate();
    }

    private void UpdateDetails()
    {
        string? path = SelectedPath();
        PolicyCatalog.Entry? p = _policies.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (p is null) { _details.Text = _policies.Count == 0 ? "No .vxpw policies were found. Set VORTEX_POLICY_ROOT to add a run folder." : ""; return; }
        _details.Text = $"{p.Path}  •  {p.Phase}  •  {p.StageSteps.ToString("N0", CultureInfo.InvariantCulture)} stage steps";
    }
}
