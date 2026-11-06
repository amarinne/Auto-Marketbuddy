"""Parse and filter Dalamud logs without changing the source file."""
from dataclasses import dataclass
import re

LEVELS = {
    "All": {"VRB", "DBG", "INF", "WRN", "ERR", "FTL"},
    "Info+": {"INF", "WRN", "ERR", "FTL"},
    "Warnings+": {"WRN", "ERR", "FTL"},
    "Errors": {"ERR", "FTL"},
}
HEADER = re.compile(
    r"^(?:(?P<time>\d{4}-\d\d-\d\d\s+\d\d:\d\d:\d\d(?:\.\d+)?\s+[+-]\d\d:\d\d)\s+"
    r"\[(?P<level>VRB|DBG|INF|WRN|ERR|FTL)\]|"
    r"\[(?P<short_time>\d\d:\d\d:\d\d(?:\.\d+)?)\s+(?P<short_level>VRB|DBG|INF|WRN|ERR|FTL)\])"
    r"\s*(?:\[(?P<source>[^\]]+)\]\s*)?(?P<message>.*)$"
)
AMB_NAMES = re.compile(r"\b(?:AutoMarketbuddy|Auto-Marketbuddy|XivCompanionBridge|XIV Companion)\b", re.I)
SLOW_CALLBACK = re.compile(r"(?:UiBuilder|Framework|Draw)\(([^)]+)\)", re.I)


@dataclass(frozen=True)
class LogLine:
    text: str
    level: str
    source: str = ""
    message: str = ""
    timestamp: str = ""
    context: str = ""
    continuation: bool = False


def parse_line(text: str, previous: LogLine | None = None) -> LogLine:
    match = HEADER.match(text)
    if match:
        return LogLine(text, match["level"] or match["short_level"], match["source"] or "",
                       match["message"], match["time"] or match["short_time"], match["message"])
    return LogLine(text, previous.level if previous else "INF", previous.source if previous else "",
                   text, previous.timestamp if previous else "", previous.context if previous else text,
                   previous is not None)


def visible_lines(lines, *, level="Info+", scope="Relevant", source="All Sources", search="", hide_repository=True):
    result = []
    for line in lines:
        if line.level not in LEVELS[level]:
            continue
        if source != "All Sources" and line.source != source:
            continue
        # Repository catalog warnings are normally maintenance noise. Errors remain visible.
        if hide_repository and source == "All Sources" and line.source == "PluginRepository" and line.level not in {"ERR", "FTL"}:
            continue
        own = bool(AMB_NAMES.search(line.source + " " + line.context))
        if scope == "Relevant" and source == "All Sources" and line.source in {"Profile", "ProfileManager"} and line.level not in {"WRN", "ERR", "FTL"}:
            continue
        if scope == "AMB" and not own:
            continue
        if scope == "Relevant" and source == "All Sources" and not (
            own or line.level in {"WRN", "ERR", "FTL"} or line.source == "HITCH" or not line.source
        ):
            continue
        if search.casefold() not in line.text.casefold():
            continue
        result.append(line)
    return result


def format_line(line: LogLine, metadata=False) -> str:
    if metadata:
        return line.text
    if line.continuation:
        return line.message
    severity = {"WRN": "Warning · ", "ERR": "Error · ", "FTL": "Fatal · "}.get(line.level, "")
    return severity + (line.source + " · " if line.source else "") + line.message


def health_summary(lines):
    """Counts describe only retained log lines, never an inferred current plugin failure."""
    errors, warnings, hitches = {}, {}, {}
    for line in lines:
        if line.continuation:
            continue
        source = line.source or "Unknown"
        if line.level in {"ERR", "FTL"}:
            errors[source] = errors.get(source, 0) + 1
        elif line.level == "WRN" and source != "PluginRepository":
            warnings[source] = warnings.get(source, 0) + 1
        if source == "HITCH":
            match = SLOW_CALLBACK.search(line.message)
            name = match[1] if match else "Unknown callback"
            hitches[name] = hitches.get(name, 0) + 1
    return errors, warnings, hitches
