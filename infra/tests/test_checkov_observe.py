from enum import Enum
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from checkov_observe import Observation


class Result(str, Enum):
    UNKNOWN = "UNKNOWN"


class CheckovObservationTests(unittest.TestCase):
    def test_results_and_exceptions_are_never_changed_or_suppressed(self):
        observation = Observation()
        check = SimpleNamespace(id="CKV_TEST_1")
        for outcome in ("PASSED", "FAILED", "SKIPPED", Result.UNKNOWN, None):
            expected = {"result": outcome, "evaluated_keys": ["properties/x"]}
            wrapped = observation.wrap(lambda *args: expected)
            self.assertIs(wrapped(check, "scan.json", {}, "test", "resource", None), expected)
        error = TypeError("synthetic original check failure")

        def fail(*args):
            raise error

        with self.assertRaises(TypeError) as caught:
            observation.wrap(fail)(check, "scan.json", {}, "test", "resource", None)
        self.assertIs(caught.exception, error)
        report = observation.report()
        self.assertEqual(report["executed"], 6)
        self.assertEqual(report["counts"]["UNKNOWN"], 1)
        self.assertEqual(report["counts"]["EXECUTION_ERROR"], 1)
        self.assertFalse(report["completeWithoutUnknownSkippedOrErrors"])

    def test_no_observed_checks_is_not_a_pass(self):
        observation = Observation()
        self.assertFalse(observation.report()["completeWithoutUnknownSkippedOrErrors"])
        observation.wrap(lambda *args: {"result": "PASSED"})(SimpleNamespace(id="CKV_TEST_1"), "scan", {}, "r", "t", None)
        self.assertTrue(observation.report()["completeWithoutUnknownSkippedOrErrors"])


if __name__ == "__main__":
    unittest.main()
