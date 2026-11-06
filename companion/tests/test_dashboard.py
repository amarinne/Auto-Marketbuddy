import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from dashboard import Dashboard, LogTail, MAX_READ, actor_text, read_state


class DashboardChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="xiv-companion-", dir=os.environ.get("COMPANION_TEST_DIR"))
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "state.json"
        self.now = datetime.now(timezone.utc)
        self.state = dict(schemaVersion=1, capturedAt=self.now.isoformat(), sequence=1,
                          bridgeActive=True, loggedIn=True, player=None, target=None,
                          location=None, conditions=["InCombat"])

    def save(self):
        self.path.write_text(json.dumps(self.state), encoding="utf-8")

    def test_fresh_stale_future_and_unload(self):
        self.save()
        self.assertEqual(read_state(self.path, self.now).status, "Connected")
        stale = read_state(self.path, self.now + timedelta(seconds=4))
        self.assertEqual(stale.status, "Stale")
        self.assertIsNone(stale.snapshot)
        self.assertEqual(read_state(self.path, self.now - timedelta(seconds=4)).status, "Clock mismatch")
        self.state["bridgeActive"] = False
        self.save()
        self.assertEqual(read_state(self.path, self.now).status, "Bridge stopped")

    def test_missing_malformed_and_incompatible(self):
        self.assertEqual(read_state(self.path).status, "Waiting for bridge")
        for content in ('{', 'null', '{"schemaVersion": 2}', '{"schemaVersion": 1}'):
            self.path.write_text(content)
            self.assertIsNone(read_state(self.path).snapshot)
        self.path.write_bytes(b"x" * (MAX_READ + 1))
        self.assertEqual(read_state(self.path).status, "State unavailable")

    def test_bad_nested_data_never_reaches_window(self):
        for field, value in (("player", []), ("target", {"name": "bad"}), ("conditions", "InCombat"),
                             ("location", {"name": "bad"}), ("capturedAt", "no date"), ("loggedIn", "yes")):
            with self.subTest(field=field):
                original = self.state[field]
                self.state[field] = value
                self.save()
                self.assertIsNone(read_state(self.path).snapshot)
                self.state[field] = original

    def test_logged_out_and_capture_error_hide_state(self):
        self.state["loggedIn"] = False
        self.save()
        self.assertEqual(read_state(self.path, self.now).status, "Connected · logged out")
        self.state["error"] = "State sample failed"
        self.save()
        self.assertIsNone(read_state(self.path, self.now).snapshot)

    def test_incremental_unicode_and_multiline_severity(self):
        log = Path(self.temporary.name) / "log"
        log.write_bytes(b"[12:00:00 ERR] failure\nstack trace\npartial ")
        reader = LogTail(log)
        self.assertEqual([x.level for x in reader.poll()], ["ERR", "ERR"])
        with log.open("ab") as stream:
            stream.write("日本".encode("utf-8")[:-1])
        self.assertEqual(reader.poll(), [])
        with log.open("ab") as stream:
            stream.write("日本".encode("utf-8")[-1:] + b"\n[12:00:01 INF] recovered\n")
        lines = reader.poll()
        self.assertEqual(lines[0].text, "partial 日本")
        self.assertEqual(lines[1].level, "INF")
        self.assertEqual(reader.poll(), [])

    def test_missing_rotation_and_truncation(self):
        log = Path(self.temporary.name) / "log"
        reader = LogTail(log)
        self.assertEqual(reader.poll(), [])
        log.write_text("old content\n")
        self.assertEqual(reader.poll()[0].text, "old content")
        log.rename(log.with_suffix(".old"))
        log.write_text("new\n")
        self.assertEqual(reader.poll()[0].text, "new")
        log.write_text("x\n")
        self.assertEqual(reader.poll()[0].text, "x")

    def test_initial_tail_and_long_lines_are_bounded(self):
        log = Path(self.temporary.name) / "log"
        log.write_bytes(b"x" * MAX_READ + b"\nrecent\n")
        reader = LogTail(log)
        self.assertEqual([x.text for x in reader.poll()], ["recent"])
        log.write_bytes(b"x" * (MAX_READ * 3))
        reader = LogTail(log)
        reader.poll()
        with log.open("ab") as stream:
            stream.write(b"y" * (MAX_READ + 1))
        reader.poll()
        self.assertLessEqual(len(reader.partial), MAX_READ)

    def test_real_csharp_contract(self):
        contract = os.environ.get("COMPANION_CONTRACT")
        if not contract:
            self.skipTest("Run companion/check.sh for the actual C# producer check")
        value = json.loads(Path(contract).read_text())
        captured = datetime.fromisoformat(value["capturedAt"])
        view = read_state(Path(contract), captured)
        self.assertEqual(view.status, "Connected")
        self.assertIn("Offline test player", actor_text(view.snapshot["player"]["actor"]))
        self.assertIn("12,345", actor_text(view.snapshot["target"]))


class WindowChecks(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("COMPANION_UI_CHECK"), "Explicit native window smoke check")
    def test_window_filters_pause_clear_and_shutdown(self):
        import tkinter as tk
        with tempfile.TemporaryDirectory(dir=os.environ.get("COMPANION_TEST_DIR")) as directory:
            log = Path(directory) / "log"
            log.write_text("[12:00:00 INF] fixture info\n[12:00:01 ERR] fixture error\n")
            state = Path(directory) / "state.json"
            if os.environ.get("COMPANION_CONTRACT"):
                fixture = json.loads(Path(os.environ["COMPANION_CONTRACT"]).read_text())
                fixture["capturedAt"] = datetime.now(timezone.utc).isoformat()
                if fixture.get("diagnostics", {}).get("ambSnapshot"):
                    amb = json.loads(fixture["diagnostics"]["ambSnapshot"])
                    amb["capturedAt"] = fixture["capturedAt"]
                    fixture["diagnostics"]["ambSnapshot"] = json.dumps(amb)
                state.write_text(json.dumps(fixture))
            root = tk.Tk()
            root.withdraw()
            app = Dashboard(root, argparse.Namespace(log=log, state=state))
            root.update_idletasks()
            root.geometry("760x580")
            root.deiconify()
            root.update()
            app.tabs.select(3)
            root.update()
            for field in app.fields.values():
                self.assertLessEqual(int(field.cget("wraplength")), field.winfo_width())
            if state.exists():
                self.assertIn("Offline test player", app.fields["Player"].cget("text"))
                self.assertIn("InCombat", app.fields["Location"].cget("text"))
                self.assertIn("Casting action #123", app.fields["Target"].cget("text"))
                self.assertIn("Retainer 0x000000000000001E", app.operation_detail.get("1.0", "end"))
                self.assertEqual(len(app.trace.get_children()), 2)
                self.assertEqual(len(app.plugins.get_children()), 2)
                app.tabs.select(0)
                root.update()
                self.assertIn("InCombat", app.overview["Game Context"].cget("text"))
                fixture["capturedAt"] = (datetime.now(timezone.utc) - timedelta(seconds=5)).isoformat()
                state.write_text(json.dumps(fixture))
                root.after_cancel(app.timer)
                app.tick()
                self.assertEqual(app.fields["Player"].cget("text"), "—")
            self.assertIn("fixture info", app.log.get("1.0", "end"))
            self.assertNotIn("[12:00:00 INF]", app.log.get("1.0", "end"))
            app.metadata.set(True)
            app.render_logs()
            self.assertIn("[12:00:00 INF]", app.log.get("1.0", "end"))
            app.metadata.set(False)
            app.render_logs()
            app.level.set("Errors")
            app.render_logs()
            self.assertNotIn("fixture info", app.log.get("1.0", "end"))
            self.assertIn("fixture error", app.log.get("1.0", "end"))
            app.search.set("absent")
            self.assertEqual(app.log.get("1.0", "end").strip(), "")
            app.search.set("")
            app.toggle_pause()
            app.clear()
            app.render_logs()
            self.assertEqual(app.log.get("1.0", "end").strip(), "")
            app.toggle_pause()
            app.close()


if __name__ == "__main__":
    unittest.main()
