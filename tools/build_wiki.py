"""
Builds the protocol wiki (site/protocol/index.html) from docs/PROTOCOL.md: one page with the sections in a sidebar,
a filter, and the ✅ markers as "verified" badges. Run by the Pages workflow on every deploy; the page is not committed.

    pip install markdown==3.7
    python tools/build_wiki.py            # writes site/protocol/index.html
"""
import html
import os
import re
import subprocess
import sys
import time

import markdown
from markdown.extensions.toc import slugify

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "docs", "PROTOCOL.md")
OUT = os.path.join(ROOT, "site", "protocol", "index.html")
REPO = "https://github.com/void-cx/avion2"

PAGE = """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>AION2 Network Protocol Reference · AVION2</title>
  <meta name="description" content="How AION2's game traffic is built: framing, compression and every packet AVION2 decodes, with what is verified and what is still open.">
  <link rel="canonical" href="%SITE%/protocol/">
  <meta name="theme-color" content="#070b14">
  <meta property="og:type" content="article">
  <meta property="og:site_name" content="AVION2">
  <meta property="og:locale" content="en_US">
  <meta property="og:url" content="%SITE%/protocol/">
  <meta property="og:title" content="AION2 Network Protocol Reference">
  <meta property="og:description" content="Framing, compression and every AION2 packet AVION2 decodes, with what is verified and what is still open.">
  <meta property="og:image" content="%SITE%/img/social.png">
  <meta property="og:image:width" content="1200">
  <meta property="og:image:height" content="630">
  <meta property="og:image:alt" content="AVION2, damage meter and fight analyzer for AION2">
  <meta property="article:modified_time" content="{updated}">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="twitter:title" content="AION2 Network Protocol Reference">
  <meta name="twitter:description" content="Framing, compression and every AION2 packet AVION2 decodes, with what is verified and what is still open.">
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
        "@type": "TechArticle",
        "headline": "AION2 Network Protocol Reference",
        "description": "How AION2's game traffic is built: framing, compression and every packet AVION2 decodes.",
        "url": "%SITE%/protocol/",
        "image": "%SITE%/img/social.png",
        "inLanguage": "en",
        "dateModified": "{updated}",
        "isPartOf": {{ "@id": "%SITE%/#website" }},
        "author": {{ "@type": "Organization", "name": "void-cx", "url": "https://github.com/void-cx" }},
        "publisher": {{ "@type": "Organization", "name": "void-cx", "url": "https://github.com/void-cx" }}
      }},
      {{
        "@type": "BreadcrumbList",
        "itemListElement": [
          {{ "@type": "ListItem", "position": 1, "name": "AVION2", "item": "%SITE%/" }},
          {{ "@type": "ListItem", "position": 2, "name": "Protocol", "item": "%SITE%/protocol/" }}
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
      <a href="./">Protocol</a>
      <a href="../changelog/">Changelog</a>
      <a href="../#install">Install</a>
      <a href="{repo}">GitHub</a>
    </nav>
  </header>

  <main class="wiki">
    <aside class="wiki-side">
      <input id="filter" class="wiki-filter" type="search" placeholder="Filter: opcode, word…" aria-label="Filter the sections">
      <nav class="wiki-toc">{toc}</nav>
    </aside>

    <article class="wiki-body">
      <p class="eyebrow">Technical documentation</p>
      <h1>AION2 Network Protocol</h1>
      <p class="fine">Last change {updated} · source: <a href="{repo}/blob/main/docs/PROTOCOL.md">docs/PROTOCOL.md</a>
        · corrections welcome as issues or pull requests</p>
      <div id="sections">{body}</div>
      <p id="nothing" class="fine" hidden>Nothing matches the filter.</p>
    </article>
  </main>

  <footer>
    <p>AVION2 · <a href="{repo}">void-cx/avion2</a> · <a href="../changelog/">Changelog</a> · GPL-3.0</p>
    <p class="fine">Not affiliated with NCSOFT. AION is a trademark of NCSOFT Corporation.</p>
  </footer>

  <script>
    // Filter: show the sections (h2/h3 blocks) whose text contains every word typed
    (function () {{
      const input = document.getElementById('filter');
      const root = document.getElementById('sections');
      const blocks = [];
      let current = null;
      for (const node of Array.from(root.children)) {{
        if (/^H[23]$/.test(node.tagName)) {{
          current = {{ head: node, nodes: [node], level: node.tagName }};
          blocks.push(current);
        }} else if (current) {{
          current.nodes.push(node);
        }}
      }}
      const links = new Map(Array.from(document.querySelectorAll('.wiki-toc a')).map(a => [a.getAttribute('href').slice(1), a]));
      input.addEventListener('input', () => {{
        const words = input.value.toLowerCase().split(/\\s+/).filter(Boolean);
        let shown = 0;
        for (const b of blocks) {{
          const text = b.nodes.map(n => n.textContent).join(' ').toLowerCase();
          const hit = words.every(w => text.includes(w));
          b.nodes.forEach(n => n.hidden = !hit);
          const link = links.get(b.head.id);
          if (link) link.parentElement.hidden = !hit;
          if (hit) shown++;
        }}
        // a matching subsection keeps its section visible in the sidebar
        for (const a of links.values()) {{
          if (!a.parentElement.hidden) {{
            for (let li = a.parentElement.parentElement.closest('li'); li; li = li.parentElement.closest('li')) li.hidden = false;
          }}
        }}
        document.getElementById('nothing').hidden = shown > 0;
      }});
    }})();
  </script>
</body>
</html>
"""


def updated() -> str:
    """The date of the last commit that changed the notes (the build date when git is not available)."""
    try:
        out = subprocess.run(["git", "log", "-1", "--format=%cs", "--", SOURCE], cwd=ROOT, capture_output=True, text=True, timeout=10)
        if out.returncode == 0 and out.stdout.strip():
            return out.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return time.strftime("%Y-%m-%d")


def toc_html(tokens) -> str:
    items = []
    for token in tokens:
        name = re.split(r"\s*(?:✅|\()", html.unescape(token["name"]), maxsplit=1)[0]
        children = toc_html(token["children"]) if token["children"] else ""
        items.append(f'<li><a href="#{token["id"]}">{html.escape(name)}</a>{children}</li>')
    return "<ul>" + "".join(items) + "</ul>"


def anchor(value: str, separator: str) -> str:
    """Short, stable anchors: the heading without its verification note ("04-38-direct-damage")."""
    value = re.split(r"\s*(?:✅|\(|—\s*verified)", value, maxsplit=1)[0]
    return slugify(value, separator)


STATUS = {"Verified": "ok", "Partly verified": "part", "Unconfirmed": "open"}


def status(match) -> str:
    """"Status: Verified · note" under a heading → a subtitle with a coloured badge per status word."""
    parts = []
    for piece in match.group(1).split(" · "):
        word = next((w for w in STATUS if piece.startswith(w)), None)
        if word:
            rest = piece[len(word):].strip()
            parts.append(f'<span class="badge {STATUS[word]}">{word}</span>' + (f" {rest}" if rest else ""))
        else:
            parts.append(piece)
    return '<p class="wiki-status">' + ' <span class="sep">·</span> '.join(parts) + "</p>"


def main() -> int:
    text = open(SOURCE, encoding="utf-8").read()
    # The page has its own title
    text = re.sub(r"\A# [^\n]*\n", "", text, count=1)
    # The notes start lists right under a paragraph line (GitHub renders that); Markdown needs a blank line first
    text = re.sub(r"(?m)^((?![-*] |\d+\. |\s|```)[^\n]+)\n(?=[-*] |\d+\. )", r"\1\n\n", text)
    md = markdown.Markdown(extensions=["fenced_code", "sane_lists", "tables", "toc"],
                           extension_configs={"toc": {"toc_depth": "2-3", "slugify": anchor}})
    body = md.convert(text)
    # Links in the source use GitHub's anchors, which keep a hyphen per separator ("05-e0--stagger-gauge");
    # the page's anchors never hold two in a row, so runs of hyphens in an in-page link are collapsed
    body = re.sub(r'href="#([^"]*)"', lambda m: 'href="#' + re.sub(r"-{2,}", "-", m.group(1)) + '"', body)
    body = re.sub(r"<p>Status: (.*?)</p>", status, body)
    body = re.sub(r"<table>(.*?)</table>", r'<div class="table-wrap"><table>\1</table></div>', body, flags=re.S)
    toc = toc_html(md.toc_tokens[0]["children"] if len(md.toc_tokens) == 1 and md.toc_tokens[0]["level"] == 1 else md.toc_tokens)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(PAGE.format(repo=REPO, toc=toc, body=body, updated=updated()))
    print(f"wrote {OUT}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
