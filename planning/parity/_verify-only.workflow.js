export const meta = {
  name: 'parity-verify-only',
  description: 'Consolidated opus re-audit of the Wave 16/17 units: re-score each registry unit vs current port and update its yaml',
  phases: [{ title: 'Verify', detail: 'opus per-unit parity re-audit; update registry yaml' }],
}

const REPO = (args && args.repo) || 'C:/Users/Bryan/Projects/Xonotic/VortexArena'
const UNITS = (args && args.units) || []

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

const verifyPrompt = (u) => `You are an OPUS parity re-auditor (the parity-diff discipline) for unit ${u}. Waves 16-17 just edited the port. Decide which gaps ACTUALLY closed and update the registry truthfully -- no inflated confidence.

STEPS:
1. Read planning/parity/specs/${u}.md (Base truth) and planning/parity/registry/${u}.yaml (baseline rows) -- and SCHEMA.md for the status vocabulary.
2. Read the CURRENT port files for this unit (the spec's "Port refs") to see exactly what landed.
3. Re-score each previously-open dimension: faithful/partial/missing (logic/values/timing/presentation/audio) and live/partial/dead (liveness). RULES: a feature coded with NO live caller is liveness:partial/dead, NOT closed. Visible fidelity not runtime-verified is 'unknown', not 'faithful'. Be skeptical; do not assume an edit worked without reading it.
4. EDIT planning/parity/registry/${u}.yaml IN PLACE to the new statuses + refresh the row notes (you exclusively own this yaml; keep the SCHEMA.md row shape valid).
5. Return closed[] (now faithful/live), stillOpen[] (each with a one-line reason), registryUpdated, and a one-line driftRow.`

log(`Consolidated verify: ${UNITS.length} units`)
phase('Verify')

// Process in chunks to ease sustained request pressure (avoids the tail rate-limit seen in the porting waves).
const CHUNK = 12
const results = []
for (let i = 0; i < UNITS.length; i += CHUNK) {
  const slice = UNITS.slice(i, i + CHUNK)
  const r = (await parallel(slice.map(u => () =>
    agent(verifyPrompt(u), { label: `verify:${u}`, phase: 'Verify', model: 'opus', schema: VERIFY_SCHEMA })
  ))).filter(Boolean)
  results.push(...r)
  log(`verified ${results.length}/${UNITS.length} (last chunk ${r.length}/${slice.length} ok)`)
}

const okUnits = new Set(results.map(r => r.unit))
const failed = UNITS.filter(u => !okUnits.has(u))
return {
  verified: results.length,
  total: UNITS.length,
  closedCount: results.reduce((n, r) => n + (r.closed || []).length, 0),
  failed,
  drift: results.map(r => r.driftRow).filter(Boolean),
  stillOpen: results.flatMap(r => (r.stillOpen || []).map(s => ({ unit: r.unit, gap: s }))),
}
