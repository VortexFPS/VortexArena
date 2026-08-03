using Vx.Commands;

namespace Vx;

/// <summary>
/// Entry point and command dispatcher for <c>./vx</c>.
///
/// <para>DELIBERATELY THIN. Real logic belongs in the command it delegates to, or in the script that command
/// wraps — the failure mode this design is guarding against is <c>vx</c> quietly becoming a second place
/// where build logic lives, in parallel with <c>ci/ci.sh</c>, <c>tools/package.sh</c> and the fetchers. Those
/// scripts are well reasoned and stay authoritative; <c>vx</c> is the door in.</para>
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        // Global flags are recognised before the command so `vx --help` and `vx doctor --json` both work.
        bool json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToArray();

        if (rest.Length == 0 || rest[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        string command = rest[0];
        string[] commandArgs = rest.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "doctor" => Doctor.Run(commandArgs, json),
                "maps" => Maps.Run(commandArgs, json),
                "setup" => Setup.Run(commandArgs, json),
                "engine" => Engine.Run(commandArgs, json),
                "build" => Wrappers.Build(commandArgs),
                "test" => Wrappers.Test(commandArgs),
                "run" => Wrappers.RunClient(commandArgs),
                "server" => Wrappers.Server(commandArgs),
                "export" => Wrappers.Export(commandArgs),
                "package" => Wrappers.Package(commandArgs),
                "ci" => Wrappers.Ci(commandArgs),
                "perf" => Wrappers.Perf(commandArgs),
                "perf-smoke" => Wrappers.PerfSmoke(commandArgs),
                "wobble" => Wrappers.Wobble(commandArgs),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            // A stack trace is the right thing for a tool its own developers maintain, but it is noise for
            // someone who just wants to build the game — so it is behind a flag rather than the default.
            Console.Error.WriteLine($"vx: {command}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("VX_DEBUG") == "1")
                Console.Error.WriteLine(ex);
            else
                Console.Error.WriteLine("(set VX_DEBUG=1 for the stack trace)");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"vx: unknown command '{command}'");
        Console.Error.WriteLine();
        PrintUsage(Console.Error);
        return 2;
    }

    private static void PrintUsage(TextWriter? w = null)
    {
        w ??= Console.Out;
        w.WriteLine("""
            vx — the Vortex Arena task runner

            Usage:
              ./vx <command> [options]

            Commands:
              doctor      Diagnose the toolchain and content. Changes nothing.
              setup       Bring this clone to a runnable state.
                            --profile <p>   play | dev | server | ci | launcher
                            --yes           proceed without confirming
                            --dry-run       print the plan, change nothing
              maps        Install the map packs pinned by data/maps.lock.json.
                            --verify-only   report drift, change nothing
                            --force         re-download everything
                            --only <map>    just these (repeatable)
                            --rebuild       compile from the maps-src submodule
                                            instead (delegates to Python)
              engine      Fetch the export templates pinned by engine.lock.json.
                            --verify-only   report drift, change nothing
                            --force         re-download everything
                            --only <plat>   just these (repeatable)

              build       Build the Godot host C# (Release by default; incremental).
                            debug           build the Debug config (what `vx run
                                            debug` loads); --config also accepted
                            --clean         full rebuild (dotnet clean first)
              test        Run the suite.               --filter <expr>
              run         Launch the client — the RELEASE export from dist/ (what a
                          player runs). Extra args go to the game.
                            debug           the Debug project via the editor engine
                                            instead (OS.IsDebugBuild() true, profiler +
                                            showfps on; NOT release-representative)
                            -n, --no-build-check
                                            skip the "sources are newer than the build,
                                            rebuild?" prompt and launch immediately
              server      Headless dedicated server.   [map] [args...]
              export      Export a release preset.     --preset <p> | --all
              package     Zip the exports.             (tools/package.sh)
              ci          The full local gate.         (ci/ci.sh)
              perf        Capture a perf session.      (tools/perf-run.sh)
              perf-smoke  Pre-merge perf gate.         --live adds a capture
              wobble      Motion/present wobble capture.

            Global options:
              --json      Machine-readable output. The schema is versioned and is a
                          contract with VortexLauncher — see the plan before changing it.
              --help      This message.

            Environment overrides, honoured by doctor and by the shell scripts alike:
              GODOT       path to the Godot 4.6.3 mono executable
              PYTHON      path to a Python 3 interpreter
            """);
    }
}
