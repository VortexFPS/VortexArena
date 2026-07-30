using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>
/// Packs a document's blend maps onto shared pages (backlog F2) — the CPU half of the same trick the BSP
/// lightmap loader plays, with the image blitting left to the render side.
///
/// The alternative is one texture per painted face, and it is what makes the difference between a feature and
/// an unusable one: a per-face texture means a duplicated material AND its own mesh surface per face, so a
/// terrain wall of two hundred painted faces becomes two hundred draw calls. Sharing a page lets every face
/// with the same material stack batch into one surface, exactly as the lightmap path merges per-page buckets.
///
/// The layout is deterministic — tallest first, id ascending within a row — so two builds of one document
/// produce identical pages, which is the same guarantee the surface builder gives and for the same reason: a
/// rebuild must not shuffle what is already on screen.
/// </summary>
public sealed class VmapBlendAtlas
{
    /// <summary>Where one blend map landed.</summary>
    public readonly record struct Slot(int Page, int X, int Y, int Width, int Height);

    /// <summary>
    /// Page edge in texels. 1024 rather than 4096 because a live paint stroke re-uploads the page it touched,
    /// and a 1024² RGBA8 page is 4 MB per upload where a 4096² one is 64.
    /// </summary>
    public const int DefaultPageSize = 1024;

    /// <summary>
    /// Replicated-edge ring around each slot, so bilinear filtering at a slot's edge cannot pick up the
    /// neighbouring face's weights. The same value and the same hazard as the lightmap atlas.
    /// </summary>
    public const int Gutter = 2;

    private readonly Dictionary<int, Slot> _slots = new();

    private VmapBlendAtlas(int pageSize) => PageSize = pageSize;

    public int PageSize { get; }

    public int PageCount { get; private set; }

    /// <summary>Blend-map id to its slot.</summary>
    public IReadOnlyDictionary<int, Slot> Slots => _slots;

    /// <summary>True when no blend map packed — the common case, and the one that skips all of this.</summary>
    public bool IsEmpty => _slots.Count == 0;

    /// <summary>Shelf-pack every valid blend map in the document.</summary>
    public static VmapBlendAtlas Build(VmapDocument doc, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(doc);
        pageSize = Math.Max(64, pageSize);
        var atlas = new VmapBlendAtlas(pageSize);
        if (doc.BlendMaps.Count == 0)
            return atlas;

        // Tallest first, then by id: height-ordered shelves waste the least, and the id tiebreak is what makes
        // the layout reproducible rather than dependent on list order.
        var ordered = new List<VmapBlendMap>(doc.BlendMaps.Count);
        foreach (VmapBlendMap m in doc.BlendMaps)
            if (m.IsValid)
                ordered.Add(m);
        ordered.Sort((a, b) =>
        {
            int byHeight = b.Height.CompareTo(a.Height);
            return byHeight != 0 ? byHeight : a.Id.CompareTo(b.Id);
        });

        int page = 0, shelfY = 0, shelfH = 0, cursorX = 0;
        foreach (VmapBlendMap m in ordered)
        {
            int w = m.Width + Gutter * 2;
            int h = m.Height + Gutter * 2;

            // A map bigger than a page gets one to itself rather than being clipped. Clipping would look like
            // a painting bug with nothing anywhere to explain it.
            if (w > pageSize || h > pageSize)
            {
                if (cursorX > 0 || shelfY > 0 || shelfH > 0)
                    page++;
                atlas._slots[m.Id] = new Slot(page, Gutter, Gutter, m.Width, m.Height);
                page++;
                shelfY = 0;
                shelfH = 0;
                cursorX = 0;
                continue;
            }

            if (cursorX + w > pageSize)
            {
                cursorX = 0;
                shelfY += shelfH;
                shelfH = 0;
            }
            if (shelfY + h > pageSize)
            {
                page++;
                shelfY = 0;
                shelfH = 0;
                cursorX = 0;
            }

            atlas._slots[m.Id] = new Slot(page, cursorX + Gutter, shelfY + Gutter, m.Width, m.Height);
            cursorX += w;
            shelfH = Math.Max(shelfH, h);
        }

        atlas.PageCount = atlas._slots.Count == 0 ? 0 : page + 1;
        return atlas;
    }

    /// <summary>
    /// Blend-map UV (0-1 over the face) to atlas UV on that map's page.
    ///
    /// Inset by half a texel at each edge, so UV 0 and 1 land on texel CENTRES rather than on the boundary
    /// between a texel and its gutter — without it every painted face gets a half-strength rim.
    /// </summary>
    public bool TryToAtlasUv(int blendMapId, Vector2 faceUv, out int page, out Vector2 atlasUv)
    {
        page = 0;
        atlasUv = Vector2.Zero;
        if (!_slots.TryGetValue(blendMapId, out Slot slot) || slot.Width <= 0 || slot.Height <= 0)
            return false;

        float u = Math.Clamp(faceUv.X, 0f, 1f);
        float v = Math.Clamp(faceUv.Y, 0f, 1f);

        float x = slot.X + 0.5f + u * (slot.Width - 1);
        float y = slot.Y + 0.5f + v * (slot.Height - 1);

        page = slot.Page;
        atlasUv = new Vector2(x / PageSize, y / PageSize);
        return true;
    }
}
