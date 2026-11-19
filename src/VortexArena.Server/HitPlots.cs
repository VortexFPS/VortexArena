using System.Globalization;
using System.Numerics;
using System.Text;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;

namespace VortexArena.Server;

/// <summary>Match-local, opt-in ports of W_HitPlotOpen, W_HitPlotAnalysis and W_HitPlotClose.</summary>
internal sealed class HitPlots(Func<string, string> resolveUserPath, string matchId) : IDisposable
{
    private readonly Dictionary<Player, StreamWriter> _files = new();

    public void Open(Player player, bool all, string selectedAddresses)
    {
        if (_files.ContainsKey(player)
            || (!all && !selectedAddresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains(player.NetAddress, StringComparer.Ordinal)))
            return;

        string address = SafeAddress(player.NetAddress);
        string fileName = FormattableString.Invariant($"hits-{matchId}-{address}-{player.PlayerId}.plot");
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(resolveUserPath(fileName), append: false, new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            writer.WriteLine("#name " + player.NetName.Replace('\r', ' ').Replace('\n', ' '));
            _files.Add(player, writer);
            player.HitPlotShot = Analyze;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DisposeWriter(writer);
            Log.Warn($"Could not open hitplot for player {player.PlayerId}: {ex.Message}");
        }
    }

    private static string SafeAddress(string address)
    {
        // IP addresses and the engine's local/bot sentinels are the only filename inputs from clients.
        if (address.Length == 0)
            return "unknown";

        return string.Concat(address.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
    }

    private void Analyze(Player shooter, Weapon? weapon)
    {
        if (shooter.IsObserver || !_files.TryGetValue(shooter, out StreamWriter? writer))
            return;

        Vector3 origin = shooter.Origin + shooter.ViewOfs;
        QMath.AngleVectors(shooter.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 coordinates;
        try
        {
            LagComp.Begin(shooter, force: true);
            TraceResult trace = Api.Trace.Trace(origin, Vector3.Zero, Vector3.Zero,
                origin + forward * WeaponFiring.CurrentMaxShotDistance, MoveFilter.Normal, shooter);
            if (trace.Ent is not { } target || (target.Flags & (EntFlags.Client | EntFlags.Monster)) == 0)
                return;

            // Both the trace endpoint and the target's bounds are sampled inside the same rewind.
            coordinates = Project(origin, target, forward, right, up, trace.EndPos);
        }
        finally
        {
            LagComp.End();
        }

        try
        {
            writer.WriteLine(FormattableString.Invariant(
                $"{Format(coordinates.X)} {Format(coordinates.Y)} {Format(coordinates.Z)} {WeaponOrder.ToXonoticRegistryId(weapon?.NetName)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Close(shooter);
            Log.Warn($"Could not write hitplot for player {shooter.PlayerId}: {ex.Message}");
        }
    }

    private static Vector3 Project(Vector3 eye, Entity target, Vector3 forward, Vector3 right, Vector3 up,
        Vector3 hit)
    {
        // Project all eight hitbox corners into the screen basis. The extrema define the normalized XY rectangle.
        Vector2 min = new(float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; ++corner)
        {
            Vector3 world = target.Origin + new Vector3(
                (corner & 4) == 0 ? target.Mins.X : target.Maxs.X,
                (corner & 2) == 0 ? target.Mins.Y : target.Maxs.Y,
                (corner & 1) == 0 ? target.Mins.Z : target.Maxs.Z);
            Vector2 screen = new(Vector3.Dot(right, world), Vector3.Dot(up, world));
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        Vector2 normalized = (new Vector2(Vector3.Dot(right, hit), Vector3.Dot(up, hit)) - min) / (max - min);
        return new Vector3(normalized, Vector3.Dot(forward, hit) - Vector3.Dot(forward, eye));
    }

    private static string Format(float value) =>
        value.ToString(float.IsFinite(value) && value == MathF.Truncate(value) ? "F0" : "F6",
            CultureInfo.InvariantCulture);

    public void Close(Player player)
    {
        if (!_files.Remove(player, out StreamWriter? writer))
            return;
        player.HitPlotShot = null;
        DisposeWriter(writer);
    }

    private static void DisposeWriter(StreamWriter? writer)
    {
        try { writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not close hitplot: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var (player, writer) in _files)
        {
            player.HitPlotShot = null;
            DisposeWriter(writer);
        }
        _files.Clear();
    }
}
