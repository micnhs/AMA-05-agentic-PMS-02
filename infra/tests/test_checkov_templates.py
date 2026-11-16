import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from checkov_templates import digest, export, resource_list, scan_copy, templates, verify_projection


class CheckovTemplateTests(unittest.TestCase):
    def setUp(self):
        self.leaf = {
            "languageVersion": "2.0",
            "parameters": {"name": {"type": "string"}, "enabled": {"type": "bool"}, "count": {"type": "int"}},
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
                    "properties": {
                        "mode": "Incremental",
                        "expressionEvaluationOptions": {"scope": "inner"},
                        "parameters": {"name": {"value": "syntheticstorage01"}},
                        "template": copy.deepcopy(self.leaf),
                    },
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
            source_bytes = source.read_bytes()
            export(source, destination)
            manifest = json.loads(destination.with_suffix(".manifest.json").read_text())
            expected = copy.deepcopy(self.root)
            expected["resources"]["runtime"]["properties"]["template"]["resources"]["storage"]["name"] = "syntheticstorage01"
            self.assertEqual(len(manifest["templates"]), 2)
            self.assertEqual(manifest["sourceSha256"], hashlib.sha256(source_bytes).hexdigest())
            self.assertEqual(source.read_bytes(), source_bytes)
            self.assertEqual((destination.parent / manifest["originalFile"]).read_bytes(), source_bytes)
            for record, (_, template), (_, original) in zip(manifest["templates"], templates(expected), templates(self.root)):
                actual = json.loads((destination / record["file"]).read_text())
                self.assertEqual(actual, resource_list(template))
                self.assertEqual(record["scanSha256"], hashlib.sha256((destination / record["file"]).read_bytes()).hexdigest())
                self.assertEqual([r["sourceSha256"] for r in record["resources"]],
                                 [digest(r) for r in original["resources"].values()])
            with self.assertRaisesRegex(ValueError, "stale"):
                export(source, destination)
            for suffix in (".manifest.json", ".original.json"):
                stale_destination = Path(temporary) / ("stale" + suffix.replace(".", "-"))
                artifact = stale_destination.with_suffix(suffix)
                artifact.write_text("keep existing evidence", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "stale"):
                    export(source, stale_destination)
                self.assertEqual(artifact.read_text(), "keep existing evidence")
                self.assertFalse(stale_destination.exists())

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

    def sql_template(self):
        return {
            "parameters": {"name": {"type": "string"}},
            "resources": {
                "server": {"type": "Microsoft.Sql/servers", "name": "[parameters('name')]",
                           "properties": {"publicNetworkAccess": "Disabled", "minimalTlsVersion": "1.2"}},
                "audit": {"type": "Microsoft.Sql/servers/auditingSettings", "apiVersion": "2023-08-01",
                          "name": "[format('{0}/{1}', parameters('name'), 'default')]",
                          "dependsOn": ["server"],
                          "properties": {"state": "Enabled", "isAzureMonitorTargetEnabled": True, "retentionDays": 0}},
                "database": {"type": "Microsoft.Sql/servers/databases", "name": "[format('{0}/db', parameters('name'))]"},
                "threat": {"type": "Microsoft.Sql/servers/securityAlertPolicies", "name": "sql/Default",
                           "condition": False, "properties": {"state": "Disabled", "emailAccountAdmins": False}},
            },
        }

    def test_sql_projection_keeps_exact_policy_and_does_not_invent_database_inheritance(self):
        source = self.sql_template()
        original = copy.deepcopy(source)
        converted, changes = scan_copy(source)
        self.assertEqual(source, original)
        server, audit, database, threat = converted["resources"]
        self.assertEqual(server["resources"], [original["resources"]["audit"]])
        self.assertEqual(audit, original["resources"]["audit"])
        self.assertNotIn("resources", database)
        self.assertEqual(threat, original["resources"]["threat"])
        self.assertEqual(len(changes), 1)
        self.assertEqual(changes[0]["resourceSha256"], digest(original["resources"]["audit"]))
        verify_projection(source, converted, changes)

    def test_missing_disabled_or_other_server_audit_never_protects_the_wrong_server(self):
        for properties in ({}, {"state": "Disabled"}, {"retentionDays": 0}):
            with self.subTest(properties=properties):
                source = self.sql_template()
                source["resources"]["audit"]["properties"] = properties
                converted, _ = scan_copy(source)
                self.assertEqual(converted["resources"][0]["resources"][0]["properties"], properties)
                self.assertNotEqual(converted["resources"][0]["resources"][0]["properties"].get("state"), "Enabled")
        source = self.sql_template()
        del source["resources"]["audit"]
        converted, changes = scan_copy(source)
        self.assertNotIn("resources", converted["resources"][0])
        self.assertEqual(changes, [])
        source["resources"]["other"] = {"type": "Microsoft.Sql/servers", "name": "other"}
        source["resources"]["audit"] = {"type": "Microsoft.Sql/servers/auditingSettings", "name": "other/default",
                                        "properties": {"state": "Enabled"}}
        converted, _ = scan_copy(source)
        self.assertNotIn("resources", converted["resources"][0])
        self.assertEqual(converted["resources"][3]["resources"], [source["resources"]["audit"]])

    def test_ambiguous_or_uncovered_sql_relationships_fail_closed(self):
        mutations = [
            lambda r: r["audit"].update(name="other/default"),
            lambda r: r.update(other=copy.deepcopy(r["server"])),
            lambda r: r.update(anotherAudit=copy.deepcopy(r["audit"])),
            lambda r: r["server"].update(copy={"count": 2}),
            lambda r: r["audit"].update(copy={"count": 2}),
            lambda r: r["audit"].update(scope="another-scope"),
            lambda r: r["server"].update(existing=True),
            lambda r: r["audit"].update(condition=False),
            lambda r: r["audit"].update(condition="[parameters('enabled')]"),
            lambda r: r["server"].update(resources=[{"type": "auditingSettings", "properties": {"state": "Disabled"}}]),
            lambda r: r["audit"].update(name="[format('{1}/{0}', parameters('name'), 'default')]"),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutations.index(mutate)):
                source = self.sql_template()
                mutate(source["resources"])
                original = copy.deepcopy(source)
                with self.assertRaises(ValueError):
                    scan_copy(source)
                self.assertEqual(source, original)

    def test_matching_sql_conditions_remain_on_both_resources_in_list_templates(self):
        source = self.sql_template()
        source["resources"]["server"]["condition"] = "[parameters('enabled')]"
        source["resources"]["audit"]["condition"] = "[parameters('enabled')]"
        original = resource_list(source)
        converted, changes = scan_copy(original)
        self.assertEqual(converted["resources"][0]["condition"], "[parameters('enabled')]")
        self.assertEqual(converted["resources"][0]["resources"][0], original["resources"][1])
        verify_projection(original, converted, changes)

    def test_storage_names_follow_closed_module_bindings_not_defaults(self):
        root = copy.deepcopy(self.root)
        root["variables"] = {"prefix": "st", "suffix": "synthetic"}
        properties = root["resources"]["runtime"]["properties"]
        properties["parameters"]["name"]["value"] = "[format('{0}{1}', variables('prefix'), variables('suffix'))]"
        leaf = properties["template"]
        leaf["parameters"]["name"]["defaultValue"] = "wrongdefault"
        leaf["variables"] = {"actual": "[format('{0}ev', parameters('name'))]"}
        leaf["resources"]["storage"]["name"] = "[variables('actual')]"
        converted, changes = scan_copy(root)
        account = converted["resources"][0]["properties"]["template"]["resources"][0]
        self.assertEqual(account["name"], "stsyntheticev")
        self.assertEqual(account["condition"], leaf["resources"]["storage"]["condition"])
        self.assertEqual(account["copy"], leaf["resources"]["storage"]["copy"])
        self.assertEqual(changes[0]["templatePath"], ["runtime"])
        self.assertEqual(changes[0]["originalName"], "[variables('actual')]")
        self.assertEqual(changes[0]["scanName"], "stsyntheticev")
        omitted = copy.deepcopy(root)
        del omitted["resources"]["runtime"]["properties"]["parameters"]["name"]
        converted, _ = scan_copy(omitted)
        self.assertEqual(converted["resources"][0]["properties"]["template"]["resources"][0]["name"], "wrongdefaultev")
        for invalid in ("UPPERCASE", "bad-name", "ab", "a" * 25,
                        "[parameters('name')].unexpected", "[[literal]tail"):
            with self.subTest(invalid=invalid):
                properties["parameters"]["name"]["value"] = invalid
                leaf["resources"]["storage"]["name"] = "[parameters('name')]"
                converted, _ = scan_copy(root)
                name = converted["resources"][0]["properties"]["template"]["resources"][0]["name"]
                self.assertEqual(name, invalid)
                self.assertNotRegex(name, r"^[a-z0-9]{3,24}$")

    def test_unbound_scope_dependent_ambiguous_and_unsupported_names_are_errors(self):
        cases = []
        root = copy.deepcopy(self.leaf)
        root["parameters"]["name"]["defaultValue"] = "gooddefault"
        cases.append(root)
        item = copy.deepcopy(root)
        item["parameters"]["name"]["allowedValues"] = ["gooddefault"]
        cases.append(item)
        for expression in ("[uniqueString(resourceGroup().id)]", "[variables('missing')]",
                           "[concat('st', 'account')]", "[format('{0:x}', 'account')]",
                           "[format('{1}', 'account')]", "[format('[{0}]', 'name')]",
                           "[[literal]", "[format('{0}', 'account') + 'extra']"):
            item = copy.deepcopy(self.leaf)
            item["resources"]["storage"]["name"] = expression
            cases.append(item)
        item = copy.deepcopy(self.leaf)
        item["resources"]["storage"]["name"] = "[variables('cycle')]"
        item["variables"] = {"cycle": "[variables('cycle')]"}
        cases.append(item)
        item = copy.deepcopy(self.root)
        item["resources"]["runtime"]["properties"]["expressionEvaluationOptions"]["scope"] = "outer"
        cases.append(item)
        item = copy.deepcopy(self.root)
        item["resources"]["runtime"]["properties"]["parameters"]["name"] = {"reference": {"secretName": "not-read"}}
        cases.append(item)
        item = copy.deepcopy(self.root)
        item["resources"]["runtime"]["properties"]["parameters"] = []
        cases.append(item)
        item = copy.deepcopy(self.root)
        properties = item["resources"]["runtime"]["properties"]
        properties["parameters"] = {"NAME": {"value": "Invalid-Name"}}
        properties["template"]["parameters"]["name"]["defaultValue"] = "validdefault"
        cases.append(item)
        item = copy.deepcopy(self.leaf)
        item["resources"]["storage"]["name"] = "[variables('actual')]"
        item["variables"] = {"actual": "validname", "ACTUAL": "Invalid-Name"}
        cases.append(item)
        for item in cases:
            with self.subTest(source=cases.index(item)):
                with self.assertRaises(ValueError):
                    scan_copy(item)

    def test_blueprint_style_scope_dependent_name_has_no_literal_substitute(self):
        source = copy.deepcopy(self.root)
        source["parameters"] = {"namePrefix": {"type": "string", "defaultValue": "cohors"}}
        source["variables"] = {
            "suffix": "[take(uniqueString(subscription().subscriptionId, resourceGroup().id, parameters('namePrefix')), 8)]",
            "compactName": "[format('{0}{1}', parameters('namePrefix'), variables('suffix'))]",
        }
        source["resources"]["runtime"]["properties"]["parameters"]["name"]["value"] = "[format('st{0}ev', variables('compactName'))]"
        with self.assertRaisesRegex(ValueError, "Unbound ARM name parameter: namePrefix"):
            scan_copy(source)
        source["variables"]["compactName"] = "[format('cohors{0}', variables('suffix'))]"
        with self.assertRaisesRegex(ValueError, "Unsupported ARM naming function: take"):
            scan_copy(source)

    def test_failed_proof_writes_no_partial_artifacts_and_cannot_hide_unknown_names(self):
        for name in ("[parameters('name')]", "var.unknown", "[format('{0}', 'random_id.hidden')]"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                source = Path(temporary) / "main.json"
                destination = Path(temporary) / "scan"
                leaf = copy.deepcopy(self.leaf)
                leaf["resources"]["storage"]["name"] = name
                contents = json.dumps(leaf).encode("utf-8")
                source.write_bytes(contents)
                with self.assertRaises(ValueError):
                    export(source, destination)
                self.assertEqual(source.read_bytes(), contents)
                self.assertFalse(destination.exists())
                self.assertFalse(destination.with_suffix(".manifest.json").exists())
                self.assertFalse(destination.with_suffix(".original.json").exists())

    def test_inverse_proof_detects_dropped_resources_changed_controls_and_unbound_evidence(self):
        source = self.sql_template()
        converted, changes = scan_copy(source)
        for mutate in (
            lambda r: r["resources"].pop(),
            lambda r: r["resources"][0]["properties"].update(publicNetworkAccess="Enabled"),
            lambda r: r["resources"][3].update(condition=True),
            lambda r: r["resources"][3].update(condition=0),
            lambda r: r["resources"][0]["resources"][0]["properties"].update(state="Disabled"),
            lambda r: r["resources"][0]["resources"][0]["properties"].update(retentionDays=False),
        ):
            altered = copy.deepcopy(converted)
            mutate(altered)
            with self.assertRaises(ValueError):
                verify_projection(source, altered, changes)
        altered_changes = copy.deepcopy(changes)
        altered_changes[0]["resourceSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "bound"):
            verify_projection(source, converted, altered_changes)


if __name__ == "__main__":
    unittest.main()
