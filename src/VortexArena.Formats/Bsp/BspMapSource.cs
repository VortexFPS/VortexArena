using VortexArena.Formats.Vfs;

namespace VortexArena.Formats.Bsp;

/// <summary>Reads a mounted native map, honoring the adjacent DarkPlaces entity replacement.</summary>
public static class BspMapSource
{
    public static BspData Read(VirtualFileSystem vfs, string bspPath) =>
        BspReader.Read(vfs.ReadBytes(bspPath), ReadEntityOverride(vfs, bspPath));

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadEntities(VirtualFileSystem vfs, string bspPath)
    {
        string? text = ReadEntityOverride(vfs, bspPath);
        return text is not null
            ? EntityLumpParser.Parse(text)
            : BspReader.ReadEntityLump(vfs.ReadBytes(bspPath));
    }

    private static string? ReadEntityOverride(VirtualFileSystem vfs, string bspPath)
    {
        string entPath = Path.ChangeExtension(bspPath, ".ent");
        return vfs.Exists(entPath) ? vfs.ReadText(entPath) : null;
    }
}
