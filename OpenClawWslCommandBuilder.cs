using System;
using System.Collections.Generic;

namespace OpenClawWinManager;

internal static class OpenClawWslCommandBuilder
{
    private const string InstallerCommand =
        "curl -fsSL https://openclaw.ai/install.sh -o /tmp/openclaw-install.sh; chmod +x /tmp/openclaw-install.sh; bash /tmp/openclaw-install.sh --no-onboard --no-prompt --no-gum";

    public static IReadOnlyList<string> BuildInstallArguments(string normalizedDistro)
    {
        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            InstallerCommand
        };
    }

    public static IReadOnlyList<string> BuildVerifyArguments(string normalizedDistro)
    {
        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            "command -v openclaw && openclaw --version"
        };
    }

    public static IReadOnlyList<string> BuildGatewayStartArguments(string normalizedDistro, int port)
    {
        var gatewayCommand = "command -v openclaw >/dev/null || exit 1; "
            + "(command -v stdbuf >/dev/null 2>&1 && stdbuf -oL -eL openclaw gateway --allow-unconfigured --port "
            + $"{port} || openclaw gateway --allow-unconfigured --port {port})";

        return new[]
        {
            "-d",
            normalizedDistro,
            "--",
            "bash",
            "-lc",
            gatewayCommand
        };
    }

    public static IReadOnlyList<string> BuildGatewayTokenConfigArguments(string normalizedDistro)
    {
        return new[]
        {
            "-d",
            normalizedDistro,
            "--",
            "bash",
            "-lc",
            "openclaw config get gateway.auth.token"
        };
    }

    public static string BuildOpenClawOnboardCommand(string normalizedDistro)
    {
        var wslDistroArg = PowerShellSingleQuoted(normalizedDistro);
        return $"& wsl.exe -d {wslDistroArg} -- bash -lc {BashSingleQuoted("openclaw onboard")}";
    }

    public static IReadOnlyList<string> BuildGatewayStopArguments(string normalizedDistro)
    {
        var stopCommand = "openclaw gateway stop >/dev/null 2>&1 || true; "
            + "if command -v systemctl >/dev/null 2>&1; then systemctl --user stop openclaw-gateway.service >/dev/null 2>&1 || true; fi; "
            + "pkill -TERM -f \"openclaw-gateway\" 2>/dev/null || true; "
            + "pkill -TERM -f \"openclaw gateway\" 2>/dev/null || true; "
            + "pkill -TERM -f \"openclaw\" 2>/dev/null || true; "
            + "sleep 1; "
            + "pkill -KILL -f \"openclaw-gateway\" 2>/dev/null || true; "
            + "pkill -KILL -f \"openclaw gateway\" 2>/dev/null || true; "
            + "pkill -KILL -f \"openclaw\" 2>/dev/null || true; "
            + "rm -f /tmp/openclaw-gateway.pid; "
            + "exit 0";

        return new[]
        {
            "-d",
            normalizedDistro,
            "--",
            "bash",
            "-lc",
            stopCommand
        };
    }

    private static string BashSingleQuoted(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static string PowerShellSingleQuoted(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }
}
