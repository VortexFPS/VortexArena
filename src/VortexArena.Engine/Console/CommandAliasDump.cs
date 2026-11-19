using System;
using System.Collections.Generic;
using System.IO;

namespace VortexArena.Engine.Console;

/// <summary>Formatting shared by the three program-specific dumpcommands exports.</summary>
public static class CommandAliasDump
{
    public const string CommandName = "dumpcommands";
    public const string Description = "Export the current program's command aliases";
    public const string WriteError = "^1Error: ^7Could not dump to file!";

    public static bool IsGeneric(string name) => LocalDiagnostics.IsCommand(name) ||
        name.ToLowerInvariant() is CommandName or "dumpturrets" or "dumpweapons" or
            "addtolist" or "removefromlist" or "maplist" or "nextframe" or "rpn" or "settemp" or "settemp_restore";

    public static void WriteSection(TextWriter writer, string heading, string prefix,
        IEnumerable<(string Name, string Description)> entries, bool last = false)
    {
        writer.Write($"dump of {heading} commands:\n");
        foreach (var (name, description) in entries)
        {
            // The stock export contains generated alias bodies and comments, never the live body of a user alias.
            writer.Write($"alias {name,-20} \"{prefix,-13} {name,-20} ${{* ?}}\" // {description}\n");
        }
        if (!last)
            writer.Write('\n');
    }

    public static string Save(string program, Func<string, Stream>? open, Action<TextWriter> write)
    {
        string filename = program + "_dump.txt";
        if (open is null)
            return WriteError;
        try
        {
            using var writer = new StreamWriter(open(filename));
            write(writer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return WriteError;
        }
        return $"Completed dump of aliases in ^2data/{filename}^7.";
    }
}
