"""Observe unchanged Checkov results and assess the source-bound network deferral."""

import argparse
from collections import Counter
from functools import wraps
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys
import threading


MARKER = "COHORS_CHECKOV_EXECUTION_OBSERVATION:"


class Observation:
    def __init__(self):
        self.records = []
        self.lock = threading.Lock()

    def wrap(self, original):
        @wraps(original)
        def observed(check, scanned_file, entity_configuration, entity_name, entity_type, skip_info):
            record = {"checkId": check.id, "file": scanned_file,
                      "entity": entity_name, "entityType": entity_type}
            try:
                result = original(check, scanned_file, entity_configuration, entity_name, entity_type, skip_info)
                outcome = result.get("result")
                record["result"] = getattr(outcome, "value", outcome)
                return result
            except Exception as error:
                record.update(result="EXECUTION_ERROR", errorType=type(error).__name__)
                raise
            finally:
                with self.lock:
                    self.records.append(record)
        return observed

    def report(self):
        counts = Counter(record["result"] for record in self.records)
        unassessed = [record for record in self.records if record["result"] not in ("PASSED", "FAILED")]
        return {"contract": "cohors-checkov-python-execution-1", "checkovVersion": "3.4.5",
                "executed": len(self.records), "counts": dict(counts), "unassessed": unassessed,
                "completeWithoutUnknownSkippedOrErrors": bool(self.records) and not unassessed,
                "records": list(self.records)}


def assess(template, report, observation, manifest, source_bytes, review, exit_code, has_diagnostics):
    from checkov_templates import digest, templates

    if not all(isinstance(value, dict) for value in (report, observation, manifest, review)):
        raise ValueError("Security assessment requires the original report, execution, manifest and review objects.")
    if hashlib.sha256(source_bytes).hexdigest() != manifest.get("sourceSha256"):
        raise ValueError("The scan-input manifest does not bind these original compiled bytes.")
    source = json.loads(source_bytes)
    originals = dict(templates(source))
    roots = source["resources"]
    roots = {str(symbol): resource for symbol, resource in
             (roots.items() if isinstance(roots, dict) else enumerate(roots))}
    if (template not in ("main", "primary-network", "primary")
            or review.get("scope") != "shared-private-network"
            or review.get("disposition") not in ("required", "deferred")):
        raise ValueError("The security review must name the exact shared private-network scope and disposition.")
    records = observation.get("records")
    if not isinstance(records, list) or not records or any(
        not isinstance(record, dict)
        or not all(isinstance(record.get(key), str) and record[key] for key in ("checkId", "file", "entityType"))
        or "result" not in record
        or record.get("result") not in ("PASSED", "FAILED", "SKIPPED", "UNKNOWN", "EXECUTION_ERROR", None)
        for record in records
    ):
        raise ValueError("Scoped assessment requires original per-execution records; historic totals cannot be reclassified.")
    if not isinstance(manifest.get("templates"), list):
        raise ValueError("A complete source-bound scan-input manifest is required.")
    views, view_types = {}, {}
    for view in manifest["templates"]:
        if not isinstance(view, dict):
            raise ValueError("Malformed scan-input view; no scoped security deferral.")
        name = view.get("file")
        if (not isinstance(name, str) or not name or "/" in name or "\\" in name or name in views
                or not isinstance(view.get("templatePath"), list)
                or not all(isinstance(symbol, str) for symbol in view["templatePath"])
                or not isinstance(view.get("resources"), list)
                or not all(isinstance(resource, dict) and isinstance(resource.get("type"), str)
                           for resource in view["resources"])):
            raise ValueError("Ambiguous scan-input manifest; no scoped security deferral.")
        original = originals.get(tuple(view["templatePath"]))
        if original is None:
            raise ValueError("The scan view has no original template path.")
        resources = original["resources"]
        expected = [{"symbol": str(symbol), "type": resource["type"], "existing": resource.get("existing", False),
                     "sourceSha256": digest(resource)}
                    for symbol, resource in (resources.items() if isinstance(resources, dict) else enumerate(resources))]
        if view["resources"] != expected:
            raise ValueError("The scan view does not preserve the original resource bindings.")
        views[name] = view
        prefix = tuple(view["templatePath"])
        view_types[name] = {
            resource["type"].lower()
            for path, child in originals.items() if path[:len(prefix)] == prefix
            for resource in (child["resources"].values() if isinstance(child["resources"], dict) else child["resources"])
        }
    if (not views or manifest.get("sourceTemplateCount") != len(originals) or manifest.get("scanVariantCount") != len(views)
            or {tuple(view["templatePath"]) for view in views.values()} != set(originals)):
        raise ValueError("A complete source-bound scan-input manifest is required.")

    def counts(items):
        return json.loads(json.dumps(dict(Counter(item.get("result") for item in items))))

    required, deferred = [], []
    for record in records:
        filename = record["file"].replace("\\", "/").rsplit("/", 1)[-1]
        view = views.get(filename, {})
        kind = record["entityType"].lower()
        path = view.get("templatePath", [])
        deployment = roots.get(path[0], {}) if path else {}
        network = (review["disposition"] == "deferred" and template == "primary-network"
                   and deployment.get("type", "").lower() == "microsoft.resources/deployments"
                   and deployment.get("condition") in (
                       "[equals(parameters('topology'), 'shared-hub-v1')]",
                       "[equals(parameters('topology'), 'shared-primary-v3')]")
                   and kind.startswith("microsoft.network/")
                   and kind in view_types.get(filename, set()))
        (deferred if network else required).append(record)

    summary = report.get("summary", {})
    summary_fields = ("passed", "failed", "skipped", "parsing_errors", "resource_count")
    if not isinstance(summary, dict) or any(type(summary.get(key)) is not int or summary[key] < 0 for key in summary_fields):
        raise ValueError("Malformed original scanner summary; no security qualification.")
    actual_counts = counts(records)
    complete = all(record.get("result") in ("PASSED", "FAILED") for record in records)
    integrity = []
    if (observation.get("contract") != "cohors-checkov-python-execution-1"
            or observation.get("checkovVersion") != "3.4.5"
            or type(observation.get("executed")) is not int
            or observation.get("executed") != len(records) or observation.get("counts") != actual_counts
            or not isinstance(observation.get("counts"), dict)
            or any(type(value) is not int or value < 0 for value in observation["counts"].values())
            or observation.get("completeWithoutUnknownSkippedOrErrors") is not complete
            or observation.get("unassessed") != [record for record in records if record.get("result") not in ("PASSED", "FAILED")]):
        integrity.append("Original execution observation is incomplete or inconsistent.")
    if any(summary[key] != actual_counts.get(outcome, 0)
           for key, outcome in (("passed", "PASSED"), ("failed", "FAILED"), ("skipped", "SKIPPED"))):
        integrity.append("Original report totals do not reconcile with the observed Python executions.")
    if type(exit_code) is not int or exit_code != (1 if summary["failed"] else 0):
        integrity.append("Scanner exit is not the expected original findings/success exit.")
    if summary["parsing_errors"] or not summary["resource_count"] or has_diagnostics:
        integrity.append("Parsing errors, empty coverage or unclassified scanner diagnostics remain blocking.")
    raw_passed = (not integrity and exit_code == 0 and summary["failed"] == 0
                  and summary["skipped"] == 0 and summary["passed"] > 0 and complete)
    allowed = (not integrity and all(record.get("result") == "PASSED" for record in required)
               and (bool(deferred) or summary["passed"] > 0))
    return {
        "contract": "cohors-scoped-security-assessment-1",
        "passed": raw_passed, "requiredChecksPassed": allowed,
        "review": review, "integrityErrors": integrity,
        "scopeRule": {"compiledRoot": "primary-network", "topologies": ["shared-hub-v1", "shared-primary-v3"],
                      "provider": "Microsoft.Network", "otherResources": "required"},
        "required": {"executed": len(required), "counts": counts(required)},
        "deferred": {"executed": len(deferred), "counts": counts(deferred), "records": deferred},
    }


def assess_main(arguments):
    parser = argparse.ArgumentParser(description="Assess original scanner output without changing or rerunning it.")
    parser.add_argument("template", choices=("main", "primary-network", "primary"))
    parser.add_argument("report", type=Path)
    parser.add_argument("observation", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("exit_code", type=int)
    parser.add_argument("has_diagnostics", choices=("true", "false"))
    options = parser.parse_args(arguments)
    plan = json.loads((Path(__file__).resolve().parents[2] / ".azure" / "infrastructure-plan.json").read_bytes())
    review = plan["plan"]["sharedHubCandidate"]["securityReview"]
    manifest = json.loads(options.manifest.read_bytes())
    original = manifest.get("originalFile")
    if not isinstance(original, str) or not original or "/" in original or "\\" in original:
        raise ValueError("The original compiled source must be the manifest's sibling file.")
    result = assess(options.template, json.loads(options.report.read_bytes()), json.loads(options.observation.read_bytes()),
                    manifest, options.manifest.with_name(original).read_bytes(), review,
                    options.exit_code, options.has_diagnostics == "true")
    print(json.dumps(result, separators=(",", ":"), ensure_ascii=True))
    return 0


def main():
    if importlib.metadata.version("checkov") != "3.4.5":
        raise RuntimeError("The observer is bound to the reviewed Checkov 3.4.5 implementation.")
    from checkov.common.checks.base_check import BaseCheck
    from checkov.main import Checkov

    observation = Observation()
    original = BaseCheck.run
    BaseCheck.run = observation.wrap(original)
    try:
        return Checkov().run()
    finally:
        BaseCheck.run = original
        print(MARKER + json.dumps(observation.report(), separators=(",", ":")), file=sys.stderr)


if __name__ == "__main__":
    raise SystemExit(assess_main(sys.argv[2:]) if sys.argv[1:2] == ["--assess"] else main())
