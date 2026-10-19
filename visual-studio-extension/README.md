# Claude Limits — Visual Studio eklentisi

Claude Pro/Max abonelik limitlerini (5 saatlik oturum, haftalık, model bazlı haftalık, ek kullanım) ve
Anthropic API limitlerini (dakikalık rate limit, aylık/günlük harcama, bugünkü tokenlar) Visual Studio'nun
durum çubuğunda ve **Claude Limits** araç penceresinde gösterir.

- **Desteklenen sürümler:** Visual Studio 2022 (17.x) ve Visual Studio 2026 (18.x), x64 ve Arm64; Community, Professional, Enterprise.
- **Yalnızca Windows.** Visual Studio for Mac kullanımdan kaldırıldığı için bu parça Windows'a özeldir (macOS'ta VS Code eklentisini veya tray uygulamasını kullanın).
- VS Code eklentisi ve tray uygulamasıyla aynı önbellek dosyasını (`%USERPROFILE%\.claude-limits\cache.json`, bkz. `docs/cache-format.md`) paylaşır; birden çok editör açıkken uç noktalar gereksiz çağrılmaz.

## Gereksinimler

- Windows 10/11
- Visual Studio 2022 17.0+ (veya 2026), **Visual Studio extension development** iş yükü
- .NET SDK 8.0+ (yalnızca birim testleri için, `dotnet test`)

## Visual Studio'da derleme

1. `visual-studio-extension\ClaudeLimits.sln` dosyasını açın.
2. Başlangıç projesi olarak **ClaudeLimits.VisualStudio**'yu seçin.
3. **F5** → eklenti yüklü olarak **Experimental Instance** (`devenv /rootsuffix Exp`) açılır.

## Komut satırı / CI

```powershell
./build.ps1                 # restore + Release derleme + testler + artifacts\ClaudeLimits.vsix
./build.ps1 -SkipTests
```

GitHub Actions (`windows-latest`) adımları:

```yaml
- uses: actions/checkout@v4
- uses: microsoft/setup-msbuild@v2
- uses: actions/setup-dotnet@v4
  with: { dotnet-version: 8.0.x }
- run: msbuild visual-studio-extension/ClaudeLimits.sln /t:Restore /p:Configuration=Release
- run: msbuild visual-studio-extension/ClaudeLimits.sln /t:Build /p:Configuration=Release /p:DeployExtension=false /m
- run: dotnet test visual-studio-extension/ClaudeLimits.Core.Tests/ClaudeLimits.Core.Tests.csproj -c Release
# Çıktı: visual-studio-extension/ClaudeLimits.VisualStudio/bin/Release/ClaudeLimits.VisualStudio.vsix
# (build.ps1 bunu visual-studio-extension/artifacts/ClaudeLimits.vsix olarak kopyalar)
```

Core kütüphanesi (`netstandard2.0`) ve testleri (`net8.0`) Linux/macOS'ta da çalışır:
`dotnet test visual-studio-extension/ClaudeLimits.Core.Tests`.

## Kurulum

`ClaudeLimits.vsix` dosyasına çift tıklayın; VSIX Installer kurulu Visual Studio 2022 ve 2026 örneklerini
listeler. Visual Studio'yu yeniden başlatın. Kaldırmak için **Uzantılar > Uzantıları Yönet**.

## Kullanım

- **Durum çubuğu:** sağ altta `Claude 5s 42% · 7g 18%`. Uyarı eşiğinde turuncu, kritik eşikte kırmızı zemin.
  Üzerine gelince tüm ayrıntılar, tıklayınca araç penceresi açılır.
- **Araç penceresi:** **Görünüm > Diğer Pencereler > Claude Limits** veya **Araçlar > Claude Limits**.
  Abonelik ve API bölümleri, **Yenile** ve **Ayarlar** düğmeleri.
- **Araçlar > Claude Limits: Yenile:** zorla yenileme (yine de en az 15 sn arayla; HTTP 429 sonrası bekleme süresine uyulur).
- **Bildirimler:** bir limit uyarı/kritik eşiği geçince Visual Studio bilgi çubuğu (her pencere ve eşik için sıfırlanana kadar bir kez).

Abonelik verisi Claude Code girişinden okunur: `%USERPROFILE%\.claude\.credentials.json`
(veya `%CLAUDE_CONFIG_DIR%\.credentials.json`, ya da `CLAUDE_CODE_OAUTH_TOKEN` ortam değişkeni).
Token **yalnızca okunur, asla yenilenmez** — süresi dolduysa Claude Code'u bir kez kullanmanız yeterli.
`CLAUDE_LIMITS_API_BASE` ortam değişkeni `https://api.anthropic.com` adresini değiştirir (test/kurumsal proxy).

## Ayarlar (Araçlar > Seçenekler > Claude Limits > Genel)

| Ayar | Varsayılan | Açıklama |
|---|---|---|
| Abonelik limitlerini göster | Açık | Claude Code girişinden oturum/haftalık limitler |
| Elle OAuth token | – | Claude Code girişi yerine kullanılacak token (şifreli saklanır) |
| API anahtarı (sk-ant-api…) | – | Dakikalık rate limit ölçümü (her ölçüm 1 token'lık istek; şifreli saklanır) |
| Admin API anahtarı (sk-ant-admin…) | – | Aylık/günlük harcama ve bugünkü tokenlar (şifreli saklanır) |
| Ölçüm modeli | `claude-haiku-5-5` | Rate limitleri ölçülen model |
| Aylık bütçe (USD) | 0 | Harcamanın bütçeye oranı; 0 = bütçe yok |
| Durum çubuğu gösterimi | Both | Session / Weekly / Both / Highest |
| Uyarı eşiği / Kritik eşik (%) | 75 / 90 | Renkler ve bildirimler |
| Bildirimler | Açık | Eşik aşımında bilgi çubuğu |
| Abonelik / Rate limit / Harcama aralığı (sn) | 180 / 900 / 600 | Yenileme aralıkları; alt sınırlar 60 / 120 / 120 sn |

Ayar değişince hemen yenilenir.

## Gizli bilgiler nerede?

API anahtarı, Admin anahtarı ve elle girilen token **kayıt defterine yazılmaz**. Windows DPAPI
(`DataProtectionScope.CurrentUser`) ile şifrelenip `%LOCALAPPDATA%\ClaudeLimits\secrets.dat` dosyasında
saklanır; yalnızca aynı Windows kullanıcısı çözebilir. Diğer ayarlar Visual Studio'nun normal ayar
deposundadır. Paylaşılan önbelleğe anahtarlar değil yalnızca SHA-256 özetinin ilk 12 karakteri yazılır.
Bildirim durumu: `%LOCALAPPDATA%\ClaudeLimits\notify-state.json`.

## Proje yapısı

- `ClaudeLimits.Core` — `netstandard2.0`; ayrıştırıcılar, biçimlendirme, HTTP istemcisi, paylaşılan önbellek, yoklama servisi (TypeScript `core/src` ile birebir).
- `ClaudeLimits.Core.Tests` — xUnit testleri (`net8.0`).
- `ClaudeLimits.VisualStudio` — VSIX (net472): paket, araç penceresi, durum çubuğu, seçenekler, bilgi çubuğu.
