using VortexArena.Common.Config;
using VortexArena.Common.Diagnostics;
using VortexArena.Server.Bot;

namespace VortexArena.Server;

public sealed partial class Commands
{
    private bool CmdBotScript(CommandContext ctx) => RunBotScriptCommand(ctx, allowLoad: true);

    private bool RunBotScriptCommand(CommandContext ctx, bool allowLoad)
    {
        if (!ctx.IsServerConsole)
            return false;

        string selector = ctx.Arg(1);
        switch (selector.ToLowerInvariant())
        {
            case "reset":
                _world.Bots.ResetScripts();
                return true;
            case "setbots" when ctx.ArgCount == 3:
                if (!int.TryParse(ctx.Arg(2), out int count) || count < 0)
                    ctx.Print("Usage: bot_cmd setbots <count>");
                else if (!_world.Bots.ReplaceScriptBots(count))
                    ctx.Print("Sorry, could not set requested bot count");
                return true;
            case "load" when allowLoad && ctx.ArgCount == 3:
                LoadBotScript(ctx, ctx.Arg(2));
                return true;
            case "help":
                foreach (string help in BotCommandQueue.Help(ctx.Arg(2)))
                    ctx.Print(help);
                return true;
        }

        if (ctx.ArgCount < 3)
        {
            ctx.Print("Usage: sv_cmd bot_cmd <name|number|all|*> <command> [arguments]");
            return true;
        }

        string name = ctx.Arg(2);
        string parameter = ctx.RawTail(3);
        if (selector == "*" || selector.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (BotBrain bot in _world.Bots.Brains)
            {
                if (!_world.Bots.ScriptFor(bot).Enqueue(name, parameter, out string error))
                {
                    ctx.Print(error);
                    return true;
                }
            }
            if (_world.Bots.Brains.Count > 0)
                ctx.Print($"Command '{ctx.RawTail(2)}' sent to all bots ({_world.Bots.Brains.Count})");
        }
        else if (_world.Bots.FindScriptBot(selector) is BotBrain bot)
        {
            if (_world.Bots.ScriptFor(bot).Enqueue(name, parameter, out string error))
                ctx.Print($"Command '{ctx.RawTail(2)}' sent to bot {bot.Bot.NetName}");
            else
                ctx.Print(error);
        }
        else
            ctx.Print($"Can't find bot with the name or number '{selector}'.");
        return true;
    }

    private void LoadBotScript(CommandContext ctx, string path)
    {
        string? text = _world.ConfigReader?.Invoke(path);
        if (text is null)
        {
            ctx.Print("Cannot open the file.");
            return;
        }

        int lines = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string line)
        {
            lines++;
            // Native files use one sv_cmd bot_cmd instruction per line. Other server console lines go
            // through the normal command buffer, so a roster change here can target newly created bots.
            string[] fields = Tokenize(line);
            if (fields.Length < 2 || !fields[0].Equals("sv_cmd", StringComparison.OrdinalIgnoreCase)
                || !fields[1].Equals("bot_cmd", StringComparison.OrdinalIgnoreCase))
            {
                QueueBotConsole(line);
                continue;
            }
            var wrapped = new CommandContext(fields, true, null, line);
            string raw = wrapped.RawTail(1);
            string[] args = Tokenize(raw);
            var nested = new CommandContext(args, true, null, raw);
            RunBotScriptCommand(nested, allowLoad: false);
            if (nested.Output.Length > 0)
                Log.Info(nested.Output.TrimEnd());
        }
        ctx.Print($"{lines} commands read");
    }

    private ConfigInterpreter? _botConsole;

    internal void QueueBotConsole(string line)
    {
        foreach (string command in ConfigInterpreter.SplitIntoCommands(line))
            Deferred.Defer(0f, command, _world.Time, ExecuteBotConsole);
    }

    private void ExecuteBotConsole(string line)
    {
        ConfigInterpreter interpreter = _botConsole ??= _world.LoadedConfig
            ?? ConfigLoader.Load(_world.Services.CvarsImpl, path => _world.ConfigReader?.Invoke(path));
        var previous = interpreter.HostCommandHandler;
        interpreter.HostCommandHandler = TryExecuteBotServerCommand;
        try
        {
            interpreter.ExecuteLine(line);
        }
        finally
        {
            interpreter.HostCommandHandler = previous;
        }
    }

    private bool TryExecuteBotServerCommand(string name, string line)
    {
        // Only deferred commands originating in server-console bot scripts reach this interpreter.
        if (name.Equals("sv_cmd", StringComparison.OrdinalIgnoreCase))
            line = new CommandContext(Tokenize(line), true, null, line).RawTail(1);
        else if (!Has(name))
            return false;

        var ctx = Execute(line, isServerConsole: true);
        if (ctx.Output.Length > 0)
            Log.Info(ctx.Output.TrimEnd());
        return true;
    }
}
