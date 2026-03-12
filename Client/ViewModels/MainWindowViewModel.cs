﻿using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Controls.ApplicationLifetimes;
using System.Linq;
using System.Net.Http;
using Avalonia.Threading;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Globalization;
using System.Xml.Linq;
using intuneMigratorClient.Services;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace intuneMigratorClient.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AuthService _authService;
    private readonly DeviceInfoService _deviceService;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _loginCts;
    private string _apiEndpointMigration = "/api/MigrationRequest";
    private string _apiEndpointCheckMigration = "/api/MigrationCheck";
    private string _wipeMode = "false";
    private readonly string _wipeFlagFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".intuneMigratorWipePending");

    [ObservableProperty]
    private bool _isDebug = false;

    [ObservableProperty]
    private bool _useDeviceCodeFlow;

    [ObservableProperty]
    private string _statusMessage = "Ready to login.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckMigrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetrieveHashCommand))]
    private bool _isAuthenticated;

    [ObservableProperty]
    private bool _isWipeOptionVisible = true;

    [ObservableProperty]
    private int _loginTimeoutSeconds;

    [ObservableProperty]
    private string? _userName;

    [ObservableProperty]
    private bool _isLoginInProgress;

    [ObservableProperty]
    private Bitmap? _companyLogo;

    [ObservableProperty]
    private string _appInstructions = "Loading instructions...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckMigrationCommand))]
    private bool _isAgreed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckMigrationCommand))]
    private bool _isWipeDevice;

    [ObservableProperty]
    private bool _isNightMode;

    [ObservableProperty]
    private bool _isHashRetrievalFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WipeDeviceCommand))]
    private bool _isWipeButtonEnabled;

    [ObservableProperty] private string _buttonLoginText = "Login to New Tenant";
    [ObservableProperty] private string _buttonLogoutText = "Logout";
    [ObservableProperty] private string _checkboxBackupText = "I confirm that I have backed up my data";
    [ObservableProperty] private string _checkboxWipeDeviceText = "Wipe my device after migration (Factory Reset)";
    [ObservableProperty] private string _buttonCheckMigrationText = "Check Migration";
    [ObservableProperty] private string _buttonStartMigrationText = "Start Migration";
    [ObservableProperty] private string _buttonWipeDeviceText = "Wipe Device";
    [ObservableProperty] private string _buttonRetryHashText = "Retry Hash Retrieval";
    [ObservableProperty] private string _buttonCancelText = "Cancel";
    [ObservableProperty] private string _buttonDebugText = "Debug Service";
    [ObservableProperty] private string _buttonCloseText = "Close";
    [ObservableProperty] private string _deviceCodeActionRequiredText = "Action Required:";
    [ObservableProperty] private string _deviceCodeStep1Text = "1. Open Link: ";
    [ObservableProperty] private string _deviceCodeStep2Text = "2. Copy Code: ";
    [ObservableProperty] private string _deviceCodeCopyTooltipText = "Click to copy code";

    [ObservableProperty]
    private string? _deviceCodeUrl;

    [ObservableProperty]
    private string? _deviceCode;

    [ObservableProperty]
    private bool _showDeviceCodePrompt;

    partial void OnIsNightModeChanged(bool value)
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    public MainWindowViewModel()
    {
        _deviceService = new DeviceInfoService();
        
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Settings.xml");

        #if DEBUG
            var devSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Settings.Development.xml");
            if (File.Exists(devSettingsPath))
            {
                settingsPath = devSettingsPath;
            }
        #endif

        if (!File.Exists(settingsPath))
            throw new FileNotFoundException($"Configuration file not found at {settingsPath}");

        var doc = XDocument.Load(settingsPath);

        // Initialize AuthService with settings from XML (Optimized: Single file read)
        var clientId = doc.Root?.Element("ClientId")?.Value ?? throw new InvalidOperationException("ClientId is missing in Settings.xml");;
        if (string.IsNullOrEmpty(clientId)) throw new InvalidOperationException("ClientId is missing or empty in Settings.xml");

        var tenantId = doc.Root?.Element("TenantId")?.Value;
        if (string.IsNullOrEmpty(tenantId)) throw new InvalidOperationException("TenantId is missing or empty in Settings.xml");

        var scopes = doc.Root?.Element("Scopes")?.Value ?? throw new InvalidOperationException("Scopes is missing in Settings.xml");
        
        var redirectUri = doc.Root?.Element("RedirectUri")?.Value;
        if (string.IsNullOrEmpty(redirectUri)) throw new InvalidOperationException("RedirectUri is missing or empty in Settings.xml");

        var spaOrigin = doc.Root?.Element("SpaOrigin")?.Value;

        _authService = new AuthService(clientId, tenantId, scopes, redirectUri, spaOrigin);

        var baseUrl = "https://localhost:7123";
        var ignoreSslErrors = false;

        var serverApiId = doc.Root?.Element("ServerApiId")?.Value;
        if (!string.IsNullOrEmpty(serverApiId)) baseUrl = serverApiId;

        var apiEndpointMigration = doc.Root?.Element("ApiEndpointMigration")?.Value;
        if (!string.IsNullOrEmpty(apiEndpointMigration)) _apiEndpointMigration = apiEndpointMigration;

        var apiEndpointCheckMigration = doc.Root?.Element("ApiEndpointCheckMigration")?.Value;
        if (!string.IsNullOrEmpty(apiEndpointCheckMigration)) _apiEndpointCheckMigration = apiEndpointCheckMigration;    
            
        var ignoreSslErrorsValue = doc.Root?.Element("IgnoreSslErrors")?.Value;
        if (!string.IsNullOrEmpty(ignoreSslErrorsValue))
        {
            bool.TryParse(ignoreSslErrorsValue, out ignoreSslErrors);
        }

        var wipeMode = doc.Root?.Element("WipeDevice")?.Value;
        if (!string.IsNullOrEmpty(wipeMode))
        {
            if (wipeMode.Equals("true", StringComparison.OrdinalIgnoreCase))
                _wipeMode = "remotelocal";
            else
                _wipeMode = wipeMode.ToLowerInvariant();
        }

        IsWipeOptionVisible = !_wipeMode.Equals("false", StringComparison.OrdinalIgnoreCase);
        if (!IsWipeOptionVisible) IsWipeDevice = false;

        _isDebug = doc.Root?.Element("Debug")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        var useDeviceCodeFlow = doc.Root?.Element("DeviceCodeFlow")?.Value;
        if (!string.IsNullOrEmpty(useDeviceCodeFlow))
        {
            _useDeviceCodeFlow = useDeviceCodeFlow.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (ignoreSslErrors)
        {
            LogService.Warning("SSL certificate validation is disabled via Settings.xml.");
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        }
        else
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        }

        if (File.Exists(_wipeFlagFile))
        {
            _isWipeButtonEnabled = true;
        }

        LoadUiResources();
    }

    [RelayCommand]
    private void Close()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private void CancelLogin()
    {
        _loginCts?.Cancel();
    }

    [RelayCommand]
    private async Task Login()
    {
        LogService.Info("Starting login process...");
        IsBusy = true;
        IsLoginInProgress = true;
        StatusMessage = "Logging in...";
        LoginTimeoutSeconds = UseDeviceCodeFlow ? 900 : 300;

        _loginCts = new CancellationTokenSource(TimeSpan.FromSeconds(LoginTimeoutSeconds));

        try
        {
            Task<AuthenticationResult?> loginTask;

            if (UseDeviceCodeFlow)
            {
                loginTask = _authService.LoginDeviceCodeAsync(async (result) =>
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        DeviceCodeUrl = result.VerificationUrl;
                        DeviceCode = result.UserCode;
                        ShowDeviceCodePrompt = true;
                        StatusMessage = $"Please sign in at {result.VerificationUrl} with code: {result.UserCode}";
                        LogService.Info($"Device Code Flow: {result.VerificationUrl} - {result.UserCode}");
                    });
                }, _loginCts.Token);
            }
            else
            {
                // Get parent window handle for the broker (WAM)
                IntPtr parentWindowHandle = IntPtr.Zero;
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    parentWindowHandle = desktop.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                }
                
                loginTask = _authService.LoginAsync(parentWindowHandle, _loginCts.Token);
            }

            // Countdown loop
            while (!loginTask.IsCompleted)
            {
                try
                {
                    await Task.Delay(1000, _loginCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (!loginTask.IsCompleted && LoginTimeoutSeconds > 0)
                {
                    LoginTimeoutSeconds--;
                }
            }

            var result = await loginTask;

            if (result != null)
            {
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
                IsAuthenticated = true;

                // Extract a friendly name from the username (UPN)
                var name = result.ClaimsPrincipal?.FindFirst("name")?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    name = result.Account.Username;
                    if (!string.IsNullOrEmpty(name) && name.Contains("@"))
                    {
                        name = name.Split('@')[0];
                    }
                } else
                {
                    if (name.Contains(","))
                    {
                        var parts = name.Split(',', StringSplitOptions.TrimEntries);

                        if (parts.Length == 2)
                        {
                            name = $"{parts[1]} {parts[0]}";
                        }
                    }
                }
                UserName = name;

                StatusMessage = $"Login successful. User: {UserName}";
                LogService.Info(StatusMessage);
            }
            else
            {
                LogService.Warning("Login failed. No authentication result received.");
                StatusMessage = "Login failed.";
            }
        }
        catch (OperationCanceledException)
        {
            LogService.Warning("Login process was cancelled or timed out.");
            StatusMessage = "Login cancelled or timed out.";
        }
        catch (MsalServiceException ex)
        {
            LogService.Error($"MSAL Login Error: {ex.Message}");
            StatusMessage = $"Login failed: {ex.ErrorCode}. {ex.Message}";
        }
        catch (Exception ex)
        {
            LogService.Error($"Login Error: {ex.Message}");
            StatusMessage = $"Login Error: {ex.Message}";
        }
        finally
        {
            _loginCts?.Dispose();
            _loginCts = null;
            IsBusy = false;
            IsLoginInProgress = false;
            ShowDeviceCodePrompt = false;
        }
    }

    [RelayCommand]
    private async Task Logout()
    {
        await _authService.LogoutAsync();
        IsAuthenticated = false;
        UserName = null;
        IsAgreed = false;
        IsWipeDevice = false;
        IsHashRetrievalFailed = false;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        StatusMessage = "Logged out successfully.";
        LogService.Info(StatusMessage);
    }

    private bool CanMigrate => IsAuthenticated && IsAgreed;

    [RelayCommand(CanExecute = nameof(CanMigrate))]
    private async Task Migrate()
    {
        IsBusy = true;
        StatusMessage = "Gathering device info...";
        LogService.Info(StatusMessage);
        
        var serial = await _deviceService.GetSerialNumberAsync();
        var hostname = await _deviceService.GetHostnameAsync();
        var hash = await _deviceService.GetHardwareHashAsync();

        var testDevicePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TestDevice.xml");
        if (File.Exists(testDevicePath))
        {
            try
            {
                var doc = XDocument.Load(testDevicePath);
                serial = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "serial", StringComparison.OrdinalIgnoreCase))?.Value;
                hostname = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "hostname", StringComparison.OrdinalIgnoreCase))?.Value;
                hash = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "hash", StringComparison.OrdinalIgnoreCase))?.Value;

                StatusMessage = "Loaded test device data from TestDevice.xml";
                LogService.Info(StatusMessage);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading test device data: {ex.Message}";
                LogService.Error(StatusMessage);
            }
        }

        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(hostname))
        {
            StatusMessage = "Failed to retrieve hardware hash or hostname. Ensure service is running.";
            LogService.Error(StatusMessage);
            IsHashRetrievalFailed = true;
            IsBusy = false;
            return;
        }

        Span<byte> buffer = new byte[hash.Length];
        if (!Convert.TryFromBase64String(hash, buffer, out _))
        {
            StatusMessage = "Retrieved hardware hash is not a valid Base64 string.";
            LogService.Error(StatusMessage);
            IsHashRetrievalFailed = true;
            IsBusy = false;
            return;
        }

        IsHashRetrievalFailed = false;

        StatusMessage = "Sending migration request...";
        LogService.Info(StatusMessage);

        try
        {
            bool performRemoteWipe = IsWipeDevice && (_wipeMode.Contains("remote"));
            bool performLocalWipe = IsWipeDevice && (_wipeMode.Contains("local"));

            var payload = new { SerialNumber = serial, DeviceName = hostname, HardwareHash = hash, WipeDevice = performRemoteWipe, Debug = IsDebug };

            var response = await _httpClient.PostAsJsonAsync(_apiEndpointMigration, payload);
            
            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Migration initiated!";
                LogService.Info(StatusMessage);

                if (!IsWipeDevice || performLocalWipe)
                {
                    try
                    {
                        if (!File.Exists(_wipeFlagFile)) File.Create(_wipeFlagFile).Dispose();
                        IsWipeButtonEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"Failed to create wipe flag file: {ex.Message}");
                    }
                }

                if (performLocalWipe)
                {
                    if (IsDebug == false)
                    {
                        // Wipe device
                        var wipe = await _deviceService.WipeDeviceAsync();
                        StatusMessage = "Migration initiated! Device will wipe shortly.";
                        LogService.Info(StatusMessage);
                    }
                }
                else if (performRemoteWipe)
                {
                    StatusMessage = "Migration initiated! Device will be wiped by the new tenant.";
                    LogService.Info(StatusMessage);
                }
                else
                {
                    StatusMessage = "Migration initiated! Device wipe must be completed manually.";
                    LogService.Warning(StatusMessage);
                }
            }
            else
            {
                var errorMsg = response.ReasonPhrase;
                try
                {
                    var errorData = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (errorData.TryGetProperty("Message", out var msg) || errorData.TryGetProperty("message", out msg))
                    {
                        errorMsg = msg.GetString();
                    }
                }
                catch
                {
                    // Fallback to ReasonPhrase if parsing fails
                }
                StatusMessage = $"Migration failed: {errorMsg}";
                LogService.Error(StatusMessage);
            }

        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            LogService.Error(StatusMessage);
        }

        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanMigrate))]
    private async Task CheckMigration()
    {
        StatusMessage = "Gathering device info...";
        LogService.Info(StatusMessage);
        
        LogService.Info("Retrieving serial number and hostname for migration check...");
        var serial = await _deviceService.GetSerialNumberAsync();
        var hostname = await _deviceService.GetHostnameAsync();

        var testDevicePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TestDevice.xml");
        if (File.Exists(testDevicePath))
        {
            try
            {
                var doc = XDocument.Load(testDevicePath);
                serial = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "serial", StringComparison.OrdinalIgnoreCase))?.Value;
                hostname = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "hostname", StringComparison.OrdinalIgnoreCase))?.Value;

                LogService.Info("Loaded test device data from TestDevice.xml");
            }
            catch (Exception ex)
            {
                LogService.Error($"Error loading TestDevice.xml: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(hostname))
        {
            StatusMessage = "Failed to retrieve hostname. Ensure service is running.";
            LogService.Error(StatusMessage);
            IsHashRetrievalFailed = true;
            IsBusy = false;
            return;
        }

        StatusMessage = "Sending migration check request...";
        LogService.Info(StatusMessage);

        try
        {
            var payload = new { SerialNumber = serial, DeviceName = hostname, Debug = IsDebug };

            var response = await _httpClient.PostAsJsonAsync(_apiEndpointCheckMigration, payload);
            
            if (response.IsSuccessStatusCode)
            {
                var successMsg = response.ReasonPhrase;
                try
                {
                    var successData = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (successData.TryGetProperty("Message", out var msg) || successData.TryGetProperty("message", out msg))
                    {
                        successMsg = msg.GetString();
                    }
                }
                catch
                {
                    // Fallback to ReasonPhrase if parsing fails
                }
                StatusMessage = successMsg ?? "Migration check successful.";
                LogService.Info(StatusMessage);
            }
            else
            {
                var errorMsg = response.ReasonPhrase;
                try
                {
                    var errorData = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (errorData.TryGetProperty("Message", out var msg) || errorData.TryGetProperty("message", out msg))
                    {
                        errorMsg = msg.GetString();
                    }
                }
                catch
                {
                    // Fallback to ReasonPhrase if parsing fails
                }
                StatusMessage = $"Migration check failed: {errorMsg}";
                LogService.Error(StatusMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            LogService.Error(StatusMessage);
        }
        IsBusy = false;

    }

    [RelayCommand(CanExecute = nameof(IsAuthenticated))]
    private async Task RetrieveHash()
    {
        IsBusy = true;
        StatusMessage = "Retrying hardware hash retrieval...";
        
        var hash = await _deviceService.GetHardwareHashAsync();

        if (string.IsNullOrEmpty(hash))
        {
            StatusMessage = "Failed to retrieve hardware hash.";
            LogService.Error(StatusMessage);
            IsHashRetrievalFailed = true;
        }
        else if (!Convert.TryFromBase64String(hash, new byte[hash.Length], out _))
        {
            StatusMessage = "Retrieved hardware hash is not a valid Base64 string.";
            LogService.Error(StatusMessage);
            IsHashRetrievalFailed = true;
        }
        else
        {
            StatusMessage = "Hardware hash retrieved successfully.";
            LogService.Info(StatusMessage);
            IsHashRetrievalFailed = false;
        }
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(IsWipeButtonEnabled))]
    private async Task WipeDevice()
    {
        StatusMessage = "Wiping device...";
        LogService.Info(StatusMessage);

        try
        {
            if (File.Exists(_wipeFlagFile)) File.Delete(_wipeFlagFile);
            IsWipeButtonEnabled = false;

            if (!IsDebug)
            {
                StatusMessage = "Device wipe initiated. Please wait for the device to wipe and restart.";
                LogService.Info(StatusMessage);
                await _deviceService.WipeDeviceAsync();
            }
            else
            {
                StatusMessage = "DEBUG: Device wipe simulated.";
                LogService.Info(StatusMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error wiping device: {ex.Message}";
            LogService.Error(StatusMessage);
        }
    }

    [RelayCommand]
    private async Task DebugService()
    {
        IsBusy = true;
        StatusMessage = "DEBUG: Querying Service...";
        LogService.Info(StatusMessage);

        try
        {
            var serial = await _deviceService.GetSerialNumberAsync();
            var manuf = await _deviceService.GetManufacturerAsync();
            var model = await _deviceService.GetModelAsync();
            var hash = await _deviceService.GetHardwareHashAsync();
            var hostname = await _deviceService.GetHostnameAsync();

            StatusMessage = $"DEBUG Info:\nHost: {hostname}\nSN: {serial}\nDevice: {manuf} {model}\nHash Length: {hash?.Length ?? 0}";

            var payload = new { SerialNumber = serial, HardwareHash = hash };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            //var debugpath = (Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_migration_request_debug.txt"));
            //await File.WriteAllTextAsync(debugpath, json);
            LogService.Info("payload: " + json);
            StatusMessage = StatusMessage + "\n\nDEBUG: Request logged.";

            LogService.Info("Checking migration status...");
            await CheckMigration();

            LogService.Info("Service debug completed.");

        }
        catch (Exception ex)
        {
            StatusMessage = $"DEBUG Error: {ex.Message}";
            LogService.Error("DEBUG: Service debug error: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDeviceCodeUrl()
    {
        if (string.IsNullOrEmpty(DeviceCodeUrl)) return;

        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DeviceCodeUrl) { UseShellExecute = true });
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                System.Diagnostics.Process.Start("xdg-open", DeviceCodeUrl);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to open URL: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CopyDeviceCode()
    {
        if (!string.IsNullOrEmpty(DeviceCode) && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(DeviceCode);
                StatusMessage = "Code copied to clipboard!";
            }
        }
    }

    private void LoadUiResources()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // Load Logo from Settings.xml
            var settingsPath = Path.Combine(baseDir, "Config", "Settings.xml");
            if (File.Exists(settingsPath))
            {
                var doc = XDocument.Load(settingsPath);
                var logoPath = doc.Root?.Element("LogoPath")?.Value;
                
                if (!string.IsNullOrEmpty(logoPath))
                {
                    if (!Path.IsPathRooted(logoPath))
                        logoPath = Path.Combine(baseDir, logoPath);

                    if (File.Exists(logoPath))
                        CompanyLogo = new Bitmap(logoPath);
                }
            }

            // Load Instructions from Strings.xml
            var stringsPath = Path.Combine(baseDir, "Config", "Strings.xml");
            if (File.Exists(stringsPath))
            {
                var doc = XDocument.Load(stringsPath);
                var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

                string? GetString(string key)
                {
                    var val = doc.Root?.Elements(key).FirstOrDefault(e => e.Attribute("Language")?.Value.Equals(lang, StringComparison.OrdinalIgnoreCase) == true)?.Value;
                    if (string.IsNullOrEmpty(val)) val = doc.Root?.Elements(key).FirstOrDefault(e => e.Attribute("Language")?.Value.Equals("en", StringComparison.OrdinalIgnoreCase) == true)?.Value;
                    return val?.Trim();
                }

                var instruction = GetString("Instruction");
                if (!string.IsNullOrEmpty(instruction)) AppInstructions = instruction;

                var t = GetString("ButtonLogin"); if (!string.IsNullOrEmpty(t)) ButtonLoginText = t;
                t = GetString("ButtonLogout"); if (!string.IsNullOrEmpty(t)) ButtonLogoutText = t;
                t = GetString("ButtonCheckMigration"); if (!string.IsNullOrEmpty(t)) ButtonCheckMigrationText = t;
                t = GetString("ButtonStartMigration"); if (!string.IsNullOrEmpty(t)) ButtonStartMigrationText = t;
                t = GetString("ButtonRetryHash"); if (!string.IsNullOrEmpty(t)) ButtonRetryHashText = t;
                t = GetString("ButtonCancel"); if (!string.IsNullOrEmpty(t)) ButtonCancelText = t;
                t = GetString("ButtonDebug"); if (!string.IsNullOrEmpty(t)) ButtonDebugText = t;
                t = GetString("ButtonClose"); if (!string.IsNullOrEmpty(t)) ButtonCloseText = t;
                t = GetString("CheckboxBackup"); if (!string.IsNullOrEmpty(t)) CheckboxBackupText = t;
                t = GetString("CheckboxWipeDevice"); if (!string.IsNullOrEmpty(t)) CheckboxWipeDeviceText = t;
                t = GetString("ButtonWipeDevice"); if (!string.IsNullOrEmpty(t)) ButtonWipeDeviceText = t;
                t = GetString("DeviceCodeActionRequired"); if (!string.IsNullOrEmpty(t)) DeviceCodeActionRequiredText = t;
                t = GetString("DeviceCodeStep1"); if (!string.IsNullOrEmpty(t)) DeviceCodeStep1Text = t;
                t = GetString("DeviceCodeStep2"); if (!string.IsNullOrEmpty(t)) DeviceCodeStep2Text = t;
                t = GetString("DeviceCodeCopyTooltip"); if (!string.IsNullOrEmpty(t)) DeviceCodeCopyTooltipText = t;
            }
        }
        catch (Exception ex)
        {
            AppInstructions = $"Error loading UI resources: {ex.Message}";
        }
    }
}
