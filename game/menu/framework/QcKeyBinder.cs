using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Console;
using VortexArena.Game.Console;
using VortexArena.Game.Hud;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Menu;

/// <summary>
/// The key binder — xonotic/keybinder.qc: Xonotic's list of bindable functions under their section titles
/// (KeyBinds_BuildList, weapon groups and their weapons with HUD icons and tree lines included), each row the
/// function's description and the keys bound to it, centred in a twelve-character column. Selecting skips the
/// titles; Enter / double-click / "Change key..." waits for the next key or button and binds it (a function
/// keeps at most two keys); Delete / "Clear" unbinds. Binds go to the client's bind table (<see cref="BindTable"/>)
/// and are saved with the config.
///
/// <para>One deliberate difference: Xonotic's "drop weapon / throw nade" row names the command <c>wepaon_drop</c>
/// (a typo, so it shows no key and binds nothing); here it is <c>weapon_drop</c>, the command the default binds
/// use.</para>
/// </summary>
public partial class QcKeyBinder : MenuListBox
{
    // keybinder.qc: "bind to empty cmd instead of using unbind so it gets saved in config and overrides any
    // default binds".
    private const string NotBound = "// not bound";
    private const int MaxKeysPerFunction = 2;

    /// <summary>One row. <see cref="Func"/>: "" = a title or blank line, "*..." = a fixed shortcut shown greyed,
    /// ";func;other..." = binding it unbinds the others. <see cref="Descr"/>: "$userbindN" = a user bind.
    /// <see cref="Tree"/>: ' ', '+', 'T' or 'L' (the weapon-group tree line).</summary>
    private sealed record Entry(string Func, string Descr, char Tree = ' ', Weapon? Icon = null);

    private readonly List<Entry> _entries = new();
    private int _previouslySelected = -1;

    /// <summary>"Change key...": held pressed while a key is awaited. "Clear": disabled meanwhile. "Edit...":
    /// enabled only on a user bind.</summary>
    public BaseButton? KeyGrabButton { get; set; }
    public BaseButton? ClearButton { get; set; }
    public BaseButton? UserbindEditButton
    {
        get => _userbindEditButton;
        set
        {
            _userbindEditButton = value;
            int i = SelectedItem;
            if (value is not null && i >= 0 && i < _entries.Count) value.Disabled = !IsUserbind(_entries[i].Descr);
        }
    }
    private BaseButton? _userbindEditButton;

    /// <summary>True while the next key pressed will be bound (QC <c>keyGrabber == me</c>).</summary>
    public bool Grabbing { get; private set; }

    /// <summary>True while any binder waits for a key: a global hotkey (the screenshot key) yields to it.</summary>
    public static bool AnyGrabbing { get; private set; }

    public QcKeyBinder()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
        FocusMode = FocusModeEnum.All;
        BuildList();
        ItemCount = _entries.Count;
        ItemActivated += _ => Change(); // doubleClickListBoxItem
    }

    public override void _Ready()
    {
        base._Ready();
        SetSelected(0);
    }

    // resizeNotify measures the icon column on WEP_FIRST's icon.
    private static Texture2D? FirstWeaponIcon()
    {
        foreach (Weapon w in Weapons.All)
            if (w.NetName.Length > 0)
                return WeaponHud.Icon(w);
        return null;
    }

    private void BuildList()
    {
        void Def(string func, string descr, char tree = ' ', Weapon? icon = null) => _entries.Add(new Entry(func, Localization.Tr(descr), tree, icon));
        void Header(string title) => Def("", title);
        void Empty() => _entries.Add(new Entry("", ""));

        Header("Moving");
        Def("+forward", "move forwards");
        Def("+back", "move backwards");
        Def("+moveleft", "strafe left");
        Def("+moveright", "strafe right");
        Def("+jump", "jump / swim");
        Def("+crouch", "crouch / sink");
        Def("+hook", "offhand hook");
        Def("+jetpack", "jetpack");
        Empty();
        Header("Attacking");
        Def("+fire", "primary fire");
        Def("+fire2", "secondary fire");
        Empty();
        Header("Weapons");
        Def("weapprev", "previous");
        Def("weapnext", "next");
        Def("weapon_last", "previously used");
        Def("weapon_best", "best");
        Def("weapon_reload", "reload");
        Def("weapon_drop", "drop weapon / throw nade");
        // Impulses 1..9 then 0: a group of one weapon is bound as the group, with the weapon's icon; a larger group
        // is bound as a whole or weapon by weapon, each overriding the other.
        for (int imp = 1; ; ++imp)
        {
            var group = new List<Weapon>();
            foreach (Weapon w in Weapons.All)
                if (w.NetName.Length > 0 && w.Impulse == imp && w.MenuRegistered
                    && (w.SpawnFlags & WeaponFlags.SpecialAttack) == 0
                    && !((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0 && (w.SpawnFlags & WeaponFlags.Hidden) != 0))
                    group.Add(w);
            if (group.Count == 1)
                Def("weapon_group_" + imp, group[0].DisplayName, ' ', group[0]);
            else if (group.Count > 1)
            {
                string overrides = "";
                foreach (Weapon w in group) overrides += ";weapon_" + w.NetName;
                string groupBind = ";weapon_group_" + imp;
                Def(groupBind + overrides, string.Format(Localization.Tr("weapon group {0}"), imp));
                for (int i = 0; i < group.Count; i++)
                    Def(";weapon_" + group[i].NetName + groupBind, group[i].DisplayName,
                        i == 0 ? 'T' : i == group.Count - 1 ? 'L' : '+', group[i]);
            }
            if (imp == 0) break;
            if (imp == 9) imp = -1;
        }
        Empty();
        Header("View");
        Def("+zoom", "hold zoom");
        Def("togglezoom", "toggle zoom");
        Def("+showscores", "show scores");
        Def("screenshot", "screenshot");
        Def("+hud_panel_radar_maximized", "maximize radar");
        Def("toggle chase_active", "3rd person view");
        Def("spec", "enter spectator mode");
        Empty();
        Header("Communication");
        Def("messagemode", "public chat");
        Def("messagemode2", "team chat");
        Def("+con_chat_maximize", "show chat history");
        Def("vyes", "vote YES");
        Def("vno", "vote NO");
        Def("ready", "ready");
        Empty();
        Header("Client");
        Def("+show_info", "server info");
        Def("toggleconsole", "enter console");
        Def("*SHIFT+ESCAPE", "enter console");
        Def("menu_showquitdialog", "quit");
        Empty();
        Header("Teamplay");
        Def("team_auto", "auto-join team");
        Def("team_selection_show", "team selection");
        Def("spec", "spectate");
        Empty();
        Header("Misc");
        Def("+use", "drop key/flag, exit vehicle");
        Def("kill", "suicide / respawn");
        Def("quickmenu", "quick menu");
        Def("*TAB+ESCAPE", "scoreboard user interface");
        Empty();
        Header("User defined");
        for (int i = 1; i <= 32; ++i)
            _entries.Add(new Entry("+userbind " + i, "$userbind" + i));
        Empty();
        Header("Development");
        Def("menu_showsandboxtools", "sandbox menu");
        Def("+button8", "drag object (sandbox)");
        Def("wpeditor_menu", "waypoint editor menu");
    }

    private static bool IsSpecial(string func) => func.StartsWith('*');
    private static bool IsOverrider(string func) => func.StartsWith(';');
    private static bool IsUserbind(string descr) => descr.StartsWith('$');

    /// <summary>KeyBinder_Bind_Edit / editUserbind: the selected row's user bind ("userbindN", the prefix of its
    /// _description / _press / _release cvars), or null when the row is not one.</summary>
    public string? SelectedUserbind
    {
        get
        {
            int i = SelectedItem;
            if (i < 0 || i >= _entries.Count) return null;
            Entry e = _entries[i];
            if (e.Func.Length == 0 || IsSpecial(e.Func) || !IsUserbind(e.Descr)) return null;
            return e.Descr[1..];
        }
    }
    private bool Unselectable(int i) => _entries[i].Func.Length == 0 || IsSpecial(_entries[i].Func);

    /// <summary>The function a row binds (an overrider's first part).</summary>
    private static string BoundFunc(string func)
    {
        if (!IsOverrider(func)) return func;
        string[] parts = func.Split(';');
        return parts.Length > 1 ? parts[1] : "";
    }

    /// <summary>findkeysforcommand: the keys bound to <paramref name="func"/>, in DarkPlaces key-number order.</summary>
    private static List<string> KeysFor(string func)
    {
        var keys = new List<string>();
        foreach (KeyValuePair<string, string> kv in BindTable.List())
            if (string.Equals(kv.Value, func, StringComparison.OrdinalIgnoreCase))
                keys.Add(kv.Key);
        keys.Sort((a, b) => KeyNumber(a).CompareTo(KeyNumber(b)));
        return keys;
    }

    private static int KeyNumber(string key)
    {
        int n = CsqcKeys.StringToKeynum(BindInput.EngineKeyName(key));
        return n < 0 ? int.MaxValue : n;
    }

    private static void UnbindAll(IEnumerable<string> keys)
    {
        foreach (string key in keys)
            BindTable.Bind(key, NotBound);
    }

    // -----------------------------------------------------------------------------------------------------
    //  Selection, buttons, grabbing
    // -----------------------------------------------------------------------------------------------------

    /// <summary>XonoticKeyBinder_setSelected: the keyboard skips titles in the direction it moves; a click on one
    /// keeps the previous selection.</summary>
    public override void SetSelected(int i)
    {
        if (ItemCount <= 0) return;
        i = Math.Clamp(i, 0, ItemCount - 1);
        if (!MousePressed)
        {
            if (i < _previouslySelected)
                while (i > 0 && Unselectable(i)) --i;
            else
                while (i < ItemCount - 1 && Unselectable(i)) ++i;
        }
        if (Unselectable(i)) i = _previouslySelected;
        else _previouslySelected = i;
        if (i < 0) return;
        if (UserbindEditButton is not null) UserbindEditButton.Disabled = !IsUserbind(_entries[i].Descr);
        base.SetSelected(i);
    }

    /// <summary>KeyBinder_Bind_Change: wait for the next key for the selected function.</summary>
    public void Change()
    {
        int i = SelectedItem;
        if (i < 0 || i >= _entries.Count || Unselectable(i)) return;
        SetSelected(i);
        Grabbing = AnyGrabbing = true;
        if (ClearButton is not null) ClearButton.Disabled = true;
        KeyGrabButton?.QueueRedraw();
        QueueRedraw();
    }

    /// <summary>KeyBinder_Bind_Clear: unbind every key of the selected function.</summary>
    public void Clear()
    {
        int i = SelectedItem;
        if (i < 0 || i >= _entries.Count || Unselectable(i)) return;
        SetSelected(i);
        UnbindAll(KeysFor(BoundFunc(_entries[i].Func)));
        Saved();
    }

    /// <summary>KeyBinder_Bind_Reset_All: <c>unbindall; exec binds-xonotic.cfg</c>.</summary>
    public void ResetAll()
    {
        MenuState.ReloadDefaultBinds();
        Saved();
    }

    private void Saved()
    {
        MenuState.SaveUserConfig();
        QueueRedraw();
    }

    private void EndGrab()
    {
        Grabbing = AnyGrabbing = false;
        if (ClearButton is not null) ClearButton.Disabled = false;
        KeyGrabButton?.QueueRedraw();
        QueueRedraw();
    }

    /// <summary>XonoticKeyBinder_keyGrabbed.</summary>
    private void KeyGrabbed(InputEvent ev)
    {
        EndGrab();
        if (ev is InputEventKey { Keycode: Key.Escape }) return;
        // "forbid these keys from being bound in the menu"
        if (ev is InputEventKey { Keycode: Key.Capslock or Key.Numlock })
        {
            Change();
            return;
        }
        string? key = BindInput.EventKey(ev);
        int i = SelectedItem;
        if (key is null || i < 0 || i >= _entries.Count || Unselectable(i)) return;
        string func = _entries[i].Func;
        if (IsOverrider(func))
        {
            string[] parts = func.Split(';');
            if (parts.Length <= 2) return;
            func = parts[1];
            for (int j = 2; j < parts.Length; j++)
                UnbindAll(KeysFor(parts[j]));
        }
        List<string> keys = KeysFor(func);
        if (keys.Count >= MaxKeysPerFunction) UnbindAll(keys);
        BindTable.Bind(key, func);
        Saved();
    }

    /// <summary>The binder is the key grabber while it waits: the next key or button press is the bind, whatever
    /// else would have taken it (Escape cancels).</summary>
    public override void _Input(InputEvent ev)
    {
        if (!Grabbing) return;
        bool press = ev is InputEventKey { Pressed: true, Echo: false } || ev is InputEventMouseButton { Pressed: true };
        if (!press) return;
        GetViewport().SetInputAsHandled();
        KeyGrabbed(ev);
    }

    protected override bool HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Enter or Key.KpEnter or Key.Space:
                Change();
                return true;
            case Key.Delete or Key.KpPeriod or Key.Backspace:
                Clear();
                return true;
            default:
                return base.HandleKey(key);
        }
    }

    // -----------------------------------------------------------------------------------------------------
    //  Drawing (XonoticKeyBinder_drawListBoxItem)
    // -----------------------------------------------------------------------------------------------------

    protected override bool DrawsOwnHighlight => true;

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _entries.Count) return;
        Entry e = _entries[index];
        float w = rect.Size.X, h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        Color color;
        float extraMargin;
        string descr = e.Descr;
        if (e.Func.Length == 0)
        {
            color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_KEYGRABBER_TITLES", 0.03f, 0.25f, 0.49f),
                MenuSkin.SkinNumber("ALPHA_KEYGRABBER_TITLES", 1f)));
            extraMargin = 0f;
        }
        else
        {
            if (isSelected)
            {
                Color fill = Grabbing
                    ? MenuSkin.Fade(MenuSkin.SkinColor("COLOR_LISTBOX_WAITING", 0.73f, 0.82f, 0.9f), MenuSkin.SkinNumber("ALPHA_LISTBOX_WAITING", 0.8f))
                    : MenuSkin.Fade(MenuSkin.Selection, MenuSkin.SelectionAlpha);
                DrawRect(rect, fill);
            }
            else if (isFocused)
                DrawRect(rect, MenuSkin.Fade(MenuSkin.ListFocused, FocusedItemAlpha));
            color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_KEYGRABBER_KEYS", 0.96f, 0.99f, 1f),
                MenuSkin.SkinNumber("ALPHA_KEYGRABBER_KEYS", 0.8f)));
            extraMargin = 0.5f * ch;
        }

        // resizeNotify: the keys column is twelve characters wide, one character in from the right.
        float keysSize = 12f * ch;
        float descrWidth = w - keysSize - 2f * ch;
        float keysOrigin = descrWidth + ch;

        if (IsUserbind(descr))
        {
            string s = descr[1..];
            descr = MenuState.Cvars.GetString(s + "_description");
            if (descr.Length == 0) descr = s;
            if (MenuState.Cvars.GetString(s + "_press").Length == 0 && MenuState.Cvars.GetString(s + "_release").Length == 0)
                color = MenuSkin.Fade(color, color.A * MenuSkin.DisabledAlpha);
        }

        float iconAlpha = MenuSkin.SelectionAlpha;
        if (e.Tree != ' ')
        {
            string pic = e.Tree == '+' ? "tree_branch" : e.Tree == 'T' ? "tree_branch_start" : "tree_elbow";
            if (QcDraw.Skin(pic) is { } tree)
                DrawTextureRect(tree, new Rect2(rect.Position + new Vector2(extraMargin, 0f), new Vector2(h, h)), false,
                    MenuSkin.Fade(Colors.White, iconAlpha));
            float added = h + 0.25f * ch;
            extraMargin += added;
            descrWidth -= added;
        }
        if (e.Icon is not null)
        {
            // columnIconSize: the row's height at the first weapon icon's aspect.
            Texture2D? icon = WeaponHud.Icon(e.Icon);
            Texture2D? first = FirstWeaponIcon() ?? icon;
            float aspect = first is not null && first.GetHeight() > 0 ? (float)first.GetWidth() / first.GetHeight() : 1f;
            if (icon is not null)
                DrawTextureRect(icon, new Rect2(rect.Position + new Vector2(extraMargin, 0f), new Vector2(h * aspect, h)), false,
                    MenuSkin.Fade(Colors.White, iconAlpha));
            float added = h * aspect + 0.25f * ch;
            extraMargin += added;
            descrWidth -= added;
        }

        float y = rect.Position.Y + 0.5f * (h - ch);
        QcDraw.Text(this, new Vector2(rect.Position.X + extraMargin, y), QcDraw.ShortenToWidth(descr, descrWidth), color);

        if (e.Func.Length == 0) return;
        string func = e.Func, keys = "";
        if (IsOverrider(func))
        {
            func = BoundFunc(func);
            if (func.Length == 0) return;
        }
        if (IsSpecial(func))
        {
            keys = func[1..];
            color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_KEYGRABBER_KEYS_IMMUTABLE", 0.48f, 0.5f, 0.5f),
                MenuSkin.SkinNumber("ALPHA_KEYGRABBER_KEYS_IMMUTABLE", 0.8f)));
        }
        else
            foreach (string key in KeysFor(func))
                keys = keys.Length == 0 ? BindInput.EngineKeyName(key) : keys + ", " + BindInput.EngineKeyName(key);
        keys = QcDraw.ShortenToWidth(keys, keysSize);
        float kw = QcDraw.TextWidth(keys);
        QcDraw.Text(this, new Vector2(rect.Position.X + keysOrigin + 0.5f * keysSize - 0.5f * kw, y), keys, color);
    }
}
