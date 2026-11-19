#!/usr/bin/env python3
"""Check the local decision page in headless Firefox with geckodriver on PATH."""

import argparse
import functools
import hashlib
import http.server
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen

from parity_reference import PIN


APPROVED = {
    "records.nickname_prompt": "Keep the existing First Run and Profile controls and leave the in-match prompt manual.",
    "records.original_server_import": "Offer an operator-run migration into the live native ladder for verified identity mappings. Preserve the original database and keep unmatched records in a separate historical list.",
    "menu.guide.filter_reselection": "Remember the most recently selected Guide entry in each topic and restore it when it is available again after an empty search.",
    "menu.guide.catalog_order": "Use the original Xonotic order for shared Guide entries and append native-only entries within their topic.",
    "mapinfo.server_visual_defaults": "Add a player opt-in for receiving those three map switches from a server. Use mounted settings until the player opts in.",
    "hud.superweapons.unlimited": "Match Xonotic and hide the row for unlimited superweapons. Keep the ordinary timed countdown.",
    "server.matrix-bridge": "Implement the optional Matrix bridge inside the native dedicated server, including original-compatible console controls.",
    "bot-ai.scripting.commands": "Support Xonotic’s full bot command queue, including timed and synchronized sequences.",
    "mutator-sandbox.drag.grab": "Implement carrying and releasing Sandbox objects with Xonotic’s ownership and read-only rules.",
    "LC-9 / Q8": "Plan original-compatible identity and encrypted connections for legacy clients.",
    "duel.mapsupport.gating": "Use Xonotic’s deathmatch fallback in both screens, including large maps.",
    "content.particles": "Match the pinned artwork and effect definitions in Original mode. Preserve the authored modern effects.",
    "content.stock-demos": "Bundle both recordings and make playback work from the game’s demo browser.",
    "engine.curved-collision": "Adopt the original collision settings for native play after a curved-floor movement playtest, and investigate rebuilt-map differences separately.",
}
EVIDENCE = {
    "chat.ignore.persistence": ["../scoreboard-blocks/row4-stock-rows.webp", "../scoreboard-blocks/row4-native-rows.webp"],
    "records.nickname_prompt": ["../hud-commands/name-prompt.webp"],
    "records.original_server_import": ["../record-lifecycle/01-ctf-after-restart.webp"],
    "menu.guide.filter_reselection": ["guide-original-search.webp", "guide-native-search.webp",
                                      "../deferred-audit/guide/native-selection-restored.webp"],
    "menu.guide.catalog_order": ["guide-original-order.webp", "guide-native-order.webp",
                                 "../deferred-audit/guide/catalog-order-verified.webp"],
    "mapinfo.server_visual_defaults": ["../map-settings/frames/darkzone-authored-fog.webp",
                                      "../map-settings/frames/darkzone-local-fog-off.webp"],
    "hud.superweapons.unlimited": ["superweapons-hud.webp"],
    "server.matrix-bridge": ["serverinfo.webp"],
    "bot-ai.scripting.commands": ["create.webp"],
    "mutator-sandbox.drag.grab": ["sandbox.webp"],
    "LC-9 / Q8": ["serverinfo.webp"],
    "duel.mapsupport.gating": ["create.webp"],
    "content.particles": ["particle-reference.webp", "particle-native.webp", "effects.webp"],
    "content.stock-demos": ["demo-list.webp"],
    "engine.curved-collision": ["stormkeep.webp"],
}
PARITY = Path(__file__).resolve().parents[1] / "planning/parity"


class QuietServer(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def check(page, driver):
    # Serve relative screenshots from the same tree as the editable page.
    root = PARITY if page.is_relative_to(PARITY) else page.parent
    handler = functools.partial(QuietServer, directory=str(root))
    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            page_url = f"http://127.0.0.1:{server.server_port}/{quote(str(page.relative_to(root)))}"
            offline = page != PARITY / "review/index.html"
            run_browser(driver, page_url, server.shutdown if offline else None)
        finally:
            server.shutdown()


def run_browser(driver, page_url, disconnect):
    with socket.socket() as port_probe:
        port_probe.bind(("127.0.0.1", 0))
        port = port_probe.getsockname()[1]

    def request(path, data=None, method=None):
        body = None if data is None else json.dumps(data).encode()
        req = Request(f"http://127.0.0.1:{port}{path}", data=body, method=method,
                      headers={"Content-Type": "application/json"})
        try:
            with urlopen(req, timeout=45) as response:
                return json.load(response)["value"]
        except HTTPError as exc:
            raise RuntimeError(exc.read().decode(errors="replace")) from exc

    with tempfile.TemporaryDirectory(prefix="vortex-decision-check-") as temp:
        downloads = Path(temp) / "downloads"
        with open(Path(temp) / "driver.log", "w+") as log:
            proc = subprocess.Popen([driver, "--host", "127.0.0.1", "--port", str(port)],
                                    stdout=log, stderr=subprocess.STDOUT)
            session = None
            try:
                for _ in range(100):
                    if proc.poll() is not None:
                        log.seek(0)
                        raise RuntimeError(log.read())
                    try:
                        request("/status")
                        break
                    except OSError:
                        time.sleep(.1)
                prefs = {"browser.download.dir": str(downloads), "browser.download.folderList": 2,
                         "browser.download.useDownloadDir": True, "browser.helperApps.neverAsk.saveToDisk": "text/plain"}
                options = {"args": ["-headless"], "prefs": prefs}
                started = request("/session", {"capabilities": {"alwaysMatch": {
                    "browserName": "firefox", "moz:firefoxOptions": options}}})
                session = "/session/" + started["sessionId"]
                verify_interactions(request, session, page_url, downloads, disconnect)
            finally:
                try:
                    if session:
                        request(session, method="DELETE")
                finally:
                    proc.terminate()
                    proc.wait(timeout=10)


def verify_interactions(request, session, page_url, downloads, disconnect):
    def js(code, asynchronous=False):
        return request(session + ("/execute/async" if asynchronous else "/execute/sync"),
                       {"script": code, "args": []})

    def navigate(url):
        request(session + "/url", {"url": url})

    def downloaded(suffix, count):
        for _ in range(30):
            navigate(downloads.as_uri() + "/")
            urls = js('return [...document.links].map(a=>a.href).filter(url=>url.endsWith('
                      + json.dumps(suffix) + '))')
            if len(urls) == count:
                return urls
            time.sleep(.2)
        raise AssertionError(f"Expected {count} downloads ending in {suffix}, found {urls}")

    def export():
        rows = js('return [...document.querySelectorAll("input:checked")].map(input => '
                  '[input.name, input.closest("article").querySelector("h2").textContent, input.parentElement.textContent])')
        js('document.querySelector("#export").click()')
        return [f"{key} — {title}\n{answer}" for key, title, answer in rows]

    navigate(page_url)
    if disconnect:
        # Snap Firefox has its own /tmp. Transfer the standalone file into its download
        # directory, stop the HTTP server, then open the downloaded HTML from disk.
        js('const link=document.createElement("a"); link.href=location.href; '
           'link.download="offline-decisions.html"; document.body.append(link); link.click()')
        file_url, = downloaded(".html", 1)
        disconnect()
        navigate(file_url)
        assert js('return location.protocol === "file:" && '
                  '[...document.images].every(i=>i.src.startsWith("blob:")) && '
                  'document.querySelector("#results").href.startsWith("blob:")')
        js('document.querySelector("#results").click()')
        downloaded(".md", 1)
        # Read the downloaded attachment through the browser's native file input.
        element = js('const i=document.createElement("input"); i.type="file"; '
                     'document.body.append(i); return i')
        element_id = element["element-6066-11e4-a52e-4f735466cecf"]
        request(session + "/element/" + element_id + "/value",
                {"text": str(downloads / "ORDERED-FOLLOWUP.md")})
        report_digest = js('const done=arguments[0]; document.querySelector("input[type=file]").files[0]'
                           '.arrayBuffer().then(bytes=>crypto.subtle.digest("SHA-256", bytes))'
                           '.then(bytes=>done(Array.from(new Uint8Array(bytes), b=>b.toString(16)'
                           '.padStart(2,"0")).join("")), error=>done(String(error)))', True)
        assert report_digest == hashlib.sha256((PARITY / "ORDERED-FOLLOWUP.md").read_bytes()).hexdigest(), "Report download differs"
        navigate(file_url)
    for width in (1152, 390):
        request(session + "/window/rect", {"width": width, "height": 840})
        assert js("return document.documentElement.scrollWidth <= innerWidth"), "Horizontal overflow"
    sizes = js("const done=arguments[0]; Promise.all([...document.images].map(async img => "
               "{await img.decode(); return img.naturalWidth})).then(done, e=>done(String(e)))", True)
    assert isinstance(sizes, list) and sizes and all(sizes), sizes
    placement = js('return Object.fromEntries([...document.querySelectorAll("article")].map(card => '
                   '[card.querySelector("input").name,[...card.querySelectorAll("img")].map(i=>i.dataset.source)]))')
    assert placement == EVIDENCE, f"Screenshot inventory differs: {placement}"
    assets = js("""
        const done = arguments[0];
        async function digest(url) {
            const response = await fetch(url);
            if (!response.ok) throw new Error("Failed to load " + url);
            const bytes = await crypto.subtle.digest("SHA-256", await response.arrayBuffer());
            return Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
        }
        (async () => {
            const result = [];
            for (const card of document.querySelectorAll("article")) {
                const images = card.querySelectorAll("img");
                if (!images.length) throw new Error("Decision has no image");
                for (const img of images) {
                    if (img.closest("a").href !== img.src) throw new Error(img.alt);
                    result.push([img.dataset.source, await digest(img.src)]);
                }
            }
            result.push(["../ORDERED-FOLLOWUP.md", await digest(document.querySelector("#results").href)]);
            return result;
        })().then(done, error => done(String(error)));
    """, True)
    assert isinstance(assets, list) and len(assets) == len(sizes) + 1, assets
    for name, digest in assets:
        source = (PARITY / "review" / name).resolve()
        source.relative_to(PARITY)
        assert hashlib.sha256(source.read_bytes()).hexdigest() == digest, name
    names = js('return [...document.querySelectorAll("fieldset")].map(f=>f.querySelector("input").name)')
    assert len(set(names)) == len(names) and set(names) == set(EVIDENCE), names
    defaults = js('return Object.fromEntries([...document.querySelectorAll("input:checked")]'
                  '.map(i=>[i.name,i.parentElement.textContent]))')
    assert defaults == APPROVED, defaults
    before = export()
    js('document.querySelector("input:checked").closest("fieldset").querySelector("button.clear").click()')
    partial = export()
    assert [len(before), len(partial)] == [len(APPROVED), len(APPROVED)-1]
    expected = [before, partial]
    js('document.querySelector("#earlier").open=true')
    counts = js('return [...document.querySelectorAll("fieldset")].map(f=>f.querySelectorAll("input").length)')
    assert min(counts) >= 2, counts
    for n in range(max(counts)):
        js('for(const field of document.querySelectorAll("fieldset")) { '
           'const options=field.querySelectorAll("input"); options[' + str(n) + ' % options.length].click() }')
        expected.append(export())
    js('document.querySelector("#earlier").open=true; for(const b of document.querySelectorAll("button.clear")) b.click()')
    assert js('return document.querySelector("#export").disabled && !document.querySelector(":checked")')

    files = downloaded(".txt", len(expected))
    actual = []
    for url in files:
        navigate(url)
        header, _, body = js("return document.body.textContent").strip().partition("\n\n")
        assert PIN in header and "Leave unanswered decisions open" in header
        actual.append(tuple(body.split("\n\n")))
    assert sorted(actual) == sorted(tuple(x) for x in expected), "Downloaded answers differ from selection"
    where = "offline file" if disconnect else "editable page"
    print(f"Firefox verified the {where}, {len(defaults)} approved answers, {len(sizes)} screenshot placements, all alternatives, "
          f"and {len(expected)} selected-answer downloads.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("page", type=Path, nargs="?", default=PARITY / "review/index.html")
    parser.add_argument("--driver", default="geckodriver")
    if not __debug__:
        parser.error("Run without -O so the verification assertions execute")
    args = parser.parse_args()
    check(args.page.resolve(), args.driver)
