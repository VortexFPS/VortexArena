export const meta = {
  name: 'parity-framework-build',
  description: 'Build greenfield parity subsystems: opus design per track -> per-file build owners -> live integrate -> build/test gate -> registry re-verify',
  phases: [
    { title: 'Design',    detail: 'one opus architect per track: wire format + public APIs + per-file work breakdown' },
    { title: 'Build',     detail: 'one opus owner per file implements its slice of every track design' },
    { title: 'Integrate', detail: 'one opus per track wires live call sites + reconciles cross-track APIs' },
    { title: 'Gate',      detail: 'opus: dotnet build + test, fix until green' },
    { title: 'Verify',    detail: 'opus per-unit parity re-audit; update registry' },
  ],
}

// ---------- args ----------
// args: { repo, waveName, tracks: [{ id, title, goal, units:[...] }] }
const REPO = (args && args.repo) || 'C:/Users/Bryan/Projects/Vortex/VortexArena'
const WAVE = (args && args.waveName) || 'framework build'
const TRACKS = (args && args.tracks) || []

// ---------- schemas ----------
const DESIGN_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['track', 'summary', 'wireFormat', 'publicApis', 'files', 'liveCallSites'],
  properties: {
    track: { type: 'string' },
    summary: { type: 'string', description: 'one-paragraph architecture of the subsystem' },
    wireFormat: { type: 'string', description: 'exact net wire layout (fields, types, order) if this track networks anything; "none" otherwise' },
    publicApis: { type: 'array', items: { type: 'string' }, description: 'exact C# signatures of every new public member other code will call' },
    files: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['path', 'kind', 'purpose', 'work'],
        properties: {
          path: { type: 'string', description: 'repo-relative path; may be a NEW file' },
          kind: { type: 'string', enum: ['new', 'modify'] },
          purpose: { type: 'string' },
          work: { type: 'string', description: 'concrete, implementable description of exactly what to add/change in this file (types, methods, fields, call wiring) with Base constants/timings' },
        },
      },
    },
    liveCallSites: { type: 'array', items: { type: 'string' }, description: 'each: file + the exact existing method where this subsystem must be invoked to be LIVE (close the liveness dimension)' },
  },
}

const BUILD_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['file', 'status'],
  properties: {
    file: { type: 'string' },
    status: { type: 'string', enum: ['done', 'partial', 'failed'] },
    implemented: { type: 'string', description: 'what landed in this file' },
    crossFileApis: { type: 'array', items: { type: 'string' }, description: 'public signatures this file now exposes (exact)' },
    concerns: { type: 'string' },
  },
}

const INTEGRATE_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['track', 'wiredLive', 'status'],
  properties: {
    track: { type: 'string' },
    status: { type: 'string', enum: ['live', 'partial', 'failed'] },
    wiredLive: { type: 'array', items: { type: 'string' }, description: 'call sites actually wired' },
    concerns: { type: 'string' },
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
const designPrompt = (t) => `You are an OPUS systems ARCHITECT for the Xonotic -> Godot/C# port. Design (do NOT build yet) a faithful, LIVE realisation of this subsystem. READ-ONLY this phase.

TRACK: ${t.id} -- ${t.title}
GOAL: ${t.goal}
RELATED PARITY UNITS: ${(t.units || []).join(', ')}

STEPS:
1. For each related unit, read planning/parity/specs/<unit>.md (authoritative Base algorithm + exact constants/cvars/timings) and planning/parity/registry/<unit>.yaml (the open gap dimensions + their notes).
2. Read the CURRENT port code (the spec's "Port refs", and the hub files this will touch: ServerNet.cs, ClientNet.cs, NetGame.cs, GameWorld.cs, ClientWorld.cs, the render/camera files). Understand the EXISTING patterns (how an existing networked entity/stat round-trips; how an existing renderer draws) and REUSE them.
3. Grep the Base QuakeC under Base/data/xonotic-data.pk3dir for the exact behavior/wire format/constants when the spec is ambiguous.

Produce a BUILD PLAN:
- summary: the architecture in one paragraph (how state is produced server-side, networked, consumed/rendered client-side -- following the port's existing seams, e.g. the CTF-flag entity-feed pattern).
- wireFormat: the EXACT net layout if you network anything (field names, types, byte/ushort widths, order), modeled on the port's existing wire structs. "none" if no networking.
- publicApis: exact C# signatures of every NEW public member other code calls.
- files[]: every file to create or modify, each with a CONCRETE 'work' description an opus builder can implement directly (types/methods/fields/call wiring + Base constants). Prefer one clear owner per concept. Keep NEW files small and focused.
- liveCallSites[]: for each, the file + exact existing method where the subsystem must be invoked so it is actually reachable in a normal match (closing the liveness dimension). A coded-but-uncalled subsystem is a FAILURE.

Be faithful to Base behavior and constants. Be exhaustive but minimal -- no speculative generality.`

const buildPrompt = (file, items) => `You are the OPUS BUILD OWNER for ONE file. You exclusively own and will create/modify:
  ${file}   (absolute: ${REPO}/${file})

Implement the work items below (slices of one or more subsystem designs that target this file). You are the sole author of this file this phase -- make it coherent, idiomatic, and COMPILING.

WORK ITEMS (${items.length}):
${items.map((w, i) => `--- [${i + 1}] track=${w.track} kind=${w.kind}
PURPOSE: ${w.purpose}
WORK: ${w.work}`).join('\n\n')}

TRACK CONTEXT (wire formats + public APIs you must match EXACTLY, from the designs):
${items.map(w => `[${w.track}] wire: ${w.wireFormat || 'n/a'}\n  apis: ${(w.publicApis || []).join(' ; ')}`).join('\n')}

STEPS:
1. Read the file fully (if it exists) and the neighbors it depends on, to match style/namespace/usings and reuse existing helpers.
2. Implement every work item. Honor every cross-track wire format + public API signature EXACTLY (other files call/parse them).
3. If a referenced helper/type doesn't exist yet, code against the SIGNATURE given in the design (its owner builds it); do not stub it away.
4. Keep the file internally consistent with correct usings. Do NOT run the build (a later gate does). Do NOT edit any other file.

Return file, status, what you implemented, the exact crossFileApis you now expose, and concerns.`

const integratePrompt = (t) => `You are the OPUS INTEGRATOR for track ${t.id} (${t.title}). The build owners just created/modified the files for this subsystem. Your job: make it LIVE and cross-consistent.

GOAL: ${t.goal}
UNITS: ${(t.units || []).join(', ')}

STEPS:
1. Re-read the design's liveCallSites for this track and the files that were built.
2. Ensure the subsystem is actually INVOKED on the live gameplay path: server produces the state every tick/event where Base does; the client consumes/renders it. Add any missing call-site wiring (you may edit the call-site files, but coordinate -- if a hub file was a build owner's file, prefer adding the call rather than rewriting its members).
3. Reconcile any cross-file/cross-track signature drift you find (a producer and consumer that disagree on a field/signature).
4. Do NOT run the build. Keep edits minimal and faithful.

Return track, status (live/partial/failed), the call sites you wired, and concerns.`

const gatePrompt = (i) => `You are the OPUS build/test gate for "${WAVE}". Get the solution GREEN. Repo root: ${REPO}

1. dotnet build tests/VortexArena.Tests/VortexArena.Tests.csproj -c Debug --nologo
2. dotnet build VortexArena.csproj -c Debug --nologo
If either FAILS: read the compiler errors, open the offending files, FIX them (most likely: signature/wire drift between the parallel build owners, missing usings, type mismatches, a referenced-but-unbuilt member). Rebuild after each fix; iterate up to ~15 cycles until BOTH build clean.
3. When clean: dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj -c Debug --no-build --nologo
   Expect 0 failed (Base/data is present). Fix NEW failures this wave caused; do not chase pre-existing skips.

Gate attempt #${i}. Report build_passed, tests_passed, counts, fixes_made, remaining_errors.`

const verifyPrompt = (unit) => `You are an OPUS parity re-auditor (parity-diff discipline) for unit ${unit}. This wave built/extended a subsystem touching it. Decide which gaps ACTUALLY closed; update the registry truthfully -- no inflated confidence.

STEPS:
1. Read planning/parity/specs/${unit}.md (Base truth), planning/parity/registry/${unit}.yaml (baseline rows), and SCHEMA.md (status vocab).
2. Read the CURRENT port files for this unit to see exactly what landed.
3. Re-score each previously-open dimension. RULES: coded with NO live caller => liveness:partial/dead, NOT closed. Visible fidelity not runtime-verified => 'unknown', not 'faithful'. Be skeptical.
4. EDIT planning/parity/registry/${unit}.yaml IN PLACE to the new statuses + refresh notes (you exclusively own this yaml).
5. Return closed[], stillOpen[] (each with a one-line reason), registryUpdated, and a one-line driftRow.`

// ============================================================
log(`${WAVE}: ${TRACKS.length} framework tracks`)

// ---------- Phase 1: DESIGN (parallel, opus, read-only) ----------
phase('Design')
const designs = (await parallel(TRACKS.map(t => () =>
  agent(designPrompt(t), { label: `design:${t.id}`, phase: 'Design', model: 'opus', schema: DESIGN_SCHEMA })
))).filter(Boolean)
log(`Designed ${designs.length}/${TRACKS.length} tracks`)

// regroup file work by target path (barrier-justified: build needs every file's work bucketed)
const trackById = {}
for (const t of TRACKS) trackById[t.id] = t
const byFile = {}
for (const d of designs) {
  const t = trackById[d.track] || {}
  for (const f of (d.files || [])) {
    (byFile[f.path] ||= []).push({
      track: d.track, kind: f.kind, purpose: f.purpose, work: f.work,
      wireFormat: d.wireFormat, publicApis: d.publicApis,
    })
  }
}
const files = Object.keys(byFile)
log(`Build plan: ${files.length} files across ${designs.length} tracks`)

// ---------- Phase 2: BUILD (opus, one owner per file; disjoint -> parallel) ----------
phase('Build')
const built = (await parallel(files.map(f => () =>
  agent(buildPrompt(f, byFile[f]), { label: `build:${f.split('/').pop()}`, phase: 'Build', model: 'opus', schema: BUILD_SCHEMA })
))).filter(Boolean)
const buildConcerns = built.filter(b => b.status !== 'done' || (b.concerns && b.concerns.trim())).map(b => `${b.file}: ${b.status}${b.concerns ? ' -- ' + b.concerns : ''}`)
log(`Built ${built.length} files; ${buildConcerns.length} reported concerns`)

// ---------- Phase 3: INTEGRATE (opus per track, live wiring) ----------
phase('Integrate')
const integrated = (await parallel(TRACKS.map(t => () =>
  agent(integratePrompt(t), { label: `integrate:${t.id}`, phase: 'Integrate', model: 'opus', schema: INTEGRATE_SCHEMA })
))).filter(Boolean)
log(`Integrated ${integrated.length} tracks; live=${integrated.filter(x => x.status === 'live').length}`)

// ---------- Phase 4: GATE (opus build/test loop) ----------
phase('Gate')
let gate = null
for (let i = 0; i < 6; i++) {
  gate = await agent(gatePrompt(i), { label: `gate#${i}`, phase: 'Gate', model: 'opus', schema: GATE_SCHEMA })
  if (gate && gate.build_passed && gate.tests_passed) break
  log(`gate attempt ${i}: build=${gate && gate.build_passed} tests=${gate && gate.tests_passed}`)
}
const green = !!(gate && gate.build_passed && gate.tests_passed)
log(green ? `GREEN: ${gate.tests_passed_count || '?'} passed / ${gate.tests_failed_count || 0} failed` : `NOT GREEN -- ${gate ? gate.remaining_errors : 'gate died'}`)

// ---------- Phase 5: VERIFY (opus per-unit; only if green) ----------
let verified = []
if (green) {
  phase('Verify')
  const allUnits = Array.from(new Set(TRACKS.flatMap(t => t.units || [])))
  verified = (await parallel(allUnits.map(u => () =>
    agent(verifyPrompt(u), { label: `verify:${u}`, phase: 'Verify', model: 'opus', schema: VERIFY_SCHEMA })
  ))).filter(Boolean)
  const closedCount = verified.reduce((n, v) => n + (v.closed || []).length, 0)
  log(`Re-audited ${verified.length} units -> ${closedCount} gap-dimensions confirmed closed`)
}

return {
  wave: WAVE,
  tracksDesigned: designs.length,
  filesBuilt: built.length,
  buildConcerns,
  integrated: integrated.map(x => ({ track: x.track, status: x.status, concerns: x.concerns })),
  gate: gate ? { build: gate.build_passed, tests: gate.tests_passed, passed: gate.tests_passed_count, failed: gate.tests_failed_count, remaining: gate.remaining_errors } : null,
  green,
  closedCount: verified.reduce((n, v) => n + (v.closed || []).length, 0),
  drift: verified.map(v => v.driftRow).filter(Boolean),
  stillOpen: verified.flatMap(v => (v.stillOpen || []).map(s => ({ unit: v.unit, gap: s }))),
}
