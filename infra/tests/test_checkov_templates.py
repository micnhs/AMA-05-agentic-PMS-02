import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from checkov_templates import export, resource_list, templates


class CheckovTemplateTests(unittest.TestCase):
    def setUp(self):
        self.leaf = {
            "languageVersion": "2.0",
            "parameters": {"name": {"type": "string"}},
            "resources": {
                "storage": {
                    "type": "Microsoft.Storage/storageAccounts",
                    "name": "[parameters('name')]",
                    "condition": "[parameters('enabled')]",
                    "copy": {"name": "storage", "count": "[parameters('count')]"},
                    "properties": {"allowSharedKeyAccess": False, "publicNetworkAccess": "Disabled"},
                },
                "reference": {
                    "type": "Microsoft.Network/routeTables",
                    "name": "existing-route-table",
                    "existing": True,
                },
            },
        }
        self.root = {
            "languageVersion": "2.0",
            "resources": {
                "runtime": {
                    "type": "Microsoft.Resources/deployments",
                    "condition": False,
                    "properties": {"mode": "Incremental", "template": copy.deepcopy(self.leaf)},
                }
            },
        }

    def test_conversion_preserves_conditions_copies_references_and_security_values(self):
        original = copy.deepcopy(self.root)
        converted = resource_list(self.root)
        self.assertEqual(self.root, original)
        runtime = converted["resources"][0]
        self.assertIs(runtime["condition"], False)
        for actual, expected in zip(runtime["properties"]["template"]["resources"], self.leaf["resources"].values()):
            self.assertEqual(actual, expected)
        self.assertEqual(list(templates(self.root))[1][0], ("runtime",))
        self.assertEqual(resource_list(converted), converted)

    def test_exports_every_template_even_under_a_disabled_deployment(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "main.json"
            destination = Path(temporary) / "scan"
            source.write_text(json.dumps(self.root), encoding="utf-8")
            export(source, destination)
            manifest = json.loads(destination.with_suffix(".manifest.json").read_text())
            self.assertEqual(len(manifest["templates"]), 2)
            for record, (_, template) in zip(manifest["templates"], templates(self.root)):
                actual = json.loads((destination / record["file"]).read_text())
                self.assertEqual(actual, resource_list(template))
            with self.assertRaisesRegex(ValueError, "stale"):
                export(source, destination)

    def test_linked_templates_and_malformed_resources_fail_closed(self):
        del self.root["resources"]["runtime"]["properties"]["template"]
        with self.assertRaisesRegex(ValueError, "Linked"):
            list(templates(self.root))
        with self.assertRaisesRegex(ValueError, "Malformed"):
            list(templates({"resources": {"bad": {"name": "missing-type"}}}))

    def test_unrendered_function_filter_is_never_a_silent_pass(self):
        self.leaf["resources"]["storage"]["name"] = "[toLower(parameters('name'))]"
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "main.json"
            source.write_text(json.dumps(self.leaf), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "silently filter"):
                export(source, Path(temporary) / "scan")


if __name__ == "__main__":
    unittest.main()
