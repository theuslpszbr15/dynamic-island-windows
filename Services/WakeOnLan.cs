using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>What the phone needs to switch this PC on (Wake-on-LAN): the wired adapter's MAC and the LAN's broadcast
/// address. Plus what usually stops it working, for the settings to point out.</summary>
public static class WakeOnLan
{
    public sealed record Target(string Name, string Mac, string Broadcast, bool Connected);

    /// <summary>The wired adapter (the one with a gateway first): Wi-Fi rarely wakes a PC that is off.</summary>
    public static Target? Wired()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                            && n.GetPhysicalAddress().GetAddressBytes().Length == 6
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .ThenByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0);
            foreach (var nic in candidates)
            {
                var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                var broadcast = "255.255.255.255";
                var v4 = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null)
                {
                    var ip = v4.Address.GetAddressBytes();
                    var mask = v4.IPv4Mask.GetAddressBytes();
                    broadcast = new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()).ToString();
                }
                return new Target(nic.Name, mac, broadcast, nic.OperationalStatus == OperationalStatus.Up);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Windows' fast startup: "shut down" is really a hibernation, and many network cards don't wake from it.</summary>
    public static bool FastStartupOn()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return key?.GetValue("HiberbootEnabled") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Turns fast startup off (asks Windows for administrator rights). False if the person said no.</summary>
    public static bool TurnOffFastStartup()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("reg.exe",
                @"add ""HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power"" /v HiberbootEnabled /t REG_DWORD /d 0 /f")
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch { return false; } // cancelled at the UAC prompt
    }
}
