using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>The "Claude Limits" tool window (View &gt; Other Windows &gt; Claude Limits).</summary>
    [Guid(PackageGuids.ToolWindowString)]
    public class LimitsToolWindow : ToolWindowPane
    {
        public LimitsToolWindow() : base(null)
        {
            Caption = "Claude Limits";
            BitmapImageMoniker = KnownMonikers.StatusInformation;
            Content = new LimitsControl();
        }

        internal LimitsControl Control => (LimitsControl)Content;

        private bool _attached;

        protected override void Initialize()
        {
            base.Initialize();
            AttachToPackage();
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            AttachToPackage();
        }

        private void AttachToPackage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_attached) return;
            if (Package is ClaudeLimitsPackage package)
            {
                _attached = true;
                Control.Attach(package);
                package.RegisterControl(Control);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Package is ClaudeLimitsPackage package)
            {
                try
                {
                    package.UnregisterControl(Control);
                }
                catch (Exception)
                {
                    /* not on the UI thread during shutdown */
                }
            }
            base.Dispose(disposing);
        }
    }
}
