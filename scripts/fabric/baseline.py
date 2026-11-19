"""Reproduce the Fabric obligation register without reading external originals."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
BASELINE = ROOT / "docs" / "fabric" / "baseline"
NS = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
REL = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"
COUNTS = dict(zip("ABCDEFGHIJKL", [10, 12, 15, 7, 12, 16, 13, 5, 8, 6, 7, 7]))
ORIGINAL_IDS = {f"{module}-{i:02}" for module, count in COUNTS.items() for i in range(1, count + 1)}
ADDITION_IDS = {f"X-{i:02}" for i in range(1, 46)}


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def workbook_rows(path):
    with zipfile.ZipFile(path) as archive:
        strings = []
        if "xl/sharedStrings.xml" in archive.namelist():
            strings = [
                "".join(node.itertext())
                for node in ET.fromstring(archive.read("xl/sharedStrings.xml")).findall("s:si", NS)
            ]
        relationships = {
            node.attrib["Id"]: node.attrib["Target"]
            for node in ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))
        }
        result = {}
        for sheet in ET.fromstring(archive.read("xl/workbook.xml")).findall("s:sheets/s:sheet", NS):
            target = relationships[sheet.attrib[REL]]
            target = target.lstrip("/") if target.startswith("/") else "xl/" + target
            rows = []
            for row in ET.fromstring(archive.read(target)).findall("s:sheetData/s:row", NS):
                cells = {}
                for cell in row.findall("s:c", NS):
                    require(cell.find("s:f", NS) is None, "Approved baseline unexpectedly contains a formula.")
                    value, inline = cell.find("s:v", NS), cell.find("s:is", NS)
                    text = "".join(inline.itertext()) if inline is not None else (
                        value.text or "" if value is not None else ""
                    )
                    if cell.attrib.get("t") == "s":
                        text = strings[int(text)]
                    require(cell.attrib.get("t") != "e", f"Excel error at {sheet.attrib['name']}:{cell.attrib['r']}")
                    cells[re.sub(r"\d+", "", cell.attrib["r"])] = text
                if cells.get("A"):
                    rows.append(cells)
            result[sheet.attrib["name"]] = rows
        return result


def verify_sources(root):
    identity = load(root / "fabric" / "delivery.json")
    require(identity["deliveryId"] == "cohors-fabric", "Wrong delivery identity.")
    require(identity["forkCommit"] == "445efc75c0b79dcfa9f23489593df0ddb8be02dc", "Unapproved fork.")
    for reference in identity["approvedReferences"] + [identity["canonicalTerminologySource"]]:
        path = (root / reference["path"]).resolve()
        require(path.is_relative_to(root.resolve()), "A bound source escapes this checkout.")
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        require(digest == reference["sha256"], f"Bound source hash mismatch: {reference['path']}")
    verify_repository_source_bindings(root)


def verify_repository_source_bindings(root):
    bindings = load(root / "docs" / "fabric" / "repository-source-bindings.json")["bindings"]
    expected = {
        "source/Konzept_PMS-System_Klasse-III-Implantate_MDR.docx",
        "source/PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx",
    }
    require(len(bindings) == 2 and {row["gitPath"] for row in bindings} == expected,
            "Repository metadata bindings must cover exactly the two approved source paths.")
    for reference in bindings:
        path = (root / reference["gitPath"]).resolve()
        require(path.is_relative_to(root.resolve()), "A repository source escapes this checkout.")
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        blob = hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data,
                            usedforsecurity=False).hexdigest()
        require(digest == reference["repositorySha256"] and blob == reference["repositoryBlob"],
                f"Repository source differs from its selected Git blob: {reference['gitPath']}")


def indexed(rows, ids):
    selected = [row for row in rows if row.get("A") in ids]
    require(len(selected) == len(ids), "Missing or duplicate workbook IDs.")
    require({row["A"] for row in selected} == ids, "Workbook ID set changed.")
    return {row["A"]: row for row in selected}


def verify_register(register):
    entries = register["requirements"]
    require(len(entries) == 163 and {row["id"] for row in entries} == ORIGINAL_IDS | ADDITION_IDS,
            "The register must contain each of the 118+45 IDs exactly once.")
    original = [row for row in entries if row["id"] in ORIGINAL_IDS]
    require(Counter(row["sourcePriority"] for row in original) == {"Muss": 104, "Soll": 14},
            "Original priority distribution changed.")
    test_ids = []
    for row in entries:
        require(row["projectBinding"] == "mandatory_in_agreed_scope", f"{row['id']}: obligation weakened.")
        require(row["sourceStatus"] in ("Offen", None), f"{row['id']}: source status changed.")
        require(row["status"] == {
            "specification": "specified",
            "implementation": "not_demonstrated_for_fabric",
            "localTest": "not_run",
            "realService": "not_run",
            "functionalAcceptance": "not_accepted",
            "operationalAcceptance": "not_accepted",
        }, f"{row['id']}: source mapping must not assert execution/acceptance.")
        for field in ("process", "roles", "data", "ui", "api", "code", "remainingBoundary", "failureRecovery"):
            require(bool(row[field]), f"{row['id']}: missing {field}.")
        for polarity in ("positive", "negative"):
            test = row["tests"][polarity]
            require(test["state"] == "specified_not_executed" and len(test["assertion"]) >= 30,
                    f"{row['id']}: missing concrete {polarity} test specification.")
            test_ids.append(test["id"])
        require(all(item["state"] == "candidate_not_fabric_evidence" for item in row["tests"]["inheritedCandidates"]),
                f"{row['id']}: inherited proof is not Fabric proof.")
    require(len(test_ids) == len(set(test_ids)) == 326, "Each requirement needs two unique test specifications.")


def render(root=ROOT):
    verify_sources(root)
    baseline = root / "docs" / "fabric" / "baseline"
    manifest = load(baseline / "baseline-manifest.json")
    config = load(baseline / "baseline-config.json")
    sheets = workbook_rows(baseline / "Cohors-Fabric-Pflichtbaseline.xlsx")
    original = indexed(sheets["Originalwerte"], ORIGINAL_IDS)
    mapping = load(root / "docs" / "fabric" / "requirement-map.json")
    profiles = load(root / "docs" / "fabric" / "test-profiles.json")
    historical = {row["id"]: row for row in load(root / "docs" / "requirements" / "coordinator-mapping.json")["mapping"]}
    require(set(mapping) == ORIGINAL_IDS | ADDITION_IDS, "Mapping must cover the exact 163 IDs.")
    require(len(manifest["requirements"]) == 118, "Manifest requirement count changed.")
    require({row["id"] for row in config["additions"]} == ADDITION_IDS, "Addition IDs changed.")
    requirements = []
    source_entries = manifest["requirements"] + config["additions"]
    for source in source_entries:
        key = source["id"]
        profile_id, positive, negative = mapping[key]
        profile = profiles[profile_id]
        for path in profile["code"] + profile["ui"] + profile["testCandidates"]:
            require((root / path).is_file(), f"{key}: referenced candidate path does not exist: {path}")
        is_original = key in ORIGINAL_IDS
        if is_original:
            row = original[key]
            require(source["source_priority"] == row["I"] and source["source_status"] == row["K"],
                    f"{key}: source metadata differs from approved workbook.")
            require(source["original_system_function"] == row["F"], f"{key}: original function changed.")
        requirements.append({
            "id": key,
            "title": original[key]["C"] if is_original else source["title"],
            "requirement": source["original_system_function"] if is_original else source["requirement"],
            "originalValues": original[key] if is_original else None,
            "sourcePriority": source["source_priority"] if is_original else None,
            "sourceStatus": source["source_status"] if is_original else None,
            "projectBinding": "mandatory_in_agreed_scope",
            "scope": source["pilot_scope"] if is_original else source["scope"],
            "scopeSupplement": source.get("scope_supplement"),
            "sourceReference": source["primary_reference"] if is_original else source["source"],
            "requiredArtifact": source["required_artifact"] if is_original else source["acceptance"],
            "process": profile["process"],
            "roles": profile["roles"],
            "data": profile["data"],
            "ui": profile["ui"],
            "api": profile["api"],
            "pathStatus": "existing_entry_points_are_partial_candidates_not_feature_completion",
            "code": profile["code"],
            "tests": {
                "positive": {"id": f"FAB-{key}-P", "state": "specified_not_executed", "assertion": positive},
                "negative": {"id": f"FAB-{key}-N", "state": "specified_not_executed", "assertion": negative},
                "inheritedCandidates": [
                    {"path": path, "state": "candidate_not_fabric_evidence"} for path in profile["testCandidates"]
                ],
                "sourceScenarios": source.get("candidate_test_ids", []),
            },
            "failureRecovery": profile["failureRecovery"],
            "remainingBoundary": (
                historical[key]["remaining_and_boundary"] if is_original else source.get("note", source["acceptance"])
            ),
            "technicalGap": profile["gap"],
            "status": {
                "specification": "specified",
                "implementation": "not_demonstrated_for_fabric",
                "localTest": "not_run",
                "realService": "not_run",
                "functionalAcceptance": "not_accepted",
                "operationalAcceptance": "not_accepted",
            },
        })
    register = {
        "id": "FAB-IMPLEMENTATION-01", "version": "0.1",
        "baseline": "FAB-BASELINE-01 v0.2",
        "evidenceBoundary": "ID-specific test specifications, not executed tests or whole-requirement acceptance.",
        "requirements": requirements,
    }
    verify_register(register)
    scenario_ids = {f"T{i:02}" for i in range(1, 24)} | {f"EU-{i:02}" for i in range(1, 31)}
    scenarios = indexed(sheets["Abnahmeszenarien"], scenario_ids)
    clarifications = indexed(sheets["Pr\u00e4zisierungen"], {f"M{i:02}" for i in range(1, 10)})
    return {
        "requirement-register.json": register,
        "acceptance-scenarios.json": {
            "baseline": "FAB-BASELINE-01 v0.2",
            "scenarios": [
                {"id": key, "title": row["B"], "expectedEvidence": row["C"],
                 "source": row["D"], "state": "not_demonstrated"}
                for key, row in scenarios.items()
            ],
        },
        "scope-clarifications.json": {
            "baseline": "FAB-BASELINE-01 v0.2",
            "clarifications": [
                {"id": key, "requirements": row["B"], "scope": row["C"]}
                for key, row in clarifications.items()
            ],
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Regenerate derived JSON only; never edits source artifacts.")
    args = parser.parse_args()
    try:
        for name, value in render().items():
            path = ROOT / "docs" / "fabric" / name
            text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
            if args.write:
                path.write_bytes(text.encode("utf-8"))
            else:
                require(path.read_bytes() == text.encode("utf-8"), f"Stale generated register: {name}; run with --write.")
        print("Verified: 118 original IDs (104 Muss/14 Soll), 45 additions, 326 test specifications, 53 scenarios, 9 clarifications.")
        print("Boundary: structural/source validation only; zero functional or real-service acceptance claims.")
    except (ValueError, KeyError, OSError, zipfile.BadZipFile, ET.ParseError) as error:
        print(f"Fabric baseline validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
