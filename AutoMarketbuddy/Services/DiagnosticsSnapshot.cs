using System;

namespace AutoMarketbuddy.Services;

public sealed record DiagnosticsSnapshot(int SchemaVersion, DateTimeOffset CapturedAt,
    string PluginVersion, SessionDiagnostics Session)
{
    // This endpoint only returns copied managed state. It exposes no action or control calls.
    public const string Endpoint = "AutoMarketbuddy.Diagnostics.V1";
}
