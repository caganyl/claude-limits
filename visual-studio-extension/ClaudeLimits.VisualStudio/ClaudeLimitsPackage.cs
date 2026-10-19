using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeLimits.Core;
using DisplayMode = ClaudeLimits.Core.DisplayMode;
using Snapshot = ClaudeLimits.Core.Snapshot;
using Thresholds = ClaudeLimits.Core.Thresholds;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeLimits.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude Limits", "Claude abonelik ve API limitleri", "1.0.0")]
    [Guid(PackageGuids.PackageString)]
    [ProvideBindingPath]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(LimitsToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(OptionsPage), "Claude Limits", "Genel", 0, 0, true)]
    public sealed class ClaudeLimitsPackage : AsyncPackage
    {
        private const double TickSec = 30;

        private readonly List<LimitsControl> _controls = new List<LimitsControl>();
        private readonly object _configLock = new object();
        private ServiceConfig _config = new ServiceConfig();
        private LimitsService _service;
        private StatusBarIndicator _statusBar;
        private InfoBarNotifier _infoBars;
        private OptionsPage _options;

        /// <summary>Latest state for the UI. Read and written on the UI thread.</summary>
        internal ViewState CurrentView { get; private set; } = new ViewState();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            AnthropicClient.EnsureModernTls();

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _options = (OptionsPage)GetDialogPage(typeof(OptionsPage));
            _options.Changed += OnOptionsChanged;
            ApplyOptions();

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                commands.AddCommand(new MenuCommand((s, e) => ShowToolWindow(), new CommandID(PackageGuids.CommandSet, PackageIds.ShowToolWindowViewCommand)));
                commands.AddCommand(new MenuCommand((s, e) => ShowToolWindow(), new CommandID(PackageGuids.CommandSet, PackageIds.ShowToolWindowToolsCommand)));
                commands.AddCommand(new MenuCommand((s, e) => RequestRefresh(force: true), new CommandID(PackageGuids.CommandSet, PackageIds.RefreshCommand)));
            }

            _statusBar = new StatusBarIndicator(this);
            _statusBar.Start();
            _infoBars = new InfoBarNotifier(this);

            _service = new LimitsService(GetConfig);
            _service.Updated += OnServiceUpdated;
            _service.Start(TickSec); // runs on the thread pool; never blocks the UI thread
        }

        // ------------------------------------------------------------------ configuration

        private ServiceConfig GetConfig()
        {
            lock (_configLock) return _config;
        }

        private void ApplyOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cfg = _options.ToServiceConfig();
            lock (_configLock) _config = cfg;
            CurrentView = BuildView(CurrentView.Snapshot);
        }

        private ViewState BuildView(Snapshot snapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new ViewState
            {
                Snapshot = snapshot ?? new Snapshot(),
                Thresholds = _options?.GetThresholds() ?? Thresholds.Default,
                Mode = _options?.DisplayMode ?? DisplayMode.Both,
                SubscriptionEnabled = _options?.SubscriptionEnabled ?? true,
                HasApiKey = _options?.HasApiKey ?? false,
                HasAdminKey = _options?.HasAdminKey ?? false,
            };
        }

        private void OnOptionsChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ApplyOptions();
            Render(notify: false);
            // Settings changed → refresh immediately (still subject to the 15 s floor and any 429 back-off).
            RequestRefresh(force: true);
        }

        // ------------------------------------------------------------------ refresh + UI

        /// <summary>Starts a refresh on the thread pool. Safe to call from the UI thread.</summary>
        internal void RequestRefresh(bool force)
        {
            var service = _service;
            if (service == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await service.RefreshAsync(force, DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ActivityLog.TryLogError("ClaudeLimits", "Yenileme başarısız: " + ex.Message);
                }
            });
        }

        private void OnServiceUpdated(object sender, Snapshot snapshot)
        {
            // Called on a thread-pool thread.
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    CurrentView = BuildView(snapshot);
                    Render(notify: true);
                }
                catch (OperationCanceledException)
                {
                    /* VS is shutting down */
                }
                catch (Exception ex)
                {
                    ActivityLog.TryLogError("ClaudeLimits", "Arayüz güncellenemedi: " + ex.Message);
                }
            });
        }

        private void Render(bool notify)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = CurrentView;
            try { _statusBar?.Update(view); } catch (Exception ex) { ActivityLog.TryLogWarning("ClaudeLimits", ex.Message); }
            foreach (var control in _controls.ToArray())
            {
                try { control.Render(view); } catch (Exception ex) { ActivityLog.TryLogWarning("ClaudeLimits", ex.Message); }
            }
            if (notify && _options != null && _options.Notifications)
            {
                try { _infoBars?.Check(view); } catch (Exception ex) { ActivityLog.TryLogWarning("ClaudeLimits", ex.Message); }
            }
        }

        internal void RegisterControl(LimitsControl control)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_controls.Contains(control)) _controls.Add(control);
        }

        internal void UnregisterControl(LimitsControl control)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _controls.Remove(control);
        }

        // ------------------------------------------------------------------ commands

        internal void ShowToolWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var window = await ShowToolWindowAsync(typeof(LimitsToolWindow), 0, true, DisposalToken);
                    if ((window as ToolWindowPane)?.Frame == null) throw new NotSupportedException("Claude Limits penceresi oluşturulamadı.");
                }
                catch (OperationCanceledException)
                {
                    /* shutting down */
                }
                catch (Exception ex)
                {
                    ActivityLog.TryLogError("ClaudeLimits", "Araç penceresi açılamadı: " + ex.Message);
                }
            });
        }

        internal void OpenOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowOptionPage(typeof(OptionsPage));
        }

        // ------------------------------------------------------------------ shutdown

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_service != null)
                    {
                        _service.Updated -= OnServiceUpdated;
                        _service.Dispose();
                        _service = null;
                    }
                    if (ThreadHelper.CheckAccess())
                    {
                        if (_options != null) _options.Changed -= OnOptionsChanged;
                        _statusBar?.Dispose();
                        _infoBars?.Dispose();
                        _controls.Clear();
                    }
                }
                catch (Exception)
                {
                    /* never throw during VS shutdown */
                }
            }
            base.Dispose(disposing);
        }
    }
}
