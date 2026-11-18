import hashlib
from pathlib import Path
import sys
import unittest


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
from primary_control import GateError
from primary_sql import sql_grants, verify_receipt


class PrimarySqlTests(unittest.TestCase):
    client = "00112233-4455-6677-8899-aabbccddeeff"
    modules = {"dbo.CohorsOperationsQuote": "a" * 64, "dbo.CohorsOperationsWrite": "b" * 64}

    def test_operations_assigns_only_original_role_and_connect_not_second_application_ddl(self):
        sql = sql_grants("operations", self.client, self.modules)
        self.assertIn("SID=0x33221100554477668899aabbccddeeff,TYPE=E", sql)
        self.assertIn("ALTER ROLE [cohors_operations_runtime] ADD MEMBER [cohors_operations]", sql)
        self.assertEqual([line for line in sql.splitlines() if line.startswith("GRANT ")],
                         ["GRANT CONNECT TO [cohors_operations];"])
        self.assertNotRegex(sql, r"(?m)^\s*(CREATE|ALTER)( OR ALTER)? (PROCEDURE|FUNCTION|TABLE)\b")
        self.assertNotRegex(sql, r"(?m)^\s*(CREATE ROLE|DENY)\b")
        self.assertIn("c.column_id,'D','UPDATE'", sql)
        self.assertIn("Original Operations role/object/column permissions changed", sql)
        self.assertIn("Deployed module bytes differ", sql)

    def test_unbound_modules_roles_and_sql_injection_are_rejected(self):
        for role, client, modules in (
            ("operations", self.client, None),
            ("operations", self.client, {**self.modules, "dbo.Other": "c" * 64}),
            ("operations", self.client, {**self.modules, "dbo.CohorsOperationsWrite": "x'; GRANT CONTROL"}),
            ("api", "x'; GRANT CONTROL", None),
            ("workflows", self.client, None),
        ):
            with self.subTest(role=role, client=client), self.assertRaises(GateError):
                sql_grants(role, client, modules)

    def test_api_receives_no_history_updates_or_cloud_physical_delete(self):
        sql = sql_grants("api", self.client)
        self.assertIn("GRANT SELECT,INSERT,UPDATE ON OBJECT::dbo.[Outbox]", sql)
        self.assertIn("GRANT SELECT,INSERT ON OBJECT::dbo.[Inbox]", sql)
        self.assertIn("GRANT SELECT,INSERT ON OBJECT::dbo.[AuditEvents]", sql)
        self.assertNotRegex(sql, r"GRANT [^;\n]*(DELETE|EXECUTE|CONTROL)")
        self.assertNotRegex(sql, r"GRANT [^;\n]*UPDATE ON OBJECT::dbo.\[(AuditEvents|AggregateVersions|Commands)\]")
        self.assertIn("DENY INSERT,UPDATE,DELETE ON OBJECT::dbo.SchemaVersions", sql)

    def test_metadata_readback_cannot_claim_runtime_entra_authentication(self):
        value = {"contract": "cohors-sql-permission-readback-1", "runtimeRole": "operations", "schemaVersion": 3,
                 "principalClientId": self.client, "principalName": "cohors_operations",
                 "principalType": "EXTERNAL_USER", "metadataSha256": "c" * 64,
                 "metadataVerified": True, "runtimeEntraAuthenticationProven": False}
        statement = sql_grants("operations", self.client, self.modules)
        self.assertEqual(verify_receipt(value, "operations", self.client, statement)["permissionStatementSha256"],
                         hashlib.sha256(statement.encode()).hexdigest())
        for change in ({"principalClientId": "11111111-2222-4333-8444-555555555555"},
                       {"schemaVersion": 2}, {"runtimeEntraAuthenticationProven": True},
                       {"metadataVerified": False}, {"metadataSha256": None}):
            with self.subTest(change=change), self.assertRaises(GateError):
                verify_receipt({**value, **change}, "operations", self.client, statement)


if __name__ == "__main__":
    unittest.main()
