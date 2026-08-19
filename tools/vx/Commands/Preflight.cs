namespace Vx.Commands;

/// <summary>One thing that must be true before the game can launch, and how to make it true.</summary>
/// <param name="Name">Short label, shown when the requirement is not met.</param>
/// <param name="Satisfied">Cheap probe. Re-run after a fix to confirm the fix worked.</param>
/// <param name="Problem">What is wrong, in a sentence a person who has never seen this tree can act on.</param>
/// <param name="Command">The command that fixes it, exactly as a person would type it. Null = not fixable here.</param>
/// <param name="Fix">Runs <paramref name="Command"/> in-process. Null = print it and let them run it.</param>
/// <param name="Fatal">True when the launch cannot proceed without it. False = warn and carry on.</param>
/// <param name="Note">Optional extra line — a cost, a caveat, a pointer.</param>
internal sealed record Requirement(
    string Name,
    Func<bool> Satisfied,
    string Problem,
    string? Command,
    Func<int>? Fix,
    bool Fatal,
    string? Note = null);

/// <summary>
/// The ushering half of <c>./vx run</c>: check what the launch needs, and offer to do whatever is missing.
///
/// <para><b>Why this exists.</b> <c>vx run</c> used to fail one requirement at a time, each with a
/// different error and a different command to type — no engine, no export templates, nothing exported, a
/// stale export, no maps. Each message was correct and each one only got you to the next one, so a fresh
/// clone took five or six round trips before anything launched. The design goal is now blunt: a person can
/// type <c>./vx run</c> at any moment, in any state of the tree, and be walked to a running game.</para>
///
/// <para><b>It asks rather than assumes.</b> Some of these fixes take hours (compiling the engine) or a
/// gigabyte of download (the maps), so an unattended <c>vx run</c> that silently started one would be
/// worse than the error it replaced. Every fix is a <c>[Y/n]</c> prompt naming the exact command. Declining
/// is a first-class answer: the command is printed so it can be run later, and the launch continues if the
/// requirement was advisory.</para>
///
/// <para><b>Non-interactive callers are never blocked on a prompt.</b> CI, a script, anything with stdin
/// redirected: the problem and the command are printed, and a fatal requirement fails the command instead
/// of hanging on a question nobody will answer. <c>--yes</c> is the way to say "do it" from a script,
/// <c>-n</c> the way to say "touch nothing, just try".</para>
/// </summary>
internal static class Preflight
{
    /// <summary>
    /// Walk the requirements in order, fixing what the caller agrees to fix.
    /// Returns false only when something FATAL is still unmet — i.e. do not launch.
    /// </summary>
    /// <param name="requirements">Ordered: earlier ones are prerequisites of later ones.</param>
    /// <param name="assumeYes">Accept every fix without asking (<c>--yes</c>).</param>
    /// <param name="noFix">Change nothing; report and continue if possible (<c>-n</c>).</param>
    internal static bool Run(IEnumerable<Requirement> requirements, bool assumeYes, bool noFix)
    {
        bool ok = true;

        foreach (Requirement req in requirements)
        {
            if (Safe(req.Satisfied)) continue;

            Console.Error.WriteLine();
            Console.Error.WriteLine($"vx run: {req.Problem}");
            if (req.Note is not null)
                Console.Error.WriteLine($"        {req.Note}");

            if (req.Command is null)
            {
                // Nothing vx can do — a broken checkout, a missing SDK. Say so plainly rather than
                // offering a fix that would not work.
                if (req.Fatal) ok = false;
                continue;
            }

            if (!ShouldFix(req, assumeYes, noFix))
            {
                Console.Error.WriteLine($"        Fix it with:  {req.Command}");
                if (req.Fatal) ok = false;
                continue;
            }

            Console.Error.WriteLine($"        → {req.Command}");
            int rc = req.Fix is null ? -1 : req.Fix();
            if (rc != 0)
            {
                Console.Error.WriteLine($"vx run: '{req.Command}' failed (exit {rc}).");
                if (req.Fatal) ok = false;
                continue;
            }

            // Re-probe rather than trusting the exit code. A command can succeed and still not produce
            // what was wanted — an export that writes nothing is the case this tree has actually hit —
            // and "it said it worked" is the least useful thing to tell someone whose game did not start.
            if (!Safe(req.Satisfied))
            {
                Console.Error.WriteLine($"vx run: '{req.Command}' reported success but {req.Name} is still "
                                        + "not in place.");
                Console.Error.WriteLine("        ./vx doctor      shows what was probed and what is missing");
                if (req.Fatal) ok = false;
            }
        }

        if (!ok)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("vx run: not launching — see above. `./vx doctor` reports the whole toolchain.");
        }
        return ok;
    }

    /// <summary>
    /// Ask, unless the caller already answered. A redirected stdin means nobody is there to ask, so the
    /// answer is no and the command is printed instead — never a prompt into a void.
    /// </summary>
    private static bool ShouldFix(Requirement req, bool assumeYes, bool noFix)
    {
        if (noFix) return false;
        if (assumeYes) return true;
        if (Console.IsInputRedirected) return false;

        Console.Error.Write($"        Run '{req.Command}' now? [Y/n] ");
        string? answer = Console.ReadLine()?.Trim();
        return answer is not { Length: > 0 } a || (a[0] is not ('n' or 'N'));
    }

    /// <summary>
    /// A probe must never be the reason the game will not start. An unreadable directory, a permission
    /// error, a lockfile someone is mid-edit on: treat a throwing probe as "satisfied" and let the real
    /// launch produce the real error, which will at least be about the real problem.
    /// </summary>
    private static bool Safe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return true; }
    }
}
