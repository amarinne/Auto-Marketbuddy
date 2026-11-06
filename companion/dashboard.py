#!/usr/bin/env python3
"""Local, read-only Dalamud log and game-state dashboard. Uses only Python's standard library."""
from __future__ import annotations

import argparse
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import sys
from logmodel import LogLine, parse_line, visible_lines, format_line, health_summary

MAX_READ = 128 * 1024
MAX_LINES = 2000
STALE_SECONDS = 3


class LogTail:
    """Bounded incremental reader; never keeps the game log open or writes to it."""

    def __init__(self, path: Path):
        self.path = path
        self.identity = None
        self.offset = 0
        self.partial = b""
        self.previous = None
        self.status = "Waiting for Log"

    def poll(self) -> list[LogLine]:
        try:
            with self.path.open("rb") as stream:
                stat = os.fstat(stream.fileno())
                identity = (stat.st_dev, stat.st_ino)
                reset = identity != self.identity or stat.st_size < self.offset
                if reset:
                    self.identity = identity
                    self.offset = max(0, stat.st_size - MAX_READ)
                    self.partial = b""
                    self.previous = None
                stream.seek(self.offset)
                chunk = stream.read(MAX_READ)
                self.offset = stream.tell()
            if reset and self.offset - len(chunk) > 0:
                # The initial tail can start in the middle of a UTF-8 character or line.
                chunk = chunk.partition(b"\n")[2]
            parts = (self.partial + chunk).split(b"\n")
            self.partial = parts.pop()
            if len(self.partial) > MAX_READ:
                parts.append(self.partial[:MAX_READ] + b" [long line truncated]")
                self.partial = b""
            result = []
            for raw in parts:
                line = raw.decode("utf-8", errors="replace").rstrip("\r")
                self.previous = parse_line(line, self.previous)
                result.append(self.previous)
            self.status = "Live" + (" · catching up" if self.offset < stat.st_size else "")
            return result
        except OSError as error:
            self.status = f"Log unavailable: {error.strerror or type(error).__name__}"
            return []


@dataclass(frozen=True)
class StateView:
    status: str
    detail: str
    snapshot: dict | None = None


def _actor_valid(actor):
    if not isinstance(actor, dict):
        return False
    if not all(isinstance(actor.get(key), str) for key in ("name", "kind")):
        return False
    position = actor.get("position")
    if not isinstance(position, dict) or not all(
        type(position.get(key)) in (int, float) and math.isfinite(position[key]) for key in ("x", "y", "z")
    ):
        return False
    return type(actor.get("isCasting")) is bool and all(actor.get(key) is None or
               (type(actor[key]) in (int, float) and math.isfinite(actor[key]))
               for key in ("level", "hp", "maxHp", "mp", "maxMp", "castActionId", "castElapsed", "castTotal"))


def read_state(path: Path, now: datetime | None = None) -> StateView:
    try:
        with path.open("rb") as stream:
            raw = stream.read(MAX_READ + 1)
        if len(raw) > MAX_READ:
            raise ValueError("Snapshot exceeds size limit")
        value = json.loads(raw, parse_constant=lambda unused: None)
        if not isinstance(value, dict) or value.get("schemaVersion") != 1:
            return StateView("Incompatible", "Update bridge")
        if not all(type(value.get(key)) is bool for key in ("bridgeActive", "loggedIn")):
            raise ValueError("Invalid connection flags")
        captured = datetime.fromisoformat(value["capturedAt"].replace("Z", "+00:00"))
        if captured.tzinfo is None:
            raise ValueError("Snapshot timestamp has no timezone")
        age = ((now or datetime.now(timezone.utc)) - captured).total_seconds()
        if age < -STALE_SECONDS:
            return StateView("Clock mismatch", "Check system clock")
        if not value["bridgeActive"]:
            return StateView("Bridge stopped", "")
        if age > STALE_SECONDS:
            return StateView("Stale", f"Last update {age:.0f}s ago")
        if value.get("error"):
            return StateView("Bridge error", str(value["error"])[:240])
        if not isinstance(value.get("conditions"), list) or not all(isinstance(x, str) for x in value["conditions"]):
            raise ValueError("Invalid conditions")
        player = value.get("player")
        if player is not None and (not isinstance(player, dict) or not _actor_valid(player.get("actor"))
                                  or not all(isinstance(player.get(key), str) for key in ("world", "homeWorld", "job"))):
            raise ValueError("Invalid player")
        if value.get("target") is not None and not _actor_valid(value["target"]):
            raise ValueError("Invalid target")
        location = value.get("location")
        if location is not None and (not isinstance(location, dict) or not isinstance(location.get("name"), str)
                                     or not all(type(location.get(key)) is int for key in ("territoryId", "mapId", "instance"))):
            raise ValueError("Invalid location")
        if not value["loggedIn"]:
            return StateView("Connected · logged out", f"Updated {max(0, age):.1f}s ago", value)
        return StateView("Connected", f"Updated {max(0, age):.1f}s ago · #{value.get('sequence', '?')}", value)
    except FileNotFoundError:
        return StateView("Waiting for bridge", "Load XIV Companion Bridge when idle")
    except (OSError, ValueError, TypeError, KeyError, AttributeError) as error:
        return StateView("State unavailable", f"Check state file ({type(error).__name__})")


def actor_text(actor: dict | None) -> str:
    if actor is None:
        return "None"
    result = [f"{actor['name']} · {actor['kind']}"]
    if actor.get("level") is not None:
        number = lambda key: f"{actor[key]:,}" if actor.get(key) is not None else "—"
        result.append(f"Level {actor['level']}  ·  HP {number('hp')} / {number('maxHp')}")
        result.append(f"MP {number('mp')} / {number('maxMp')}")
    position = actor["position"]
    result.append("X {x:.1f}   Y {y:.1f}   Z {z:.1f}".format(**position))
    if actor.get("isCasting"):
        elapsed, total = actor.get("castElapsed"), actor.get("castTotal")
        if elapsed is not None and total is not None:
            result.append(f"Casting action #{actor.get('castActionId')} · {elapsed:.1f} / {total:.1f}s")
    return "\n".join(result)


class Dashboard:
    def __init__(self, root, args):
        import tkinter as tk
        from tkinter import ttk
        from diagnostics import bridge_diagnostics, read_amb, operation_text, safety_text, health_text

        self.root, self.args = root, args
        self.tail = LogTail(args.log)
        self.lines = deque(maxlen=MAX_LINES)
        self.paused = False
        self.timer = None
        self.rendered = {}
        self.last_plugins = None
        self.last_trace = None
        self.bridge_diagnostics, self.read_amb = bridge_diagnostics, read_amb
        self.operation_text, self.safety_text, self.health_text = operation_text, safety_text, health_text
        root.title("XIV Companion")
        root.geometry("1180x900")
        root.minsize(760, 650)
        root.configure(background="#111923")
        style = ttk.Style(root)
        style.theme_use("clam")
        style.configure(".", background="#111923", foreground="#e5edf6", fieldbackground="#1c2836", font=("Sans", 10))
        style.configure("TLabelframe", background="#111923", bordercolor="#314355")
        style.configure("TLabelframe.Label", foreground="#7dd3fc", font=("Sans", 11, "bold"))
        style.configure("TButton", background="#26394c", padding=(8, 4))
        style.map("TButton", background=[("active", "#3a5269")])
        style.configure("TCombobox", selectbackground="#26394c", selectforeground="#ffffff")
        style.map("TCombobox", fieldbackground=[("readonly", "#1c2836")], foreground=[("readonly", "#e5edf6")])
        style.configure("Treeview", background="#111923", fieldbackground="#111923", foreground="#e5edf6", rowheight=25)
        style.configure("Treeview.Heading", background="#26394c", foreground="#e5edf6")
        style.map("Treeview", background=[("selected", "#31506a")])
        style.configure("TNotebook.Tab", background="#26394c", padding=(12, 7))
        style.map("TNotebook.Tab", background=[("selected", "#31506a")])

        frame = ttk.Frame(root, padding=16)
        frame.pack(fill="both", expand=True)
        header = ttk.Frame(frame)
        header.pack(fill="x")
        ttk.Label(header, text="XIV Companion", font=("Sans", 18, "bold")).pack(side="left")
        self.connection = ttk.Label(frame, text="Waiting for bridge", font=("Sans", 12, "bold"))
        self.connection.pack(anchor="w", pady=(10, 2))
        self.detail = ttk.Label(frame, text="", foreground="#9aacbe", wraplength=700)
        self.detail.pack(anchor="w", pady=(0, 8))
        self.panes = ttk.Panedwindow(frame, orient="vertical")
        self.panes.pack(fill="both", expand=True)
        self.tabs = ttk.Notebook(self.panes, height=320)
        self.panes.add(self.tabs, weight=1)

        overview, operation, health, game = (ttk.Frame(self.tabs, padding=10) for _ in range(4))
        for title, tab in (("Overview", overview), ("AMB Trace", operation), ("Plugins", health), ("Game", game)):
            self.tabs.add(tab, text=title)
        self.overview = self.make_cards(overview, ("AMB", "Game Context", "Plugins"))
        self.fields = self.make_cards(game, ("Player", "Location", "Target"))
        self.operation_detail = self.make_text(operation, height=8, wrap="word")
        ttk.Label(operation, text="Recent Transitions", foreground="#7dd3fc").pack(anchor="w", pady=(8, 3))
        self.trace = self.make_tree(operation, ("Elapsed", "Phase", "Event"), (80, 110, 650), height=4)
        self.plugins = self.make_tree(health, ("State", "Plugin", "Version", "API", "Log"), (120, 240, 110, 55, 230), height=6)
        self.health_detail = self.make_text(health, height=4, wrap="word")

        logs = ttk.Frame(self.panes, padding=(0, 10, 0, 0))
        self.panes.add(logs, weight=1)
        ttk.Label(logs, text="Log", font=("Sans", 11, "bold"), foreground="#7dd3fc").pack(anchor="w", pady=(0, 6))
        controls = ttk.Frame(logs)
        controls.pack(fill="x", pady=(0, 6))
        self.scope = tk.StringVar(value=getattr(args, "scope", "Relevant"))
        self.level = tk.StringVar(value=getattr(args, "level", "Info+"))
        self.source = tk.StringVar(value=getattr(args, "source", "All Sources"))
        for variable, values, width in ((self.scope, ("Relevant", "AMB", "Everything"), 11),
                                         (self.level, ("All", "Info+", "Warnings+", "Errors"), 11)):
            widget = ttk.Combobox(controls, textvariable=variable, values=values, width=width, state="readonly")
            widget.pack(side="left", padx=(0, 6))
            widget.bind("<<ComboboxSelected>>", lambda unused: self.render_logs())
        self.sources = ttk.Combobox(controls, textvariable=self.source, values=("All Sources",), width=23, state="readonly")
        self.sources.pack(side="left", padx=(0, 6))
        self.sources.bind("<<ComboboxSelected>>", lambda unused: self.render_logs())
        self.search = tk.StringVar()
        search = ttk.Entry(controls, textvariable=self.search)
        search.pack(side="left", fill="x", expand=True)
        self.search.trace_add("write", lambda *unused: self.render_logs())
        self.pause_button = ttk.Button(controls, text="Pause", command=self.toggle_pause)
        self.pause_button.pack(side="left", padx=(6, 0))
        options = ttk.Frame(logs)
        options.pack(fill="x", pady=(0, 6))
        self.hide_repository = tk.BooleanVar(value=not getattr(args, "include_repository", False))
        self.metadata = tk.BooleanVar(value=getattr(args, "metadata", False))
        self.follow = tk.BooleanVar(value=True)
        ttk.Checkbutton(options, text="Repository Errors Only", variable=self.hide_repository, command=self.render_logs).pack(side="left")
        ttk.Checkbutton(options, text="Full Metadata", variable=self.metadata, command=self.render_logs).pack(side="left", padx=8)
        ttk.Checkbutton(options, text="Follow", variable=self.follow).pack(side="left")
        ttk.Button(options, text="Clear View", command=self.clear).pack(side="right")
        self.log = self.make_text(logs, height=9, wrap="word")
        for tag, color in (("ERR", "#fda4af"), ("FTL", "#ff7389"), ("WRN", "#fcd58a"), ("DBG", "#8499af"), ("VRB", "#8499af")):
            self.log.tag_configure(tag, foreground=color)
        self.log_status = ttk.Label(logs, text="", foreground="#9aacbe", wraplength=700)
        self.log_status.pack(anchor="w", pady=(6, 0))
        root.protocol("WM_DELETE_WINDOW", self.close)
        self.tick()

    def make_cards(self, parent, titles):
        from tkinter import ttk
        cards = ttk.Frame(parent)
        cards.pack(fill="both", expand=True)
        result = {}
        for column, title in enumerate(titles):
            cards.columnconfigure(column, weight=1, uniform="card")
            card = ttk.LabelFrame(cards, text=title, padding=10)
            card.grid(row=0, column=column, sticky="nsew", padx=(0 if column == 0 else 8, 0))
            label = ttk.Label(card, text="—", justify="left", anchor="nw", wraplength=300)
            label.pack(fill="both", expand=True)
            card.bind("<Configure>", lambda event, field=label: field.configure(wraplength=max(100, event.width - 28)))
            result[title] = label
        cards.rowconfigure(0, weight=1)
        return result

    @staticmethod
    def make_text(parent, **kwargs):
        import tkinter as tk
        from tkinter import ttk
        holder = ttk.Frame(parent)
        holder.pack(fill="both", expand=True)
        text = tk.Text(holder, background="#0b1119", foreground="#c9d6e5", font=("Monospace", 10),
                       state="disabled", borderwidth=0, padx=8, pady=8, **kwargs)
        scrollbar = ttk.Scrollbar(holder, command=text.yview)
        text.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        text.pack(fill="both", expand=True)
        return text

    @staticmethod
    def make_tree(parent, columns, widths, **kwargs):
        from tkinter import ttk
        holder = ttk.Frame(parent)
        holder.pack(fill="both", expand=True)
        tree = ttk.Treeview(holder, columns=columns, show="headings", **kwargs)
        for column, width in zip(columns, widths):
            tree.heading(column, text=column)
            tree.column(column, width=width, minwidth=45, stretch=column in {"Event", "Plugin", "Log"})
        scroll = ttk.Scrollbar(holder, command=tree.yview)
        horizontal = ttk.Scrollbar(holder, orient="horizontal", command=tree.xview)
        tree.configure(yscrollcommand=scroll.set, xscrollcommand=horizontal.set)
        scroll.pack(side="right", fill="y")
        horizontal.pack(side="bottom", fill="x")
        tree.pack(fill="both", expand=True)
        return tree

    def set_text(self, widget, text):
        if self.rendered.get(widget) == text:
            return
        self.rendered[widget] = text
        position = widget.yview()[0]
        widget.configure(state="normal")
        widget.delete("1.0", "end")
        widget.insert("end", text)
        widget.configure(state="disabled")
        widget.yview_moveto(position)

    def tick(self):
        view = read_state(self.args.state)
        self.connection.configure(text=view.status, foreground="#86efac" if view.status.startswith("Connected") else "#fcd58a")
        self.detail.configure(text=view.detail)
        snapshot = view.snapshot
        try:
            diagnostics = self.bridge_diagnostics(snapshot)
            amb = self.read_amb(diagnostics)
            if snapshot is None:
                from diagnostics import AmbView
                amb = AmbView("—")
        except ValueError:
            diagnostics = None
            from diagnostics import AmbView
            amb = AmbView("Invalid bridge data")
        summary, detail = self.operation_text(amb)
        self.overview["AMB"].configure(text=summary)
        self.overview["Game Context"].configure(text=self.safety_text(snapshot, diagnostics))
        self.overview["Plugins"].configure(text=self.health_text(diagnostics, self.lines))
        self.set_text(self.operation_detail, detail)
        trace = tuple((f"{e['elapsedMs'] / 1000:.1f}s", e["phase"], e["message"]) for e in (amb.session or {}).get("trace", []))
        if trace != self.last_trace:
            self.trace.delete(*self.trace.get_children())
            for row in trace:
                self.trace.insert("", "end", values=row)
            self.last_trace = trace
            if trace:
                self.trace.see(self.trace.get_children()[-1])
        errors, warnings, hitches = health_summary(self.lines)
        rows = []
        for plugin in diagnostics["plugins"] if diagnostics else []:
            name = plugin["internalName"]
            state = "API Mismatch" if plugin["isOutdated"] else "Banned" if plugin["isBanned"] else "Loaded" if plugin["isLoaded"] else "Not loaded"
            notes = []
            for title, values in (("errors", errors), ("warnings", warnings), ("slow", hitches)):
                if values.get(name):
                    notes.append(f"{values[name]} {title}")
            rows.append((state, plugin["name"] + (" (dev)" if plugin["isDev"] else ""), plugin["version"], plugin["apiLevel"], ", ".join(notes)))
        rows.sort(key=lambda row: (row[0] not in {"API Mismatch", "Banned"}, not row[-1], row[1].casefold()))
        if rows != self.last_plugins:
            self.plugins.delete(*self.plugins.get_children())
            for row in rows:
                self.plugins.insert("", "end", values=row)
            self.last_plugins = rows
        important = [format_line(line) for line in self.lines if not line.continuation and (line.level in {"ERR", "FTL"} or line.source == "HITCH")]
        self.set_text(self.health_detail, "\n".join(important[-15:]))
        values = {key: "—" for key in self.fields}
        if snapshot and snapshot["loggedIn"]:
            player = snapshot.get("player")
            if player:
                values["Player"] = f"{player['job']} · {player['world']}\n" + actor_text(player["actor"]) + f"\nHome: {player['homeWorld']}"
            location = snapshot.get("location")
            values["Location"] = (f"{location['name']}\nTerritory {location['territoryId']} · Map {location['mapId']} · Instance {location['instance']}\n\n" if location else "")
            values["Location"] += ", ".join(snapshot["conditions"]) or "None"
            values["Target"] = actor_text(snapshot.get("target"))
        for key, label in self.fields.items():
            label.configure(text=values[key])
        new = self.tail.poll()
        self.lines.extend(new)
        if new:
            self.sources.configure(values=("All Sources",) + tuple(sorted({line.source for line in self.lines if line.source})))
            if not self.paused:
                self.render_logs()
        self.update_log_status()
        self.timer = self.root.after(500, self.tick)

    def filtered(self):
        return visible_lines(self.lines, level=self.level.get(), scope=self.scope.get(), source=self.source.get(),
                             search=self.search.get(), hide_repository=self.hide_repository.get())

    def update_log_status(self):
        shown = len(self.filtered())
        text = f"{self.tail.status} · {shown} shown / {len(self.lines)} retained · {len(self.lines) - shown} filtered"
        if self.paused:
            text += " · paused"
        if self.metadata.get():
            text += f"\nLog: {self.args.log}\nState: {self.args.state}"
        self.log_status.configure(text=text)

    def render_logs(self):
        if self.paused:
            return
        position = self.log.yview()[0]
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        for line in self.filtered():
            self.log.insert("end", format_line(line, self.metadata.get()) + "\n", line.level)
        self.log.configure(state="disabled")
        if self.follow.get():
            self.log.see("end")
        else:
            self.log.yview_moveto(position)
        self.update_log_status()

    def toggle_pause(self):
        self.paused = not self.paused
        self.pause_button.configure(text="Resume" if self.paused else "Pause")
        if not self.paused:
            self.render_logs()
        self.update_log_status()

    def clear(self):
        self.lines.clear()
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")
        self.update_log_status()

    def close(self):
        if self.timer is not None:
            self.root.after_cancel(self.timer)
        self.root.destroy()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=Path, default=Path.home() / ".xlcore/logs/dalamud.log")
    parser.add_argument("--state", type=Path, default=Path.home() / ".xlcore/pluginConfigs/XivCompanionBridge/state.json")
    parser.add_argument("--state-once", action="store_true", help="Print game state.")
    parser.add_argument("--diagnostics-once", action="store_true", help="Print plugin and AMB diagnostics.")
    parser.add_argument("--logs-once", action="store_true", help="Print recent logs.")
    parser.add_argument("--scope", choices=("Relevant", "AMB", "Everything"), default="Relevant")
    parser.add_argument("--source", default="All Sources", help="Filter by source or plugin.")
    parser.add_argument("--level", choices=("All", "Info+", "Warnings+", "Errors"), default="Info+")
    parser.add_argument("--metadata", action="store_true", help="Show original log headers.")
    parser.add_argument("--include-repository", action="store_true", help="Include repository logs.")
    args = parser.parse_args(argv)
    if args.state_once:
        view = read_state(args.state)
        print(json.dumps({"status": view.status, "detail": view.detail, "snapshot": view.snapshot}, ensure_ascii=False))
        return 0 if view.snapshot is not None else 1
    if args.logs_once:
        tail = LogTail(args.log)
        for line in visible_lines(tail.poll(), level=args.level, scope=args.scope, source=args.source,
                                  hide_repository=not args.include_repository):
            print(format_line(line, args.metadata))
        return 0 if tail.identity is not None else 1
    if args.diagnostics_once:
        from diagnostics import bridge_diagnostics, read_amb, safety_text, health_text
        view = read_state(args.state)
        try:
            diagnostic = bridge_diagnostics(view.snapshot)
        except ValueError:
            diagnostic = None
        amb = read_amb(diagnostic)
        if view.snapshot is None:
            from diagnostics import AmbView
            amb = AmbView("—")
        print(json.dumps({"connection": view.status, "ambStatus": amb.status, "operation": amb.session,
                          "plugins": diagnostic["plugins"] if diagnostic else [],
                          "safety": safety_text(view.snapshot, diagnostic),
                          "health": health_text(diagnostic, LogTail(args.log).poll())}, ensure_ascii=False))
        return 0 if diagnostic is not None else 1
    import tkinter as tk
    root = tk.Tk()
    Dashboard(root, args)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
