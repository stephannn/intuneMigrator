using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace intuneMigratorClient.Services;

public class DeviceInfoService
{
    public async Task<string> GetSerialNumberAsync()
    {
        return await SendCommandAsync("GET_SERIAL") ?? "UNKNOWN";
    }

    public async Task<string?> GetHardwareHashAsync()
    {
        return await SendCommandAsync("GET_HASH");
    }

    public async Task<string> GetManufacturerAsync()
    {
        return await SendCommandAsync("GET_MANUFACTURER") ?? "UNKNOWN";
    }

    public async Task<string> GetModelAsync()
    {
        return await SendCommandAsync("GET_MODEL") ?? "UNKNOWN";
    }

    public async Task<string> GetHostnameAsync()
    {
        return await SendCommandAsync("GET_HOSTNAME") ?? "UNKNOWN";
    }

    public async Task<string> WipeDeviceAsync()
    {
        return await SendCommandAsync("WIPE_DEVICE") ?? "UNKNOWN";
    }

    public async Task<string> WipeCloudDeviceAsync()
    {
        return await SendCommandAsync("WIPE_CLOUD_DEVICE") ?? "UNKNOWN";
    }

    private async Task<string?> SendCommandAsync(string command)
    {
        //if (!OperatingSystem.IsWindows()) return "UNSUPPORTED_PLATFORM";

        try
        {
            using var client = new NamedPipeClientStream(".", "IntuneMigratorPipe", PipeDirection.InOut);
            await client.ConnectAsync(3000); // 3 second timeout

            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };

            await writer.WriteLineAsync(command);
            return await reader.ReadLineAsync();
        }
        catch (Exception Ex)
        {
            Console.WriteLine("Failed to connect to the service.");
            Console.WriteLine(Ex.ToString());
            return null;
        }
    }
}