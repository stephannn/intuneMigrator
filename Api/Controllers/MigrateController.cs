using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Identity.Web.Resource;
using intuneMigratorApi.Data;
using intuneMigratorApi.Models;
using intuneMigratorApi.Services;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace intuneMigratorApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
public class MigrateController : ControllerBase
{
    private readonly SourceTenantClientFactory _sourceTenantFactory;
    private readonly GraphServiceClient _graphServiceClientDestination;
    private readonly ILogger<MigrateController> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly IConfiguration _configuration;
    private readonly IntuneMigratorDBContext _dbContext;

    public MigrateController(
        SourceTenantClientFactory sourceTenantFactory,
        [FromKeyedServices("Destination")] GraphServiceClient graphServiceClientDestination,
        ILogger<MigrateController> logger,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        IntuneMigratorDBContext dbContext)
    {
        _sourceTenantFactory = sourceTenantFactory;
        _graphServiceClientDestination = graphServiceClientDestination;
        _logger = logger;
        _memoryCache = memoryCache;
        _configuration = configuration;
        _dbContext = dbContext;
    }

    [HttpPost("/api/MigrationRequest")]
    public async Task<IActionResult> Post([FromBody] MigrationRequestModel request)
    {
        var username = User.Identity?.Name
                     ?? User.FindFirst(ClaimTypes.Name)?.Value 
                     ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                     ?? User.FindFirst("oid")?.Value 
                     ?? "Anonymous";

        if (username == "Anonymous")
        {
            return Unauthorized(new { Message = "User identity could not be determined." });
        }

        var tenantContext = _sourceTenantFactory.GetTenantContext(User);
        if (tenantContext == null)
        {
            return Unauthorized(new { Message = "No valid source tenant configuration found for the authenticated user's role." });
        }

        var _graphServiceClientSource = tenantContext.GraphClient;
        var migrationConfig = tenantContext.TenantConfig.GetSection("Migration");
        if (!migrationConfig.Exists()) migrationConfig = _configuration.GetSection("Migration"); // Fallback

        var timeoutSeconds = migrationConfig.GetValue<int?>("DevicesRequestTimeout");
        
        if (timeoutSeconds.HasValue && timeoutSeconds.Value > 0)
        {
            if (_memoryCache.TryGetValue($"RateLimit_{username}", out _))
            {
                return StatusCode(429, $"Too Many Requests. Please wait {timeoutSeconds} seconds before trying again.");
            }

            _memoryCache.Set($"RateLimit_{username}", true, TimeSpan.FromSeconds(timeoutSeconds.Value));
        }

        if (string.IsNullOrWhiteSpace(request.HardwareHash) || string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return BadRequest("HardwareHash and SerialNumber are required.");
        }

        _logger.LogInformation("Received migration request for Device: {DeviceName}, Serial: {SerialNumber}", request.DeviceName, request.SerialNumber);

        var migrationId = Guid.NewGuid();
        var groupTag = migrationConfig.GetValue<string>("GroupTag") ?? String.Empty;

        async Task LogStatus(MigrationStatus status, string message)
        {
            _dbContext.MigrationStatuses.Add(new MigrationStatusModel
            {
                Id = migrationId,
                Status = status,
                SerialNumber = request.SerialNumber,
                DeviceName = request.DeviceName,
                Message = message,
                Timestamp = DateTime.UtcNow,
                Username = username,
                GroupTag = groupTag,
                Debug = request.Debug
            });
            await _dbContext.SaveChangesAsync();
        }

        await LogStatus(MigrationStatus.Processing, "Migration started");

        try
        {
            var mustDeviceExistInSource = migrationConfig.GetValue<bool>("SourceTenantDeviceMustExists");
            _logger.LogDebug("SourceTenantDeviceMustExists setting: {Value}", mustDeviceExistInSource);

            var shouldRemoveDeviceRegistration = migrationConfig.GetValue<bool>("DeviceRegistrationRemoval");
            _logger.LogDebug("DeviceRegistrationRemoval setting: {Value}", shouldRemoveDeviceRegistration);

            var shouldRemoveDevice = migrationConfig.GetValue<bool>("DeviceRemoval");
            _logger.LogDebug("DeviceRemoval setting: {Value}", shouldRemoveDevice);

            var existingDevice = await DeviceManagementService.GetDeviceAsync(_graphServiceClientSource, serialNumber: request.SerialNumber, deviceName: request.DeviceName, logger: _logger);
                                
            if (existingDevice == null && mustDeviceExistInSource == true)
            {
                await LogStatus(MigrationStatus.Failed, "Device does not exist in source tenant.");
                return NotFound(new { Message = "Device does not exist in source tenant but is required for migration.", Status = "ExistsInCurrent" });
            }

            bool removalRegistrationSuccess = false;
            bool removalSuccess = false;

            if (existingDevice != null)
            {
                // Wiping
                if (request.WipeDevice == true)
                {
                    var wipeSuccess = await DeviceManagementService.WipeDeviceAsync(_graphServiceClientSource, existingDevice.SerialNumber, _logger, request.Debug);
                    if (wipeSuccess)
                    {
                        await LogStatus(MigrationStatus.WipeRequested, "Wipe command sent");
                    } else
                    {
                        await LogStatus(MigrationStatus.Failed, "Failed to send wipe command.");
                    }
                }

                // Remove device registration from source
                if (shouldRemoveDeviceRegistration)
                {
                    removalRegistrationSuccess = await DeviceManagementService.RemoveDeviceRegistrationAsync(_graphServiceClientSource, serialNumber: existingDevice.Id, logger: _logger, debug: request.Debug);
                    
                    if (removalRegistrationSuccess) {
                        await LogStatus(MigrationStatus.RemovedFromSource, "Device registration removed from source");
                    } else
                    {
                        await LogStatus(MigrationStatus.Failed, "Failed to remove device registration from source.");
                        return StatusCode(500, new { Message = "Failed to remove device registration from source tenant.", Status = "Error" });
                    }
                    
                } else
                {
                    _logger.LogInformation("DeviceRegistrationRemoval is false, skipping device registration removal for Serial: {SerialNumber}", existingDevice.SerialNumber);
                }

                // Remove device from source
                if (shouldRemoveDevice)
                {
                    removalSuccess = await DeviceManagementService.RemoveDeviceAsync(_graphServiceClientSource, serialNumber: existingDevice.SerialNumber, logger: _logger, debug: request.Debug);
                    if (removalSuccess)
                    {
                        await LogStatus(MigrationStatus.RemovedFromSource, "Device removed from source");
                    } else
                    {
                        await LogStatus(MigrationStatus.Failed, "Failed to remove device from source.");
                        return StatusCode(500, new { Message = "Failed to remove device from source tenant.", Status = "Error" });
                    }
                } else
                {
                    _logger.LogInformation("DeviceRemoval is false, skipping device removal for Serial: {SerialNumber}", existingDevice.SerialNumber);
                }

            }

            if ((removalRegistrationSuccess || !shouldRemoveDeviceRegistration) && (removalSuccess || !shouldRemoveDevice) || (mustDeviceExistInSource == false && existingDevice == null))
            {
                await Task.Delay(2000);

                try
                {
                    
                    var resultAddDevice = await DeviceManagementService.AddDeviceAsync(_graphServiceClientDestination, new DeviceIdentityModel
                    {
                        serialNumber = request.SerialNumber,
                        hardwareHash = request.HardwareHash,
                        groupTag = groupTag
                    }, _logger, request.Debug);

                    if (resultAddDevice != null)
                    {
                        await LogStatus(MigrationStatus.AddedToDestination, "Device added to destination");
                    } else
                    {
                        await LogStatus(MigrationStatus.Failed, "Failed to add device to destination.");
                        return StatusCode(500, new { Message = "Failed to add device to destination tenant.", Status = "Error" });
                    }
                }
                catch (ServiceException ex)
                {
                    if (ex.Message.Contains("ZtdDeviceAssignedToOtherTenant") || ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        await LogStatus(MigrationStatus.Failed, "Device is registered in another tenant.");
                        return Conflict(new { Message = "Device is registered in another tenant.", Status = "Conflict" });
                    }

                    _logger.LogError(ex, "Graph API Error during device addition. Status: {StatusCode}, Code: {Code}, Message: {Message}", ex.StatusCode, ex.Error?.Code, ex.Message);
                    await LogStatus(MigrationStatus.Failed, $"Graph API Error during device addition: {ex.Message}");
                    return StatusCode((int)ex.StatusCode, new { Message = "Error communicating with Microsoft Graph during device addition.", Details = ex.Message });
                }
            }
            
            await LogStatus(MigrationStatus.Success, "Migration finished.");

            return Ok(new { Message = "Migration finished.", Status = "Received" });
        }
        catch (ServiceException ex)
        {
            await LogStatus(MigrationStatus.Failed, ex.Message);

            // If we attempted an import and got a conflict:
            if (ex.Message.Contains("ZtdDeviceAssignedToOtherTenant") || ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                 return Conflict(new { Message = "Device is registered in another tenant.", Status = "Conflict" });
            }

            _logger.LogError(ex, "Graph API Error. Status: {StatusCode}, Code: {Code}, Message: {Message}", ex.StatusCode, ex.Error?.Code, ex.Message);
            return StatusCode((int)ex.StatusCode, new { Message = "Error communicating with Microsoft Graph.", Details = ex.Message });
        }
    }

    [HttpPost("/api/MigrationCheck")]
    public async Task<IActionResult> MigrationCheck([FromBody] MigrationRequestModel request)
    {
        try
        {
            var username = User.Identity?.Name
                     ?? User.FindFirst(ClaimTypes.Name)?.Value 
                     ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                     ?? User.FindFirst("oid")?.Value 
                     ?? "Anonymous";

            if (username == "Anonymous")
            {
                return Unauthorized(new { Message = "User identity could not be determined." });
            }

            var tenantContext = _sourceTenantFactory.GetTenantContext(User);
            if (tenantContext == null)
            {
                return Unauthorized(new { Message = "No valid source tenant configuration found for the authenticated user's role." });
            }

            var _graphServiceClientSource = tenantContext.GraphClient;
            var migrationConfig = tenantContext.TenantConfig.GetSection("Migration");
            if (!migrationConfig.Exists()) migrationConfig = _configuration.GetSection("Migration"); // Fallback

            var mustDeviceExistInSource = migrationConfig.GetValue<bool>("SourceTenantDeviceMustExists");
            _logger.LogDebug("SourceTenantDeviceMustExists setting: {Value}", mustDeviceExistInSource);

            _logger.LogInformation("Checking device existence for Device: {DeviceName}, Serial: {SerialNumber}", request.DeviceName, request.SerialNumber);
            var existingDevice = await DeviceManagementService.GetDeviceAsync(_graphServiceClientSource, serialNumber: request.SerialNumber, deviceName: request.DeviceName, logger: _logger);
       
            if (existingDevice == null)
            {
                _logger.LogInformation("Device not found in source tenant for Serial: {SerialNumber}", request.SerialNumber);
                if(mustDeviceExistInSource == true)
                {
                    _logger.LogInformation("SourceTenantDeviceMustExists is true, returning NotFound for Serial: {SerialNumber}", request.SerialNumber);    
                    return NotFound(new { Message = "Device does not exist in source tenant but is required for migration.", Status = "ExistsInCurrent" });
                }
            }

            // check if exists in new tenant already
            _logger.LogDebug("Checking device existence in destination tenant for Serial: {SerialNumber}", request.SerialNumber);
            var deviceInDestination = await DeviceManagementService.GetDeviceAsync(_graphServiceClientDestination, serialNumber: request.SerialNumber, logger: _logger);

            if (deviceInDestination != null)
            {
                _logger.LogInformation("Device already exists in destination tenant for Serial: {SerialNumber}", request.SerialNumber);
                return Conflict(new { Message = "Device already exists in destination tenant.", Status = "Conflict" });
            }

            _logger.LogInformation("Device info received and validated for Serial: {SerialNumber}. Ready for migration.", request.SerialNumber);
            return Ok(new { Message = "Device info received. Ready for migration.", Status = "Received" });
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during migration check: {Message}", ex.Message);
            return StatusCode(500, new { Message = "Internal server error during migration check.", Details = ex.Message });
        }

    }
    
        
}