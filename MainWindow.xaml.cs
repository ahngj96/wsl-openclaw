using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace OpenClawWinManager;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const string InstallerCommand =
        "curl -fsSL https://openclaw.ai/install.sh -o /tmp/openclaw-install.sh; chmod +x /tmp/openclaw-install.sh; bash /tmp/openclaw-install.sh --no-onboard --no-prompt --no-gum";
    private const int OpenClawDefaultPort = 18789;
    private static readonly TimeSpan GatewayHealthTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GatewayHealthInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] GatewayHealthPaths = new[] { "/health", "/healthz", "/api/health", "/" };
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = GatewayHealthTimeout
    };

    private bool _isBusy;
    private bool _distroExists;
    private bool _updatingDistroControls;
    private Process? _runningProcess;
    private bool _gatewayRunning;
    private bool _isMonitoring;
    private string? _gatewayToken;
    private int _gatewayPort = OpenClawDefaultPort;
    private CancellationTokenSource? _healthMonitorCancellation;
    private CancellationTokenSource? _gatewayCommandCancellation;
    private Task<CommandResult>? _gatewayCommandTask;
    private CancellationTokenSource? _operationCancellation;
    private static readonly Regex GatewayTokenFromUrlRegex = new(
        @"(?i)token=([A-Za-z0-9._-]{20,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayTokenFromLabelRegex = new(
        @"(?i)\b(?:dashboard|control|access|auth)\s*token\b[^A-Za-z0-9]{0,20}([A-Za-z0-9._-]{20,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayPortFromTextRegex = new(
        @"(?i)(?:--port\s+(?<port>\d{1,5})|\bport[:=]\s*(?<port>\d{1,5})|:(?<port>\d{1,5}))",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        UpdateInstallButtonState();
    }

    private string Distro => NormalizeCommandName(DistroTextBox.Text);
    private bool GatewayPortValid => TryParseGatewayPort(PortTextBox.Text, out _);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await PerformCheckAsync(string.Empty, autoSelectFirst: true);
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await PerformCheckAsync(Distro, autoSelectFirst: false);
    }

    private async Task PerformCheckAsync(string requestedDistro, bool autoSelectFirst)
    {
        var cts = BeginOperation("Checking WSL distros");
        string finalStatus = "Check complete.";
        bool progressSuccess = false;
        var selectedDistro = autoSelectFirst ? string.Empty : requestedDistro;

        try
        {
            var result = await RunWslCommandAsync("wsl.exe", new[] { "-l", "-q" }, cts.Token);

            if (result.Canceled)
            {
                finalStatus = "Check canceled.";
                return;
            }

            if (result.ExitCode != 0)
            {
                AppendLog($"wsl -l -q exited with code {result.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    AppendLog($"wsl -l -q stderr: {NormalizeLogText(result.StandardError)}");
                }

                finalStatus = "Check failed.";
                return;
            }

            var distros = GetWslLines(result.StandardOutput)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateDistroCandidates(distros, selectedDistro);

            if (distros.Count == 0)
            {
                finalStatus = "No WSL distro found.";
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    AppendLog($"wsl -l -q raw: {NormalizeLogText(result.StandardOutput)}");
                }

                AppendLog(finalStatus);
                return;
            }

            var selectedForValidation = Distro;
            var exists = !string.IsNullOrWhiteSpace(selectedForValidation) &&
                distros.Any(value => string.Equals(value, selectedForValidation, StringComparison.OrdinalIgnoreCase));

            _distroExists = exists;
            progressSuccess = exists;

            finalStatus = exists
                ? $"Distro '{selectedForValidation}' found."
                : $"Distro '{(string.IsNullOrWhiteSpace(selectedForValidation) ? "(not selected)" : selectedForValidation)}' not found. Select from the list.";

            AppendLog(finalStatus);
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Check canceled.";
            _distroExists = false;
        }
        catch (Exception ex)
        {
            AppendLog($"Check failed: {ex.Message}");
            finalStatus = "Check failed.";
            _distroExists = false;
        }
        finally
        {
            FinishOperation(finalStatus, progressSuccess);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!_distroExists)
        {
            AppendLog("Run Check first to confirm the distro exists.");
            SetStatus("Distro has not been checked.");
            return;
        }

        var distro = NormalizeCommandName(DistroTextBox.Text);
        if (string.IsNullOrWhiteSpace(distro))
        {
            AppendLog("Distro is required.");
            SetStatus("Distro required.");
            return;
        }

        if (!string.Equals(distro, DistroTextBox.Text, StringComparison.Ordinal))
        {
            _updatingDistroControls = true;
            try
            {
                DistroTextBox.Text = distro;
            }
            finally
            {
                _updatingDistroControls = false;
            }
        }

        var cts = BeginOperation($"Installing OpenClaw into '{distro}'");
        string finalStatus = "Install complete.";
        bool progressSuccess = false;

        try
        {
            var installArgs = BuildInstallArguments(distro).ToArray();
            AppendLog("Running install as root to avoid sudo prompts.");
            AppendLog($"Running: wsl.exe {FormatCommandForLog(installArgs)}");
            SetProgress(0, indeterminate: true);

            var installResult = await RunWslCommandAsync("wsl.exe", installArgs, cts.Token);
            if (installResult.Canceled)
            {
                finalStatus = "Install canceled.";
                return;
            }

            if (installResult.ExitCode != 0)
            {
                AppendLog($"Install script exited with code {installResult.ExitCode}.");
                finalStatus = "Install failed.";
                return;
            }

            SetProgress(70, indeterminate: true);
            SetStatus("Verifying OpenClaw installation.");
            var verifyResult = await RunWslCommandAsync(
                "wsl.exe",
                BuildVerifyArguments(distro).ToArray(),
                cts.Token);

            if (verifyResult.Canceled)
            {
                finalStatus = "Verification canceled.";
                return;
            }

            if (!VerifyVersion(verifyResult, out var version, out var verificationMessage))
            {
                AppendLog(verificationMessage);
                finalStatus = verificationMessage;
                return;
            }

            AppendLog($"openclaw --version: {version}");
            finalStatus = $"Installation complete. {version}.";
            progressSuccess = true;
            SetProgress(100, indeterminate: false);

            if (TryParseGatewayPort(PortTextBox.Text, out var defaultPort))
            {
                var gatewayStarted = await StartGatewayAsync(distro, defaultPort, cts.Token, verifyReachable: true);
                if (gatewayStarted)
                {
                    AppendLog($"Gateway started on port {defaultPort}.");
                    finalStatus += $" Gateway started on port {defaultPort}.";
                    StartGatewayHealthMonitoring(cts.Token);
                }
                else
                {
                    AppendLog("Install complete, but gateway start/health-check failed. You can start it manually.");
                    finalStatus += " Gateway is not running.";
                }
            }
            else
            {
                AppendLog("Invalid gateway port in UI. Gateway auto-start skipped.");
                finalStatus += " Gateway auto-start skipped (invalid port).";
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Install canceled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Install failed: {ex.Message}");
            finalStatus = "Install failed.";
        }
        finally
        {
            FinishOperation(finalStatus, progressSuccess);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Cancel requested.");
        if (_operationCancellation is null)
        {
            return;
        }

        SetStatus("Canceling...");
        TryKillProcess();
        _operationCancellation.Cancel();
    }

    private void DistroTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingDistroControls)
        {
            return;
        }

        var normalized = NormalizeCommandName(DistroTextBox.Text);
        if (!string.Equals(normalized, DistroTextBox.Text, StringComparison.Ordinal))
        {
            _updatingDistroControls = true;
            try
            {
                DistroTextBox.Text = normalized;
            }
            finally
            {
                _updatingDistroControls = false;
            }
        }

        _distroExists = false;
        UpdateInstallButtonState();
    }

    private void GatewayPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateInstallButtonState();
    }

    private void DistroListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDistroControls)
        {
            return;
        }

        var selected = DistroListBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selected))
        {
            _distroExists = false;
            UpdateInstallButtonState();
            return;
        }

        var normalized = NormalizeDistroValue(selected);

        if (!string.Equals(normalized, DistroTextBox.Text, StringComparison.Ordinal))
        {
            _updatingDistroControls = true;
            try
            {
                DistroTextBox.Text = normalized;
            }
            finally
            {
                _updatingDistroControls = false;
            }
        }

        _distroExists = true;
        UpdateInstallButtonState();
    }

    private async void StartGatewayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var distro = Distro;
        if (string.IsNullOrWhiteSpace(distro))
        {
            AppendLog("Distro is required.");
            SetStatus("Distro required.");
            return;
        }

        if (!TryParseGatewayPort(PortTextBox.Text, out var port))
        {
            AppendLog("Invalid gateway port. Enter 1~65535.");
            SetStatus("Invalid gateway port.");
            return;
        }

        var cts = BeginOperation($"Starting OpenClaw gateway on port {port}");

        try
        {
            var started = await StartGatewayAsync(distro, port, cts.Token);
            if (started)
            {
                SetStatus($"Gateway running on port {port}.");
                AppendLog($"Gateway running on port {port}.");
                StartGatewayHealthMonitoring(cts.Token);
            }
            else
            {
                SetStatus("Failed to start gateway.");
                AppendLog("Gateway start failed.");
            }
        }
        finally
        {
            FinishOperation(_gatewayRunning
                ? $"Gateway running on port {_gatewayPort}."
                : "Gateway start failed.", _gatewayRunning);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private async void StopGatewayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var distro = Distro;
        var cts = BeginOperation("Stopping OpenClaw gateway.");

        try
        {
            await StopGatewayAsync(distro, cts.Token);
            if (!_gatewayRunning)
            {
                SetStatus("Gateway stopped.");
                AppendLog("Gateway stopped.");
            }
            else
            {
                SetStatus("Failed to stop gateway.");
                AppendLog("Failed to stop gateway.");
            }
        }
        finally
        {
            FinishOperation(_gatewayRunning ? "Gateway still running." : "Gateway stopped.", !_gatewayRunning);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private void HealthMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_gatewayRunning && !_isMonitoring)
        {
            AppendLog("Gateway is not running. Start gateway first.");
            return;
        }

        if (_isMonitoring)
        {
            StopGatewayHealthMonitoring();
            SetStatus("Gateway health monitoring stopped.");
            AppendLog("Gateway health monitoring stopped.");
            return;
        }

        if (_isBusy)
        {
            return;
        }

        var started = StartGatewayHealthMonitoring(CancellationToken.None);
        if (started)
        {
            AppendLog("Gateway health monitoring started.");
        }
    }

    private CancellationTokenSource BeginOperation(string status)
    {
        _isBusy = true;
        _distroExists = _distroExists && !string.IsNullOrWhiteSpace(Distro);

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();

        SetStatus(status);
        AppendLog(status);
        InstallProgressBar.Value = 0;
        InstallProgressBar.IsIndeterminate = true;
        if (CheckButton is not null)
        {
            CheckButton.IsEnabled = false;
        }

        if (StartGatewayButton is not null)
        {
            StartGatewayButton.IsEnabled = false;
        }

        if (StopGatewayButton is not null)
        {
            StopGatewayButton.IsEnabled = false;
        }

        if (HealthMonitorButton is not null)
        {
            HealthMonitorButton.IsEnabled = false;
        }

        if (InstallButton is not null)
        {
            InstallButton.IsEnabled = false;
        }

        if (CancelButton is not null)
        {
            CancelButton.IsEnabled = true;
        }

        return _operationCancellation;
    }

    private void FinishOperation(string status, bool success)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }
        _isBusy = false;
        SetProgress(success ? 100 : 0, indeterminate: false);
        if (CheckButton is not null)
        {
            CheckButton.IsEnabled = true;
        }

        if (CancelButton is not null)
        {
            CancelButton.IsEnabled = false;
        }

        UpdateInstallButtonState();
        _runningProcess = null;
    }

    private void UpdateInstallButtonState()
    {
        if (InstallButton is null)
        {
            return;
        }

        InstallButton.IsEnabled = !_isBusy && _distroExists && !string.IsNullOrWhiteSpace(Distro);
        if (StartGatewayButton is not null)
        {
            StartGatewayButton.IsEnabled = !_isBusy
                && _distroExists
                && !string.IsNullOrWhiteSpace(Distro)
                && GatewayPortValid
                && !_gatewayRunning;
        }

        if (StopGatewayButton is not null)
        {
            StopGatewayButton.IsEnabled = !_isBusy && _gatewayRunning;
        }

        if (HealthMonitorButton is not null)
        {
            HealthMonitorButton.IsEnabled = !_isBusy && _gatewayRunning;
            HealthMonitorButton.Content = _isMonitoring ? "Stop Monitoring" : "Start Monitoring";
        }
    }

    private async Task<bool> StartGatewayAsync(string distro, int port, CancellationToken token, bool verifyReachable = false)
    {
        var normalizedDistro = NormalizeDistroValue(distro);
        if (string.IsNullOrWhiteSpace(normalizedDistro))
        {
            AppendLog("Cannot start gateway: distro is not set.");
            return false;
        }

        _gatewayToken = null;
        StopGatewayStream();

        var existingPort = await DetectRunningGatewayPortAsync(normalizedDistro, token).ConfigureAwait(false);
        if (existingPort.HasValue)
        {
            _gatewayPort = existingPort.Value;
            SetGatewayPortForMonitoring(existingPort.Value);
            _gatewayRunning = true;
            AppendLog($"Existing OpenClaw gateway detected on port {existingPort.Value}. Monitoring existing instance.");
            if (verifyReachable)
            {
                if (!await WaitForGatewayHealthyAsync(token).ConfigureAwait(false))
                {
                    AppendLog($"Existing gateway on port {existingPort.Value} is not reachable yet.");
                }
            }
            return true;
        }

        var startArgs = BuildGatewayStartArguments(normalizedDistro, port).ToArray();
        AppendLog($"Running: wsl.exe {FormatCommandForLog(startArgs)}");

        var startGatewayTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        _gatewayCommandCancellation = startGatewayTokenSource;
        var startToken = startGatewayTokenSource.Token;

        _gatewayCommandTask = RunWslCommandCapturedAsync(
            "wsl.exe",
            startArgs,
            line => AppendGatewayOutput(line, isError: false),
            line => AppendGatewayOutput(line, isError: true),
            startToken);

        try
        {
            await Task.Delay(300, startToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopGatewayStreamAsync(startToken);
            _gatewayRunning = false;
            return false;
        }

        if (_gatewayCommandTask.IsCompleted)
        {
            var startResult = await _gatewayCommandTask.ConfigureAwait(false);
            if (startResult.Canceled)
            {
                _gatewayRunning = false;
                StopGatewayStream();
                return false;
            }

            if (startResult.ExitCode != 0)
            {
                AppendLog($"Gateway start exited with code {startResult.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(startResult.StandardError))
                {
                    AppendLog($"Gateway start stderr: {NormalizeLogText(startResult.StandardError)}");
                }

                if (await TryAttachToRunningGatewayAsync(normalizedDistro, startToken).ConfigureAwait(false))
                {
                    return true;
                }

                _gatewayRunning = false;
                StopGatewayStream();
                return false;
            }
        }

        _gatewayRunning = true;
        _gatewayPort = port;

        if (!verifyReachable)
        {
            return true;
        }

        var reachable = await WaitForGatewayHealthyAsync(startToken);
        if (!reachable)
        {
            AppendLog($"Gateway did not become reachable at port {port}.");
            await StopGatewayStreamAsync(startToken);
            _gatewayRunning = false;
            return false;
        }

        if (_gatewayCommandTask.IsCompleted)
        {
            var finalResult = await _gatewayCommandTask.ConfigureAwait(false);
            if (finalResult.Canceled || finalResult.ExitCode != 0)
            {
                AppendLog($"Gateway command ended unexpectedly after startup (exit {finalResult.ExitCode}).");
                if (await TryAttachToRunningGatewayAsync(normalizedDistro, startToken).ConfigureAwait(false))
                {
                    return true;
                }

                _gatewayRunning = false;
                StopGatewayStream();
                return false;
            }
        }

        return true;
    }

    private async Task<bool> TryAttachToRunningGatewayAsync(string normalizedDistro, CancellationToken token)
    {
        var detectedPort = await DetectRunningGatewayPortAsync(normalizedDistro, token).ConfigureAwait(false);
        if (!detectedPort.HasValue)
        {
            return false;
        }

        _gatewayPort = detectedPort.Value;
        SetGatewayPortForMonitoring(detectedPort.Value);
        AppendLog($"Existing OpenClaw gateway detected on port {detectedPort.Value}. Monitoring existing instance.");
        _gatewayToken = null;
        _gatewayRunning = true;
        return true;
    }

    private static int? TryParseGatewayPortFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var matches = GatewayPortFromTextRegex.Matches(text);
        foreach (Match portMatch in matches)
        {
            if (!portMatch.Success)
            {
                continue;
            }

            var portCandidate = ExtractCapturedPort(portMatch.Groups["port"].Value);
            if (!portCandidate.HasValue)
            {
                continue;
            }

            if (portCandidate is < 1 or > 65535)
            {
                continue;
            }

            return portCandidate;
        }

        return null;
    }

    private static int? ExtractCapturedPort(string? value)
    {
        if (int.TryParse(value, out var parsed) && parsed is >= 1 and <= 65535)
        {
            return parsed;
        }

        return null;
    }

    private async Task<int?> DetectRunningGatewayPortAsync(string normalizedDistro, CancellationToken token)
    {
        var detectArgs = BuildGatewayPortProbeArguments(normalizedDistro).ToArray();
        var detectResult = await RunWslCommandAsync("wsl.exe", detectArgs, token).ConfigureAwait(false);
        if (detectResult.Canceled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(detectResult.StandardOutput))
        {
            return null;
        }

        return TryParseGatewayPortFromText(detectResult.StandardOutput)
            ?? TryParseGatewayPortFromText(detectResult.StandardError);
    }

    private void SetGatewayPortForMonitoring(int port)
    {
        if (Dispatcher.CheckAccess())
        {
            PortTextBox.Text = port.ToString(CultureInfo.InvariantCulture);
            return;
        }

        Dispatcher.BeginInvoke(() => SetGatewayPortForMonitoring(port));
    }

    private async Task<bool> StopGatewayAsync(string distro, CancellationToken token)
    {
        var normalizedDistro = NormalizeDistroValue(distro);
        if (string.IsNullOrWhiteSpace(normalizedDistro))
        {
            AppendLog("Cannot stop gateway: distro is not set.");
            return false;
        }

        await StopGatewayStreamAsync(token);
        StopGatewayHealthMonitoring();
        var stopArgs = BuildGatewayStopArguments(normalizedDistro).ToArray();
        AppendLog($"Running: wsl.exe {FormatCommandForLog(stopArgs)}");
        var stopResult = await RunWslCommandAsync("wsl.exe", stopArgs, token);
        if (stopResult.Canceled)
        {
            return false;
        }

        var statusExitCode = stopResult.ExitCode;
        if (statusExitCode != 0)
        {
            AppendLog($"Gateway stop command exited with code {statusExitCode}.");
            if (!string.IsNullOrWhiteSpace(stopResult.StandardError))
            {
                AppendLog($"Gateway stop stderr: {NormalizeLogText(stopResult.StandardError)}");
            }
        }

        var stillRunning = await IsGatewayProcessRunningAsync(normalizedDistro, token).ConfigureAwait(false);
        if (stillRunning)
        {
            AppendLog("Gateway is still running after stop request.");
            return false;
        }

        if (statusExitCode != 0)
        {
            AppendLog("Gateway stop command returned non-zero exit code, but no running gateway process was detected.");
        }

        _gatewayRunning = false;
        _gatewayToken = null;
        _gatewayPort = OpenClawDefaultPort;
        return true;
    }

    private void StopGatewayStream()
    {
        _gatewayCommandCancellation?.Cancel();
        _gatewayCommandCancellation?.Dispose();
        _gatewayCommandCancellation = null;
        _gatewayCommandTask = null;
    }

    private async Task StopGatewayStreamAsync(CancellationToken cancellationToken)
    {
        var commandTask = _gatewayCommandTask;
        if (commandTask is null || commandTask.IsCompleted)
        {
            StopGatewayStream();
            return;
        }

        _gatewayCommandCancellation?.Cancel();
        try
        {
            await commandTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        StopGatewayStream();
    }

    private void AppendGatewayOutput(string line, bool isError)
    {
        if (isError)
        {
            AppendLog($"[gateway][stderr] {line}");
        }
        else
        {
            AppendLog($"[gateway] {line}");
        }

        TryLogGatewayToken(line);
    }

    private void TryLogGatewayToken(string line)
    {
        if (!string.IsNullOrWhiteSpace(_gatewayToken))
        {
            return;
        }

        var token = ExtractGatewayToken(line);
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        _gatewayToken = token;
        AppendLog($"[gateway] Dashboard token. Copy this to Control UI settings: {token}");
    }

    private static string? ExtractGatewayToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var urlMatch = GatewayTokenFromUrlRegex.Match(line);
        if (urlMatch.Success)
        {
            return urlMatch.Groups[1].Value;
        }

        var labelMatch = GatewayTokenFromLabelRegex.Match(line);
        if (labelMatch.Success)
        {
            return labelMatch.Groups[1].Value;
        }

        return null;
    }

    private async Task<bool> IsGatewayProcessRunningAsync(string normalizedDistro, CancellationToken token)
    {
        var checkArgs = BuildGatewayProcessCheckArguments(normalizedDistro).ToArray();
        var checkResult = await RunWslCommandAsync("wsl.exe", checkArgs, token);
        if (checkResult.Canceled)
        {
            return false;
        }

        if (checkResult.ExitCode == 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(checkResult.StandardError))
        {
            AppendLog($"Gateway status check stderr: {NormalizeLogText(checkResult.StandardError)}");
        }

        return false;
    }

    private bool StartGatewayHealthMonitoring(CancellationToken token)
    {
        if (_isMonitoring)
        {
            AppendLog("Gateway monitoring already running.");
            return true;
        }

        if (!_gatewayRunning)
        {
            AppendLog("Gateway is not running. Start gateway first.");
            return false;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _healthMonitorCancellation?.Cancel();
        _healthMonitorCancellation = cancellation;
        _isMonitoring = true;
        UpdateInstallButtonState();

        _ = MonitorGatewayHealthLoopAsync(cancellation.Token);
        return true;
    }

    private async Task MonitorGatewayHealthLoopAsync(CancellationToken token)
    {
        var previousState = (bool?)null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var isHealthy = await CheckGatewayHealthAsync(token);
                if (!previousState.HasValue || previousState.Value != isHealthy)
                {
                    AppendLog(isHealthy
                        ? $"Gateway health check: healthy on port {_gatewayPort}."
                        : $"Gateway health check: unhealthy on port {_gatewayPort}.");
                    previousState = isHealthy;
                }

                if (!isHealthy && _gatewayRunning)
                {
                    SetStatus($"Gateway unhealthy on port {_gatewayPort}.");
                }
                else if (isHealthy)
                {
                    SetStatus($"Gateway healthy on port {_gatewayPort}.");
                }

                await Task.Delay(GatewayHealthInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _isMonitoring = false;
                UpdateInstallButtonState();
            }
        }
    }

    private void StopGatewayHealthMonitoring()
    {
        if (!_isMonitoring)
        {
            return;
        }

        _healthMonitorCancellation?.Cancel();
        _healthMonitorCancellation = null;
        _isMonitoring = false;
        UpdateInstallButtonState();
    }

    private async Task<bool> CheckGatewayHealthAsync(CancellationToken token)
    {
        foreach (var path in GatewayHealthPaths)
        {
            using var pathTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            pathTokenSource.CancelAfter(GatewayHealthTimeout);

            try
            {
                using var response = await HealthClient.GetAsync(
                    $"http://127.0.0.1:{_gatewayPort}{path}",
                    pathTokenSource.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Moved
                    || response.StatusCode == System.Net.HttpStatusCode.Found
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound
                    || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    throw;
                }
            }
            catch (HttpRequestException)
            {
            }
        }

        return false;
    }

    private async Task<bool> WaitForGatewayHealthyAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (await CheckGatewayHealthAsync(token).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }

        return false;
    }

    private static bool TryParseGatewayPort(string? value, out int port)
    {
        if (!int.TryParse(value?.Trim(), out port))
        {
            return false;
        }

        if (port < 1 || port > 65535)
        {
            return false;
        }

        return true;
    }

    private void SetStatus(string status)
    {
        if (Dispatcher.CheckAccess())
        {
            StatusLabel.Text = status;
            return;
        }

        Dispatcher.BeginInvoke(() => StatusLabel.Text = status);
    }

    private void SetProgress(int value, bool indeterminate)
    {
        if (Dispatcher.CheckAccess())
        {
            InstallProgressBar.IsIndeterminate = indeterminate;
            if (!indeterminate)
            {
                InstallProgressBar.Value = value;
            }

            return;
        }

        Dispatcher.BeginInvoke(() => SetProgress(value, indeterminate));
    }

    private void AppendLog(string text)
    {
        if (Dispatcher.CheckAccess())
        {
            LogsTextBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
            LogsTextBox.ScrollToEnd();
            return;
        }

        Dispatcher.BeginInvoke(() => AppendLog(text));
    }

    private async Task<CommandResult> RunWslCommandAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true
        };

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var normalizedArgument = NormalizeDistroArgument(argument, i > 0 && arguments[i - 1] == "-d");
            startInfo.ArgumentList.Add(normalizedArgument);
        }

        process.StartInfo = startInfo;

        _runningProcess = process;

        if (!process.Start())
        {
            _runningProcess = null;
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        var outputTask = CaptureStreamAsync(process.StandardOutput, standardOutput, false, token);
        var errorTask = CaptureStreamAsync(process.StandardError, standardError, true, token);

        using var registration = token.Register(TryKillProcess);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new CommandResult(
                process.ExitCode,
                standardOutput.ToString(),
                standardError.ToString(),
                false);
        }
        catch (OperationCanceledException)
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new CommandResult(-1, standardOutput.ToString(), standardError.ToString(), true);
        }
        finally
        {
            _runningProcess = null;
        }
    }

    private Task<CommandResult> RunWslCommandCapturedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? stdoutLogger,
        Action<string>? stderrLogger,
        CancellationToken token)
    {
        return RunWslCommandCapturedInternalAsync(
            fileName,
            arguments,
            stdoutLogger,
            stderrLogger,
            token);
    }

    private async Task<CommandResult> RunWslCommandCapturedInternalAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? stdoutLogger,
        Action<string>? stderrLogger,
        CancellationToken token)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true
        };

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var normalizedArgument = NormalizeDistroArgument(argument, i > 0 && arguments[i - 1] == "-d");
            startInfo.ArgumentList.Add(normalizedArgument);
        }

        process.StartInfo = startInfo;
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        var outputTask = CaptureStreamAsync(process.StandardOutput, standardOutput, false, token, stdoutLogger);
        var errorTask = CaptureStreamAsync(process.StandardError, standardError, true, token, stderrLogger);

        using var registration = token.Register(() => TryKillProcess(process));
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new CommandResult(
                process.ExitCode,
                standardOutput.ToString(),
                standardError.ToString(),
                false);
        }
        catch (OperationCanceledException)
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new CommandResult(-1, standardOutput.ToString(), standardError.ToString(), true);
        }
    }

    private async Task CaptureStreamAsync(
        StreamReader reader,
        StringBuilder output,
        bool isError,
        CancellationToken token,
        Action<string>? lineLogger = null)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            line = line.Replace("\0", string.Empty);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            output.AppendLine(line);
            if (lineLogger is null)
            {
                AppendLog(isError ? $"[stderr] {line}" : line);
                continue;
            }

            lineLogger(line);
        }
    }

    private void UpdateDistroCandidates(IReadOnlyList<string> distros, string requestedDistro)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyDistroCandidates(distros, requestedDistro);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyDistroCandidates(distros, requestedDistro));
    }

    private void ApplyDistroCandidates(IReadOnlyList<string> distros, string requestedDistro)
    {
        _updatingDistroControls = true;
        try
        {
            DistroListBox.Items.Clear();
            foreach (var value in distros.Select(NormalizeCommandName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                DistroListBox.Items.Add(value);
            }

            var normalizedRequested = NormalizeCommandName(requestedDistro);
            if (distros.Count == 0)
            {
                _distroExists = false;
                DistroListBox.SelectedIndex = -1;
                return;
            }

            var matched = DistroListBox.Items.Cast<string>().FirstOrDefault(
                value => string.Equals(value, normalizedRequested, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                DistroTextBox.Text = matched;
                DistroListBox.SelectedItem = matched;
                _distroExists = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(requestedDistro))
            {
                var first = DistroListBox.Items.Cast<string>().FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    DistroTextBox.Text = first;
                    DistroListBox.SelectedItem = first;
                    _distroExists = true;
                    return;
                }
            }

            DistroListBox.SelectedIndex = -1;
            _distroExists = false;
        }
        finally
        {
            _updatingDistroControls = false;
            UpdateInstallButtonState();
        }
    }

    private static IEnumerable<string> GetWslLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        output = output.Replace("\0", string.Empty)
            .Replace('\uFEFF', ' ')
            .Normalize(NormalizationForm.FormC);

        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanupDistroName)
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string CleanupDistroName(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var current in normalized.Normalize(NormalizationForm.FormC))
        {
            if (current == '\u00A0')
            {
                builder.Append(' ');
                continue;
            }

            var category = char.GetUnicodeCategory(current);
            if (category == UnicodeCategory.Control || category == UnicodeCategory.Format)
            {
                continue;
            }

            if (current == '\0')
            {
                continue;
            }

            if (char.IsWhiteSpace(current) && current != ' ')
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeCommandName(string value)
    {
        var cleaned = CleanupDistroName(value.Replace("\0", string.Empty));

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens.All(token => token.Length == 1))
        {
            return string.Concat(tokens);
        }

        if (tokens.Length >= 3 && tokens.Count(token => token.Length == 1) >= tokens.Length - 1)
        {
            return string.Concat(tokens);
        }

        return cleaned;
    }

    private static string NormalizeLogText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sanitized = text.Replace("\0", "\\0")
            .Replace('\uFEFF', ' ')
            .Normalize(NormalizationForm.FormC);

        return sanitized.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string BashSingleQuoted(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static IReadOnlyList<string> BuildInstallArguments(string distro)
    {
        var normalizedDistro = NormalizeDistroValue(distro);

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

    private static IReadOnlyList<string> BuildVerifyArguments(string distro)
    {
        var normalizedDistro = NormalizeDistroValue(distro);

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

    private static IReadOnlyList<string> BuildGatewayStartArguments(string distro, int port)
    {
        var normalizedDistro = NormalizeDistroValue(distro);
        var gatewayCommand = $"command -v openclaw >/dev/null || exit 1; "
            + "(command -v stdbuf >/dev/null 2>&1 && stdbuf -oL -eL openclaw gateway --allow-unconfigured --port "
            + $"{port} || openclaw gateway --allow-unconfigured --port {port})";

        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            gatewayCommand
        };
    }

    private static IReadOnlyList<string> BuildGatewayStopArguments(string distro)
    {
        var normalizedDistro = NormalizeDistroValue(distro);
        var stopCommand = "self=$$; "
            + "pid=0; "
            + "if [ -f /tmp/openclaw-gateway.pid ]; then "
            + "read -r pid < /tmp/openclaw-gateway.pid; "
            + "fi; "
            + "if [ -n \"$pid\" ] && kill -0 \"$pid\" 2>/dev/null; then "
            + "kill -15 \"$pid\" 2>/dev/null || true; "
            + "sleep 1; "
            + "if kill -0 \"$pid\" 2>/dev/null; then "
            + "kill -9 \"$pid\" 2>/dev/null || true; "
            + "fi; "
            + "fi; "
            + "openclaw gateway stop >/dev/null 2>&1 || true; "
            + "for p in $(pidof openclaw 2>/dev/null); do "
            + "cmd=\"$(ps -p \"$p\" -o args= 2>/dev/null)\"; "
            + "case \"$cmd\" in *\"openclaw gateway\"*|*\"openclaw-gateway\"*) "
            + "if [ \"$p\" != \"$self\" ]; then "
            + "kill -15 \"$p\" 2>/dev/null || true; "
            + "sleep 0.5; "
            + "if kill -0 \"$p\" 2>/dev/null; then "
            + "kill -9 \"$p\" 2>/dev/null || true; "
            + "fi; "
            + "fi;; "
            + "esac; "
            + "done; "
            + "rm -f /tmp/openclaw-gateway.pid; "
            + "exit 0";

        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            stopCommand
        };
    }

    private static IReadOnlyList<string> BuildGatewayPortProbeArguments(string distro)
    {
        var normalizedDistro = NormalizeDistroValue(distro);

        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            "for pid in $(pidof openclaw 2>/dev/null); do "
            + "args=\"$(ps -p \"$pid\" -o args= 2>/dev/null)\"; "
            + "case \"$args\" in *\"openclaw gateway\"*|*\"openclaw-gateway\"*) "
            + "echo \"$pid $args\"; "
            + ";; esac; "
            + "done"
        };
    }

    private static IReadOnlyList<string> BuildGatewayProcessCheckArguments(string distro)
    {
        var normalizedDistro = NormalizeDistroValue(distro);

        return new[]
        {
            "-d",
            normalizedDistro,
            "-u",
            "root",
            "--",
            "bash",
            "-lc",
            "if [ -f /tmp/openclaw-gateway.pid ]; then "
            + "read -r pid < /tmp/openclaw-gateway.pid; "
            + "if [ -n \"$pid\" ] && kill -0 \"$pid\" 2>/dev/null; then "
            + "exit 0; fi; fi; "
            + "for pid in $(pidof openclaw 2>/dev/null); do "
            + "cmd=\"$(ps -p \"$pid\" -o args= 2>/dev/null)\"; "
            + "case \"$cmd\" in *\"openclaw gateway\"*|*\"openclaw-gateway\"*) exit 0;; esac; "
            + "done; "
            + "exit 1"
        };
    }

    private static string FormatCommandForLog(IReadOnlyList<string> args)
    {
        return string.Join(
            " ",
            args.Select((arg, index) =>
            {
                var logArgument = index > 0 && args[index - 1] == "-d"
                    ? NormalizeDistroValue(arg)
                    : arg;

                return logArgument.Contains(' ')
                    ? $"\"{logArgument.Replace("\"", "\\\"")}\""
                    : logArgument;
            }));
    }

    private static bool VerifyVersion(CommandResult result, out string version, out string message)
    {
        version = string.Empty;
        message = "Verification failed.";

        if (result.ExitCode != 0)
        {
            message = $"openclaw --version command failed (exit {result.ExitCode}).";
            return false;
        }

        var output = GetWslLines(result.StandardOutput).LastOrDefault();
        if (string.IsNullOrWhiteSpace(output))
        {
            message = "openclaw --version returned no output.";
            return false;
        }

        version = output;
        message = string.Empty;
        return true;
    }

    private static string NormalizeDistroValue(string value)
    {
        var cleaned = NormalizeCommandName(value);
        return cleaned;
    }

    private static string NormalizeDistroArgument(string argument, bool isDistroArgument)
    {
        if (!isDistroArgument)
        {
            return argument;
        }

        return NormalizeDistroValue(argument);
    }

    private void TryKillProcess()
    {
        if (_runningProcess is null || _runningProcess.HasExited)
        {
            return;
        }

        try
        {
            _runningProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to kill running process: {ex.Message}");
        }
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError, bool Canceled);
}
