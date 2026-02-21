namespace OpenClawWinManager;

internal sealed record GatewayMonitorState(
    int Port,
    bool IsMonitoring,
    bool? IsHealthy,
    DateTime? LastChecked,
    string? MonitorToken,
    bool TokenRequired,
    string? TokenStatusMessage,
    string? HealthStatusMessage);
