using System;
using System.Linq;
using System.Windows.Media;

namespace OpenClawWinManager;

internal static class GatewayStatusColor
{
    private static readonly string[] FailureTokens =
    {
        "fail",
        "failed",
        "error",
        "denied",
        "invalid",
        "not found",
        "unreachable",
        "not running",
        "token required",
        "disconnected",
        "missing"
    };

    private static readonly string[] SuccessTokens =
    {
        "running",
        "started",
        "completed",
        "healthy",
        "reachable",
        "complete",
        "connected",
        "copied",
        "loaded"
    };

    private static readonly string[] WarningTokens =
    {
        "pending",
        "checking",
        "paused",
        "canceled",
        "cancelled",
        "stopped",
        "not configured",
        "not set",
        "waiting"
    };

    public static Brush GetStatusBrush(string? status)
    {
        var normalized = (status ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(normalized, FailureTokens))
        {
            return Brushes.Firebrick;
        }

        if (ContainsAny(normalized, WarningTokens))
        {
            return Brushes.DarkOrange;
        }

        if (ContainsAny(normalized, SuccessTokens))
        {
            return Brushes.SeaGreen;
        }

        return Brushes.DarkSlateGray;
    }

    public static Brush GetMonitorSummaryBrush(GatewayMonitorState? monitor)
    {
        if (monitor is null)
        {
            return Brushes.SlateGray;
        }

        if (monitor.TokenRequired)
        {
            return Brushes.IndianRed;
        }

        if (!monitor.IsMonitoring)
        {
            return Brushes.DarkOrange;
        }

        return monitor.IsHealthy switch
        {
            true => Brushes.SeaGreen,
            false => Brushes.Firebrick,
            _ => Brushes.SteelBlue
        };
    }

    public static Brush GetTokenStatusBrush(string? status)
    {
        var normalized = (status ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(normalized, FailureTokens))
        {
            return Brushes.Firebrick;
        }

        if (ContainsAny(normalized, WarningTokens))
        {
            return Brushes.DarkOrange;
        }

        if (normalized.Contains("loaded") || normalized.Contains("copied"))
        {
            return Brushes.SeaGreen;
        }

        return Brushes.DarkSlateGray;
    }

    public static string GetStatusIcon(string? status)
    {
        var normalized = (status ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(normalized, FailureTokens))
        {
            return "!!";
        }

        if (ContainsAny(normalized, WarningTokens))
        {
            return "--";
        }

        if (ContainsAny(normalized, SuccessTokens))
        {
            return "OK";
        }

        return "--";
    }

    public static string GetMonitorIcon(GatewayMonitorState? monitor)
    {
        if (monitor is null)
        {
            return "--";
        }

        if (monitor.TokenRequired)
        {
            return "!!";
        }

        if (!monitor.IsMonitoring)
        {
            return "--";
        }

        return monitor.IsHealthy switch
        {
            true => "OK",
            false => "!!",
            _ => "--"
        };
    }

    public static string GetTokenStatusIcon(string? status)
    {
        return GetStatusIcon(status);
    }

    private static bool ContainsAny(string text, string[] tokens)
    {
        return tokens.Any(text.Contains);
    }
}

