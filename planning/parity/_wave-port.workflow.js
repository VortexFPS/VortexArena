export const meta = {
  name: 'parity-port-wave',
  description: 'Port a parity wave: tiered parallel planning -> opus per-file apply/review -> build+test gate -> opus re-verify',
  phases: [
    { title: 'Plan',   detail: 'read-only edit-spec per unit; haiku/sonnet/opus by difficulty' },
    { title: 'Apply',  detail: 'one opus owner per file applies+corrects+reviews all queued edits' },
    { title: 'Gate',   detail: 'opus: dotnet build + test, fix until green' },
    { title: 'Verify', detail: 'opus per-unit parity re-audit; update registry' },
  ],
}

// ---------- args ----------
const REPO = (args && args.repo) || 'C:/Users/Bryan/Projects/Xonotic/VortexArena'
const WAVE = (args && args.waveName) || 'parity wave'
const UNITS = (args && args.units) || []
const modelFor = t => (t === 'haiku' ? 'haiku' : t === 'sonnet' ? 'sonnet' : 'opus')

// ---------- schemas ----------
const EDIT_SPEC_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['unit', 'edits', 'unportable'],
  properties: {
    unit: { type: 'string' },
    edits: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['file', 'anchor', 'action', 'code', 'rationale'],
        properties: {
          file: { type: 'string', description: 'repo-relative path' },
          anchor: { type: 'string', description: 'unique existing snippet near the change (NOT a line number)' },
          action: { type: 'string', enum: ['insert_after', 'insert_before', 'replace', 'new_method', 'new_field', 'modify'] },
          code: { type: 'string', description: 'exact compiling C# to add/replace' },
          rationale: { type: 'string' },
          crossFileApi: { type: 'string', description: 'exact signature of any new public member other files must call' },
        },
      },
    },
    unportable: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['gap', 'reason'],
        properties: { gap: { type: 'string' }, reason: { type: 'string' } },
      },
    },
    notes: { type: 'string' },
  },
}

const APPLY_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['file', 'appliedCount', 'status'],
  properties: {
    file: { type: 'string' },
    appliedCount: { type: 'integer' },
    status: { type: 'string', enum: ['all_applied', 'partial', 'failed'] },
    corrections: { type: 'string' },
    concerns: { type: 'string' },
    crossFileApis: { type: 'array', items: { type: 'string' } },
  },
}

const GATE_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['build_passed', 'tests_passed'],
  properties: {
    build_passed: { type: 'boolean' },
    tests_passed: { type: 'boolean' },
    tests_passed_count: { type: 'integer' },
    tests_failed_count: { type: 'integer' },
    fixes_made: { type: 'string' },
    remaining_errors: { type: 'string' },
  },
}

const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['unit', 'closed', 'stillOpen', 'registryUpdated'],
  properties: {
    unit: { type: 'string' },
    closed: { type: 'array', items: { type: 'string' } },
    stillOpen: { type: 'array', items: { type: 'string' } },
    registryUpdated: { type: 'boolean' },
    driftRow: { type: 'string' },
  },
}

// ---------- prompts ----------
const planPrompt = (u) => `You are a READ-ONLY parity analyst for the Xonotic -> Godot/C# port. Do NOT edit any files. Produce a precise edit-spec that another (opus) agent will apply.

UNIT: ${u.unit}  (stream=${u.stream}, ~${u.gaps} open gap-dimensions)

STEPS (read, in this order):
1. planning/parity/specs/${u.unit}.md  -- the AUTHORITATIVE Base algorithm + exact constants/cvars/timings.
2. planning/parity/registry/${u.unit}.yaml -- the per-dimension parity rows. Every dimension whose status is NOT 'faithful' (logic/values/timing/presentation/audio) or NOT 'live' (liveness) is a gap to close. The row notes describe exactly what's missing/partial.
3. The port .cs files named in the spec's "Port refs" (read them to see the CURRENT implementation).
4. If the spec is ambiguous, grep the Base QuakeC under Base/data/xonotic-data.pk3dir for the exact behavior.

For EACH open gap, design the minimal faithful C# change. Emit edits[] of {file, anchor, action, code, rationale, crossFileApi?}:
- file: repo-relative (e.g. src/VortexArena.Common/Gameplay/GameTypes/FreezeTag.cs).
- anchor: a UNIQUE existing snippet (a method signature / distinctive line) near the change site -- NOT a line number; the applier locates by it.
- code: REAL compiling C# matching the file's style/naming/comment-density. Reuse existing port helpers/patterns.
- WIRE IT LIVE: if you add a method/field, ALSO add the edit at the live call site that invokes it. A coded-but-uncalled feature does NOT close a liveness gap -- you must close logic AND liveness together. Match Base constants/timing exactly (values/timing dimensions).
- crossFileApi: if an edit adds/changes a public member that ANOTHER file calls, give its exact signature so that file's owner stays consistent.

If a gap genuinely needs real Godot render plumbing, an art asset, or a live in-game check (not a code edit), put it in unportable[] with a concrete reason. Do NOT fabricate or stub-and-claim-done. Be exhaustive across the unit's gaps but surgical per edit.`

const applyPrompt = (file, edits) => `You are the OPUS file owner + reviewer for ONE file this wave. You exclusively own:
  ${file}
Apply the queued edits (proposed by possibly-cheaper plan models), CORRECTING their mistakes, and leave the file coherent and compiling. You are the review/correction gate -- the proposals are suggestions, not gospel.

QUEUED EDITS (${edits.length}):
${edits.map((e, i) => `--- [${i + 1}] unit=${e.unit} action=${e.action}
ANCHOR: ${e.anchor}
RATIONALE: ${e.rationale}${e.crossFileApi ? `\nCROSS-FILE API: ${e.crossFileApi}` : ''}
CODE:
${e.code}`).join('\n')}

STEPS:
1. Read ${file} fully (absolute: ${REPO}/${file}).
2. Apply each edit at its anchor with Edit/Write. Plan agents may have wrong anchors, missing usings, wrong types/names, or duplicate intent -- FIX all of it. Reconcile overlapping/adjacent edits into clean code; remove duplication.
3. Honor every CROSS-FILE API signature EXACTLY (other files call them).
4. Ensure required \`using\`s exist and the file is internally consistent. Keep the file's existing style.
5. Do NOT run the build (a later gate phase does). Do NOT edit any other file.

Return appliedCount, status, a short 'corrections' note (what you fixed in the proposals), 'concerns' (anything that may not compile or needs cross-file follow-up), and crossFileApis you exposed.`

const gatePrompt = (i) => `You are the OPUS build/test gate for "${WAVE}". Get the solution GREEN. Work from repo root: ${REPO}

1. Build the tests project: dotnet build tests/VortexArena.Tests/VortexArena.Tests.csproj -c Debug --nologo
2. Build the game:          dotnet build VortexArena.csproj -c Debug --nologo
If either FAILS: read the compiler errors, open the offending files, and FIX them. The most likely breakage is signature drift between files from the parallel apply phase (a method added in file A with a different signature than file B calls), missing usings, typos, or type mismatches. Rebuild after each fix. Iterate until BOTH build clean (up to ~15 fix cycles).
3. When the build is clean, run the suite: dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj -c Debug --no-build --nologo
   Baseline expectation: 0 failed (real-data tests need Base/data, which IS present on this machine, so expect the full suite to run). Fix any NEW failures this wave introduced; do NOT chase pre-existing skips/ignores.

This is gate attempt #${i}. If a prior attempt left errors, just re-run the builds to see the current state and continue fixing. Report build_passed, tests_passed, counts, fixes_made, and any remaining_errors you could not resolve.`

const verifyPrompt = (u) => `You are an OPUS parity re-auditor (the parity-diff discipline) for unit ${u.unit}. The wave just edited the port. Decide which gaps ACTUALLY closed and update the registry truthfully -- no inflated confidence.

STEPS:
1. Read planning/parity/specs/${u.unit}.md (Base truth) and planning/parity/registry/${u.unit}.yaml (baseline rows) -- and SCHEMA.md for the status vocabulary.
2. Read the CURRENT port files for this unit to see exactly what landed this wave.
3. Re-score each previously-open dimension: faithful/partial/missing (logic/values/timing/presentation/audio) and live/partial/dead (liveness). RULES: a feature coded with NO live caller is liveness:partial/dead, NOT closed. Visible fidelity not runtime-verified is 'unknown', not 'faithful'. Be skeptical.
4. EDIT planning/parity/registry/${u.unit}.yaml IN PLACE to the new statuses + refresh the row notes (you exclusively own this yaml).
5. Return closed[] (now faithful/live), stillOpen[] (each with a one-line reason), registryUpdated, and a one-line driftRow.`

// ============================================================
log(`${WAVE}: ${UNITS.length} units`)

// ---------- Phase 1: PLAN (parallel, tiered, read-only) ----------
phase('Plan')
const specs = (await parallel(UNITS.map(u => () =>
  agent(planPrompt(u), { label: `plan:${u.unit}`, phase: 'Plan', model: modelFor(u.tier), schema: EDIT_SPEC_SCHEMA })
))).filter(Boolean)

// regroup edits by target file (barrier-justified: apply needs every spec bucketed by file)
const byFile = {}
let totalEdits = 0, totalUnportable = 0
for (const s of specs) {
  for (const e of (s.edits || [])) { (byFile[e.file] ||= []).push({ unit: s.unit, ...e }); totalEdits++ }
  totalUnportable += (s.unportable || []).length
}
const files = Object.keys(byFile)
log(`Planned ${specs.length}/${UNITS.length} units -> ${totalEdits} edits across ${files.length} files (${totalUnportable} flagged unportable)`)

// ---------- Phase 2: APPLY (opus, one owner per file; files disjoint -> parallel) ----------
phase('Apply')
const applied = (await parallel(files.map(f => () =>
  agent(applyPrompt(f, byFile[f]), { label: `apply:${f.split('/').pop()}`, phase: 'Apply', model: 'opus', schema: APPLY_SCHEMA })
))).filter(Boolean)
const applyConcerns = applied.filter(a => a.status !== 'all_applied' || (a.concerns && a.concerns.trim())).map(a => `${a.file}: ${a.status}${a.concerns ? ' -- ' + a.concerns : ''}`)
log(`Applied across ${applied.length} files; ${applyConcerns.length} files reported concerns`)

// ---------- Phase 3: GATE (opus build/test, loop until green) ----------
phase('Gate')
let gate = null
for (let i = 0; i < 5; i++) {
  gate = await agent(gatePrompt(i), { label: `gate#${i}`, phase: 'Gate', model: 'opus', schema: GATE_SCHEMA })
  if (gate && gate.build_passed && gate.tests_passed) break
  log(`gate attempt ${i}: build=${gate && gate.build_passed} tests=${gate && gate.tests_passed}`)
}
const green = !!(gate && gate.build_passed && gate.tests_passed)
log(green ? `GREEN: ${gate.tests_passed_count || '?'} passed / ${gate.tests_failed_count || 0} failed` : `NOT GREEN -- ${gate ? gate.remaining_errors : 'gate died'}`)

// ---------- Phase 4: VERIFY (opus per-unit re-audit; only if green) ----------
let verified = []
if (green) {
  phase('Verify')
  verified = (await parallel(specs.map(s => () =>
    agent(verifyPrompt({ unit: s.unit }), { label: `verify:${s.unit}`, phase: 'Verify', model: 'opus', schema: VERIFY_SCHEMA })
  ))).filter(Boolean)
  const closedCount = verified.reduce((n, v) => n + (v.closed || []).length, 0)
  log(`Re-audited ${verified.length} units -> ${closedCount} gap-dimensions confirmed closed`)
}

return {
  wave: WAVE,
  unitsPlanned: specs.length,
  unitsTotal: UNITS.length,
  totalEdits,
  filesTouched: files,
  unportable: specs.flatMap(s => (s.unportable || []).map(x => ({ unit: s.unit, ...x }))),
  applyConcerns,
  gate: gate ? { build: gate.build_passed, tests: gate.tests_passed, passed: gate.tests_passed_count, failed: gate.tests_failed_count, remaining: gate.remaining_errors } : null,
  green,
  closedCount: verified.reduce((n, v) => n + (v.closed || []).length, 0),
  drift: verified.map(v => v.driftRow).filter(Boolean),
  stillOpen: verified.flatMap(v => (v.stillOpen || []).map(s => ({ unit: v.unit, gap: s }))),
}
