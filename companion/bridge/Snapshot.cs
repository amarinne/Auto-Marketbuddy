using System;

namespace XivCompanionBridge;

public sealed record Position(float X, float Y, float Z);
public sealed record Actor(string Name, string Kind, byte? Level, uint? Hp, uint? MaxHp,
    uint? Mp, uint? MaxMp, Position Position, bool IsCasting, uint? CastActionId,
    float? CastElapsed, float? CastTotal);
public sealed record Player(Actor Actor, string World, string HomeWorld, string Job);
public sealed record Location(uint TerritoryId, string Name, uint MapId, uint Instance);
public sealed record PluginHealth(string Name, string InternalName, string Version, int ApiLevel,
    bool IsLoaded, bool IsOutdated, bool IsBanned, bool IsDev);
public sealed record Diagnostics(string DalamudVersion, PluginHealth[] Plugins, string[] VisibleWindows,
    string AmbStatus, string? AmbSnapshot);
public sealed record Snapshot(int SchemaVersion, DateTimeOffset CapturedAt, long Sequence,
    string BridgeVersion, bool BridgeActive, bool LoggedIn, Player? Player, Actor? Target,
    Location? Location, string[] Conditions, string? Error = null, Diagnostics? Diagnostics = null)
{
    public static Snapshot Empty(long sequence, bool active, string? error = null) =>
        new(1, DateTimeOffset.UtcNow, sequence, "0.2.0.0", active, false, null, null, null, [], error);
}
