export const meta = {
  name: 'parity-diff',
  description: 'Reusable Base<->port parity diff: re-audit registry units against current Base+port code, detect drift (regressions, fixes, new gaps, newly-unmapped Base features), write a DRIFT report. Optionally update the registry in place.',
  whenToUse: 'After porting work, before a release, or to re-check parity. args: { scope?: "all" | <category> | <unit-id>[], mode?: "diff" | "update", dateLabel?: "YYYY-MM-DD" }',
  phases: [
    { title: 'Discover', detail: 'enumerate in-scope registry units' },
    { title: 'Diff', detail: 'per unit: compare baseline registry row vs current Base+port code' },
    { title: 'Report', detail: 'aggregate drift into a report (regressions first)' },
  ],
}

// ---- Resolve the two roots (never hardcode them) ----------------------------
// This file used to carry `C:/Users/Bryan/Projects/Xonotic/...` literals. The tree moved to
// Projects/Vortex/ and every one of them died — silently, and in the worst possible way: a diff
// agent that cannot read the Base spec still returns changed=false, so the report reads
// "No drift detected" and a run over a nonexistent baseline looks like a clean bill of health.
// So resolve up front, verify, and abort before spending a single diff agent.
const ROOTS_SCHEMA = {
  type: 'object', required: ['port', 'base', 'ok'], additionalProperties: false,
  properties: {
    port: { type: 'string' }, base: { type: 'string' },
    ok: { type: 'boolean' }, note: { type: 'string' },
  },
}
const roots = await agent(`Resolve two absolute filesystem roots and return them. Do NOT read or
analyze any game code — this is a path-resolution task only.

port = the Vortex Arena repo root: \`git rev-parse --show-toplevel\` (you start inside it).
base = the upstream Xonotic reference checkout's QuakeC spec tree. Take the FIRST that exists:
  1. $VA_BASE_DIR/xonotic-data.pk3dir/qcsrc   (only if VA_BASE_DIR is set in the environment)
  2. <parent of port>/Base/data/xonotic-data.pk3dir/qcsrc
That order mirrors tests/VortexArena.Tests/TestPaths.cs, which is the existing contract for where
the reference checkout lives; keep the two in agreement.

Confirm each path with a real directory listing. Set ok=true ONLY if both directories exist AND base
actually contains .qc files. Otherwise ok=false, and say in note exactly which path failed and what
you saw there.`, { label: 'resolve-roots', phase: 'Discover', agentType: 'Explore', effort: 'low', schema: ROOTS_SCHEMA })

if (!roots || !roots.ok) {
  throw new Error(`parity-diff: cannot resolve the Base/port roots, so a drift report would be `
    + `meaningless (every unit would come back "no drift" because nothing was readable). `
    + `Resolver said: ${roots ? roots.note || '(no note)' : 'the resolver agent returned nothing'}. `
    + `Fix: clone the upstream reference beside this repo as <parent>/Base/data, or point VA_BASE_DIR at it.`)
}
const BASE = roots.base
const PORT = roots.port
const REGDIR = `${PORT}/planning/parity/registry`
log(`roots: port=${PORT} base=${BASE}`)

const scope = (args && args.scope) || 'all'
const mode = (args && args.mode) || 'diff'        // 'diff' = report only; 'update' = rewrite registry
const label = (args && args.dateLabel) || 'latest'
const scopeDesc = Array.isArray(scope) ? `units [${scope.join(', ')}]` : `scope "${scope}"`

log(`parity-diff: ${scopeDesc}, mode=${mode}, report=DRIFT-${label}.md`)

// ---- Discover in-scope units -----------------------------------------------
const DISCOVER_SCHEMA = {
  type: 'object', required: ['units'], additionalProperties: false,
  properties: { units: { type: 'array', items: {
    type: 'object', required: ['id', 'category'], additionalProperties: false,
    properties: { id: { type: 'string' }, category: { type: 'string' } } } } },
}
const discoverPrompt = `List the parity registry shards to diff. Glob ${REGDIR}/*.yaml. For each file read
only its top-of-file 'unit:' and 'category:' lines (do not read the whole file). ${
  scope === 'all' ? 'Return ALL of them.'
  : Array.isArray(scope) ? `Return only those whose unit id is one of: ${scope.join(', ')}.`
  : `Return only those whose category equals "${scope}" OR whose unit id equals "${scope}".`
} Return the structured list.`

const discovered = await agent(discoverPrompt, { label: 'discover', phase: 'Discover', agentType: 'Explore', schema: DISCOVER_SCHEMA })
const UNITS = (discovered && discovered.units) || []
if (!UNITS.length) { log('No in-scope units found.'); return { units: 0, note: 'nothing in scope' } }
log(`Diffing ${UNITS.length} units.`)

// ---- Per-unit diff ----------------------------------------------------------
const DIFF_SCHEMA = {
  type: 'object', required: ['unit', 'changed', 'changes', 'new_unmapped', 'wrote_files'], additionalProperties: false,
  properties: {
    unit: { type: 'string' },
    changed: { type: 'boolean' },
    rewrote_registry: { type: 'boolean' },
    wrote_files: { type: 'boolean' },
    changes: { type: 'array', items: {
      type: 'object', required: ['feature', 'dimension', 'from', 'to', 'kind', 'reason'], additionalProperties: false,
      properties: {
        feature: { type: 'string' }, dimension: { type: 'string' },
        from: { type: 'string' }, to: { type: 'string' },
        kind: { type: 'string', enum: ['regression', 'fix', 'new-gap', 'reclassified'] },
        reason: { type: 'string' },
      } } },
    new_unmapped: { type: 'array', items: { type: 'string' } },  // Base features not present in the registry at all
  },
}
const diffPrompt = (u) => `You are running a PARITY DRIFT CHECK for unit "${u.id}" (category ${u.category}).

BASELINE: ${REGDIR}/${u.id}.yaml  — the last-recorded parity truth. Read it in full.
CONTRACT: ${PORT}/planning/parity/SCHEMA.md — read it. Statuses/vocab must stay schema-valid.
Base spec root: ${BASE}   Port root: ${PORT}   (use ABSOLUTE paths)

For EACH feature row in the baseline:
 1. Re-locate the Base symbol (base_refs) and the port symbol (port_refs) in the CURRENT code.
 2. Independently re-derive the true per-dimension status (logic/values/timing/presentation/audio)
    + liveness, exactly as a fresh adversarial audit would. Check live callers; diff constants vs Base.
 3. Compare to the baseline status. Emit a change record for every dimension that differs:
      kind=regression  baseline better than reality (e.g. faithful->partial, live->dead)
      kind=fix         reality better than baseline (e.g. missing->faithful, dead->live)
      kind=new-gap     a defect now present that the baseline did not record
      kind=reclassified neutral change (e.g. unknown->faithful after verification)
Also RE-SCAN the Base source for features that exist in Base but have NO row in the baseline at all
-> list them in new_unmapped (these are coverage gaps in the registry itself).

${mode === 'update'
  ? `MODE=update: REWRITE ${REGDIR}/${u.id}.yaml with the current truth (corrected statuses, gaps,
     constants, confidence) per SCHEMA.md, bump last_audited to "${label}" if it is a date, and ADD
     rows for any new_unmapped features. Set wrote_files/rewrote_registry true.
     CRITICAL: PRESERVE the existing 'base_ref_rev:' field VERBATIM — it is a PINNED Base revision, NOT the
     local Base git HEAD. Do not read it from git; copy the string already in the baseline yaml unchanged.`
  : `MODE=diff: DO NOT modify any file. Report changes only. Set wrote_files/rewrote_registry false.`}

Return the structured diff. If nothing differs and nothing is unmapped, set changed=false with empty arrays.`

const deltas = (await pipeline(UNITS,
  (u) => agent(diffPrompt(u), { label: `diff:${u.id}`, phase: 'Diff', agentType: 'general-purpose', schema: DIFF_SCHEMA, effort: 'high' }),
)).filter(Boolean)

const drifted = deltas.filter(d => d.changed || (d.new_unmapped && d.new_unmapped.length))
log(`Diff complete: ${drifted.length}/${deltas.length} units drifted.`)

// ---- Report -----------------------------------------------------------------
const REPORT_SCHEMA = {
  type: 'object', required: ['regressions', 'fixes', 'new_gaps', 'unmapped', 'wrote_report'], additionalProperties: false,
  properties: {
    regressions: { type: 'integer' }, fixes: { type: 'integer' }, new_gaps: { type: 'integer' },
    unmapped: { type: 'integer' }, wrote_report: { type: 'boolean' }, headline: { type: 'string' },
  },
}
const reportPrompt = `Write the parity drift report from these per-unit deltas (JSON):
${JSON.stringify(drifted)}

Write ${PORT}/planning/parity/DRIFT-${label}.md (absolute path, overwrite) with sections in THIS order:
 1. "## Regressions" — every kind=regression change, table: unit | feature | dimension | was -> now | reason.
    These are the alarms: parity that got WORSE. Sort core gameplay categories (gametype/weapon/damage/
    physics/item/scoring) first.
 2. "## New gaps" — kind=new-gap changes.
 3. "## Registry coverage gaps" — all new_unmapped Base features (registry is missing rows for them).
 4. "## Fixes" — kind=fix changes (parity that improved since the baseline).
 5. "## Reclassified" — kind=reclassified (neutral).
Lead with a one-line headline (counts). If everything is empty, say "No drift detected." Then run
${mode === 'update' ? '' : 'NOT '}forget: mode was "${mode}".
Return the structured summary.`

const summary = await agent(reportPrompt, { label: 'report', phase: 'Report', agentType: 'general-purpose', schema: REPORT_SCHEMA })
return { units: UNITS.length, drifted: drifted.length, mode, report: `planning/parity/DRIFT-${label}.md`, summary }
