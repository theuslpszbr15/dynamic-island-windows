using System.Diagnostics;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// "Criar meu repasse": writes relay/ (embedded in the exe) to disk and opens a PowerShell window that installs it, signs in to the
/// person's Cloudflare account (in the browser) and publishes it. The address it gets is left in <see cref="ResultFile"/>,
/// which the settings pick up and use as the island's own relay.
/// </summary>
public static class RelaySetup
{
    public static string ResultFile => Path.Combine(IslandSettings.Folder, "own-relay.txt");
    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "relay");

    public static bool HasNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir.Trim('"'), "node.exe"))) return true;
        return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
    }

    public static void Start()
    {
        WriteSource();
        var script = Script
            .Replace("{{DIR}}", Folder)
            .Replace("{{RESULT}}", ResultFile);
        var path = Path.Combine(Path.GetTempPath(), "dynamic-island-criar-repasse.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true)); // BOM: Windows PowerShell reads the accents right
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -NoProfile -ExecutionPolicy Bypass -File \"{path}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>The relay's source, embedded in the exe (relay/…), written to <see cref="Folder"/>; node_modules is kept.</summary>
    static void WriteSource()
    {
        var assembly = typeof(RelaySetup).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("relay/", StringComparison.Ordinal)))
        {
            var target = Path.Combine(Folder, name["relay/".Length..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    /// <summary>The wss:// address a finished setup left behind (and forgets it), or null.</summary>
    public static string? TakeResult()
    {
        try
        {
            if (!File.Exists(ResultFile)) return null;
            var text = File.ReadAllText(ResultFile).Trim();
            File.Delete(ResultFile);
            return PhoneLink.NormalizeRelay(text);
        }
        catch { return null; }
    }

    const string Script = """
        $ErrorActionPreference = 'Stop'
        $Host.UI.RawUI.WindowTitle = 'Dynamic Island - criar meu repasse'
        # corporate proxies: go through with the signed-in Windows user
        [System.Net.WebRequest]::DefaultWebProxy.Credentials = [System.Net.CredentialCache]::DefaultNetworkCredentials
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host ''
        Write-Host '  Criar o seu repasse do Dynamic Island (grátis, na Cloudflare)' -ForegroundColor Cyan
        Write-Host '  O repasse deixa o celular falar com o PC de qualquer rede (4G, outra casa, trabalho).'
        Write-Host ''
        try {
            if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
                Write-Host '  Falta o Node.js neste PC. Vou abrir a página de download:' -ForegroundColor Yellow
                Write-Host '  instale a versão LTS (Avançar, Avançar, Concluir) e clique em "Criar meu repasse" de novo.'
                Start-Process 'https://nodejs.org/pt/download'
                return
            }

            Write-Host '  1/4  Preparando o código do repasse...' -ForegroundColor Cyan
            Set-Location '{{DIR}}'

            Write-Host '  2/4  Instalando (pode levar um minuto)...' -ForegroundColor Cyan
            npm install --no-audit --no-fund
            if ($LASTEXITCODE) { throw 'npm install falhou (sem internet ou proxy bloqueando?)' }

            Write-Host ''
            Write-Host '  3/4  Vai abrir o navegador: entre na sua conta Cloudflare' -ForegroundColor Cyan
            Write-Host '       (ou crie uma grátis, só e-mail e senha) e clique em Allow.'
            npx wrangler login
            if ($LASTEXITCODE) { throw 'O login na Cloudflare não terminou.' }

            Write-Host ''
            Write-Host '  4/4  Publicando o repasse...' -ForegroundColor Cyan
            Write-Host '       Se perguntar se quer registrar um subdomínio workers.dev, responda y e escolha um nome.'
            # Windows PowerShell turns redirected stderr (wrangler's warnings) into errors under 'Stop'
            $ErrorActionPreference = 'Continue'
            $out = npx wrangler deploy 2>&1 | ForEach-Object { Write-Host $_; "$_" } | Out-String
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($code) { throw 'A publicação falhou (veja a mensagem acima).' }
            $m = [regex]::Match($out, 'https://[A-Za-z0-9.-]+\.workers\.dev')
            if (-not $m.Success) { throw 'Publicou, mas não achei o endereço na saída acima. Copie o https://...workers.dev e cole na ilha.' }
            $relay = $m.Value -replace '^https://', 'wss://'
            Set-Content -Path '{{RESULT}}' -Value $relay -Encoding ASCII

            Write-Host ''
            Write-Host "  Pronto! Seu repasse: $relay" -ForegroundColor Green
            Write-Host '  Volte para as Configurações da ilha: ela já passa a usar este repasse.'
            Write-Host '  Depois, no celular, escaneie o código de pareamento de novo.'
        }
        catch {
            Write-Host ''
            Write-Host "  Não deu certo: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host '  Pode fechar esta janela e tentar de novo pelo botão "Criar meu repasse".'
        }
        """;
}
