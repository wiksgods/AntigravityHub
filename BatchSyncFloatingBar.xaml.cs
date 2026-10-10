using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProfileSwitchChrome.Services;

namespace ProfileSwitchChrome
{
    public partial class BatchSyncFloatingBar : Window
    {
        public event Action? SkipRequested;
        public event Action? StopRequested;
        public event Action<bool>? AutoClickToggled;

        private bool _isManuallyMoved = false;
        private readonly DispatcherTimer _followTimer;

        public BatchSyncFloatingBar()
        {
            InitializeComponent();

            _followTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _followTimer.Tick += FollowTimer_Tick;

            this.Loaded += (s, e) =>
            {
                ChkAutoClickFloating.IsChecked = GoogleOAuthAutoConsentService.AutoClickSignInEnabled;
                CenterOnScreenInitial();
                _followTimer.Start();
                if (GoogleOAuthAutoConsentService.LastOAuthHwnd != IntPtr.Zero)
                {
                    AlignToWindow(GoogleOAuthAutoConsentService.LastOAuthHwnd);
                }
            };

            this.Closed += (s, e) =>
            {
                _followTimer.Stop();
            };
        }

        private void CenterOnScreenInitial()
        {
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            if (sw <= 0) sw = 1920;
            if (sh <= 0) sh = 1080;

            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                dpiScaleX = dpi.DpiScaleX;
                dpiScaleY = dpi.DpiScaleY;
            }
            catch { }
            if (dpiScaleX <= 0) dpiScaleX = 1.0;
            if (dpiScaleY <= 0) dpiScaleY = 1.0;

            double twDip = Math.Min(1020 / dpiScaleX, sw - 300);
            double thDip = Math.Min(880 / dpiScaleY, Math.Max(600, sh - 60));
            double barW = this.Width > 0 ? this.Width : 280;

            double comboW = twDip + 12 + barW;
            if (comboW <= sw - 20)
            {
                // Căn giữa cả cụm (Chrome + Popup điều khiển) cạnh nhau
                double chromePx = Math.Max(10, (sw - comboW) / 2);
                double chromePy = Math.Max(10, (sh - thDip) / 2);
                this.Left = chromePx + twDip + 12;
                this.Top = chromePy + 10;
            }
            else
            {
                // Màn hình quá nhỏ
                this.Left = Math.Max(10, sw - barW - 15);
                this.Top = 15;
            }
        }

        private void FollowTimer_Tick(object? sender, EventArgs e)
        {
            IntPtr hwnd = GoogleOAuthAutoConsentService.LastOAuthHwnd;
            if (hwnd != IntPtr.Zero && GoogleOAuthAutoConsentService.IsWindow(hwnd))
            {
                AlignToWindow(hwnd);
            }
        }

        public void UpdateProgress(int currentIndex, int totalCount, string email, string? status = null)
        {
            Dispatcher.Invoke(() =>
            {
                TxtProgressPill.Text = $"{currentIndex} / {totalCount}";
                TxtAccountEmail.Text = string.IsNullOrWhiteSpace(email) ? "Tài khoản Google" : email;
                TxtAccountInitial.Text = !string.IsNullOrWhiteSpace(email) ? email[0].ToString().ToUpperInvariant() : "G";
                TxtStatusMessage.Text = status ?? "Vui lòng xác nhận trên trình duyệt...";
            });
        }

        public void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    TxtStatusMessage.Text = message;
                }
            });
        }

        public void AlignToWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !GoogleOAuthAutoConsentService.IsWindow(hWnd)) return;
            if (_isManuallyMoved) return; // User moved the window, respect their placement

            if (!GoogleOAuthAutoConsentService.GetWindowRect(hWnd, out var rect)) return;
            int widthPx = rect.Right - rect.Left;
            int heightPx = rect.Bottom - rect.Top;
            if (widthPx < 120 || heightPx < 120) return;

            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                dpiScaleX = dpi.DpiScaleX;
                dpiScaleY = dpi.DpiScaleY;
            }
            catch { }

            double cLeft = rect.Left / dpiScaleX;
            double cTop = rect.Top / dpiScaleY;
            double cRight = rect.Right / dpiScaleX;
            double cBottom = rect.Bottom / dpiScaleY;

            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;

            double barW = this.ActualWidth > 0 ? this.ActualWidth : 280;
            double barH = this.ActualHeight > 0 ? this.ActualHeight : 160;

            // Vị trí ưu tiên: Cạnh bên phải của cửa sổ Chrome
            double targetLeft = cRight + 12;
            double targetTop = cTop + 10;

            // Nếu bên phải màn hình không đủ chỗ, chuyển sang cạnh bên trái Chrome
            if (targetLeft + barW > screenW - 10)
            {
                targetLeft = cLeft - barW - 12;
            }

            // Nếu cả bên trái cũng không đủ chỗ (màn hình nhỏ), đưa lên góc trên Chrome
            if (targetLeft < 10)
            {
                targetLeft = Math.Max(10, cRight - barW - 10);
                targetTop = Math.Max(10, cTop - barH - 12);
                if (targetTop < 10)
                {
                    targetTop = cTop + 10;
                }
            }

            targetTop = Math.Min(screenH - barH - 10, Math.Max(10, targetTop));

            // Chỉ cập nhật khi vị trí lệch đáng kể (> 3px) để tránh hiện tượng giật rung
            if (Math.Abs(this.Left - targetLeft) > 3 || Math.Abs(this.Top - targetTop) > 3)
            {
                this.Left = targetLeft;
                this.Top = targetTop;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                _isManuallyMoved = true;
                DragMove();
            }
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            SkipRequested?.Invoke();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopRequested?.Invoke();
        }

        public void SetAutoClickState(bool enabled)
        {
            Dispatcher.Invoke(() =>
            {
                ChkAutoClickFloating.IsChecked = enabled;
            });
        }

        private void ChkAutoClickFloating_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkAutoClickFloating.IsChecked == true;
            GoogleOAuthAutoConsentService.AutoClickSignInEnabled = enabled;
            AutoClickToggled?.Invoke(enabled);
        }
    }
}
