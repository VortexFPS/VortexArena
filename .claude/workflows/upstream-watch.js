export const meta = {
  name: 'upstream-watch',
  description: 'Analyze a harvested upstream worklist (new master commits + branches/MRs from the Xonotic data + DarkPlaces repos) against the upstream-watch rubric, assign UW-#### ids, and append entries to LEDGER.yaml + per-item deep dives. Human sets the final port/reject decision; regenerate LEDGER.html afterwards.',
  whenToUse: 'Weekly, after running `python tools/upstream-watch.py`. args: { worklist?: "planning/upstream-watch/_inbox/worklist-<date>.json" }',
  phases: [
    { title: 'Load', detail: 'read the worklist + current LEDGER.yaml high-water UW id' },
    { title: 'Analyze', detail: 'one analyst per non-chore candidate: apply the rubric from the real diff' },
    { title: 'Write', detail: 'scribe appends YAML entries + writes deep-dive docs' },
  ],
}

const pad = n => `UW-${String(n).padStart(4, '0')}`

// ---- Resolve the three roots (never hardcode them) ---------------------------
// These were `C:/Users/Bryan/Projects/Xonotic/...` literals until the tree moved to Projects/Vortex/,
// which killed all three. The damage is not a clean crash: an analyst whose `git -C <dead-path> show`
// fails still owes this workflow a filled-in ANALYSIS_SCHEMA, so it answers from the commit TITLE
// alone — and the result is a ledger row that looks reviewed but never saw the diff. Resolve and
// verify before any analyst runs.
const ROOTS_SCHEMA = {
  type: 'object', required: ['port', 'data', 'darkplaces', 'ok'], additionalProperties: false,
  properties: {
    port: { type: 'string' }, data: { type: 'string' }, darkplaces: { type: 'string' },
    ok: { type: 'boolean' }, note: { type: 'string' },
  },
}
const roots = await agent(`Resolve three absolute filesystem roots and return them. Do NOT analyze any
commits — this is a path-resolution task only.

port = the Vortex Arena repo root: \`git rev-parse --show-toplevel\` (you start inside it).
Then locate the upstream reference checkouts. Let BASE be the first of these that exists:
  1. $VA_BASE_DIR            (only if VA_BASE_DIR is set in the environment)
  2. <parent of port>/Base/data
(that order mirrors tests/VortexArena.Tests/TestPaths.cs, the existing contract for where the
reference checkout lives). Then:
  data        = BASE/xonotic-data.pk3dir
  darkplaces  = the sibling darkplaces checkout — BASE/../darkplaces, else BASE/darkplaces

Both data and darkplaces must be real GIT CLONES, not just directories, because the analysts diff
them with \`git -C <path> show\` and \`git -C <path> diff origin/master...\`. Verify each with
\`git -C <path> rev-parse --git-dir\` AND confirm an origin/master ref exists
(\`git -C <path> rev-parse --verify origin/master\`). Set ok=true only if all three resolve and both
git checks pass on both repos; otherwise ok=false and say in note which check failed where.`,
  { label: 'resolve-roots', phase: 'Load', agentType: 'Explore', effort: 'low', schema: ROOTS_SCHEMA })

if (!roots || !roots.ok) {
  throw new Error(`upstream-watch: cannot resolve the upstream checkouts, so the analysts would be `
    + `triaging commits they cannot read — producing ledger rows written from commit titles alone. `
    + `Resolver said: ${roots ? roots.note || '(no note)' : 'the resolver agent returned nothing'}. `
    + `Fix: clone xonotic-data.pk3dir and darkplaces under <parent>/Base/, or set VA_BASE_DIR.`)
}
const PORT = roots.port
const WATCH = `${PORT}/planning/upstream-watch`
const DATA = roots.data
const DP = roots.darkplaces

const worklist = (args && args.worklist)
  ? (args.worklist.startsWith('/') || /^[A-Za-z]:/.test(args.worklist) ? args.worklist : `${PORT}/${args.worklist}`)
  : null

log(`upstream-watch analysis: worklist=${worklist || '(latest in _inbox)'} · data=${DATA} dp=${DP}`)

// ---- Load: worklist candidates + ledger high-water mark ---------------------
const LOAD_SCHEMA = {
  type: 'object', required: ['next_uw', 'harvest_date', 'candidates'], additionalProperties: false,
  properties: {
    next_uw: { type: 'integer' },        // highest UW-#### in LEDGER.yaml + 1 (or 1 if none)
    harvest_date: { type: 'string' },    // the worklist JSON's "generated" field (YYYY-MM-DD)
    candidates: { type: 'array', items: {
      type: 'object', required: ['source', 'repo', 'type', 'title'], additionalProperties: false,
      properties: {
        source: { type: 'string' },                       // dedup key, e.g. data@a1b2c3 or dp:branch@tip
        repo: { type: 'string', enum: ['data', 'dp'] },
        type: { type: 'string', enum: ['commit', 'branch'] },
        ref: { type: 'string' },                          // sha, or branch name to diff over master
        title: { type: 'string' },
        noise: { type: 'boolean' },                       // pre-flagged ci/chore
        open_mr: { type: 'boolean' },
        url: { type: 'string' },                          // GitLab commit-or-MR URL (see load prompt)
      } } },
  },
}
const loadPrompt = `Read ${worklist ? `the worklist JSON at ${worklist}` :
  `the most recent planning/upstream-watch/_inbox/worklist-*.json file`} and read
planning/upstream-watch/LEDGER.yaml. Return: next_uw = (highest UW-#### integer among the entries'
`uw:` fields) + 1, or 1 if the file has no entries yet. harvest_date = the worklist JSON's top-level
"generated" field. And candidates = one entry per master commit
(type "commit", ref=sha from the source key after '@') and per branch/MR (type "branch", ref=branch
name), carrying source, repo, title (commit subject or MR title), noise flag, and open_mr. Also set
url per candidate — the GitLab link to the original change: for a commit use
"https://gitlab.com/xonotic/<proj>/-/commit/<full sha from the worklist>"; for a branch use its
worklist "mr_url" if present, else "https://gitlab.com/xonotic/<proj>/-/tree/<branch>". <proj> is
"xonotic-data.pk3dir" for repo=data and "darkplaces" for repo=dp. Do not analyze anything yet —
just enumerate faithfully and completely.`

const loaded = await agent(loadPrompt, { label: 'load', phase: 'Load', agentType: 'Explore', schema: LOAD_SCHEMA })
let candidates = (loaded && loaded.candidates) || []
const baseUw = (loaded && loaded.next_uw) || 1
if (!candidates.length) { log('No candidates in worklist — nothing to analyze.'); return { analyzed: 0 } }

// Deterministic, stable id assignment: worklist order. No cross-agent race on the counter.
candidates = candidates.map((c, i) => ({ ...c, uw: pad(baseUw + i) }))
const noise = candidates.filter(c => c.noise && c.type === 'commit' && !c.open_mr)
const real = candidates.filter(c => !(c.noise && c.type === 'commit' && !c.open_mr))
log(`${candidates.length} candidate(s): ${real.length} to analyze, ${noise.length} noise→n/a mechanically. Ids ${pad(baseUw)}..${pad(baseUw + candidates.length - 1)}.`)

// ---- Analyze: one analyst per real candidate (rubric from the actual diff) ---
const ANALYSIS_SCHEMA = {
  type: 'object',
  required: ['uw', 'source', 'slug', 'kind', 'summary', 'portability', 'completeness',
             'quality', 'alignment', 'effort', 'worth', 'proposed_decision', 'recommendation', 'needs_deepdive'],
  additionalProperties: false,
  properties: {
    uw: { type: 'string' }, source: { type: 'string' }, slug: { type: 'string' },
    kind: { type: 'string', enum: ['qc-gameplay', 'data-cfg', 'asset', 'dp-engine', 'build/i18n/ci'] },
    base_symbols: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' }, portability: { type: 'string' }, completeness: { type: 'string' },
    quality: { type: 'string' }, alignment: { type: 'string' },
    effort: { type: 'string', enum: ['S', 'M', 'L'] },
    worth: { type: 'string', enum: ['high', 'medium', 'low', 'none'] },
    proposed_decision: { type: 'string', enum: ['pending', 'port', 'adapt', 'defer', 'reject', 'n/a'] },
    recommendation: { type: 'string' }, needs_deepdive: { type: 'boolean' },
  },
}
function analyzePrompt(c) {
  const repoPath = c.repo === 'dp' ? DP : DATA
  const how = c.type === 'commit'
    ? `Read the diff: \`git -C ${repoPath} show --stat ${c.ref}\` then \`git -C ${repoPath} show ${c.ref}\`.`
    : `Read what the branch adds over master: \`git -C ${repoPath} log --oneline --stat origin/master..origin/${c.ref}\` then \`git -C ${repoPath} diff origin/master...origin/${c.ref}\`.`
  return `Triage upstream Xonotic contribution ${c.uw} for the Vortex Arena port (a C#/Godot
reimplementation of Xonotic). Source ${c.source} (repo=${c.repo}, ${c.type}${c.open_mr ? ', OPEN MR' : ''}).
Title: "${c.title}".

${how} Read the ACTUAL diff, not just the title. Apply the rubric in
planning/upstream-watch/README.md §5 (read it if unsure). Return the structured analysis with
uw="${c.uw}", source="${c.source}", and:
- summary: what it does / how it works, from the diff. Name Base files/symbols touched (base_symbols).
- kind: qc-gameplay (qcsrc/**) · data-cfg (cfg/effectinfo/notifications) · asset · dp-engine
  (DarkPlaces C — usually n/a since Godot replaces DP, EXCEPT protocol/physics/collision/asset-format
  behavior, or a security/parser fix, which we DO mirror) · build/i18n/ci (usually n/a).
- portability, completeness (merged vs WIP/draft; tests?), quality, alignment (serves Vortex Arena or
  upstream-only churn / conflicts with an existing intended_divergence — grep planning/ if unsure).
- effort S/M/L; worth high/medium/low/none; proposed_decision (default "pending" for real gameplay
  impact — Bryan decides; "n/a" for translations/CI/engine-internal; "reject" only with a clear reason;
  "defer" for stale drafts worth revisiting).
- needs_deepdive: true only if non-trivial (real gameplay/feature/subsystem) — those get a full doc;
  small fixes/noise get a one-line ledger row (false). slug: short kebab-case name.
Be concise; these become ledger cells + a short doc.`
}

const analyses = (await parallel(real.map(c => () =>
  agent(analyzePrompt(c), { label: `${c.uw}:${c.repo}`, phase: 'Analyze', agentType: 'Explore', schema: ANALYSIS_SCHEMA })
    .then(a => a ? { ...a, uw: c.uw, source: a.source || c.source, url: c.url, _title: c.title } : null)
))).filter(Boolean)

// Mechanical n/a rows for pre-flagged chores (CI/version) — no agent spent.
const noiseRows = noise.map(c => ({
  uw: c.uw, source: c.source, url: c.url, kind: 'build/i18n/ci', summary: c.title.slice(0, 140),
  worth: 'none', proposed_decision: 'n/a', effort: 'S', needs_deepdive: false, recommendation: 'chore (ci/version)',
}))
const all = [...analyses, ...noiseRows].sort((a, b) => a.uw.localeCompare(b.uw))
log(`Analyzed ${analyses.length}; +${noiseRows.length} mechanical chore rows. Building ${all.length} ledger rows.`)

// ---- Build ledger entries (YAML) + deep-dive docs deterministically in JS ----
// LEDGER.yaml is the source of truth; LEDGER.html is regenerated from it afterwards.
// Emit each entry as a YAML list item using JSON-encoded scalars (valid YAML, safely quoted).
const norm = s => String(s || '').replace(/\s+/g, ' ').trim()
const sentences = t => norm(t).split(/(?<=[.!?])\s+(?=[A-Z0-9("'])/).map(s => s.trim()).filter(Boolean)
const headlineOf = s => { const h = sentences(s)[0] || norm(s); return h.length > 220 ? h.slice(0, 220).replace(/\s+\S*$/, '') + '…' : h }
const J = v => JSON.stringify(v === undefined ? null : v)
const entryYaml = a => [
  `- uw: ${J(a.uw)}`,
  `  title: ${J(norm(a._title))}`,
  `  headline: ${J(headlineOf(a.summary))}`,
  `  summary: ${J(norm(a.summary))}`,
  `  recommendation: ${J(norm(a.recommendation))}`,
  `  relevance: ${J(a.worth)}`,
  `  decision: ${J(a.proposed_decision)}`,
  `  kind: ${J(a.kind)}`,
  `  effort: ${J(a.effort)}`,
  `  repo: ${J(a.source.split(':')[0].split('@')[0])}`,
  `  source: ${J(a.source)}`,
  `  url: ${J(a.url || null)}`,
  `  base_symbols: ${J(a.base_symbols || [])}`,
  `  deep_dive: ${J(a.needs_deepdive ? `items/${a.uw}-${a.slug}.md` : null)}`,
].join('\n')
const ledgerBlock = all.map(entryYaml).join('\n')

const deepDives = analyses.filter(a => a.needs_deepdive).map(a => ({
  path: `${WATCH}/items/${a.uw}-${a.slug}.md`,
  content: `# ${a.uw} — ${norm(a._title)}

- **Source:** \`${a.source}\`
- **Kind:** ${a.kind}
- **Base symbols touched:** ${(a.base_symbols || []).map(s => '`' + s + '`').join(', ') || '—'}
- **Port-worthiness:** ${a.worth}  ·  **Effort:** ${a.effort}
- **Decision:** ${a.proposed_decision}

## What it does / how it works
${a.summary}

## Portability
${a.portability}

## Completeness (upstream)
${a.completeness}

## Quality
${a.quality}

## Roadmap / design alignment
${a.alignment}

## Recommendation
${a.recommendation}
`,
}))

// ---- Write: one scribe appends YAML entries + writes deep-dive docs ----------
const WRITE_SCHEMA = {
  type: 'object', required: ['entries_appended', 'docs_written'], additionalProperties: false,
  properties: { entries_appended: { type: 'integer' }, docs_written: { type: 'integer' } },
}
const scribePrompt = `Do two mechanical writes — do NOT rewrite or re-reason the content, insert it verbatim.

1) APPEND the following ${all.length} YAML list item(s) to the end of ${WATCH}/LEDGER.yaml (that file
is a single top-level YAML list; add these as new items at the very end, preserving exact
indentation). Do not modify or reorder existing entries. The block to append:

${ledgerBlock}

2) Write these ${deepDives.length} deep-dive file(s) verbatim (create each path with the given content):

${deepDives.map(d => `--- FILE: ${d.path} ---\n${d.content}`).join('\n\n')}

Return entries_appended and docs_written counts.`

const written = await agent(scribePrompt, { label: 'scribe', phase: 'Write', schema: WRITE_SCHEMA })
log(`Done: ${written ? written.entries_appended : 0} ledger entr(ies), ${written ? written.docs_written : 0} deep-dive doc(s). Now regenerate the HTML: \`python tools/upstream-ledger-html.py\`. All decisions pending/n-a until Bryan rules.`)
return { analyzed: analyses.length, noise: noiseRows.length, ...written, id_range: `${pad(baseUw)}..${pad(baseUw + candidates.length - 1)}` }
