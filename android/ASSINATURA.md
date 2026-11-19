# Assinatura do APK (release)

O APK publicado é assinado com uma chave de release própria. A chave e a senha **nunca** vão para o git
(`*.jks`, `*.keystore` e `keystore.properties` estão no `.gitignore`).

## Configuração local (já feita neste PC)

- Keystore: `C:\Users\brend\.dynamic-island-signing\release.jks` (alias `dynamicisland`, RSA 4096, validade ~100 anos)
- `android/keystore.properties` (ignorado pelo git) com `storeFile`, `storePassword`, `keyAlias`, `keyPassword`
- A senha existe só nesse arquivo local. Para gerar em outra máquina:

```bash
keytool -genkeypair -keystore release.jks -storetype PKCS12 -alias dynamicisland -keyalg RSA -keysize 4096 -validity 36500
```

Depois crie `android/keystore.properties` com as quatro chaves acima. Em CI, use as variáveis
`DI_STORE_FILE`, `DI_STORE_PASSWORD`, `DI_KEY_ALIAS` e `DI_KEY_PASSWORD` (secrets).

Compilar: `gradlew assembleRelease`. Sem chave, o build avisa e cai na chave de debug (não publique esse APK).

## BACKUP DA CHAVE (importante)

Quem perder o `release.jks` ou a senha **nunca mais consegue publicar atualização** que instale por cima do app já
instalado: todos os usuários teriam de desinstalar e reinstalar.

1. Copie `release.jks` e a senha (de `keystore.properties`) para pelo menos dois lugares fora do PC:
   gerenciador de senhas + pendrive/nuvem privada. Não coloque no repositório nem em release do GitHub.
2. Para CI: `base64 -w0 release.jks` vira o secret `ANDROID_KEYSTORE_BASE64`; senhas em secrets separados.
3. Confira o backup: `keytool -list -keystore release.jks` com a senha guardada.

## Aviso: migração do APK de debug

Os APKs antigos foram assinados com a chave de debug. O Android não atualiza um app por cima de outro com
assinatura diferente, então **quem tem o APK debug precisa desinstalar uma vez** e instalar o APK de release.
(Os dados locais do app, como o pareamento, são perdidos; é preciso parear de novo.) Daqui para a frente as
atualizações funcionam normalmente enquanto a chave for a mesma.

## CI de release (`.github/workflows/release.yml`)

Publicar uma tag `vX.Y.Z` (igual ao `<Version>` do `DynamicIsland.csproj`) builda `DynamicIsland.exe`,
`DynamicIsland-Leve.exe` e `DynamicIsland-Android.apk`, anexa à release com `SHA256SUMS.txt`, garante a linha
`versionCode: N` nas notas e confere os digests que o GitHub registrou. Falta de exe, apk ou digest derruba o job
(e devolve a release para rascunho). Rodar o workflow à mão (`workflow_dispatch`) só builda e confere, não publica.

Secrets do repositório (Settings → Secrets and variables → Actions) para assinar o APK:

| Secret | Conteúdo |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | `base64 -w0 release.jks` |
| `ANDROID_STORE_PASSWORD` | senha do keystore (`DI_STORE_PASSWORD` no gradle) |
| `ANDROID_KEY_ALIAS` | `dynamicisland` |
| `ANDROID_KEY_PASSWORD` | senha da chave |

Sem esses secrets, uma tag **não publica** (o APK cairia na chave de debug); só o ensaio manual segue com aviso.

## Por que a v1.8.8 pede desinstalar (e como evitar)

O Android só atualiza um app por cima de outro quando os dois foram assinados com a **mesma chave**. O app que está
nos celulares (até a v1.8.7, Android 1.5.0) foi assinado com a chave de debug desta máquina
(`C:\Users\brend\.android\debug.keystore`, SHA-256 `ea7480fd...87ad`). O APK da v1.8.8 foi assinado com a chave de
release (certificado Horus, SHA-256 `7bf076c2...1939`), então o celular recusa a atualização por cima.

- **Caminho adotado:** trocar a chave agora, uma única vez. Desinstalar o app antigo, instalar o da v1.8.8 e parear de
  novo. Dali em diante toda atualização funciona por cima.
- **Alternativa (atualiza por cima, sem desinstalar):** assinar o APK da v1.8.8 com a chave de debug
  (`apksigner sign --ks %USERPROFILE%\.android\debug.keystore ...`). O pareamento e os dados são mantidos. O custo é
  manter a chave de debug (sem proteção e só neste PC) como chave oficial; a migração para a chave de release fica
  para depois, com a mesma desinstalação.
