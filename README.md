<p align="center"><img src="docs/img/icon.png" width="96" alt="Ícone"></p>

# Dynamic Island para Windows

Uma ilha dinâmica no topo da tela, no estilo do iPhone, feita em C# / WPF (.NET 10).
Mostra música, volume, bateria, sistema, timer, notificações, microfone/câmera em uso, uma bandeja
de arquivos e o que seus agentes de IA (Copilot no VS Code, Claude Code) estão fazendo — sem
atrapalhar: os cliques passam por ela.

<p align="center">
  <img src="docs/demo.gif" width="640" alt="Animação mostrando como a Dynamic Island funciona">
</p>

## Baixar

1. Baixe **[DynamicIsland.exe](https://github.com/theuslpszbr15/dynamic-island-windows/releases/latest/download/DynamicIsland.exe)**
   (não precisa instalar o .NET).
2. Abra. Se o Windows mostrar "O Windows protegeu o computador", clique em *Mais informações → Executar assim mesmo*
   (o executável não é assinado).
3. No ícone da bandeja, clique em **Instalar no computador**: ela vai para
   `%LOCALAPPDATA%\Programs\DynamicIsland`, ganha atalho no menu Iniciar, inicia com o Windows e aparece em
   *Configurações → Aplicativos* para desinstalar. Não pede administrador.

## O que ela faz

| | |
|---|---|
| **Pipoca, o bichinho** — um gatinho que mora na ilha: segue o mouse com os olhos, dança com a música, fica azul enquanto o agente trabalha, laranja e pulando quando ele precisa de você, comemora quando termina e dorme quando nada acontece. Clique nele: ele se irrita; três vezes rápido, fica tonto. | <img src="docs/img/09-agent-alert.png" width="380"> |
| **Temas** — Obsidiana, Grafite, Ardósia, Oceano, Floresta, Café, Ameixa e Cereja. | <img src="docs/img/22-theme-ameixa.png" width="380"> |
| **Mídia** — capa, título, progresso e controles de qualquer player (Spotify, YouTube…). A cor de destaque vem da capa. | <img src="docs/img/02-now-playing.png" width="380"> |
| **Volume** — barra ao mudar o volume (role o mouse sobre ela para ajustar). | <img src="docs/img/04-volume.png" width="380"> |
| **Agentes de IA** — azul trabalhando, laranja precisa de você, verde concluído. | <img src="docs/img/10-expanded-agents.png" width="380"> |
| **Sistema** — CPU, memória, disco e bateria. | <img src="docs/img/06-expanded-system.png" width="380"> |
| **Bandeja de arquivos** — arraste arquivos até a ilha, arraste de volta quando precisar. | <img src="docs/img/07-expanded-files.png" width="380"> |
| **Timer / Pomodoro** — 5, 15, 25, 45 min ou 1 h; a contagem fica na ilha e ela toca um alarme no fim. | <img src="docs/img/15-expanded-timer.png" width="380"> |
| **Notificações do Windows** — WhatsApp, Teams, Slack, e-mail… aparecem na ilha com o ícone do app. | <img src="docs/img/17-notification.png" width="380"> |
| **Microfone e câmera** — avisa qual app começou a usar e deixa um ponto laranja (mic) ou verde (câmera) ao lado. | <img src="docs/img/18-microphone.png" width="380"> |
| **Formato notch** — colado na borda, com cantos invertidos. | <img src="docs/img/12-notch-expanded.png" width="380"> |

- **Discreta:** quando ociosa vira uma barrinha fina no topo; a janela é *click-through*, então abas do navegador continuam clicáveis.
- **Abre** parando o mouse sobre ela; a roda do mouse troca de página.
- **Vários monitores:** fica na tela principal, numa tela escolhida ou segue a janela ativa (respeita a escala de cada monitor).
- Some sozinha com app em tela cheia e não rouba o foco.

## Configurações

No ícone da bandeja (ou clique direito na ilha): formato pílula/notch, tema, bichinho, recolher quando ociosa, monitor,
mostrar notificações, iniciar com o Windows, conectar Copilot, hooks do Claude Code e instalar/desinstalar.

## Compilar

Requer Windows 10 (2004+) ou 11 e o .NET 10 SDK.

```powershell
dotnet build
.\bin\Debug\net10.0-windows10.0.22621.0\DynamicIsland.exe

# executável único (o mesmo da página de Releases)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

`DynamicIsland.exe --install` instala sem perguntar; `--uninstall` remove (é o que *Configurações → Aplicativos* chama).

## Agentes de IA

- **Copilot (VS Code / CLI):** menu da bandeja → *Conectar Copilot (VS Code)*. Cria
  `~/.copilot/hooks/dynamic-island.json`; vale para chats novos.
- **Claude Code:** menu da bandeja → *Copiar hooks do Claude Code* e cole em `~/.claude/settings.json`.
- **Qualquer script:**

  ```powershell
  DynamicIsland.exe agent --name "Build" --state working --text "dotnet build"
  # estados: working | waiting | done | idle | end
  ```

Os eventos chegam por um *named pipe* restrito ao usuário atual. O comando nunca escreve no stdout e
sempre sai com código 0, então não trava nem altera o agente.

## Desenvolvimento

`DynamicIsland.exe --snapshot <pasta>` renderiza todos os estados da ilha em PNG com dados de
demonstração (foi assim que as imagens acima foram geradas).

| Arquivo | Conteúdo |
|---|---|
| `IslandWindow.xaml(.cs)` | Interface, animação de mola, estados e páginas |
| `IslandWindow.Activities.cs` | Timer, notificações e indicador de microfone/câmera |
| `PetView.cs`, `IslandWindow.Pet.cs` | Pipoca (desenhado com formas WPF, humores e reações) e temas |
| `Services/NotificationService.cs` | Notificações do Windows (UserNotificationListener) |
| `Services/PrivacyMonitor.cs` | Apps usando microfone/câmera (mesmos dados do indicador do Windows) |
| `Services/Installer.cs` | Instalação por usuário, atalho e entrada em Aplicativos |
| `Services/MediaService.cs` | Sessão de mídia do Windows (GSMTC) |
| `Services/VolumeService.cs` | Volume via Core Audio |
| `Services/BatteryService.cs` | Eventos de energia |
| `Services/AgentHub.cs`, `AgentCli.cs` | Pipe dos agentes e CLI dos hooks |
