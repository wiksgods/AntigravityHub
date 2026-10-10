using System;
using System.Windows;
using System.Windows.Media;

namespace ProfileSwitchChrome.Services
{
    public static class ThemeManager
    {
        public static string CurrentTheme { get; private set; } = "dark";
        public static bool IsDark => CurrentTheme == "dark";

        public static event Action? ThemeChanged;

        public static void SetTheme(string theme)
        {
            CurrentTheme = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            ApplyThemeResources(CurrentTheme);
            ThemeChanged?.Invoke();
        }

        public static void ToggleTheme()
        {
            SetTheme(IsDark ? "light" : "dark");
        }

        public static void ApplyThemeResources(string theme)
        {
            var res = Application.Current?.Resources;
            if (res == null) return;

            bool isLight = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

            if (isLight)
            {
                // Modern Clean Light Palette (Predominantly Soft Light Blue)
                res["ThemeWindowBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F6FC"));
                res["ThemeHeaderBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeHeaderBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));
                res["ThemeCardBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeCardBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));
                res["ThemeCardSubBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F8FD"));
                res["ThemeCardSubBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1EBF5"));
                res["ThemeCardHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeCardHoverBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"));

                res["ThemeTextPrimary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
                res["ThemeTextSecondary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
                res["ThemeTextMuted"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));

                res["ThemeInputBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeInputBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));

                res["ThemeToolbarBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeToolbarBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                res["ThemeToolbarBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));

                res["ThemeSecondaryBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeSecondaryBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                res["ThemeSecondaryBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeSecondaryBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));

                res["ThemeBottomBarBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F8FD"));
                res["ThemeBottomBarBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));

                res["ThemeOptionCardBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeOptionCardBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));

                res["ThemeNavBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF3FC"));
                res["ThemeNavBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D8E6F5"));
                res["ThemeNavActiveBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeNavActiveFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeNavActiveBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeNavInactiveFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
                res["ThemeNavInactiveHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2EEFA"));
                res["ThemeNavInactiveHoverFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));

                res["ThemeContextMenuBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeContextMenuBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));
                res["ThemeMenuItemHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeSeparator"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));

                res["ThemeStatusIndicatorBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeStatusIndicatorBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeStatusIndicatorFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                // Background glow & Hero Banners & Cards
                res["ThemeWindowGlowBrush"] = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.0),
                    GradientOrigin = new Point(0.5, 0.0),
                    RadiusX = 0.9,
                    RadiusY = 0.5,
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop((Color)ColorConverter.ConvertFromString("#E0F2FE"), 0),
                        new GradientStop((Color)ColorConverter.ConvertFromString("#F0F6FC"), 1)
                    }
                };

                res["ThemeChromeHeroBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#EBF5FF"),
                    (Color)ColorConverter.ConvertFromString("#F4F8FD"),
                    new Point(0, 0), new Point(1, 1));

                res["ThemeAntigravityHeroBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#EBF5FF"),
                    (Color)ColorConverter.ConvertFromString("#F4F8FD"),
                    new Point(0, 0), new Point(1, 1));

                res["ThemeHeroTitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));

                res["ThemeCardDefaultBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#EBF5FF"),
                    (Color)ColorConverter.ConvertFromString("#FFFFFF"),
                    new Point(0, 0), new Point(1, 0));
                res["ThemeCardDefaultBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                res["ThemeProgressBarTrack"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
                res["ThemeGeminiLabelFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeClaudeLabelFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
                res["ThemeGeminiDisplayFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));

                res["ThemeActiveStatusBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeActiveStatusBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeActiveStatusBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                res["ThemeBottomNoticeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeBottomNoticeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0EBF7"));
                res["ThemeBottomNoticeTitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeBottomNoticeDescFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));

                res["ThemeAccountAvatarBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));
                res["ThemeAccountAvatarBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeAccountAvatarFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeEmailFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeModalBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeModalBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));

                res["ThemeHeroBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeActiveHeroBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));
                res["ThemeActiveHeroBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"));
                res["ThemeActiveHeroBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));

                res["ThemeHeroGeminiQuotaBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeHeroGeminiQuotaBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeHeroGeminiQuotaFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeHeroGeminiQuotaValFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));

                res["ThemeHeroClaudeQuotaBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F3FF"));
                res["ThemeHeroClaudeQuotaBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DDD6FE"));
                res["ThemeHeroClaudeQuotaFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
                res["ThemeHeroClaudeQuotaValFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6D28D9"));

                res["ThemeDangerBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF2F2"));
                res["ThemeDangerBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FECDD3"));
                res["ThemeDangerBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                res["ThemeDangerBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));

                res["ThemeSuccessBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ECFDF5"));
                res["ThemeSuccessBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A7F3D0"));
                res["ThemeSuccessBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#059669"));
                res["ThemeSuccessBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D1FAE5"));

                res["ThemeScrollBarThumb"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                res["ThemeScrollBarThumbHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

                // Option Cards Checked state (Bottom bar checkboxes)
                res["ThemeOptionCardCheckedBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeOptionCardCheckedBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeOptionCardCheckedFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));

                // Running & Default Status Badges
                res["ThemeStatusRunningBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
                res["ThemeStatusRunningBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeStatusRunningFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeStatusRunningDot"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                // Accounts Count Badge (Google Accounts)
                res["ThemeAccountsBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
                res["ThemeAccountsBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFDBFE"));
                res["ThemeAccountsBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));

                // Hero Chrome Subtitle
                res["ThemeChromeHeroSubtitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                // Toast Banner (Light Mode: Clean white card harmonizing with light theme)
                res["ThemeToastBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["ThemeToastBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                res["ThemeToastFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
                res["ThemeToastCloseFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
                res["ThemeToastCloseHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF5FF"));
            }
            else
            {
                // Premium Dark Palette
                res["ThemeWindowBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#080C14"));
                res["ThemeHeaderBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1321"));
                res["ThemeHeaderBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeCardBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#101826"));
                res["ThemeCardBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B2537"));
                res["ThemeCardSubBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111A29"));
                res["ThemeCardSubBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2A3C"));
                res["ThemeCardHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#162235"));
                res["ThemeCardHoverBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D415F"));

                res["ThemeTextPrimary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC"));
                res["ThemeTextSecondary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                res["ThemeTextMuted"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));

                res["ThemeInputBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E1726"));
                res["ThemeInputBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));

                res["ThemeToolbarBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E1726"));
                res["ThemeToolbarBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeToolbarBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#162238"));

                res["ThemeSecondaryBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#162032"));
                res["ThemeSecondaryBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#293548"));
                res["ThemeSecondaryBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                res["ThemeSecondaryBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#223048"));

                res["ThemeBottomBarBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0C1322"));
                res["ThemeBottomBarBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C273D"));

                res["ThemeOptionCardBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111A29"));
                res["ThemeOptionCardBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2A3C"));

                res["ThemeNavBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1220"));
                res["ThemeNavBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeNavActiveBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeNavActiveFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC"));
                res["ThemeNavActiveBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                res["ThemeNavInactiveFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                res["ThemeNavInactiveHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#162032"));
                res["ThemeNavInactiveHoverFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));

                res["ThemeContextMenuBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1321"));
                res["ThemeContextMenuBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeMenuItemHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeSeparator"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));

                res["ThemeStatusIndicatorBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141E2F"));
                res["ThemeStatusIndicatorBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#24334C"));
                res["ThemeStatusIndicatorFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));

                // Background glow & Hero Banners & Cards
                res["ThemeWindowGlowBrush"] = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.0),
                    GradientOrigin = new Point(0.5, 0.0),
                    RadiusX = 0.9,
                    RadiusY = 0.5,
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop((Color)ColorConverter.ConvertFromString("#182338"), 0),
                        new GradientStop((Color)ColorConverter.ConvertFromString("#080C14"), 1)
                    }
                };

                res["ThemeChromeHeroBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0C2340"),
                    (Color)ColorConverter.ConvertFromString("#0C1726"),
                    new Point(0, 0), new Point(1, 1));

                res["ThemeAntigravityHeroBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0C2340"),
                    (Color)ColorConverter.ConvertFromString("#091424"),
                    new Point(0, 0), new Point(1, 1));

                res["ThemeHeroTitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));

                res["ThemeCardDefaultBg"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0C2340"),
                    (Color)ColorConverter.ConvertFromString("#0F1B2A"),
                    new Point(0, 0), new Point(1, 0));
                res["ThemeCardDefaultBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));

                res["ThemeProgressBarTrack"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeGeminiLabelFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                res["ThemeClaudeLabelFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C084FC"));
                res["ThemeGeminiDisplayFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"));

                res["ThemeActiveStatusBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeActiveStatusBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                res["ThemeActiveStatusBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

                res["ThemeBottomNoticeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1321"));
                res["ThemeBottomNoticeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17253D"));
                res["ThemeBottomNoticeTitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                res["ThemeBottomNoticeDescFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));

                res["ThemeAccountAvatarBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                res["ThemeAccountAvatarBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                res["ThemeAccountAvatarFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                res["ThemeEmailFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC"));
                res["ThemeModalBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
                res["ThemeModalBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#24344D"));

                res["ThemeHeroBorderBrush"] = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0284C7"),
                    (Color)ColorConverter.ConvertFromString("#6366F1"),
                    new Point(0, 0), new Point(1, 0));
                res["ThemeActiveHeroBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0C4A6E"));
                res["ThemeActiveHeroBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeActiveHeroBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));

                res["ThemeHeroGeminiQuotaBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0C4A6E"));
                res["ThemeHeroGeminiQuotaBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeHeroGeminiQuotaFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                res["ThemeHeroGeminiQuotaValFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));

                res["ThemeHeroClaudeQuotaBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E1065"));
                res["ThemeHeroClaudeQuotaBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
                res["ThemeHeroClaudeQuotaFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C084FC"));
                res["ThemeHeroClaudeQuotaValFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E9D5FF"));

                res["ThemeDangerBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E1010"));
                res["ThemeDangerBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                res["ThemeDangerBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                res["ThemeDangerBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#450A0A"));

                res["ThemeSuccessBtnBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064E3B"));
                res["ThemeSuccessBtnBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#047857"));
                res["ThemeSuccessBtnFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
                res["ThemeSuccessBtnHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#065F46"));

                res["ThemeScrollBarThumb"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                res["ThemeScrollBarThumbHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));

                // Option Cards Checked state (Bottom bar checkboxes)
                res["ThemeOptionCardCheckedBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#082F49"));
                res["ThemeOptionCardCheckedBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeOptionCardCheckedFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));

                // Running & Default Status Badges
                res["ThemeStatusRunningBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#082F49"));
                res["ThemeStatusRunningBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                res["ThemeStatusRunningFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                res["ThemeStatusRunningDot"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));

                // Accounts Count Badge (Google Accounts)
                res["ThemeAccountsBadgeBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#172554"));
                res["ThemeAccountsBadgeBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
                res["ThemeAccountsBadgeFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60A5FA"));

                // Hero Chrome Subtitle
                res["ThemeChromeHeroSubtitleFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));

                // Toast Banner (Dark Mode: Sleek dark navy card matching dark theme)
                res["ThemeToastBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111A29"));
                res["ThemeToastBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#24334C"));
                res["ThemeToastFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC"));
                res["ThemeToastCloseFg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                res["ThemeToastCloseHoverBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
            }
        }
    }
}
