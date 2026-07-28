using Godot;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Builds Godot render geometry from an editable <see cref="VmapDocument"/> — the <c>.vmap</c> counterpart of
/// <see cref="MapLoader.BuildMap"/> (design doc §11.2, phase E0).
///
/// The critical difference from the BSP path: nothing here is precompiled. Brush planes are evaluated into
/// polygons by <see cref="VmapGeometryBuilder"/> every build, which is exactly what makes an edited map
/// viewable immediately — no compile step between dragging a wall and seeing it.
///
/// Geometry arrives in Quake space and is converted per-vertex through <see cref="Coords.ToGodot"/>. That
/// mapping is orientation-preserving (determinant +1), so the counter-clockwise-from-outside winding the
/// geometry builder guarantees survives into Godot as correct front faces.
/// </summary>
public static class VmapMapBuilder
{
    /// <summary>
    /// World-space cell edge in Quake units for the frustum-culling split. Mirrors
    /// <see cref="MapLoader"/>'s default: without a spatial split every material would be one map-spanning
    /// MeshInstance3D whose AABB never leaves the frustum, so nothing would ever cull.
    /// </summary>
    public const float CellSize = 1024f;

    /// <summary>
    /// Build the render tree: a root <see cref="Node3D"/> with one <see cref="MeshInstance3D"/> per occupied
    /// spatial cell, each holding one mesh surface per material present in that cell.
    /// </summary>
    /// <param name="doc">The truth document.</param>
    /// <param name="assets">Material facade (resolves shader names to Godot materials).</param>
    /// <param name="options">
    /// Which brushes take part and whether buried faces are removed. Defaults to the editor's world view:
    /// gametype-filtered, occlusion-culled, no sky.
    /// </param>
    /// <param name="lit">
    /// Build materials that RESPOND to light (design doc §10.1 rung 1) rather than fullbright ones. The
    /// fullbright path stays because it is the only thing that is never wrong while geometry is in motion,
    /// and because a map whose lights did not survive compilation has nothing to light it with.
    /// </param>
    public static Node3D BuildMap(VmapDocument doc, AssetSystem assets, VmapSurfaceOptions? options = null,
        bool lit = false)
    {
        Lit = lit;
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(assets);

        options ??= new VmapSurfaceOptions { CullOccludedFaces = true };

        var root = new Node3D { Name = "VmapWorld" };
        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, options);

        // Bucket every triangle into its spatial cell, keyed by cell then material.
        var cells = new Dictionary<(int X, int Y, int Z), Dictionary<string, CellSurface>>();

        // Warpzone windows and their pocket decor are pulled OUT of the cell meshes into their own addressable
        // nodes, exactly as MapLoader does — PortalRenderer finds them by node metadata and swaps a live
        // see-through render onto the window. Left in the merged cell mesh they are unaddressable, so the
        // warpzone draws its flat placeholder shader and the portal never appears.
        var portalRoot = new Node3D { Name = "Portals" };

        foreach (VmapSurface surface in surfaces)
        {
            if (IsPortalCameraShader(assets, surface.Material))
            {
                AppendPortalNodes(portalRoot, surface, assets, decor: false);
                continue;
            }
            if (IsWarpzoneDecorShader(assets, surface.Material))
            {
                AppendPortalNodes(portalRoot, surface, assets, decor: true);
                continue;
            }

            for (int i = 0; i + 2 < surface.Indices.Count; i += 3)
            {
                int i0 = surface.Indices[i], i1 = surface.Indices[i + 1], i2 = surface.Indices[i + 2];
                NVec3 p0 = surface.Positions[i0], p1 = surface.Positions[i1], p2 = surface.Positions[i2];

                (int, int, int) key = CellKey((p0 + p1 + p2) / 3f);
                if (!cells.TryGetValue(key, out Dictionary<string, CellSurface>? byMaterial))
                {
                    byMaterial = new Dictionary<string, CellSurface>(StringComparer.Ordinal);
                    cells[key] = byMaterial;
                }
                if (!byMaterial.TryGetValue(surface.Material, out CellSurface? cell))
                {
                    cell = new CellSurface(surface.Material);
                    byMaterial[surface.Material] = cell;
                }

                if (EditorLightBake.Active)
                    AppendBakedTriangle(cell, surface, i0, i1, i2);
                else
                    cell.AddTriangle(surface, i0, i1, i2);
            }
        }

        // ---- bake the vertex lighting ACROSS CORES -----------------------------------------------------
        // Every vertex is independent and the light/occluder indices are read-only, so this is the one place
        // in the build that parallelises perfectly — and it is where the shadow rays are spent.
        if (EditorLightBake.Active)
        {
            var bakeCells = new List<CellSurface>();
            foreach (Dictionary<string, CellSurface> byMat in cells.Values)
                bakeCells.AddRange(byMat.Values);
            System.Threading.Tasks.Parallel.ForEach(bakeCells, static cell => cell.BakeColors());

            // Radiosity's shoot/gather, once: what the direct pass RECEIVED becomes virtual emitters, and a
            // second pass adds their glow. This is what keeps traced shadows from being pitch black.
            if (EditorLightBake.BounceActive && EditorLightBake.BuildBounceLights() > 0)
                System.Threading.Tasks.Parallel.ForEach(bakeCells, static cell => cell.AddBounceColors());
        }

        // Deterministic node order so two builds of the same document produce an identical tree.
        foreach ((int X, int Y, int Z) key in cells.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z))
        {
            Dictionary<string, CellSurface> byMaterial = cells[key];
            var mesh = new ArrayMesh();
            var materials = new List<Material>(byMaterial.Count);

            foreach (string material in byMaterial.Keys.OrderBy(m => m, StringComparer.Ordinal))
            {
                CellSurface cell = byMaterial[material];
                if (cell.Indices.Count == 0)
                    continue;
                cell.Pack(mesh);
                materials.Add(EditorMaterial(assets, material));
            }

            if (mesh.GetSurfaceCount() == 0)
                continue;

            var instance = new MeshInstance3D
            {
                Name = $"VmapCell_{key.X}_{key.Y}_{key.Z}",
                Mesh = mesh,
                // SDFGI only sees geometry that opts in. Static is right even though the map is being edited:
                // it describes how the surface participates in GI, and a rebuild re-registers it anyway.
                GIMode = GeometryInstance3D.GIModeEnum.Static,
                // DOUBLE-SIDED shadows, though the materials are cull_back. The occlusion-culled world keeps
                // only the visible skin, so a roof exists solely as its interior ceiling face — which, seen
                // from the sun outside the map, is a backface. A one-sided shadow pass skips it and the sun
                // pours straight through the roof, while walls whose exterior faces happened to survive DO
                // block: exactly the "wrong geometry casts the shadow" symptom. Double-sided casting makes
                // every kept face a blocker from both sides.
                CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided,
            };
            for (int s = 0; s < materials.Count; s++)
                instance.SetSurfaceOverrideMaterial(s, materials[s]);

            root.AddChild(instance);
        }

        if (portalRoot.GetChildCount() > 0)
            root.AddChild(portalRoot);
        else
            portalRoot.QueueFree();

        return root;
    }

    /// <summary>
    /// Tessellate one source triangle for the bake by clipping it against a WORLD-ALIGNED grid on its plane,
    /// sampling light at every generated vertex.
    ///
    /// Grid clipping, not barycentric subdivision, and the difference is visible: a big floor polygon fans
    /// into long thin triangles from one corner, and splitting those barycentrically keeps them thin — one
    /// shadowed vertex then smears a dark wedge down the whole sliver, which is exactly the streaky artefact
    /// reported in playtest. Clipping against fixed world planes cuts every face into compact, roughly
    /// square pieces whose vertices sit where a lightmap's luxels would, and because the planes are the same
    /// for every face, samples agree along shared edges.
    /// </summary>
    private static void AppendBakedTriangle(CellSurface cell, VmapSurface surface, int i0, int i1, int i2)
    {
        NVec3 a = surface.Positions[i0], b = surface.Positions[i1], c = surface.Positions[i2];
        NVec2 ua = surface.Uvs[i0], ub = surface.Uvs[i1], uc = surface.Uvs[i2];
        NVec3 normal = surface.Normals[i0];
        Vector3 gn = Coords.ToGodot(normal);

        // The two world axes spanning the face's dominant plane.
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);
        int axisU, axisV;
        if (az >= ax && az >= ay) { axisU = 0; axisV = 1; }
        else if (ax >= ay) { axisU = 1; axisV = 2; }
        else { axisU = 0; axisV = 2; }

        // Barycentric basis for interpolating UVs at generated vertices, in the projected plane.
        float e1u = Axis(b, axisU) - Axis(a, axisU), e1v = Axis(b, axisV) - Axis(a, axisV);
        float e2u = Axis(c, axisU) - Axis(a, axisU), e2v = Axis(c, axisV) - Axis(a, axisV);
        float det = e1u * e2v - e2u * e1v;
        bool canInterp = MathF.Abs(det) > 1e-6f;

        float minU = MathF.Min(Axis(a, axisU), MathF.Min(Axis(b, axisU), Axis(c, axisU)));
        float maxU = MathF.Max(Axis(a, axisU), MathF.Max(Axis(b, axisU), Axis(c, axisU)));
        float minV = MathF.Min(Axis(a, axisV), MathF.Min(Axis(b, axisV), Axis(c, axisV)));
        float maxV = MathF.Max(Axis(a, axisV), MathF.Max(Axis(b, axisV), Axis(c, axisV)));

        // A map-spanning face is clipped at coarser spacing rather than into thousands of pieces.
        float spacingU = MathF.Max(EditorLightBake.SampleSpacing, (maxU - minU) / 40f);
        float spacingV = MathF.Max(EditorLightBake.SampleSpacing, (maxV - minV) / 40f);

        var strip = new List<NVec3>(8);
        var strip2 = new List<NVec3>(8);
        var piece = new List<NVec3>(8);
        var final = new List<NVec3>(8);
        var tri = new List<NVec3> { a, b, c };

        int u0 = (int)MathF.Floor(minU / spacingU), u1 = (int)MathF.Ceiling(maxU / spacingU);
        for (int uu = u0; uu < u1; uu++)
        {
            ClipAxis(tri, strip, axisU, uu * spacingU, keepAbove: true);
            if (strip.Count < 3) continue;
            ClipAxis(strip, strip2, axisU, (uu + 1) * spacingU, keepAbove: false);
            if (strip2.Count < 3) continue;

            int v0 = (int)MathF.Floor(minV / spacingV), v1c = (int)MathF.Ceiling(maxV / spacingV);
            for (int vv = v0; vv < v1c; vv++)
            {
                ClipAxis(strip2, piece, axisV, vv * spacingV, keepAbove: true);
                if (piece.Count < 3) continue;
                ClipAxis(piece, final, axisV, (vv + 1) * spacingV, keepAbove: false);
                if (final.Count < 3) continue;

                // Fan the (convex) piece; colours stay black here and are filled by the parallel passes.
                (Vector3, Vector3, Vector2, Color) Vtx(NVec3 pt)
                {
                    NVec2 uv;
                    if (canInterp)
                    {
                        float pu = Axis(pt, axisU) - Axis(a, axisU);
                        float pv = Axis(pt, axisV) - Axis(a, axisV);
                        float w1 = (pu * e2v - e2u * pv) / det;
                        float w2 = (e1u * pv - pu * e1v) / det;
                        uv = ua + (ub - ua) * w1 + (uc - ua) * w2;
                    }
                    else
                        uv = ua;
                    return (Coords.ToGodot(pt), gn, new Vector2(uv.X, uv.Y), Colors.Black);
                }

                (Vector3, Vector3, Vector2, Color) first = Vtx(final[0]);
                for (int t = 1; t + 1 < final.Count; t++)
                    cell.AddBakedTriangle(first, Vtx(final[t]), Vtx(final[t + 1]));
            }
        }
    }

    private static float Axis(NVec3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    /// <summary>Sutherland-Hodgman clip of a convex polygon against one axis-aligned plane.</summary>
    private static void ClipAxis(List<NVec3> input, List<NVec3> output, int axis, float limit, bool keepAbove)
    {
        output.Clear();
        int n = input.Count;
        for (int i = 0; i < n; i++)
        {
            NVec3 cur = input[i];
            NVec3 nxt = input[(i + 1) % n];
            float dc = Axis(cur, axis) - limit;
            float dn = Axis(nxt, axis) - limit;
            bool inCur = keepAbove ? dc >= 0f : dc <= 0f;
            bool inNxt = keepAbove ? dn >= 0f : dn <= 0f;

            if (inCur)
                output.Add(cur);
            if (inCur != inNxt)
            {
                float t = dc / (dc - dn);
                output.Add(cur + (nxt - cur) * t);
            }
        }
    }

    /// <summary>
    /// A shader that camera-renders a portal view (<c>dpcamera</c>, e.g. <c>effects_warpzone/wavy</c>).
    /// Same authority as <see cref="MapLoader"/>: the parsed shader def, with the name patterns as the
    /// fallback for a portal-ish shader that has no script.
    /// </summary>
    private static bool IsPortalCameraShader(AssetSystem assets, string shaderName)
    {
        string name = (shaderName ?? string.Empty).Replace('\\', '/');
        if (assets.GetShader(name) is { } def)
            return def.Dp.Camera;
        string n = name.ToLowerInvariant();
        return n.Contains("/portals/") || n.StartsWith("portals/", StringComparison.Ordinal)
            || n.Contains("portals_") || n.Contains("mirror");
    }

    /// <summary>
    /// Warpzone pocket DECOR — an <c>effects_warpzone/</c> shader WITHOUT <c>dpcamera</c> (the backdrop and the
    /// blueedge/rededge rims). Drawn normally, but it has to be addressable so the portal cameras — which sit
    /// inside the pocket, behind the exit plane — can cull it; otherwise the backdrop fills the portal view.
    /// </summary>
    private static bool IsWarpzoneDecorShader(AssetSystem assets, string shaderName)
    {
        string name = (shaderName ?? string.Empty).Replace('\\', '/');
        if (!name.ToLowerInvariant().Contains("/effects_warpzone/"))
            return false;
        return assets.GetShader(name) is not { Dp.Camera: true };
    }

    /// <summary>
    /// Emit one node per coplanar group of a portal/decor surface, carrying the metadata contract
    /// <see cref="XonoticGodot.Game.Client.PortalRenderer"/> matches on.
    ///
    /// Grouping by plane is what makes a window ONE node: a warpzone surface is usually several brush faces, and
    /// a node per face would give the renderer several overlapping portals for one opening. Unlike the BSP path
    /// this needs no plane quantisation to recover the grouping — a regenerated face already carries its exact
    /// plane — but the same 8-unit bucketing is kept so faces that differ by float noise still merge.
    /// </summary>
    private static void AppendPortalNodes(Node3D portalRoot, VmapSurface surface, AssetSystem assets, bool decor)
    {
        var groups = new Dictionary<(long, long, long, long), CellSurface>();
        var planes = new Dictionary<(long, long, long, long), (NVec3 NSum, NVec3 OSum, int N)>();

        for (int i = 0; i + 2 < surface.Indices.Count; i += 3)
        {
            int i0 = surface.Indices[i], i1 = surface.Indices[i + 1], i2 = surface.Indices[i + 2];
            NVec3 p0 = surface.Positions[i0], p1 = surface.Positions[i1], p2 = surface.Positions[i2];

            NVec3 n = surface.Normals[i0];
            n = n.LengthSquared() > 1e-9f ? NVec3.Normalize(n) : new NVec3(0f, 0f, 1f);
            NVec3 centre = (p0 + p1 + p2) / 3f;

            var key = (
                (long)MathF.Round(n.X * 32f), (long)MathF.Round(n.Y * 32f), (long)MathF.Round(n.Z * 32f),
                (long)MathF.Round(NVec3.Dot(centre, n) / 8f));

            if (!groups.TryGetValue(key, out CellSurface? group))
            {
                groups[key] = group = new CellSurface(surface.Material);
                planes[key] = (NVec3.Zero, NVec3.Zero, 0);
            }
            group.AddTriangle(surface, i0, i1, i2);

            (NVec3 NSum, NVec3 OSum, int N) acc = planes[key];
            planes[key] = (acc.NSum + n, acc.OSum + centre, acc.N + 1);
        }

        foreach ((long, long, long, long) key in groups.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2)
                     .ThenBy(k => k.Item3).ThenBy(k => k.Item4))
        {
            CellSurface group = groups[key];
            if (group.Indices.Count == 0)
                continue;

            var mesh = new ArrayMesh();
            group.Pack(mesh);
            mesh.SurfaceSetMaterial(0, EditorMaterial(assets, surface.Material));

            (NVec3 NSum, NVec3 OSum, int N) acc = planes[key];
            NVec3 planeN = acc.NSum.LengthSquared() > 1e-9f ? NVec3.Normalize(acc.NSum) : new NVec3(0f, 0f, 1f);
            NVec3 planeO = acc.OSum / Math.Max(1, acc.N);

            var mi = new MeshInstance3D
            {
                Name = decor ? $"PortalDecor_{portalRoot.GetChildCount()}" : $"Portal_{portalRoot.GetChildCount()}",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            if (decor)
            {
                mi.SetMeta("wz_decor", true);
            }
            else
            {
                // QUAKE-space plane, stored raw in a Vector3 holder — PortalRenderer reads these back as Quake
                // and matches them against WarpzoneManager's zones, which are also Quake. NOT Coords-converted.
                mi.SetMeta("wz_origin", new Vector3(planeO.X, planeO.Y, planeO.Z));
                mi.SetMeta("wz_normal", new Vector3(planeN.X, planeN.Y, planeN.Z));
            }

            portalRoot.AddChild(mi);
        }
    }

    /// <summary>Whether the current build wants lit materials; set by <see cref="BuildMap"/>.</summary>
    private static bool Lit;

    /// <summary>
    /// Live multiplier on the baked light (the shader's <c>baked_scale</c>). Calibrated so the bake lands near
    /// the compiled map's own brightness: at 1.0 the editor measured 52.3 mean against the baked reference's
    /// 26.8. Because it is a shader uniform rather than part of the bake, changing it re-lights instantly with
    /// no rebuild.
    /// </summary>
    public static float BakeScale = 0.24f;

    /// <summary>Cache so a shared material is built once per map build, not once per cell. Keyed by shader AND
    /// lit-ness, because the two variants of one shader are different materials.</summary>
    private static readonly Dictionary<string, Material> EditorMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// FULLBRIGHT textured material for the editor world.
    ///
    /// The truth document carries no lightmap UVs — lighting is baked data derived FROM geometry, and the
    /// geometry is what is being edited — so the game's normal lit materials resolve to unlit black here and
    /// the map looks destroyed even though every surface is exactly where it should be. Editors solve this the
    /// same way Radiant does: draw the world fullbright. You lose the lighting, which is meaningless mid-edit
    /// anyway, and you can actually see what you are building.
    /// </summary>
    private static Material EditorMaterial(AssetSystem assets, string shaderName)
    {
        string key = (Lit ? (EditorLightBake.Active ? "baked:" : "lit:") : "flat:") + shaderName;
        if (EditorMaterials.TryGetValue(key, out Material? cached) && GodotObject.IsInstanceValid(cached))
            return cached;

        AssetSystem.LightmapDiffuse diffuse = assets.ResolveLightmapDiffuse(shaderName);
        Texture2D? albedo = diffuse.Texture ?? assets.LoadTexture(shaderName);

        // Baked path: the precomputed light rides in the mesh's COLOR channel, so the surface needs a shader
        // that adds it as emission while leaving ALBEDO for the one real-time light left (the sun).
        if (Lit && EditorLightBake.Active)
        {
            var baked = new ShaderMaterial { Shader = EditorWorldShader.Instance };
            baked.SetShaderParameter("albedo_tex", albedo);
            baked.SetShaderParameter("albedo_tint",
                albedo is null ? new Vector3(0.55f, 0.55f, 0.58f) : Vector3.One);
            baked.SetShaderParameter("uv_scale",
                diffuse.UvScale.X != 0f && diffuse.UvScale.Y != 0f ? diffuse.UvScale : Vector2.One);
            baked.SetShaderParameter("alpha_cutoff", diffuse.AlphaCutoff);
            baked.SetShaderParameter("baked_scale", BakeScale);

            float bakedEmit = SurfaceEmit(assets, shaderName);
            if (diffuse.Glow is not null)
            {
                baked.SetShaderParameter("glow_tex", diffuse.Glow);
                baked.SetShaderParameter("glow_energy", 1.1f);
            }
            else if (bakedEmit > 0f && albedo is not null)
            {
                baked.SetShaderParameter("glow_tex", albedo);
                baked.SetShaderParameter("glow_energy", Math.Clamp(bakedEmit / 1000f, 0.3f, 1.5f));
            }

            EditorMaterials[key] = baked;
            return baked;
        }

        var mat = new StandardMaterial3D
        {
            ShadingMode = Lit ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded,
            // Q3 world surfaces are diffuse masonry and panelling; a default-shiny PBR material would read as
            // wet plastic under real lights.
            Roughness = 0.9f,
            Metallic = 0f,
            AlbedoTexture = albedo,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            // A shader we cannot resolve to an image becomes flat grey rather than invisible: unfound geometry
            // must still be visible and clickable in an editor.
            AlbedoColor = albedo is null ? new Color(0.55f, 0.55f, 0.58f) : Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };

        // The shader's static `tcMod scale`, which the BSP path applies as albedoUvScale. Dropping it does not
        // shift the texture, it RESIZES it — the surface is drawn at the wrong texel density and stops lining
        // up with its neighbours, which looks exactly like a broken texture alignment even though the UVs the
        // importer recovered are correct. Guarded against a zero scale, which would collapse the whole surface
        // onto one texel.
        if (diffuse.UvScale.X != 0f && diffuse.UvScale.Y != 0f)
            mat.Uv1Scale = new Vector3(diffuse.UvScale.X, diffuse.UvScale.Y, 1f);

        // Alpha-tested shaders (grates, ladders, foliage cards) are cut-outs: drawn opaque they become solid
        // rectangles, which reads as geometry the mapper does not have.
        if (diffuse.AlphaCutoff > 0f)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = diffuse.AlphaCutoff;
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;   // cut-outs are authored to be seen both ways
        }
        else if (diffuse.Translucent)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }

        if (Lit)
        {
            // The fixture's own face must GLOW. Two sources, in order:
            //   - the shader's glow companion page (the bright part of the light-panel textures). The BSP path
            //     draws these additively; the lit editor material was dropping them entirely, which is why
            //     light fixtures rendered as dark plates — "the fixtures aren't showing at all".
            //   - for a q3map_surfaceLight shader with no glow page, the albedo itself, scaled by the emit
            //     value. Self-glow only: the light these panels THROW is EditorLighting's surface lights.
            float emit = SurfaceEmit(assets, shaderName);
            if (diffuse.Glow is not null)
            {
                mat.EmissionEnabled = true;
                mat.Emission = Colors.White;
                mat.EmissionTexture = diffuse.Glow;
                // MULTIPLY, not the default ADD. With Add, emission = emission_color + texture, and a white
                // emission colour saturates to 1.0 everywhere the texture is drawn — every light panel became
                // a featureless white rectangle regardless of what its glow page contained. Multiply makes
                // white the identity so the page's own image comes through.
                mat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                // ~1, not the 2x it briefly shipped with: the glow page is already authored at display
                // brightness, and doubling it blew every fixture out to a white rectangle.
                mat.EmissionEnergyMultiplier = 1.1f;
            }
            else if (emit > 0f)
            {
                mat.EmissionEnabled = true;
                mat.Emission = Colors.White;
                mat.EmissionTexture = albedo;
                mat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                mat.EmissionEnergyMultiplier = Math.Clamp(emit / 1000f, 0.3f, 1.5f);
            }
        }

        EditorMaterials[key] = mat;
        return mat;
    }

    /// <summary>
    /// The shader's <c>q3map_surfaceLight</c> emission value, or 0. Falls back to a modest default when the
    /// shader def is missing but the material NAME says it is a surface light (q3map2-era content sometimes
    /// references compiled-in shader variants that have no script on disk).
    /// </summary>
    internal static float SurfaceEmit(AssetSystem assets, string material)
    {
        string name = (material ?? string.Empty).Replace('\\', '/');
        if (assets.GetShader(name) is { } def)
            return def.SurfaceLight ?? 0f;
        return name.Contains("surfacelight", StringComparison.OrdinalIgnoreCase) ? 400f : 0f;
    }

    /// <summary>Drop cached materials (called when a session closes so textures are not pinned forever).</summary>
    public static void ClearMaterialCache() => EditorMaterials.Clear();

    private static (int, int, int) CellKey(NVec3 quakePosition) => (
        (int)MathF.Floor(quakePosition.X / CellSize),
        (int)MathF.Floor(quakePosition.Y / CellSize),
        (int)MathF.Floor(quakePosition.Z / CellSize));

    /// <summary>
    /// Per-(cell, material) vertex accumulator. Vertices are re-indexed as triangles are added, because a
    /// source surface's vertices are shared across cells and each cell needs its own compact buffer.
    /// </summary>
    private sealed class CellSurface
    {
        private readonly Dictionary<int, int> _remap = new();

        public CellSurface(string material) => Material = material;

        public string Material { get; }
        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<Color> Colors { get; } = new();
        public List<int> Indices { get; } = new();

        public void AddTriangle(VmapSurface source, int i0, int i1, int i2)
        {
            Indices.Add(Map(source, i0));
            Indices.Add(Map(source, i1));
            Indices.Add(Map(source, i2));
        }

        /// <summary>
        /// Append one already-subdivided triangle with its own positions — the baked path, where vertices are
        /// generated rather than referenced, so they cannot be shared through the source index remap.
        /// </summary>
        public void AddBakedTriangle(
            (Vector3 P, Vector3 N, Vector2 Uv, Color C) a,
            (Vector3 P, Vector3 N, Vector2 Uv, Color C) b,
            (Vector3 P, Vector3 N, Vector2 Uv, Color C) c)
        {
            foreach ((Vector3 P, Vector3 N, Vector2 Uv, Color C) v in stackalloc[] { a, b, c })
            {
                Indices.Add(Positions.Count);
                Positions.Add(v.P);
                Normals.Add(v.N);
                Uvs.Add(v.Uv);
                Colors.Add(v.C);
            }
        }

        private int Map(VmapSurface source, int sourceIndex)
        {
            if (_remap.TryGetValue(sourceIndex, out int local))
                return local;

            local = Positions.Count;
            _remap[sourceIndex] = local;

            Positions.Add(Coords.ToGodot(source.Positions[sourceIndex]));
            Normals.Add(Coords.ToGodot(source.Normals[sourceIndex]));
            NVec2 uv = source.Uvs[sourceIndex];
            Uvs.Add(new Vector2(uv.X, uv.Y));
            return local;
        }

        /// <summary>
        /// Fill every vertex colour from the light bake. Called on a worker thread — it touches only this
        /// cell's own lists and the bake's read-only indices.
        /// </summary>
        public void BakeColors()
        {
            for (int i = 0; i < Positions.Count; i++)
            {
                NVec3 p = Coords.ToQuake(Positions[i]);
                NVec3 n = Coords.ToQuake(Normals[i]);
                Colors[i] = EditorLightBake.Sample(p, n);
            }
        }

        /// <summary>Add the bounce gather on top of the direct colours (worker-thread safe, own lists only).</summary>
        public void AddBounceColors()
        {
            for (int i = 0; i < Positions.Count; i++)
            {
                NVec3 p = Coords.ToQuake(Positions[i]);
                NVec3 n = Coords.ToQuake(Normals[i]);
                Color bounce = EditorLightBake.SampleBounce(p, n);
                Color c = Colors[i];
                Colors[i] = new Color(c.R + bounce.R, c.G + bounce.G, c.B + bounce.B);
            }
        }

        public void Pack(ArrayMesh mesh)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Positions.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = Normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = Uvs.ToArray();
            if (Colors.Count == Positions.Count && Colors.Count > 0)
                arrays[(int)Mesh.ArrayType.Color] = Colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = Indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
    }
}
