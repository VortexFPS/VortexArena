# Rebranding: Xonotic → Vortex Arena

An inventory of **every piece of media and game-facing reference** that carries the
Xonotic identity, and what each one needs to become for the rebrand to *Vortex Arena*
to be complete. This is an analysis/checklist — it does not itself change anything.

> **Scope note.** This document covers *product/brand surfaces* — things a player sees,
> hears, or that identify the game to other software. It deliberately does **not** cover
> internal code identifiers, which stay as-is (see [What stays](#what-stays)).

---

## The naming rule (updated)

> **This supersedes the earlier split.** The original rule kept a hard line: brand → *Vortex
> Arena*, but all internal identifiers (namespaces, `.sln`/`.csproj`, assemblies, artifacts,
> campaign id) stay `VortexArena`. **Per Decision 3, internal IDs are now being renamed too.**

Where things land now:

- **Brand / product surfaces → `Vortex Arena`.** Anything a player reads on screen, the OS
  window title, app metadata, logo/wordmark, hostnames, first-run copy.
- **Internal IDs → `VortexArena`, all of them, this pass.** Per Decision 3 the rename is a
  clean-break big-bang covering both Tier 0 (campaign id, gamename, env var, config filenames,
  bundle id) **and** Tier 1 (namespaces `VortexArena.* → VortexArena.*`, `.sln`/`.csproj`,
  assemblies, artifact filenames). See [Decision 3](#decision-3--internal-id-rename-scope).
- **Genuinely frozen** — only upstream-lineage comments, `.po` msgid keys, Xonotic
  attribution, and the `"DarkPlaces"` wire protocol. See [What stays](#what-stays-unchanged-even-after-decision-3).

Three items below are **not mechanical renames** — they are product/legal/scope decisions.
Decisions **1** (own master servers) and **3** (rename internal IDs) are settled; Decision
**2** (assets/trademark) is a spectrum. All three are elaborated in
[Key decisions](#key-decisions--elaborated).

---

## The single biggest fact

**Almost no brand media lives in this repo.** The repository ships *zero* committed logo,
icon, or splash images (only throwaway `screenshots/` and gitignored `_scratch/`). Every
visible logo, background, HUD graphic, sound, and map is mounted at runtime from the
**Xonotic assets pack** (`assets/data/`, populated by `download-assets.sh` from the
upstream `gitlab.com/xonotic` repos). So the rebrand has two very different halves:

1. **In-repo strings & config** — small, mechanical, ~15 touch-points (tables A–C, E, G).
2. **The assets pack** — the maps, models, sounds, music, *and the logo/wordmark texture* —
   which is Xonotic-branded content the port does not own (tables D, F). This is the large,
   open-ended half and is as much a **licensing/trademark** question as an engineering one.

---

## A. In-repo user-facing text strings

Small, mechanical, self-contained. These are literals a player sees on screen.

| # | File | Line | Current | Proposed | Where it shows |
|---|------|------|---------|----------|----------------|
| A1 | `game/menu/MainMenu.cs` | 93 | `"XONOTIC"` | `"VORTEX ARENA"` | Main-menu wordmark — **fallback only**, used when the logo texture (D1) is absent |
| A2 | `game/menu/MainMenu.cs` | 99 | `"VortexArena"` | `"Vortex Arena"` | Main-menu subtitle, always visible under the logo |
| A3 | `game/menu/dialogs/DialogFirstRun.cs` | 64–65 | `"Welcome to Xonotic, please select…"` | `"Welcome to Vortex Arena…"` | First-run setup dialog |
| A4 | `game/menu/dialogs/DialogTermsOfService.cs` | 40 | `"Welcome to Xonotic! Please read the…"` | `"Welcome to Vortex Arena!…"` | Terms-of-service header |
| A5 | `game/console/ConsoleOverlay.cs` | 304 | `"VortexArena console. …"` | `"Vortex Arena console. …"` | In-game console (backtick) header |
| A6 | `game/menu/dialogs/DialogBindingsReset.cs` | 49 | `"Reset every key binding to the Xonotic defaults"` | `"…to the default bindings"` | Button tooltip (hover) |

## B. Project & export identity (app metadata / window title)

These set the OS-level app name, window title bar, and installer/bundle identity.
The window title bar currently reads **"VortexArena"** (Godot derives it from `config/name`
— no explicit title is set in code).

| # | File | Line | Current | Proposed | Notes |
|---|------|------|---------|----------|-------|
| B1 | `project.godot` | 13 | `config/name="VortexArena"` | `"Vortex Arena"` | **This is the OS window-title bar text.** ⚠ Godot's `user://` path is derived from this — see G-note |
| B2 | `project.godot` | 14 | `config/description="Xonotic, reborn on Godot + C#…"` | Vortex Arena description | Metadata only |
| B3 | `export_presets.cfg` | 64 | `application/product_name="VortexArena"` | `"Vortex Arena"` | Windows .exe product name (file properties) |
| B4 | `export_presets.cfg` | 65 | `application/file_description="VortexArena client"` | `"Vortex Arena client"` | Windows .exe description |
| B5 — **DONE (already correct in the file)** | `export_presets.cfg` | 175 | `application/bundle_identifier="org.vortexarena.client"` | e.g. `"org.vortexarena.client"` | macOS bundle id — **changing it after first release re-identifies the app** to the OS |

> **Kept as-is (artifact filenames):** `export_path=".../VortexArena.exe"`,
> `vortexarena-dedicated.x86_64`, preset *names* (`windows-client`, etc.), and
> `project/assembly_name`. These are release-artifact / codename surfaces per the naming
> rule, not player-facing brand.

## C. Application icon (currently **missing** — must be *created*)

There is **no app icon at all** today — `application/icon=""` in every export preset, and
no `icon.svg`/`.ico`/`.icns` exists in the repo. This is a create-from-scratch task, not a
replace.

| # | Where | Action |
|---|-------|--------|
| C1 | new asset | Design a Vortex Arena icon; export `.ico` (Windows), `.icns` (macOS), and a source `.svg`/`.png` |
| C2 | `export_presets.cfg` :58 (win), :173 (mac) | Set `application/icon` / `application/console_wrapper_icon` to the new files |
| C3 | `project.godot` | Optionally add `config/icon` for the Godot editor / taskbar |

## D. Visual media in the assets pack ⚠ (external Xonotic content)

These are **not in the repo** — they are mounted from `assets/data/`. The code references
them by fixed VFS paths, so if you supply replacement Vortex Arena art at the same paths,
**no code change is needed**. The exception is D1, which is brittle.

| # | Asset path (in `assets/data/`) | Referenced by | What it is |
|---|-------------------------------|---------------|-----------|
| D1 | `gfx/menu/luma/background_l2.tga` | `game/menu/framework/MenuSkin.cs:107` | **The menu logo.** Not a standalone file — the code **crops a fixed pixel region `Rect2I(46,0,1367,466)`** out of this 2560×2048 layer to extract the Xonotic wordmark+eagle. A Vortex Arena logo either has to occupy that exact region of a replacement layer, **or** `LoadLogo()` must be repointed at a proper standalone logo asset (recommended). |
| D2 | `gfx/menu/luma/background.tga` | `game/menu/MenuRoot.cs:188` | Full-screen main-menu backdrop (the space/earth photo) |
| D3 | `gfx/loading.tga` | `game/LoadingScreen.cs:221` | Loading-screen background |
| D4 | `gfx/menu/luma/cursor.tga` | `game/menu/framework/MenuSkin.cs:80` | Menu cursor (Xonotic-styled, not strictly branded) |

> **Recommendation for D1:** replace the fragile crop with a dedicated
> `gfx/menu/vortex_logo.*` asset and simplify `LoadLogo()`. The current region-crop is a
> hidden dependency on Xonotic's exact atlas layout.

## E. Audio

There is **no announcer line that says "Xonotic"** — the announcer is a generic,
event-driven system (countdown, kills, etc.). The Xonotic identity here is structural, not
spoken.

| # | Path / cvar | Referenced by | Note |
|---|-------------|---------------|------|
| E1 | `sound/announcer/default/*.ogg` (VFS) | `game/hud/HudNotifications.cs:431`, `game/net/NetGame.cs:1602` | Announcer voice tree, mounted from the assets pack. Part of Xonotic content (table F), no spoken brand — no rename needed unless you re-voice it |
| E2 | menu music (`assets/data`, xonotic-music.pk3dir) | `download-assets.sh` | Xonotic soundtrack — content/licensing item, not a string |

## F. Game content — the assets pack itself ⚠ DECISION / licensing

`download-assets.sh` clones the upstream **`gitlab.com/xonotic`** repos:
`xonotic-data.pk3dir`, `xonotic-music.pk3dir`, `xonotic-maps.pk3dir`, plus fonts and
compiled map `.pk3`s from `dl.xonotic.org`. **All maps, player/weapon models, textures,
sounds, music, and gfx are Xonotic media.** They remain Xonotic-branded — and Xonotic-owned
— unless replaced or recreated.

This is the open-ended half of the rebrand and is primarily a **trademark/licensing**
question, not a code question:

- "Xonotic" is the upstream project's name/mark; a *fork* redistributing the original art
  and the mark is a legal question, not a `sed` job.
- Content is GPLv2+/CC — replacing it is a large art/audio effort (many maps, models,
  textures) that lands outside the code entirely.
- See the [Decision points](#decision-points) and the licensing note (I) below.

## G. Persistent identity & config strings

These ship as *defaults* and end up baked into servers' listings and users' saved config.

| # | File | Line | Current | Proposed | Note |
|---|------|------|---------|----------|------|
| G1 — **DONE 2026-07-30** | `src/VortexArena.Server/Cvars.cs` | 378 | `hostname` default `"Xonotic VortexArena Server"` | `"Vortex Arena Server"` | Shown in the server browser & server-info dialog |
| G2 — **DONE 2026-07-30** | `game/net/NetGame.cs` | 74, 386, 399 | `"VortexArena Listen Server"` (×3) | `"Vortex Arena Listen Server"` | Scoreboard/server-info default |
| G3 — **DONE 2026-07-30** | `game/Shell.cs` | 614 | `"VortexArena Listen Server"` | `"Vortex Arena Listen Server"` | Fallback when `hostname` is empty |
| G4 — **DEFERRED by decision 2026-07-30** | `campaign.cfg` | 1 | `set g_campaignxonoticbeta_index 1` | **rename** → e.g. `g_campaignvortexbeta_index` | Campaign id `xonoticbeta` is baked into the cvar **name**; progress persists as `g_campaignxonoticbeta_index` in `config.cfg`. **Decision 3 — renaming.** Pre-release, resetting progress is acceptable; otherwise add a one-time cvar-copy migration. Rename the campaign data file/dir it points at too. See [Decision 3](#decision-3--internal-id-rename-scope) |
| G5 | `game/UserPaths.cs` | 29 | user data dir `~/XonData` | keep `XonData`; rename env var | Folder `XonData` is already de-Xonoticized — fine. The **env-var override `VORTEX_USERDIR`** → `VORTEX_USERDIR` (Tier 0, dev/CI only; update CI scripts + tests that set it) |

> **G-note (config path coupling):** Godot's `user://` location is derived from
> `project.godot`'s `config/name`. `UserPaths.cs` already redirects real user data to
> `~/XonData` and migrates the legacy `%APPDATA%\Godot\app_userdata\VortexArena` path — so
> changing B1 won't strand saves, but re-verify the migration path after B1 lands.

## H. Network identity / master server — ✅ DECIDED: own masters

**Decision 1 is made: Vortex Arena runs its own master servers** with its own `gamename`,
forming an independent ecosystem separate from Xonotic. These three must change **together**
so client filter, server heartbeat, and server reply all agree:

| # | File | Line | Current | Change to |
|---|------|------|---------|-----------|
| H1 | `game/menu/ServerBrowser.cs` | 97 | `GameName = "Xonotic"` | `"VortexArena"` — the dpmaster gamename filter the browser queries with |
| H2 | `game/menu/ServerBrowser.cs` | 108–112 | `Masters` = `dpm{4,6}.xonotic.xyz`, `master3.xonotic.org` | your own hosts, e.g. `master1.vortexarena.<tld>:27950` (2+ for redundancy) |
| H3 | `game/net/ServerNet.cs` | 641 | `["gamename"] = "Xonotic"` | `"VortexArena"` — the infostring servers reply with; must match H1 or the client filters them out |
| H4 | server heartbeat defaults (`sv_masterextra*`, ServerNet.cs:556 area + server cfg) | — | Xonotic masters | your masters, or servers never register |
| H5 | `tests/VortexArena.Tests/MasterServerProtocolTests.cs` | 39, 49, 51, 53 | pins `"Xonotic"` in the wire bytes | update to `"VortexArena"` (byte-format assertions otherwise stay) |

See [Decision 1](#decision-1--master-servers--network-identity-decided) for what standing up
your own masters entails and why the wire protocol itself does **not** change.

## I. Docs & licensing

| # | File | Note |
|---|------|------|
| I1 | `COPYING` | Header still reads *"VortexArena Licensing … a port of the Xonotic game code."* Prose brand — update to Vortex Arena while **keeping** the upstream-attribution paragraphs (GPLv3 derivation requires them). |
| I2 | `README.md`, `CLAUDE.md`, `planning/README.md`, `docs/*` | Already rebranded to Vortex Arena (2026-07-09). Cross-check for stragglers before release. |
| I3 | trademark | Decide how the fork refers to Xonotic: attribution ("a fork of Xonotic") is fine and required by the GPL lineage; using the Xonotic *name/logo as your own brand* is not. Ties into F and H1. |

---

## Key decisions — elaborated

Three things here are **not** mechanical find-and-replace. Each is a product/legal/scope
call with consequences beyond the edit itself. Decisions 1 and 3 are now made (per project
direction); Decision 2 is a spectrum to walk over time. Full reasoning follows.

### Decision 1 — Master servers & network identity (✅ DECIDED)

**What the mechanism actually is.** The server browser finds internet games by sending a
`getservers` query to *master servers* (dpmaster daemons) carrying two keys: a **`gamename`
filter** (`"Xonotic"`) and a **DP protocol version** (`3`). The master replies with the list
of servers registered under that gamename. Each game **server** independently *heartbeats* to
those same masters (`sv_masterextra*`) to register itself, and answers the browser's `getinfo`
probe with an infostring that includes `gamename="Xonotic"`. The client only lists servers
whose gamename matches its own filter. So three places must agree: the client's filter (H1),
the server's registration target (H4), and the server's reply (H3).

**The decision made: run our own masters with our own gamename.** Concretely: stand up
dpmaster instances at DNS names we control, and flip the gamename to `"VortexArena"` on both
client and server.

**Options that were on the table**
- **(a) Own gamename + own masters — CHOSEN.** A fully independent server list. Xonotic
  servers never appear in VA's browser (different gamename); VA servers never appear in
  Xonotic's. Clean separation, full control.
- **(b) Keep `gamename="Xonotic"`, just mirror the masters.** Pointless — you'd be a private
  window onto Xonotic's ecosystem while claiming a separate brand. Rejected.
- **(c) Stay entirely on Xonotic's masters.** Maximum player population on day one, but VA is
  then a *reskinned client on someone else's network*, not a distinct game. Rejected in favor
  of an independent identity.

**Considerations baked into the choice**
- **Protocol ≠ brand.** The wire protocol string is fixed as `"DarkPlaces"` (dpmaster's
  grammar) and the DP protocol version stays `3`. These are *transport*, not brand — you run
  **stock, unmodified dpmaster** and simply occupy a new gamename on it. Nothing in
  `MasterServerProtocol.cs` changes except the gamename value the callers pass.
- **Infrastructure.** dpmaster is a tiny C daemon. Host **2+** for redundancy on stable DNS
  (`master1/2.vortexarena.<tld>`), UDP 27950 (or a port you pick). This is an ops task, not a
  code task.
- **Cold-start is the real cost.** The instant you switch gamename, VA's server list is
  **empty** until VA servers exist and point at your masters — you forfeit access to Xonotic's
  existing servers and players. That's the deliberate price of an independent identity. Plan
  for seeding a few official servers at launch so the browser isn't empty.
- **Keep the escape hatches.** `Masters` stays a mutable list + cvar so users can point at a
  private/LAN master (already supported). Good for testing and community masters.
- **Optional, additive later:** a Steam-lobby or web-based server list (see
  `planning/OPEN-QUESTIONS.md` Q9). Not required by this decision.

### Decision 2 — Assets & trademark (the content pack)

**What it is.** Every map, model, texture, sound, and music track is upstream Xonotic content
mounted from `assets/data/` (table F). The name "Xonotic" and its wordmark/eagle are the
upstream project's identity. Two intertwined questions: *(i) legal* — may you redistribute the
content and drop/keep the mark? *(ii) product* — how visually independent should VA be?

**Legal baseline (why this is allowed at all)**
- **Code** is GPLv3-or-later; you already comply — just keep the upstream attribution.
- **Content** (art/music/maps) is per-file GPLv2+/CC-BY-SA. Those licenses **permit**
  redistribution and modification, with **attribution + share-alike**. So a fork *may* ship the
  content.
- **Trademark ≠ copyright.** Even where the license lets you copy a file, the *name and logo*
  are a project mark. Using them to **attribute** ("a fork of Xonotic") is fine and expected.
  Using the Xonotic **wordmark/eagle as VA's own logo** is exactly what the rebrand removes —
  that's the point.

**Options — a spectrum, not a binary**
- **Option A — Reskin on Xonotic content (fast).** Keep all upstream maps/models/sounds;
  replace only the *brand surfaces* (logo, name, menu identity, master). Ships now. Downside:
  the maps/models are visibly Xonotic's; the mark is gone but the lineage is obvious. Legal
  with attribution.
- **Option B — Curated divergence (medium) — RECOMMENDED.** Start from A, then progressively
  replace the highest-identity assets: menu logo/backgrounds (D1–D3), a distinct HUD skin, a
  few signature maps and player models. VA gradually becomes its own game. Realistic path.
- **Option C — Fully original content (large, long-term).** Replace *everything* with original
  or differently-sourced art/audio. Only this makes VA fully independent of Xonotic content.
  It's a multi-person, multi-month art track — effectively separate from the code rebrand, and
  **not** a release blocker.

**Considerations**
- **Attribution stays regardless of option** — the *code* lineage is GPL and requires crediting
  Xonotic in `COPYING`/credits even under Option C.
- **The logo (D1) is the one asset to replace first and unconditionally** — it *is* the mark,
  and today it's a brittle pixel-crop out of `background_l2.tga`. Replace with a dedicated
  `gfx/menu/vortex_logo.*` and simplify `LoadLogo()`.
- **Asset delivery:** under A/B you keep pulling from `gitlab.com/xonotic` via
  `download-assets.sh`; under C you'd host your own asset repo.
- **Recommendation:** Option B, phased. Brand surfaces + logo now; diverge signature assets
  over time; treat full C as an aspiration, not a blocker.

### Decision 3 — Internal-ID rename scope (✅ renaming; choose *how far*)

The earlier naming rule froze all internal identifiers as `VortexArena`. **That is now
reversed: internal IDs are being renamed too, including the campaign.** "Internal IDs" spans a
wide cost range, so the real decision is *how far to go now*. Two tiers:


> **Status 2026-07-30.** Hostname / listen-server defaults (G1—G3) are **done**: the server `hostname`
> cvar now defaults to `"Vortex Arena Server"` and the four listen-server display strings to
> `"Vortex Arena Listen Server"`. The macOS bundle id (B5) was **already** `org.vortexarena.client`.
> Full suite green afterwards: 3,933 passed, 0 skipped.
>
> **The campaign id (G4) is deliberately NOT done**, by decision: the id is baked into the cvar *name*
> `g_campaignxonoticbeta_index`, so renaming it orphans any saved campaign progress. That is cheap now
> and expensive later, but it was judged not worth doing in the same pass as cosmetic display strings.
> Revisit before the first release that advertises campaign progress as persistent.
>
> **`gamename` (H1/H3) is NOT a display string and was left alone.** `"Xonotic"` appears in
> `MasterServerProtocol`, `ServerBrowser` and `ServerNet` as the dpmaster/server-browser game filter — a
> **protocol identifier**. Changing it makes our servers invisible to anything filtering on the old
> value, so it is a compatibility decision, not a branding one, and needs its own call.
>
> **Do not trust the "current value" column below for anything.** The Tier-1 namespace sweep rewrote
> string literals inside this document too, so several rows now read `X —> X` with both sides
> identical (see the bundle-id and env-var bullets). Verify against the actual file before acting.

**Tier 0 — Player-adjacent internal IDs (cheap, low-risk — do now, folded into Phase 1)**
- **Campaign id** `xonoticbeta` → e.g. `vortexbeta`: `campaign.cfg`, the campaign data
  file/dir it names, and every reader of the id. **Migration:** progress lives in the user cvar
  `g_campaignxonoticbeta_index`; renaming orphans it. Pre-release, a reset is acceptable —
  otherwise add a one-time cvar-copy on first run. (G4)
- **`gamename`** → `"VortexArena"` (this *is* part of Decision 1). (H1/H3)
- **hostname / listen-server defaults** (G1–G3, A2).
- **macOS bundle id** `org.vortexarena.client` → `org.vortexarena.client` (B5).
- **env var** `VORTEX_USERDIR` → `VORTEX_USERDIR` (dev/CI; update CI + tests). (G5)
- **your own config filenames** (`xonotic-client.cfg`, `xonotic-server.cfg`,
  `binds-xonotic.cfg`) → `va-*` / `vortex-*`, updating the code that loads them by name.
  *Caveat:* keep it clear which cfgs mirror upstream defaults, so the parity-diff tooling that
  compares against `Base/data/.../*.cfg` still lines up.

**Tier 1 — Solution / assembly / namespace (large, high blast radius — separate refactor)**
- **C# namespaces `VortexArena.*`** across ~250 files, all `using` lines, `VortexArena.sln`,
  every `.csproj`, `assembly_name` / `RootNamespace`. Mechanical but sweeping. **Traps:** the
  Roslyn **source generators** (`RegistryGenerator` etc.) emit code referencing these names, and
  any **reflection-/attribute-by-name** lookups — both must be renamed in lockstep or the build
  breaks subtly. Verifiable in isolation: build + the full 2 900-test suite must stay green.
- **Release-artifact filenames** (`VortexArena.exe`, `vortexarena-dedicated.x86_64`),
  `export_path`s, `tools/package.sh`, `.github/workflows/release.yml`, **and the
  launcher/updater `latest.json` manifest** — renaming artifacts **breaks updater continuity**
  across the rename boundary, so coordinate the cutover with the launcher's expected filenames
  (see the launcher/updater track).
- **Local checkout directory** and this project's **naming rule / memory** are superseded.

**Scope decision: Tier 0 + Tier 1 together (✅ chosen).** This pass is a clean-break big-bang
rebrand — brand strings, all player-adjacent IDs *and* the `VortexArena.* → VortexArena.*`
namespace sweep, `.sln`/`.csproj`/assembly names, and artifact filenames all move at once.

Execution notes for the big-bang (to keep it green):
- **Namespaces + source generators must move in lockstep.** Rename `VortexArena.*` →
  `VortexArena.*` everywhere, *including* the strings the Roslyn generators
  (`RegistryGenerator` and friends) emit and any reflection/attribute-by-name lookups. Rebuild
  from clean — stale generated code is the classic post-rename break.
- **Solution/project cutover:** `VortexArena.sln` → `VortexArena.sln`, every `*.csproj`
  filename + `RootNamespace`/`AssemblyName`, `project/assembly_name` in `project.godot`.
- **Artifacts + updater in one motion:** `export_path`s, `tools/package.sh`,
  `.github/workflows/release.yml`, and the launcher/updater `latest.json` **must all adopt the
  new artifact names in the same release** — the updater loses continuity across the rename
  boundary, so the first VortexArena-named release is a deliberate cutover (document it in the
  launcher track / RELEASING).
- **Proof:** clean `dotnet build` + the full ~2 900-test suite green is the whole verification
  for the mechanical sweep; run `ci/ci.sh` before merge.
- **Suggested ordering even within the big-bang:** land the brand strings + Tier-0 IDs first in
  the branch history (small, readable commits), then the namespace/solution sweep as the final
  commit(s) so a reviewer can bisect brand-intent edits from mechanical renames.

## Suggested phasing

- **Phase 1 — In-repo brand + Tier-0 IDs (mechanical):** tables A, B, C, E-strings, G1–G5, H1–H5,
  I1, plus the campaign-id rename. No external dependencies beyond one icon asset + the master
  DNS names. Ships a build whose window, menus, console, and server listings all say Vortex
  Arena and that queries *our* masters.
- **Phase 2 — Logo / visual (D1–D3):** dedicated Vortex Arena logo asset + `LoadLogo()` cleanup,
  then backdrops. Needs art (Decision 2, Option B first step).
- **Phase 3 — Infrastructure:** stand up the dpmaster instances (Decision 1) and seed official
  servers so the browser isn't empty at launch.
- **Phase 4 — Tier-1 rename (this pass, final commits):** the `VortexArena.* → VortexArena.*`
  namespace/solution/assembly/artifact sweep, coordinated with the launcher's artifact filenames
  (the first VortexArena-named release is the updater cutover). Big-bang per Decision 3.
- **Phase 5 — Content divergence (long-term):** replace signature Xonotic maps/models/sounds
  with original assets (Decision 2, toward Option C).

## What stays (unchanged even after Decision 3)

Only these remain genuinely frozen:

- **Source comments and porting notes** that reference upstream Xonotic / QuakeC / DarkPlaces —
  they document lineage and parity, and must keep the upstream names to stay meaningful.
- **`.po` localization `msgid` context keys** that mirror upstream string ids (changing them
  desyncs from upstream translation sources).
- **Attribution** of Xonotic in `COPYING` / credits (GPL lineage requires it).
- **The `"DarkPlaces"` protocol string and DP protocol version 3** — transport interop, not
  brand (see Decision 1).

Everything the old naming rule froze — namespaces, `.sln`/`.csproj`, assemblies, artifact
filenames, config filenames, the campaign id — is now **in scope** for renaming per Decision 3;
the only question is Tier-0-now vs. Tier-0+Tier-1-now.
