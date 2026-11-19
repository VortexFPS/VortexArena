using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Engine.Console;

namespace VortexArena.Server;

public sealed partial class Commands
{
    // BanCommand_macro_write_aliases walks BAN_COMMANDS in source order, including this order of the unban verbs.
    private static readonly string[] BanCommandOrder =
    {
        "ban", "banlist", "kickban", "mute", "playban", "unban", "unmute", "unvoteban", "unplayban", "voteban",
    };

    private static readonly HashSet<string> CommonReplyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cvar_changes", "cvar_purechanges", "editmob", "info", "ladder", "lsmaps", "printmaplist",
        "rankings", "records", "teamstatus", "time", "timein", "timeout", "vote", "who",
    };

    private bool DumpCommandAliases(CommandContext context)
    {
        if (!context.IsServerConsole)
            return false;

        context.Print(CommandAliasDump.Save("sv_cmd", OpenCommandExport, writer =>
        {
            var scopes = _commands.Values.OrderBy(command => command.Name, StringComparer.Ordinal)
                .ToLookup(command => AliasSection(command.Name));
            CommandAliasDump.WriteSection(writer, "server console", "qc_cmd_sv", Describe(scopes["server console"]));
            CommandAliasDump.WriteSection(writer, "networked client only", "qc_cmd_cmd", Describe(scopes["networked client only"]));
            CommandAliasDump.WriteSection(writer, "common", "qc_cmd_svcmd", Describe(scopes["common"]));
            CommandAliasDump.WriteSection(writer, "ban", "qc_cmd_sv", Describe(BanCommandOrder.Where(Has)
                .Select(name => _commands[name])));
            CommandAliasDump.WriteSection(writer, "generic", "qc_cmd_svmenu", Describe(scopes["generic"]), last: true);
        }));
        return true;

        static IEnumerable<(string, string)> Describe(IEnumerable<ConsoleCommand> commands) =>
            commands.Select(command => (command.Name, command.Help));
    }

    private static string AliasSection(string name)
    {
        if (BanCommandOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
            return "ban";
        if (CommandAliasDump.IsGeneric(name))
            return "generic";
        if (CommonReplyCommands.Contains(name))
            return "common";
        return ClientCommandRegistry.IsClientCallable(name) ? "networked client only" : "server console";
    }
}
