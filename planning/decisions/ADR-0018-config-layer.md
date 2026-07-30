# ADR-0018 — Vortex config is a layer, not a fork of the Xonotic config

**Status:** Accepted (2026-07-30)

## Context

This is the decision most likely to be quietly violated, because the intuitive way to change a game value
is to edit the file that sets it. So the reasoning needs to be attached to the record.

**The evidence is a divergence that was already lost, once, exactly that way.** The port shipped one
config difference: a physics preset documented in `ConfigLoader.cs` as "stock Xonotic +
`sv_step_upspeed_max 1`" and recorded in `planning/parity/cvar-diff-known.yaml` as *"Shipped as
physicsBryan.cfg; xonotic-server.cfg execs it instead of physicsX.cfg."* The mechanism was: copy an
upstream preset, hand-edit the copy, and hand-edit `xonotic-server.cfg` to exec it.

Checked against the tree on 2026-07-29:

| claim | reality |
| --- | --- |
| `physicsBryan.cfg` exists | **No.** 33 `physics*.cfg` files; that is not one of them |
| `xonotic-server.cfg` execs it | **No.** Line 675 reads `exec physicsX.cfg`, unmodified upstream |
| the tree carries local edits | **No.** No modified `.cfg` at all |

The divergence had evaporated when the content tree was re-pointed at a clean upstream checkout, and
**nothing failed loudly**. The game ran stock physics while two files in the repo described it as running
ours. Worse, the tool that should have caught it — `tools/parity-cvar-diff.py` — was comparing upstream
against itself (see [ADR-0016](ADR-0016-content-ownership.md)), so the guard was structurally incapable of
firing.

Committing the content (ADR-0016) makes such an edit *durable*, but it also makes every upstream refresh a
merge conflict against files we have no reason to own. And the touch list argues against renaming: roughly
a hundred tracked files cite `bal-wep-xonotic.cfg` and friends, almost all as **provenance comments** of
the form `// balance from bal-wep-xonotic.cfg (g_balance_arc_*)`. Those citations are how a reader checks a
ported value against its source, and what the parity registry is written against. Renaming the files would
invalidate every one for no gain.

## Decision

**The `xonotic-*.cfg` tree and everything it execs is upstream and is NEVER edited. Vortex divergence is
an additive `vortex-*.cfg` layer exec'd after it.**

The override mechanism already existed: `ConfigLoader.Load` takes `params string[] entryFiles` and
documents "later files override earlier ones (DP `set` semantics)". This is a policy plus five call-site
edits, not new machinery.

- **`vortex-common.cfg` is the only entry point** the C# names; it execs `vortex-physics.cfg`,
  `vortex-balance.cfg`, `vortex-bal-wep.cfg`, `vortex-server.cfg`, `vortex-client.cfg`. Adding a sixth
  layer file later is a content change with no code change.
- **It runs last in the chain and before `Cvar_LockDefaults`.** Last so it overrides; before the lock so
  its values become shipped *defaults* rather than values indistinguishable from ones the player typed.
- **`vortex-binds.cfg` is exec'd separately, at two sites.** `binds-xonotic.cfg` is itself exec'd twice on
  purpose — `xonotic-client.cfg:603` pulls it in before any `bind` sink exists, so those binds are parsed
  and dropped, and `MenuState` re-execs it after `BindInput.RegisterBindCommands` to actually fill
  `BindTable`; the settings scratch interpreter does it again. Wire the layer into only one and Vortex
  binds land in the boot path but not in the menu's "Reset all", or vice versa.
- **Plain `set`, never `seta`.** The shipped cfgs are the authority on which cvars are archiveable; a
  `seta` here would widen every player's `config.cfg` beyond upstream's set.
- **Empty layer files are present, not absent.** A missing file is a no-op that increments
  `FilesMissing`, and a counter that is permanently non-zero is useless as the signal that a config
  genuinely went missing.
- **The C#-side video overrides moved into `vortex-client.cfg`.** `vid_fullscreen 2` / `vid_vsync 0` were
  inline `_cvars.Set` calls in `MenuState`. Config is the better home — visible to the player, greppable,
  changeable without a rebuild — and they are deliberately *not* duplicated in C# as a fallback, because
  two sources of truth for a default is how they drift apart.

## Consequences

- The upstream chain stops diverging at all, which replaces a weak check with a strong invariant: **every
  port-side cvar difference is attributable to a `vortex-*.cfg` file.** The differ confirms it —
  `port_only_files=6, differing=0`.
- An upstream content refresh becomes a file replacement rather than a merge.
- `cvar-diff-known.yaml` shrank, and one suppression was **deleted**: `g_mod_physics` reads `Xonotic` in
  both trees now, so suppressing it hid a name that no longer diverges.
- **One real cost.** A cvar assignment cannot append to a list, so `vortex-physics.cfg` restates
  `g_physics_clientselect_options` in full to add `warsow bryan`. If upstream adds a preset, our restated
  string silently drops it and that preset becomes unreachable from the menu.
  `VortexConfigLayerTests.Restated_Physics_Preset_List_Keeps_Every_Upstream_Option` reads upstream's list
  out of `physics.cfg` and requires ours to be a superset, rather than pinning an expectation that would
  not notice.
- The video defaults no longer apply when no content tree is mounted. That is the consistent reading —
  nothing else from the shipped cfgs applies there either — but it is a change from unconditional.

## Enforcement

Policy without a test is how the last one disappeared. `VortexConfigLayerTests` asserts the values, and
every assertion was negative-tested:

| break | tests that fail |
| --- | --- |
| entry point emptied | 4 |
| an upstream preset dropped from the restated list | 1 |
| `seta` instead of `set` | 1 |
| a layer file missing | 1 |
| `xonotic-server.cfg` edited to reach the layer | 1 |

## Alternatives considered

- **Edit the upstream files directly.** This is what happened before. The edit did not survive, and no
  test or tool noticed for months.
- **Rename the upstream files to `*-vortex.cfg` and own them.** Rejected: invalidates ~100 provenance
  citations and the parity registry written against them, and makes every upstream refresh a merge.
- **Express divergence only in C#.** Rejected: invisible to the player, invisible to the cvar differ (a
  C# assignment is not in the cfg chain), and requires a rebuild to change. The video defaults moved the
  other way for exactly these reasons.
