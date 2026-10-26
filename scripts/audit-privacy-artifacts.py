"""Inspect only owned fixtures and an explicit release ZIP; never scan personal profiles."""
import argparse
import json
import re
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--release", type=Path)
args = parser.parse_args()
checks = []
shape_count = 0
sensitive = {"cookie", "authorization", "password", "accesstoken", "refreshtoken", "studentno", "studentnumber", "studentcode"}
placeholders = {"<string>", "<number>", "<boolean>", "<bounded>"}


def check(name, passed):
    checks.append({"name": name, "passed": bool(passed)})
    if not passed:
        raise ValueError("Privacy artifact check failed: " + name)


def scan(value, shape=False, field=""):
    if isinstance(value, dict):
        for key, child in value.items():
            check("no credential fields", key.lower().replace("_", "") not in sensitive)
            if key.lower() == "shapejson":
                scan(json.loads(child), True)
            else:
                scan(child, shape, key)
    elif isinstance(value, list):
        for child in value:
            scan(child, shape, field)
    elif isinstance(value, str):
        check("no raw student-number-shaped strings", re.fullmatch(r"\d{8,16}", value) is None)
        check("no bearer/JWT credentials", re.search(r"Bearer\s+[A-Za-z0-9._-]{12,}|\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}\.", value) is None)
        if shape:
            timing = field == "date" and re.fullmatch(r"20\d\d-\d\d-\d\d", value)
            weeks = field.lower() in {"weeks", "zcd", "skzc", "skzcd", "kczc", "jcs", "jcd", "周次", "节次"} and re.fullmatch(r"[0-9 ,，;；\-~至单双周节()（）]{1,96}", value)
            check("shape strings are placeholders or calendar evidence", value in placeholders or timing or weeks)
        if field.lower() == "route":
            check("routes omit query values", value.startswith("/") and "?" not in value and "#" not in value)
    elif isinstance(value, (int, float)) and not isinstance(value, bool):
        check("no identifier-sized numeric evidence", abs(value) <= 5 * 1024 * 1024)


fixtures = sorted((root / "fixtures/dlut").glob("*.json"))
for path in fixtures:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if path.name == "synthetic-term.json":
        check("demo marked synthetic", value.get("synthetic") is True)
        account=value.get("accountId", "")
        check("demo account is not email or student-number-shaped", "@" not in account and re.search(r"\d{8,16}", account) is None)
    scan(value)
    if isinstance(value, dict):
        shape_count += len(value.get("responses", value.get("Responses", [])))

release = args.release
if release is None:
    release = Path(json.loads((root / "docs/validation/current-release.json").read_text(encoding="utf-8-sig"))["archive"])
with zipfile.ZipFile(release) as bundle:
    names = bundle.namelist()
    for name in names:
        check("package excludes personal/runtime/probe artifacts", not re.search(r"(?:^|/)(?:auth-profile|profiles|logs|artifacts|spikes|tests)(?:/|$)|(?:^|/)(?:timetable\.db|settings\.json)|\.log(?:\.|$)|ManagedFault|\.Spike", name, re.I))
    demos = [name for name in names if name.endswith("fixtures/synthetic-term.json")]
    check("one explicit packaged demo", len(demos) == 1)
    value = json.loads(bundle.read(demos[0]))
    check("packaged demo marked synthetic", value.get("synthetic") is True)
    scan(value)

report = {"fixtureFiles": len(fixtures), "observedResponseShapes": shape_count,
          "releaseArchive": release.name, "releaseEntries": len(names), "passed": True,
          "checksPerformed": len(checks),
          "scope": "Structural and credential-pattern checks of owned fixtures and explicit ZIP paths/demo; not a scan of personal files or a proof against all possible secret formats."}
(root / "docs/validation/privacy-artifacts.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, ensure_ascii=False))
