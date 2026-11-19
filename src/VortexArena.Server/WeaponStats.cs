using System.Globalization;
using System.IO;
using System.Text;
using VortexArena.Common;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;

namespace VortexArena.Server;

/// <summary>The optional local weapon matchup report for one map (QC server/weapons/weaponstats.qc).</summary>
internal sealed class WeaponStats(GameWorld world, Func<string, string> resolveUserPath,
    string pureChanges, int pureChangesCount) : IWeaponStatsSink
{
    internal const string FileCvar = "sv_weaponstats_file";

    private SortedDictionary<(int AttackWeapon, int AttackerBot, int VictimWeapon, int VictimBot),
        (int Kills, int Hits, float Damage)>? _rows = new();

    public bool CanRecord(Player victim)
        => _rows is not null && Api.Services == world.ServerServices
           && world.Time >= world.GameStartTime && world.Rounds is not { IsRoundStarted: false }
           && victim.FrozenStat == 0 && !(world.GameType is FreezeTag freezeTag && freezeTag.IsFrozen(victim));

    public void Record(Player attacker, Player victim, string attackWeapon, float damage, bool killed)
    {
        if (_rows is not { } rows)
            return;

        int attackId = WeaponOrder.ToXonoticRegistryId(attackWeapon);
        int victimId = WeaponOrder.ToXonoticRegistryId(Inventory.CurrentWeapon(victim)?.NetName);
        if (attackId == 0 || victimId == 0 || !float.IsFinite(damage) || damage < 0f)
            return;

        var key = (attackId, attacker.IsBot ? 1 : 0, victimId, victim.IsBot ? 1 : 0);
        rows.TryGetValue(key, out var totals);
        rows[key] = killed
            ? (totals.Kills + 1, totals.Hits, totals.Damage)
            : (totals.Kills, totals.Hits + 1, totals.Damage + damage);
    }

    public void Flush()
    {
        if (_rows is not { } rows)
            return;

        // One flush owns the batch through all destination attempts. QC clears it when the list
        // is exhausted or a destination closes, so intermission followed by shutdown cannot replay it.
        _rows = null;
        string destinations = world.Services.Cvars.GetString(FileCvar);
        if (string.IsNullOrWhiteSpace(destinations))
            return;

        string hostname = world.Services.Cvars.GetString("hostname");
        string gametype = world.GameType?.NetName ?? GameWorld.DefaultGameType;
        string config = pureChanges + "\n";
        var utc = DateTime.UtcNow;
        var report = new StringBuilder();
        report.Append("#begin statsfile\n");
        report.Append("#date ").Append(utc.ToString("ddd MMM", CultureInfo.InvariantCulture))
            .Append(' ').Append(utc.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2))
            .Append(utc.ToString(" HH:mm:ss 'UTC' yyyy\n", CultureInfo.InvariantCulture));
        report.Append("#version ").Append(BuildInfo.Version).Append('\n');
        report.Append(CultureInfo.InvariantCulture, $"#config {ConfigCrc(config)}\n");
        report.Append(CultureInfo.InvariantCulture, $"#cvar_purechanges {pureChangesCount}\n");
        foreach (string line in pureChanges.Split('\n'))
            report.Append("#cvar_purechange ").Append(line).Append('\n');

        foreach (var (key, totals) in rows)
        {
            report.Append(hostname).Append('\t').Append(gametype).Append('_').Append(world.MapName).Append('\t');
            report.Append(CultureInfo.InvariantCulture,
                $"{key.AttackWeapon} {key.AttackerBot}\t{key.VictimWeapon} {key.VictimBot}\t{totals.Kills} {totals.Hits} {totals.Damage:g6}\n");
        }
        report.Append("#end\n\n");

        string body = report.ToString();
        foreach (string destination in LocalDestinationTokens(destinations))
        {
            if (destination == "-")
            {
                try
                {
                    Console.Out.Write(body);
                    Console.Out.Flush();
                }
                catch (IOException ex)
                {
                    Log.Warn($"[weaponstats] stdout write failed: {ex.Message}");
                }
                return;
            }

            string relativePath = destination.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') || relativePath.Contains(':')
                || relativePath.Split('/').Any(part => part.TrimEnd(' ') == ".."))
            {
                Log.Warn($"[weaponstats] skipping unsupported destination '{destination}'.");
                continue;
            }

            StreamWriter writer;
            try
            {
                writer = new StreamWriter(resolveUserPath(relativePath), append: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn($"[weaponstats] cannot open '{destination}' for append: {ex.Message}");
                continue;
            }

            // An open failure is safe to fall back from. Once writing starts, a failure can
            // leave a partial or complete report, so treat it as terminal.
            try
            {
                using (writer)
                    writer.Write(body);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"[weaponstats] append to '{destination}' failed: {ex.Message}");
            }
            return;
        }
        Log.Warn("[weaponstats] no usable destination for this map's report.");
    }

    // The QC console tokenizer only unescapes backslash and double quote inside quoted arguments.
    // Keep other backslashes so the local-path check also sees Windows roots and traversal.
    private static IEnumerable<string> LocalDestinationTokens(string destinations)
    {
        int offset = 0;
        while (offset < destinations.Length)
        {
            if (destinations[offset] <= ' ')
            {
                offset++;
                continue;
            }
            if (destinations.AsSpan(offset).StartsWith("//", StringComparison.Ordinal))
            {
                while (offset < destinations.Length && destinations[offset] is not ('\r' or '\n'))
                    offset++;
                continue;
            }
            if (destinations[offset] != '"')
            {
                int start = offset++;
                while (offset < destinations.Length && destinations[offset] > ' ')
                    offset++;
                yield return destinations[start..offset];
                continue;
            }

            var token = new StringBuilder();
            offset++;
            while (offset < destinations.Length && destinations[offset] != '"')
            {
                if (destinations[offset] == '\\' && offset + 1 < destinations.Length
                    && destinations[offset + 1] is '\\' or '"')
                    offset++;
                token.Append(destinations[offset++]);
            }
            if (offset < destinations.Length)
                offset++;
            yield return token.ToString();
        }
    }

    // DarkPlaces crc16(false, cvar_purechanges): CCITT over the case-sensitive UTF-8 bytes.
    private static ushort ConfigCrc(string config)
    {
        ushort crc = 0xffff;
        foreach (byte value in Encoding.UTF8.GetBytes(config))
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
