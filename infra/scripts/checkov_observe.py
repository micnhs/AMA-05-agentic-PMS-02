"""Observe Checkov's unchanged Python check results, including UNKNOWN/errors."""

from collections import Counter
from functools import wraps
import importlib.metadata
import json
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
                "completeWithoutUnknownSkippedOrErrors": bool(self.records) and not unassessed}


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
    raise SystemExit(main())
