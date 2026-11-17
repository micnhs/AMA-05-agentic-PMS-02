import copy
import unittest

import baseline


class BaselineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.outputs = baseline.render()

    def test_complete_reproducible_original_and_additional_scope(self):
        register = self.outputs["requirement-register.json"]
        baseline.verify_register(register)
        self.assertEqual(53, len(self.outputs["acceptance-scenarios.json"]["scenarios"]))
        self.assertEqual(9, len(self.outputs["scope-clarifications.json"]["clarifications"]))
        entries = {row["id"]: row for row in register["requirements"]}
        for key in ("X-12", "X-15"):
            self.assertEqual("not_demonstrated_for_fabric", entries[key]["status"]["implementation"])
        self.assertIn("Soll", {entries[f"F-{n:02}"]["sourcePriority"] for n in range(8, 17)})

    def test_missing_duplicate_or_reclassified_requirement_is_rejected(self):
        original = self.outputs["requirement-register.json"]
        for mutation in ("missing", "duplicate", "priority", "binding"):
            with self.subTest(mutation=mutation):
                register = copy.deepcopy(original)
                if mutation == "missing":
                    register["requirements"].pop()
                elif mutation == "duplicate":
                    register["requirements"][-1] = register["requirements"][0]
                elif mutation == "priority":
                    register["requirements"][0]["sourcePriority"] = "Soll"
                else:
                    register["requirements"][0]["projectBinding"] = "optional"
                with self.assertRaises(ValueError):
                    baseline.verify_register(register)

    def test_mapping_cannot_promote_historical_or_local_proof_to_service_acceptance(self):
        original = self.outputs["requirement-register.json"]
        for field in ("implementation", "localTest", "realService", "functionalAcceptance", "operationalAcceptance"):
            with self.subTest(field=field):
                register = copy.deepcopy(original)
                register["requirements"][0]["status"][field] = "passed"
                with self.assertRaises(ValueError):
                    baseline.verify_register(register)
        register = copy.deepcopy(original)
        register["requirements"][0]["tests"]["inheritedCandidates"][0]["state"] = "passed"
        with self.assertRaises(ValueError):
            baseline.verify_register(register)

    def test_each_id_requires_distinct_positive_negative_assertions_and_paths(self):
        for mutation in ("negative", "duplicate-test", "ui", "boundary"):
            with self.subTest(mutation=mutation):
                register = copy.deepcopy(self.outputs["requirement-register.json"])
                entry = register["requirements"][0]
                if mutation == "negative":
                    entry["tests"]["negative"]["assertion"] = ""
                elif mutation == "duplicate-test":
                    entry["tests"]["negative"]["id"] = entry["tests"]["positive"]["id"]
                elif mutation == "ui":
                    entry["ui"] = []
                else:
                    entry["remainingBoundary"] = ""
                with self.assertRaises(ValueError):
                    baseline.verify_register(register)


if __name__ == "__main__":
    unittest.main()
