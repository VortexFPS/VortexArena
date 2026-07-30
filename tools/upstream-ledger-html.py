#!/usr/bin/env python3
"""Render planning/upstream-watch/LEDGER.yaml -> LEDGER.html.

LEDGER.yaml is the source of truth (one entry per triaged upstream contribution);
this produces a self-contained, filterable HTML view of it (no external assets, so
it works opened straight from disk). Regenerate after editing the YAML:

    python tools/upstream-ledger-html.py

GitHub shows .html as source, not a rendered page -- open LEDGER.html in a browser
(e.g. `wmux browser open`), or serve it via GitHub Pages.
"""
import html
import re
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
YAML_PATH = ROOT / 'planning' / 'upstream-watch' / 'LEDGER.yaml'
HTML_PATH = ROOT / 'planning' / 'upstream-watch' / 'LEDGER.html'
# Deep-dive docs are markdown; link them to the GitHub blob view (renders md) so the links
# work from the Pages site and from a locally-opened LEDGER.html alike.
GITHUB_BLOB = 'https://github.com/VortexFPS/VortexArena/blob/main/planning/upstream-watch/'

REL = {'high': ('🟢', 'High'), 'medium': ('🟡', 'Medium'), 'low': ('🟠', 'Low'), 'none': ('🔴', 'None')}
DEC = {'pending': ('⏳', 'Pending'), 'port': ('✅', 'Port'), 'adapt': ('🔧', 'Adapt'),
       'ported': ('📦', 'Ported'), 'defer': ('⏸️', 'Defer'), 'reject': ('❌', 'Reject'), 'n/a': ('➖', 'N/A')}
REL_ORDER = ['high', 'medium', 'low', 'none']
DEC_ORDER = ['pending', 'port', 'adapt', 'ported', 'defer', 'reject', 'n/a']


def inline(s):
    """Escape, then render `code` and **bold** inline markdown (safe: after escaping)."""
    s = html.escape(str(s or ''))
    s = re.sub(r'`([^`]+)`', r'<code>\1</code>', s)
    s = re.sub(r'\*\*([^*]+)\*\*', r'<strong>\1</strong>', s)
    return s


def cls(v):
    return re.sub(r'[^a-z0-9]+', '-', str(v).lower()).strip('-')


def render_row(e):
    uw = html.escape(e['uw'])
    rel, dec = e.get('relevance', 'none'), e.get('decision', 'pending')
    rel_i, rel_l = REL.get(rel, ('', rel))
    dec_i, dec_l = DEC.get(dec, ('', dec))
    kind = html.escape(e.get('kind', ''))
    effort = html.escape(e.get('effort', '') or '')
    src = html.escape(e.get('source', ''))
    url = e.get('url')
    src_html = f'<a href="{html.escape(url)}" target="_blank" rel="noopener"><code>{src}</code></a>' if url else f'<code>{src}</code>'
    dd_url = html.escape(GITHUB_BLOB + e['deep_dive']) if e.get('deep_dive') else None
    uw_cell = f'<a href="{dd_url}" target="_blank" rel="noopener">{uw}</a>' if dd_url else uw

    meta = [src_html, kind]
    if effort:
        meta.append(f'effort {effort}')
    if dd_url:
        meta.append(f'<a href="{dd_url}" target="_blank" rel="noopener">deep dive</a>')
    meta_html = ' · '.join(meta)

    extra = ''
    rec = e.get('recommendation')
    syms = e.get('base_symbols') or []
    if rec or syms:
        parts = []
        if rec:
            parts.append(f'<p class="rec"><span class="lbl">Recommendation.</span> {inline(rec)}</p>')
        if syms:
            chips = ' '.join(f'<code>{html.escape(s)}</code>' for s in syms)
            parts.append(f'<p class="syms"><span class="lbl">Touches.</span> {chips}</p>')
        extra = f'<details><summary>analysis</summary>{"".join(parts)}</details>'

    search = html.escape(' '.join([e['uw'], e.get('headline', ''), e.get('summary', ''),
                                   e.get('recommendation', ''), e.get('source', ''), kind]).lower())
    uwn = int(re.sub(r'\D', '', e['uw']) or 0)
    hl = html.escape(str(e.get('headline', '')).lower())
    return f'''<tr data-rel="{rel}" data-dec="{cls(dec)}" data-kind="{kind}" data-repo="{html.escape(e.get('repo',''))}" data-uwn="{uwn}" data-hl="{hl}" data-search="{search}">
  <td class="uw">{uw_cell}</td>
  <td class="contrib">
    <div class="headline">{inline(e.get('headline',''))}</div>
    <div class="summary">{inline(e.get('summary',''))}</div>
    <div class="meta">{meta_html}</div>
    {extra}
  </td>
  <td class="badge"><span class="pill rel-{cls(rel)}">{rel_i} {html.escape(rel_l)}</span></td>
  <td class="badge"><span class="pill dec-{cls(dec)}">{dec_i} {html.escape(dec_l)}</span></td>
</tr>'''


def render(entries):
    from collections import Counter
    dcount = Counter(e.get('decision', 'pending') for e in entries)
    rcount = Counter(e.get('relevance', 'none') for e in entries)
    kinds = sorted({e.get('kind', '') for e in entries})

    dec_btns = ''.join(
        f'<button class="fbtn" data-group="dec" data-value="{cls(k)}">{DEC[k][0]} {DEC[k][1]}'
        f'<span class="n">{dcount.get(k,0)}</span></button>'
        for k in DEC_ORDER if dcount.get(k, 0))
    rel_btns = ''.join(
        f'<button class="fbtn" data-group="rel" data-value="{k}">{REL[k][0]} {REL[k][1]}'
        f'<span class="n">{rcount.get(k,0)}</span></button>'
        for k in REL_ORDER if rcount.get(k, 0))
    kind_opts = ''.join(f'<option value="{html.escape(k)}">{html.escape(k)}</option>' for k in kinds if k)
    rows = '\n'.join(render_row(e) for e in entries)

    return (TEMPLATE
            .replace('__ROWS__', rows)
            .replace('__DEC_BTNS__', dec_btns)
            .replace('__REL_BTNS__', rel_btns)
            .replace('__KIND_OPTS__', kind_opts)
            .replace('__TOTAL__', str(len(entries))))


TEMPLATE = r'''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Upstream Watch — Ledger</title>
<style>
  :root {
    --bg:#ffffff; --fg:#1f2328; --muted:#656d76; --line:#d0d7de; --card:#f6f8fa;
    --code:#eff1f3; --accent:#0969da;
  }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#0d1117; --fg:#e6edf3; --muted:#9198a1; --line:#30363d; --card:#161b22;
            --code:#1f242c; --accent:#4493f8; }
  }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--fg);
    font:15px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
  header { padding:20px 24px 8px; }
  h1 { margin:0 0 2px; font-size:20px; }
  .sub { color:var(--muted); font-size:13px; }
  .toolbar { position:sticky; top:0; z-index:5; background:var(--bg); border-bottom:1px solid var(--line);
    padding:12px 24px; display:flex; flex-wrap:wrap; gap:10px 16px; align-items:center; }
  .toolbar .grp { display:flex; flex-wrap:wrap; gap:6px; align-items:center; }
  .toolbar label { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em; margin-right:2px; }
  input[type=search], select { background:var(--card); color:var(--fg); border:1px solid var(--line);
    border-radius:6px; padding:6px 10px; font-size:14px; }
  input[type=search] { min-width:240px; flex:1; }
  .fbtn { cursor:pointer; background:var(--card); color:var(--fg); border:1px solid var(--line);
    border-radius:999px; padding:4px 10px; font-size:13px; display:inline-flex; gap:6px; align-items:center; }
  .fbtn.off { opacity:.38; text-decoration:line-through; }
  .fbtn .n { color:var(--muted); font-size:11px; }
  #reset { cursor:pointer; background:none; border:1px solid var(--line); color:var(--muted);
    border-radius:6px; padding:6px 10px; font-size:13px; }
  #count { color:var(--muted); font-size:13px; margin-left:auto; white-space:nowrap; }
  table { border-collapse:collapse; width:100%; }
  thead th { position:sticky; top:57px; background:var(--bg); text-align:left; color:var(--muted);
    font-size:12px; text-transform:uppercase; letter-spacing:.04em; padding:8px 12px; border-bottom:1px solid var(--line); z-index:4; }
  th.sortable { cursor:pointer; user-select:none; white-space:nowrap; }
  th.sortable:hover { color:var(--fg); }
  th .arr { margin-left:5px; color:var(--accent); font-size:11px; }
  th.sorted { color:var(--fg); }
  td { padding:12px; border-bottom:1px solid var(--line); vertical-align:top; }
  td.uw { white-space:nowrap; font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; font-size:12px; }
  td.uw a { color:var(--accent); text-decoration:none; }
  td.contrib { max-width:0; width:100%; }
  .headline { font-weight:600; margin-bottom:4px; }
  .summary { color:var(--fg); font-size:14px; }
  .meta { color:var(--muted); font-size:12.5px; margin-top:6px; }
  .meta a { color:var(--accent); text-decoration:none; }
  code { background:var(--code); border-radius:4px; padding:1px 5px; font-size:.86em;
    font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; }
  details { margin-top:8px; }
  summary { cursor:pointer; color:var(--muted); font-size:12.5px; }
  details p { margin:8px 0 0; font-size:13.5px; }
  .lbl { color:var(--muted); font-weight:600; }
  td.badge { white-space:nowrap; }
  .pill { display:inline-block; padding:3px 9px; border-radius:999px; font-size:12.5px; font-weight:600; border:1px solid transparent; }
  .rel-high{background:#1a7f3722;color:#1a7f37;border-color:#1a7f3755}
  .rel-medium{background:#9a670022;color:#bb8009;border-color:#9a670055}
  .rel-low{background:#bc4c0022;color:#e16f24;border-color:#bc4c0055}
  .rel-none{background:#cf222e22;color:#e5534b;border-color:#cf222e55}
  .dec-pending{background:#656d7622;color:var(--muted);border-color:#656d7644}
  .dec-port{background:#1a7f3722;color:#1a7f37;border-color:#1a7f3755}
  .dec-adapt{background:#1b7c8322;color:#2aa6b0;border-color:#1b7c8355}
  .dec-ported{background:#0969da22;color:var(--accent);border-color:#0969da55}
  .dec-defer{background:#9a670022;color:#bb8009;border-color:#9a670055}
  .dec-reject{background:#cf222e22;color:#e5534b;border-color:#cf222e55}
  .dec-n-a{background:#656d7615;color:var(--muted);border-color:#656d7633}
  tr.hidden { display:none; }
  .empty { padding:40px; text-align:center; color:var(--muted); }
</style>
</head>
<body>
<header>
  <h1>Upstream Watch — Ledger</h1>
  <div class="sub">Triaged original-Xonotic contributions considered for Vortex Arena.
    Source of truth: <code>LEDGER.yaml</code> · regenerate this page with <code>python tools/upstream-ledger-html.py</code>.</div>
</header>
<div class="toolbar">
  <input type="search" id="q" placeholder="Search headline, summary, source…" autocomplete="off">
  <div class="grp"><label>Decision</label>__DEC_BTNS__</div>
  <div class="grp"><label>Relevance</label>__REL_BTNS__</div>
  <div class="grp"><label>Kind</label><select id="kind"><option value="">all</option>__KIND_OPTS__</select></div>
  <button id="reset">reset</button>
  <span id="count"></span>
</div>
<table>
  <thead><tr>
    <th class="sortable" data-key="uw">UW<span class="arr"></span></th>
    <th class="sortable" data-key="contrib">Contribution<span class="arr"></span></th>
    <th class="sortable" data-key="rel">Relevance<span class="arr"></span></th>
    <th class="sortable" data-key="dec">Decision<span class="arr"></span></th>
  </tr></thead>
  <tbody id="rows">
__ROWS__
  </tbody>
</table>
<div class="empty" id="empty" style="display:none">No contributions match the current filters.</div>
<script>
(function(){
  var rows = Array.prototype.slice.call(document.querySelectorAll('#rows tr'));
  var q = document.getElementById('q'), kind = document.getElementById('kind');
  var count = document.getElementById('count'), empty = document.getElementById('empty');
  var btns = Array.prototype.slice.call(document.querySelectorAll('.fbtn'));
  btns.forEach(function(b){ b.addEventListener('click', function(){ b.classList.toggle('off'); apply(); }); });
  function activeSet(group){
    var s = {};
    btns.filter(function(b){return b.dataset.group===group && !b.classList.contains('off');})
        .forEach(function(b){ s[b.dataset.value]=1; });
    return s;
  }
  function apply(){
    var text = q.value.trim().toLowerCase();
    var dec = activeSet('dec'), rel = activeSet('rel'), k = kind.value;
    var shown = 0;
    rows.forEach(function(r){
      var ok = dec[r.dataset.dec] && rel[r.dataset.rel]
        && (!k || r.dataset.kind===k)
        && (!text || r.dataset.search.indexOf(text)!==-1);
      r.classList.toggle('hidden', !ok);
      if (ok) shown++;
    });
    count.textContent = shown + ' of __TOTAL__ shown';
    empty.style.display = shown ? 'none' : 'block';
  }
  q.addEventListener('input', apply);
  kind.addEventListener('change', apply);
  document.getElementById('reset').addEventListener('click', function(){
    q.value=''; kind.value=''; btns.forEach(function(b){b.classList.remove('off');}); apply();
  });

  // --- column sorting ---
  var REL_RANK = {high:0, medium:1, low:2, none:3};
  var DEC_RANK = {pending:0, port:1, adapt:2, ported:3, defer:4, reject:5, 'n-a':6};
  var tbody = document.getElementById('rows');
  var heads = Array.prototype.slice.call(document.querySelectorAll('th.sortable'));
  var sortKey = null, sortDir = 1;
  function keyval(r, key){
    if (key==='uw') return +r.dataset.uwn;
    if (key==='rel') return REL_RANK[r.dataset.rel];
    if (key==='dec') return DEC_RANK[r.dataset.dec];
    return r.dataset.hl;
  }
  function sortBy(key){
    if (sortKey===key) sortDir = -sortDir; else { sortKey = key; sortDir = 1; }
    rows.sort(function(a,b){
      var av = keyval(a,key), bv = keyval(b,key);
      if (av < bv) return -sortDir;
      if (av > bv) return sortDir;
      return (+a.dataset.uwn) - (+b.dataset.uwn);   // stable tiebreak by UW
    });
    rows.forEach(function(r){ tbody.appendChild(r); });
    heads.forEach(function(h){
      var on = h.dataset.key===sortKey;
      h.classList.toggle('sorted', on);
      h.querySelector('.arr').textContent = on ? (sortDir>0?'▲':'▼') : '';
    });
  }
  heads.forEach(function(h){ h.addEventListener('click', function(){ sortBy(h.dataset.key); }); });

  sortBy('uw');   // default: UW ascending, with arrow shown
  apply();
})();
</script>
</body>
</html>
'''


def main():
    entries = yaml.safe_load(YAML_PATH.read_text(encoding='utf-8'))
    HTML_PATH.write_text(render(entries), encoding='utf-8')
    print(f'wrote {HTML_PATH.relative_to(ROOT)} ({len(entries)} entries)')


if __name__ == '__main__':
    main()
