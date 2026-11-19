using System.Security.Cryptography;

namespace VortexArena.Net;

/// <summary>The local native player key shared by the game and the offline consent command.</summary>
public static class PlayerIdentityFile
{
    public const string FileName = "player-identity.pkcs8";

    public static PlayerIdentity Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try { return PlayerIdentity.FromPrivateKey(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static PlayerIdentity LoadOrCreate(string path)
    {
        try { return Load(path); }
        catch (FileNotFoundException) { }

        using var identity = PlayerIdentity.Generate();
        byte[] bytes = identity.ExportPrivateKey();
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(staging, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(staging, path); }
            catch (IOException) when (File.Exists(path)) { } // Another client initialized the same profile.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(staging);
        }
        return Load(path);
    }
}
