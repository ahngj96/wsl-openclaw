using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;

namespace OpenClawWinManager;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int OpenClawDefaultPort = 18789;
    private const string RootCommandContext = "root";
    private const string UserCommandContext = "user";
    private const string SettingsFileName = "settings.json";
    private static readonly TimeSpan GatewayHealthTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GatewayHealthInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] GatewayHealthPaths = new[] { "/health", "/healthz", "/api/health", "/" };
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawWinManager",
        SettingsFileName);
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = GatewayHealthTimeout
    };

    private bool _isBusy;
    private bool _distroExists;
    private bool _updatingDistroControls;
    private Process? _runningProcess;
    private bool _gatewayRunning;
    private readonly object _gatewayMonitorLock = new();
    private GatewayMonitorState? _gatewayMonitor;
    private bool _isMonitoring;
    private string? _gatewayToken;
    private int _gatewayPort = OpenClawDefaultPort;
    private CancellationTokenSource? _healthMonitorCancellation;
    private CancellationTokenSource? _gatewayCommandCancellation;
    private Task<CommandResult>? _gatewayCommandTask;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _onboardClipboardCancellation;
    private bool _isLoadingSettings;
    private static readonly Regex GatewayTokenFromUrlRegex = new(
        @"(?i)(?:token|access_token)=([A-Za-z0-9._%/\-+=]{12,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayTokenFromLabelRegex = new(
        @"(?i)\b(?:dashboard|control(?:\s+ui)?|access|auth|authorization|openclaw|gateway).{0,40}?\btoken\b[^A-Za-z0-9]{0,24}([A-Za-z0-9._%/\-+=]{12,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayTokenFromJsonRegex = new(
        @"(?i)""(?:dashboard|control|access_token|token)""[\s:=""']+([A-Za-z0-9._%/\-+=]{12,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayTokenFromAnyTokenParamRegex = new(
        @"(?i)[?&](?:token|access_token)=([A-Za-z0-9._%/\-+=]{12,})",
        RegexOptions.Compiled);
    private static readonly Regex GatewayPlainTokenCandidateRegex = new(
        @"^[A-Za-z0-9._%/\-+=]{12,}$",
        RegexOptions.Compiled);
    private static readonly Regex GatewayAlreadyRunningOutputRegex = new(
        @"(?i)\b(already\s+running|already\s+in\s+use|lock\s+timeout)\b",
        RegexOptions.Compiled);
    private static readonly Regex GatewayTokenMissingFromGatewayLogRegex = new(
        @"(?i)(?:disconnected\s*\(\s*1008\s*\)\s*:\s*unauthorized:\s*gateway\s+token\s+missing|reason\s*=\s*token_missing|code\s*=\s*4008)",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        _isLoadingSettings = true;
        LoadSettings();
        UpdateGatewayMonitorViews();
        UpdateInstallButtonState();
        if (!string.IsNullOrWhiteSpace(_gatewayToken))
        {
            UpdateGatewayTokenDisplay(
                _gatewayToken,
                "저장된 토큰을 불러왔습니다. 필요한 경우 Control UI에 붙여 넣으세요.");
        }
        else
        {
            UpdateGatewayTokenDisplay(
                GatewayTokenTextBox.Text,
                "토큰이 필요하면 'Onboard'를 실행하고 발급된 토큰을 복사해 주세요.");
        }

        _isLoadingSettings = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopOnboardClipboardMonitoring();
        SaveSettings();
        base.OnClosed(e);
    }

    private string Distro => NormalizeCommandName(DistroTextBox.Text);
    private bool GatewayPortValid => TryParseGatewayPort(PortTextBox.Text, out _);
    private GatewayMonitorState? GatewayMonitor
    {
        get
        {
            lock (_gatewayMonitorLock)
            {
                return _gatewayMonitor;
            }
        }
    }

    private void LoadSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJsonOptions);
            if (settings is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(settings.Distro))
            {
                _updatingDistroControls = true;
                try
                {
                    DistroTextBox.Text = settings.Distro;
                }
                finally
                {
                    _updatingDistroControls = false;
                }
            }

            if (settings.GatewayPort is >= 1 and <= 65535)
            {
                PortTextBox.Text = settings.GatewayPort.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (settings.MonitorPort is >= 1 and <= 65535)
            {
                MonitorPortTextBox.Text = settings.MonitorPort.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(settings.GatewayToken))
            {
                _gatewayToken = settings.GatewayToken;
                UpdateGatewayTokenDisplay(
                    settings.GatewayToken,
                    "저장된 토큰을 불러왔습니다. 필요한 경우 Control UI에 붙여 넣으세요.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings || !Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SaveSettings);
            return;
        }

        try
        {
            var tokenToSave = _gatewayToken;
            if (string.IsNullOrWhiteSpace(tokenToSave) && File.Exists(SettingsFilePath))
            {
                try
                {
                    var existingJson = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    var existingSettings = JsonSerializer.Deserialize<AppSettings>(existingJson, SettingsJsonOptions);
                    tokenToSave = existingSettings?.GatewayToken;
                }
                catch
                {
                }
            }

            var settings = new AppSettings
            {
                Distro = NormalizeCommandName(DistroTextBox.Text),
                GatewayPort = TryParseGatewayPort(PortTextBox.Text, out var gatewayPort)
                    ? gatewayPort
                    : null,
                MonitorPort = TryParseGatewayPort(MonitorPortTextBox.Text, out var monitorPort)
                    ? monitorPort
                    : null,
                GatewayToken = string.IsNullOrWhiteSpace(tokenToSave) ? null : tokenToSave
            };

            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SettingsJsonOptions);
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to save settings: {ex.Message}");
        }
    }

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

            if (exists)
            {
                AppendLog("Check complete.");
            }
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

    private async Task<bool> EnsureOpenClawInstalledAsync(string distro, CancellationToken token)
    {
        var verifyArgs = OpenClawWslCommandBuilder.BuildVerifyArgumentsAsRoot(distro).ToArray();
        AppendLog("Checking if OpenClaw is already installed.");
        AppendWslCommandLog(RootCommandContext, verifyArgs);
        var verifyResult = await RunWslCommandAsync("wsl.exe", verifyArgs, token);
        if (token.IsCancellationRequested)
        {
            return false;
        }

        if (verifyResult.ExitCode == 0
            && VerifyVersion(verifyResult, out var existingVersion, out _))
        {
            AppendLog($"OpenClaw already installed: {existingVersion}.");
            return true;
        }

        AppendLog("OpenClaw not detected. Running install.");
        AppendLog("Running install as root to avoid sudo prompts.");

        var installArgs = OpenClawWslCommandBuilder.BuildInstallArguments(distro).ToArray();
        AppendWslCommandLog(RootCommandContext, installArgs);
        SetProgress(20, indeterminate: true);

        var installResult = await RunWslCommandAsync("wsl.exe", installArgs, token);
        if (installResult.Canceled)
        {
            AppendLog("Install canceled.");
            return false;
        }

        if (installResult.ExitCode != 0)
        {
            AppendLog($"Install script exited with code {installResult.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(installResult.StandardError))
            {
                AppendLog($"Install stderr: {NormalizeLogText(installResult.StandardError)}");
            }

            return false;
        }

        SetProgress(60, indeterminate: true);
        AppendLog("Verifying OpenClaw installation after install.");
        AppendWslCommandLog(RootCommandContext, verifyArgs);
        verifyResult = await RunWslCommandAsync("wsl.exe", verifyArgs, token);
        if (verifyResult.Canceled)
        {
            AppendLog("Verification canceled.");
            return false;
        }

        if (!VerifyVersion(verifyResult, out var version, out var verificationMessage))
        {
            AppendLog(verificationMessage);
            return false;
        }

        AppendLog($"OpenClaw installed: {version}");
        return true;
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
        if (!_isLoadingSettings)
        {
            SaveSettings();
        }
    }

    private void GatewayPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateInstallButtonState();
        if (!_isLoadingSettings)
        {
            SaveSettings();
        }
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
        var finalStatus = "Gateway start failed.";
        var success = false;

        try
        {
            var started = await StartGatewayAsync(distro, port, cts.Token);
            if (started)
            {
                SetStatus($"Gateway running on port {_gatewayPort}.");
                AppendLog($"Gateway running on port {_gatewayPort}.");
                finalStatus = $"Gateway running on port {_gatewayPort}.";
                success = true;
            }
            else
            {
                SetStatus("Failed to start gateway.");
                AppendLog("Gateway start failed.");
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Gateway start canceled.";
            SetStatus(finalStatus);
            AppendLog(finalStatus);
        }
        catch (Exception ex)
        {
            finalStatus = $"Gateway start failed: {ex.Message}";
            SetStatus(finalStatus);
            AppendLog(finalStatus);
        }
        finally
        {
            FinishOperation(finalStatus, success);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private async void OnboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var distro = NormalizeCommandName(DistroTextBox.Text);
        if (string.IsNullOrWhiteSpace(distro))
        {
            AppendLog("Distro is required for onboarding.");
            SetStatus("Distro required.");
            return;
        }

        var normalizedDistro = NormalizeDistroValue(distro);
        if (string.IsNullOrWhiteSpace(normalizedDistro))
        {
            AppendLog("Distro name is invalid.");
            SetStatus("Distro invalid.");
            return;
        }

        var cts = BeginOperation($"Preparing OpenClaw onboarding for '{normalizedDistro}'");
        var finalStatus = "Onboard failed.";
        var opened = false;

        try
        {
            if (!await EnsureOpenClawInstalledAsync(normalizedDistro, cts.Token).ConfigureAwait(true))
            {
                finalStatus = "Install step failed. Cannot start onboard.";
                return;
            }

            var terminalCommand = OpenClawWslCommandBuilder.BuildOpenClawOnboardCommand(normalizedDistro);
            AppendLog($"[{UserCommandContext}] Opening OpenClaw onboard terminal for '{normalizedDistro}'.");
            AppendLog($"[{UserCommandContext}] Running: {terminalCommand}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-Command \"{terminalCommand}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                }
            };

            process.Start();
            SetStatus("OpenClaw onboard terminal opened.");
            AppendLog("Complete onboarding in the terminal. Token will be read from config after the command exits.");
            UpdateGatewayTokenDisplay(
                GatewayTokenTextBox.Text,
                "온보드 완료 후 토큰을 자동 조회합니다...");
            opened = true;
            finalStatus = "Onboard terminal opened.";
            _ = MonitorOnboardCompletionAsync(process, normalizedDistro);
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Onboard canceled.";
            SetStatus(finalStatus);
        }
        catch (Exception ex)
        {
            finalStatus = "Failed to open onboard terminal.";
            AppendLog($"Failed to open onboard terminal: {ex.Message}");
            SetStatus(finalStatus);
        }
        finally
        {
            FinishOperation(finalStatus, opened);
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
        var cts = BeginOperation("Stopping all OpenClaw processes.");

        try
        {
            var stopped = await StopGatewayAsync(distro, cts.Token);
            SetStatus(stopped ? "All OpenClaw processes stopped." : "OpenClaw stop request sent.");
            AppendLog(stopped ? "All OpenClaw processes stopped." : "OpenClaw stop command sent. Processes may still be shutting down.");
        }
        finally
        {
            var status = _gatewayRunning
                ? "OpenClaw is still running."
                : "All OpenClaw processes stopped.";
            FinishOperation(status, !_gatewayRunning);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
        }
    }

    private void AddMonitorPortButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseGatewayPort(MonitorPortTextBox.Text, out var port))
        {
            AppendLog("Invalid monitor port. Enter 1~65535.");
            SetStatus("Invalid monitor port.");
            return;
        }

        lock (_gatewayMonitorLock)
        {
            _gatewayMonitor = new GatewayMonitorState(
                Port: port,
                IsMonitoring: true,
                IsHealthy: null,
                LastChecked: null,
                MonitorToken: null,
                TokenRequired: false,
                TokenStatusMessage: null,
                HealthStatusMessage: null);
        }

        AppendLog($"Gateway monitor port set to {port}.");
        SetStatus($"Monitor port set to {port}.");
        UpdateGatewayMonitorViews();
        _ = CheckGatewayHealthAndRefreshAsync();
        SaveSettings();
        StartGatewayHealthMonitoring(CancellationToken.None);
    }

    private void HealthMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayMonitor is null)
        {
            AppendLog("No monitor port configured.");
            SetStatus("Set a monitor port first.");
            return;
        }

        if (_isMonitoring)
        {
            lock (_gatewayMonitorLock)
            {
                var monitor = _gatewayMonitor;
                if (monitor is null)
                {
                    return;
                }

                _gatewayMonitor = monitor with { IsMonitoring = false };
            }

            StopGatewayHealthMonitoring();
            SetStatus("Gateway health monitoring stopped.");
            AppendLog("Gateway health monitoring stopped.");
            return;
        }

        if (_isBusy)
        {
            return;
        }

        lock (_gatewayMonitorLock)
        {
            var monitor = _gatewayMonitor;
            if (monitor is null)
            {
                return;
            }

            _gatewayMonitor = monitor with { IsMonitoring = true };
        }

        var started = StartGatewayHealthMonitoring(CancellationToken.None);
        if (started)
        {
            AppendLog("Gateway health monitoring started.");
        }
    }

    private void CopyGatewayTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gatewayToken))
        {
            AppendLog("No gateway token detected yet.");
            UpdateGatewayTokenDisplay(
                GatewayTokenTextBox.Text,
                "토큰이 아직 감지되지 않았습니다. 게이트웨이를 시작하거나 OpenClaw를 다시 실행해 보세요.");
            SetStatus("No gateway token available.");
            return;
        }

        try
        {
            Clipboard.SetText(_gatewayToken);
            AppendLog("Dashboard token copied to clipboard.");
            UpdateGatewayTokenDisplay(_gatewayToken, "클립보드에 복사되었습니다. OpenClaw dashboard의 Control UI 설정에 붙여 넣으세요.");
            SetStatus("Gateway token copied.");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to copy token: {ex.Message}");
            UpdateGatewayTokenDisplay(
                _gatewayToken,
                $"클립보드 복사 실패: {ex.Message}");
            SetStatus("Failed to copy token.");
        }
    }

    private void PasteTokenFromClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                AppendLog("No text in clipboard.");
                SetStatus("No token in clipboard.");
                UpdateGatewayTokenDisplay(
                    GatewayTokenTextBox.Text,
                    "클립보드에 토큰 텍스트가 없습니다.");
                return;
            }

            var clipboardText = Clipboard.GetText();
            var token = ExtractGatewayToken(clipboardText);
            if (string.IsNullOrWhiteSpace(token))
            {
                token = ExtractPlainTokenCandidate(clipboardText);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                AppendLog("Clipboard text does not contain a token pattern.");
                SetStatus("Invalid token in clipboard.");
                UpdateGatewayTokenDisplay(
                    GatewayTokenTextBox.Text,
                    "클립보드 텍스트에서 토큰 형식을 찾지 못했습니다.");
                return;
            }

            _gatewayToken = token;
            UpdateGatewayMonitorStateForTokenStatus(token, tokenRequired: false);
            AppendLog("Gateway token pasted from clipboard.");
            UpdateGatewayTokenDisplay(token, "클립보드의 토큰을 적용했습니다. Control UI settings에 붙여 넣으세요.");
            SaveSettings();
            StopOnboardClipboardMonitoring();
            SetStatus("Gateway token pasted.");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to paste token: {ex.Message}");
            UpdateGatewayTokenDisplay(
                GatewayTokenTextBox.Text,
                $"클립보드 토큰 적용 실패: {ex.Message}");
            SetStatus("Failed to apply clipboard token.");
        }
    }

    private void StartOnboardClipboardMonitoring()
    {
        StopOnboardClipboardMonitoring();
        _onboardClipboardCancellation = new CancellationTokenSource();
        AppendLog("Listening for token in clipboard. Copy the token in the onboard terminal.");

        UpdateGatewayTokenDisplay(
            GatewayTokenTextBox.Text,
            "온보드 토큰 복사 대기 중...");

        _ = PollOnboardClipboardForTokenAsync(_onboardClipboardCancellation.Token);
    }

    private void StopOnboardClipboardMonitoring()
    {
        if (_onboardClipboardCancellation is null)
        {
            return;
        }

        _onboardClipboardCancellation.Cancel();
        _onboardClipboardCancellation.Dispose();
        _onboardClipboardCancellation = null;
    }

    private async Task PollOnboardClipboardForTokenAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        string? currentToken = null;

        try
        {
            while (!token.IsCancellationRequested && DateTime.UtcNow <= deadline)
            {
                var clipboardText = string.Empty;
                try
                {
                    clipboardText = await Dispatcher.InvokeAsync(
                        () =>
                        {
                            if (!Clipboard.ContainsText())
                            {
                                return string.Empty;
                            }

                            return Clipboard.GetText() ?? string.Empty;
                        }).Task;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    continue;
                }

                var tokenCandidate = string.IsNullOrWhiteSpace(clipboardText)
                    ? null
                    : ExtractGatewayToken(clipboardText) ?? ExtractPlainTokenCandidate(clipboardText);

                if (!string.IsNullOrWhiteSpace(tokenCandidate))
                {
                    if (!string.Equals(tokenCandidate, currentToken, StringComparison.Ordinal))
                    {
                        currentToken = tokenCandidate;
                        _gatewayToken = tokenCandidate;
                        UpdateGatewayMonitorStateForTokenStatus(tokenCandidate, tokenRequired: false);
                        UpdateGatewayTokenDisplay(
                            tokenCandidate,
                            "클립보드에서 토큰을 자동 감지했습니다. Control UI settings에 붙여 넣으세요.");
                        AppendLog("Gateway token was detected from clipboard automatically.");
                        SetStatus("Gateway token detected from clipboard.");
                        SaveSettings();
                    }

                    StopOnboardClipboardMonitoring();
                    break;
                }

                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard monitor stopped: {ex.Message}");
            SetStatus("Clipboard monitoring failed.");
        }
        finally
        {
            if (token.IsCancellationRequested == false)
            {
                    if (DateTime.UtcNow > deadline)
                    {
                        AppendLog("Clipboard token monitor timed out after 10 minutes.");
                        if (string.IsNullOrWhiteSpace(_gatewayToken))
                        {
                            UpdateGatewayTokenDisplay(
                                GatewayTokenTextBox.Text,
                                "클립보드에서 토큰이 자동 수집되지 않았습니다. Get token을 눌러 설정하세요.");
                        }
                    }
                }
            }
    }

    private async Task MonitorOnboardCompletionAsync(Process process, string distro)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = process.ExitCode;
            process.Dispose();

            if (exitCode != 0)
            {
                AppendLog($"Onboard exited with code {exitCode}. Run onboard again and complete the process.");
                UpdateGatewayTokenDisplay(
                    GatewayTokenTextBox.Text,
                    "온보드가 완료되지 않았습니다. 터미널에서 온보드 과정을 끝내고 다시 시도하세요.");
                SetStatus("Onboard not completed.");
                return;
            }

            var token = await TryLoadGatewayTokenFromConfigAsync(distro, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                await Task.Delay(1200).ConfigureAwait(false);
                token = await TryLoadGatewayTokenFromConfigAsync(distro, CancellationToken.None).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                AppendLog("Gateway token not found after onboard. Please complete onboard and apply token if needed.");
                UpdateGatewayTokenDisplay(
                    GatewayTokenTextBox.Text,
                    "온보드 완료 후 토큰 조회가 안 됐습니다. 온보드를 완전히 끝내고 다시 클릭하세요.");
                SetStatus("Onboard completed, but token not found.");
                return;
            }

            _gatewayToken = token;
            UpdateGatewayMonitorStateForTokenStatus(token, tokenRequired: false);
            UpdateGatewayTokenDisplay(
                token,
                "온보드에서 발급된 토큰을 config에서 읽어왔습니다. Control UI settings에 붙여 넣으세요.");
            AppendLog($"[{UserCommandContext}] Gateway token loaded from openclaw config.");
            SaveSettings();
            SetStatus("Gateway token loaded.");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to process onboard completion: {ex.Message}");
            UpdateGatewayTokenDisplay(
                GatewayTokenTextBox.Text,
                "온보드 완료 감지 후 토큰 조회 중 오류가 발생했습니다.");
        }
    }

    private async Task<string?> TryLoadGatewayTokenFromConfigAsync(string distro, CancellationToken ct)
    {
        var normalizedDistro = NormalizeDistroValue(distro);
        if (string.IsNullOrWhiteSpace(normalizedDistro))
        {
            return null;
        }

        var configArgs = OpenClawWslCommandBuilder.BuildGatewayTokenConfigArguments(normalizedDistro).ToArray();
        AppendWslCommandLog(UserCommandContext, configArgs);

        var result = await RunWslCommandAsync("wsl.exe", configArgs, ct).ConfigureAwait(false);
        if (result.Canceled)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                AppendLog($"Failed to get gateway token: {NormalizeLogText(result.StandardError)}");
            }

            return null;
        }

        var output = result.StandardOutput?.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var configToken = ExtractGatewayToken(output) ?? ExtractPlainTokenCandidate(output);
        if (string.IsNullOrWhiteSpace(configToken))
        {
            return null;
        }

        return configToken;
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

        if (HealthMonitorButton is not null)
        {
            HealthMonitorButton.IsEnabled = false;
        }

        if (AddMonitorPortButton is not null)
        {
            AddMonitorPortButton.IsEnabled = false;
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
        if (StartGatewayButton is not null)
        {
            StartGatewayButton.IsEnabled = !_isBusy
                && _distroExists
                && !string.IsNullOrWhiteSpace(Distro)
                && GatewayPortValid
                && !_gatewayRunning;
        }

        if (OnboardButton is not null)
        {
            OnboardButton.IsEnabled = !_isBusy
                && !string.IsNullOrWhiteSpace(Distro);
        }

        if (StopGatewayButton is not null)
        {
            StopGatewayButton.IsEnabled = true;
        }

        if (HealthMonitorButton is not null)
        {
            HealthMonitorButton.IsEnabled = !_isBusy && GatewayMonitor is not null;
            HealthMonitorButton.Content = _isMonitoring ? "Stop Monitoring" : "Start Monitoring";
        }

        if (GetTokenButton is not null)
        {
            GetTokenButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(Distro);
        }

        if (AddMonitorPortButton is not null)
        {
            AddMonitorPortButton.IsEnabled = !_isBusy;
        }
    }

    private async void GetTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var distro = NormalizeCommandName(DistroTextBox.Text);
        if (string.IsNullOrWhiteSpace(distro))
        {
            AppendLog("Distro is required.");
            SetStatus("Distro required.");
            return;
        }

        var normalizedDistro = NormalizeDistroValue(distro);
        if (string.IsNullOrWhiteSpace(normalizedDistro))
        {
            AppendLog("Distro name is invalid.");
            SetStatus("Distro invalid.");
            return;
        }

        var cts = BeginOperation($"Getting gateway token for '{normalizedDistro}'.");
        string finalStatus = "Gateway token not found.";
        bool success = false;

        try
        {
            var token = await TryLoadGatewayTokenFromConfigAsync(normalizedDistro, cts.Token).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                finalStatus = "Gateway token not found. Run Onboard first.";
                AppendLog("Cannot find gateway token from config. Run Onboard and complete it.");
                UpdateGatewayTokenDisplay(
                    GatewayTokenTextBox.Text,
                    "온보드 완료가 안 됐습니다. 먼저 Onboard를 실행해 주세요.");
                SetStatus(finalStatus);
                return;
            }

            _gatewayToken = token;
            UpdateGatewayMonitorStateForTokenStatus(token, tokenRequired: false);
            UpdateGatewayTokenDisplay(token, "Config에서 토큰을 읽어왔습니다. Control UI settings에 붙여 넣으세요.");
            AppendLog($"[{UserCommandContext}] Gateway token loaded from config.");
            SaveSettings();
            finalStatus = "Gateway token loaded from config.";
            success = true;
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Get token canceled.";
        }
        catch (Exception ex)
        {
            finalStatus = "Failed to get token.";
            AppendLog($"Failed to get token: {ex.Message}");
            SetStatus(finalStatus);
        }
        finally
        {
            FinishOperation(finalStatus, success);
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            cts.Dispose();
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
        UpdateGatewayTokenDisplay("아직 토큰이 감지되지 않았습니다.", "게이트웨이를 시작하는 중입니다. 토큰 로그를 기다려주세요.");
        StopGatewayStream();

        var startGatewayTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        _gatewayCommandCancellation = startGatewayTokenSource;
        var startToken = startGatewayTokenSource.Token;
        var startupOutput = new StringBuilder();
        var startupError = new StringBuilder();
        var startupOutputLock = new object();
        void CaptureStartupOutput(string line, bool isError)
        {
            lock (startupOutputLock)
            {
                if (isError)
                {
                    startupError.AppendLine(line);
                }
                else
                {
                    startupOutput.AppendLine(line);
                }
            }

            AppendGatewayOutput(line, isError);
        }

        var startArgs = OpenClawWslCommandBuilder.BuildGatewayStartArguments(normalizedDistro, port).ToArray();
        AppendWslCommandLog(UserCommandContext, startArgs);

        _gatewayCommandTask = RunWslCommandCapturedAsync(
            "wsl.exe",
            startArgs,
            line => CaptureStartupOutput(line, isError: false),
            line => CaptureStartupOutput(line, isError: true),
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

                var alreadyRunning = IsGatewayAlreadyRunningMessage(startResult.StandardError)
                    || IsGatewayAlreadyRunningMessage(startResult.StandardOutput);
                if (alreadyRunning)
                {
                    AppendLog($"요청 포트 {port}가 이미 사용 중입니다. 다른 포트를 선택해 다시 시작하세요.");
                }
                else
                {
                    AppendLog("Gateway failed to start. See logs for details.");
                }

                _gatewayRunning = false;
                StopGatewayStream();
                return false;
            }
        }
        else
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                string startupOutputSnapshot;
                string startupErrorSnapshot;
                lock (startupOutputLock)
                {
                    startupOutputSnapshot = startupOutput.ToString();
                    startupErrorSnapshot = startupError.ToString();
                }

                if (IsGatewayAlreadyRunningMessage(startupErrorSnapshot)
                    || IsGatewayAlreadyRunningMessage(startupOutputSnapshot))
                {
                    AppendLog($"요청 포트 {port}가 이미 사용 중입니다. 다른 포트를 선택해 다시 시작하세요.");

                    _gatewayRunning = false;
                    StopGatewayStream();
                    return false;
                }

                if (_gatewayCommandTask.IsCompleted)
                {
                    break;
                }

                await Task.Delay(500, startToken).ConfigureAwait(false);
            }
        }

        _gatewayRunning = true;
        _gatewayPort = port;
        var monitorConfigured = ConfigureGatewayMonitorForPort(port);
        if (monitorConfigured)
        {
            AppendLog($"Gateway monitor auto-registered for port {port}.");
        }
        else
        {
            AppendLog($"Gateway started on port {port}, but failed to auto-register monitor.");
        }

        if (!verifyReachable)
        {
            StartGatewayHealthMonitoring(CancellationToken.None);
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
                if (IsGatewayAlreadyRunningMessage(finalResult.StandardError)
                    || IsGatewayAlreadyRunningMessage(finalResult.StandardOutput))
                {
                    AppendLog($"요청 포트 {port}가 이미 사용 중입니다. 다른 포트를 선택해 다시 시작하세요.");
                }
                _gatewayRunning = false;
                StopGatewayStream();
                return false;
            }
        }

        StartGatewayHealthMonitoring(CancellationToken.None);
        return true;
    }

    private static bool IsGatewayAlreadyRunningMessage(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && GatewayAlreadyRunningOutputRegex.IsMatch(text);
    }

    private void SetGatewayPortForMonitoring(int port)
    {
        if (Dispatcher.CheckAccess())
        {
            PortTextBox.Text = port.ToString(CultureInfo.InvariantCulture);
            SaveSettings();
            return;
        }

        Dispatcher.BeginInvoke(() => SetGatewayPortForMonitoring(port));
    }

    private bool ConfigureGatewayMonitorForPort(int port)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ConfigureGatewayMonitorForPort(port));
            return true;
        }

        try
        {
            var monitor = new GatewayMonitorState(
                Port: port,
                IsMonitoring: true,
                IsHealthy: null,
                LastChecked: null,
                MonitorToken: null,
                TokenRequired: false,
                TokenStatusMessage: null,
                HealthStatusMessage: null);

            lock (_gatewayMonitorLock)
            {
                _gatewayMonitor = monitor;
            }

            AppendLog($"Gateway monitor target set to port {port}.");
            if (MonitorPortTextBox is not null)
            {
                MonitorPortTextBox.Text = port.ToString(CultureInfo.InvariantCulture);
            }

            SaveSettings();
            UpdateGatewayMonitorViews();
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to configure monitor port: {ex.Message}");
            return false;
        }
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
        var stopArgs = OpenClawWslCommandBuilder.BuildGatewayStopArguments(normalizedDistro).ToArray();
        AppendWslCommandLog(UserCommandContext, stopArgs);
        var stopResult = await RunWslCommandAsync("wsl.exe", stopArgs, token);
        if (stopResult.Canceled)
        {
            return false;
        }

        var statusExitCode = stopResult.ExitCode;
        if (statusExitCode != 0 && statusExitCode != 15)
        {
            AppendLog($"Gateway stop command exited with code {statusExitCode}.");
            if (!string.IsNullOrWhiteSpace(stopResult.StandardError))
            {
                AppendLog($"Gateway stop stderr: {NormalizeLogText(stopResult.StandardError)}");
            }
        }
        else if (statusExitCode == 15)
        {
            AppendLog("Gateway stop command exited with code 15 (treated as expected when termination signals are used).");
        }

        if (!string.IsNullOrWhiteSpace(stopResult.StandardOutput))
        {
            AppendLog($"Gateway stop stdout: {NormalizeLogText(stopResult.StandardOutput)}");
        }

        if (statusExitCode != 0 && !string.IsNullOrWhiteSpace(stopResult.StandardError))
        {
            AppendLog($"Gateway stop stderr: {NormalizeLogText(stopResult.StandardError)}");
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
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (TryParseGatewayTokenMissingLine(line, out var tokenMissingMessage))
        {
            _gatewayToken = null;
            UpdateGatewayMonitorStateForTokenStatus(tokenMissingMessage, tokenRequired: true);
            UpdateGatewayTokenDisplay(string.Empty, "토큰이 없어 연결이 거부되었습니다. 대시보드에서 토큰을 가져와 붙여 넣으세요.");
            AppendLog($"[gateway] {tokenMissingMessage}");
            return;
        }
    }

    private bool TryParseGatewayTokenMissingLine(string line, out string missingMessage)
    {
        missingMessage = string.Empty;
        if (!GatewayTokenMissingFromGatewayLogRegex.IsMatch(line))
        {
            return false;
        }

        missingMessage = "disconnected (1008): unauthorized: gateway token missing (open the dashboard URL and paste the token in Control UI settings)";
        return true;
    }

    private void UpdateGatewayMonitorStateForTokenStatus(string tokenMessage, bool tokenRequired)
    {
        lock (_gatewayMonitorLock)
        {
            if (_gatewayMonitor is null)
            {
                return;
            }

            _gatewayMonitor = _gatewayMonitor with
            {
                MonitorToken = tokenRequired ? null : tokenMessage,
                TokenRequired = tokenRequired,
                TokenStatusMessage = tokenRequired ? tokenMessage : null
            };
        }

        UpdateGatewayMonitorViews();
    }

    private void UpdateGatewayTokenDisplay(string token, string statusMessage)
    {
        if (Dispatcher.CheckAccess())
        {
            GatewayTokenTextBox.Text = token;
            GatewayTokenStatus.Text = statusMessage;
            GatewayTokenStatus.Foreground = GatewayStatusColor.GetTokenStatusBrush(statusMessage);
            if (TokenIcon is not null)
            {
                TokenIcon.Text = GatewayStatusColor.GetTokenStatusIcon(statusMessage);
                TokenIcon.Foreground = GatewayStatusColor.GetTokenStatusBrush(statusMessage);
            }

            if (GatewayTokenSummary is not null)
            {
                GatewayTokenSummary.Text = statusMessage;
                GatewayTokenSummary.Foreground = GatewayStatusColor.GetTokenStatusBrush(statusMessage);
            }

            GatewayTokenTextBox.ToolTip = token;
            return;
        }

        Dispatcher.BeginInvoke(() => UpdateGatewayTokenDisplay(token, statusMessage));
    }

    private static string? ExtractGatewayToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var matches = new[]
        {
            GatewayTokenFromUrlRegex,
            GatewayTokenFromAnyTokenParamRegex,
            GatewayTokenFromJsonRegex,
            GatewayTokenFromLabelRegex
        };

        foreach (var regex in matches)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                var tokenCandidate = match.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(tokenCandidate) && tokenCandidate != "null")
                {
                    return tokenCandidate;
                }
            }
        }

        return null;
    }

    private static string? ExtractPlainTokenCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separators = new[] { ' ', '\t', '\r', '\n' };
        var candidates = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in candidates)
        {
            var trimmed = candidate.Trim('\"', '\'', '`', ';', ',', '.', ':');
            if (GatewayPlainTokenCandidateRegex.IsMatch(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    private bool StartGatewayHealthMonitoring(CancellationToken token)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => StartGatewayHealthMonitoring(token));
            return true;
        }

        if (_isMonitoring)
        {
            AppendLog("Gateway monitoring already running.");
            return true;
        }

        var monitor = GatewayMonitor;
        if (monitor is null || !monitor.IsMonitoring)
        {
            if (_gatewayPort is > 0)
            {
                AppendLog($"No active monitor port. Re-configuring monitor for port {_gatewayPort}.");
                var configured = ConfigureGatewayMonitorForPort(_gatewayPort);
                if (!configured)
                {
                    AppendLog("No active monitor port.");
                    return false;
                }

                monitor = GatewayMonitor;
                if (monitor is null || !monitor.IsMonitoring)
                {
                    AppendLog("No active monitor port.");
                    return false;
                }
            }
            else
            {
                AppendLog("No active monitor port.");
                return false;
            }
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _healthMonitorCancellation?.Cancel();
        _healthMonitorCancellation = cancellation;
        _isMonitoring = true;
        UpdateInstallButtonState();
        SetStatus("Gateway monitoring started.");

        _ = MonitorGatewayHealthLoopAsync(cancellation.Token);
        return true;
    }

    private async Task MonitorGatewayHealthLoopAsync(CancellationToken token)
    {
        var previousState = (bool?)null;
        var isFirstIteration = true;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var monitor = GatewayMonitor;
                if (monitor is null || !monitor.IsMonitoring)
                {
                    break;
                }

                var checkedAt = DateTime.UtcNow;
                var healthy = await CheckGatewayHealthAsync(monitor.Port, token).ConfigureAwait(false);
                UpdateGatewayMonitorState(healthy, checkedAt);
                UpdateGatewayMonitorViews();

                monitor = GatewayMonitor;
                if (monitor is null)
                {
                    break;
                }

                if (isFirstIteration || previousState != monitor.IsHealthy)
                {
                    if (!isFirstIteration)
                    {
                        AppendLog(monitor.IsHealthy == true
                            ? $"Gateway health check: healthy on port {monitor.Port}."
                            : $"Gateway health check: unhealthy on port {monitor.Port}.");
                    }

                    previousState = monitor.IsHealthy;
                }

                SetStatus(BuildGatewayMonitorStatus(monitor));
                isFirstIteration = false;

                if (token.IsCancellationRequested)
                {
                    break;
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
        _healthMonitorCancellation?.Dispose();
        _healthMonitorCancellation = null;
        _isMonitoring = false;
        UpdateInstallButtonState();
    }

    private Task<bool> CheckGatewayHealthAsync(CancellationToken token)
    {
        return CheckGatewayHealthAsync(_gatewayPort, token);
    }

    private async Task<bool> CheckGatewayHealthAsync(int port, CancellationToken token)
    {
        foreach (var path in GatewayHealthPaths)
        {
            using var pathTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            pathTokenSource.CancelAfter(GatewayHealthTimeout);

            try
            {
                using var response = await HealthClient.GetAsync(
                    $"http://127.0.0.1:{port}{path}",
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

    private Task<bool> WaitForGatewayHealthyAsync(CancellationToken token)
    {
        return WaitForGatewayHealthyAsync(_gatewayPort, token);
    }

    private async Task<bool> WaitForGatewayHealthyAsync(int port, CancellationToken token)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (await CheckGatewayHealthAsync(port, token).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }

        return false;
    }

    private async Task CheckGatewayHealthAndRefreshAsync()
    {
        var monitor = GatewayMonitor;
        if (monitor is null)
        {
            return;
        }

        var isHealthy = false;
        try
        {
            isHealthy = await CheckGatewayHealthAsync(monitor.Port, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        UpdateGatewayMonitorState(
            isHealthy,
            DateTime.UtcNow,
            healthStatusMessage: null,
            tokenRequired: monitor.TokenRequired);
        UpdateGatewayMonitorViews();
    }

    private List<GatewayMonitorState> GetMonitorSnapshot()
    {
        lock (_gatewayMonitorLock)
        {
            var monitors = _gatewayMonitor is null ? [] : new List<GatewayMonitorState> { _gatewayMonitor };
            return monitors;
        }
    }

    private void UpdateGatewayMonitorState(
        bool isHealthy,
        DateTime checkedAt,
        string? healthStatusMessage = null,
        bool? tokenRequired = null)
    {
        lock (_gatewayMonitorLock)
        {
            if (_gatewayMonitor is null)
            {
                return;
            }

            _gatewayMonitor = _gatewayMonitor with
            {
                IsHealthy = isHealthy,
                LastChecked = checkedAt,
                HealthStatusMessage = healthStatusMessage,
                TokenRequired = tokenRequired ?? _gatewayMonitor.TokenRequired
            };
        }
    }

    private void UpdateGatewayMonitorViews()
    {
        var snapshot = GetMonitorSnapshot();
        if (Dispatcher.CheckAccess())
        {
            ApplyGatewayMonitorViews(snapshot);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyGatewayMonitorViews(snapshot));
    }

    private void ApplyGatewayMonitorViews(IReadOnlyList<GatewayMonitorState> monitors)
    {
        if (GatewayMonitorSummary is null)
        {
            return;
        }

        if (monitors.Count == 0)
        {
            GatewayMonitorSummary.Text = "Monitor target not set.";
            GatewayMonitorSummary.Foreground = GatewayStatusColor.GetMonitorSummaryBrush(null);
            if (MonitorIcon is not null)
            {
                MonitorIcon.Text = GatewayStatusColor.GetMonitorIcon(null);
                MonitorIcon.Foreground = GatewayStatusColor.GetMonitorSummaryBrush(null);
            }

            if (GatewayTokenSummary is not null)
            {
                GatewayTokenSummary.Text = "Token status unknown.";
                GatewayTokenSummary.Foreground = GatewayStatusColor.GetTokenStatusBrush("Token status unknown.");
            }

            UpdateInstallButtonState();
            return;
        }

        GatewayMonitorSummary.Text = BuildGatewayMonitorSummaryText(monitors);
        GatewayMonitorSummary.Foreground = GatewayStatusColor.GetMonitorSummaryBrush(monitors[0]);
        if (MonitorIcon is not null)
        {
            MonitorIcon.Text = GatewayStatusColor.GetMonitorIcon(monitors[0]);
            MonitorIcon.Foreground = GatewayStatusColor.GetMonitorSummaryBrush(monitors[0]);
        }

        if (GatewayTokenSummary is not null)
        {
            GatewayTokenSummary.Text = monitors[0].TokenRequired ? "Token required." : "Token detected.";
            GatewayTokenSummary.Foreground = GatewayStatusColor.GetTokenStatusBrush(
                monitors[0].TokenRequired ? "token required" : "token detected");
        }

        UpdateInstallButtonState();
    }

    private static string BuildGatewayMonitorStatus(GatewayMonitorState? monitor)
    {
        if (monitor is null)
        {
            return "Monitor target not set.";
        }

        if (!monitor.IsMonitoring)
        {
            return $"Port {monitor.Port}: monitoring paused.";
        }

        if (monitor.TokenRequired)
        {
            return $"Port {monitor.Port}: token required - {monitor.TokenStatusMessage ?? "open dashboard and paste gateway token."}";
        }

        var state = monitor.IsHealthy switch
        {
            true => "reachable",
            false => "unreachable",
            _ => "checking"
        };

        var checkedAt = monitor.LastChecked?.ToLocalTime().ToString("HH:mm:ss") ?? "never";
        return $"Port {monitor.Port}: {state}. Last check: {checkedAt}.";
    }

    private static string BuildGatewayMonitorSummaryText(IReadOnlyList<GatewayMonitorState> monitors)
    {
        if (monitors.Count == 0)
        {
            return "No monitor port configured.";
        }

        var monitor = monitors[0];
        var monitoringState = monitor.IsMonitoring ? "running" : "paused";
        var state = monitor.TokenRequired
            ? "token required"
            : monitor.IsHealthy switch
            {
                true => "reachable",
                false => "unreachable",
                _ => "checking"
            };
        var checkedAt = monitor.LastChecked?.ToLocalTime().ToString("HH:mm:ss") ?? "never";

        var tokenDisplay = monitor.TokenRequired
            ? "missing (token required)"
            : string.IsNullOrWhiteSpace(monitor.MonitorToken)
                ? "pending"
                : "detected";

        var statusDetail = monitor.HealthStatusMessage;
        if (string.IsNullOrWhiteSpace(statusDetail))
        {
            statusDetail = monitor.TokenRequired ? null : monitor.IsHealthy switch
            {
                true => "reachable",
                false => "not responding",
                _ => "checking"
            };
        }

        var detailedStatus = statusDetail is null
            ? state
            : $"{state} ({statusDetail})";

        if (monitor.TokenRequired && !string.IsNullOrWhiteSpace(monitor.TokenStatusMessage))
        {
            return $"Port {monitor.Port} [{monitoringState}] | {detailedStatus}: {monitor.TokenStatusMessage} | token: {tokenDisplay} | last: {checkedAt}";
        }

        return $"Port {monitor.Port} [{monitoringState}] | {detailedStatus} | token: {tokenDisplay} | last: {checkedAt}";
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
            StatusLabel.Foreground = GatewayStatusColor.GetStatusBrush(status);
            if (StatusIcon is not null)
            {
                StatusIcon.Text = GatewayStatusColor.GetStatusIcon(status);
                StatusIcon.Foreground = GatewayStatusColor.GetStatusBrush(status);
            }
            return;
        }

        Dispatcher.BeginInvoke(() => SetStatus(status));
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
        if (LogsTextBox is null)
        {
            Debug.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            LogsTextBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
            LogsTextBox.ScrollToEnd();
            return;
        }

        Dispatcher.BeginInvoke(() => AppendLog(text));
    }

    private void AppendWslCommandLog(string context, IReadOnlyList<string> arguments)
    {
        AppendLog($"[{context}] Running: wsl.exe {FormatCommandForLog(arguments)}");
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

    private sealed class AppSettings
    {
        public string? Distro { get; set; }
        public int? GatewayPort { get; set; }
        public int? MonitorPort { get; set; }
        public string? GatewayToken { get; set; }
    }

    private readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError, bool Canceled);
}
