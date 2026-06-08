using Microsoft.Graph;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using intuneMigratorApi.Models;

namespace intuneMigratorApi.Services;

public static class DeviceManagementService
{
    /// <summary>
    /// Search for a device if exists, shall return the device.
    /// </summary>
    public static async Task<WindowsAutopilotDeviceIdentity?> GetDeviceRegistrationAsync(GraphServiceClient graphServiceClient, string ?serialNumber = null, string ?deviceName = null, ILogger? logger = null)
    {

        try
        {
            if (string.IsNullOrEmpty(serialNumber) && string.IsNullOrEmpty(deviceName))
            {
                throw new ArgumentException("Either serialNumber or deviceName must be provided.");
            }

            /* var allDevices = await graphServiceClient
                .DeviceManagement
                .WindowsAutopilotDeviceIdentities
                .Request()
                .GetAsync();

            WindowsAutopilotDeviceIdentity? result = null;

            if (!string.IsNullOrEmpty(serialNumber))
            {
                result = allDevices
                .FirstOrDefault(d =>
                    string.Equals(d.SerialNumber, serialNumber,
                                StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrEmpty(serialNumber) && !string.IsNullOrEmpty(deviceName))
            {
                result = allDevices
                .FirstOrDefault(d =>
                    string.Equals(d.DisplayName, deviceName,
                                StringComparison.OrdinalIgnoreCase));
            }
            
            return result; */

            /*
            var page = await graphServiceClient
                .DeviceManagement
                .WindowsAutopilotDeviceIdentities
                .Request()
                .Select("id,serialNumber,displayName,managedDeviceId")
                .Top(500)
                .GetAsync();

            WindowsAutopilotDeviceIdentity? result = null;

            while (page != null)
            {

                if (!string.IsNullOrEmpty(serialNumber))
                {
                    result = page.FirstOrDefault(d =>
                        string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    result = page.FirstOrDefault(d =>
                        string.Equals(d.DisplayName, deviceName, StringComparison.OrdinalIgnoreCase));
                }

                if (result != null)
                {
                    return result;
                }

                if (page.NextPageRequest == null)
                {
                    break;
                }

                page = await page.NextPageRequest.GetAsync();
            }

            return result;
            */

            // _ is an invalid character in both serial number and device name, we can use it as a delimiter to split and get the clean serial number or device name for filtering.
            string? cleanSerialNumber = string.IsNullOrEmpty(serialNumber) ? null : serialNumber.Trim().Split('_')[0];
            string? cleanDeviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName.Trim().Split('_')[0];

            // Using a raw HTTP request with a server-side filter
            string filterQuery = !string.IsNullOrEmpty(cleanSerialNumber) 
                ? $"contains(serialNumber,'{cleanSerialNumber}')" 
                : $"contains(displayName,'{cleanDeviceName}')";

            var requestUrl = $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeviceIdentities?$filter={filterQuery}&$top=25";
            
            while (!string.IsNullOrEmpty(requestUrl))
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                
                // Let the Graph SDK append the Authorization Bearer token to our raw request
                await graphServiceClient.AuthenticationProvider.AuthenticateRequestAsync(httpRequest);
                
                var response = await graphServiceClient.HttpProvider.SendAsync(httpRequest);
                response.EnsureSuccessStatusCode();

                var jsonString = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(jsonString);

                if (jsonDoc.RootElement.TryGetProperty("value", out var valueArray))
                {
                    foreach (var element in valueArray.EnumerateArray())
                    {
                        var sn = element.TryGetProperty("serialNumber", out var snProp) ? snProp.GetString() : null;
                        var dn = element.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : null;

                        bool isMatch = (!string.IsNullOrEmpty(serialNumber) && string.Equals(sn, serialNumber, StringComparison.OrdinalIgnoreCase)) ||
                                       (string.IsNullOrEmpty(serialNumber) && !string.IsNullOrEmpty(deviceName) && string.Equals(dn, deviceName, StringComparison.OrdinalIgnoreCase));

                        if (isMatch)
                        {
                            return new WindowsAutopilotDeviceIdentity
                            {
                                Id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null,
                                SerialNumber = sn,
                                DisplayName = dn,
                                ManagedDeviceId = element.TryGetProperty("managedDeviceId", out var mdIdProp) ? mdIdProp.GetString() : null
                            };
                        }
                    }
                }

                requestUrl = jsonDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
            }
            
            return null;
        }
        catch (ServiceException ex)
        {
            logger?.LogError(ex, "Error searching for device with SerialNumber: {SerialNumber}", serialNumber);
            throw;
        }
    }

    /// <summary>
    /// Delete a device by its name (SerialNumber) or ID.
    /// </summary>
    public static async Task<bool> RemoveDeviceRegistrationAsync(GraphServiceClient graphServiceClient, string ?serialNumber = null, string ?deviceName = null, ILogger? logger = null, bool debug = false)
    {
        if (debug)
        {
            logger?.LogInformation("Debug: Simulating RemoveDeviceAsync for SerialNumber: {SerialNumber}", serialNumber);
            return true;
        }

        if (serialNumber == null && deviceName != null){
            var device = await GetDeviceRegistrationAsync(graphServiceClient, deviceName: deviceName, logger: logger);
            serialNumber = device?.Id;
        }

        if (string.IsNullOrEmpty(serialNumber))
        {
            logger?.LogWarning("Device not found for deletion: {SerialNumber}", serialNumber);
            return false;
        }

        try
        {
            await graphServiceClient.DeviceManagement.WindowsAutopilotDeviceIdentities[serialNumber]
                .Request()
                .DeleteAsync();
            
            logger?.LogInformation("Deleted device: {Id}", serialNumber);
            return true;
        }
        catch (ServiceException ex)
        {
            logger?.LogError(ex, "Error deleting device: {Id}", serialNumber);
            return false;
        }
    }

    /// <summary>
    /// Add a new device by the hash and a group tag.
    /// </summary>
    public static async Task<ImportedWindowsAutopilotDeviceIdentity> AddDeviceAsync(GraphServiceClient graphServiceClient, DeviceIdentityModel deviceIdentity, ILogger? logger = null, bool debug = false)
    {
        if (debug)
        {
            logger?.LogInformation("Debug: Simulating AddDeviceAsync for SerialNumber: {SerialNumber}", deviceIdentity.serialNumber);
            return new ImportedWindowsAutopilotDeviceIdentity
            {
                Id = "DEBUG-IMPORTED-ID",
                SerialNumber = deviceIdentity.serialNumber,
                GroupTag = deviceIdentity?.groupTag?.Trim(),
                State = new ImportedWindowsAutopilotDeviceIdentityState { DeviceImportStatus = ImportedWindowsAutopilotDeviceIdentityImportStatus.Complete }
            };
        }

        var newDevice = new ImportedWindowsAutopilotDeviceIdentity
        {
            SerialNumber = deviceIdentity.serialNumber.Trim(),
            //HardwareIdentifier = Encoding.ASCII.GetBytes(deviceIdentity.hardwareHash),
            HardwareIdentifier = Convert.FromBase64String(deviceIdentity.hardwareHash.Trim()),
            GroupTag = deviceIdentity?.groupTag?.Trim(),
            State = new ImportedWindowsAutopilotDeviceIdentityState { DeviceImportStatus = ImportedWindowsAutopilotDeviceIdentityImportStatus.Pending }
        };

        try
        {
            return await graphServiceClient.DeviceManagement.ImportedWindowsAutopilotDeviceIdentities
                .Request()
                .AddAsync(newDevice);
        }
        catch (ServiceException ex)
        {
            logger?.LogError(ex, "Error adding device: {SerialNumber}", deviceIdentity?.serialNumber);
            logger?.LogError("Error Message: {Message}, StatusCode: {StatusCode}, Code: {Code}", ex.Message, ex.StatusCode, ex.Error?.Code);
            throw;
        }
    }

    /// <summary>
    /// Find a device by the corporate identifier (manufacturer, model, serial number) and return the device ID if found.
    /// </summary>
    public static async Task<string?> GetDeviceByCorporateIdentifierAsync(GraphServiceClient graphServiceClient, string? manufacturer, string? model, string? serialNumber, ILogger? logger = null)
    {
        try
        {
            if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(serialNumber))
            {
                logger?.LogWarning("Manufacturer, Model, and SerialNumber must be provided for corporate identifier search.");
                return null;
            }

            string cleanManufacturer = manufacturer.Trim();
            string cleanModel = model.Trim();
            string cleanSerialNumber = serialNumber.Trim().Replace("_", ""); // Remove any underscores which are used as delimiters in our logic
            string exactIdentifier = $"{cleanManufacturer},{cleanModel},{cleanSerialNumber}";

            var requestUrl = $"https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities?$filter=contains(importedDeviceIdentifier,'{exactIdentifier}')&$top=50";
            
            while (!string.IsNullOrEmpty(requestUrl))
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                
                await graphServiceClient.AuthenticationProvider.AuthenticateRequestAsync(httpRequest);
                
                var response = await graphServiceClient.HttpProvider.SendAsync(httpRequest);
                
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && requestUrl.Contains("$filter"))
                {
                    logger?.LogWarning("Filter not supported on importedDeviceIdentities. Falling back to client-side filtering.");
                    requestUrl = "https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities?$top=50";
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var jsonString = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(jsonString);

                if (jsonDoc.RootElement.TryGetProperty("value", out var valueArray))
                {
                    foreach (var element in valueArray.EnumerateArray())
                    {
                        var identifier = element.TryGetProperty("importedDeviceIdentifier", out var idProp) ? idProp.GetString() : null;
                        var type = element.TryGetProperty("importedDeviceIdentityType", out var typeProp) ? typeProp.GetString() : null;
                        
                        // Client-side exact match check for the corporate identifier and type
                        if (string.Equals(identifier, exactIdentifier, StringComparison.OrdinalIgnoreCase) && 
                            string.Equals(type, "manufacturerModelSerial", StringComparison.OrdinalIgnoreCase))
                        {
                            return element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        }
                    }
                }

                requestUrl = jsonDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error searching for corporate device with SerialNumber: {SerialNumber}", serialNumber);
            throw;
        }
    }

    /// <summary>
    /// Delete a device by the corporate identifier (manufacturer, model, serial number).
    /// </summary>
    public static async Task<bool> RemoveDeviceByCorporateIdentifierAsync(GraphServiceClient graphServiceClient, string? manufacturer, string? model, string? serialNumber, ILogger? logger = null, bool debug = false)
    {
        if (debug)
        {
            logger?.LogInformation("Debug: Simulating RemoveDeviceByCorporateIdentifierAsync for device: {Manufacturer},{Model},{SerialNumber}", manufacturer, model, serialNumber);
            return true;
        }

        string? deviceId = await GetDeviceByCorporateIdentifierAsync(graphServiceClient, manufacturer, model, serialNumber, logger);

        if (string.IsNullOrEmpty(deviceId))
        {
            logger?.LogWarning("Corporate device not found for deletion: {Manufacturer},{Model},{SerialNumber}", manufacturer, model, serialNumber);
            return false;
        }

        try
        {
            var requestUrl = $"https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities/{deviceId}";
            var httpRequest = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
            await graphServiceClient.AuthenticationProvider.AuthenticateRequestAsync(httpRequest);
            
            var response = await graphServiceClient.HttpProvider.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
            
            logger?.LogInformation("Deleted corporate device registration: {Id}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error deleting corporate device registration: {Id}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Add a new device by the corporate identifier (manufacturer, model, serial number)
    /// </summary>
    public static async Task<bool> AddDeviceByCorporateIdentifierAsync(GraphServiceClient graphServiceClient, DeviceCorporateIdentityModel deviceIdentity, ILogger? logger = null, bool debug = false)
    {
        if (debug)
        {
            logger?.LogInformation("Debug: Simulating AddDeviceByCorporateAsync for device: {manufacturer} {model} {serialNumber}", deviceIdentity.manufacturer, deviceIdentity.model, deviceIdentity.serialNumber);
            return true;
        }

        var requestUrl = "https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities/importDeviceIdentityList";

        var payload = new
        {
            overwriteImportedDeviceIdentities = false,
            importedDeviceIdentities = new[]
            {
                new
                {
                    importedDeviceIdentityType = "manufacturerModelSerial",
                    importedDeviceIdentifier = $"{deviceIdentity.manufacturer.Trim()},{deviceIdentity.model.Trim()},{deviceIdentity.serialNumber.Trim()}"
                }
            }
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(payload);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            await graphServiceClient.AuthenticationProvider.AuthenticateRequestAsync(httpRequest);

            var response = await graphServiceClient.HttpProvider.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            logger?.LogInformation("Successfully added device by corporate identifier: {manufacturer},{model},{serialNumber}", deviceIdentity.manufacturer, deviceIdentity.model, deviceIdentity.serialNumber);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error adding device: {SerialNumber}", deviceIdentity?.serialNumber);
            throw;
        }
    }

    /// <summary>
    /// Wipes a device associated with the given serial number.
    /// </summary>
    public static async Task<bool> WipeDeviceAsync(GraphServiceClient graphServiceClient, string serialNumber, ILogger? logger = null, bool debug = false)
    {
        if (debug)
        {
            logger?.LogInformation("Debug: Simulating WipeDeviceAsync for SerialNumber: {SerialNumber}", serialNumber);
            return true;
        }

        var deviceIdentity = await GetDeviceRegistrationAsync(graphServiceClient, serialNumber, logger: logger);

        if (deviceIdentity == null)
        {
            logger?.LogWarning("Device with SerialNumber {SerialNumber} not found for wipe.", serialNumber);
            return false;
        }

        if (string.IsNullOrEmpty(deviceIdentity.ManagedDeviceId))
        {
            logger?.LogWarning("Device with SerialNumber {SerialNumber} is not managed (no ManagedDeviceId). Cannot wipe.", serialNumber);
            return false;
        }

        try
        {
            await graphServiceClient.DeviceManagement.ManagedDevices[deviceIdentity.ManagedDeviceId]
                .Wipe(false, false, null)
                .Request()
                .PostAsync();

            logger?.LogInformation("Wipe command sent for device: {SerialNumber} (ManagedID: {ManagedId})", serialNumber, deviceIdentity.ManagedDeviceId);
            return true;
        }
        catch (ServiceException ex)
        {
            logger?.LogError(ex, "Error wiping device: {SerialNumber}", serialNumber);
            throw;
        }
    }

    /// <summary>
    /// Removes the device from Intune (Managed Device).
    /// </summary>
    public static async Task<bool> RemoveDeviceAsync(GraphServiceClient graphServiceClient, string serialNumber, ILogger? logger = null, bool debug = false)
    {
        if (string.IsNullOrEmpty(serialNumber))
        {
            logger?.LogWarning("SerialNumber is required for RemoveDeviceAsync.");
            return false;
        }

        if (debug)
        {
            logger?.LogInformation("Debug: Simulating RemoveDeviceAsync for SerialNumber: {SerialNumber}", serialNumber);
            return true;
        }
        
        var deviceIdentity = await GetDeviceRegistrationAsync(graphServiceClient, serialNumber, logger: logger);

        if (deviceIdentity == null)
        {
            logger?.LogWarning("Device with SerialNumber {SerialNumber} not found for removal.", serialNumber);
            return false;
        }

        if (string.IsNullOrEmpty(deviceIdentity.Id))
        {
            logger?.LogWarning("Device with SerialNumber {SerialNumber} is not managed (no DeviceId). Cannot remove.", serialNumber);
            return false;
        }

        try
        {

            await graphServiceClient.DeviceManagement.ManagedDevices[deviceIdentity.Id]
                .Request()
                .DeleteAsync();
            
            logger?.LogInformation("Deleted Device: {Id}", deviceIdentity.Id);
            return true;
            
        }
        catch (ServiceException ex)
        {
            logger?.LogError(ex, "Error deleting Device with SerialNumber: {SerialNumber}", serialNumber);
            throw;
        }
        

    }
}