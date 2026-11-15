import hashlib
import json
import re
from collections import Counter
from pathlib import Path

from openpyxl import load_workbook


root = Path(__file__).resolve().parent
source = root / "imdrf-2026-fda-mapping-source.xlsx"
source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
if source_hash != "c73f606d8ffbdcf715950cf342028b45a8008440255cd5467fa74315f2e42c76":
    raise ValueError("The captured FDA source has changed.")

annexes = ("A", "C", "D", "F")
book = load_workbook(source, read_only=True, data_only=False)
combined = book["Combined"]
combined_headers = [cell.value for cell in combined[4]]
current = {}
for values in combined.iter_rows(min_row=5, values_only=True):
    row = dict(zip(combined_headers, values))
    if row["Annex"] not in annexes:
        continue
    source_code = row["IMDRF Code"]
    if not isinstance(source_code, str):
        raise ValueError("Missing IMDRF identifier in the selected source annexes.")
    code = source_code.strip()
    if not re.fullmatch(r"[ACDF](?:[0-9]{2}){1,3}", code):
        raise ValueError("Unexpected IMDRF identifier in the selected source annexes.")
    if code in current:
        raise ValueError(f"Duplicate selectable code: {code}")
    current[code] = row["Term"].strip()

terms = {}
for annex in annexes:
    sheet = book[annex]
    if sheet["A3"].value != "Release Number: 2026":
        raise ValueError(f"Unexpected release in Annex {annex}.")
    headers = [cell.value for cell in sheet[8]]
    if not {"IMDRF Code", "CodeHierarchy", "Status"}.issubset(headers):
        raise ValueError(f"Unexpected source columns in Annex {annex}.")
    level_headers = [name for name in headers if isinstance(name, str) and name.startswith("Level ")]
    for row_number, values in enumerate(sheet.iter_rows(min_row=9, values_only=True), 9):
        row = dict(zip(headers, values))
        source_code = row["IMDRF Code"]
        if source_code is None:
            if any(row[name] is not None for name in level_headers):
                raise ValueError(f"Term without a code in Annex {annex}, row {row_number}.")
            continue
        if not isinstance(source_code, str):
            raise ValueError(f"Non-text identifier in Annex {annex}, row {row_number}.")
        code = source_code.strip()
        if not re.fullmatch(annex + r"(?:[0-9]{2}){1,3}", code):
            raise ValueError(f"Unexpected identifier in Annex {annex}, row {row_number}.")
        populated = [(level + 1, row[name]) for level, name in enumerate(level_headers) if row[name]]
        if len(populated) != 1 or code in terms:
            raise ValueError(f"Ambiguous code or hierarchy term: {code}")
        level, term = populated[0]
        hierarchy = [item.strip() for item in row["CodeHierarchy"].split("|")]
        if hierarchy[-1] != code or any(not re.fullmatch(annex + r"(?:[0-9]{2}){1,3}", item) for item in hierarchy):
            raise ValueError(f"Invalid source hierarchy: {code}")
        hierarchy_issue = None if len(hierarchy) == level else (
            f"Source term is in Level {level} column but CodeHierarchy contains {len(hierarchy)} nodes."
        )
        if code in current and current[code] != term.strip():
            raise ValueError(f"Selectable and hierarchy terms disagree: {code}")
        terms[code] = {
            "code": code,
            "annex": annex,
            "term": term.strip(),
            "level": level if hierarchy_issue is None else None,
            "sourceLevelColumn": level,
            "hierarchy": hierarchy,
            "hierarchyIssue": hierarchy_issue,
            "automaticAggregationEligible": hierarchy_issue is None,
            "currentSelectable": code in current,
            "sourceStatus": row["Status"],
            "sourceStatusDescription": row["Status Description"],
            "sourceSheet": annex,
            "sourceRow": row_number,
            "sourceCode": source_code,
        }
book.close()

if not current.keys() <= terms.keys():
    raise ValueError("Selectable terms are missing from the source hierarchy.")
for term in terms.values():
    if not set(term["hierarchy"]) <= terms.keys():
        raise ValueError(f"Missing ancestor for {term['code']}.")
counts = dict(Counter(code[0] for code in current))
if counts != {"A": 487, "C": 160, "D": 43, "F": 81}:
    raise ValueError(f"Unexpected selectable-code counts: {counts}")
if terms["F02"]["term"] != "Death" or "D1401" in current or "D1402" in current:
    raise ValueError("The independently inspected death/retired-code boundaries changed.")

result = {
    "referenceVersion": "1",
    "releaseId": "fda-imdrf-mapping-2026-08-17",
    "purpose": "Independent terminology source/reference for the synthetic Cohors PSUR pilot, not a medical coding decision.",
    "source": {
        "publisher": "U.S. Food and Drug Administration",
        "page": "https://www.fda.gov/medical-devices/mdr-adverse-event-codes/coding-resources-medical-device-reports",
        "download": "https://www.fda.gov/media/192166/download?attachment",
        "sourceFilename": source.name,
        "sha256": source_hash,
        "bytes": source.stat().st_size,
        "pageMaintenanceDate": "2026-08-17",
        "pageContentCurrentAsOf": "2026-08-20",
        "workbookRelease": "2026",
        "verifiedOn": "2026-11-15",
        "scope": "FDA-published mapping of IMDRF terminology. Not asserted to be a byte-identical native IMDRF release; the native IMDRF site timed out during verification.",
    },
    "omittedColumns": ["FDA Code", "NCIt Code", "Other Code", "Non-IMDRF Code", "Non-IMDRF Code/Term", "Definition"],
    "normalization": "Outer whitespace is trimmed from codes, term labels and hierarchy nodes; original source bytes and each hierarchy sourceCode are preserved. The source Combined sheet contains F30 with trailing whitespace.",
    "limitations": [
        "Use only IMDRF identifiers from the named release, not FDA, NCIt or MedDRA identifiers.",
        "The Combined sheet is the source of current selectability. Historic/retired entries remain recorded in the hierarchy; they are not silently made selectable or recoded.",
        "PSUR Level-2 grouping does not replace the originally recorded case code; Level 1 is used when no Level 2 is available, as distinguished in MDCG 2022-21.",
        "C1901 and C1902 have inconsistent source level-column/hierarchy metadata. Their current code membership is preserved, but automatic aggregation is blocked until that metadata is authoritatively resolved. Do not silently infer or repair their level.",
        "MIR XSD validity alone neither proves terminology membership nor justifies a clinical code choice.",
        "A combined death-or-deterioration seriousness category must not automatically create a Death/F02 code.",
        "Clinical applicability, coding rationale and any change of release remain human-reviewed; this source import does not grant approval.",
    ],
    "currentSelectableCounts": counts,
    "hierarchyIssues": [
        {"code": term["code"], "issue": term["hierarchyIssue"], "sourceRow": term["sourceRow"]}
        for term in terms.values() if term["hierarchyIssue"] is not None
    ],
    "referenceChecks": {
        "deathCode": "F02",
        "deathTerm": "Death",
        "notDeathCode": "F01",
        "retiredNotSelectable": ["D1401", "D1402"],
        "newCurrentCodes": ["C1901", "C1902", "F0104"],
        "level2Group": {"caseCodes": ["A010101", "A010102"], "groupCode": "A0101"},
        "unknownCode": "A999999",
    },
    "terms": [terms[code] for code in sorted(terms)],
}
output = root / "cohors-terminology-reference.json"
output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(json.dumps({
    "terms": len(terms),
    "currentSelectableCounts": counts,
    "sourceSha256": source_hash,
    "referenceSha256": hashlib.sha256(output.read_bytes()).hexdigest(),
    "nonSelectable": [term["code"] for term in terms.values() if not term["currentSelectable"]],
    "hierarchyIssues": result["hierarchyIssues"],
}, indent=2))
