# Paylaşılan önbellek dosyası

Tüm istemciler (her VS Code penceresi, tray uygulaması, Visual Studio eklentisi) aynı
makinede aynı dosyayı okur/yazar; böylece birden çok editör açıkken Anthropic uç noktaları
gereksiz yere çağrılmaz.

**Konum:** `~/.claude-limits/cache.json` (Windows: `%USERPROFILE%\.claude-limits\cache.json`)

```jsonc
{
  "version": 1,
  "subscription": {                       // abonelik limitleri (Claude Code OAuth)
    "fetchedAt": "2026-10-19T11:20:00.000Z",  // son deneme (başarılı/başarısız)
    "data": {
      "windows": [
        { "id": "session", "label": "Oturum (5 saat)", "shortLabel": "5s",
          "percent": 42, "resetsAt": "2026-10-19T14:00:00+00:00", "active": true },
        { "id": "weekly_all", "label": "Haftalık (tüm modeller)", "shortLabel": "7g", "percent": 18,
          "resetsAt": "2026-10-23T08:00:00+00:00" },
        { "id": "weekly_scoped:Opus", "label": "Haftalık · Opus", "shortLabel": "7g Opus", "percent": 31 }
      ],
      "extra": { "enabled": true, "percent": 5, "monthlyLimitUsd": 50 },
      "plan": "max",
      "source": "claude-code",
      "fetchedAt": "2026-10-19T11:20:00.000Z"
    },
    "error": "…",               // son hata (varsa); data son başarılı değeri korur
    "errorKind": "auth|rate_limited|network|server|bad_request|no_credentials|expired",
    "backoffUntil": "…",        // HTTP 429 sonrası bu zamana kadar kimse çağırmaz
    "keyId": "…"                // elle girilen token'ın sha256 ilk 12 hex'i (yoksa alan yok)
  },
  "rateLimits": {               // API dakikalık limitleri; keyId = sha256(apiKey)[0..12] + "|" + model
    "fetchedAt": "…", "keyId": "ab12cd34ef56|claude-haiku-5-5",
    "data": {
      "model": "claude-haiku-5-5", "limited": false, "fetchedAt": "…",
      "buckets": [
        { "id": "requests", "label": "İstek / dk", "limit": 50, "remaining": 49,
          "percentUsed": 2, "resetsAt": "…" }
      ]
    }
  },
  "cost": {                     // Admin API harcaması; keyId = sha256(adminKey)[0..12]
    "fetchedAt": "…", "keyId": "…",
    "data": { "monthToDateUsd": 12.5, "todayUsd": 0.8,
              "tokensToday": { "uncachedInput": 0, "cacheRead": 0, "cacheWrite": 0, "output": 0 },
              "fetchedAt": "…" }
  }
}
```

Kurallar:

- Bir bölüm, `keyId` eşleşiyor ve `fetchedAt` yenileme aralığından yeniyse ağdan çekilmez.
- `backoffUntil` gelecekteyse (zorla yenilemede bile) çağrı yapılmaz.
- Yazma: geçici dosyaya yaz → `rename`; başarısızsa doğrudan yaz. Bilinmeyen alanlar korunur.
- Gizli anahtarlar bu dosyaya **asla** yazılmaz; yalnızca hash.
- Minimum aralıklar: abonelik 60 sn, rate-limit 120 sn, maliyet 120 sn.
