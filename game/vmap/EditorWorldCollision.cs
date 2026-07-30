using VortexArena.Engine.Collision;
using VortexArena.Formats.Vmap;

namespace VortexArena.Game.Vmap;

/// <summary>
/// Collision for the editor's world, built from the DOCUMENT — the piece that makes PLAYTEST honest. Without
/// it, the editor renders the document while the physics still trace the compiled BSP, and the mapper runs
/// through walls they just built and bounces off walls they just deleted.
///
/// This is a thin gametype-filter wrapper over <see cref="VmapCollisionBuilder"/>: collision must drop the
/// same conditional submodels the render drops (render and collision MUST agree — the GameDemo rule), and the
/// underlying builder deliberately knows nothing about gametype filtering.
/// </summary>
public static class EditorWorldCollision
{
    /// <summary>
    /// Build collision from <paramref name="doc"/>, excluding brushes belonging to submodels in
    /// <paramref name="droppedSubmodels"/> (the gametype-conditional <c>func_wall</c> set).
    /// </summary>
    public static BspCollisionBuilder.Result Build(
        VmapDocument doc, IReadOnlySet<int>? droppedSubmodels)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (droppedSubmodels is not { Count: > 0 })
            return VmapCollisionBuilder.Build(doc);

        // A filtered VIEW, not a copy: the brush objects are shared, so this allocates one list while the
        // session's edits keep flowing through to future rebuilds.
        var filtered = new VmapDocument { Manifest = doc.Manifest };
        foreach (VmapBrush brush in doc.Brushes)
            if (brush.SubmodelIndex == 0 || !droppedSubmodels.Contains(brush.SubmodelIndex))
                filtered.Brushes.Add(brush);
        foreach (VmapPatch patch in doc.Patches)
            filtered.Patches.Add(patch);
        foreach (VmapEntity entity in doc.Entities)
            filtered.Entities.Add(entity);

        return VmapCollisionBuilder.Build(filtered);
    }
}
