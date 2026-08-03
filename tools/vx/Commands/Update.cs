namespace Vx.Commands;

/// <summary>
/// <c>vx update</c> — pull, then say what the pull implies.
///
/// <para><b>Why this is not just <c>git pull</c>.</b> A pull can change three kinds of thing that a clone
/// does not notice on its own: the map lockfile, the engine-template lockfile, and the C# sources. Each has
/// its own fetch/build command, and the failure mode of not knowing that is a session spent debugging a
/// "broken" tree that is merely half-updated. So this pulls, diffs what moved, and names the follow-up.</para>
///
/// <para><b>The dirty-tree question is asked, not assumed.</b> Every other design here would be wrong for
/// someone: refusing outright blocks the contributor who just wants the fix, auto-stashing surprises the
/// person mid-change, and <c>--force</c>-style discarding is how work disappears. So a dirty tree stops and
/// asks, with the file list on screen — and then vx carries out whichever answer was given, because "run
/// these four git commands yourself" is the advice that made the question worth avoiding in the first
/// place. Non-interactive callers get a refusal and the three flags that state intent up front.</para>
///
/// <para><b>Discarding still leaves a way back.</b> <c>--discard</c> parks the changes in a labelled stash
/// before cleaning, and says so. The working tree ends up exactly as clean as the word implies; what it does
/// not do is make "I picked the wrong menu item" unrecoverable. `git stash pop` undoes it.</para>
/// </summary>
internal static class Update
{
    private enum Disposition { Ask, Stash, Keep, Discard, Cancel }

    internal static int Run(string[] args, bool json)
    {
        bool dryRun = args.Contains("--dry-run");
        bool yes = args.Contains("--yes") || args.Contains("-y");
        Disposition want =
            args.Contains("--stash") ? Disposition.Stash
            : args.Contains("--keep") ? Disposition.Keep
            : args.Contains("--discard") ? Disposition.Discard
            : Disposition.Ask;

        string? git = Env.Which("git");
        if (git is null)
        {
            Console.Error.WriteLine("vx update: git is not on PATH.");
            Console.Error.WriteLine($"           {Packages.Advice("git", "install git — https://git-scm.com/downloads")}");
            return 1;
        }

        if (Env.Run(git, "rev-parse", "--is-inside-work-tree").Out.Trim() != "true")
        {
            Console.Error.WriteLine("vx update: this clone is not a git working tree — nothing to pull.");
            Console.Error.WriteLine("           (A release unzip is not a clone. Re-clone to track updates.)");
            return 1;
        }

        string before = Head(git);
        string branch = Env.Run(git, "rev-parse", "--abbrev-ref", "HEAD").Out.Trim();
        Console.WriteLine();
        Console.WriteLine($"vx update — {branch} @ {Short(before)}");
        Console.WriteLine();

        // ---- the dirty tree ----------------------------------------------------------------------------
        string[] dirty = Env.Run(git, "status", "--porcelain").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .ToArray();

        if (dirty.Length > 0)
        {
            Console.WriteLine($"You have {dirty.Length} uncommitted change(s):");
            foreach (string d in dirty.Take(20)) Console.WriteLine($"    {d}");
            if (dirty.Length > 20) Console.WriteLine($"    … and {dirty.Length - 20} more");
            Console.WriteLine();

            if (want == Disposition.Ask)
            {
                if (dryRun) { Console.WriteLine("--dry-run: would ask what to do with them, then pull."); return 0; }
                // Deliberately no default-on-Enter. Each branch does something different to work that is not
                // vx's, and a stray keypress should not be what picks.
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("vx update: uncommitted changes, and no terminal to ask at. Say which you want:");
                    Console.Error.WriteLine("           --stash     set them aside, pull, leave them stashed");
                    Console.Error.WriteLine("           --keep      set them aside, pull, re-apply them on top");
                    Console.Error.WriteLine("           --discard   throw them away (a labelled stash is kept)");
                    return 1;
                }
                want = AskDisposition();
            }

            if (dryRun) { Console.WriteLine($"--dry-run: would {want.ToString().ToLowerInvariant()} them, then pull."); return 0; }

            int rc = ApplyDisposition(git, want, dirty);
            if (rc != 0) return rc;
            Console.WriteLine();
        }
        else if (dryRun)
        {
            Console.WriteLine("--dry-run: tree is clean; would pull.");
            return 0;
        }

        // ---- the pull ----------------------------------------------------------------------------------
        // FAST-FORWARD ONLY. "Get me the latest" should never silently author a merge commit on someone's
        // behalf, and on a branch with local commits the refusal is the useful answer — it says the tree has
        // diverged, which is a thing to decide about rather than paper over.
        Console.WriteLine("→ git pull --ff-only");
        if (Env.Exec(git, ["pull", "--ff-only"]) != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("vx update: could not fast-forward. Usually this means the branch has diverged —");
            Console.Error.WriteLine("           you have local commits that are not upstream. Pick deliberately:");
            Console.Error.WriteLine("             git pull --rebase     replay your commits on top of theirs");
            Console.Error.WriteLine("             git pull --no-ff      make a merge commit");
            Console.Error.WriteLine("             git log --oneline @{u}..HEAD    see what is yours");
            if (want == Disposition.Stash || want == Disposition.Keep)
                Console.Error.WriteLine("           Your changes are in `git stash list` — nothing was lost.");
            return 1;
        }

        string after = Head(git);

        // ---- re-apply, if that was the answer ----------------------------------------------------------
        if (want == Disposition.Keep)
        {
            Console.WriteLine();
            Console.WriteLine("→ git stash pop   (re-applying your changes on top)");
            if (Env.Exec(git, ["stash", "pop"]) != 0)
            {
                // pop only drops the stash on success, so a conflict leaves the entry intact. Saying so is
                // the difference between "resolve these markers" and "did I just lose my afternoon".
                Console.Error.WriteLine();
                Console.Error.WriteLine("vx update: your changes conflict with what was pulled. The stash is STILL THERE");
                Console.Error.WriteLine("           (`git stash list`) — resolve the conflicts, then `git stash drop`.");
                return 1;
            }
        }

        // ---- what moved, and what that implies ---------------------------------------------------------
        Console.WriteLine();
        if (before == after)
        {
            // git has already printed its own "Already up to date." — this adds the half it does not cover.
            Console.WriteLine("Nothing new — no re-fetch or rebuild needed.");
            return 0;
        }

        string[] changed = Env.Run(git, "diff", "--name-only", before, after).Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Replace('\\', '/'))
            .ToArray();
        int commits = int.TryParse(Env.Run(git, "rev-list", "--count", $"{before}..{after}").Out.Trim(), out int n) ? n : 0;

        Console.WriteLine($"Updated {Short(before)} → {Short(after)} ({commits} commit(s), {changed.Length} file(s)).");
        Console.WriteLine();

        var todo = new List<string>();
        if (changed.Contains("data/maps.lock.json"))
            todo.Add("./vx maps        the pinned map packs changed");
        if (changed.Contains("engine.lock.json") || changed.Contains("tools/godot.lock.json"))
            todo.Add("./vx engine      the pinned engine/export templates changed");
        if (changed.Any(c => c.EndsWith(".cs") || c.EndsWith(".csproj") || c.EndsWith(".props")))
            todo.Add("./vx build       C# sources changed");
        // The shim rebuilds vx itself on the next invocation, so this is a courtesy note rather than a task —
        // but an unexplained "building the task runner…" right after an update looks like a fault.
        if (changed.Any(c => c.StartsWith("tools/vx/")))
            todo.Add("(vx itself changed — the next ./vx rebuilds the task runner automatically)");

        if (todo.Count == 0)
        {
            Console.WriteLine("Nothing to re-fetch or rebuild.");
            return 0;
        }

        Console.WriteLine("Next:");
        foreach (string t in todo) Console.WriteLine($"  {t}");
        Console.WriteLine();
        Console.WriteLine("`./vx doctor` confirms the result; `./vx ci` is the full gate.");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------

    private static Disposition AskDisposition()
    {
        Console.WriteLine("What should happen to them?");
        Console.WriteLine("  1. stash      set aside, pull, leave them stashed for you to pop later");
        Console.WriteLine("  2. keep       set aside, pull, re-apply them on top (conflicts are yours to resolve)");
        Console.WriteLine("  3. discard    throw them away — a labelled stash is kept, so `git stash pop` undoes it");
        Console.WriteLine("  4. cancel     change nothing");
        while (true)
        {
            Console.Write("Choice [1-4]: ");
            switch (Console.ReadLine()?.Trim())
            {
                case "1": return Disposition.Stash;
                case "2": return Disposition.Keep;
                case "3": return Disposition.Discard;
                // Enter alone cancels. Of the four, doing nothing is the only one that cannot cost anybody
                // anything, so it is what an ambiguous keypress should mean.
                case "4" or "" or null: return Disposition.Cancel;
                default: Console.WriteLine("  (1, 2, 3 or 4)"); break;
            }
        }
    }

    private static int ApplyDisposition(string git, Disposition want, string[] dirty)
    {
        if (want is Disposition.Cancel or Disposition.Ask)
        {
            Console.WriteLine("Cancelled — nothing was changed.");
            return 1;
        }

        // -u so UNTRACKED files travel with the stash. Without it they would sit through the pull and then
        // collide with any incoming file of the same name, which is the confusing half of this problem.
        string label = want == Disposition.Discard
            ? $"vx update: discarded {DateTime.Now:yyyy-MM-dd HH:mm}"
            : $"vx update: {DateTime.Now:yyyy-MM-dd HH:mm}";

        Console.WriteLine($"→ git stash push -u -m \"{label}\"");
        if (Env.Exec(git, ["stash", "push", "-u", "-m", label]) != 0)
        {
            Console.Error.WriteLine("vx update: could not stash — stopping before the pull, tree untouched.");
            return 1;
        }

        Console.WriteLine(want switch
        {
            Disposition.Stash => $"  {dirty.Length} change(s) stashed. Recover with: git stash pop",
            Disposition.Keep => $"  {dirty.Length} change(s) stashed, to be re-applied after the pull.",
            _ => $"  {dirty.Length} change(s) discarded. Changed your mind? git stash pop",
        });
        return 0;
    }

    private static string Head(string git) => Env.Run(git, "rev-parse", "HEAD").Out.Trim();

    private static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
