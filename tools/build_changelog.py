"""
Builds the changelog page (site/changelog/index.html) from the GitHub releases: each release's "What's new", newest
first, with its download and the compare link. Run by the Pages workflow (also when a release is published or edited);
the page is not committed.

    pip install markdown==3.7
    python tools/build_changelog.py            # writes site/changelog/index.html (GITHUB_TOKEN is used if set)
"""
import html
import json
import os
import re
import sys
import urllib.request

import markdown

REPO = "void-cx/avion2"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "site", "changelog", "index.html")

PAGE = """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>AVION2 Changelog · What's new in each release</title>
  <meta name="description" content="What changed in each AVION2 release, newest first: new features, fixes and downloads for the AION2 damage meter. Latest: {latest}.">
  <link rel="canonical" href="%SITE%/changelog/">
  <meta name="theme-color" content="#070b14">
  <meta property="og:type" content="website">
  <meta property="og:site_name" content="AVION2">
  <meta property="og:locale" content="en_US">
  <meta property="og:url" content="%SITE%/changelog/">
  <meta property="og:title" content="AVION2 Changelog">
  <meta property="og:description" content="What changed in each AVION2 release. Latest: {latest}.">
  <meta property="og:image" content="%SITE%/img/social.png">
  <meta property="og:image:width" content="1200">
  <meta property="og:image:height" content="630">
  <meta property="og:image:alt" content="AVION2, damage meter and fight analyzer for AION2">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="twitter:title" content="AVION2 Changelog">
  <meta name="twitter:description" content="What changed in each AVION2 release. Latest: {latest}.">
  <meta name="twitter:image" content="%SITE%/img/social.png">
  <link rel="icon" href="../favicon.ico" sizes="any">
  <link rel="icon" href="../img/favicon.png" type="image/png">
  <link rel="apple-touch-icon" href="../img/apple-touch-icon.png">
  <link rel="manifest" href="../site.webmanifest">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Cinzel:wght@500;700&family=Inter:wght@400;500;600&display=swap" rel="stylesheet" media="print" onload="this.media='all'">
  <noscript><link href="https://fonts.googleapis.com/css2?family=Cinzel:wght@500;700&family=Inter:wght@400;500;600&display=swap" rel="stylesheet"></noscript>
  <link rel="stylesheet" href="../styles.css">
  <script type="application/ld+json">
  {{
    "@context": "https://schema.org",
    "@graph": [
      {{
        "@type": "WebPage",
        "name": "AVION2 Changelog",
        "description": "What changed in each AVION2 release.",
        "url": "%SITE%/changelog/",
        "inLanguage": "en",
        "dateModified": "{modified}",
        "isPartOf": {{ "@id": "%SITE%/#website" }},
        "about": {{ "@id": "%SITE%/#app" }}
      }},
      {{
        "@type": "BreadcrumbList",
        "itemListElement": [
          {{ "@type": "ListItem", "position": 1, "name": "AVION2", "item": "%SITE%/" }},
          {{ "@type": "ListItem", "position": 2, "name": "Changelog", "item": "%SITE%/changelog/" }}
        ]
      }}
    ]
  }}
  </script>
</head>
<body>
  <header class="top">
    <a class="brand" href="../">✦ AVION2</a>
    <nav aria-label="Main">
      <a href="../#features">Features</a>
      <a href="../protocol/">Protocol</a>
      <a href="./">Changelog</a>
      <a href="../#install">Install</a>
      <a href="https://github.com/{repo}">GitHub</a>
    </nav>
  </header>

  <main class="changelog">
    <p class="eyebrow">Changelog</p>
    <h1>What's new</h1>
    <p class="fine">Every release of AVION2, newest first. AVION2 shows new versions as a banner on its home page and
      installs them with one click.</p>
    {releases}
  </main>

  <footer>
    <p>AVION2 · <a href="https://github.com/{repo}">{repo}</a> · <a href="../protocol/">Protocol wiki</a> · GPL-3.0</p>
    <p class="fine">Not affiliated with NCSOFT. AION is a trademark of NCSOFT Corporation.</p>
  </footer>
</body>
</html>
"""

# Lines every release repeats (how to update, how to install, the compare link): not part of what changed
BOILERPLATE = re.compile(r"^\s*(\*\*Updating:\*\*|\*\*New here\?\*\*|Changes since |Full Changelog)", re.I)


def fetch():
    request = urllib.request.Request(f"https://api.github.com/repos/{REPO}/releases?per_page=100",
                                     headers={"Accept": "application/vnd.github+json", "User-Agent": "AVION2 site build"})
    if token := os.environ.get("GITHUB_TOKEN"):
        request.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def version(tag):
    return tuple(int(p) for p in re.findall(r"\d+", tag)[:3])


def notes(body):
    text = (body or "").replace("\r\n", "\n")
    text = re.sub(r"(?m)^##\s+What's new\s*\n", "", text)
    lines = [line for line in text.split("\n") if not BOILERPLATE.match(line)]
    text = "\n".join(lines).strip()
    # GitHub renders a list right under a line; Markdown needs a blank line first
    text = re.sub(r"(?m)^((?![-*] |\d+\. |\s)[^\n]+)\n(?=[-*] )", r"\1\n\n", text)
    # GitHub nests with two spaces, Markdown wants four (sub-lists and paragraphs inside a list item)
    text = re.sub(r"(?m)^  (?=\S)", "    ", text)
    # …and a list item right after such an indented paragraph starts the next item, not a continuation
    text = re.sub(r"(?m)^(    (?![-*] )[^\n]+)\n(?=[-*] )", r"\1\n\n", text)
    # Bare links become links
    text = re.sub(r"(?<![(<\"])(https?://[^\s)<]+)", r"<\1>", text)
    return markdown.markdown(text, extensions=["sane_lists"]) if text else "<p>Maintenance release.</p>"


def main() -> int:
    releases = [r for r in fetch() if not r.get("draft") and not r.get("prerelease")]
    releases.sort(key=lambda r: version(r["tag_name"]), reverse=True)
    parts = []
    for i, r in enumerate(releases):
        tag = r["tag_name"]
        day = (r.get("published_at") or r.get("created_at") or "")[:10]
        zip_url = next((a["browser_download_url"] for a in r.get("assets", []) if a["name"].endswith(".zip")), r["html_url"])
        previous = releases[i + 1]["tag_name"] if i + 1 < len(releases) else None
        links = [f'<a href="{html.escape(zip_url)}">Download</a>', f'<a href="{html.escape(r["html_url"])}">Release page</a>']
        if previous:
            links.append(f'<a href="https://github.com/{REPO}/compare/{previous}...{tag}">All changes since {previous}</a>')
        latest = ' <span class="badge ok">latest</span>' if i == 0 else ""
        parts.append(f'''<section class="release" id="{html.escape(tag)}">
      <h2>{html.escape(tag)}{latest}</h2>
      <p class="fine">{day}</p>
      {notes(r.get("body"))}
      <p class="release-links">{" · ".join(links)}</p>
    </section>''')

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        newest = releases[0] if releases else {}
        f.write(PAGE.format(repo=REPO, releases="\n    ".join(parts), latest=html.escape(newest.get("tag_name", "")),
                            modified=(newest.get("published_at") or "")[:10]))
    print(f"{len(releases)} releases → {OUT}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
