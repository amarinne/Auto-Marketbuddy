"""Validated, read-only presentation of bridge and AMB diagnostic contracts."""
from dataclasses import dataclass
from datetime import datetime, timezone
import json

from logmodel import health_summary

CONFLICTING = {"Marketbuddy", "AllaganMarket", "AutoRetainer"}
CONTEXT_FLAGS = {"InCombat", "WatchingCutscene", "WatchingCutscene78", "OccupiedInCutSceneEvent",
                 "BetweenAreas", "BetweenAreas51", "BoundByDuty", "BoundByDuty56", "BoundByDuty95"}


@dataclass(frozen=True)
class AmbView:
    status: str
    session: dict | None = None


def bridge_diagnostics(snapshot):
    if not snapshot or snapshot.get("diagnostics") is None:
        return None
    value = snapshot["diagnostics"]
    if not isinstance(value, dict) or not all(isinstance(value.get(k), str) for k in ("dalamudVersion", "ambStatus")):
        raise ValueError("Invalid bridge diagnostics")
    plugins = value.get("plugins")
    windows = value.get("visibleWindows")
    if not isinstance(plugins, list) or len(plugins) > 512 or not isinstance(windows, list) or not all(isinstance(x, str) for x in windows):
        raise ValueError("Invalid plugin/window list")
    for plugin in plugins:
        if not isinstance(plugin, dict) or not all(isinstance(plugin.get(k), str) for k in ("name", "internalName", "version")):
            raise ValueError("Invalid plugin identity")
        if type(plugin.get("apiLevel")) is not int or not all(type(plugin.get(k)) is bool for k in ("isLoaded", "isOutdated", "isBanned", "isDev")):
            raise ValueError("Invalid plugin state")
    if value.get("ambSnapshot") is not None and not isinstance(value["ambSnapshot"], str):
        raise ValueError("Invalid AMB envelope")
    return value


def read_amb(diagnostics, now=None):
    if diagnostics is None:
        return AmbView("Update bridge to 0.2")
    raw = diagnostics.get("ambSnapshot")
    if raw is None:
        return AmbView(diagnostics["ambStatus"])
    try:
        if len(raw) > 48_000:
            raise ValueError("Oversized AMB snapshot")
        value = json.loads(raw)
        if value.get("schemaVersion") != 1:
            return AmbView("Update AMB diagnostics")
        captured = datetime.fromisoformat(value["capturedAt"].replace("Z", "+00:00"))
        if captured.tzinfo is None:
            raise ValueError("Missing timezone")
        age = ((now or datetime.now(timezone.utc)) - captured).total_seconds()
        if age > 3 or age < -3:
            return AmbView("AMB data stale or clock mismatch")
        session = value["session"]
        if not isinstance(session, dict) or type(session.get("isRunning")) is not bool:
            raise ValueError("Invalid session")
        for key in ("runId", "mode", "phase", "status", "waitReason", "confirmation", "lastError"):
            if not isinstance(session.get(key), str):
                raise ValueError("Invalid operation text")
        for key in ("completed", "total", "undercut"):
            if type(session.get(key)) is not int:
                raise ValueError("Invalid operation count")
        if not isinstance(session.get("comparisonSource", "In-game"), str) or type(session.get("cachedItems", 0)) is not int or session.get("cachedItems", 0) < 0:
            raise ValueError("Invalid comparison cache state")
        for key in ("timeoutRemainingMs", "delayRemainingMs", "quoteAgeMs"):
            if session.get(key) is not None and (type(session[key]) is not int or session[key] < 0):
                raise ValueError("Invalid operation timing")
        item = session.get("item")
        if item is not None and (not isinstance(item, dict) or not isinstance(item.get("retainerId"), str)
                                 or type(item.get("isHq")) is not bool or not all(type(item.get(k)) is int for k in ("worldId", "slot", "itemId", "quantity", "price"))):
            raise ValueError("Invalid listing target")
        decision = session.get("decision")
        if decision is not None:
            if not isinstance(decision, dict) or not isinstance(decision.get("reason"), str):
                raise ValueError("Invalid decision")
            if decision.get("price") is not None and type(decision["price"]) is not int:
                raise ValueError("Invalid price")
            evidence = decision.get("evidence")
            if not isinstance(evidence, dict) or not all(type(evidence.get(k)) is int for k in ("received", "eligible", "wrongItem", "wrongQuality", "mannequin", "ownRetainer", "invalid")):
                raise ValueError("Invalid pricing evidence")
            if evidence.get("lowestCompetingPrice") is not None and type(evidence["lowestCompetingPrice"]) is not int:
                raise ValueError("Invalid competing price")
        trace = session.get("trace")
        if not isinstance(trace, list) or len(trace) > 30:
            raise ValueError("Invalid trace")
        for event in trace:
            if not isinstance(event, dict) or type(event.get("elapsedMs")) is not int or not all(isinstance(event.get(k), str) for k in ("phase", "message")):
                raise ValueError("Invalid trace event")
        return AmbView("Running" if session["isRunning"] else session["status"], session)
    except (ValueError, KeyError, TypeError, AttributeError):
        return AmbView("Invalid AMB data")


def operation_text(amb):
    session = amb.session
    if session is None:
        return amb.status, ""
    mode = session["mode"]
    timing = ""
    if session["timeoutRemainingMs"] is not None:
        timing = f"\nTimeout {session['timeoutRemainingMs'] / 1000:.1f}s · delay {(session.get('delayRemainingMs') or 0) / 1000:.1f}s"
    summary = (f"{mode} · {session['phase']} · {session['completed']}/{session['total']} listings\n"
               f"{session['waitReason']}{timing}")
    detail = []
    if session["status"] not in {session["waitReason"], session["lastError"]}:
        detail.append(session["status"])
    if session["lastError"]:
        detail.append("Stopped: " + session["lastError"])
    item = session.get("item")
    if item:
        detail.append(f"Retainer 0x{item['retainerId']} · world #{item['worldId']} · slot {item['slot'] + 1}")
        detail.append(f"Item #{item['itemId']} · {'HQ' if item['isHq'] else 'NQ'} · qty {item['quantity']} · unit price {item['price']:,} gil")
    decision = session.get("decision")
    if decision:
        evidence = decision["evidence"]
        lowest = evidence["lowestCompetingPrice"]
        proposed = decision["price"]
        age = session.get("quoteAgeMs")
        source = session.get("comparisonSource", "In-game")
        detail.append(f"{source} · {age / 1000:.1f}s old" if age is not None else f"{source} · age unknown")
        detail.append(f"Eligible {evidence['eligible']}/{evidence['received']} · excluded: item {evidence['wrongItem']}, quality {evidence['wrongQuality']}, own retainer {evidence['ownRetainer']}, mannequin {evidence['mannequin']}, invalid {evidence['invalid']}")
        detail.append(f"Lowest {lowest if lowest is not None else 'none'} · undercut {session['undercut']} · proposed {proposed if proposed is not None else 'unchanged'} gil")
        detail.append(decision["reason"])
    detail.append("Confirmation: " + session["confirmation"])
    if session["isRunning"]:
        detail.append("Stop: /aundercut stop")
    return summary, "\n".join(detail)


def safety_text(snapshot, diagnostics):
    if snapshot is None:
        return "—"
    result = []
    if not snapshot["loggedIn"]:
        result.append("Logged out")
    flags = [flag for flag in snapshot["conditions"] if flag in CONTEXT_FLAGS]
    result.append("Conditions: " + (", ".join(flags) or "None"))
    if diagnostics is None:
        result.append("Update bridge to 0.2")
    else:
        windows = diagnostics["visibleWindows"]
        competing = [p["internalName"] for p in diagnostics["plugins"] if p["isLoaded"] and p["internalName"] in CONFLICTING]
        result.append("Retainer automation: " + (", ".join(competing) or "None"))
        result.append("Market windows: " + (", ".join(windows) or "None"))
        if "SelectYesno" in windows:
            result.append("Confirmation owner: Unknown")
    return "\n".join(result)


def health_text(diagnostics, lines):
    errors, warnings, hitches = health_summary(lines)
    result = [f"Log: {sum(errors.values())} errors · {sum(warnings.values())} warnings · {sum(hitches.values())} slow callbacks"]
    if diagnostics:
        plugins = diagnostics["plugins"]
        result.insert(0, f"{sum(p['isLoaded'] for p in plugins)}/{len(plugins)} loaded")
        for label, flag in (("API mismatch", "isOutdated"), ("Banned", "isBanned")):
            names = [p["internalName"] for p in plugins if p[flag]]
            if names:
                result.append(label + ": " + ", ".join(names[:3]) + (f" +{len(names) - 3}" if len(names) > 3 else ""))
    else:
        result.append("Inventory unavailable")
    if errors:
        result.append("Errors: " + ", ".join(f"{name} ({count})" for name, count in list(errors.items())[:3]))
    if hitches:
        result.append("Slow callbacks: " + ", ".join(f"{name} ({count})" for name, count in list(hitches.items())[:3]))
    return "\n".join(result)
