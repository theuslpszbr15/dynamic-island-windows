using System.ComponentModel;
using System.Diagnostics;

namespace DynamicIsland.Services;

/// <summary>
/// The phone connects to this PC, so Windows Firewall has to let the island receive connections (private and domain
/// networks). Windows asks once when the island starts listening, and the question is easy to miss or dismiss.
/// </summary>
public static class FirewallRule
{
    const string RuleName = "Dynamic Island (celular)";
    const int ActionAllow = 1, DirectionIn = 1, ProfilePrivate = 2, ProfileDomain = 1, ProtocolTcp = 6, ProtocolAny = 256;

    /// <summary>True/false from the firewall's rules (no admin needed to read them); null if they can't be read.</summary>
    public static bool? IsOpen(int port)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return null;
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return null;
            dynamic policy = Activator.CreateInstance(type)!;
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != DirectionIn || (int)rule.Action != ActionAllow) continue;
                var profiles = (int)rule.Profiles;
                if ((profiles & (ProfilePrivate | ProfileDomain)) == 0) continue;
                var protocol = (int)rule.Protocol;
                if (protocol != ProtocolTcp && protocol != ProtocolAny) continue;
                string? app = rule.ApplicationName;
                if (!string.IsNullOrEmpty(app))
                {
                    if (string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), exe, StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                string? ports = rule.LocalPorts;
                if (ports != null && ports.Split(',').Any(p => p.Trim() == port.ToString())) return true;
            }
            return false;
        }
        catch { return null; }
    }

    /// <summary>Adds the rule through an elevated netsh (Windows asks the user to confirm). Null on success, else a message.</summary>
    public static string? Open()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return "Não achei o caminho do programa.";
        // With UseShellExecute (needed for "runas") ArgumentList is ignored, so the arguments go in one string.
        var start = new ProcessStartInfo("netsh")
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" " +
                        "enable=yes profile=private,domain protocol=TCP",
        };
        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit(30_000);
            return process is { ExitCode: 0 } ? null : "O netsh não conseguiu criar a regra.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Você cancelou o pedido de administrador."; }
        catch (Exception ex) { return ex.Message; }
    }
}
