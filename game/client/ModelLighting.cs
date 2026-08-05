using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Loaders;
using VortexArena.Common.Diagnostics;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// The map-wide model-lighting bindings: the baked lightgrid as a GPU 3-D texture, plus the scalars the skin
/// shader needs to sample it. This is the C# half of <b>F1-B</b> — DarkPlaces'
/// <c>mod_q3bsp_lightgrid_texture</c> path, which is what DP does by default.
///
/// <para><b>Why a global shader parameter.</b> Every model in the scene needs the same grid, and skin
/// materials are shared/cached across entities — so binding the texture per material would either defeat the
/// sharing or require tracking every live material. A Godot <i>global</i> shader parameter is broadcast to
/// every material that declares it, so one <see cref="RenderingServer.GlobalShaderParameterSet"/> per map
/// change lights the whole cast. Same mechanism <see cref="WorldTint"/> uses for the map/entity tints.</para>
///
/// <para><b>Lifetime.</b> <see cref="ApplyMap"/> on map load, <see cref="Clear"/> on teardown. The previous
/// map's texture is dropped on each apply — a light grid is single-digit MB and there is no reason to keep a
/// map's grid alive after leaving it.</para>
///
/// <para><b>The fallback is not a failure path.</b> A map with no lump 15, a grid too large for
/// <see cref="LightGridTexture"/>'s cap, or a driver that refuses the 3-D texture all leave
/// <c>lightgrid_params.w = 0</c>, and the skin shader's lobe 1 folds away to nothing. What is left is the
/// per-entity CPU sample on lobe 2 — the pre-F1-B behaviour, bit for bit.</para>
/// </summary>
public static class ModelLighting
{
    /// <summary>DP's model-light scale: a grid byte of 128 reads as 1.0 (<c>Mod_Q3BSP_LightPoint</c>'s
    /// stylescale). The GPU path samples the texture as 0..1 rather than 0..255, so the constant carries the
    /// 255 back: <c>(raw/255) · (255/128) == raw/128</c>, matching the CPU path exactly.</summary>
    private const float DpByteScale = 255f / 128f;

    private static bool _registered;
    private static LightGridTexture? _current;
    private static ImageTexture3D? _dummy;

    /// <summary>
    /// Register the three global shader parameters. MUST run before any shader that declares them compiles,
    /// or Godot reports an unknown-global error and the surface fails to render — so this is called from
    /// <see cref="WorldTint.EnsureRegistered"/>, which <c>Main._Ready</c> already runs before the first map.
    ///
    /// <para>The sampler is registered with a 1×1×3 neutral dummy rather than left unbound: an unbound global
    /// sampler is a compile-time diagnostic on some drivers even when every read is behind a branch that is
    /// never taken.</para>
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;

        RenderingServer.GlobalShaderParameterAdd(
            PlayerSkinShader.LightGridTexUniform,
            RenderingServer.GlobalShaderParameterType.Sampler3D,
            DummyTexture());
        RenderingServer.GlobalShaderParameterAdd(
            PlayerSkinShader.LightGridMatrixUniform,
            RenderingServer.GlobalShaderParameterType.Mat4,
            Projection.Identity);
        // (zmin, zmax, scale, enabled) — enabled 0 = no grid, lobe 1 contributes nothing.
        RenderingServer.GlobalShaderParameterAdd(
            PlayerSkinShader.LightGridParamsUniform,
            RenderingServer.GlobalShaderParameterType.Vec4,
            new Vector4(0f, 0f, 1f, 0f));
    }

    /// <summary>A 1×1×3 all-black 3-D texture — the neutral bind when no map grid is loaded.</summary>
    private static ImageTexture3D DummyTexture()
    {
        if (_dummy is not null)
            return _dummy;
        var slices = new Godot.Collections.Array<Image>();
        for (int i = 0; i < 3; i++)
            slices.Add(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { 0, 0, 0, 255 }));
        var t = new ImageTexture3D();
        t.Create(Image.Format.Rgba8, 1, 1, 3, useMipmaps: false, slices);
        return _dummy = t;
    }

    /// <summary>True when a real map grid is currently bound (lobe 1 is live).</summary>
    public static bool HasGrid => _current is not null;

    /// <summary>
    /// Build and bind the lightgrid texture for <paramref name="grid"/>. Passing null (or a grid the packer
    /// refuses) unbinds and leaves every model on the per-entity CPU path.
    /// </summary>
    public static void ApplyMap(LightGridData? grid)
    {
        EnsureRegistered();
        _pending = grid;
        // r_model_lightgrid is the port name for DP mod_q3bsp_lightgrid_texture (whose default is also 1):
        // 0 forces every model back onto the per-entity CPU sample. That is the A/B lever for judging what
        // the per-pixel path actually buys, and the escape hatch if a driver dislikes the 3-D texture.
        _current = GridEnabled() ? LightGridTexture.Build(grid) : null;

        if (_current is null)
        {
            RenderingServer.GlobalShaderParameterSet(PlayerSkinShader.LightGridTexUniform, DummyTexture());
            RenderingServer.GlobalShaderParameterSet(PlayerSkinShader.LightGridMatrixUniform, Projection.Identity);
            RenderingServer.GlobalShaderParameterSet(
                PlayerSkinShader.LightGridParamsUniform, new Vector4(0f, 0f, 1f, 0f));
            Log.Info(grid is null
                ? "[LightGrid] this map ships no light grid (BSP lump 15) — models use the PBR + sun fallback."
                : "[LightGrid] GPU grid unavailable for this map — models fall back to per-entity sampling.");
            return;
        }

        RenderingServer.GlobalShaderParameterSet(PlayerSkinShader.LightGridTexUniform, _current.Texture);
        RenderingServer.GlobalShaderParameterSet(
            PlayerSkinShader.LightGridMatrixUniform, _current.WorldToTexture);
        PushParams();

        Log.Info($"[LightGrid] GPU lightgrid {_current.Width}x{_current.Height}x{_current.Depth} " +
                 $"({_current.Bytes / 1024} KB) bound — per-pixel model lighting active.");
    }

    /// <summary>Unbind (map teardown). Models return to the per-entity path until the next map binds a grid.</summary>
    public static void Clear() => ApplyMap(null);

    /// <summary>
    /// Re-push the sample scalars. Called on map load and whenever <c>r_model_light_scale</c> changes, so the
    /// brightness knob is live in the console like the tint cvars are.
    /// </summary>
    public static void PushParams()
    {
        if (_current is null)
            return;
        // z clamp: the DATA slices of block 0 are texture slices 1..Nz (slice 0 and Nz+1 are the padding that
        // gives "outside the grid" its black falloff). Clamp to their centres so a sample outside the grid
        // lands on real data at the boundary rather than half-blending into the padding.
        int blockSlices = _current.Depth / 3;
        int nz = blockSlices - 2;
        float zmin = 1.5f / _current.Depth;
        float zmax = (nz + 0.5f) / _current.Depth;
        RenderingServer.GlobalShaderParameterSet(
            PlayerSkinShader.LightGridParamsUniform,
            new Vector4(zmin, zmax, DpByteScale * UserScale(), 1f));
    }

    /// <summary>The user's <c>r_model_light_scale</c> brightness multiplier (unset/&lt;=0 → 1).</summary>
    public static float UserScale()
    {
        string s = MenuState.Cvars.GetString("r_model_light_scale");
        if (string.IsNullOrWhiteSpace(s))
            return 1f;
        float v = MenuState.Cvars.GetFloat("r_model_light_scale");
        return v <= 0f ? 1f : v;
    }

    /// <summary>Poll <c>r_model_light_scale</c> and re-push only when it actually moved (per client frame).</summary>
    public static void PollCvars()
    {
        PollGridToggle();
        if (_current is null)
            return;
        float s = UserScale();
        if (Mathf.IsEqualApprox(s, _appliedScale))
            return;
        _appliedScale = s;
        PushParams();
    }

    private static float _appliedScale = 1f;

    /// <summary>The last map grid, kept so a live <c>r_model_lightgrid</c> toggle can rebuild the texture
    /// without a map reload.</summary>
    private static LightGridData? _pending;

    private static bool _appliedEnabled = true;

    /// <summary><c>r_model_lightgrid</c> (DP <c>mod_q3bsp_lightgrid_texture</c>) - 1 (default) = per-pixel
    /// GPU grid, 0 = per-entity CPU sample. Unset reads as ON, matching DP.</summary>
    public static bool GridEnabled()
    {
        string s = MenuState.Cvars.GetString("r_model_lightgrid");
        return string.IsNullOrWhiteSpace(s) || MenuState.Cvars.GetFloat("r_model_lightgrid") != 0f;
    }

    /// <summary>Poll <c>r_model_lightgrid</c>; a change rebuilds (or drops) the texture in place. Instances
    /// keep their <c>grid_lit</c> flag either way - with the texture gone lobe 1 folds away and lobe 2 carries
    /// the model, the same fallback a grid-less map takes.</summary>
    private static void PollGridToggle()
    {
        bool on = GridEnabled();
        if (on == _appliedEnabled)
            return;
        _appliedEnabled = on;
        ApplyMap(_pending);
    }
}
