using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace QwenStudio.Core
{
    /// <summary>
    /// Interface language. Russian is the source: every UI string is written in Russian and goes through
    /// <see cref="T"/> (or <see cref="F"/> for a composite format); in English it is looked up in <see cref="Strings.En"/>.
    /// A string missing from the table stays Russian. XAML texts are translated by <see cref="Apply"/>.
    /// </summary>
    public static class L
    {
        /// <summary>"ru" or "en"; ui.env LANG.</summary>
        public static string Current { get; private set; } = "ru";
        public static bool En => Current == "en";

        public static void Set(string lang)
        {
            Current = lang == "en" ? "en" : "ru";
            // numbers and dates follow the interface language (decimal point in English); Russian keeps the Windows settings
            var c = En ? CultureInfo.GetCultureInfo("en-US") : system;
            var ui = En ? c : systemUi;
            CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = ui;
        }

        static readonly CultureInfo system = CultureInfo.CurrentCulture, systemUi = CultureInfo.CurrentUICulture;

        public static string T(string ru) => En && ru != null && Strings.En.TryGetValue(ru, out var en) ? en : ru;

        public static string F(string ruFormat, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(ruFormat), args);

        /// <summary>Russian texts as XAML set them, per element and property: switching back to Russian needs them.</summary>
        static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> source = new();

        /// <summary>
        /// Translates the texts, captions and tooltips XAML put into the tree. A value code has changed since
        /// (neither the XAML text nor its translation) is left alone: code sets its own texts through T/F.
        /// </summary>
        public static void Apply(DependencyObject root)
        {
            if (root == null) return;
            switch (root)
            {
                case TextBlock tb: Put(tb, TextBlock.TextProperty); break;
                case ContentControl cc when cc.Content is string: Put(cc, ContentControl.ContentProperty); break;
            }
            if (root is FrameworkElement fe && fe.ToolTip is string) Put(fe, FrameworkElement.ToolTipProperty);
            foreach (var child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject d) Apply(d);
        }

        static void Put(DependencyObject o, DependencyProperty p)
        {
            var now = o.GetValue(p) as string;
            if (string.IsNullOrEmpty(now)) return;
            var map = source.GetOrCreateValue(o);
            if (!map.TryGetValue(p, out var ru))
            {
                map[p] = ru = now;
            }
            else if (now != ru && !(Strings.En.TryGetValue(ru, out var was) && now == was))
            {
                map[p] = ru = now;     // code replaced it: that is the new source text
            }
            var want = T(ru);
            if (now != want) o.SetValue(p, want);
        }
    }
}
