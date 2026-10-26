"""Refresh root reading copies without resetting live task status or changing design baselines.

Optional documentation tool; requires markdown-it-py. The desktop application does not require Python.
"""
from pathlib import Path
import re
from markdown_it import MarkdownIt

root = Path(__file__).resolve().parents[1]
renderer = MarkdownIt("commonmark", {"html": False}).enable("table")
for name in ("README", "tasks", "PRD-大工桌面课表", "SSD-软件系统设计"):
    page = root / f"{name}.html"
    prefix = page.read_text(encoding="utf-8").split("<main>", 1)[0] + "<main>"
    body = renderer.render((root / f"{name}.md").read_text(encoding="utf-8"))
    body = re.sub(
        r'href="([^"#?]+)\.md"',
        lambda match: 'href="' + match[1] + (".html" if (root / (match[1] + ".html")).exists() else ".md") + '"',
        body,
    )
    output = prefix + body + '<p class="foot">实施进度 · G1/G2/G3 未完成完整验收</p></main></body></html>'
    page.write_text(output, encoding="utf-8")
    if name == "README":
        (root / "index.html").write_text(output, encoding="utf-8")
print("Updated README/tasks/PRD/SSD reading copies; source task statuses unchanged.")
