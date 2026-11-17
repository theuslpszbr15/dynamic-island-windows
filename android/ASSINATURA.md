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
