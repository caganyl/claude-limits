# Claude Limits — VS Code

Claude Pro/Max/Team abonelik limitlerinizi ve Anthropic API limitlerinizi durum çubuğunda gösterir.

![icon](media/icon.png)

## Ne gösterir?

| Kaynak | Ne | Gerekli |
|---|---|---|
| **Abonelik** | 5 saatlik oturum, haftalık (tüm modeller), model bazlı haftalık limitler, ek kullanım; her biri için sıfırlanma süresi | Bu bilgisayarda Claude Code girişi (`claude` → `/login`) |
| **API rate limit** | Dakikalık istek / girdi token / çıktı token: kalan ve limit | API anahtarı (`sk-ant-api…`) |
| **API harcaması** | Bu ayki ve bugünkü harcama, bugünkü tokenlar, aylık bütçe yüzdesi | Admin API anahtarı (`sk-ant-admin…`, kuruluş hesabı) |

Abonelik limitleri hesap bazındadır: Claude Code, claude.ai ve Claude masaüstü uygulaması aynı limitleri paylaşır.

## Kullanım

- Durum çubuğundaki `5s 42% · 7g 18%` öğesine tıklayın → ayrıntı paneli.
- Üzerine gelin → tüm limitlerin tablosu.
- Komut paleti (`Ctrl/Cmd+Shift+P`) → **Claude Limits: …**
  - *API anahtarı gir* / *Admin API anahtarı gir* — anahtarlar VS Code’un şifreli SecretStorage’ında tutulur.
  - *Şimdi yenile*, *Ayarları aç*, *Kayıtlı anahtarları sil*.

Turuncu arka plan = uyarı eşiği (varsayılan %75), kırmızı = kritik (varsayılan %90). Eşik aşılınca bildirim gelir.

## Ayarlar

`claudeLimits.displayMode` (both / session / weekly / highest), eşikler, yenileme aralıkları,
`claudeLimits.api.probeModel` (rate limit’i ölçülen model), `claudeLimits.api.monthlyBudgetUsd`.

## Gizlilik

- Claude Code token’ı yalnızca okunur, asla yenilenmez veya başka yere gönderilmez; yalnızca `api.anthropic.com`’a gider.
- Sonuçlar `~/.claude-limits/cache.json` dosyasında paylaşılır (açık VS Code pencereleri, tray uygulaması ve Visual Studio eklentisi aynı veriyi kullanır). Bu dosyada gizli anahtar yoktur.
- API rate limit ölçümü her seferinde 1 token’lık bir istek gönderir (varsayılan 15 dakikada bir; maliyeti ihmal edilebilir).
