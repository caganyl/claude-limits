using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeLimits.Core;
using Level = ClaudeLimits.Core.Level;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>
    /// Shows "Claude 5s 42% · 7g 18%" in the Visual Studio status bar by injecting a small WPF element
    /// into the status bar's DockPanel. The status bar layout is not a public API: recent VS versions may
    /// host it on a separate UI thread, so every PresentationSource is searched and the element is created
    /// on (and only touched through) the dispatcher that owns the status bar. When injection keeps failing,
    /// falls back to IVsStatusbar.SetText. Public members must be called on the VS main thread.
    /// </summary>
    internal sealed class StatusBarIndicator : IDisposable
    {
        private const int MaxAttempts = 20;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan CrossThreadTimeout = TimeSpan.FromMilliseconds(750);

        private readonly ClaudeLimitsPackage _package;
        private DispatcherTimer _retryTimer;
        private int _attempts;
        private bool _fallback;
        private bool _disposed;
        private ViewState _lastView;
        private string _lastFallbackText;

        // Owned by _host (the dispatcher of the status bar's visual tree).
        private Dispatcher _host;
        private Border _border;
        private TextBlock _text;
        private volatile bool _lost;
        private int _misses;

        public StatusBarIndicator(ClaudeLimitsPackage package)
        {
            _package = package;
        }

        private bool Injected => _host != null && !_lost;

        public void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (TryInject()) return;
            _retryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RetryInterval };
            _retryTimer.Tick += OnRetryTick;
            _retryTimer.Start();
        }

        public void Update(ViewState view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed || view == null) return;
            _lastView = view;

            // VS can rebuild its status bar (layout reset, theme change); re-inject when our element was dropped.
            if (_host != null && _lost)
            {
                _host = null;
                _lost = false;
                TryInject();
            }

            if (Injected) Apply(view);
            else if (_fallback) WriteFallback(SnapshotText.StatusText(view));
        }

        // ------------------------------------------------------------------ rendering

        private void Apply(ViewState view)
        {
            var host = _host;
            if (host == null) return;
            var text = SnapshotText.StatusText(view);
            var tooltip = SnapshotText.Tooltip(view);
            var level = SnapshotText.StatusLevel(view);
            Action apply = () =>
            {
                try
                {
                    if (_border == null || _text == null) return;
                    // Items added to an ItemsControl are realized at layout time, so a single miss is not conclusive.
                    if (PresentationSource.FromVisual(_border) == null)
                    {
                        if (++_misses >= 2) _lost = true;
                    }
                    else
                    {
                        _misses = 0;
                    }
                    _text.Text = text;
                    _border.ToolTip = tooltip;
                    switch (level)
                    {
                        case Level.Crit:
                            _border.Background = Theme.Crit;
                            _text.Foreground = Theme.White;
                            break;
                        case Level.Warn:
                            _border.Background = Brushes.Transparent;
                            _text.Foreground = Theme.StatusWarnText;
                            break;
                        default:
                            _border.Background = Brushes.Transparent;
                            ApplyDefaultForeground();
                            break;
                    }
                }
                catch (Exception)
                {
                    _lost = true;
                }
            };
            if (host.CheckAccess()) apply();
            else host.BeginInvoke(apply, DispatcherPriority.Background);
        }

        private void WriteFallback(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var statusBar = ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                if (statusBar == null) return;
                statusBar.IsFrozen(out int frozen);
                if (frozen != 0) return;
                // Only write when our text changed, so VS's own messages are not overwritten all the time.
                if (text == _lastFallbackText) return;
                statusBar.SetText(text);
                _lastFallbackText = text;
            }
            catch (Exception)
            {
                /* status bar unavailable */
            }
        }

        // ------------------------------------------------------------------ injection

        private void OnRetryTick(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed || TryInject())
            {
                StopTimer();
                return;
            }
            if (++_attempts >= MaxAttempts)
            {
                StopTimer();
                _fallback = true;
                ActivityLog.TryLogWarning("ClaudeLimits", "Durum çubuğuna öğe eklenemedi; IVsStatusbar metnine geçiliyor.");
                if (_lastView != null) WriteFallback(SnapshotText.StatusText(_lastView));
            }
        }

        private void StopTimer()
        {
            if (_retryTimer == null) return;
            _retryTimer.Stop();
            _retryTimer.Tick -= OnRetryTick;
            _retryTimer = null;
        }

        private enum Strategy
        {
            NamedPanel,
            StatusBarControl,
            FuzzyName,
        }

        private bool TryInject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var roots = CollectRoots();
                foreach (Strategy strategy in Enum.GetValues(typeof(Strategy)))
                {
                    foreach (var root in roots)
                    {
                        if (InjectInto(root, strategy))
                        {
                            _host = root.Dispatcher;
                            _lost = false;
                            _misses = 0;
                            _fallback = false;
                            if (_lastView != null) Apply(_lastView);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("ClaudeLimits", "Durum çubuğu öğesi eklenemedi: " + ex.Message);
            }
            return false;
        }

        /// <summary>Main window first, then every other PresentationSource (possibly on other UI threads).</summary>
        private static List<DispatcherObject> CollectRoots()
        {
            var roots = new List<DispatcherObject>();
            var main = Application.Current?.MainWindow;
            if (main != null) roots.Add(main);
            try
            {
                foreach (var item in PresentationSource.CurrentSources)
                {
                    if (item is PresentationSource source) roots.Add(source);
                }
            }
            catch (Exception)
            {
                /* enumeration not permitted or collection changed: use what we have */
            }
            return roots;
        }

        private bool InjectInto(DispatcherObject root, Strategy strategy)
        {
            Func<bool> work = () =>
            {
                Visual visual = root as Visual;
                if (root is PresentationSource source)
                {
                    if (source.IsDisposed) return false;
                    visual = source.RootVisual;
                }
                if (visual == null) return false;
                return InjectOnOwnerThread(visual, strategy);
            };

            var dispatcher = root.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return false;
            if (dispatcher.CheckAccess()) return work();
            try
            {
                // Short timeout: never let the VS main thread wait long on another UI thread.
                return dispatcher.Invoke(work, DispatcherPriority.Send, System.Threading.CancellationToken.None, CrossThreadTimeout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Runs on the thread that owns <paramref name="root"/>.</summary>
        private bool InjectOnOwnerThread(Visual root, Strategy strategy)
        {
            switch (strategy)
            {
                case Strategy.NamedPanel:
                    {
                        // The DockPanel VS uses for status bar items is named "StatusBarPanel".
                        if (FindFirst(root, d => d is Panel p && p.Name == "StatusBarPanel") is Panel panel)
                            return InsertIntoPanel(panel);
                        return false;
                    }
                case Strategy.StatusBarControl:
                    {
                        if (FindFirst(root, d => d is System.Windows.Controls.Primitives.StatusBar) is System.Windows.Controls.Primitives.StatusBar statusBar && statusBar.ItemsSource == null)
                        {
                            EnsureElement();
                            Detach();
                            statusBar.Items.Insert(0, _border);
                            return true;
                        }
                        return false;
                    }
                default:
                    {
                        var fuzzy = FindFirst(root, d => d is DockPanel p && !p.IsItemsHost
                                                         && (p.Name ?? "").IndexOf("StatusBar", StringComparison.OrdinalIgnoreCase) >= 0) as DockPanel;
                        return fuzzy != null && InsertIntoPanel(fuzzy);
                    }
            }
        }

        private bool InsertIntoPanel(Panel panel)
        {
            if (panel.IsItemsHost) return false; // children are owned by an ItemsControl
            if (!(panel is DockPanel) && !(panel is StackPanel sp && sp.Orientation == Orientation.Horizontal)) return false;
            EnsureElement();
            Detach();
            // In a DockPanel the first Dock=Right child sits at the far right edge.
            panel.Children.Insert(0, _border);
            ApplyDefaultForeground();
            return true;
        }

        /// <summary>
        /// Picks a readable text colour for the label. The status bar panel carries no text colour of its own and
        /// neighbouring items don't reliably expose the colour they render with, so we look at what is actually
        /// behind the label: the first ancestor with an opaque solid background decides light vs dark text.
        /// Re-run on every update because the status bar changes colour (debugging, solution loaded…).
        /// Runs on the element's owner thread.
        /// </summary>
        private void ApplyDefaultForeground()
        {
            if (_text == null) return;
            var bg = EffectiveBackground(_border) ?? ThemeStatusBarBackground();
            _text.Foreground = bg.HasValue && Luminance(bg.Value) > 0.55 ? DarkText : LightText;
        }

        private static readonly Brush LightText = FrozenBrush(Color.FromRgb(0xF1, 0xF1, 0xF1));
        private static readonly Brush DarkText = FrozenBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        private static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>Relative luminance (0 = black, 1 = white), sRGB.</summary>
        private static double Luminance(Color c)
        {
            double Lin(byte v)
            {
                var x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>First opaque solid background found walking up from the element.</summary>
        private static Color? EffectiveBackground(DependencyObject? start)
        {
            var cur = start == null ? null : VisualTreeHelper.GetParent(start);
            for (var depth = 0; cur != null && depth < 40; depth++)
            {
                Brush? brush = null;
                if (cur is Panel p) brush = p.Background;
                else if (cur is Border b) brush = b.Background;
                else if (cur is Control c) brush = c.Background;
                if (brush is SolidColorBrush scb && scb.Color.A >= 200 && scb.Opacity > 0.8) return scb.Color;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        /// <summary>Fallback: the current theme's status bar background, when it can be resolved here.</summary>
        private Color? ThemeStatusBarBackground()
        {
            try
            {
                if (_border?.TryFindResource(EnvironmentColors.StatusBarDefaultBrushKey) is SolidColorBrush b) return b.Color;
            }
            catch (Exception)
            {
                /* resource lookup across threads may fail */
            }
            return null;
        }

        /// <summary>Creates the element on the current (owner) thread, recreating it when it belongs to another thread.</summary>
        private void EnsureElement()
        {
            if (_border != null && _border.CheckAccess()) return;
            if (_border != null)
            {
                var old = _border;
                try
                {
                    old.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (old.Parent is Panel p) p.Children.Remove(old);
                        else if (old.Parent is ItemsControl ic) ic.Items.Remove(old);
                    }));
                }
                catch (Exception)
                {
                    /* old thread gone */
                }
            }

            _text = new TextBlock
            {
                Text = _lastView != null ? SnapshotText.StatusText(_lastView) : "Claude …",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            _border = new Border
            {
                Child = _text,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "Claude Limits",
                Name = "ClaudeLimitsStatusBarItem",
            };
            DockPanel.SetDock(_border, Dock.Right);
            ToolTipService.SetShowDuration(_border, 60000);
            _border.MouseLeftButtonUp += OnClick;
        }

        /// <summary>Runs on the element's owner thread.</summary>
        private void Detach()
        {
            if (_border == null) return;
            if (_border.Parent is Panel p) p.Children.Remove(_border);
            else if (_border.Parent is ItemsControl ic) ic.Items.Remove(_border);
        }

        /// <summary>Runs on the element's owner thread (possibly not the VS main thread).</summary>
        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var package = _package;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                package.ShowToolWindow();
            });
        }

        /// <summary>Breadth-first search of the visual tree; runs on the tree's owner thread.</summary>
        private static DependencyObject FindFirst(DependencyObject root, Func<DependencyObject, bool> match)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            var visited = 0;
            while (queue.Count > 0 && visited < 50000)
            {
                var current = queue.Dequeue();
                visited++;
                if (match(current)) return current;
                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D)) continue;
                int count;
                try
                {
                    count = VisualTreeHelper.GetChildrenCount(current);
                }
                catch (Exception)
                {
                    continue;
                }
                for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                StopTimer();
                var border = _border;
                if (border != null)
                {
                    Action detach = () =>
                    {
                        border.MouseLeftButtonUp -= OnClick;
                        if (border.Parent is Panel p) p.Children.Remove(border);
                        else if (border.Parent is ItemsControl ic) ic.Items.Remove(border);
                    };
                    if (border.CheckAccess()) detach();
                    else if (!border.Dispatcher.HasShutdownStarted) border.Dispatcher.BeginInvoke(detach);
                }
            }
            catch (Exception)
            {
                /* shutting down */
            }
        }
    }
}
