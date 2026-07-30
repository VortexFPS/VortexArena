// Port of Base/data/xonotic-data.pk3dir/qcsrc/client/shownames.qh + the hud_shownames_* block of
// Base/data/xonotic-data.pk3dir/_hud_common.cfg (lines 331-349).
using Godot;
using VortexArena.Common.Services;          // CvarFlags
using VortexArena.Engine.Simulation;         // CvarService (RegisterDefaults)

namespace VortexArena.Game.Hud;

/// <summary>
/// The shownames "panel" — the cvar/identity shell for the floating player name + health/armor tags
/// (QC <c>client/shownames.qc</c> <c>Draw_ShowNames</c>/<c>Draw_ShowNames_All</c>). Unlike a normal HUD panel,
/// shownames is NOT a screen-space rectangle: it is a 3D in-world overlay drawn per visible player, projected
/// through the first-person camera. That draw lives in <see cref="VortexArena.Game.Client.ShowNamesLayer"/>
/// (the analogue of <see cref="VortexArena.Game.Client.WaypointSpriteLayer"/>), added by the net layer.
///
/// <para>This class exists so the <c>hud_shownames_*</c> cvars (which live in <c>shownames.qh</c> as
/// <c>autocvar_hud_shownames_*</c>, seeded by <c>_hud_common.cfg</c>) are registered centrally — it is
/// auto-discovered by <see cref="HudRegistry"/> and its <see cref="RegisterDefaults"/> is invoked by reflection
/// from <see cref="HudConfig"/>, exactly like every other panel's behaviour-cvar registrar. The shownames cvars
/// are GLOBAL (<c>hud_shownames_…</c>, not <c>hud_panel_shownames_…</c>), so they are registered by full name.</para>
///
/// <para>Because shownames has no luma layout entry, <see cref="HudLayoutDefaults.For"/> hands it the harmless
/// full-screen / no-frame fallback; <see cref="DrawPanel"/> is a deliberate no-op so the discovered panel paints
/// nothing on the 2D HUD layer (all real drawing is the in-world <see cref="VortexArena.Game.Client.ShowNamesLayer"/>).
/// The id is not in <c>HudRegistry.Order</c> / <c>StartHiddenIds</c>, so it simply sits inert in the panel set.</para>
/// </summary>
public partial class ShownamesPanel : HudPanel
{
    /// <summary>QC <c>panel.panel_name</c> equivalent. Not a real luma panel — see the class summary.</summary>
    public override string PanelId => "shownames";

    /// <summary>Nothing to redraw on the 2D HUD layer — the real draw is the in-world overlay. Suppress the
    /// per-frame QueueRedraw the dynamic-panel default would schedule.</summary>
    public override bool IsDynamic => false;

    /// <summary>No 2D panel chrome/content (the floating tags are drawn in 3D by
    /// <see cref="VortexArena.Game.Client.ShowNamesLayer"/>). Intentionally empty.</summary>
    protected override void DrawPanel() { }

    /// <summary>
    /// Register the <c>hud_shownames_*</c> cvar defaults — the C# successor to the <c>seta hud_shownames*</c>
    /// block in <c>_hud_common.cfg</c> (lines 331-349) plus the two <c>shownames.qh</c> initialisers
    /// (<c>statusbar_highlight = 1</c>, <c>antioverlap_minalpha = 0.4</c>). Invoked by reflection from
    /// <see cref="HudConfig.RegisterDefaults"/>; <c>Register</c> is idempotent so a loaded cfg / user seta wins.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags S = CvarFlags.Save;
        c.Register("hud_shownames", "1", S);                               // _hud_common.cfg:331
        c.Register("hud_shownames_enemies", "1", S);                       // :332
        c.Register("hud_shownames_crosshairdistance", "0", S);             // :333
        c.Register("hud_shownames_crosshairdistance_time", "5", S);        // :334
        c.Register("hud_shownames_crosshairdistance_antioverlap", "0", S); // :335
        c.Register("hud_shownames_self", "0", S);                          // :336
        c.Register("hud_shownames_status", "1", S);                        // :337
        c.Register("hud_shownames_statusbar_height", "4", S);              // :338
        c.Register("hud_shownames_statusbar_highlight", "1", S);           // :339 / shownames.qh:11
        c.Register("hud_shownames_aspect", "8", S);                        // :340
        c.Register("hud_shownames_fontsize", "12", S);                     // :341
        c.Register("hud_shownames_decolorize", "1", S);                    // :342
        c.Register("hud_shownames_alpha", "0.7", S);                       // :343
        c.Register("hud_shownames_resize", "1", S);                        // :344
        c.Register("hud_shownames_mindistance", "1000", S);               // :345
        c.Register("hud_shownames_maxdistance", "5000", S);               // :346
        c.Register("hud_shownames_antioverlap", "1", S);                  // :347
        c.Register("hud_shownames_antioverlap_minalpha", "0.4", S);      // :348 / shownames.qh:20
        c.Register("hud_shownames_offset", "52", S);                      // :349
    }
}
