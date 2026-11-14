"""Preview the CopperFS project site, or prepare its static directory for GitHub Pages."""

import argparse
from functools import partial
from html import escape
from html.parser import HTMLParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import subprocess
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "site"
REPO = "https://github.com/ilehtoranta/CopperFS"
PREFIX = "/CopperFS/"
REPORT_LINK = re.compile(r'href="\.\./\.\./([^"#]+)(#[^"]+)?"')
REVISION = re.compile(r'<span id="source-revision">.*?</span>')


def slug(heading):
    return re.sub(r"[^\w -]", "", heading.lower()).replace(" ", "-")


def headings(source):
    """Map Markdown heading lines to GitHub-style anchors in the cited reports."""
    result = {}
    used = set()
    in_code = False
    for number, line in enumerate(source.splitlines()):
        if line.startswith("```"):
            in_code = not in_code
        match = re.match(r"^#{1,6} +(.+)$", line)
        if match and not in_code:
            base = anchor = slug(match[1])
            suffix = 0
            while anchor in used:
                suffix += 1
                anchor = f"{base}-{suffix}"
            result[number] = anchor
            used.add(anchor)
    return result


class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = set()

    def handle_starttag(self, tag, attrs):
        if tag == "a":
            self.links.add(dict(attrs)["href"])


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def public_bytes(url, method="GET"):
    request = Request(url, method=method, headers={"User-Agent": "CopperFS-Pages-link-check"})
    with urlopen(request, timeout=30) as response:
        return response.read()


def prepare(publish):
    revision = git("rev-parse", "HEAD")
    pages = {path: path.read_text(encoding="utf-8") for path in SITE.rglob("*.html")}
    references = {}
    for markup in pages.values():
        for match in REPORT_LINK.finditer(markup):
            path, fragment = match.groups()
            references.setdefault(path, set())
            if fragment:
                references[path].add(fragment[1:])

    documents = {}
    for path, anchors in references.items():
        source = (ROOT / path).read_text(encoding="utf-8")
        documents[path] = source
        missing = anchors - set(headings(source).values())
        if missing:
            raise ValueError(f"Missing heading in {path}: {sorted(missing)}")
        if publish:
            raw = f"https://raw.githubusercontent.com/ilehtoranta/CopperFS/{revision}/{path}"
            public_source = public_bytes(raw).decode("utf-8").replace("\r\n", "\n")
            if public_source != source:
                raise ValueError(f"Public evidence differs at {revision}: {path}")

    dirty = bool(git("status", "--porcelain", "--", *references))
    if publish and dirty:
        raise ValueError("Evidence in this checkout differs from the checked-out commit.")
    label = f'<a href="{REPO}/commit/{revision}"><code>{revision[:12]}</code></a>'
    if dirty:
        label = "working tree based on " + label
    base = f"{REPO}/blob/{revision}/" if publish else PREFIX + "source/"
    for path, markup in pages.items():
        markup = REPORT_LINK.sub(lambda m: f'href="{base}{m[1]}{m[2] or ""}"', markup)
        pages[path] = REVISION.sub(f'<span id="source-revision">{label}</span>', markup)
    return pages, documents


def publish_site():
    pages, _ = prepare(publish=True)
    # Raw files and their heading anchors were verified above. Check other linked
    # destinations anonymously as a reader would, including the classic release.
    links = Links()
    for markup in pages.values():
        links.feed(markup)
    pinned = f"{REPO}/blob/{git('rev-parse', 'HEAD')}/"
    for href in sorted(links.links):
        if href.startswith("https://") and not href.startswith(pinned):
            public_bytes(href, method="HEAD")
    for path, markup in pages.items():
        path.write_text(markup, encoding="utf-8")


def source_page(path, source):
    ids = headings(source)
    lines = [
        (f'<span id="{ids[i]}">{escape(line)}</span>' if i in ids else escape(line))
        for i, line in enumerate(source.splitlines())
    ]
    return (f'<!doctype html><html lang="en"><meta charset="utf-8">'
            f'<meta name="viewport" content="width=device-width, initial-scale=1">'
            f'<title>{escape(path)} · CopperFS source</title><style>'
            'body{max-width:80ch;margin:2em auto;padding:0 1em;color:#252823;background:#f8f7f3}'
            'pre{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.5}'
            ':target{background:#f2dfbf}</style>'
            f'<a href="{PREFIX}">CopperFS</a><h1>{escape(path)}</h1><pre>'
            + "\n".join(lines) + "</pre></html>").encode()


class Preview(BaseHTTPRequestHandler):
    def __init__(self, *args, pages, documents, **kwargs):
        self.pages = pages
        self.documents = documents
        super().__init__(*args, **kwargs)

    def do_GET(self):
        path = urlsplit(self.path).path
        if path in ("/", PREFIX.rstrip("/")):
            self.send_response(302)
            self.send_header("Location", PREFIX)
            self.end_headers()
            return
        data = None
        mime = "text/html; charset=utf-8"
        if path.startswith(PREFIX + "source/"):
            name = path.removeprefix(PREFIX + "source/")
            if name in self.documents:
                data = source_page(name, self.documents[name])
        elif path.startswith(PREFIX):
            name = path.removeprefix(PREFIX)
            if not name or name.endswith("/"):
                name += "index.html"
            file = (SITE / name).resolve()
            if file in self.pages:
                data = self.pages[file].encode()
            elif file.is_relative_to(SITE) and file.is_file():
                types = {".css": "text/css", ".xml": "application/xml", ".webp": "image/webp"}
                if file.suffix in types:
                    mime = types[file.suffix]
                    data = file.read_bytes()
        if data is None:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["preview", "publish"])
    parser.add_argument("--port", default=8765, type=int)
    args = parser.parse_args()
    if args.action == "publish":
        publish_site()
    else:
        pages, documents = prepare(publish=False)
        handler = partial(Preview, pages=pages, documents=documents)
        with ThreadingHTTPServer(("127.0.0.1", args.port), handler) as server:
            print(f"http://127.0.0.1:{server.server_port}{PREFIX}", flush=True)
            server.serve_forever()
