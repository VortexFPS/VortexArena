using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VortexArena.Game.Legacy;

/// <summary>
/// What a legacy session sent to the server as console commands, for the session log - enough of it to see,
/// afterwards, whether the player's input left the client at all.
///
/// <para>It used to be the first 40 commands of a SESSION, which a level's signon and the map vote's picture
/// requests used up before the player had done anything; on the level after, where the trouble was, the log
/// showed nothing. Now: the first <see cref="FullPerLevel"/> commands of each LEVEL in full, and after that
/// one line a second with the commands of that second counted by name. The words of a chat line are never
/// written, only its length; a line that names rcon or a password is dropped by <see cref="LegacyLog"/>.</para>
///
/// <para>Commands the console did not know and DarkPlaces would not forward ("Unknown command") are listed
/// the same way: the first few by name, then counted.</para>
/// </summary>
public sealed class LegacyCommandLog
{
    public const int FullPerLevel = 80;
    private const int UnknownNamesListed = 24;

    private readonly Action<string> _write;
    private readonly Func<double> _now;
    private readonly SortedDictionary<string, int> _second = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _unknownSecond = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownNamed = new(StringComparer.OrdinalIgnoreCase);
    private double _secondStarted = double.NaN;
    private int _level, _sentThisLevel, _summarisedThisLevel;

    /// <param name="write">Where a line goes (the session log).</param>
    /// <param name="now">The session's clock, in seconds.</param>
    public LegacyCommandLog(Action<string> write, Func<double> now)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>Commands sent on this connection, and on the level being played.</summary>
    public long Sent { get; private set; }
    public int SentThisLevel => _sentThisLevel;
    /// <summary>Commands that were unknown and went nowhere.</summary>
    public long Unknown { get; private set; }

    /// <summary>The first word of a command line.</summary>
    public static string NameOf(string command)
    {
        ReadOnlySpan<char> text = command.AsSpan().TrimStart();
        int end = 0;
        while (end < text.Length && text[end] > ' ' && end < 40) end++;
        return end == 0 ? "(empty)" : text[..end].ToString();
    }

    /// <summary>What may be written of a command: everything, except the words of a chat line.</summary>
    public static string Loggable(string command)
    {
        string name = NameOf(command);
        if (name.Equals("say", StringComparison.OrdinalIgnoreCase) || name.Equals("say_team", StringComparison.OrdinalIgnoreCase) || name.Equals("tell", StringComparison.OrdinalIgnoreCase))
            return string.Create(CultureInfo.InvariantCulture, $"{name} <{Math.Max(0, command.TrimStart().Length - name.Length - 1)} characters of chat>");
        return command;
    }

    /// <summary>A command was queued for the server (DpClient.CommandSent).</summary>
    public void OnSent(string command)
    {
        string name = NameOf(command);
        // "prespawn" is the first thing a client says on a level: the count starts over with it.
        if (name == "prespawn")
        {
            EndLevel();
            _level++;
        }
        Sent++;
        _sentThisLevel++;
        if (_sentThisLevel <= FullPerLevel)
        {
            _write("cmd> " + Loggable(command));
            if (_sentThisLevel == FullPerLevel) _write($"cmd> ({FullPerLevel} commands of this level were written in full; from here on they are counted by name, once a second)");
            return;
        }
        _summarisedThisLevel++;
        Count(_second, name);
    }

    /// <summary>The console did not know a command and it was not one a DarkPlaces client forwards.</summary>
    public void OnUnknown(string name)
    {
        Unknown++;
        if (name.Length > 40) name = name[..40];
        if (_unknownNamed.Count < UnknownNamesListed && _unknownNamed.Add(name))
        {
            _write($"Unknown command \"{name}\" (not sent to the server: a DarkPlaces client forwards only \"cmd ...\" and its own short list)");
            return;
        }
        Count(_unknownSecond, name);
    }

    /// <summary>Once a frame: writes the summary of the second that has ended, if it had anything.</summary>
    public void Tick()
    {
        double now = _now();
        if (double.IsNaN(_secondStarted)) _secondStarted = now;
        if (now - _secondStarted < 1) return;
        _secondStarted = now;
        Flush();
    }

    /// <summary>The session is over.</summary>
    public void End()
    {
        EndLevel();
        _write(string.Create(CultureInfo.InvariantCulture, $"commands sent to the server on this connection: {Sent}; unknown commands kept from it: {Unknown}"));
    }

    private void EndLevel()
    {
        Flush();
        if (_level > 0)
            _write(string.Create(CultureInfo.InvariantCulture, $"level {_level}: {_sentThisLevel} commands were sent to the server ({Math.Min(_sentThisLevel, FullPerLevel)} written in full, {_summarisedThisLevel} counted by name)"));
        _sentThisLevel = _summarisedThisLevel = 0;
    }

    private void Flush()
    {
        if (_second.Count != 0)
        {
            _write("cmds sent in the last second: " + Join(_second));
            _second.Clear();
        }
        if (_unknownSecond.Count != 0)
        {
            _write("unknown commands in the last second (not sent): " + Join(_unknownSecond));
            _unknownSecond.Clear();
        }
    }

    private static void Count(SortedDictionary<string, int> counts, string name)
    {
        if (counts.Count >= 64 && !counts.ContainsKey(name)) name = "(others)";
        counts[name] = counts.GetValueOrDefault(name) + 1;
    }

    private static string Join(SortedDictionary<string, int> counts)
    {
        StringBuilder text = new();
        foreach ((string name, int count) in counts)
        {
            if (text.Length != 0) text.Append(", ");
            text.Append(name);
            if (count > 1) text.Append(" x").Append(count.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
