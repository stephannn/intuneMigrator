using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;

namespace intuneMigratorService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private const string PipeName = "IntuneMigratorPipe";

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Service starting. Listening on pipe: {PipeName}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                NamedPipeServerStream serverStream;
                if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService() && HasElevatedRights())
                {
                    var pipeSecurity = new PipeSecurity();
                    pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    serverStream = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
                }
                else
                {
                    
                    if (OperatingSystem.IsWindows())
                    {
                    
                        var pipeSecurity = new PipeSecurity();
                        pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

                        serverStream = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
                    }
                    else
                    {
                        serverStream = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    }
                    
                }

                await using var server = serverStream;
                
                _logger.LogInformation("Waiting for connection...");
                await server.WaitForConnectionAsync(stoppingToken);

                if (!IsValidClient(server))
                {
                    _logger.LogWarning("Unauthorized client connection attempt rejected.");
                    continue;
                }

                _logger.LogInformation("Client connected.");

                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };

                var command = await reader.ReadLineAsync(stoppingToken);
                _logger.LogInformation("Received command: {Command}", command);

                string response = "ERROR";

                if (command == "GET_HASH")
                {
                    response = GetHardwareHash() ?? "NOT_FOUND";
                }
                else if (command == "GET_SERIAL")
                {
                    response = GetSerialNumber() ?? "NOT_FOUND";
                }
                else if (command == "GET_MANUFACTURER")
                {
                    response = GetManufacturer() ?? "NOT_FOUND";
                }
                else if (command == "GET_MODEL")
                {
                    response = GetModel() ?? "NOT_FOUND";
                }
                else if (command == "GET_HOSTNAME")
                {
                    response = GetHostname() ?? "NOT_FOUND";
                }
                else if (command == "WIPE_DEVICE")
                {
                    response = "OK";
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(1000);
                        WipeDevice();
                    });
                }

                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing request");
            }
        }
    }

    private string? GetHardwareHash()
    {
        if (OperatingSystem.IsLinux()) {
            return "Hash only available on Windows via WMI";
        }

        if (OperatingSystem.IsWindows()) {
            try
            {
                // Namespace root/cimv2/mdm/dmmap -Class MDM_DevDetail_Ext01
                using var searcher = new ManagementObjectSearcher(@"root\cimv2\mdm\dmmap", "SELECT DeviceHardwareData FROM MDM_DevDetail_Ext01");
                using var collection = searcher?.Get();
                var obj = collection?.Cast<ManagementObject>().FirstOrDefault();
                return obj?["DeviceHardwareData"]?.ToString();
            }
            catch (Exception ex) { 
                _logger.LogError(ex, "Error requesting Hardware Hash");
            }
        }
        return null;
    }

    private string? GetSerialNumber()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Requires root
                return File.ReadAllText("/sys/class/dmi/id/product_serial").Trim();
            }
            catch { return "UNKNOWN"; }
        }

        if (OperatingSystem.IsWindows()) {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                using var collection = searcher.Get();
                var obj = collection.Cast<ManagementObject>().FirstOrDefault();
                return obj?["SerialNumber"]?.ToString();
            }
                catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting Serial Number");
            }
        }
        return null; 
    }

    private string? GetManufacturer()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return File.ReadAllText("/sys/class/dmi/id/sys_vendor").Trim();
            }
            catch { return "UNKNOWN"; }
        }
        if (OperatingSystem.IsWindows()) {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
                using var collection = searcher.Get();
                var obj = collection.Cast<ManagementObject>().FirstOrDefault();
                return obj?["Manufacturer"]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting Manufacturer");
            }
        }
        return null; 
    }

    private string? GetModel()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
            }
            catch { return "UNKNOWN"; }
        }
        if (OperatingSystem.IsWindows()) {
            try
            {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            var obj = collection.Cast<ManagementObject>().FirstOrDefault();
            return obj?["Model"]?.ToString();
        }
        catch (Exception ex) { 
            _logger.LogError(ex, "Error requesting Model");
         }
        }
        return null;
    }

    private string? GetHostname()
    {
        try
        {
            return System.Environment.MachineName;
        }
        catch { return null; }
    }

    private void WipeDevice()
    {
        if (OperatingSystem.IsLinux())
        {
            _logger.LogInformation("Wipe Device requested on Linux. Manual wipe required.");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                _logger.LogInformation("Initiating Device Wipe...");
                using var searcher = new ManagementObjectSearcher(@"root\cimv2\mdm\dmmap", "SELECT * FROM MDM_RemoteWipe");
                using var collection = searcher.Get();
                var obj = collection.Cast<ManagementObject>().FirstOrDefault();
                if (obj != null)
                {
                    var inputParams = obj.GetMethodParameters("doWipeMethod");
                    inputParams["param"] = "";
                    obj.InvokeMethod("doWipeMethod", inputParams, null);
                }
                else throw new Exception("MDM_RemoteWipe instance not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wipe via WMI failed. Trying systemreset.exe.");
                try
                {
                    Process.Start(Path.Combine(Environment.SystemDirectory, "systemreset.exe"), "-factoryreset");
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Wipe via systemreset.exe failed.");
                }
            }
        }
    }

    private bool IsValidClient(NamedPipeServerStream server)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out uint pid))
                return false;

            using var proc = Process.GetProcessById((int)pid);
            if (!proc.ProcessName.Equals("intuneMigratorClient", StringComparison.OrdinalIgnoreCase))
                return false;

//#if !DEBUG
            // In Release mode, verify the client executable is signed by a trusted certificate
            if (proc.MainModule?.FileName is string path)
            {
                if (!VerifyDigitalSignature(path))
                {
                    _logger.LogWarning("Client process {Pid} signature verification failed.", pid);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("Unable to get main module for client process {Pid}.", pid);
                return false;
            }
//#endif
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating client process: " +ex.Message);
            return false;
        }
    }

    private bool VerifyDigitalSignature(string filePath)
    {
        try
        {
            var allowedThumbprint = _configuration["AllowedClientThumbprint"];
            if (string.IsNullOrEmpty(allowedThumbprint))
            {
                _logger.LogWarning("Allowed client thumbprint is not configured.");
                return true;
            }
            if (File.Exists(filePath))
            {

                //var signer = X509CertificateLoader.LoadCertificateFromFile(filePath);
                #pragma warning disable SYSLIB0057 // Type or member is obsolete
                var signer = X509Certificate.CreateFromSignedFile(filePath);
                #pragma warning restore SYSLIB0057 // Type or member is obsolete

                var x509 = new X509Certificate2(signer);
                

                if (!string.IsNullOrEmpty(allowedThumbprint) && x509.Thumbprint.Equals(allowedThumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating certifiction: " +ex.Message);
        }
        return false;
    }

    private static bool HasElevatedRights()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        System.Diagnostics.Debug.WriteLine(principal.IsInRole(WindowsBuiltInRole.Administrator));
        return principal.IsInRole(WindowsBuiltInRole.Administrator)
            || identity.IsSystem;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);
}