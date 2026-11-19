using System;
using System.Globalization;
using System.IO;
using System.Linq;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Console;

namespace VortexArena.Server;

public sealed partial class Commands
{
    /// <summary>Opens a relative export filename in the host's writable game directory.</summary>
    public Func<string, Stream>? OpenCommandExport { get; set; }

    public static bool IsBalanceDump(string name) =>
        name.Equals("dumpweapons", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("dumpturrets", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalDiagnostic(string name) => IsBalanceDump(name) || LocalDiagnostics.IsCommand(name)
        || name.Equals(CommandAliasDump.CommandName, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalDiagnosticLine(string line)
    {
        string[] args = Tokenize(line);
        return args.Length != 0 && (IsLocalDiagnostic(args[0]) ||
            (args.Length > 1 && args[0].Equals("sv_cmd", StringComparison.OrdinalIgnoreCase) && IsLocalDiagnostic(args[1])));
    }

    private void RegisterLocalDiagnostics()
    {
        Register(CommandAliasDump.CommandName, CommandAliasDump.Description, DumpCommandAliases);
        Register("dumpweapons", "dumpweapons [<filename>] — export weapon balance (weapons_dump.cfg by default)",
            ctx => DumpBalance(ctx, true));
        Register("dumpturrets", "dumpturrets [<filename>] — export turret balance (turrets_dump.cfg by default)",
            ctx => DumpBalance(ctx, false));
        foreach (var (name, help) in LocalDiagnostics.CommandHelp)
            Register(name, help, RunLocalDiagnostic);
    }

    private bool RunLocalDiagnostic(CommandContext ctx)
    {
        if (!ctx.IsServerConsole)
            return false;

        // Clients precede map entities in the QC edict scan. Native client IDs are negative and have
        // their own roster. The engine entity table is already in slot order and includes its real slot 0.
        LocalDiagnostics.Run(ctx.Argv, "server", _world.Services.CvarsImpl,
            _world.Clients.Players.Concat(_world.Services.EntityTable.All), ctx.Write);
        return true;
    }

    private bool DumpBalance(CommandContext ctx, bool weapons)
    {
        if (!ctx.IsServerConsole)
            return false;

        string kind = weapons ? "weapons" : "turrets";
        string filename = ctx.Arg(1);
        // QC initializes alsoprint to -1 (true). An explicitly supplied custom path also echoes.
        bool echo = filename.Length > 0;
        if (filename is "" or "-")
            filename = kind + "_dump.cfg";

        try
        {
            if (OpenCommandExport is not { } open || !IsDumpFilename(filename))
            {
                ctx.Print($"^1Error: ^7Could not open file '{filename}'!");
                return true;
            }

            using var writer = new StreamWriter(open(filename));
            void Write(string chunk)
            {
                writer.Write(chunk);
                if (echo)
                    ctx.Write(chunk);
            }

            if (weapons)
                Write(WeaponDumpHeader + "\n\n");

            var sections = weapons ? BalanceDumpCatalog.Weapons : BalanceDumpCatalog.Turrets;
            var cvars = _world.Services.CvarsImpl;
            int settings = 0;
            int sectionNumber = 0;
            bool aborted = false;
            foreach (var section in sections)
            {
                Weapon? weapon = weapons ? WeaponOrder.FromXonoticRegistryId(section.RegistryId) : null;
                if (weapon is not null && (weapon.SpawnFlags & WeaponFlags.SpecialAttack) != 0)
                    continue;

                string[] lines = section.Properties
                    .Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(property =>
                    {
                        string name = section.CvarPrefix + property;
                        bool readable = cvars.Has(name) && !cvars.IsPrivate(name);
                        if (property == "weaponreplace")
                            return $"set {name} \"{(readable ? cvars.GetString(name) : "")}\"\n";
                        float number = readable ? cvars.GetFloat(name) : 0;
                        string value = number.ToString("g6", CultureInfo.InvariantCulture);
                        return $"set {name} {value}\n";
                    })
                    .ToArray();

                const int maxSettings = 70;
                if (lines.Length >= maxSettings)
                {
                    aborted = true;
                    ctx.Write($"\n^1Dumping aborted^7: hit MAX_CONFIG_SETTINGS ({maxSettings}) limit\n\n");
                    break;
                }

                // Every turret setting starts with "set " and has a distinct ASCII key shorter than the
                // 100-character comparison window in T_Config_Queue_Compare. Both orderings reduce to strcmp.
                Array.Sort(lines, StringComparer.Ordinal);
                int headingNumber = weapons ? sectionNumber + 1 : section.RegistryId;
                bool mutatorBlocked = weapon is not null
                    ? (weapon.SpawnFlags & WeaponFlags.MutatorBlocked) != 0
                    : section.Mutator;
                // The native Nexball mode owns its loadout without mutating the global Ball Stealer descriptor.
                if (weapon is BallStealer && _world.GameType is Nexball)
                    mutatorBlocked = false;
                string mutator = mutatorBlocked ? " (MUTATOR WEAPON)" : "";
                Write($"// {{{{{{ #{headingNumber}: {section.Name}{mutator}\n");
                foreach (string line in lines)
                    Write(line);
                Write("// }}}\n");
                ctx.Print($"#{section.RegistryId}: {section.Name}: {lines.Length} settings...");
                settings += lines.Length;
                sectionNumber++;
            }

            if (!aborted)
                ctx.Print($"Totals: {sectionNumber} {kind}, {settings} settings");

            writer.Flush();
            ctx.Print($"Dumping {kind}... File located in ^2data/{filename}^7.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Print($"^1Error: ^7Could not open file '{filename}'!");
        }

        return true;
    }

    // DarkPlaces FS_CheckNastyPath applied to the filename below the writable data directory.
    private static bool IsDumpFilename(string filename) =>
        filename.Length <= 1018 && filename[0] is not ('/' or '.') &&
        !filename.EndsWith('/') && !filename.Contains("//", StringComparison.Ordinal) &&
        !filename.Contains("..", StringComparison.Ordinal) && !filename.Contains("/.", StringComparison.Ordinal) &&
        !filename.Any(c => char.IsControl(c) || c is '\\' or ':');

    private const string WeaponDumpHeader = """
        // ************************************************************************************ //
        // **                 WARNING - MANUALLY EDIT THIS FILE WITH CAUTION                 ** //
        // **                                                                                ** //
        // **  This file can be automatically generated with the command 'dumpweapons'.      ** //
        // **  This is useful to create custom weapon balance configs, after changing some   ** //
        // **  weapon balance cvars in-game.                                                 ** //
        // **  If you are creating a custom weapon balance config file, delete this.         ** //
        // **                                                                                ** //
        // **  If you are just editing the default weapon balance (bal-wep-xonotic.cfg),     ** //
        // **  please keep this comment as it contains helpful documentation.                ** //
        // **                                                                                ** //
        // **  These are the default weapon-related balance config settings, categorized by  ** //
        // **  weapon.                                                                       ** //
        // **  Other default settings are in balance-xonotic.cfg, which executes this file.  ** //
        // **  Other wep balance configs like bal-wep-nexuiz25.cfg first execute this, then  ** //
        // **  override some settings.                                                       ** //
        // **  Therefore any changes made to the default balance here will affect all        ** //
        // **  bal-wep-*.cfg files, unless you override it in such files.                    ** //
        // **                                                                                ** //
        // **  If you add a new weapon cvar, remove a weapon cvar, or rename a weapon cvar,  ** //
        // **  please regenerate this file with that command, or manually add it here.       ** //
        // **                                                                                ** //
        // ************************************************************************************ //
        """;
}
