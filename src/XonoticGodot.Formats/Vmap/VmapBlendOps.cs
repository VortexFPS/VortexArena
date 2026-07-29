using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Give a face a blend map, sized from the face's own winding (backlog F2).
///
/// Mints an id, so it carries the same handshake every creating op does: a guest asks with 0, the server
/// assigns during Apply, and the re-encoded line is what every peer replays.
/// </summary>
public sealed class CreateBlendMapOp : IVmapOp
{
    private readonly int _brushId;
    private readonly int _faceIndex;
    private readonly float _unitsPerTexel;
    private readonly int _forcedId;
    private int _assignedId;

    /// <summary>Smallest map a face gets, whatever its size — below this a stroke covers everything.</summary>
    public const int MinSide = 4;

    /// <summary>
    /// Largest side. A map-spanning face at a fine texel size would otherwise eat a whole atlas page on its
    /// own and cost a 4 MB upload per stroke.
    /// </summary>
    public const int MaxSide = 512;

    public CreateBlendMapOp(int brushId, int faceIndex, float unitsPerTexel, int forcedId = 0)
    {
        _brushId = brushId;
        _faceIndex = faceIndex;
        _unitsPerTexel = unitsPerTexel;
        _forcedId = forcedId;
    }

    /// <summary>Id given to the map; valid after a successful <see cref="Apply"/>.</summary>
    public int BlendMapId => _assignedId;

    /// <summary>The id this op carries on the wire — assigned once it has run, requested before that.</summary>
    public int WireId => _assignedId != 0 ? _assignedId : _forcedId;

    /// <summary>The brush whose face gets the map. Read by the wire codec.</summary>
    public int BrushId => _brushId;

    /// <summary>Which face. Read by the wire codec.</summary>
    public int FaceIndex => _faceIndex;

    /// <summary>Requested resolution in world units per texel. Read by the wire codec.</summary>
    public float UnitsPerTexel => _unitsPerTexel;

    public IReadOnlyList<int> TouchedBrushIds => new[] { _brushId };

    public string Describe() => $"Blend map on face {_faceIndex} of brush {_brushId}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_unitsPerTexel <= 0f)
            return false;
        if (doc.FindBrush(_brushId) is not { } brush)
            return false;
        if (_faceIndex < 0 || _faceIndex >= brush.Faces.Count)
            return false;

        VmapFace face = brush.Faces[_faceIndex];
        if (face.BlendMapId != 0 && doc.FindBlendMap(face.BlendMapId) is not null)
            return false;      // already painted; re-creating would throw the paint away

        Vector3[] winding = VmapWinding.BuildFaceWinding(brush, _faceIndex);
        if (winding.Length < 3)
            return false;

        // A projection whose UV spans exactly [0,1] across the face — which IS the "planar, no unwrap" the
        // design asks for, and it is already written and already tested.
        VmapTexProjection projection = VmapTexAlign.Fit(
            VmapTexProjection.AxialFor(face.Plane.Normal, 1f), winding, 1f, 1f);

        // The world extent the projection now maps to one unit of UV: the axes are repeats-per-unit, so the
        // span is their reciprocal.
        float spanU = projection.AxisU.Length() > 1e-9f ? 1f / projection.AxisU.Length() : 0f;
        float spanV = projection.AxisV.Length() > 1e-9f ? 1f / projection.AxisV.Length() : 0f;
        if (spanU <= 0f || spanV <= 0f)
            return false;

        int width = Math.Clamp((int)MathF.Round(spanU / _unitsPerTexel), MinSide, MaxSide);
        int height = Math.Clamp((int)MathF.Round(spanV / _unitsPerTexel), MinSide, MaxSide);

        _assignedId = _forcedId != 0 ? _forcedId : doc.NextBlendMapId();
        doc.BlendMaps.Add(new VmapBlendMap
        {
            Id = _assignedId,
            Width = width,
            Height = height,
            UnitsPerTexel = _unitsPerTexel,
            Projection = projection,
            Texels = new byte[width * height * 4],
        });
        face.BlendMapId = _assignedId;
        return true;
    }
}

/// <summary>
/// One painted stroke: a polyline of samples in a blend map's own UV space (backlog F3).
///
/// The STROKE is the op, not the resulting bitmap, and that is the whole reason painting can replicate at
/// all: twenty samples is a couple of hundred characters, and a 128-square tile delta is 64 KB against a
/// 16000-character submit cap. It also makes the undo step a rectangle rather than an image.
///
/// It only works because <see cref="VmapBlendPaint"/> is deterministic — a peer replays the stroke rather than
/// receiving its pixels.
/// </summary>
public sealed class PaintBlendOp : IVmapOp
{
    private readonly int _blendMapId;
    private readonly int _channel;
    private readonly VmapPaintMode _mode;
    private readonly Vector2[] _samples;
    private readonly float _radiusUv;
    private readonly float _strength;
    private readonly float _hardness;
    private readonly VmapBlendRegion[] _touched;

    /// <param name="doc">
    /// Needed at CONSTRUCTION to size the touched rectangle, exactly as the entity ops need it to resolve
    /// owned geometry: the journal reads the touched set before Apply runs, and a UV radius is only a number
    /// of TEXELS once you know how many texels the map has. Without it the op falls back to declaring the
    /// whole map — correct, and a bigger snapshot than it needs to be.
    /// </param>
    public PaintBlendOp(
        int blendMapId, int channel, VmapPaintMode mode, IReadOnlyList<Vector2> samples,
        float radiusUv, float strength, float hardness, VmapDocument? doc = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _blendMapId = blendMapId;
        _channel = channel;
        _mode = mode;
        _samples = samples.ToArray();
        _radiusUv = radiusUv;
        _strength = strength;
        _hardness = hardness;

        if (_samples.Length == 0)
        {
            _touched = Array.Empty<VmapBlendRegion>();
            return;
        }

        if (doc?.FindBlendMap(blendMapId) is not { IsValid: true } map)
        {
            _touched = new[] { VmapBlendRegion.Whole(blendMapId) };
            return;
        }

        var regions = new List<VmapBlendRegion>(_samples.Length);
        foreach (Vector2 s in _samples)
        {
            VmapBlendPaint.RegionOf(map.Width, map.Height, s, _radiusUv,
                out int rx, out int ry, out int rw, out int rh);
            if (rw > 0 && rh > 0)
                regions.Add(new VmapBlendRegion(_blendMapId, rx, ry, rw, rh));
        }
        _touched = regions.Count == 0
            ? Array.Empty<VmapBlendRegion>()
            : new[] { VmapBlendPaint.Union(_blendMapId, regions) };
    }

    /// <summary>Which map. Read by the wire codec.</summary>
    public int BlendMapId => _blendMapId;

    /// <summary>Which of the four weight channels. Read by the wire codec.</summary>
    public int Channel => _channel;

    /// <summary>Read by the wire codec.</summary>
    public VmapPaintMode Mode => _mode;

    /// <summary>The stroke, in the map's 0-1 space. Read by the wire codec.</summary>
    public IReadOnlyList<Vector2> Samples => _samples;

    /// <summary>Read by the wire codec.</summary>
    public float RadiusUv => _radiusUv;

    /// <summary>Read by the wire codec.</summary>
    public float Strength => _strength;

    /// <summary>Read by the wire codec.</summary>
    public float Hardness => _hardness;

    // Paint changes texels and nothing else — no geometry is touched, which is exactly why a stroke must not
    // bump the geometry version and trigger an ~880 ms world rebuild.
    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    public IReadOnlyList<VmapBlendRegion> TouchedBlendRegions => _touched;

    public string Describe() => $"Paint {_samples.Length} sample{(_samples.Length == 1 ? "" : "s")}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_samples.Length == 0 || doc.FindBlendMap(_blendMapId) is not { } map)
            return false;

        bool any = false;
        foreach (Vector2 s in _samples)
            any |= VmapBlendPaint.Stamp(
                map, s, _radiusUv, _strength, _hardness, _channel, _mode, out _, out _, out _, out _);
        return any;
    }
}

/// <summary>
/// Set a rectangle of texels outright — the resync and undo-echo form, never a gesture (backlog F2).
///
/// The same role <c>SetObjectsOp</c> plays for geometry: an undo has no op to replay, so what travels is the
/// resulting STATE of exactly the rectangles the undone step touched. Server-to-client only in practice; the
/// submit direction is capped at a length a bitmap cannot fit in, and the answer there is to send strokes.
/// </summary>
public sealed class SetBlendRegionOp : IVmapOp
{
    private readonly VmapBlendRegion[] _regions;
    private readonly byte[][] _texels;

    public SetBlendRegionOp(IReadOnlyList<VmapBlendRegion> regions, IReadOnlyList<byte[]> texels)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(texels);
        if (regions.Count != texels.Count)
            throw new ArgumentException("one texel block per region", nameof(texels));
        _regions = regions.ToArray();
        _texels = texels.ToArray();
    }

    /// <summary>Read the CURRENT texels of a set of rectangles — how a restore becomes something replayable.</summary>
    public static SetBlendRegionOp Capture(VmapDocument doc, IReadOnlyList<VmapBlendRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(regions);

        var kept = new List<VmapBlendRegion>();
        var blocks = new List<byte[]>();
        foreach (VmapBlendRegion r in regions)
        {
            if (doc.FindBlendMap(r.BlendMapId) is not { } map || !map.IsValid)
                continue;
            VmapBlendRegion clamped = Clamp(map, r);
            if (clamped.Width <= 0 || clamped.Height <= 0)
                continue;
            kept.Add(clamped);
            blocks.Add(map.CopyRegion(clamped.X, clamped.Y, clamped.Width, clamped.Height));
        }
        return new SetBlendRegionOp(kept, blocks);
    }

    /// <summary>Clamp a declared region to a map's real size. <c>Whole</c> declares int.MaxValue.</summary>
    public static VmapBlendRegion Clamp(VmapBlendMap map, VmapBlendRegion r)
    {
        ArgumentNullException.ThrowIfNull(map);
        int x0 = Math.Clamp(r.X, 0, map.Width);
        int y0 = Math.Clamp(r.Y, 0, map.Height);
        int x1 = (int)Math.Clamp((long)r.X + r.Width, 0, map.Width);
        int y1 = (int)Math.Clamp((long)r.Y + r.Height, 0, map.Height);
        return new VmapBlendRegion(r.BlendMapId, x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Read by the wire codec.</summary>
    public IReadOnlyList<VmapBlendRegion> Regions => _regions;

    /// <summary>Read by the wire codec, one block per region.</summary>
    public IReadOnlyList<byte[]> Texels => _texels;

    /// <summary>True when there is nothing to send — do not broadcast an empty restore.</summary>
    public bool IsEmpty => _regions.Length == 0;

    public IReadOnlyList<int> TouchedBrushIds => Array.Empty<int>();

    public IReadOnlyList<VmapBlendRegion> TouchedBlendRegions => _regions;

    public string Describe() => $"Restore {_regions.Length} blend region{(_regions.Length == 1 ? "" : "s")}";

    public bool Apply(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_regions.Length == 0)
            return false;

        bool any = false;
        for (int i = 0; i < _regions.Length; i++)
        {
            VmapBlendRegion r = _regions[i];
            if (doc.FindBlendMap(r.BlendMapId) is not { } map || !map.IsValid)
                continue;
            if (_texels[i].Length < r.Width * r.Height * 4)
                return false;    // a block that disagrees with its rectangle is a corrupt line, not a partial
            any |= map.PasteRegion(r.X, r.Y, r.Width, r.Height, _texels[i]);
        }
        return any;
    }
}
