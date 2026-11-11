# Repasse (relay) do Dynamic Island

Deixa o celular alcançar a ilha do PC quando os dois não estão na mesma rede (4G, outra casa…).

É um cano cego: o celular continua fazendo TLS direto com o PC (certificado fixado no QR) e manda o
código de pareamento dentro do TLS, igual na rede local. O repasse só vê o nome da sala (um SHA-256 do
código, que não revela o código) e bytes criptografados. Ele não lê, não aprova e não inventa nada, e
não grava log do conteúdo.

## Publicar (grátis no Cloudflare)

Precisa de Node.js e de uma conta Cloudflare.

```bash
cd relay
npm install
npx wrangler login
npx wrangler deploy
```

(O `package.json` já libera os scripts de instalação do `esbuild` e do `workerd`, que o Wrangler precisa.)

O deploy mostra o endereço, algo como `https://dynamic-island-relay.<seu-usuario>.workers.dev`.
Na ilha: **Configurações → Celular → Fora de casa**, cole `wss://dynamic-island-relay.<seu-usuario>.workers.dev`
e clique em Salvar. O celular pareado recebe o endereço na próxima conexão (ou escaneie o QR de novo).

## Como funciona

| Caminho | Quem | Para quê |
| --- | --- | --- |
| `wss://host/pc/<sala>` | PC | controle: um por sala, o novo substitui o antigo; ping a cada 20 s |
| `wss://host/phone/<sala>` | celular | entra na sala; o PC recebe `{"type":"incoming","conn":"<id>"}` |
| `wss://host/pc/<sala>/<id>` | PC | túnel de dados para aquele celular |

- `sala` = hex minúsculo de `SHA256("dynamicisland-room|" + token)`.
- Túnel de dados: só frames binários (pedaços do fluxo TCP, sem fronteiras), até 64 KB cada. Frame de
  texto no túnel fecha o par. Fechar um lado fecha o outro.
- No máximo 6 celulares por sala. Celular cujo PC não abre o túnel em 15 s é derrubado.
- Códigos de close: `4400` requisição inválida, `4404` PC não está na sala, `4429` sala cheia.
- Durable Object com hibernação de WebSocket: sala parada não gasta nada.

`npm run check` confere os tipos; `npx wrangler dev` roda local para testar.
