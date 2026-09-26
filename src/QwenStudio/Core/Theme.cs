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
            ["Bg"] = "#222222", ["Surface"] = "#2B2B2B", ["Surface2"] = "#343434", ["Hover"] = "#3D3D3D", ["Line"] = "#3A3A3A",
            ["Text"] = "#E9E9E9", ["Muted"] = "#A8A8A8", ["Faint"] = "#7D7D7D",
            ["Accent"] = "#4CC2FF", ["AccentHover"] = "#7AD2FF", ["AccentSoft"] = "#22435A", ["OnAccent"] = "#0A1620",
            ["Good"] = "#3DD68C", ["Warn"] = "#F5B94A", ["Bad"] = "#FF6B6B", ["BadSoft"] = "#4F2D2F",
            ["Series1"] = "#4CC2FF", ["Series2"] = "#F89DC9", ["Series3"] = "#52D7C1", ["Series4"] = "#EBB25F", ["Series5"] = "#A8A8A8",
            ["LogText"] = "#C1C1C1", ["ThumbBrush"] = "#525252",
        };

        static readonly Dictionary<string, string> light = new()
        {
            ["Bg"] = "#F3F3F3", ["Surface"] = "#FFFFFF", ["Surface2"] = "#EEEEEE", ["Hover"] = "#E3E3E3", ["Line"] = "#DEDEDE",
            ["Text"] = "#1C1C1C", ["Muted"] = "#5D5D5D", ["Faint"] = "#9E9E9E",
            ["Accent"] = "#005FB8", ["AccentHover"] = "#1A6FC4", ["AccentSoft"] = "#E1EEFA", ["OnAccent"] = "#FFFFFF",
            ["Good"] = "#17935A", ["Warn"] = "#8F5B00", ["Bad"] = "#DC3F45", ["BadSoft"] = "#FDE8E9",
            ["Series1"] = "#005FB8", ["Series2"] = "#A14E78", ["Series3"] = "#008471", ["Series4"] = "#976200", ["Series5"] = "#9E9E9E",
            ["LogText"] = "#3D3D3D", ["ThumbBrush"] = "#C4C4C4",
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
