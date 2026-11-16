using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>What the phone needs to switch this PC on (Wake-on-LAN): the wired adapter's MAC and the LAN's broadcast
/// address. Plus what usually stops it working, for the settings to point out.</summary>
public static class WakeOnLan
{
    public sealed record Target(string Name, string Mac, string Broadcast, bool Connected, string Description);

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
                return new Target(nic.Name, mac, broadcast, nic.OperationalStatus == OperationalStatus.Up, nic.Description);
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

    /// <summary>Windows lets this card wake the PC (powercfg's wake_armed list, readable without admin).</summary>
    public static bool CardArmed(Target target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/devicequery wake_armed")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Split('\n').Any(line => line.Trim().Equals(target.Description, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Everything Windows-side in one administrator prompt: fast startup off, "Wake on Magic Packet" (and the
    /// driver's wake-from-shutdown options, when it has them) on, and the card allowed to wake the PC. The card restarts
    /// once if a driver option changed (a few seconds without network). False if the person said no.</summary>
    public static bool Prepare(Target target)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            reg.exe add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f | Out-Null
            $name = {{Quote(target.Name)}}
            $changed = $false
            if ((Get-NetAdapterPowerManagement -Name $name).WakeOnMagicPacket -ne 'Enabled') {
                Set-NetAdapterPowerManagement -Name $name -WakeOnMagicPacket Enabled -NoRestart; $changed = $true
            }
            $keys = '*WakeOnMagicPacket', 'S5WakeOnLan', 'EnablePME', 'WakeOnMagicPacketFromS5', '*ModernStandbyWoLMagicPacket'
            Get-NetAdapterAdvancedProperty -Name $name -AllProperties |
                Where-Object { $keys -contains $_.RegistryKeyword -and $_.ValidRegistryValues -contains '1' -and "$($_.RegistryValue)" -ne '1' } |
                ForEach-Object { Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $_.RegistryKeyword -RegistryValue 1 -NoRestart; $changed = $true }
            powercfg.exe /deviceenablewake {{Quote(target.Description)}}
            if ($changed) { Restart-NetAdapter -Name $name }
            exit 0
            """;
        var path = Path.Combine(Path.GetTempPath(), "dynamic-island-ligar-pc.ps1");
        try
        {
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{path}\"")
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(90_000);
            return process?.ExitCode == 0;
        }
        catch { return false; } // cancelled at the UAC prompt
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>The motherboard maker and where its BIOS keeps the Wake-on-LAN switch (the part Windows can't change).</summary>
    public static (string Maker, string Where) BiosHint() => Bios.Value;

    static readonly Lazy<(string, string)> Bios = new(ReadBiosHint);

    static (string, string) ReadBiosHint()
    {
        var maker = "";
        try
        {
            using var boards = new System.Management.ManagementObjectSearcher("SELECT Manufacturer FROM Win32_BaseBoard");
            maker = boards.Get().Cast<System.Management.ManagementObject>().Select(b => b["Manufacturer"]?.ToString()).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim() ?? "";
        }
        catch { }
        bool Is(string s) => maker.Contains(s, StringComparison.OrdinalIgnoreCase);
        var where =
            Is("ASUS") ? "Advanced › APM Configuration: \"Power On By PCI-E\" = Enabled e \"ErP Ready\" = Disabled" :
            Is("Gigabyte") ? "Settings › Platform Power: \"Wake on LAN\" = Enabled e \"ErP\" = Disabled" :
            Is("Micro-Star") || Is("MSI") ? "Settings › Advanced › Wake Up Event Setup: \"Resume By PCI-E Device\" = Enabled; e \"ErP Ready\" = Disabled" :
            Is("ASRock") ? "Advanced › ACPI Configuration: \"PCIE Devices Power On\" = Enabled" :
            Is("Dell") ? "Power Management › Wake on LAN: \"LAN Only\"" :
            Is("HP") || Is("Hewlett") ? "Advanced › Built-In Device Options: marque \"S5 Wake On LAN\"" :
            Is("Lenovo") ? "Power › Automatic Power On: \"Wake on LAN\" = Primary (ou Enabled)" :
            "procure \"Wake on LAN\", \"Power On By PCI-E\" ou \"Resume by LAN\" e deixe Enabled; se tiver \"ErP\", deixe Disabled";
        return (maker, where);
    }

    /// <summary>Restarts straight into the BIOS/UEFI setup (asks for administrator rights). False if the person said no
    /// or the PC can't (old BIOS without UEFI).</summary>
    public static bool RestartToBios()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /fw /t 0")
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }
}
