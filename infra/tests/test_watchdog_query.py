"""Offline contract tests, not a Kusto service or Azure alert scheduler.

Translate only this query's filter/count/projection to in-memory SQLite. Read
predicates from the production KQL; reject other pipeline shapes instead of
silently skipping them. Time values use integer UTC microseconds.
"""

import json
import re
import sqlite3
import unittest
from contextlib import closing
from pathlib import Path


QUERY_PATH = Path(__file__).resolve().parents[1] / "monitoring/watchdog-missing.kql"
SECOND = 1_000_000
SUCCESS = "cohors.watchdog.completed"
ROLE = "cohors-operations"


class WatchdogQueryTests(unittest.TestCase):
    def evaluate(self, events, query=None):
        query = (QUERY_PATH.read_text(encoding="utf-8") if query is None else query).replace(
            "__OPERATIONS_ROLE__", ROLE
        )
        match = re.fullmatch(
            r"\s*AppEvents\s*(?P<filters>(?:\|\s*where\s+[^|]+)+)"
            r"\|\s*summarize\s+(?P<count>[A-Za-z_]\w*)\s*=\s*count\(\)\s*"
            r"\|\s*project\s+(?P<column>[A-Za-z_]\w*)\s*=\s*(?P<expression>[^|]+)\s*",
            query,
        )
        self.assertIsNotNone(match, "Unsupported alert query shape; update the contract test explicitly.")
        parameters = {}

        def scalar(expression):
            def literal(token):
                value = token.group()
                if value.startswith('"'):
                    name = f"value_{len(parameters)}"
                    parameters[name] = json.loads(value)
                    return f":{name}"
                return str(int(value[:-1]) * {"s": SECOND, "m": 60 * SECOND}[value[-1]])

            return re.sub(r'"(?:\\.|[^"\\])*"|\b\d+[sm]\b', literal, expression)

        filters = re.findall(r"\|\s*where\s+([^|]+)", match["filters"])
        predicate = " AND ".join(f"({scalar(part)})" for part in filters)
        projection = scalar(match["expression"])
        sql = (f'SELECT {projection} AS {match["column"]} FROM '
               f'(SELECT count(*) AS {match["count"]} FROM AppEvents WHERE {predicate})')
        with closing(sqlite3.connect(":memory:")) as database:
            database.create_function("now", 0, lambda: 0)
            database.create_function("ago", 1, lambda duration: -duration)
            database.create_function("toint", 1, lambda value: None if value is None else int(value))
            database.execute("CREATE TABLE AppEvents (TimeGenerated INTEGER, Name TEXT, AppRoleName TEXT)")
            database.executemany("INSERT INTO AppEvents VALUES (?, ?, ?)", events)
            result = database.execute(sql, parameters)
            self.assertEqual([column[0] for column in result.description], ["MissingSuccess"])
            rows = result.fetchall()
            self.assertEqual(len(rows), 1, "Empty input must still produce exactly one numeric result.")
            self.assertIs(type(rows[0][0]), int)
            return rows[0][0]

    def test_healthy_host_has_recent_success(self):
        self.assertEqual(self.evaluate([
            (-60 * SECOND, SUCCESS, ROLE), (-30 * SECOND, SUCCESS, ROLE), (0, SUCCESS, ROLE),
        ]), 0)

    def test_never_started_host_is_missing(self):
        self.assertEqual(self.evaluate([]), 1)
        self.assertEqual(self.evaluate([(0, "cohors.monitor.bootstrap", "cohors-monitor-bootstrap")]), 1)

    def test_last_success_on_either_side_of_three_minutes(self):
        for age, expected in ((179_999_999, 0), (180_000_000, 0), (180_000_001, 1)):
            with self.subTest(age_microseconds=age):
                self.assertEqual(self.evaluate([(-age, SUCCESS, ROLE)]), expected)

    def test_old_foreign_future_and_undated_events_cannot_mask_failure(self):
        events = [
            (-300 * SECOND, SUCCESS, ROLE),
            (-1200 * SECOND, SUCCESS, ROLE),
            (0, "cohors.watchdog.failed", ROLE),
            (0, SUCCESS, "cohors-api"),
            (0, SUCCESS.upper(), ROLE),
            (0, SUCCESS, ROLE.upper()),
            (SECOND, SUCCESS, ROLE),
            (None, SUCCESS, ROLE),
        ]
        for event in events:
            with self.subTest(event=event):
                self.assertEqual(self.evaluate([event]), 1)
        self.assertEqual(self.evaluate(events), 1)

    def test_recent_success_recovers_despite_unrelated_history(self):
        self.assertEqual(self.evaluate([
            (-300 * SECOND, SUCCESS, ROLE),
            (0, "cohors.watchdog.failed", ROLE),
            (0, SUCCESS, "cohors-api"),
            (-30 * SECOND, SUCCESS, ROLE),
        ]), 0)


if __name__ == "__main__":
    unittest.main()
