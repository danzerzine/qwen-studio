using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QwenStudio.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Swaps the palette brushes in Application.Resources; everything in the UI refers to them as DynamicResource.</summary>
    public static class Theme
    {
        static readonly Dictionary<string, string> dark = new()
        {
            ["Bg"] = "#0E1014", ["Surface"] = "#161920", ["Surface2"] = "#1D2029", ["Hover"] = "#252935", ["Line"] = "#272B36",
            ["Text"] = "#E7E9EF", ["Muted"] = "#8A92A6", ["Faint"] = "#5C6376",
            ["Accent"] = "#7B8CFF", ["AccentHover"] = "#909EFF", ["AccentSoft"] = "#1F2440", ["OnAccent"] = "#0E1014",
            ["Good"] = "#3DD68C", ["Warn"] = "#F5B94A", ["Bad"] = "#FF6B6B", ["BadSoft"] = "#3A1E24",
            ["Series1"] = "#7B8CFF", ["Series2"] = "#4CC9D9", ["Series3"] = "#C084FC", ["Series4"] = "#F08BB4", ["Series5"] = "#8A92A6",
            ["LogText"] = "#AEB4C2", ["ThumbBrush"] = "#3A3F4D",
        };

        static readonly Dictionary<string, string> light = new()
        {
            ["Bg"] = "#F3F4F7", ["Surface"] = "#FFFFFF", ["Surface2"] = "#EEF0F4", ["Hover"] = "#E3E6ED", ["Line"] = "#DCE0E7",
            ["Text"] = "#1A1D24", ["Muted"] = "#5E6677", ["Faint"] = "#98A0AF",
            ["Accent"] = "#4D5DE3", ["AccentHover"] = "#6270EC", ["AccentSoft"] = "#E7EAFF", ["OnAccent"] = "#FFFFFF",
            ["Good"] = "#17935A", ["Warn"] = "#8F5B00", ["Bad"] = "#DC3F45", ["BadSoft"] = "#FDE8E9",
            ["Series1"] = "#4D5DE3", ["Series2"] = "#0E8FA3", ["Series3"] = "#8B4FD6", ["Series4"] = "#C94F86", ["Series5"] = "#98A0AF",
            ["LogText"] = "#3A4150", ["ThumbBrush"] = "#C3C8D2",
        };

        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        public static bool IsDark { get; private set; } = true;
        public static event Action Changed;

        static bool hooked;

        public static void Apply(ThemeMode mode)
        {
            Mode = mode;
            IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemIsDark());
            var res = Application.Current.Resources;
            foreach (var kv in IsDark ? dark : light)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                b.Freeze();
                res[kv.Key] = b;
            }
            if (!hooked)
            {
                hooked = true;
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (Mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
                        Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
                };
            }
            Changed?.Invoke();
        }

        public static ThemeMode Parse(string s) => Enum.TryParse<ThemeMode>(s, true, out var m) ? m : ThemeMode.System;

        static bool SystemIsDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return true; }
        }

        /// <summary>COLORREF (0x00BBGGRR) of a palette entry, for the DWM title bar.</summary>
        public static int ColorRef(string key)
        {
            var c = (Color)ColorConverter.ConvertFromString((IsDark ? dark : light)[key]);
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }
}
