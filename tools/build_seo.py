"""
Finishes the website for search engines after the wiki and changelog are built: puts the site's address into every
page (%SITE% in canonical links, Open Graph tags and structured data), writes robots.txt and sitemap.xml, and checks
every page (one h1, a title, a description, valid JSON-LD, images with alt text and a size, internal links that
exist). Run by the Pages workflow as the last step; fails the build on a problem. It rewrites the pages in place,
so run it locally on a copy of site/.

    python tools/build_seo.py [folder]        # default: site/
"""
import html.parser
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse

# The site's one address: canonical links, the sitemap and social previews all use it
SITE = "https://avion2.app"

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(ROOT, "site")


class Page(html.parser.HTMLParser):
    """What the checks need from a page: headings, links, images, ids, head tags and JSON-LD blocks."""

    def __init__(self):
        super().__init__()
        self.h1 = 0
        self.title = ""
        self.meta = {}
        self.links = []
        self.images = []
        self.ids = set()
        self.ld = []
        self._in = None

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if "id" in a:
            self.ids.add(a["id"])
        if tag == "h1":
            self.h1 += 1
        elif tag == "title":
            self._in = "title"
        elif tag == "meta":
            key = a.get("name") or a.get("property")
            if key:
                self.meta[key] = a.get("content", "")
        elif tag == "a" and "href" in a:
            self.links.append(a["href"])
        elif tag == "link" and a.get("rel") in ("stylesheet", "icon", "apple-touch-icon", "manifest") and "href" in a:
            self.links.append(a["href"])
        elif tag == "img":
            self.images.append(a)
            if "src" in a:
                self.links.append(a["src"])
        elif tag == "script" and a.get("type") == "application/ld+json":
            self._in = "ld"
            self.ld.append("")

    def handle_endtag(self, tag):
        if tag in ("title", "script"):
            self._in = None

    def handle_data(self, data):
        if self._in == "title":
            self.title += data
        elif self._in == "ld":
            self.ld[-1] += data


def pages():
    """Every HTML file under site/, as (URL path, file)."""
    for folder, _, files in os.walk(OUT):
        for name in sorted(files):
            if name.endswith(".html"):
                path = os.path.join(folder, name)
                rel = os.path.relpath(path, OUT).replace(os.sep, "/")
                yield ("/" + rel).replace("index.html", ""), path


def modified(text, path):
    """The page's last change: the date in its structured data, else the last commit of the file, else today."""
    if match := re.search(r'"dateModified":\s*"(\d{4}-\d{2}-\d{2})', text):
        return match.group(1)
    try:
        out = subprocess.run(["git", "log", "-1", "--format=%cs", "--", path], cwd=ROOT, capture_output=True, text=True, timeout=10)
        if out.returncode == 0 and out.stdout.strip():
            return out.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return time.strftime("%Y-%m-%d")


def check(url, page, parsed, all_ids):
    problems = []
    if parsed.h1 != 1:
        problems.append(f"{parsed.h1} h1 headings (want 1)")
    if not parsed.title.strip():
        problems.append("no title")
    if not parsed.meta.get("description"):
        problems.append("no meta description")
    for i, block in enumerate(parsed.ld):
        try:
            json.loads(block)
        except json.JSONDecodeError as e:
            problems.append(f"JSON-LD block {i + 1} doesn't parse: {e}")
    for img in parsed.images:
        if not img.get("alt"):
            problems.append(f"image without alt text: {img.get('src')}")
        if not (img.get("width") and img.get("height")):
            problems.append(f"image without width/height: {img.get('src')}")
    for href in parsed.links:
        target = urllib.parse.urljoin(SITE + url, href)
        if not target.startswith(SITE + "/"):
            continue  # another site
        path, _, fragment = target[len(SITE):].partition("#")
        path = path.split("?")[0]
        file = os.path.join(OUT, *path.strip("/").split("/")) if path.strip("/") else OUT
        if os.path.isdir(file):
            file = os.path.join(file, "index.html")
        if not os.path.exists(file):
            problems.append(f"broken link: {href}")
        elif fragment and fragment not in all_ids.get(file, set()):
            problems.append(f"missing anchor: {href}")
    return problems


def main() -> int:
    found = []
    for url, path in pages():
        with open(path, encoding="utf-8") as f:
            text = f.read().replace("%SITE%", SITE)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        parsed = Page()
        parsed.feed(text)
        found.append((url, path, text, parsed))

    all_ids = {path: parsed.ids for _, path, _, parsed in found}
    problems = []
    for url, path, text, parsed in found:
        problems += [f"{url}: {p}" for p in check(url, path, parsed, all_ids)]

    # Pages that say noindex (the 404 page) stay out of the sitemap
    listed = [(url, modified(text, path)) for url, path, text, parsed in found if "noindex" not in parsed.meta.get("robots", "")]
    listed.sort(key=lambda p: (p[0] != "/", p[0]))
    entries = "\n".join(f"  <url><loc>{SITE}{url}</loc><lastmod>{day}</lastmod></url>" for url, day in listed)
    with open(os.path.join(OUT, "sitemap.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f'<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n{entries}\n</urlset>\n')
    with open(os.path.join(OUT, "robots.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"User-agent: *\nAllow: /\n\nSitemap: {SITE}/sitemap.xml\n")

    for p in problems:
        print(f"error: {p}", file=sys.stderr)
    print(f"{len(listed)} pages in the sitemap, {len(problems)} problems", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
