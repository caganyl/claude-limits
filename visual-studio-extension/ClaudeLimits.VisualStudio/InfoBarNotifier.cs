using System;
using System.Collections.Generic;
using System.IO;
using ClaudeLimits.Core;
using Formatting = ClaudeLimits.Core.Formatting;
using Level = ClaudeLimits.Core.Level;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>
    /// Shows a Visual Studio InfoBar when a limit crosses the warn/crit threshold (once per window, level
    /// and reset period, via the shared <see cref="ThresholdNotifier"/>). State survives VS restarts.
    /// All members must be called on the UI thread.
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    public sealed class InfoBarNotifier : IVsInfoBarUIEvents, IDisposable
    {
        private readonly ClaudeLimitsPackage _package;
        private readonly ThresholdNotifier _notifier;
        private readonly Dictionary<IVsInfoBarUIElement, uint> _open = new Dictionary<IVsInfoBarUIElement, uint>();

        internal InfoBarNotifier(ClaudeLimitsPackage package)
        {
            _package = package;
            _notifier = new ThresholdNotifier(LoadState());
        }

        private static string StatePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLimits", "notify-state.json");

        internal void Check(ViewState view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var alerts = _notifier.Check(view.Snapshot, view.Thresholds);
            SaveState();
            foreach (var alert in alerts) Show(alert);
        }

        private void Show(LimitAlert alert)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
                var factory = ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
                if (shell == null || factory == null) return;
                if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))) return;
                if (!(hostObj is IVsInfoBarHost host)) return;

                var model = new InfoBarModel(
                    new IVsInfoBarTextSpan[] { new InfoBarTextSpan(alert.Message) },
                    new IVsInfoBarActionItem[] { new InfoBarHyperlink("Ayrıntılar", "details") },
                    alert.Level == Level.Crit ? KnownMonikers.StatusError : KnownMonikers.StatusWarning,
                    true);
                var element = factory.CreateInfoBar(model);
                if (element == null) return;
                element.Advise(this, out uint cookie);
                _open[element] = cookie;
                host.AddInfoBar(element);
            }
            catch (Exception ex)
            {
                ActivityLog.TryLogWarning("ClaudeLimits", "Bildirim gösterilemedi: " + ex.Message);
            }
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (infoBarUIElement != null && _open.TryGetValue(infoBarUIElement, out uint cookie))
            {
                infoBarUIElement.Unadvise(cookie);
                _open.Remove(infoBarUIElement);
            }
        }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package.ShowToolWindow();
            infoBarUIElement?.Close();
        }

        private static Dictionary<string, Level> LoadState()
        {
            var state = new Dictionary<string, Level>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(StatePath)) return state;
                var json = JObject.Parse(File.ReadAllText(StatePath));
                foreach (var p in json.Properties())
                {
                    switch ((string)p.Value)
                    {
                        case "warn": state[p.Name] = Level.Warn; break;
                        case "crit": state[p.Name] = Level.Crit; break;
                        case "ok": state[p.Name] = Level.Ok; break;
                    }
                }
            }
            catch (Exception)
            {
                /* start fresh */
            }
            return state;
        }

        private void SaveState()
        {
            try
            {
                var json = new JObject();
                foreach (var kv in _notifier.State) json[kv.Key] = Formatting.LevelString(kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, json.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception)
            {
                /* best-effort */
            }
        }

        public void Dispose()
        {
            try
            {
                foreach (var kv in _open) kv.Key.Unadvise(kv.Value);
            }
            catch (Exception)
            {
                /* shutting down */
            }
            _open.Clear();
        }
    }
}
