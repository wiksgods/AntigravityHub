using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ProfileSwitchChrome.Models;
using ProfileSwitchChrome.Services;

namespace ProfileSwitchChrome
{
    public partial class ModelQuotaWindow : Window
    {
        private readonly AntigravityAccountInfo _account;
        private readonly Action<AntigravityAccountInfo>? _onSwitchRequested;

        public ModelQuotaWindow(AntigravityAccountInfo account, Action<AntigravityAccountInfo>? onSwitchRequested = null)
        {
            InitializeComponent();
            _account = account;
            _onSwitchRequested = onSwitchRequested;
            LoadAccountData();

            LocalizationManager.LanguageChanged += ApplyLanguage;

            Loaded += (s, e) =>
            {
                ApplyLanguage();
                BtnClose.Focus();
                Keyboard.Focus(BtnClose);
                AnimateWindowOpen();
            };

            Unloaded += (s, e) =>
            {
                LocalizationManager.LanguageChanged -= ApplyLanguage;
            };
        }

        private void ApplyLanguage()
        {
            this.Title = LocalizationManager.T("ModelQuotaTitle");
            if (TxtWinTitle != null) TxtWinTitle.Text = LocalizationManager.T("ModelQuotaTitle");
            if (Txt5HoursLabel != null) Txt5HoursLabel.Text = LocalizationManager.T("5Hours");
            if (TxtWeeklyLabel != null) TxtWeeklyLabel.Text = LocalizationManager.T("Weekly");
            if (BtnClose != null) BtnClose.Content = LocalizationManager.T("Close");
            if (TxtRefreshText != null && BtnRefreshQuota != null && BtnRefreshQuota.IsEnabled)
            {
                TxtRefreshText.Text = LocalizationManager.T("RemodulateQuotaAction");
            }
            LoadAccountData();
        }

        public void LoadAccountData()
        {
            TxtAccountName.Text = string.IsNullOrWhiteSpace(_account.Name) ? _account.Email : _account.Name;
            TxtAccountEmail.Text = _account.Email ?? (LocalizationManager.IsEnglish ? "Unknown email" : "Chưa rõ email");
            TxtPlanBadge.Text = _account.PlanBadgeDisplay;
            if (BorderPlanBadge != null)
            {
                BorderPlanBadge.Background = _account.PlanBadgeBackground;
                BorderPlanBadge.BorderBrush = _account.PlanBadgeBorderBrush;
                TxtPlanBadge.Foreground = _account.PlanBadgeForeground;
                if (_account.PlanBadgeText == "PRO")
                {
                    BorderPlanBadge.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = (Color)ColorConverter.ConvertFromString("#EC4899"),
                        BlurRadius = 10,
                        ShadowDepth = 0,
                        Opacity = 0.7
                    };
                    TxtPlanBadge.Text = _account.PlanBadgeDisplay;
                }
                else
                {
                    BorderPlanBadge.Effect = null;
                    TxtPlanBadge.Text = _account.PlanBadgeDisplay;
                }
            }
            TxtCredits.Text = _account.CreditsText;
            TxtInitial.Text = _account.InitialLetter;

            // Load Avatar
            if (_account.HasAvatar && _account.AvatarSource != null)
            {
                ImgAvatar.ImageSource = _account.AvatarSource;
                BorderAvatarImage.Visibility = Visibility.Visible;
            }
            else
            {
                BorderAvatarImage.Visibility = Visibility.Collapsed;
            }

            // Bind Models
            ItemsCol1.ItemsSource = _account.Col1Models;
            ItemsCol2.ItemsSource = _account.Col2Models;
            ItemsCol3.ItemsSource = _account.Col3Models;
            ItemsWeekly.ItemsSource = _account.WeeklyModels;

            // Summary & Timestamps
            TxtResetSummary.Text = _account.ResetCountdownSummary;
            if (_account.QuotaUpdatedAt.HasValue)
            {
                TxtLastUpdated.Text = $"{LocalizationManager.T("LastUpdated")}{_account.QuotaUpdatedAt.Value:HH:mm:ss dd/MM/yyyy}";
            }
            else
            {
                TxtLastUpdated.Text = LocalizationManager.IsEnglish ? "Estimated quota based on account" : "Hạn mức ước tính dựa trên tài khoản";
            }
        }

        private async void BtnRefreshQuota_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnRefreshQuota.IsEnabled) return;

            BtnRefreshQuota.IsEnabled = false;
            TxtRefreshText.Text = LocalizationManager.IsEnglish ? "Measuring quota..." : "Đang đo quota...";
            TxtRefreshText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));

            // Start continuous smooth 360-degree rotation animation on the icon
            var spinAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(750),
                RepeatBehavior = RepeatBehavior.Forever
            };
            RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);

            try
            {
                bool success = await AntigravityProfileManager.FetchLiveModelQuotaAsync(_account);
                LoadAccountData();

                // Stop spinning animation
                RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                RefreshIconRotate.Angle = 0;

                if (success)
                {
                    TxtRefreshIcon.Text = "✓";
                    TxtRefreshText.Text = LocalizationManager.IsEnglish ? "Updated!" : "Đã cập nhật!";
                    TxtRefreshText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                    await Task.Delay(1300);
                }
                else
                {
                    TxtRefreshIcon.Text = "⚠️";
                    TxtRefreshText.Text = LocalizationManager.IsEnglish ? "No response" : "Không phản hồi";
                    TxtRefreshText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                    await Task.Delay(1300);
                }
            }
            catch (Exception ex)
            {
                RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                RefreshIconRotate.Angle = 0;
                TxtRefreshIcon.Text = "❌";
                TxtRefreshText.Text = LocalizationManager.IsEnglish ? "Error measuring" : "Lỗi đo";
                TxtRefreshText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                await Task.Delay(1300);
            }
            finally
            {
                RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                RefreshIconRotate.Angle = 0;
                TxtRefreshIcon.Text = "🔄";
                TxtRefreshText.Text = LocalizationManager.T("RemodulateQuotaAction");
                TxtRefreshText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeSecondaryBtnFg");
                BtnRefreshQuota.IsEnabled = true;
            }
        }

        private void BtnPowerSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_onSwitchRequested != null)
            {
                Close();
                _onSwitchRequested.Invoke(_account);
            }
            else
            {
                MessageBox.Show($"Tài khoản {_account.Email} đang được chọn.", "Thông tin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnPowerSwitch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            BtnPowerSwitch_Click(sender, e);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            AnimateWindowClose();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                AnimateWindowClose();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Left)
            {
                if (BtnClose.IsFocused)
                {
                    BtnPowerSwitch.Focus();
                    e.Handled = true;
                }
                else if (BtnPowerSwitch.IsFocused)
                {
                    BtnRefreshQuota.Focus();
                    e.Handled = true;
                }
                else if (BtnRefreshQuota.IsFocused)
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
                else
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Right)
            {
                if (BtnClose.IsFocused)
                {
                    BtnRefreshQuota.Focus();
                    e.Handled = true;
                }
                else if (BtnRefreshQuota.IsFocused)
                {
                    BtnPowerSwitch.Focus();
                    e.Handled = true;
                }
                else if (BtnPowerSwitch.IsFocused)
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
                else
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Up)
            {
                if (BtnClose.IsFocused)
                {
                    BtnPowerSwitch.Focus();
                    e.Handled = true;
                }
                else if (BtnPowerSwitch.IsFocused || BtnRefreshQuota.IsFocused)
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
                else
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down)
            {
                if (BtnRefreshQuota.IsFocused || BtnPowerSwitch.IsFocused)
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
                else if (BtnClose.IsFocused)
                {
                    BtnRefreshQuota.Focus();
                    e.Handled = true;
                }
                else
                {
                    BtnClose.Focus();
                    e.Handled = true;
                }
            }
        }

        #region Window Caption Controls & Custom Chrome Events
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyModernWindowAppearance();
        }

        private void ApplyModernWindowAppearance()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                // 1. Kích hoạt native Windows 11 Rounded Corners (DWMWCP_ROUND = 2)
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

                // 2. Kích hoạt native transitions từ Windows GPU Compositor
                int forceTransitions = 0;
                DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref forceTransitions, sizeof(int));

                // 3. Kích hoạt native DWM Drop Shadow xung quanh cửa sổ
                MARGINS margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 1, cyBottomHeight = 0 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch { }
        }

        #region DWM Win32 Interop for Native Rounded Corners & Shadow
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2; // Bo tròn tiêu chuẩn Windows 11
        #endregion

        private void ModelQuotaWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                if (MainRootBorder != null)
                {
                    MainRootBorder.BorderThickness = new Thickness(0);
                    MainRootBorder.Padding = new Thickness(7);
                    MainRootBorder.CornerRadius = new CornerRadius(0);
                }
                if (MainHeaderBorder != null)
                {
                    MainHeaderBorder.CornerRadius = new CornerRadius(0);
                }
                if (PathWinMaximize != null) PathWinMaximize.Visibility = Visibility.Collapsed;
                if (PathWinRestore != null) PathWinRestore.Visibility = Visibility.Visible;
                if (BtnWinMaximize != null)
                {
                    BtnWinMaximize.ToolTip = LocalizationManager.IsEnglish ? "Restore Down" : "Khôi phục kích thước";
                }
            }
            else
            {
                if (MainRootBorder != null)
                {
                    MainRootBorder.BorderThickness = new Thickness(1);
                    MainRootBorder.Padding = new Thickness(0);
                    MainRootBorder.CornerRadius = new CornerRadius(10);
                }
                if (MainHeaderBorder != null)
                {
                    MainHeaderBorder.CornerRadius = new CornerRadius(9, 9, 0, 0);
                }
                if (PathWinMaximize != null) PathWinMaximize.Visibility = Visibility.Visible;
                if (PathWinRestore != null) PathWinRestore.Visibility = Visibility.Collapsed;
                if (BtnWinMaximize != null)
                {
                    BtnWinMaximize.ToolTip = LocalizationManager.IsEnglish ? "Maximize" : "Phóng to";
                }
            }
        }

        private void BtnWinMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnWinMaximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnWinClose_Click(object sender, RoutedEventArgs e)
        {
            AnimateWindowClose();
        }

        private bool _isClosing = false;

        private void AnimateWindowOpen()
        {
            if (MainRootBorder == null) return;
            try
            {
                // Bật BitmapCache tạm thời để GPU xử lý animation 120 FPS không tốn CPU
                MainRootBorder.CacheMode = new BitmapCache { SnapsToDevicePixels = true, EnableClearType = true };
                MainRootBorder.Opacity = 0.0;
                if (WindowTranslate != null) WindowTranslate.Y = 12.0;

                // Trì hoãn 1 tick Render để layout 20 progressbar hoàn tất trước khi animation chạy
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                {
                    var fadeAnim = new DoubleAnimation
                    {
                        From = 0.0,
                        To = 1.0,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    fadeAnim.Completed += (s, e) =>
                    {
                        // Khi animation kết thúc, gỡ bỏ CacheMode để văn bản giữ ClearType sắc nét tự nhiên
                        if (MainRootBorder != null)
                        {
                            MainRootBorder.CacheMode = null;
                        }
                    };

                    if (WindowTranslate != null)
                    {
                        var slideAnim = new DoubleAnimation
                        {
                            From = 12.0,
                            To = 0.0,
                            Duration = TimeSpan.FromMilliseconds(150),
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        WindowTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                    }

                    MainRootBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                }));
            }
            catch
            {
                MainRootBorder.Opacity = 1.0;
            }
        }

        private void AnimateWindowClose()
        {
            if (_isClosing) return;
            _isClosing = true;

            if (MainRootBorder == null)
            {
                this.Close();
                return;
            }

            try
            {
                MainRootBorder.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

                var fadeAnim = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(80),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeAnim.Completed += (s, e) =>
                {
                    this.Close();
                };

                if (WindowTranslate != null)
                {
                    var slideAnim = new DoubleAnimation
                    {
                        From = 0.0,
                        To = 6.0,
                        Duration = TimeSpan.FromMilliseconds(80),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };
                    WindowTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }

                MainRootBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            catch
            {
                this.Close();
            }
        }
        #endregion
    }
}
