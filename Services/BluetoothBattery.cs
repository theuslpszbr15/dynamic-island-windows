using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

public sealed record BluetoothDevice(string Name, int Battery);

/// <summary>Battery level that Windows already reads from connected Bluetooth devices (headphones, mice…).</summary>
public static class BluetoothBattery
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";
    const string Query = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    public static async Task<List<BluetoothDevice>> ListAsync()
    {
        List<BluetoothDevice> result = [];
        var devices = await DeviceInformation.FindAllAsync(Query, [BatteryKey, ConnectedKey], DeviceInformationKind.Device);
        foreach (var device in devices)
        {
            if (!device.Properties.TryGetValue(BatteryKey, out var battery) || battery == null) continue;
            if (device.Properties.TryGetValue(ConnectedKey, out var connected) && connected is false) continue;
            var level = Convert.ToInt32(battery);
            if (level is < 0 or > 100 || string.IsNullOrWhiteSpace(device.Name)) continue;
            if (result.Any(d => d.Name == device.Name)) continue;
            result.Add(new BluetoothDevice(device.Name.Trim(), level));
        }
        return result;
    }
}
