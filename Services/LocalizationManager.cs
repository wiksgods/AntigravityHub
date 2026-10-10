using System;
using System.Collections.Generic;

namespace ProfileSwitchChrome.Services
{
    public static class LocalizationManager
    {
        public static string CurrentLanguage { get; private set; } = "vi";
        public static bool IsEnglish => string.Equals(CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

        public static event Action? LanguageChanged;

        public static void SetLanguage(string lang)
        {
            CurrentLanguage = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "vi";
            LanguageChanged?.Invoke();
        }

        public static void ToggleLanguage()
        {
            SetLanguage(IsEnglish ? "vi" : "en");
        }

        private static readonly Dictionary<string, (string Vi, string En)> Strings = new(StringComparer.OrdinalIgnoreCase)
        {
            // Window Titles
            ["AppTitle"] = ("Antigravity Hub - Quản Trị Quota, Tài Khoản & Chrome Workspaces", "Antigravity Hub - Quota, Account & Chrome Workspace Manager"),
            ["ModelQuotaTitle"] = ("Bảng Hạn Mức Model Quota - Antigravity IDE", "Model Quota Dashboard - Antigravity IDE"),

            // Header & Tabs
            ["HeaderSubtitle"] = ("Chrome & Antigravity", "Chrome & Antigravity"),
            ["TabChrome"] = ("🌐 Chrome", "🌐 Chrome"),
            ["TabAntigravity"] = ("🚀 Antigravity", "🚀 Antigravity"),
            ["ChromeRunning"] = ("Chrome mở", "Chrome Running"),
            ["ChromeClosed"] = ("Chrome đã đóng", "Chrome Closed"),
            ["CloseChrome"] = ("🛑 Tắt", "🛑 Close"),
            ["CloseChromeTip"] = ("Đóng tất cả tiến trình Chrome để cập nhật triệt để", "Close all Chrome processes to apply changes thoroughly"),
            ["Refresh"] = ("🔄 Làm mới", "🔄 Refresh"),
            ["RefreshTip"] = ("Tải lại danh sách profiles và trạng thái Chrome (F5)", "Reload profile list and Chrome status (F5)"),
            ["IdeRunning"] = ("IDE mở", "IDE Running"),
            ["IdeClosed"] = ("IDE đã đóng", "IDE Closed"),
            ["CloseIde"] = ("🛑 Tắt", "🛑 Close"),
            ["CloseIdeTip"] = ("Đóng tất cả tiến trình Antigravity IDE & Language Server để giải phóng RAM", "Close all Antigravity IDE & Language Server processes to free RAM"),
            ["OAuthLogin"] = ("🔑 Đăng nhập", "🔑 Login"),
            ["OAuthLoginTip"] = ("Đăng nhập tài khoản Antigravity qua trình duyệt (OAuth)", "Sign in to Antigravity account via browser (OAuth)"),
            ["OAuthCancel"] = ("❌ Hủy", "❌ Cancel"),
            ["OAuthCancelTip"] = ("Hủy phiên đăng nhập đang chờ", "Cancel pending login process"),
            ["OAuthWaiting"] = ("⏳ Chờ...", "⏳ Waiting..."),
            ["OAuthCancelled"] = ("Đã hủy đăng nhập.", "Login cancelled."),
            ["OAuthPortConflict"] = ("Xung đột cổng đăng nhập (8888)", "Port Conflict (8888)"),
            ["OAuthPortConflictDesc"] = ("Ứng dụng 'antigravity-manager' đang chạy và chiếm cổng 8888 của Google OAuth.\n\nBạn có muốn đóng 'antigravity-manager' để tiếp tục đăng nhập không?", "The app 'antigravity-manager' is currently running and occupying Google OAuth port 8888.\n\nDo you want to close 'antigravity-manager' to proceed with login?"),
            ["OAuthCloseAndLogin"] = ("Đóng & Tiếp tục", "Close & Login"),
            ["OAuthPortBusy"] = ("Cổng 8888 đang bận", "Port 8888 Busy"),
            ["OAuthPortBusyDesc"] = ("Cổng 8888 đang bị chiếm bởi ứng dụng '{0}' (PID {1}). Vui lòng tắt ứng dụng này trước khi đăng nhập.", "Port 8888 is currently occupied by '{0}' (PID {1}). Please close that application before logging in."),
            ["RefreshAccountsTip"] = ("Tải lại danh sách tài khoản Antigravity", "Reload Antigravity account list"),
            ["RestoreChat"] = ("💬 Khôi phục", "💬 Restore"),
            ["RestoreChatTip"] = ("Khôi phục và ánh xạ lại toàn bộ lịch sử chat bị mất về các project sidebar (Fix lỗi Antigravity 2.0)", "Restore and re-map all lost chat history to sidebar projects (Antigravity 2.0 Fix)"),
            ["RestoreChatSuccess"] = ("Đã khôi phục thành công {0} cuộc trò chuyện vào {1} project!", "Successfully restored {0} conversations across {1} projects!"),
            ["RestoreChatConfirm"] = ("Antigravity IDE đang mở. Để khôi phục và ánh xạ lịch sử chat an toàn, IDE sẽ được đóng và khởi động lại.\n\nBạn có muốn tiếp tục không?", "Antigravity IDE is currently running. To repair and restore chat history safely, IDE will be closed and restarted.\n\nDo you want to proceed?"),
            ["ThemeDark"] = ("Tối", "Dark"),
            ["ThemeLight"] = ("Sáng", "Light"),
            ["ThemeToggleTip"] = ("Chuyển đổi giao diện Sáng / Tối (Dark / Light Mode)", "Toggle Light / Dark Mode"),
            ["LangToggleTip"] = ("Chuyển đổi ngôn ngữ (Tiếng Việt / English)", "Switch language (Tiếng Việt / English)"),

            // Tab 1: Chrome View
            ["DefaultProfileBadge"] = ("MẶC ĐỊNH HIỆN TẠI", "CURRENT DEFAULT"),
            ["NoDefaultProfile"] = ("Chưa chọn profile mặc định", "No default profile selected"),
            ["OpenChrome"] = ("🚀 Mở Chrome", "🚀 Open Chrome"),
            ["OpenChromeTip"] = ("Mở trình duyệt Google Chrome với profile mặc định này", "Open Google Chrome with this default profile"),
            ["SearchChromePlaceholder"] = ("Tìm kiếm theo tên profile...", "Search by profile name..."),
            ["Total"] = ("Tổng:", "Total:"),
            ["StatusRunning"] = ("Đang chạy", "Running"),
            ["StatusClosed"] = ("Đã đóng", "Closed"),
            ["SetDefaultBtn"] = ("★ Đặt mặc định", "★ Set Default"),
            ["DefaultBadge"] = ("Mặc định", "Default"),
            ["OpenBtn"] = ("Mở", "Open"),
            ["AutoProtectHeader"] = ("TÙY CHỌN TỰ ĐỘNG BẢO VỆ", "AUTO PROTECTION OPTIONS"),
            ["OptAutoProtect"] = ("Tự động bảo vệ & khôi phục file cấu hình khi Chrome đóng", "Auto protect & restore config file when Chrome closes"),
            ["OptOpenImmediately"] = ("Tự động mở Chrome ngay sau khi bấm 'Đặt làm mặc định'", "Automatically open Chrome immediately after setting default"),
            ["OptUpdateShortcuts"] = ("Cập nhật các phím tắt Chrome trên Desktop & Taskbar", "Update Chrome shortcuts on Desktop & Taskbar"),
            ["OptStartWithWindows"] = ("Khởi động cùng Windows (chạy ngầm trong khay hệ thống)", "Start with Windows (run in system tray)"),
            ["OptMinimizeToTray"] = ("Thu nhỏ xuống khay hệ thống khi bấm nút đóng [X]", "Minimize to system tray when closing [X]"),

            // Tab 2: Antigravity View - Hero
            ["HeroActiveBadge"] = ("🚀 ĐANG DÙNG TRONG IDE", "🚀 ACTIVE IN IDE"),
            ["NoAccountSelected"] = ("Chưa chọn tài khoản", "No account selected"),
            ["NoAccountConnected"] = ("Chưa kết nối tài khoản", "No account connected"),
            ["NotLoggedIn"] = ("Chưa đăng nhập", "Not logged in"),
            ["NotSynced"] = ("Chưa đồng bộ", "Not synced"),
            ["HeroNameTip"] = ("Nhấp chuột để ẩn/hiện tên và email tài khoản", "Click to toggle hiding name & email"),
            ["HeroEmailTip"] = ("Nhấp chuột trái: Ẩn/Hiện thông tin | Nhấp chuột phải: Sao chép email", "Left-click: Hide/Show info | Right-click: Copy email"),
            ["CreditsTip"] = ("Số AI Credits bổ sung", "Available AI Credits"),
            ["LsConnected"] = ("Language Server: Kết nối ổn định", "Language Server: Connected"),
            ["LsStarting"] = ("Language Server: Đang khởi động", "Language Server: Starting..."),
            ["LsInactive"] = ("Language Server: Chưa bật", "Language Server: Inactive"),
            ["HeroRamTip"] = ("Lượng bộ nhớ RAM đang tiêu thụ bởi Antigravity IDE và AI Language Server", "RAM consumed by Antigravity IDE & AI Language Server"),
            ["GeminiQuotaTip"] = ("Hạn mức mô hình Gemini Flash & Pro", "Gemini Flash & Pro model quota"),
            ["ClaudeQuotaTip"] = ("Hạn mức mô hình Claude Opus/Sonnet & GPT-OSS", "Claude Opus/Sonnet & GPT-OSS quota"),
            ["LoadingQuota"] = ("Đang tải...", "Loading..."),
            ["PendingQuota"] = ("Chờ đo...", "Pending..."),
            ["RemodulateQuota"] = ("Đo lại Quota", "Measure Quota"),
            ["RemodulateQuotaTip"] = ("Bấm để kiểm tra lại hạn mức model trực tiếp từ Language Server", "Re-check model quota directly from Language Server"),
            ["View20Models"] = ("📊 Xem 20 Model Quota ▼", "📊 View 20 Models ▼"),
            ["CollapseModels"] = ("▲ Thu gọn 20 Model", "▲ Collapse Models"),
            ["Toggle20ModelsTip"] = ("Ẩn / hiện bảng chi tiết 20 model", "Show / hide detailed 20-model table"),
            ["SeparateWindow"] = ("Cửa sổ riêng", "Pop-out Window"),
            ["SeparateWindowTip"] = ("Mở bảng hạn mức dưới dạng cửa sổ phóng to riêng biệt", "Open quota dashboard in a separate window"),
            ["ReloadIdeBtn"] = ("Tải lại IDE (1s)", "Reload IDE (1s)"),
            ["ReloadIdeTip"] = ("Tải lại IDE tức thì trong 1 giây để áp dụng tài khoản mới mà không mất tab/file", "Instantly reload IDE in 1 second to apply new account without losing files"),
            ["SaveAccountBtn"] = ("Lưu Account", "Save Account"),
            ["SaveAccountTip"] = ("Lưu tài khoản hiện tại vào danh sách quản lý", "Save current active account to managed list"),
            ["5Hours"] = ("5 giờ", "5 Hours"),
            ["Weekly"] = ("Tuần", "Weekly"),

            // Tab 2: Toolbar
            ["SearchAntigravityPlaceholder"] = ("Tìm kiếm theo email, tên account...", "Search by email, account name..."),
            ["MeasureAllQuota"] = ("Đo Quota Tất Cả Acc", "Measure All Quotas"),
            ["MeasureAllQuotaTip"] = ("Kích hoạt đo hạn mức trực tiếp cho toàn bộ tài khoản (Chuột phải để chọn số luồng)", "Measure quota directly for all accounts (Right-click to select threads)"),
            ["ThreadsFormat"] = ("{0} luồng", "{0} threads"),
            ["ConcurrencyTip"] = ("Nhấp chuột để chọn số luồng song song (1 - 15 luồng)", "Click to select concurrency threads (1 - 15)"),
            ["AutoQuotaOn"] = ("Auto Quota: Bật", "Auto Quota: ON"),
            ["AutoQuotaOff"] = ("Auto Quota: Tắt", "Auto Quota: OFF"),
            ["AutoQuotaTip"] = ("Bật/Tắt tự động đo Quota định kỳ cho toàn bộ tài khoản (Chuột phải để chọn chu kỳ)", "Toggle automatic Quota measurement for all accounts (Right-click to configure)"),
            ["IntervalTip"] = ("Nhấp chuột để chọn chu kỳ tự động đo", "Click to select auto-measure interval"),
            ["PrivacyHideInfo"] = ("Ẩn thông tin", "Hide Info"),
            ["PrivacyAllHidden"] = ("Đang ẩn tất cả", "All Hidden"),
            ["PrivacyNamesHidden"] = ("Đang ẩn tên", "Names Hidden"),
            ["PrivacyEmailsHidden"] = ("Đang ẩn mail", "Emails Hidden"),
            ["PrivacyTip"] = ("Ẩn/Hiện thông tin tên và email tất cả tài khoản (Chuột phải để chọn chi tiết)", "Hide/Show names & emails of all accounts (Right-click for options)"),
            ["PrivacyMenuTip"] = ("Tùy chọn: Ẩn tất cả tên, Ẩn tất cả mail, hoặc Hiện tất cả", "Options: Hide all names, hide all emails, or show all"),
            ["FilterPlan"] = ("Lọc theo gói", "Filter by Plan"),
            ["FilterPlanAll"] = ("Tất cả gói", "All Plans"),
            ["FilterPlanTip"] = ("Lọc theo gói tài khoản (Google AI Pro, Starter...)", "Filter by account plan (Google AI Pro, Starter...)"),
            ["SortPrefix"] = ("Sắp xếp:", "Sort:"),
            ["SortTotal"] = ("Score tổng", "Total Score"),
            ["SortRecent"] = ("Gần đây", "Recent"),
            ["SortClaude"] = ("Claude/GPT", "Claude/GPT"),
            ["SortPro3"] = ("Gemini Pro", "Gemini Pro"),
            ["SortFlash"] = ("Gemini Flash", "Gemini Flash"),
            ["SortTip"] = ("Sắp xếp tài khoản theo hạn mức còn lại hoặc thời gian", "Sort accounts by remaining quota or usage"),
            ["SortDesc"] = ("Lớn ➔ Nhỏ", "High ➔ Low"),
            ["SortAsc"] = ("Nhỏ ➔ Lớn", "Low ➔ High"),
            ["SortOrderTip"] = ("Thứ tự: Lớn ➔ Nhỏ hoặc Nhỏ ➔ Lớn", "Sort order: High to Low or Low to High"),
            ["ScopeBoth"] = ("Cả hai", "Both"),
            ["Scope5h"] = ("5 giờ", "5 Hours"),
            ["ScopeWeek"] = ("Tuần", "Weekly"),
            ["ScopeTip"] = ("Chọn phạm vi tính điểm & hiển thị hạn mức (5 giờ, Tuần hoặc Cả hai)", "Select quota scope for scoring and display (5h, Weekly, or Both)"),

            // Recommendation Card
            ["SmartRecommendation"] = ("Gợi ý thông minh:", "Smart Recommendation:"),
            ["SwitchNow"] = ("Chuyển ngay", "Switch Now"),

            // Account List Items
            ["SwitchBtn"] = ("Switch", "Switch"),
            ["SwitchTip"] = ("Chuyển sang tài khoản này và khởi động lại Language Server", "Switch to this account and restart Language Server"),
            ["RenameAccount"] = ("Đổi tên", "Rename"),
            ["DeleteAccount"] = ("Xóa", "Delete"),
            ["ActiveBadge"] = ("Đang dùng", "Active"),
            ["MeasuringBadge"] = ("Đang đo...", "Measuring..."),

            // Modals & Context Menus
            ["CustomIntervalTitle"] = ("Tùy Chỉnh Chu Kỳ Đo Quota", "Custom Auto-Measure Interval"),
            ["CustomIntervalSubtitle"] = ("Cài đặt khoảng thời gian đo Quota tự động cho tất cả tài khoản", "Set automatic quota measurement interval for all accounts"),
            ["CustomIntervalDesc"] = ("Nhập khoảng thời gian bạn muốn hệ thống tự động kiểm tra hạn mức AI (tối thiểu 5 giây):", "Enter interval to automatically check AI quota (min 5 seconds):"),
            ["UnitSeconds"] = ("Giây (s)", "Seconds (s)"),
            ["UnitMinutes"] = ("Phút (m)", "Minutes (m)"),
            ["UnitHours"] = ("Giờ (h)", "Hours (h)"),
            ["QuickPresets"] = ("Gợi ý:", "Presets:"),
            ["Cancel"] = ("Hủy bỏ", "Cancel"),
            ["ApplyAndEnable"] = ("Áp dụng & Bật", "Apply & Enable"),
            ["Close"] = ("Đóng", "Close"),

            // Privacy Menu Items
            ["PrivacyMenuHeader"] = ("🛡 QUẢN LÝ BẢO MẬT & ẨN THÔNG TIN", "🛡 PRIVACY & DATA MASKING"),
            ["PrivacyChromeMenuHeader"] = ("🛡 QUẢN LÝ BẢO MẬT & ẨN THÔNG TIN (CHROME)", "🛡 CHROME PRIVACY & DATA MASKING"),
            ["PrivacyHideAllNames"] = ("Ẩn tất cả Tên tài khoản", "Hide All Account Names"),
            ["PrivacyHideAllEmails"] = ("Ẩn tất cả Email tài khoản", "Hide All Account Emails"),
            ["PrivacyHideAllChromeNames"] = ("Ẩn tất cả Tên profile", "Hide All Profile Names"),
            ["PrivacyHideAllChromeEmails"] = ("Ẩn tất cả Email Google", "Hide All Google Emails"),
            ["PrivacyHideAllBoth"] = ("🔒  Ẩn cả Tên và Email (Tất cả)", "🔒  Hide Both Name & Email (All)"),
            ["PrivacyShowAll"] = ("👁  Hiện lại tất cả thông tin", "👁  Show All Information"),
            ["PrivacyChromeTip"] = ("Ẩn/Hiện thông tin tên và email tất cả profile Chrome (Chuột phải để chọn chi tiết)", "Hide/Show names & emails of all Chrome profiles (Right-click for options)"),

            // Auto Refresh Menu Items
            ["AutoRefreshMenuHeader"] = ("⏱ TỰ ĐỘNG ĐO QUOTA (ALL ACC)", "⏱ AUTO MEASURE QUOTA (ALL)"),
            ["AutoRefreshToggle"] = ("Bật tự động đo Quota", "Enable Auto Measure Quota"),
            ["CustomIntervalMenuItem"] = ("⚙  Tùy chỉnh thời gian...", "⚙  Custom Interval..."),

            // Concurrency Menu Items
            ["ConcurrencyMenuHeader"] = ("⚡ SỐ LUỒNG ĐO ĐỒNG THỜI", "⚡ PARALLEL MEASURE THREADS"),
            ["ConcurrencyDesc"] = ("Tối ưu hóa tốc độ đo Quota", "Optimize quota measurement speed"),

            // Model Quota Window
            ["LastUpdated"] = ("Cập nhật lúc: ", "Last updated: "),
            ["RemodulateQuotaAction"] = ("Đo lại Quota", "Remodulate Quota")
        };

        public static string T(string key)
        {
            if (Strings.TryGetValue(key, out var val))
            {
                return IsEnglish ? val.En : val.Vi;
            }
            return key;
        }

        public static string T(string key, params object[] args)
        {
            string fmt = T(key);
            try
            {
                return string.Format(fmt, args);
            }
            catch
            {
                return fmt;
            }
        }
    }
}
