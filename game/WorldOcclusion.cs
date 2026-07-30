using Godot;

namespace VortexArena.Game;

/// <summary>
/// (§12.8 A/B) Runtime switch for Godot's native, view-dependent occlusion culling — an experimental
/// alternative to the compiled-PVS <see cref="WorldPvsCuller"/>. It owns one <see cref="OccluderInstance3D"/>
/// built at map load from the OPAQUE world surfaces (see <c>MapLoader.BuildWorldOccluder</c>) and toggles it,
/// plus the game viewport's <see cref="Viewport.UseOcclusionCulling"/> flag, from the <c>r_occlusion_cull</c>
/// cvar. The engine then software-rasterizes the occluder into a depth buffer each frame and culls any object
/// whose AABB is fully behind it.
///
/// <para><b>Orthogonal to the PVS culler.</b> This and <see cref="WorldPvsCuller"/> read independent cvars
/// (<c>r_occlusion_cull</c> / <c>r_pvs_cull</c>), so the four A/B combinations — off, PVS only, occlusion only,
/// both — are just the 2×2 of the two toggles. Default <c>r_occlusion_cull 0</c> keeps today's behavior.</para>
///
/// <para><b>Why not brushes?</b> The BSP solid brushes were rejected as the occluder source: finalrage alone
/// carries ~17.6k bevel-padded solid brushes (hundreds of thousands of triangles) versus ~24k render
/// triangles. The render mesh is both lower-poly and guaranteed conservative (it IS the solid surface), so it
/// never over-occludes. Translucent/liquid/nonsolid/sky surfaces are excluded when the occluder is built.</para>
/// </summary>
public sealed partial class WorldOcclusion : Node
{
    private readonly Occluder3D _occluder;
    private OccluderInstance3D? _instance;
    private bool? _lastEnabled;   // null → first _Process always applies

    public WorldOcclusion(Occluder3D occluder)
    {
        Name = "WorldOcclusion";
        _occluder = occluder;
    }

    public override void _Ready()
    {
        // Escape hatch / A-B toggle. Deliberately NOT archived (no CvarFlags.Save): an experiment pin must not
        // silently persist into the player's config.cfg. Default OFF: the PVS culler stays the shipping path
        // until this is measured. Mirrors WorldPvsCuller's defensive self-registration.
        VortexArena.Game.Menu.MenuState.Cvars.Register("r_occlusion_cull", "0");

        _instance = new OccluderInstance3D { Name = "WorldOccluder", Occluder = _occluder };
        AddChild(_instance);
        Apply(false);   // start inert; _Process flips it on if the cvar is set (no first-frame flash)
    }

    public override void _Process(double delta)
    {
        using var _prof = VortexArena.Game.Client.FrameProfiler.Scope("clientmisc");
        bool enabled = VortexArena.Game.Menu.MenuState.Cvars.GetFloat("r_occlusion_cull") != 0f;
        if (enabled == _lastEnabled)
            return;
        _lastEnabled = enabled;
        Apply(enabled);
    }

    private void Apply(bool enabled)
    {
        if (_instance is not null && GodotObject.IsInstanceValid(_instance))
            _instance.Visible = enabled;   // a hidden occluder contributes nothing to the depth buffer
        if (GetViewport() is { } vp)
            vp.UseOcclusionCulling = enabled;   // per-viewport opt-in; the menu viewport is left untouched
    }
}
