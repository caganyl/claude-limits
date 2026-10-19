using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeLimits.Core;
using Formatting = ClaudeLimits.Core.Formatting;
using Level = ClaudeLimits.Core.Level;
using Snapshot = ClaudeLimits.Core.Snapshot;
using Thresholds = ClaudeLimits.Core.Thresholds;
using Microsoft.VisualStudio.Shell;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>Content of the "Claude Limits" tool window. All members run on the UI thread.</summary>
    public partial class LimitsControl : UserControl
    {
        private ClaudeLimitsPackage _package;

        public LimitsControl()
        {
            InitializeComponent();
        }

        internal void Attach(ClaudeLimitsPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            Render(package.CurrentView);
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _package?.RequestRefresh(force: true);
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            _package?.OpenOptions();
        }

        internal void Render(ViewState view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view == null) return;
            var s = view.Snapshot ?? new Snapshot();
            var t = view.Thresholds ?? Thresholds.Default;
            var now = DateTimeOffset.UtcNow;

            RenderSubscription(view, s, t, now);
            RenderApi(view, s, t, now);
            UpdatedText.Text = "Son güncelleme: " + SnapshotText.UpdatedAt(s);
        }

        // ------------------------------------------------------------------ subscription

        private void RenderSubscription(ViewState view, Snapshot s, Thresholds t, DateTimeOffset now)
        {
            SubscriptionPanel.Children.Clear();
            var sub = s.Subscription;

            if (!view.SubscriptionEnabled)
            {
                SubscriptionChip.Text = "";
                SubscriptionPanel.Children.Add(Theme.Text("Abonelik limitleri ayarlarda kapalı.", muted: true));
                return;
            }

            if (sub != null)
            {
                var chip = new List<string>();
                var plan = Formatting.PlanName(sub.Plan);
                if (plan.Length > 0) chip.Add(plan);
                chip.Add(sub.Source == "claude-web" ? "claude.ai" : "Claude Code");
                SubscriptionChip.Text = string.Join(" · ", chip);
            }
            else
            {
                SubscriptionChip.Text = "";
            }

            if (sub != null && sub.Windows.Count > 0)
            {
                foreach (var w in sub.Windows)
                {
                    var level = Formatting.LevelOf(w.Percent, t);
                    var reset = SnapshotText.ResetText(w.ResetsAt ?? "", now);
                    SubscriptionPanel.Children.Add(Row(w.Label, Formatting.FormatPercent(w.Percent), level, w.Percent, reset, w.IsActive));
                }

                var extra = sub.Extra;
                if (extra != null && extra.Enabled)
                {
                    var pct = extra.Percent;
                    var lim = extra.MonthlyLimitUsd;
                    double? used = pct.HasValue && lim.HasValue ? pct.Value / 100 * lim.Value : (double?)null;
                    string sub2 = "";
                    if (lim.HasValue) sub2 = used.HasValue ? Formatting.FormatUsd(used) + " / " + Formatting.FormatUsd(lim) : "Limit " + Formatting.FormatUsd(lim);
                    var level = Formatting.LevelOf(pct, t);
                    SubscriptionPanel.Children.Add(Row(
                        "Ek kullanım (aylık)",
                        pct.HasValue ? Formatting.FormatPercent(pct) : "açık",
                        level,
                        pct,
                        sub2,
                        false));
                }
            }
            else if (s.SubscriptionError == null)
            {
                SubscriptionPanel.Children.Add(Theme.Text("Veri bekleniyor…", muted: true));
            }

            if (s.SubscriptionError != null) SubscriptionPanel.Children.Add(Theme.Error(s.SubscriptionError));
        }

        // ------------------------------------------------------------------ API

        private void RenderApi(ViewState view, Snapshot s, Thresholds t, DateTimeOffset now)
        {
            ApiPanel.Children.Clear();
            ApiChip.Text = s.RateLimits?.Model ?? "";

            if (!view.HasApiKey && !view.HasAdminKey && s.RateLimits == null && s.Cost == null)
            {
                ApiPanel.Children.Add(Theme.Text("API limitleri için Araçlar > Seçenekler > Claude Limits'ten API anahtarı girin.", muted: true));
                ApiPanel.Children.Add(Theme.Text(
                    "API anahtarı (sk-ant-api…) dakikalık rate limitleri, Admin API anahtarı (sk-ant-admin…) harcama ve token kullanımını gösterir.",
                    11, muted: true));
                return;
            }

            // Rate limits.
            if (s.RateLimits != null)
            {
                ApiPanel.Children.Add(SubHeader("Dakikalık rate limit"));
                if (s.RateLimits.Limited) ApiPanel.Children.Add(Theme.Error("429 – limit dolu (dakikalık limit şu an dolu)."));
                var since = Formatting.FormatSince(s.RateLimits.FetchedAt, now);
                foreach (var b in s.RateLimits.Buckets)
                {
                    var level = Formatting.LevelOf(b.PercentUsed, t);
                    // Per-minute reset times are meaningless after a multi-minute probe interval: show the measurement age instead.
                    var line = "%" + Formatting.RoundedNumber(b.PercentUsed) + " kullanıldı · ölçüm " + since;
                    ApiPanel.Children.Add(Row(
                        b.Label,
                        Formatting.FormatCount(b.Remaining) + " / " + Formatting.FormatCount(b.Limit) + " kaldı",
                        level,
                        b.PercentUsed,
                        line,
                        false));
                }
            }
            else if (s.RateLimitsError == null)
            {
                ApiPanel.Children.Add(Theme.Text(
                    view.HasApiKey ? "Ölçülüyor…" : "Dakikalık rate limit için bir API anahtarı (sk-ant-api…) girin.",
                    muted: true));
            }
            if (s.RateLimitsError != null) ApiPanel.Children.Add(Theme.Error(s.RateLimitsError));

            // Cost.
            if (s.Cost != null)
            {
                var c = s.Cost;
                var header = SubHeader("Harcama (Admin API)");
                header.Margin = new Thickness(0, 10, 0, 4);
                ApiPanel.Children.Add(header);

                var grid = new UniformGridLite();
                grid.Add("Bu ay", Formatting.FormatUsd(c.MonthToDateUsd), null);
                grid.Add("Bugün (UTC)", Formatting.FormatUsd(c.TodayUsd), null);
                if (c.TokensToday != null)
                {
                    var k = c.TokensToday;
                    grid.Add("Bugünkü tokenlar",
                        Formatting.FormatCount(k.UncachedInput + k.CacheRead + k.CacheWrite),
                        "girdi · " + Formatting.FormatCount(k.Output) + " çıktı");
                }
                ApiPanel.Children.Add(grid.Build());

                if (c.BudgetUsd.HasValue && c.PercentOfBudget.HasValue)
                {
                    var level = Formatting.LevelOf(c.PercentOfBudget, t);
                    ApiPanel.Children.Add(Row(
                        "Aylık bütçe",
                        Formatting.FormatPercent(c.PercentOfBudget),
                        level,
                        c.PercentOfBudget,
                        Formatting.FormatUsd(c.MonthToDateUsd) + " / " + Formatting.FormatUsd(c.BudgetUsd),
                        false));
                }
            }
            else if (s.CostError == null)
            {
                ApiPanel.Children.Add(Theme.Text(
                    view.HasAdminKey ? "Harcama yükleniyor…" : "Harcama ve token kullanımı için Admin API anahtarı (sk-ant-admin…) girin.",
                    muted: true));
            }
            if (s.CostError != null) ApiPanel.Children.Add(Theme.Error(s.CostError));
        }

        // ------------------------------------------------------------------ building blocks

        private static TextBlock SubHeader(string text)
        {
            var tb = Theme.Text(text, 11, bold: true, muted: true);
            tb.Margin = new Thickness(0, 2, 0, 4);
            return tb;
        }

        private static FrameworkElement Row(string label, string value, Level level, double? percent, string subText, bool active)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var top = new DockPanel { LastChildFill = true };
            var valueText = new TextBlock
            {
                Text = value,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            if (level == Level.Warn || level == Level.Crit) valueText.Foreground = Theme.ForLevel(level);
            DockPanel.SetDock(valueText, Dock.Right);
            top.Children.Add(valueText);

            var labelPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            labelPanel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
            if (active)
            {
                var tag = new TextBlock
                {
                    Text = "BAĞLAYICI",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Şu anda hesabı sınırlayan limit",
                };
                tag.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ControlLinkTextKey);
                labelPanel.Children.Add(tag);
            }
            top.Children.Add(labelPanel);
            panel.Children.Add(top);

            if (percent.HasValue)
            {
                var bar = Theme.Bar(percent.Value, level);
                if (active)
                {
                    var outline = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(1), CornerRadius = new CornerRadius(2), Child = bar };
                    outline.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ControlLinkTextKey);
                    bar.Margin = new Thickness(0);
                    outline.Margin = new Thickness(0, 3, 0, 3);
                    panel.Children.Add(outline);
                }
                else
                {
                    panel.Children.Add(bar);
                }
            }

            if (!string.IsNullOrEmpty(subText)) panel.Children.Add(Theme.Text(subText, 11, muted: true));
            return panel;
        }

        /// <summary>Small helper that lays out stat tiles in equal columns.</summary>
        private sealed class UniformGridLite
        {
            private readonly List<Tuple<string, string, string>> _tiles = new List<Tuple<string, string, string>>();

            public void Add(string caption, string value, string note) => _tiles.Add(Tuple.Create(caption, value, note));

            public FrameworkElement Build()
            {
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = Math.Max(1, _tiles.Count), Margin = new Thickness(0, 0, 0, 8) };
                foreach (var tile in _tiles)
                {
                    var border = new Border
                    {
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(0, 0, 6, 0),
                    };
                    border.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
                    var sp = new StackPanel();
                    sp.Children.Add(Theme.Text(tile.Item1, 11, muted: true));
                    sp.Children.Add(new TextBlock { Text = tile.Item2, FontSize = 15, FontWeight = FontWeights.SemiBold });
                    if (!string.IsNullOrEmpty(tile.Item3)) sp.Children.Add(Theme.Text(tile.Item3, 10.5, muted: true));
                    border.Child = sp;
                    grid.Children.Add(border);
                }
                return grid;
            }
        }
    }
}
