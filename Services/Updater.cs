using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DynamicIsland.Services;

public sealed record UpdateInfo(Version Version, string Url, string Sha256, string Notes);

/// <summary>
/// Looks for a newer GitHub release and downloads its single-file exe. The download must match the release's
/// SHA-256 digest, and once this build is code-signed, the new exe must carry a valid signature from the same
/// certificate (the digest alone only proves the file wasn't damaged on the way, not who published it).
/// </summary>
public static class Updater
{
    const string AssetName = "DynamicIsland.exe";

    /// <summary>"owner/name" of the GitHub repository releases come from: the UpdateRepository item in
    /// DynamicIsland.csproj, so a fork changes it in one place.</summary>
    public static string Repository { get; } =
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "theuslpszbr15/dynamic-island-windows";

    public static string RepositoryUrl => $"https://github.com/{Repository}";

    static string LatestApi => $"https://api.github.com/repos/{Repository}/releases/latest";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicIsland/{Current}");
        return http;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (latest <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url == null || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) return null;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            // No digest, no update: an unchecked exe is never installed.
            if (digest?.StartsWith("sha256:", StringComparison.Ordinal) != true)
            {
                Log.Warn("Updater", $"release {tag} has no sha256 digest for {AssetName}; ignored");
                return null;
            }
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(latest, url, digest[7..], notes);
        }
        return null;
    }

    /// <summary>Downloads to a fresh folder in %TEMP%; throws if the file doesn't match the release digest or
    /// (when this copy is signed) isn't signed by the same publisher.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info)
    {
        var bytes = await Http.GetByteArrayAsync(info.Url);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O arquivo baixado não confere com o da release.");

        var folder = Path.Combine(Path.GetTempPath(), "DynamicIsland-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"DynamicIsland-{info.Version}.exe");
        await File.WriteAllBytesAsync(path, bytes);

        if (!SamePublisher(Environment.ProcessPath, path))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("A nova versão não tem a mesma assinatura digital desta.");
        }
        return path;
    }

    /// <summary>True when the running exe is unsigned (nothing to compare yet), or when the downloaded exe has a
    /// valid Authenticode signature from the same certificate.</summary>
    static bool SamePublisher(string? current, string downloaded)
    {
        var mine = SignerThumbprint(current);
        if (mine == null) return true;
        return VerifySignature(downloaded) && string.Equals(SignerThumbprint(downloaded), mine, StringComparison.OrdinalIgnoreCase);
    }

    static string? SignerThumbprint(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // reading the embedded Authenticode certificate, not loading a cert file
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Thumbprint;
        }
        catch (CryptographicException) { return null; } // not signed
    }

    // ───────────────────────── WinVerifyTrust ─────────────────────────

    static bool VerifySignature(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = file,
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,       // WTD_STATEACTION_IGNORE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Updater.VerifySignature", ex);
            return false;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
