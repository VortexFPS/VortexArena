using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VortexArena.Game.Hud;

/// <summary>
/// Reflection-driven panel discovery — the C# successor to QuakeC's <c>REGISTER_HUD_PANEL</c> registry
/// (Base/.../qcsrc/client/hud/hud.qh), which at load time built the static <c>hud_panels</c> list the HUD loop
/// walked. Here we reflect every concrete <see cref="HudPanel"/> subclass in the loaded assembly ONCE and hand
/// the ordered type list to <see cref="Hud"/>, which instantiates one of each. The payoff is the same property
/// the QC macro bought: a panel exists simply by being a class — no central list to edit — so new panels can be
/// added in their own files and fan out in parallel without ever touching <see cref="Hud"/>.
///
/// <see cref="MinigameRenderer"/> / <see cref="MinigameMenu"/> are deliberately NOT <see cref="HudPanel"/>s
/// (they capture clicks/keys) so they are not discovered here; the manager adds them directly.
/// </summary>
public static class HudRegistry
{
    // QC _hud_panelorder draw priority (lower = drawn earlier = further back). Anything not listed sorts after,
    // alphabetically by id; the manager forces true overlays (scoreboard, mapvote, quickmenu, chat) to the top
    // regardless. This only affects sibling z-order for the rare overlapping panels.
    private static readonly string[] Order =
    {
        "radar", "weapons", "ammo", "powerups", "healtharmor", "notify", "timer", "score", "racetimer",
        "vote", "modicons", "pressedkeys", "engineinfo", "infomessages", "physics", "strafehud", "pickup",
        "centerprint", "itemstime", "checkpoints", "crosshair", "vehicle", "fps", "ping", "position",
        // The editor readouts sit above the world-state panels but below the true modal overlays: the action
        // line rides just over the crosshair, and the context menu must draw over everything it covers or its
        // rows would be unreadable against a lit wall.
        "editoraction", "editor", "minigamehelp", "quickmenu", "editormenu", "editordialog",
        "chat", "mapvote", "scoreboard",
    };

    private static List<Type>? _types;

    /// <summary>All concrete <see cref="HudPanel"/> subclasses, in draw order (built once, cached).</summary>
    public static IReadOnlyList<Type> PanelTypes => _types ??= Build();

    private static List<Type> Build()
    {
        var found = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(HudPanel).IsAssignableFrom(t))
            .ToList();

        int Rank(Type t)
        {
            string id = HudLayoutDefaults.DeriveId(t);
            int i = Array.IndexOf(Order, id);
            return i < 0 ? Order.Length : i;
        }

        return found
            .OrderBy(Rank)
            .ThenBy(t => HudLayoutDefaults.DeriveId(t), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Instantiate a discovered panel type (parameterless ctor, like the old <c>new XxxPanel()</c>).</summary>
    public static HudPanel Create(Type t) => (HudPanel)Activator.CreateInstance(t)!;
}
