# Repasse (relay) do Dynamic Island

Deixa o celular alcançar a ilha do PC quando os dois não estão na mesma rede (4G, outra casa…).

É um cano cego: o celular continua fazendo TLS direto com o PC (certificado fixado no QR) e manda o
código de pareamento dentro do TLS, igual na rede local. O repasse só vê o nome da sala (um SHA-256 do
código, que não revela o código) e bytes criptografados. Ele não lê, não aprova e não inventa nada, e
não grava log do conteúdo.

## Repasse público (padrão)

A ilha já vem com um repasse público ligado para pareamentos novos
(`wss://dynamic-island-relay.brendoribeiron.workers.dev`): o usuário só escaneia o QR, sem conta e sem deploy.
O QR leva `relay=` mas não leva chave. Para não ficar caro nem virar túnel grátis, ele tem limites:

- por sala (pareamento): 50 MB por dia de túnel (somando os dois sentidos) e 30 conexões novas por minuto;
- por endereço IP: 60 conexões novas por minuto (binding de Rate Limiting da Cloudflare, aproximado e por local);
- arquivos pelo repasse: até 10 MB por envio (o PC e o celular conferem; na rede local continuam 30 MB por arquivo).

Passou de um limite, a conexão fecha com `4429` e o app e a ilha mostram
"limite do repasse atingido, tente mais tarde ou use seu próprio repasse".

## Publicar o seu (grátis no Cloudflare)

Precisa de Node.js e de uma conta Cloudflare.

```bash
cd relay
npm install
npx wrangler login
npx wrangler deploy
```

(O `package.json` já libera os scripts de instalação do `esbuild` e do `workerd`, que o Wrangler precisa. O binding
de Rate Limiting do `wrangler.toml` pede Wrangler 4.36 ou mais novo.)

A chave é opcional: `npx wrangler secret put RELAY_KEY` (16 a 128 caracteres base64url). Com ela, quem manda
`Authorization: Bearer <chave>` precisa mandar a certa; quem não manda nada entra do mesmo jeito.

O deploy mostra o endereço, algo como `https://dynamic-island-relay.<seu-usuario>.workers.dev`.
Na ilha: **Configurações → Celular → Fora de casa → Usar meu próprio repasse**, cole
`wss://dynamic-island-relay.<seu-usuario>.workers.dev` (e a chave, se tiver) e clique em Salvar. O celular pareado
recebe o endereço na próxima conexão (ou escaneie o QR de novo).

## Discord (quem entra na call)

O mesmo Worker faz a troca de chave do **app do Discord da ilha**, para o usuário só clicar em *Conectar*:

- `GET /discord/app` → `{"client_id":"…"}` (público; a ilha pergunta qual app usar).
- `POST /discord/token` com `{"grant_type":"authorization_code","code":"…"}` ou `{"grant_type":"refresh_token","refresh_token":"…"}`:
  o Worker junta o Client Secret e repassa ao `discord.com`. Não guarda nem registra nada; tem o mesmo limite por IP.

Configurar (uma vez, com o app criado no Discord Developer Portal e `http://localhost` em OAuth2 › Redirects):

```bash
cd relay
npx wrangler secret put DISCORD_CLIENT_ID
npx wrangler secret put DISCORD_CLIENT_SECRET
npx wrangler deploy
```

Sem esses dois segredos as rotas respondem `503 not_configured` e a ilha avisa. Lembre: o Discord só libera o RPC
para o dono do app e até 50 testers (Developer Portal › App Testers).

## Como funciona

| Caminho | Quem | Para quê |
| --- | --- | --- |
| `wss://host/pc/<sala>` | PC | controle: assina o desafio; o novo substitui o antigo só depois de assinar |
| `wss://host/phone/<sala>` | celular | entra na sala; o PC recebe `{"type":"incoming","conn":"<id>"}` |
| `wss://host/pc/<sala>/<id>` | PC | túnel de dados para aquele celular |

- `sala` = hex minúsculo de `SHA256("dynamicisland-room|" + token)`.
- Dono da sala (confiança no primeiro uso): ao abrir o controle o repasse manda
  `{"type":"challenge","nonce":"<hex>"}` e o PC responde `{"type":"auth","key":"<Ed25519, hex>","sig":"<hex>"}`,
  assinando `dynamicisland-relay-auth|<sala>|<nonce>`. A primeira chave fica guardada; depois só ela entra
  (outra chave ou assinatura errada = `4401`), então quem descobrir a sala não derruba o PC verdadeiro. Até assinar,
  o PC não está na sala. "Parear de novo" gera token, sala e chave novos. Uma ilha antiga com a chave do repasse
  (Bearer certo) numa sala que nunca viu assinatura entra sem assinar, para continuar funcionando.
- Túnel de dados: só frames binários (pedaços do fluxo TCP, sem fronteiras), até 64 KB cada. Frame de
  texto no túnel fecha o par, exceto o `ping`, que o repasse responde `pong` sozinho. Fechar um lado fecha o outro.
- Manter vivo não acorda a sala: pings de WebSocket e o `ping` de texto são respondidos pelo runtime sem acordar o
  Durable Object. O ping do app dentro do TLS (que passa pela sala) vai a cada ~5 min pelo repasse.
- No máximo 6 celulares por sala. Celular cujo PC não abre o túnel em 15 s é derrubado; controle que não assina em
  10 s também.
- Códigos de close: `4400` requisição inválida, `4401` o PC não provou a chave da sala, `4404` PC não está na sala,
  `4429` limite (motivo `room full`, `daily limit` ou `rate limit`). HTTP `401`: Bearer com chave errada.
- Durable Object com hibernação de WebSocket: sala parada não gasta nada.

`npm run check` confere os tipos; `npm test` roda os testes (Worker e Durable Object de verdade num workerd local,
com PCs e celulares falsos); `npx wrangler dev` roda local para testar.
