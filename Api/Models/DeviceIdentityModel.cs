

namespace intuneMigratorApi.Models;

public class DeviceIdentityModel
{
    public required string serialNumber { get; set; }
    public required string hardwareHash { get; set; }
    public string? groupTag { get; set; }
            
}