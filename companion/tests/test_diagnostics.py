from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from logmodel import parse_line, visible_lines, format_line, health_summary
from diagnostics import bridge_diagnostics, read_amb, operation_text, safety_text


def line(level, source, message):
    return parse_line(f"2026-11-06 13:12:05.287 +07:00 [{level}] [{source}] {message}")


class LogFilterChecks(unittest.TestCase):
    def test_compact_format_preserves_message_and_full_metadata_is_available(self):
        record = line("INF", "AutoMarketbuddy", "Slot 8: comparison ready")
        self.assertEqual(record.source, "AutoMarketbuddy")
        self.assertEqual(format_line(record), "AutoMarketbuddy · Slot 8: comparison ready")
        self.assertNotIn("2026-11-06", format_line(record))
        self.assertEqual(format_line(record, True), record.text)

    def test_repository_chatter_hidden_but_errors_and_stack_are_kept(self):
        error = line("ERR", "PluginRepository", "Repository request failed")
        stack = parse_line("   at Request.Fetch()", error)
        records = [line("INF", "PluginRepository", "Successfully fetched"),
                   line("WRN", "PluginRepository", "Missing testing API metadata"), error, stack]
        self.assertEqual(visible_lines(records), [error, stack])
        self.assertEqual(len(visible_lines(records, scope="Everything", hide_repository=False)), 4)
        self.assertEqual(len(visible_lines(records, source="PluginRepository")), 4)

    def test_amb_scope_keeps_lifecycle_exception_context_and_filters_other_sources(self):
        own = line("ERR", "LocalPlugin", "Failed loading AutoMarketbuddy.dll")
        stack = parse_line("   at Plugin.Load()", own)
        unrelated = line("ERR", "OtherPlugin", "Other failure")
        self.assertEqual(visible_lines([own, stack, unrelated], scope="AMB"), [own, stack])
        self.assertEqual(visible_lines([own, stack, unrelated], source="OtherPlugin"), [unrelated])
        self.assertEqual(visible_lines([own, stack], scope="AMB", search="Plugin.Load"), [stack])

    def test_relevant_level_and_explicit_all_controls(self):
        routine = line("INF", "Profile", "Profile metadata refreshed")
        useful = line("INF", "AutoMarketbuddy", "Preview started")
        debug = line("DBG", "AutoMarketbuddy", "Diagnostic detail")
        warning = line("WRN", "OtherPlugin", "Something needs attention")
        records = [routine, useful, debug, warning]
        self.assertEqual(visible_lines(records), [useful, warning])
        self.assertEqual(visible_lines(records, level="All", scope="Everything"), records)
        self.assertEqual(visible_lines(records, level="Warnings+"), [warning])
        own_metadata = line("INF", "Profile", 'Adding plugin AutoMarketbuddy("guid") to profile "guid"')
        self.assertEqual(visible_lines([own_metadata]), [])
        self.assertEqual(visible_lines([own_metadata], source="Profile"), [own_metadata])

    def test_health_counts_events_not_stack_lines_and_identifies_slow_plugin(self):
        error = line("ERR", "AutoMarketbuddy", "Stopped with error")
        records = [error, parse_line("trace line", error), line("WRN", "PluginRepository", "Catalog warning"),
                   line("WRN", "HITCH", "Long UiBuilder(SlowPlugin) detected, 451ms > 100ms")]
        errors, warnings, hitches = health_summary(records)
        self.assertEqual(errors, {"AutoMarketbuddy": 1})
        self.assertNotIn("PluginRepository", warnings)
        self.assertEqual(hitches, {"SlowPlugin": 1})


class DiagnosticContractChecks(unittest.TestCase):
    def test_old_bridge_and_malformed_diagnostics_are_explicit(self):
        self.assertIsNone(bridge_diagnostics({"conditions": []}))
        self.assertIn("Update bridge", read_amb(None).status)
        with self.assertRaises(ValueError):
            bridge_diagnostics({"diagnostics": {"plugins": "bad"}})
        malformed = {"ambStatus": "Available", "ambSnapshot": "[]"}
        self.assertIsNone(read_amb(malformed).session)

    def test_real_csharp_diagnostic_contract_and_independent_amb_freshness(self):
        contract = os.environ.get("COMPANION_CONTRACT")
        if not contract:
            self.skipTest("Run companion/check.sh for the actual C# contract")
        snapshot = json.loads(Path(contract).read_text())
        diagnostics = bridge_diagnostics(snapshot)
        value = json.loads(diagnostics["ambSnapshot"])
        captured = datetime.fromisoformat(value["capturedAt"])
        amb = read_amb(diagnostics, captured)
        self.assertIsNotNone(amb.session)
        self.assertNotIn("characterId", amb.session["item"])
        summary, detail = operation_text(amb)
        self.assertIn("Preview", summary)
        self.assertIn("slot 8", detail)
        self.assertIn("Eligible 1/4", detail)
        self.assertIn("proposed 899 gil", detail)
        self.assertIn("Cache · 0.5s old", detail)
        stale = read_amb(diagnostics, captured + timedelta(seconds=4))
        self.assertIsNone(stale.session)
        self.assertIn("stale", stale.status)
        for field, bad in (("trace", [None]), ("timeoutRemainingMs", "soon"), ("item", []), ("decision", {}),
                           ("comparisonSource", []), ("cachedItems", -1)):
            with self.subTest(field=field):
                broken = json.loads(diagnostics["ambSnapshot"])
                broken["session"][field] = bad
                diagnostic = dict(diagnostics, ambSnapshot=json.dumps(broken))
                self.assertIsNone(read_amb(diagnostic, captured).session)

    def test_safety_requires_evidence_and_never_claims_apply_is_safe(self):
        plugin = dict(name="Marketbuddy", internalName="Marketbuddy", version="1", apiLevel=15,
                      isLoaded=True, isOutdated=False, isBanned=False, isDev=False)
        snapshot = dict(loggedIn=True, conditions=["InCombat", "BetweenAreas", "InDutyQueue"])
        diagnostic = dict(plugins=[plugin], visibleWindows=["SelectYesno"])
        text = safety_text(snapshot, diagnostic)
        self.assertIn("InCombat", text)
        self.assertIn("Marketbuddy", text)
        self.assertIn("BetweenAreas", text)
        self.assertIn("SelectYesno", text)
        self.assertIn("Confirmation owner: Unknown", text)
        self.assertNotIn("safe", text.casefold())
        self.assertEqual(safety_text(None, None), "—")


if __name__ == "__main__":
    unittest.main()
