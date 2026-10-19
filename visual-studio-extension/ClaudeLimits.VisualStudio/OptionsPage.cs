using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClaudeLimits.Core;
using DisplayMode = ClaudeLimits.Core.DisplayMode;
using Thresholds = ClaudeLimits.Core.Thresholds;
using Microsoft.VisualStudio.Shell;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>
    /// Tools &gt; Options &gt; Claude Limits &gt; Genel. Plain settings use the default DialogPage (registry)
    /// storage; the three secrets are excluded from it and stored DPAPI-encrypted by <see cref="SecretStore"/>.
    /// </summary>
    [Guid(PackageGuids.OptionsPageString)]
    [ComVisible(true)]
    public class OptionsPage : DialogPage
    {
        private const string CatSubscription = "1. Abonelik (Claude Pro/Max)";
        private const string CatApi = "2. Anthropic API";
        private const string CatDisplay = "3. Görünüm ve bildirimler";
        private const string CatIntervals = "4. Yenileme aralıkları";

        private string _apiKey = "";
        private string _adminKey = "";
        private string _manualOAuthToken = "";

        /// <summary>Raised (on the UI thread) after the user applies changes.</summary>
        public event EventHandler Changed;

        // ------------------------------------------------------------------ subscription

        [Category(CatSubscription)]
        [DisplayName("Abonelik limitlerini göster")]
        [Description("Claude Code girişinden (~/.claude/.credentials.json) 5 saatlik oturum ve haftalık limitleri okur. Token yalnızca okunur, asla yenilenmez.")]
        [DefaultValue(true)]
        public bool SubscriptionEnabled { get; set; } = true;

        [Category(CatSubscription)]
        [DisplayName("Elle OAuth token (isteğe bağlı)")]
        [Description("Claude Code girişi yerine kullanılacak OAuth token. Boş bırakılırsa Claude Code girişi (veya CLAUDE_CODE_OAUTH_TOKEN) kullanılır. Kayıt defterine yazılmaz; Windows DPAPI ile şifrelenip %LOCALAPPDATA%\\ClaudeLimits\\secrets.dat dosyasında saklanır.")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ManualOAuthToken
        {
            get => _manualOAuthToken;
            set => _manualOAuthToken = (value ?? "").Trim();
        }

        // ------------------------------------------------------------------ API

        [Category(CatApi)]
        [DisplayName("API anahtarı (sk-ant-api…)")]
        [Description("Dakikalık rate limitleri ölçmek için. Her ölçüm 1 token'lık küçük bir istek gönderir. Kayıt defterine yazılmaz; DPAPI ile şifrelenir.")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ApiKey
        {
            get => _apiKey;
            set => _apiKey = (value ?? "").Trim();
        }

        [Category(CatApi)]
        [DisplayName("Admin API anahtarı (sk-ant-admin…)")]
        [Description("Aylık/günlük harcama ve bugünkü token kullanımı için (Admin API). Kayıt defterine yazılmaz; DPAPI ile şifrelenir.")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string AdminKey
        {
            get => _adminKey;
            set => _adminKey = (value ?? "").Trim();
        }

        [Category(CatApi)]
        [DisplayName("Ölçüm modeli")]
        [Description("Rate limitleri ölçülecek model. Limitler model sınıfına göre değişir.")]
        [DefaultValue(ServiceConfig.DefaultProbeModel)]
        public string ProbeModel { get; set; } = ServiceConfig.DefaultProbeModel;

        [Category(CatApi)]
        [DisplayName("Aylık bütçe (USD)")]
        [Description("Admin API harcamasının yüzde kaçının kullanıldığını göstermek için aylık bütçe. 0 = bütçe yok.")]
        [DefaultValue(0d)]
        public double MonthlyBudgetUsd { get; set; }

        // ------------------------------------------------------------------ display

        [Category(CatDisplay)]
        [DisplayName("Durum çubuğu gösterimi")]
        [Description("Session: yalnızca 5 saatlik oturum. Weekly: haftalık. Both: ikisi birden. Highest: en yüksek olan.")]
        [DefaultValue(DisplayMode.Both)]
        public DisplayMode DisplayMode { get; set; } = DisplayMode.Both;

        [Category(CatDisplay)]
        [DisplayName("Uyarı eşiği (%)")]
        [Description("Bu yüzdenin üstünde turuncu gösterilir ve bildirim verilir.")]
        [DefaultValue(75)]
        public int WarnThreshold { get; set; } = 75;

        [Category(CatDisplay)]
        [DisplayName("Kritik eşik (%)")]
        [Description("Bu yüzdenin üstünde kırmızı gösterilir ve bildirim verilir.")]
        [DefaultValue(90)]
        public int CritThreshold { get; set; } = 90;

        [Category(CatDisplay)]
        [DisplayName("Bildirimler")]
        [Description("Bir limit uyarı veya kritik eşiği geçtiğinde Visual Studio bilgi çubuğu gösterilir (her pencere ve eşik için bir kez).")]
        [DefaultValue(true)]
        public bool Notifications { get; set; } = true;

        // ------------------------------------------------------------------ intervals

        [Category(CatIntervals)]
        [DisplayName("Abonelik aralığı (sn)")]
        [Description("Abonelik limitlerinin yenilenme aralığı. En az 60 sn.")]
        [DefaultValue(180)]
        public int SubscriptionIntervalSec { get; set; } = 180;

        [Category(CatIntervals)]
        [DisplayName("Rate limit aralığı (sn)")]
        [Description("API rate limit ölçüm aralığı. En az 120 sn.")]
        [DefaultValue(900)]
        public int RateLimitIntervalSec { get; set; } = 900;

        [Category(CatIntervals)]
        [DisplayName("Harcama aralığı (sn)")]
        [Description("Admin API harcama yenileme aralığı. En az 120 sn.")]
        [DefaultValue(600)]
        public int CostIntervalSec { get; set; } = 600;

        // ------------------------------------------------------------------ storage

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            var s = SecretStore.Load();
            _apiKey = s.ApiKey ?? "";
            _adminKey = s.AdminKey ?? "";
            _manualOAuthToken = s.ManualOAuthToken ?? "";
        }

        public override void SaveSettingsToStorage()
        {
            // Secrets are [DesignerSerializationVisibility(Hidden)], which DialogPage skips. Blank them anyway
            // while the base class writes the registry, so a future SDK change can never persist them in clear text.
            var api = _apiKey;
            var admin = _adminKey;
            var manual = _manualOAuthToken;
            try
            {
                _apiKey = "";
                _adminKey = "";
                _manualOAuthToken = "";
                base.SaveSettingsToStorage();
            }
            finally
            {
                _apiKey = api;
                _adminKey = admin;
                _manualOAuthToken = manual;
            }

            try
            {
                SecretStore.Save(new SecretStore.Secrets { ApiKey = api, AdminKey = admin, ManualOAuthToken = manual });
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogError("ClaudeLimits", "Gizli anahtarlar kaydedilemedi: " + ex.Message);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Builds the immutable config handed to the background service.</summary>
        public ServiceConfig ToServiceConfig()
        {
            return new ServiceConfig
            {
                SubscriptionEnabled = SubscriptionEnabled,
                ManualOAuthToken = string.IsNullOrWhiteSpace(_manualOAuthToken) ? null : _manualOAuthToken,
                ApiKey = string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey,
                AdminKey = string.IsNullOrWhiteSpace(_adminKey) ? null : _adminKey,
                ProbeModel = string.IsNullOrWhiteSpace(ProbeModel) ? ServiceConfig.DefaultProbeModel : ProbeModel.Trim(),
                MonthlyBudgetUsd = MonthlyBudgetUsd > 0 ? MonthlyBudgetUsd : (double?)null,
                SubscriptionIntervalSec = SubscriptionIntervalSec,
                RateLimitIntervalSec = RateLimitIntervalSec,
                CostIntervalSec = CostIntervalSec,
            };
        }

        public Thresholds GetThresholds()
        {
            var warn = Math.Max(0, Math.Min(100, WarnThreshold));
            var crit = Math.Max(0, Math.Min(100, CritThreshold));
            if (crit < warn) crit = warn;
            return new Thresholds(warn, crit);
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool HasAdminKey => !string.IsNullOrWhiteSpace(_adminKey);
    }
}
