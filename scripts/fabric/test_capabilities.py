import copy
import json
import unittest

from capabilities import ROOT, runtime_blockers, validate


class CapabilityEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.document = json.loads((ROOT / "docs" / "fabric" / "capability-gates.json").read_text(encoding="utf-8"))
        self.delivery = json.loads((ROOT / "fabric" / "delivery.json").read_text(encoding="utf-8"))

    def test_present_evidence_is_valid_but_cannot_admit_runtime(self):
        validate(self.document)
        self.assertTrue(any("G02" in blocker for blocker in runtime_blockers(self.document, self.delivery)))
        for flag in ("adapterActivationAllowed", "productionCutoverAllowed"):
            changed = copy.deepcopy(self.document)
            changed[flag] = True
            with self.assertRaisesRegex(ValueError, "Open gates"):
                validate(changed)

    def test_build_pass_cannot_be_promoted_to_service_evidence_or_acceptance(self):
        changed = copy.deepcopy(self.document)
        gate = changed["gates"][0]
        gate["realService"] = "passed"
        gate["serviceEvidence"] = ["CI-CONTRACT-01"]
        with self.assertRaisesRegex(ValueError, "cannot become real-service"):
            validate(changed)
        gate["serviceEvidence"] = []
        with self.assertRaisesRegex(ValueError, "no service evidence"):
            validate(changed)
        gate["realService"] = "not_run"
        gate["operationalAcceptance"] = "accepted"
        with self.assertRaisesRegex(ValueError, "cannot precede"):
            validate(changed)

    def test_missing_duplicate_or_weakened_hard_gate_is_rejected(self):
        for mutate in (
            lambda gates: gates.pop(),
            lambda gates: gates.__setitem__(1, copy.deepcopy(gates[0])),
            lambda gates: gates[1].__setitem__("hardProductionGate", False)
        ):
            changed = copy.deepcopy(self.document)
            mutate(changed["gates"])
            with self.assertRaises(ValueError):
                validate(changed)


if __name__ == "__main__":
    unittest.main()
