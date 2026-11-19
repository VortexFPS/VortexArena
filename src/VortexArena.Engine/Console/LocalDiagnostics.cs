using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using VortexArena.Common;
using VortexArena.Common.Framework;
using VortexArena.Engine.Simulation;

namespace VortexArena.Engine.Console;

/// <summary>Native equivalents of the local generic commands in common/debug.qh.</summary>
public static class LocalDiagnostics
{
    public static IReadOnlyList<(string Name, string Help)> CommandHelp { get; } = new[]
    {
        ("bufstr_get", "bufstr_get <bufhandle> <string_index> — examine a QuakeC string buffer"),
        ("version", "version — print the current build version"),
        ("cvar_localchanges", "cvar_localchanges — print locally changed cvars"),
        ("findent", "findent <classname> — search entities for a matching classname"),
        ("findat", "findat \"x y z\" [<dist>] — search entities for a matching origin"),
    };

    public static bool IsCommand(string name) =>
        CommandHelp.Any(command => command.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void Run(IReadOnlyList<string> args, string scope, CvarService cvars,
        IEnumerable<Entity> entities, Action<string> write)
    {
        string first = args.Count > 1 ? args[1] : "";
        string second = args.Count > 2 ? args[2] : "";

        switch (args[0].ToLowerInvariant())
        {
            case "version":
                write($"VortexArena {scope} version: {BuildInfo.Version}\n");
                break;
            case "bufstr_get":
                // Handles belong to a particular QuakeC VM. Native services have no shared buffer heap.
                write($"bufstr_get: invalid buffer {(int)Number(first)} in the native {scope} (QuakeC buffers require legacy mode).\n");
                break;
            case "cvar_localchanges":
                foreach (string name in cvars.Names.OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (name.StartsWith('_') || cvars.IsPrivate(name))
                        continue;
                    string value = cvars.GetString(name);
                    string baseline = cvars.GetDefault(name);
                    if (value != baseline)
                        write($"{name} \"{value}\" // \"{baseline}\"\n");
                }
                break;
            case "findent":
                if (first.Length > 0)
                    PrintMatches(entities, e => e.ClassName == first, write);
                break;
            case "findat":
                Vector3 origin = ParseVector(first);
                float dist = Number(second);
                PrintMatches(entities, e => dist > 0
                    ? Vector3.DistanceSquared(e.Origin, origin) < dist * dist
                    : e.Origin == origin, write);
                break;
        }
    }

    private static void PrintMatches(IEnumerable<Entity> entities, Func<Entity, bool> matches, Action<string> write)
    {
        int count = 0;
        foreach (Entity entity in entities)
        {
            if (entity.IsFreed || !matches(entity))
                continue;
            write($"{entity.Index} ({entity.ClassName})\n");
            count++;
        }
        if (count != 0)
            write($"Found {count} entities\n");
    }

    private static Vector3 ParseVector(string value)
    {
        string[] components = value.Trim('\'').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return new Vector3(Component(0), Component(1), Component(2));

        float Component(int index) => index < components.Length ? Number(components[index]) : 0f;
    }

    private static float Number(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number : 0f;
}
