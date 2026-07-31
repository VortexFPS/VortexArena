# Vendored: Conductor.Protocol

Verbatim copies of the announce v1 protocol types from the Conductor repo
(`Conductor/src/Conductor.Protocol/`). Upstream is the source of truth; nothing here is edited.

## Why a copy instead of a package reference

`Conductor.Protocol` is packable but is not published to any feed. Conductor itself solves the same
problem for `Launcher.Protocol` with a conditional `ProjectReference` to a sibling checkout and a
`PackageReference` fallback, which works there because everyone who builds Conductor is working on the
whole system. The game repo is the opposite case: almost everyone who clones it has no sibling
Conductor checkout and no access to a feed that could serve the fallback, so the fallback would be a
restore error rather than a fallback. A `git clone && dotnet build` has to work with nothing else on
the box.

These are five BCL-only files with no dependencies, which is the case vendoring is for.

## Keeping it in sync

The copies are byte-identical to upstream, so drift is a plain directory diff rather than a merge:

    diff -r src/VortexArena.Net/Vendor/Conductor.Protocol ../Conductor/src/Conductor.Protocol \
        --exclude=bin --exclude=obj --exclude=Conductor.Protocol.csproj --exclude=README.md

Do not fix a protocol bug here. Fix it upstream and re-copy, or the two ends of the wire stop
agreeing about what "valid" means, which is exactly what the shared validation exists to prevent.

If `Conductor.Protocol` is ever published, delete this directory and add the `PackageReference`. The
namespace is unchanged, so no calling code moves.
