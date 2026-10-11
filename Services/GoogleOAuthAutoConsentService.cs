using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ProfileSwitchChrome.Services
{
    /// <summary>
    /// Service giám sát phiên Google OAuth trong Chrome và hỗ trợ đóng cửa sổ tự động sau khi đăng nhập xong.
    /// Có chế độ tự động bấm nút Đăng nhập (Sign in) khi được bật:
    /// - Chỉ bấm khi thực sự phát hiện thấy nút Đăng nhập / Sign in / Tiếp tục / Continue.
    /// - Nếu không tìm thấy nút sau thời gian chờ, thông báo cho người dùng biết để thao tác thủ công.
    /// </summary>
    public static class GoogleOAuthAutoConsentService
    {
        #region Win32 Interop
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint WM_CLOSE = 0x0010;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        #endregion

        #region Auto Click Sign-In
        public static bool AutoClickSignInEnabled { get; set; } = false;

        private static readonly string[] TargetKeywords = new[]
        {
            "đăng nhập",
            "dang nhap",
            "sign in",
            "tiếp tục",
            "tiep tuc",
            "continue",
            "tiếp theo",
            "tiep theo",
            "next",
            "cho phép",
            "cho phep",
            "allow"
        };

        private static readonly string[] ExcludeKeywords = new[]
        {
            "hủy",
            "huỷ",
            "cancel",
            "quay lại",
            "back",
            "trợ giúp",
            "help",
            "quyền riêng tư",
            "privacy",
            "điều khoản",
            "terms",
            "tiếng việt",
            "english",
            "ngôn ngữ"
        };

        public static bool TryFindAndClickSignInButton(IntPtr hWnd, out string detectedButtonName)
        {
            detectedButtonName = "";
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;

            try
            {
                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return false;

                AutomationElement? foundBtn = null;
                string foundName = "";

                // 1. Quét tìm trong danh sách ControlType.Button
                try
                {
                    var btnCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                    var buttons = root.FindAll(TreeScope.Descendants, btnCondition);
                    if (buttons != null && buttons.Count > 0)
                    {
                        for (int i = 0; i < buttons.Count; i++)
                        {
                            var b = buttons[i];
                            if (IsTargetSignInButton(b, out string mName))
                            {
                                foundBtn = b;
                                foundName = mName;
                                break;
                            }
                        }
                    }
                }
                catch { }

                // 2. Fallback nếu không có ControlType.Button: quét Custom / Hyperlink
                if (foundBtn == null)
                {
                    try
                    {
                        var customCondition = new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)
                        );
                        var elements = root.FindAll(TreeScope.Descendants, customCondition);
                        if (elements != null && elements.Count > 0)
                        {
                            for (int i = 0; i < elements.Count; i++)
                            {
                                var el = elements[i];
                                if (IsTargetSignInButton(el, out string mName))
                                {
                                    foundBtn = el;
                                    foundName = mName;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (foundBtn != null)
                {
                    detectedButtonName = foundName;
                    return PerformClickOnElement(hWnd, foundBtn);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryFindAndClickSignInButton exception: {ex.Message}");
            }

            return false;
        }

        private static bool IsTargetSignInButton(AutomationElement element, out string matchedName)
        {
            matchedName = "";
            try
            {
                string name = element.Current.Name?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name)) return false;

                foreach (var ex in ExcludeKeywords)
                {
                    if (name.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }

                foreach (var target in TargetKeywords)
                {
                    if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase) ||
                        (name.Length < 35 && name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var rect = element.Current.BoundingRectangle;
                        if (!rect.IsEmpty && rect.Width > 15 && rect.Height > 10)
                        {
                            matchedName = name;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool PerformClickOnElement(IntPtr hWnd, AutomationElement element)
        {
            bool clicked = false;

            // Cách 1: Thử InvokePattern của UI Automation
            try
            {
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object pObj) && pObj is InvokePattern inv)
                {
                    inv.Invoke();
                    clicked = true;
                }
            }
            catch { }

            // Cách 2: Click chuột vật lý vào toạ độ tâm của nút
            try
            {
                var rect = element.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    int cx = (int)(rect.Left + rect.Width / 2);
                    int cy = (int)(rect.Top + rect.Height / 2);

                    SetForegroundWindow(hWnd);
                    Thread.Sleep(30);

                    GetCursorPos(out POINT orig);
                    SetCursorPos(cx, cy);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    SetCursorPos(orig.X, orig.Y);
                    clicked = true;
                }
            }
            catch { }

            return clicked;
        }
        #endregion

        public static int CdpPort { get; set; } = 9223;

        public static IntPtr LastOAuthHwnd { get; private set; } = IntPtr.Zero;
        public static event Action<IntPtr>? OAuthWindowDetected;

        public static void RestoreAnyHiddenChromeWindows()
        {
            // Giữ lại để đảm bảo tương thích callers
        }

        /// <summary>
        /// Checks if a window belongs to a browser process (Chrome, Edge, Brave, etc.)
        /// and explicitly NOT Antigravity or any Electron IDE.
        /// </summary>
        public static bool IsBrowserWindow(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
                GetWindowThreadProcessId(hWnd, out uint procId);
                if (procId == 0) return false;
                using var proc = Process.GetProcessById((int)procId);
                string name = proc.ProcessName.ToLowerInvariant();

                // STRICT: Never touch Antigravity IDE, VS Code, or Electron
                if (name.Contains("antigravity") || name.Contains("code") || name.Contains("electron"))
                    return false;

                return name.Contains("chrome") || name.Contains("msedge") || name.Contains("brave") || name.Contains("opera") || name.Contains("firefox");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Đóng cửa sổ/tab OAuth bằng WM_CLOSE sau khi hoàn tất đăng nhập.
        /// Đảm bảo TUYỆT ĐỐI không bao giờ gửi WM_CLOSE đến Antigravity IDE.
        /// </summary>
        public static void CloseOAuthTab(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                {
                    if (IsBrowserWindow(hwnd))
                    {
                        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            catch { }
            finally
            {
                if (LastOAuthHwnd == hwnd)
                {
                    LastOAuthHwnd = IntPtr.Zero;
                    OAuthWindowDetected?.Invoke(IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Đóng ngay lập tức tất cả cửa sổ trình duyệt OAuth / Callback thành công.
        /// </summary>
        public static void CloseAllOAuthWindows()
        {
            try
            {
                if (LastOAuthHwnd != IntPtr.Zero && IsWindow(LastOAuthHwnd) && IsBrowserWindow(LastOAuthHwnd))
                {
                    CloseOAuthTab(LastOAuthHwnd);
                }

                // Quét và đóng ngay các cửa sổ trình duyệt đang hiển thị trang callback hoặc Google OAuth
                EnumWindows((hWnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd) || !IsBrowserWindow(hWnd)) return true;

                        var classSb = new StringBuilder(64);
                        GetClassName(hWnd, classSb, 64);
                        if (classSb.ToString().Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                        {
                            string title = GetWindowTitle(hWnd);
                            if (IsCallbackSuccess(title) || IsGoogleOAuthTitle(title))
                            {
                                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        private static CancellationTokenSource? _currentWatchCts;
        private static readonly object _watchLock = new object();

        /// <summary>
        /// Dừng ngay lập tức watcher đang chạy nếu có
        /// </summary>
        public static void StopWatching()
        {
            lock (_watchLock)
            {
                try
                {
                    _currentWatchCts?.Cancel();
                    _currentWatchCts?.Dispose();
                }
                catch { }
                _currentWatchCts = null;
            }
        }

        /// <summary>
        /// Giám sát cửa sổ đăng nhập Chrome để cập nhật trạng thái và tự hủy khi người dùng đóng trình duyệt.
        /// Người dùng tự bấm nút trên giao diện Chrome, service hoàn toàn không can thiệp chuột/phím/focus.
        /// </summary>
        public static async Task StartWatchingAsync(
            CancellationToken ct,
            Action<string>? statusCallback = null,
            string? loginHint = null,
            bool isSilent = false,
            Action? onBrowserClosed = null)
        {
            CancellationTokenSource localCts;
            lock (_watchLock)
            {
                try
                {
                    _currentWatchCts?.Cancel();
                    _currentWatchCts?.Dispose();
                }
                catch { }
                _currentWatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                localCts = _currentWatchCts;
            }

            try
            {
                LastOAuthHwnd = IntPtr.Zero;
                DateTime startTime = DateTime.UtcNow;
                bool hasSeenOAuthWindow = false;
                bool hasEnforcedSmallSize = false;
                int notFoundCount = 0;
                string lastReportedMsg = "";
                DateTime? firstOAuthSeenTime = null;
                bool hasAttemptedAutoClick = false;
                int autoClickScanCount = 0;
                bool reportedNotFound = false;

                // Chụp danh sách các cửa sổ trình duyệt hiện có TRƯỚC KHI Chrome mở tab/window mới
                var existingHwnds = new HashSet<IntPtr>();
                try
                {
                    EnumWindows((hWnd, _) =>
                    {
                        if (IsWindowVisible(hWnd) && IsBrowserWindow(hWnd))
                        {
                            existingHwnds.Add(hWnd);
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                void Notify(string msg)
                {
                    if (string.IsNullOrWhiteSpace(msg) || msg == lastReportedMsg) return;
                    lastReportedMsg = msg;
                    statusCallback?.Invoke(msg);
                }

                bool hasResizedWindow = false;

                while (!localCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        IntPtr oauthHwnd = FindOAuthWindow(existingHwnds);

                        if (oauthHwnd == IntPtr.Zero)
                        {
                            if (hasSeenOAuthWindow)
                            {
                                notFoundCount++;
                                if (notFoundCount >= 4)
                                {
                                    Notify("Trình duyệt đăng nhập đã đóng.");
                                    onBrowserClosed?.Invoke();
                                    break;
                                }
                            }
                            else
                            {
                                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                                if (elapsed > 1.5 && elapsed < 30.0)
                                {
                                    Notify(!string.IsNullOrWhiteSpace(loginHint)
                                        ? $"Đang mở trình duyệt cho '{loginHint}'..."
                                        : "Đang mở trình duyệt đăng nhập...");
                                }
                            }
                            // Polling cực nhanh 40ms để bắt cửa sổ ngay khi xuất hiện
                            await Task.Delay(hasSeenOAuthWindow ? 300 : 40, localCts.Token);
                            continue;
                        }

                        hasSeenOAuthWindow = true;
                        notFoundCount = 0;
                        LastOAuthHwnd = oauthHwnd;
                        OAuthWindowDetected?.Invoke(oauthHwnd);

                        // Tự động điều chỉnh kích thước cửa sổ và căn giữa màn hình
                        // Nếu Chrome đang maximize hoặc to hơn giới hạn, thu nhỏ về kích thước chuẩn
                        if (!hasEnforcedSmallSize && IsBrowserWindow(oauthHwnd))
                        {
                            try
                            {
                                // Kiểm tra kích thước thực tế - nếu đã đúng kích thước thì bỏ qua resize
                                GetWindowRect(oauthHwnd, out RECT currentRect);
                                int currentW = currentRect.Right - currentRect.Left;
                                int currentH = currentRect.Bottom - currentRect.Top;

                                int sw = GetSystemMetrics(SM_CXSCREEN);
                                int sh = GetSystemMetrics(SM_CYSCREEN);
                                if (sw <= 0) sw = 1920;
                                if (sh <= 0) sh = 1080;
                                int tw = Math.Min(1020, Math.Max(840, sw - 320));
                                int th = Math.Min(880, Math.Max(660, sh - 80));

                                // Nếu cửa sổ đang maximized hoặc quá to (>= 110% kích thước mục tiêu) thì resize
                                bool needsResize = IsZoomed(oauthHwnd) || currentW > tw * 1.1 || currentH > th * 1.1;
                                if (needsResize)
                                {
                                    if (IsZoomed(oauthHwnd))
                                    {
                                        ShowWindow(oauthHwnd, SW_RESTORE);
                                        await Task.Delay(150, localCts.Token); // Đợi restore animation
                                    }

                                    int barW = 280;
                                    int comboW = tw + 12 + barW;
                                    int px, py;
                                    if (comboW <= sw - 20)
                                    {
                                        px = Math.Max(10, (sw - comboW) / 2);
                                        py = Math.Max(10, (sh - th) / 2);
                                    }
                                    else
                                    {
                                        px = Math.Max(0, (sw - tw) / 2);
                                        py = Math.Max(0, (sh - th) / 2);
                                    }
                                    SetWindowPos(oauthHwnd, IntPtr.Zero, px, py, tw, th, SWP_NOZORDER | SWP_SHOWWINDOW);
                                    hasResizedWindow = true;
                                }
                                hasEnforcedSmallSize = true;
                            }
                            catch { }
                        }

                        string windowTitle = GetWindowTitle(oauthHwnd);
                        if (IsCallbackSuccess(windowTitle))
                        {
                            Notify(LocalizationManager.IsEnglish ? "Google sign-in successful! Saving account..." : "Đăng nhập Google thành công! Đang lưu thông tin...");
                            await Task.Delay(500, localCts.Token);
                            break;
                        }

                        if (AutoClickSignInEnabled && !hasAttemptedAutoClick)
                        {
                            if (firstOAuthSeenTime == null)
                            {
                                firstOAuthSeenTime = DateTime.UtcNow;
                            }

                            double elapsedSec = (DateTime.UtcNow - firstOAuthSeenTime.Value).TotalSeconds;

                            // Đợi lâu hơn nếu vừa resize để trang web render lại layout
                            double waitBeforeClick = hasResizedWindow ? 1.5 : 0.9;
                            if (elapsedSec >= waitBeforeClick)
                            {
                                if (TryFindAndClickSignInButton(oauthHwnd, out string btnName))
                                {
                                    hasAttemptedAutoClick = true;
                                    Notify(LocalizationManager.IsEnglish ? $"✓ Detected button '{btnName}' and clicked automatically!" : $"✓ Đã phát hiện nút '{btnName}' và tự động bấm!");
                                }
                                else
                                {
                                    autoClickScanCount++;
                                    if (autoClickScanCount % 3 == 0 && elapsedSec < 12.0)
                                    {
                                        Notify(LocalizationManager.IsEnglish ? "Searching for Sign in button on browser..." : "Đang tìm nút Đăng nhập trên trình duyệt...");
                                    }
                                    else if (elapsedSec >= 12.0 && !reportedNotFound)
                                    {
                                        hasAttemptedAutoClick = true; // Dừng quét
                                        reportedNotFound = true;
                                        Notify(LocalizationManager.IsEnglish ? "⚠️ Sign in button not found! Please click manually." : "⚠️ Không tìm thấy nút Đăng nhập! Vui lòng bấm thủ công.");
                                    }
                                }
                            }
                            else
                            {
                                Notify(LocalizationManager.IsEnglish ? "Loading Google sign-in page..." : "Đang tải trang đăng nhập Google...");
                            }
                        }
                        else if (!AutoClickSignInEnabled && !hasAttemptedAutoClick)
                        {
                            hasAttemptedAutoClick = true; // Chỉ thông báo 1 lần
                            Notify(!string.IsNullOrWhiteSpace(loginHint)
                                ? (LocalizationManager.IsEnglish ? $"Please confirm login in browser for '{loginHint}'" : $"Xác nhận đăng nhập trên trình duyệt cho '{loginHint}'")
                                : (LocalizationManager.IsEnglish ? "Please confirm login in browser..." : "Xác nhận đăng nhập trên trình duyệt..."));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"GoogleOAuthAutoConsentService loop error: {ex.Message}");
                    }

                    // Polling chậm hơn sau khi đã enforce kích thước để tiết kiệm CPU
                    await Task.Delay(hasEnforcedSmallSize ? 350 : 50, localCts.Token);
                }

            }
            finally
            {
                lock (_watchLock)
                {
                    if (_currentWatchCts == localCts)
                    {
                        _currentWatchCts = null;
                    }
                }
            }
        }

        private static IntPtr FindOAuthWindow(HashSet<IntPtr>? existingHwnds = null)
        {
            if (LastOAuthHwnd != IntPtr.Zero && IsWindow(LastOAuthHwnd) && IsBrowserWindow(LastOAuthHwnd))
            {
                string title = GetWindowTitle(LastOAuthHwnd);
                if (IsGoogleOAuthTitle(title) || IsCallbackSuccess(title))
                {
                    return LastOAuthHwnd;
                }
            }

            IntPtr found = IntPtr.Zero;
            IntPtr newBrowserHwnd = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd) || !IsBrowserWindow(hWnd)) return true;

                    var classSb = new StringBuilder(64);
                    GetClassName(hWnd, classSb, 64);
                    if (classSb.ToString().Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                    {
                        string title = GetWindowTitle(hWnd);
                        if (IsGoogleOAuthTitle(title) || IsCallbackSuccess(title))
                        {
                            found = hWnd;
                            return false;
                        }

                        // Nếu là cửa sổ Chrome mới tinh vừa xuất hiện sau lệnh StartWatchingAsync
                        if (existingHwnds != null && !existingHwnds.Contains(hWnd))
                        {
                            newBrowserHwnd = hWnd;
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero ? found : newBrowserHwnd;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return "";
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            return sb.ToString().Trim();
        }

        private static bool IsGoogleOAuthTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string low = title.ToLowerInvariant();
            return low.Contains("accounts.google.com") ||
                   low.Contains("google.com/signin") ||
                   low.Contains("google.com") ||
                   low.Contains("đăng nhập") ||
                   low.Contains("sign in") ||
                   low.Contains("tài khoản google") ||
                   low.Contains("google account") ||
                   low.Contains("choose an account") ||
                   low.Contains("chọn một tài khoản") ||
                   low.Contains("chọn tài khoản") ||
                   low.Contains("sign in with google") ||
                   low.Contains("đăng nhập bằng google") ||
                   low.Contains("xác minh") ||
                   low.Contains("verify") ||
                   low.Contains("oauth") ||
                   low.Contains("consent") ||
                   low.Contains("chào mừng") ||
                   low.Contains("welcome");
        }

        private static bool IsCallbackSuccess(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string low = title.ToLowerInvariant();
            return low.Contains("127.0.0.1:8888") ||
                   low.Contains("oauth-callback") ||
                   low.Contains("đăng nhập thành công") ||
                   low.Contains("login successful");
        }
    }
}
