using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// The engine-drawn screen overlay — readouts DarkPlaces paints from <c>SCR_DrawScreen</c> rather than from the
/// QuakeC HUD, so they are on screen whatever <c>key_dest</c> is: at the main menu, on the loading screen, in a
/// match, in the settings dialogs. Today that is the <c>showfps</c> counter (<c>Sbar_ShowFPS</c>,
/// Base/darkplaces/sbar.c).
///
/// <para><b>Why it exists.</b> <see cref="FpsPanel"/> is a <see cref="HudPanel"/>, and every HudPanel is created
/// by <see cref="Hud"/> — which <c>NetGame</c> builds when a match starts and frees when it ends. So the port's
/// <c>showfps</c> only worked inside a live match, while Xonotic's works everywhere, because in DP it was never
/// part of the HUD to begin with: <c>Sbar_ShowFPS</c> is drawn by the engine's screen pass, one layer under the
/// console. This layer restores that. <see cref="HudRegistry.EngineGlobal"/> keeps the in-game HUD from
/// creating a second copy.</para>
///
/// <para>Layer 120 puts it over the menu (10), the HUD (5), the chat prompt (90) and the loading screen (100),
/// and under the console (128) — DP's ordering, where the drop-down console is the last thing drawn. Being its
/// own layer also means the readout does NOT inherit the HUD's damage shake, which is correct: an engine
/// overlay never shook in DP.</para>
///
/// <para>The panel is otherwise unchanged — it still self-manages its visibility from <c>cl_showfps</c> and
/// redraws only when the displayed number changes. All this node does is keep it alive and hand it the
/// viewport rect each frame, which is exactly what <see cref="Hud"/> did for the self-managed panels.</para>
/// </summary>
public partial class EngineOverlay : CanvasLayer
{
    /// <summary>The framerate readout (DP <c>Sbar_ShowFPS</c>), gated on <c>cl_showfps</c>/<c>showfps</c>.</summary>
    public FpsPanel Fps { get; private set; } = null!;

    public override void _Ready()
    {
        Layer = 120;                              // over menu/HUD/chat/loading, under the console (128)
        ProcessMode = ProcessModeEnum.Always;     // keeps counting while the pause menu freezes the tree

        // Starts hidden, as it did under Hud's StartHiddenIds: the panel's own _Process turns it on from
        // cl_showfps on the first frame, so a run with the readout off never flashes one.
        Fps = new FpsPanel { Name = "Fps", Visible = false };
        AddChild(Fps);
    }

    public override void _Process(double delta)
    {
        using var _scope = VortexArena.Game.Client.FrameProfiler.Scope("engineoverlay");
        // The self-managed panels take the full viewport as their layout rect (what Hud._Process passes them);
        // FpsPanel right-aligns against that rect's edge. Resolving it every frame is what keeps the readout
        // pinned to the corner across a resolution change.
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        Fps.LoadConfig(vp, 1f, 1f);
    }
}
