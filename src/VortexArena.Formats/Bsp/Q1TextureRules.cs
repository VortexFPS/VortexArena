// Port of the name rules of Base/darkplaces/model_brush.c Mod_Q1BSP_LoadTextures (what a texture of a Quake 1
// format map is, by its name; the "+" animation chains; where a replacement image is looked for) and of
// gl_rmain.c R_UpdateCurrentTexture's frame selection for such a chain.
namespace VortexArena.Formats.Bsp;

/// <summary>What DarkPlaces makes of a texture of a Quake 1 format map from its name alone.</summary>
public enum Q1SurfaceKind
{
    /// <summary>MATERIALFLAG_WALL: lit by its lightmap.</summary>
    Wall,
    /// <summary>"{name": a wall whose palette index 255 is see-through (MATERIALFLAG_ALPHATEST).</summary>
    Fence,
    /// <summary>"*name": water, slime, lava, a teleporter. Drawn with the scrolling texture matrix.</summary>
    Liquid,
    /// <summary>"sky...": shows the sky, not the texture.</summary>
    Sky,
    /// <summary>"caulk": not drawn.</summary>
    NoDraw,
}

/// <param name="Kind">See <see cref="Q1SurfaceKind"/>.</param>
/// <param name="WaterAlpha">MATERIALFLAG_WATERALPHA: <c>r_wateralpha</c> applies (water and slime; not lava,
/// teleporters or "*rift"), and only on a map whose visibility data allows it
/// (<see cref="Q1BspData.SupportsWaterAlpha"/>).</param>
public readonly record struct Q1TextureClass(Q1SurfaceKind Kind, bool WaterAlpha)
{
    public bool WaterScroll => Kind == Q1SurfaceKind.Liquid;
}

/// <summary>One "+" animation: the frames of the primary chain ("+0" to "+9") and of the alternate chain
/// ("+a" to "+j"), as indices into the map's texture list. A texture of the alternate chain has the two swapped,
/// as DarkPlaces links them ("the primary/alternate are reversed here").</summary>
public sealed record Q1TextureAnimation(int[] Primary, int[] Alternate)
{
    /// <summary>
    /// The texture drawn at <paramref name="time"/> (rsurface.shadertime): five frames a second through the
    /// primary chain, or through the alternate one when the entity's frame is not 0 (a pressed button).
    /// </summary>
    public int FrameAt(double time, bool alternate)
    {
        int[] chain = alternate && Alternate.Length > 0 ? Alternate : Primary;
        if (chain.Length == 0) return -1;
        if (chain.Length < 2) return chain[0];
        // "(int)(rsurface.shadertime * 5.0f) % t->anim_total"
        int step = (int)((float)time * 5.0f);
        int at = step % chain.Length;
        if (at < 0) at += chain.Length;
        return chain[at];
    }
}

public static class Q1TextureRules
{
    /// <summary>The material flags DarkPlaces gives a texture for its (lower-case) name.</summary>
    public static Q1TextureClass Classify(string name)
    {
        if (string.IsNullOrEmpty(name)) return new Q1TextureClass(Q1SurfaceKind.Wall, false);
        if (name[0] == '*')
        {
            // "some turbulent textures should not be affected by wateralpha"
            bool opaque = name.StartsWith("*lava", StringComparison.Ordinal) || name.StartsWith("*teleport", StringComparison.Ordinal)
                || name.StartsWith("*rift", StringComparison.Ordinal);
            return new Q1TextureClass(Q1SurfaceKind.Liquid, !opaque);
        }
        if (name[0] == '{') return new Q1TextureClass(Q1SurfaceKind.Fence, false);
        if (name.StartsWith("sky", StringComparison.Ordinal)) return new Q1TextureClass(Q1SurfaceKind.Sky, false);
        if (name == "caulk") return new Q1TextureClass(Q1SurfaceKind.NoDraw, false);
        return new Q1TextureClass(Q1SurfaceKind.Wall, false);
    }

    /// <summary>
    /// A texture name as a file name: every '*' becomes '#' (image.c loadimagepixelsbgra, "so commandline
    /// utils don't get confused when dealing with the external files").
    /// </summary>
    public static string FileName(string textureName) => textureName.Replace('*', '#');

    /// <summary>
    /// Where a replacement image for a texture is looked for, in order, without an extension:
    /// <c>textures/&lt;map&gt;/&lt;name&gt;</c>, then <c>textures/&lt;name&gt;</c>. <paramref name="mapName"/> is the
    /// map's file name without "maps/" and ".bsp". (A sky texture is not replaced this way: DarkPlaces reads
    /// such an image only to split it into the two sky layers.)
    /// </summary>
    public static string[] ExternalCandidates(string mapName, string textureName)
    {
        string file = FileName(textureName);
        return string.IsNullOrEmpty(mapName)
            ? new[] { "textures/" + file }
            : new[] { "textures/" + mapName + "/" + file, "textures/" + file };
    }

    /// <summary>The suffixes of the unlit layer beside a replacement image, in the order tried
    /// (gl_rmain.c R_SkinFrame_LoadExternal_SkinFrame).</summary>
    public static readonly string[] GlowSuffixes = { "_glow", "_luma" };

    /// <summary>"maps/e1m1.bsp" gives "e1m1": the directory replacement textures of one map live in.</summary>
    public static string MapName(string mapPath)
    {
        string s = mapPath.Replace('\\', '/');
        if (s.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        int dot = s.LastIndexOf('.');
        int slash = s.LastIndexOf('/');
        return dot > slash ? s[..dot] : s;
    }

    /// <summary>
    /// "sequence the animations": for each texture, its animation or null. Names are the map's texture names
    /// in file order (lower case). A chain with a missing frame is not linked (its textures stay still), as in
    /// DarkPlaces; a chain with only alternate frames uses them for both.
    /// </summary>
    public static Q1TextureAnimation?[] BuildAnimations(IReadOnlyList<string> names)
    {
        var result = new Q1TextureAnimation?[names.Count];
        var done = new bool[names.Count];
        int[] anims = new int[10], altAnims = new int[10];
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (name is null || name.Length < 3 || name[0] != '+') continue;
            char num = name[1];
            if ((num < '0' || num > '9') && (num < 'a' || num > 'j')) continue;   // "Bad animating texture"
            if (done[i]) continue;                                                 // "already sequenced"

            Array.Fill(anims, -1);
            Array.Fill(altAnims, -1);
            for (int j = i; j < names.Count; j++)
            {
                string other = names[j];
                if (other is null || other.Length < 2 || other[0] != '+' || !string.Equals(other[2..], name[2..], StringComparison.Ordinal)) continue;
                char n = other[1];
                if (n >= '0' && n <= '9') anims[n - '0'] = j;
                else if (n >= 'a' && n <= 'j') altAnims[n - 'a'] = j;
            }
            int max = 0, altMax = 0;
            for (int j = 0; j < 10; j++)
            {
                if (anims[j] >= 0) max = j + 1;
                if (altAnims[j] >= 0) altMax = j + 1;
            }
            bool incomplete = false;
            for (int j = 0; j < max; j++) incomplete |= anims[j] < 0;
            for (int j = 0; j < altMax; j++) incomplete |= altAnims[j] < 0;
            if (incomplete) continue;

            int[] primary = anims[..max], alternate = altAnims[..altMax];
            bool realAlternate = altMax >= 1 && max >= 1;
            if (altMax < 1) alternate = primary;     // "duplicate the primary animation into the alternate"
            if (max < 1) primary = alternate;        // "duplicating the alternate animation into the primary"

            var forward = new Q1TextureAnimation(primary, alternate);
            foreach (int t in primary)
            {
                result[t] = forward;
                done[t] = true;
            }
            if (realAlternate)
            {
                var reversed = new Q1TextureAnimation(alternate, primary);
                foreach (int t in alternate)
                {
                    result[t] = reversed;
                    done[t] = true;
                }
            }
            else
                foreach (int t in alternate) done[t] = true;
        }
        return result;
    }
}
