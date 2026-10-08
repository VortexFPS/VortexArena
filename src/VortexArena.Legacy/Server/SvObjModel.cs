// Port of Base/darkplaces/model_brush.c Mod_OBJ_Load, as far as the server needs a Wavefront OBJ
// model: the bounding box of the vertices its faces use (loadmodel->normalmins / normalmaxs).
using System.Globalization;
using System.Numerics;

namespace VortexArena.Legacy.Server;

/// <summary>
/// The box of an OBJ model (map decoration placed with misc_gamemodel: cog wheels, bolts, crates).
/// The server never draws it; setmodel gives the entity this box, and a solid one is clipped as it.
/// </summary>
internal static class SvObjModel
{
    private const int MaxVertices = 1 << 22;   // a bound on what one file may make this allocate

    /// <summary>False if the text has no face that names a vertex.</summary>
    /// <param name="fixOrientation">mod_obj_orientation: "fix orientation of OBJ models to the usual
    /// conventions (if zero, use coordinates as is)". DarkPlaces' default is 1; Xonotic's configuration sets 0.</param>
    public static bool TryGetBounds(ReadOnlySpan<byte> file, bool fixOrientation, out Vector3 mins, out Vector3 maxs)
    {
        mins = maxs = default;
        List<Vector3> vertices = new();
        bool any = false;
        string text = System.Text.Encoding.UTF8.GetString(file);
        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.Length < 2 || line[0] == '#') continue;
            if (line[0] == 'v' && char.IsWhiteSpace(line[1]))
            {
                if (vertices.Count >= MaxVertices) return false;
                Span<Range> parts = stackalloc Range[5];
                int count = line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
                if (count < 4) { vertices.Add(default); continue; }
                float x = Atof(line[parts[1]]), y = Atof(line[parts[2]]), z = Atof(line[parts[3]]);
                // with mod_obj_orientation the file's second and third coordinates change places
                vertices.Add(fixOrientation ? new Vector3(x, z, y) : new Vector3(x, y, z));
            }
            else if (line[0] == 'f' && char.IsWhiteSpace(line[1]))
            {
                // "f v/vt/vn v/vt/vn ...": only the vertex number of each corner matters here
                ReadOnlySpan<char> rest = line[1..];
                while (true)
                {
                    rest = rest.TrimStart();
                    if (rest.IsEmpty) break;
                    int end = rest.IndexOfAny(' ', '\t');
                    ReadOnlySpan<char> corner = end < 0 ? rest : rest[..end];
                    rest = end < 0 ? default : rest[end..];
                    int slash = corner.IndexOf('/');
                    if (slash >= 0) corner = corner[..slash];
                    if (!int.TryParse(corner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0) continue;
                    // a negative number counts back from the vertices read so far
                    index = index < 0 ? vertices.Count + index : index - 1;
                    if ((uint)index >= (uint)vertices.Count) continue;
                    Vector3 v = vertices[index];
                    if (!any) { mins = maxs = v; any = true; }
                    else { mins = Vector3.Min(mins, v); maxs = Vector3.Max(maxs, v); }
                }
            }
        }
        return any;
    }

    private static float Atof(ReadOnlySpan<char> text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : 0;
}
