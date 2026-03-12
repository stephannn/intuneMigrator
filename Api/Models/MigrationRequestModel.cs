namespace intuneMigratorApi.Models;

public class MigrationRequestModel
{
    public string? DeviceName { get; set; }
    public string? HardwareHash { get; set; }
    public string? SerialNumber { get; set; }
    public bool? WipeDevice { get; set; }
    public bool Debug { get; set; } = false;
}