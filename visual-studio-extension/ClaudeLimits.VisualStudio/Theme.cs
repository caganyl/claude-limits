using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeLimits.Core;
using Level = ClaudeLimits.Core.Level;
using Microsoft.VisualStudio.Shell;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>Level colours (readable on both dark and light VS themes) and themed element helpers.</summary>
    internal static class Theme
    {
        public static readonly Brush Ok = Frozen(0x4A, 0x9A, 0x5C);
        public static readonly Brush Warn = Frozen(0xE0, 0x8A, 0x1E);
        public static readonly Brush Crit = Frozen(0xD9, 0x49, 0x3A);
        public static readonly Brush ErrorText = Frozen(0xD0, 0x60, 0x50);
        public static readonly Brush Track = Frozen(0x80, 0x80, 0x80, 0x40);
        public static readonly Brush White = Frozen(0xFF, 0xFF, 0xFF);

        /// <summary>Light orange that stays legible on the (usually dark or blue) status bar.</summary>
        public static readonly Brush StatusWarnText = Frozen(0xFF, 0xB3, 0x47);

        public static Brush ForLevel(Level level)
        {
            switch (level)
            {
                case Level.Crit: return Crit;
                case Level.Warn: return Warn;
                default: return Ok;
            }
        }

        private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string text, double size = 12, bool bold = false, bool muted = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (muted) tb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.GrayTextKey);
            return tb;
        }

        public static TextBlock Error(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ErrorText,
                Margin = new Thickness(0, 4, 0, 0),
            };
        }

        public static ProgressBar Bar(double percent, Level level)
        {
            var pct = percent;
            if (double.IsNaN(pct)) pct = 0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = pct,
                Height = 6,
                Margin = new Thickness(0, 3, 0, 3),
                Foreground = ForLevel(level),
                Background = Track,
                BorderThickness = new Thickness(0),
                IsTabStop = false,
            };
        }
    }
}
