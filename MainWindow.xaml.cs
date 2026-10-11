using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ProfileSwitchChrome.Models;
using ProfileSwitchChrome.Services;

namespace ProfileSwitchChrome
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly ProfileGuardService _guardService;
        private List<ChromeProfileInfo> _profiles = new();
        private ChromeProfileInfo? _currentDefault;
        private readonly DispatcherTimer _chromeStatusTimer;
        private readonly DispatcherTimer _toastDismissTimer;
        private readonly DispatcherTimer _profileSearchDebounceTimer;
        private readonly DispatcherTimer _antigravitySearchDebounceTimer;

        // Antigravity Account Switcher state
        private List<AntigravityAccountInfo> _antigravityAccounts = new();
        private AntigravityAccountInfo? _activeAntigravityAccount;
        private readonly DispatcherTimer _antigravityStatusTimer;
        private readonly DispatcherTimer _autoRefreshQuotaTimer;
        private DateTime? _lastAutoRefreshQuotaTime;
        private bool _isBatchMeasuring = false;
        private volatile bool _isSwitching = false;

        // Action callback for custom in-app modal confirmation
        private Action? _modalConfirmAction;

        // Antigravity Filter, Sort & Scope state (Matching Mockup UI)
        private string? _selectedPlanFilter = null;
        private string _currentSortCriteria = "total"; // "recent", "total", "claude", "pro3", "flash"
        private string _currentQuotaScope = "both";   // "both", "5h", "week"
        private bool _isSortAscending = false;        // false: Lớn ➔ Nhỏ (Giảm dần), true: Nhỏ ➔ Lớn (Tăng dần)
        private AntigravityAccountInfo? _recommendedAccount = null;
        private int _selectedAccountIndex = -1;
        private List<AntigravityAccountInfo> _currentFilteredAccounts = new();

        // Chrome Profiles Keyboard Navigation state
        private int _selectedProfileIndex = -1;
        private List<ChromeProfileInfo> _currentFilteredProfiles = new();

        public MainWindow()
        {
            void Log(string msg)
            {
                try
                {
                    System.IO.File.AppendAllText("launch_debug.log", $"[{DateTime.Now:HH:mm:ss.fff}] [Window] {msg}\r\n");
                }
                catch { }
            }

            Log("MainWindow constructor start");
            try
            {
                InitializeComponent();
                Log("InitializeComponent OK");
            }
            catch (Exception ex)
            {
                Log($"InitializeComponent ERROR: {ex}");
                throw;
            }

            try
            {
                _config = AppConfig.Load();
                Log("AppConfig loaded");
                LocalizationManager.SetLanguage(_config.Language);
                LocalizationManager.LanguageChanged += ApplyLanguage;
                this.StateChanged += MainWindow_StateChanged;
                _guardService = new ProfileGuardService(_config);
                Log("ProfileGuardService created");

                ChkShortcuts.IsChecked = _config.UpdateShortcutsOnSet;
                ChkRegistry.IsChecked = _config.UpdateRegistryOnSet;
                ChkGuard.IsChecked = _config.AutoProtectEnabled;
                ChkStartup.IsChecked = _config.StartWithWindows;
                GoogleOAuthAutoConsentService.AutoClickSignInEnabled = _config.AutoClickSignIn;
                UpdateSilentSyncUI();

                // Restore any Chrome windows that might have been shifted off-screen previously
                Task.Run(() => GoogleOAuthAutoConsentService.RestoreAnyHiddenChromeWindows());

                if (_config.AutoProtectEnabled)
                {
                    _guardService.Start();
                    Log("Guard service started");
                }
            }
            catch (Exception ex)
            {
                Log($"Config/Guard init error: {ex}");
            }

            _chromeStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _chromeStatusTimer.Tick += (s, e) => UpdateChromeRunningStatus();
            _chromeStatusTimer.Start();

            _antigravityStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.0) };
            _antigravityStatusTimer.Tick += (s, e) => UpdateAntigravityRunningStatus();
            _antigravityStatusTimer.Start();

            _autoRefreshQuotaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoRefreshQuotaTimer.Tick += AutoRefreshQuotaTimer_Tick;
            if (_config.AutoRefreshAllQuotaEnabled)
            {
                _autoRefreshQuotaTimer.Start();
            }

            _toastDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _toastDismissTimer.Tick += (s, e) =>
            {
                DismissToast();
            };

            _profileSearchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
            _profileSearchDebounceTimer.Tick += (s, e) =>
            {
                _profileSearchDebounceTimer.Stop();
                ApplyProfileFilter();
            };

            _antigravitySearchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
            _antigravitySearchDebounceTimer.Tick += (s, e) =>
            {
                _antigravitySearchDebounceTimer.Stop();
                FilterAntigravityAccountsList();
            };

            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            this.Loaded += (s, e) =>
            {
                Log("Loaded event fired");
                try
                {
                    double workAreaHeight = SystemParameters.WorkArea.Height;
                    double workAreaWidth = SystemParameters.WorkArea.Width;

                    // Allow native full-screen maximizing without clipping or gaps
                    this.MaxHeight = double.PositiveInfinity;
                    this.MaxWidth = double.PositiveInfinity;

                    if (this.WindowState == WindowState.Normal)
                    {
                        if (this.Height > workAreaHeight - 20)
                        {
                            this.Height = Math.Max(520, workAreaHeight - 36);
                        }
                        if (this.Width > workAreaWidth - 20)
                        {
                            this.Width = Math.Max(820, workAreaWidth - 36);
                        }

                        this.Left = SystemParameters.WorkArea.Left + Math.Max(0, (workAreaWidth - this.ActualWidth) / 2);
                        this.Top = SystemParameters.WorkArea.Top + Math.Max(0, (workAreaHeight - this.ActualHeight) / 2);
                    }

                    Log($"WorkArea calculated: Left={this.Left}, Top={this.Top}, W={this.Width}, H={this.Height}, State={this.WindowState}");

                    UpdateThemeButtonUI();
                    UpdateScopeButtonsUI();
                    UpdateSortDirectionUI();
                    UpdateBatchConcurrencyUI();
                    UpdateAutoRefreshQuotaUI();
                    UpdatePrivacyUI();
                    UpdateChromePrivacyUI();
                    ApplyLanguage();
                    Log("UI controls updated");

                    // Load profiles and accounts deferred so window renders immediately without waiting for disk I/O
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        try
                        {
                            ReloadProfiles();
                            Log("ReloadProfiles completed");
                            UpdateChromeRunningStatus();

                            ReloadAntigravityAccounts();
                            Log("ReloadAntigravityAccounts completed");
                            UpdateAntigravityRunningStatus();
                            Log("Deferred background load finished successfully");
                        }
                        catch (Exception ex)
                        {
                            Log($"Deferred load ERROR: {ex}");
                        }
                    }));

                    Log("Loaded event finished (window ready to paint)");
                }
                catch (Exception ex)
                {
                    Log($"Loaded event ERROR: {ex}");
                }
            };

            this.ContentRendered += (s, e) =>
            {
                Log($"ContentRendered fired! Window is visible. Left={this.Left}, Top={this.Top}, Visibility={this.Visibility}, WindowState={this.WindowState}");
                try
                {
                    this.Activate();
                    this.Focus();
                    WarmupAboutModal();
                }
                catch { }
            };

            this.Closed += (s, e) =>
            {
                _chromeStatusTimer.Stop();
                _antigravityStatusTimer.Stop();
                _toastDismissTimer.Stop();
                _guardService.Dispose();
            };
        }

        private void UpdateChromeRunningStatus()
        {
            var (procCount, totalMemory) = ChromeProfileManager.GetChromeProcessStats();
            var activeProfiles = ChromeProfileManager.GetActiveProfiles();
            bool isDark = ThemeManager.IsDark;

            if (procCount > 0)
            {
                string memStr = ChromeProfileInfo.FormatBytes(totalMemory);
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                TxtChromeStatus.Text = $"Chrome ({procCount} proc) • {memStr}";
                TxtChromeStatus.Foreground = isDark 
                    ? new SolidColorBrush(Color.FromRgb(252, 211, 77)) 
                    : new SolidColorBrush(Color.FromRgb(146, 64, 14));
                BorderChromeStatus.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(45, 22, 9)) 
                    : new SolidColorBrush(Color.FromRgb(254, 243, 199));
                BorderChromeStatus.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) 
                    : new SolidColorBrush(Color.FromRgb(253, 230, 138));
                BtnCloseChrome.Visibility = Visibility.Visible;
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                TxtChromeStatus.Text = LocalizationManager.T("ChromeClosed");
                TxtChromeStatus.Foreground = isDark 
                    ? new SolidColorBrush(Color.FromRgb(110, 231, 183)) 
                    : new SolidColorBrush(Color.FromRgb(6, 95, 70));
                BorderChromeStatus.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(9, 36, 27)) 
                    : new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BorderChromeStatus.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(5, 150, 105)) 
                    : new SolidColorBrush(Color.FromRgb(167, 243, 208));
                BtnCloseChrome.Visibility = Visibility.Collapsed;
            }

            // Distribute RAM among active profiles
            long ramPerActive = activeProfiles.Count > 0 ? (totalMemory / activeProfiles.Count) : 0;
            bool anyChanged = false;

            foreach (var p in _profiles)
            {
                bool isNowRunning = activeProfiles.Contains(p.Id);
                long mem = isNowRunning ? ramPerActive : 0;

                if (p.IsRunning != isNowRunning || p.MemoryBytes != mem)
                {
                    p.IsRunning = isNowRunning;
                    p.MemoryBytes = mem;
                    anyChanged = true;
                }
            }

            // Update Hero Banner running status
            if (_currentDefault != null)
            {
                bool isDefaultRunning = activeProfiles.Contains(_currentDefault.Id);
                _currentDefault.IsRunning = isDefaultRunning;
                _currentDefault.MemoryBytes = isDefaultRunning ? ramPerActive : 0;

                if (isDefaultRunning)
                {
                    BorderHeroRunningBadge.Visibility = Visibility.Visible;
                    TxtHeroRunningBadge.Text = _currentDefault.RunningBadgeText;
                }
                else
                {
                    BorderHeroRunningBadge.Visibility = Visibility.Collapsed;
                }
            }

            if (anyChanged)
            {
                ProfilesItemsControl.Items.Refresh();
            }
        }

        private void ReloadProfiles()
        {
            _profiles = ChromeProfileManager.LoadProfiles();

            // Detect current default
            string savedDefault = _config.DefaultProfileId;
            _currentDefault = _profiles.FirstOrDefault(p => p.IsDefault);

            if (_currentDefault == null && !string.IsNullOrEmpty(savedDefault))
            {
                _currentDefault = _profiles.FirstOrDefault(p => p.Id == savedDefault);
            }

            if (_currentDefault == null && _profiles.Count > 0)
            {
                _currentDefault = _profiles[0];
            }

            if (_currentDefault != null)
            {
                foreach (var p in _profiles)
                {
                    p.IsDefault = (p.Id == _currentDefault.Id);
                    p.IsNameHidden = _config.HideChromeNames;
                    p.IsEmailHidden = _config.HideChromeEmails;
                }

                _currentDefault.IsNameHidden = _config.HideChromeNames;
                _currentDefault.IsEmailHidden = _config.HideChromeEmails;

                // Update Hero Banner
                UpdateHeroDefaultProfileDisplay(_currentDefault);

                _config.DefaultProfileId = _currentDefault.Id;
                _config.Save();
            }

            _lastProfileSearchKeyword = null;
            ApplyProfileFilter(force: true);
            UpdateChromePrivacyUI();
        }

        private void UpdateHeroDefaultProfileDisplay(ChromeProfileInfo? profile = null)
        {
            var p = profile ?? _currentDefault;
            if (p == null || TxtHeroName == null) return;

            TxtHeroName.Text = p.NameMasked;
            if (TxtHeroTag != null) TxtHeroTag.Text = $"[{p.Id}]";
            if (TxtHeroEmail != null) TxtHeroEmail.Text = p.UserNameMasked;
            if (TxtHeroInitial != null) TxtHeroInitial.Text = p.InitialLetter;
            if (ImgHeroAvatar != null) ImgHeroAvatar.Source = p.AvatarSource;

            if (BorderHeroAccountsBadge != null)
            {
                if (p.GoogleAccounts.Count > 0)
                {
                    BorderHeroAccountsBadge.Visibility = Visibility.Visible;
                    if (TxtHeroAccountsBadge != null) TxtHeroAccountsBadge.Text = p.AccountsBadgeText;
                }
                else
                {
                    BorderHeroAccountsBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private string? _lastProfileSearchKeyword = null;

        private void ApplyProfileFilter(bool force = false)
        {
            string keyword = (TxtSearchProfile?.Text ?? "").Trim();
            if (!force && _lastProfileSearchKeyword != null && string.Equals(keyword, _lastProfileSearchKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _lastProfileSearchKeyword = keyword;

            List<ChromeProfileInfo> filtered;
            if (string.IsNullOrEmpty(keyword))
            {
                filtered = _profiles;
                if (BtnClearSearch != null) BtnClearSearch.Visibility = Visibility.Collapsed;
            }
            else
            {
                filtered = _profiles.Where(p =>
                    (p.Name != null && p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                    (p.UserName != null && p.UserName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                    (p.Id != null && p.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                    (p.GoogleAccounts != null && p.GoogleAccounts.Any(a => 
                        (a.Email != null && a.Email.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                        (a.FullName != null && a.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    ))
                ).ToList();
                if (BtnClearSearch != null) BtnClearSearch.Visibility = Visibility.Visible;
            }

            if (TxtProfileCount != null)
            {
                TxtProfileCount.Text = $" ({filtered.Count}/{_profiles.Count})";
            }
            _currentFilteredProfiles = filtered;

            if (_selectedProfileIndex >= filtered.Count)
            {
                _selectedProfileIndex = filtered.Count - 1;
            }

            for (int i = 0; i < filtered.Count; i++)
            {
                filtered[i].IsKeyboardSelected = (i == _selectedProfileIndex);
            }

            if (!ReferenceEquals(ProfilesItemsControl.ItemsSource, filtered))
            {
                ProfilesItemsControl.ItemsSource = filtered;
            }
            else
            {
                ProfilesItemsControl.Items.Refresh();
            }

            if (BorderEmptySearch != null)
            {
                BorderEmptySearch.Visibility = (filtered.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void TxtSearchProfile_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = TxtSearchProfile.Text ?? "";
            if (BtnClearSearch != null)
            {
                BtnClearSearch.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            _profileSearchDebounceTimer.Stop();
            if (string.IsNullOrEmpty(text))
            {
                ApplyProfileFilter(force: true);
            }
            else
            {
                _profileSearchDebounceTimer.Start();
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn)
            {
                btn.ReleaseMouseCapture();
            }
            Mouse.Capture(null);

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                TxtSearchProfile.Text = string.Empty;
                TxtSearchProfile.Focus();
            }));
        }

        private void BtnSetDefault_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not ChromeProfileInfo profile)
                return;

            // Apply default without launching or killing Chrome
            bool ok = ChromeProfileManager.SetDefaultProfile(
                profile.Id,
                ChkShortcuts.IsChecked == true,
                ChkRegistry.IsChecked == true,
                out string msg);

            if (ok)
            {
                _config.DefaultProfileId = profile.Id;
                _config.Save();

                ReloadProfiles();

                ShowToast(LocalizationManager.IsEnglish ? $"✓ Set '{profile.Name}' as default successfully!" : $"✓ Đã đặt '{profile.Name}' làm mặc định thành công!");
            }
            else
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Configuration Error" : "Lỗi thiết lập", LocalizationManager.IsEnglish ? "An error occurred: " + msg : "Có lỗi xảy ra: " + msg, isError: true);
            }
        }

        private void BtnHeroLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDefault != null)
            {
                ChromeProfileManager.LaunchChrome(_currentDefault.Id);
                ShowToast(LocalizationManager.IsEnglish ? $"Opening Chrome with profile '{_currentDefault.Name}'..." : $"Đang mở Chrome với profile '{_currentDefault.Name}'...");
            }
        }

        private void BtnLaunchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not ChromeProfileInfo profile)
                return;

            ChromeProfileManager.LaunchChrome(profile.Id);
            ShowToast(LocalizationManager.IsEnglish ? $"Opening Chrome with profile '{profile.Name}'..." : $"Đang mở Chrome với profile '{profile.Name}'...");
        }

        private void BtnCreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not ChromeProfileInfo profile)
                return;

            bool ok = ChromeProfileManager.CreateDedicatedShortcut(profile.Id, profile.Name);
            if (ok)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"✓ Created Desktop shortcut 'Chrome - {profile.Name}'!" : $"✓ Đã tạo phím tắt 'Chrome - {profile.Name}' ra màn hình Desktop!");
            }
            else
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Shortcut Creation Error" : "Lỗi tạo phím tắt", LocalizationManager.IsEnglish ? "Cannot create Desktop shortcut. Please check Administrator privileges." : "Không thể tạo phím tắt ra Desktop. Vui lòng kiểm tra quyền Administrator.", isError: true);
            }
        }

        private void BtnCloseChrome_Click(object sender, RoutedEventArgs e)
        {
            int count = ChromeProfileManager.GetChromeProcessCount();
            string msg = LocalizationManager.IsEnglish
                ? $"There are {count} Chrome processes currently running.\n\nDo you want to close all Chrome instances to thoroughly apply the default profile and shortcut settings?"
                : $"Đang có {count} tiến trình Chrome đang hoạt động trên máy.\n\nBạn có muốn đóng tất cả Chrome ngay để cấu hình Profile mặc định và phím tắt được áp dụng triệt để không?";

            ShowConfirmModal(
                title: LocalizationManager.IsEnglish ? "Confirm Close Chrome" : "Xác nhận đóng Chrome",
                message: msg,
                confirmText: LocalizationManager.IsEnglish ? "🛑 Close All Chrome" : "🛑 Đóng tất cả Chrome",
                isDanger: true,
                onConfirm: () =>
                {
                    ChromeProfileManager.KillChromeProcesses();
                    System.Threading.Thread.Sleep(500);
                    UpdateChromeRunningStatus();
                    ReloadProfiles();
                    ShowToast(LocalizationManager.IsEnglish ? "✓ Successfully closed all Chrome processes." : "✓ Đã đóng tất cả tiến trình Chrome thành công.");
                });
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            ReloadProfiles();
            UpdateChromeRunningStatus();
            ShowToast(LocalizationManager.IsEnglish ? "✓ Profile list refreshed." : "✓ Đã làm mới danh sách Profile.");
        }

        private void ShowToast(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            string cleanMessage = message.Trim();

            // Prevent duplicate checkmarks if any caller concatenated them
            while (cleanMessage.StartsWith("✓ ✓") || cleanMessage.StartsWith("✓  ✓") || cleanMessage.StartsWith("✔ ✔"))
            {
                int nextIdx = cleanMessage.IndexOfAny(new[] { '✓', '✔' }, 1);
                if (nextIdx > 0)
                    cleanMessage = cleanMessage.Substring(nextIdx).Trim();
                else
                    break;
            }

            if (TxtToastMessage != null) TxtToastMessage.Text = cleanMessage;

            if (ToastBanner != null)
            {
                ToastBanner.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));
                ToastBanner.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }

            _toastDismissTimer.Stop();
            _toastDismissTimer.Start();
        }

        private void BtnCloseToast_Click(object sender, RoutedEventArgs e)
        {
            DismissToast();
        }

        private void DismissToast()
        {
            _toastDismissTimer.Stop();
            if (ToastBanner != null && ToastBanner.Visibility == Visibility.Visible)
            {
                var fadeOut = new DoubleAnimation(ToastBanner.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (s, e) =>
                {
                    ToastBanner.Visibility = Visibility.Collapsed;
                };
                ToastBanner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;

            _config.UpdateShortcutsOnSet = ChkShortcuts.IsChecked == true;
            _config.UpdateRegistryOnSet = ChkRegistry.IsChecked == true;
            _config.AutoProtectEnabled = ChkGuard.IsChecked == true;
            _config.StartWithWindows = ChkStartup.IsChecked == true;
            GoogleOAuthAutoConsentService.AutoClickSignInEnabled = _config.AutoClickSignIn;
            _floatingBar?.SetAutoClickState(_config.AutoClickSignIn);
            _config.Save();
            UpdateSilentSyncUI();

            if (_config.AutoProtectEnabled)
            {
                _guardService.Start();
            }
            else
            {
                _guardService.Stop();
            }
        }

        private void BtnToggleSilentSync_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            _config.AutoClickSignIn = !_config.AutoClickSignIn;
            GoogleOAuthAutoConsentService.AutoClickSignInEnabled = _config.AutoClickSignIn;
            _config.Save();
            _floatingBar?.SetAutoClickState(_config.AutoClickSignIn);
            UpdateSilentSyncUI();
            ShowToast(_config.AutoClickSignIn 
                ? (LocalizationManager.IsEnglish ? "⚡ Enabled Auto-Click Sign in" : "⚡ Đã BẬT Tự động bấm Đăng nhập (Auto-Click Sign in)")
                : (LocalizationManager.IsEnglish ? "ℹ️ Disabled Auto-Click (Manual confirmation)" : "ℹ️ Đã TẮT Tự động bấm (Xác nhận thủ công)"));
        }

        private void UpdateSilentSyncUI()
        {
            if (_config == null) return;
            if (TxtSilentSyncLabel != null)
            {
                TxtSilentSyncLabel.Text = _config.AutoClickSignIn 
                    ? (LocalizationManager.IsEnglish ? "Auto-Click: ON" : "Tự bấm Sign-in: Bật")
                    : (LocalizationManager.IsEnglish ? "Auto-Click: OFF" : "Tự bấm Sign-in: Tắt");
            }
            if (TxtSilentSyncIcon != null)
            {
                TxtSilentSyncIcon.Text = _config.AutoClickSignIn ? "⚡" : "🖱️";
            }
        }

        // =====================================================================
        // CUSTOM IN-APP MODAL METHODS (Replaces native white Win32 MessageBox)
        // =====================================================================
        private void ShowConfirmModal(string title, string message, string confirmText, Action onConfirm, bool isDanger = true)
        {
            TxtModalTitle.Text = title;
            TxtModalSubtitle.Text = isDanger 
                ? (LocalizationManager.IsEnglish ? "Action requires confirmation" : "Hành động yêu cầu xác nhận")
                : (LocalizationManager.IsEnglish ? "Safe configuration sync" : "Đồng bộ an toàn cấu hình");
            TxtModalMessage.Text = message;
            BtnModalConfirm.Content = confirmText;
            _modalConfirmAction = onConfirm;

            BorderModalCountBadge.Visibility = Visibility.Collapsed;
            PanelBatchSyncDetails.Visibility = Visibility.Collapsed;
            PanelNormalMessage.Visibility = Visibility.Visible;

            bool isDark = ThemeManager.IsDark;
            if (isDanger)
            {
                TxtModalIcon.Text = "🛑";
                ModalIconBorder.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(69, 10, 10)) 
                    : new SolidColorBrush(Color.FromRgb(254, 226, 226));
                ModalIconBorder.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(153, 27, 27)) 
                    : new SolidColorBrush(Color.FromRgb(252, 165, 165));
                BtnModalConfirm.Style = (Style)FindResource("DangerBtn");
            }
            else
            {
                TxtModalIcon.Text = "💡";
                ModalIconBorder.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(12, 74, 110)) 
                    : new SolidColorBrush(Color.FromRgb(224, 242, 254));
                ModalIconBorder.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(2, 132, 199)) 
                    : new SolidColorBrush(Color.FromRgb(125, 211, 252));
                BtnModalConfirm.Style = (Style)FindResource("PrimaryGreenBtn");
            }

            BtnModalCancel.Visibility = Visibility.Visible;
            CustomModalOverlay.Visibility = Visibility.Visible;
        }

        private void ShowAlertModal(string title, string message, bool isError = false)
        {
            TxtModalTitle.Text = title;
            TxtModalSubtitle.Text = isError 
                ? (LocalizationManager.IsEnglish ? "System notice" : "Thông báo hệ thống")
                : (LocalizationManager.IsEnglish ? "Information" : "Thông tin chi tiết");
            TxtModalMessage.Text = message;
            BtnModalConfirm.Content = LocalizationManager.IsEnglish ? "Understood" : "Đã hiểu";
            _modalConfirmAction = null;

            BorderModalCountBadge.Visibility = Visibility.Collapsed;
            PanelBatchSyncDetails.Visibility = Visibility.Collapsed;
            PanelNormalMessage.Visibility = Visibility.Visible;

            bool isDark = ThemeManager.IsDark;
            if (isError)
            {
                TxtModalIcon.Text = "⚠️";
                ModalIconBorder.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(69, 10, 10)) 
                    : new SolidColorBrush(Color.FromRgb(254, 226, 226));
                ModalIconBorder.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(153, 27, 27)) 
                    : new SolidColorBrush(Color.FromRgb(252, 165, 165));
                BtnModalConfirm.Style = (Style)FindResource("DangerBtn");
            }
            else
            {
                TxtModalIcon.Text = "ℹ️";
                ModalIconBorder.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(12, 74, 110)) 
                    : new SolidColorBrush(Color.FromRgb(224, 242, 254));
                ModalIconBorder.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(2, 132, 199)) 
                    : new SolidColorBrush(Color.FromRgb(125, 211, 252));
                BtnModalConfirm.Style = (Style)FindResource("SecondaryBtn");
            }

            BtnModalCancel.Visibility = Visibility.Collapsed;
            CustomModalOverlay.Visibility = Visibility.Visible;
        }

        private void ShowBatchSyncConfirmModal(List<GoogleAccountInfo> accounts, Action onConfirm)
        {
            TxtModalTitle.Text = LocalizationManager.IsEnglish ? "Batch Sync into Antigravity" : "Đồng bộ hàng loạt vào Antigravity";
            TxtModalSubtitle.Text = LocalizationManager.IsEnglish ? "Securely sync accounts into IDE config" : "Chuyển token an toàn vào cấu hình IDE";

            BorderModalCountBadge.Visibility = Visibility.Visible;
            TxtModalCountBadge.Text = LocalizationManager.IsEnglish ? $"{accounts.Count} accounts" : $"{accounts.Count} tài khoản";

            TxtModalIcon.Text = "⚡";
            bool isDark = ThemeManager.IsDark;
            ModalIconBorder.Background = isDark 
                ? new SolidColorBrush(Color.FromRgb(12, 74, 110)) 
                : new SolidColorBrush(Color.FromRgb(224, 242, 254));
            ModalIconBorder.BorderBrush = isDark 
                ? new SolidColorBrush(Color.FromRgb(2, 132, 199)) 
                : new SolidColorBrush(Color.FromRgb(125, 211, 252));

            PanelNormalMessage.Visibility = Visibility.Collapsed;
            PanelBatchSyncDetails.Visibility = Visibility.Visible;

            RenderBatchSyncPreviewAccounts(accounts);

            if (ChkAutoClickInModal != null)
            {
                ChkAutoClickInModal.IsChecked = _config?.AutoClickSignIn == true;
            }

            BtnModalCancel.Visibility = Visibility.Visible;
            BtnModalConfirm.Content = LocalizationManager.IsEnglish 
                ? $"Start Sync ({accounts.Count})" 
                : $"Bắt đầu đồng bộ ({accounts.Count})";
            BtnModalConfirm.Style = (Style)FindResource("PrimaryGreenBtn");

            _modalConfirmAction = onConfirm;
            CustomModalOverlay.Visibility = Visibility.Visible;
        }

        private void RenderBatchSyncPreviewAccounts(List<GoogleAccountInfo> accounts)
        {
            if (PanelBatchSyncAccountItems == null) return;
            PanelBatchSyncAccountItems.Children.Clear();
            var preview = accounts.Take(4).ToList();
            string[] avatarColors = new[] { "#0284C7", "#7C3AED", "#059669", "#D97706", "#DC2626", "#4F46E5" };

            for (int i = 0; i < preview.Count; i++)
            {
                var acc = preview[i];
                string initial = !string.IsNullOrWhiteSpace(acc.Email) ? acc.Email[0].ToString().ToUpper() : "G";
                string color = avatarColors[i % avatarColors.Length];

                var border = new Border
                {
                    Background = (Brush)FindResource("ThemeCardBg"),
                    BorderBrush = (Brush)FindResource("ThemeCardBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var avatarBorder = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var avatarText = new TextBlock
                {
                    Text = initial,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                avatarBorder.Child = avatarText;
                Grid.SetColumn(avatarBorder, 0);
                grid.Children.Add(avatarBorder);

                var emailText = new TextBlock
                {
                    Text = acc.EmailMasked ?? acc.Email,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                emailText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
                Grid.SetColumn(emailText, 1);
                grid.Children.Add(emailText);

                var badgeBorder = new Border
                {
                    Background = (Brush)FindResource("ThemeActiveStatusBadgeBg"),
                    BorderBrush = (Brush)FindResource("ThemeActiveStatusBadgeBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var badgeText = new TextBlock
                {
                    Text = LocalizationManager.IsEnglish ? "Ready" : "Sẵn sàng",
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold
                };
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeActiveStatusBadgeFg");
                badgeBorder.Child = badgeText;
                Grid.SetColumn(badgeBorder, 2);
                grid.Children.Add(badgeBorder);

                border.Child = grid;
                PanelBatchSyncAccountItems.Children.Add(border);
            }

            if (accounts.Count > 4)
            {
                TxtBatchSyncMoreAccounts.Visibility = Visibility.Visible;
                TxtBatchSyncMoreAccounts.Text = LocalizationManager.IsEnglish
                    ? $"+ and {accounts.Count - 4} other accounts in this profile"
                    : $"+ và {accounts.Count - 4} tài khoản khác trong danh sách này";
            }
            else
            {
                TxtBatchSyncMoreAccounts.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkAutoClickInModal_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            bool isChecked = ChkAutoClickInModal.IsChecked == true;
            _config.AutoClickSignIn = isChecked;
            GoogleOAuthAutoConsentService.AutoClickSignInEnabled = isChecked;
            _config.Save();
            _floatingBar?.SetAutoClickState(isChecked);
            UpdateSilentSyncUI();
            ShowToast(isChecked 
                ? (LocalizationManager.IsEnglish ? "⚡ Enabled Auto-Click Sign in" : "⚡ Đã BẬT Tự động bấm Đăng nhập (Auto-Click Sign in)")
                : (LocalizationManager.IsEnglish ? "ℹ️ Disabled Auto-Click (Manual confirmation)" : "ℹ️ Đã TẮT Tự động bấm (Xác nhận thủ công)"));
        }

        private void BtnModalConfirm_Click(object sender, RoutedEventArgs e)
        {
            CustomModalOverlay.Visibility = Visibility.Collapsed;
            _modalConfirmAction?.Invoke();
            _modalConfirmAction = null;
        }

        private void BtnModalCancel_Click(object sender, RoutedEventArgs e)
        {
            CustomModalOverlay.Visibility = Visibility.Collapsed;
            _modalConfirmAction = null;
        }

        private void ModalBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Close modal if user clicks outside
            CustomModalOverlay.Visibility = Visibility.Collapsed;
            _modalConfirmAction = null;
        }

        // =====================================================================
        // ABOUT & AUTHOR MODAL HANDLERS
        // =====================================================================
        private void WarmupAboutModal()
        {
            if (AboutModalOverlay == null || AboutModalCard == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    // Render ngầm 1 frame ở Opacity = 0 để biên dịch Shader DropShadow & visual tree trước
                    AboutModalOverlay.Opacity = 0.0;
                    AboutModalOverlay.Visibility = Visibility.Visible;
                    AboutModalOverlay.UpdateLayout();
                    AboutModalOverlay.Visibility = Visibility.Collapsed;
                    AboutModalOverlay.Opacity = 1.0;
                }
                catch { }
            }));
        }

        private void ShowAboutModal()
        {
            if (AboutModalOverlay == null || AboutModalCard == null) return;

            // Huỷ các animation cũ nếu đang chạy dở
            AboutModalBackdrop?.BeginAnimation(UIElement.OpacityProperty, null);
            AboutModalCard.BeginAnimation(UIElement.OpacityProperty, null);
            AboutModalTranslate?.BeginAnimation(TranslateTransform.YProperty, null);

            // Thiết lập trạng thái bắt đầu ở Opacity = 0 trước khi hiển thị
            if (AboutModalBackdrop != null) AboutModalBackdrop.Opacity = 0.0;
            AboutModalCard.Opacity = 0.0;
            if (AboutModalTranslate != null) AboutModalTranslate.Y = 14.0;

            AboutModalOverlay.Opacity = 1.0;
            AboutModalOverlay.Visibility = Visibility.Visible;

            // Bật GPU BitmapCache tạm thời để chuyển động đạt 120 FPS không gián đoạn
            AboutModalCard.CacheMode = new BitmapCache { SnapsToDevicePixels = true, EnableClearType = true };

            // Trì hoãn timeline animation 1 tick (Render priority) để đảm bảo layout pass và GPU shaders đã hoàn tất 100%
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (AboutModalOverlay.Visibility != Visibility.Visible) return;

                var backdropFade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                AboutModalBackdrop?.BeginAnimation(UIElement.OpacityProperty, backdropFade);

                var cardFade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                cardFade.Completed += (s, e) =>
                {
                    // Sau khi kết thúc animation, bỏ CacheMode để giữ text ClearType sắc nét hoàn hảo
                    if (AboutModalCard != null)
                    {
                        AboutModalCard.CacheMode = null;
                    }
                };
                AboutModalCard.BeginAnimation(UIElement.OpacityProperty, cardFade);

                if (AboutModalTranslate != null)
                {
                    var slideAnim = new DoubleAnimation
                    {
                        From = 14.0,
                        To = 0.0,
                        Duration = TimeSpan.FromMilliseconds(160),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    AboutModalTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
            }));
        }

        private void HideAboutModal()
        {
            if (AboutModalOverlay == null || AboutModalOverlay.Visibility != Visibility.Visible || AboutModalCard == null) return;

            // Bật CacheMode khi đóng để GPU xử lý tức thì không tốn CPU
            AboutModalCard.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

            var backdropFade = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(90),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            AboutModalBackdrop?.BeginAnimation(UIElement.OpacityProperty, backdropFade);

            var cardFade = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(90),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            cardFade.Completed += (s, e) =>
            {
                AboutModalOverlay.Visibility = Visibility.Collapsed;
                if (AboutModalCard != null)
                {
                    AboutModalCard.CacheMode = null;
                }
            };
            AboutModalCard.BeginAnimation(UIElement.OpacityProperty, cardFade);

            if (AboutModalTranslate != null)
            {
                var slideAnim = new DoubleAnimation
                {
                    From = 0.0,
                    To = 8.0,
                    Duration = TimeSpan.FromMilliseconds(90),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                AboutModalTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            }
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            ShowAboutModal();
        }

        private void BtnCloseAboutModal_Click(object sender, RoutedEventArgs e)
        {
            HideAboutModal();
        }

        private void AboutModalBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            HideAboutModal();
        }

        private void BtnOpenGithub_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/wiksgods");
        }

        private void BtnCopyGithub_Click(object sender, RoutedEventArgs e)
        {
            CopyTextToClipboard("https://github.com/wiksgods", LocalizationManager.IsEnglish ? "✓ Copied GitHub link!" : "✓ Đã sao chép liên kết GitHub!");
        }

        private void BtnOpenTelegram_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://t.me/DvenxyWiks");
        }

        private void BtnCopyTelegram_Click(object sender, RoutedEventArgs e)
        {
            CopyTextToClipboard("@DvenxyWiks", LocalizationManager.IsEnglish ? "✓ Copied Telegram handle!" : "✓ Đã sao chép Telegram: @DvenxyWiks!");
        }

        private void BtnCopyEmail_Click(object sender, RoutedEventArgs e)
        {
            CopyTextToClipboard("phungtoan2006@gmail.com", LocalizationManager.IsEnglish ? "✓ Copied email address!" : "✓ Đã sao chép địa chỉ email!");
        }

        private void OpenExternalUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"Could not open link: {ex.Message}" : $"Không thể mở liên kết: {ex.Message}");
            }
        }

        private void CopyTextToClipboard(string text, string toastSuccess)
        {
            try
            {
                Clipboard.SetText(text);
                ShowToast(toastSuccess);
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"Copy failed: {ex.Message}" : $"Lỗi sao chép: {ex.Message}");
            }
        }

        // =====================================================================
        // GOOGLE ACCOUNTS MODAL HANDLERS
        // =====================================================================
        private void BtnViewAccounts_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ChromeProfileInfo profile)
            {
                OpenAccountsModal(profile);
            }
        }

        private void BtnHeroViewAccounts_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentDefault != null)
            {
                OpenAccountsModal(_currentDefault);
            }
        }

        private ChromeProfileInfo? _currentViewingAccountsProfile;

        private void OpenAccountsModal(ChromeProfileInfo profile)
        {
            _currentViewingAccountsProfile = profile;
            TxtAccountsModalTitle.Text = LocalizationManager.IsEnglish ? "Google Accounts in Profile" : "Tài khoản Google trong Profile";
            TxtAccountsModalProfileName.Text = $"Profile: {profile.NameMasked} ({profile.Id})";
            TxtAccountsModalCount.Text = profile.GoogleAccountCount.ToString();
            BtnCloseAccountsModalTop.ToolTip = LocalizationManager.IsEnglish ? "Close" : "Đóng";
            BtnBatchSyncAllInModal.Content = LocalizationManager.IsEnglish ? "⚡ Sync All to Antigravity" : "⚡ Đồng bộ tất cả vào Antigravity";
            BtnBatchSyncAllInModal.ToolTip = LocalizationManager.IsEnglish 
                ? "Sync all Google accounts in this Profile to Antigravity (confirm in browser, tab closes automatically when done)"
                : "Đồng bộ toàn bộ tài khoản Google trong Profile này vào Antigravity (bấm xác nhận trên trình duyệt, tab tự đóng khi hoàn tất)";
            TxtCancelSyncInModalText.Text = LocalizationManager.IsEnglish ? "Stop Sync" : "Dừng đồng bộ";
            BtnCancelSyncInModal.ToolTip = LocalizationManager.IsEnglish ? "Immediately stop the batch sync process" : "Dừng ngay lập tức toàn bộ quá trình đồng bộ đang chạy";
            TxtCancelSyncBannerText.Text = LocalizationManager.IsEnglish ? "Stop Sync" : "Dừng đồng bộ";
            BtnCancelSyncBanner.ToolTip = LocalizationManager.IsEnglish ? "Immediately stop the batch sync process" : "Dừng ngay lập tức toàn bộ quá trình đồng bộ hàng loạt";
            BtnCloseAccountsModalBottom.Content = LocalizationManager.IsEnglish ? "Close" : "Đóng";

            // Refresh whether each account already exists in Antigravity
            var existingEmails = new HashSet<string>(
                _antigravityAccounts.Select(a => a.Email ?? "").Where(e => !string.IsNullOrEmpty(e)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var acc in profile.GoogleAccounts)
            {
                acc.ProfileId = profile.Id;
                acc.IsAlreadyInAntigravity = existingEmails.Contains(acc.Email);
                acc.IsNameHidden = _config.HideChromeNames;
                acc.IsEmailHidden = _config.HideChromeEmails;
                acc.NotifyLanguageChanged();
            }

            AccountsItemsControl.ItemsSource = null;
            AccountsItemsControl.ItemsSource = profile.GoogleAccounts;
            AccountsModalOverlay.Visibility = Visibility.Visible;
        }

        private void TxtGoogleAccountName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement elem && elem.DataContext is GoogleAccountInfo acc)
            {
                acc.IsNameHidden = !acc.IsNameHidden;
                e.Handled = true;
            }
        }

        private void TxtGoogleAccountEmail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement elem && elem.DataContext is GoogleAccountInfo acc)
            {
                acc.IsEmailHidden = !acc.IsEmailHidden;
                e.Handled = true;
            }
        }

        private void TxtGoogleAccountEmail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is GoogleAccountInfo acc && !string.IsNullOrEmpty(acc.Email))
            {
                try
                {
                    Clipboard.SetText(acc.Email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {acc.Email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void BtnCloseAccountsModal_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchSyncing)
            {
                StopBatchSync(LocalizationManager.IsEnglish ? "Batch sync stopped." : "Đã dừng đồng bộ hàng loạt.");
            }
            AccountsModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void AccountsModalBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isBatchSyncing)
            {
                StopBatchSync(LocalizationManager.IsEnglish ? "Batch sync stopped." : "Đã dừng đồng bộ hàng loạt.");
            }
            AccountsModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnSyncAccountToAntigravity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is GoogleAccountInfo acc)
            {
                if (string.IsNullOrWhiteSpace(acc.Email))
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Email address not found for this account." : "Không tìm thấy địa chỉ email của tài khoản này.");
                    return;
                }

                if (_oauthService != null)
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Stopping previous sync session to switch to new account..." : "Đang dừng phiên đồng bộ cũ để chuyển sang tài khoản mới...");
                    try
                    {
                        _oauthCts?.Cancel();
                        _oauthService?.Cancel();
                        if (GoogleOAuthAutoConsentService.LastOAuthHwnd != IntPtr.Zero)
                        {
                            GoogleOAuthAutoConsentService.CloseOAuthTab(GoogleOAuthAutoConsentService.LastOAuthHwnd);
                        }
                    }
                    catch { }

                    int waitLimit = 0;
                    while (_oauthService != null && waitLimit < 15)
                    {
                        await Task.Delay(100);
                        waitLimit++;
                    }
                    await Task.Delay(150);
                }

                string profileId = !string.IsNullOrWhiteSpace(acc.ProfileId) 
                    ? acc.ProfileId 
                    : (_currentViewingAccountsProfile?.Id ?? "");

                // Check port 8888 conflict
                int? pid = OAuthLoginService.GetProcessIdUsingPort(OAuthLoginService.DefaultPort);
                if (pid.HasValue && pid.Value != Process.GetCurrentProcess().Id)
                {
                    string procName = "Unknown";
                    try { procName = Process.GetProcessById(pid.Value).ProcessName; } catch { }

                    if (procName.Equals("antigravity-manager", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowConfirmModal(
                            LocalizationManager.T("OAuthPortConflict"),
                            LocalizationManager.T("OAuthPortConflictDesc"),
                            LocalizationManager.T("OAuthCloseAndLogin"),
                            () =>
                            {
                                try
                                {
                                    foreach (var p in Process.GetProcessesByName("antigravity-manager"))
                                    {
                                        try { p.Kill(); } catch { }
                                    }
                                    System.Threading.Thread.Sleep(500);
                                }
                                catch { }
                                _ = ExecuteOAuthLoginAsync(acc.Email, profileId);
                            },
                            isDanger: false);
                        return;
                    }
                    else
                    {
                        ShowAlertModal(
                            LocalizationManager.T("OAuthPortBusy"),
                            string.Format(LocalizationManager.T("OAuthPortBusyDesc"), procName, pid.Value),
                            isError: true);
                        return;
                    }
                }

                await ExecuteOAuthLoginAsync(acc.Email, profileId);
            }
        }

        private bool _isBatchSyncing = false;
        private CancellationTokenSource? _batchSyncCts;
        private bool _lastSyncWasBrowserClosedManually = false;
        private BatchSyncFloatingBar? _floatingBar;

        private void StopBatchSync(string reason = "Đã dừng đồng bộ.")
        {
            bool wasSyncing = _isBatchSyncing;
            _isBatchSyncing = false;
            try
            {
                _floatingBar?.Close();
            }
            catch { }
            _floatingBar = null;

            try
            {
                _batchSyncCts?.Cancel();
                _oauthCts?.Cancel();
                _oauthService?.Cancel();
                GoogleOAuthAutoConsentService.StopWatching();
                GoogleOAuthAutoConsentService.CloseAllOAuthWindows();
            }
            catch { }

            if (BtnBatchSyncAllInModal != null)
            {
                BtnBatchSyncAllInModal.IsEnabled = true;
                BtnBatchSyncAllInModal.Visibility = Visibility.Visible;
            }
            if (BtnCancelSyncInModal != null)
            {
                BtnCancelSyncInModal.Visibility = Visibility.Collapsed;
            }
            if (BorderBatchSyncProgress != null)
            {
                BorderBatchSyncProgress.Visibility = Visibility.Collapsed;
            }

            if (wasSyncing)
            {
                ShowToast(reason);
            }
        }

        private void BtnCancelSyncInModal_Click(object sender, RoutedEventArgs e)
        {
            StopBatchSync(LocalizationManager.IsEnglish ? "Sync stopped." : "Đã dừng đồng bộ.");
        }

        private void BtnBatchSyncAllInModal_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchSyncing)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Batch sync process is already running." : "Tiến trình đồng bộ hàng loạt đang chạy.");
                return;
            }

            if (_currentViewingAccountsProfile == null || _currentViewingAccountsProfile.GoogleAccounts.Count == 0)
            {
                ShowToast(LocalizationManager.IsEnglish ? "No Google accounts in this profile to sync." : "Không có tài khoản Google nào trong Profile này để đồng bộ.");
                return;
            }

            var accountsToSync = _currentViewingAccountsProfile.GoogleAccounts
                .Where(a => !string.IsNullOrWhiteSpace(a.Email))
                .ToList();

            if (accountsToSync.Count == 0)
            {
                ShowToast(LocalizationManager.IsEnglish ? "No valid email addresses found." : "Không tìm thấy email hợp lệ trong danh sách tài khoản.");
                return;
            }

            // Lọc ra các tài khoản chưa có token trong Antigravity (nếu tất cả đã có thì cho phép sync lại)
            var unsyncedAccounts = accountsToSync.Where(a => !a.IsAlreadyInAntigravity).ToList();
            var targetList = (unsyncedAccounts.Count > 0) ? unsyncedAccounts : accountsToSync;

            ShowBatchSyncConfirmModal(
                targetList, 
                () => _ = RunBatchSyncAsync(targetList, _currentViewingAccountsProfile.Id));
        }

        private async Task RunBatchSyncAsync(List<GoogleAccountInfo> accounts, string profileId)
        {
            _isBatchSyncing = true;
            _batchSyncCts = new CancellationTokenSource();
            var token = _batchSyncCts.Token;

            if (BtnBatchSyncAllInModal != null)
            {
                BtnBatchSyncAllInModal.IsEnabled = false;
                BtnBatchSyncAllInModal.Visibility = Visibility.Collapsed;
            }
            if (BtnCancelSyncInModal != null) BtnCancelSyncInModal.Visibility = Visibility.Visible;
            if (BorderBatchSyncProgress != null) BorderBatchSyncProgress.Visibility = Visibility.Visible;

            int total = accounts.Count;
            int successCount = 0;
            int failCount = 0;

            ShowToast(LocalizationManager.IsEnglish ? $"🌐 Starting batch sync for {total} accounts. Please confirm in your browser..." : $"🌐 Bắt đầu đồng bộ {total} tài khoản. Vui lòng bấm xác nhận trên trình duyệt...");

            // Khởi tạo Floating UI bám cạnh popup Chrome để người dùng có thể Dừng hoặc Bỏ qua bất kỳ lúc nào
            try
            {
                _floatingBar?.Close();
            }
            catch { }

            _floatingBar = new BatchSyncFloatingBar();
            _floatingBar.SetAutoClickState(_config.AutoClickSignIn);
            _floatingBar.AutoClickToggled += (enabled) =>
            {
                _config.AutoClickSignIn = enabled;
                _config.Save();
                UpdateSilentSyncUI();
                ShowToast(enabled 
                    ? (LocalizationManager.IsEnglish ? "⚡ Enabled Auto-Click Sign in" : "⚡ Đã BẬT Tự động bấm Đăng nhập (Auto-Click Sign in)")
                    : (LocalizationManager.IsEnglish ? "ℹ️ Disabled Auto-Click (Manual confirmation)" : "ℹ️ Đã TẮT Tự động bấm (Xác nhận thủ công)"));
            };
            _floatingBar.SkipRequested += () =>
            {
                ShowToast(LocalizationManager.IsEnglish ? "⏭ Skipping this account as requested..." : "⏭ Đang bỏ qua tài khoản này theo yêu cầu...");
                try
                {
                    GoogleOAuthAutoConsentService.CloseAllOAuthWindows();
                    _oauthCts?.Cancel();
                    _oauthService?.Cancel();
                }
                catch { }
            };
            _floatingBar.StopRequested += () =>
            {
                StopBatchSync(LocalizationManager.IsEnglish ? "Batch sync stopped." : "Đã dừng đồng bộ hàng loạt.");
            };
            _floatingBar.Show();

            try
            {
                for (int i = 0; i < accounts.Count; i++)
                {
                    if (token.IsCancellationRequested || !_isBatchSyncing) break;

                    var acc = accounts[i];
                    string email = acc.Email ?? "";
                    if (string.IsNullOrWhiteSpace(email)) continue;

                    if (TxtBatchSyncProgress != null)
                    {
                        TxtBatchSyncProgress.Text = LocalizationManager.IsEnglish ? $"Syncing ({i + 1}/{total}): {email}..." : $"Đang đồng bộ ({i + 1}/{total}): {email}...";
                    }

                    _floatingBar?.UpdateProgress(i + 1, total, email);

                    _lastSyncWasBrowserClosedManually = false;
                    bool success = await ExecuteSingleSyncAsync(email, profileId, token);
                    if (success)
                    {
                        successCount++;
                        acc.IsAlreadyInAntigravity = true;
                        ShowToast(LocalizationManager.IsEnglish ? $"✓ Synced: {email}" : $"✓ Đã đồng bộ: {email}");
                    }
                    else
                    {
                        failCount++;

                        // CHỈ DỪNG HOÀN TOÀN khi người dùng bấm nút [Dừng đồng bộ] trên UI ứng dụng!
                        if (_batchSyncCts?.IsCancellationRequested == true || !_isBatchSyncing)
                        {
                            break;
                        }

                        // Nếu người dùng bấm nút [X] trên cửa sổ Chrome, CHỈ BỎ QUA (skip) tài khoản này và tiếp tục mở tài khoản tiếp theo!
                        if (_lastSyncWasBrowserClosedManually)
                        {
                            ShowToast(LocalizationManager.IsEnglish ? $"⏭ Skipped {email} (Chrome was closed). Opening next account..." : $"⏭ Đã bỏ qua {email} (đã đóng Chrome). Đang mở tiếp tài khoản tiếp theo...");
                        }
                    }

                    if (i < accounts.Count - 1 && !token.IsCancellationRequested && _isBatchSyncing)
                    {
                        await Task.Delay(800, token);
                    }
                }

                if (!token.IsCancellationRequested && _isBatchSyncing)
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Batch sync finished: {successCount}/{total} accounts succeeded!" : $"✓ Hoàn tất đồng bộ hàng loạt: {successCount}/{total} tài khoản thành công!");
                    ShowAlertModal(
                        LocalizationManager.IsEnglish ? "Batch Sync Finished" : "Đồng bộ hàng loạt hoàn tất",
                        LocalizationManager.IsEnglish
                            ? $"Successfully synced {successCount}/{total} accounts to Antigravity.\nFailed/Skipped: {failCount}."
                            : $"Đã đồng bộ thành công: {successCount}/{total} tài khoản vào Antigravity.\nThất bại/Bỏ qua: {failCount}.",
                        isError: false);
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"Batch sync stopped ({successCount}/{total} accounts completed)." : $"Đã dừng đồng bộ hàng loạt ({successCount}/{total} tài khoản hoàn tất).");
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Batch sync process stopped." : "Đã dừng tiến trình đồng bộ hàng loạt.");
            }
            catch (Exception ex)
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Batch Sync Error" : "Lỗi đồng bộ hàng loạt", ex.Message, isError: true);
            }
            finally
            {
                _isBatchSyncing = false;
                _batchSyncCts = null;

                try
                {
                    _floatingBar?.Close();
                }
                catch { }
                _floatingBar = null;

                if (BtnBatchSyncAllInModal != null)
                {
                    BtnBatchSyncAllInModal.IsEnabled = true;
                    BtnBatchSyncAllInModal.Visibility = Visibility.Visible;
                }
                if (BtnCancelSyncInModal != null) BtnCancelSyncInModal.Visibility = Visibility.Collapsed;
                if (BorderBatchSyncProgress != null) BorderBatchSyncProgress.Visibility = Visibility.Collapsed;

                ReloadAntigravityAccounts();
                if (AccountsModalOverlay.Visibility == Visibility.Visible && _currentViewingAccountsProfile != null)
                {
                    OpenAccountsModal(_currentViewingAccountsProfile);
                }
            }
        }

        private async Task<bool> ExecuteSingleSyncAsync(string email, string profileId, CancellationToken ct)
        {
            if (_oauthService != null)
            {
                try { _oauthCts?.Cancel(); _oauthService?.Cancel(); } catch { }
                int waitCount = 0;
                while (_oauthService != null && waitCount < 15) { await Task.Delay(100); waitCount++; }
                await Task.Delay(150);
            }

            _oauthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _oauthService = new OAuthLoginService();
            _oauthService.StatusChanged += msg => Dispatcher.Invoke(() =>
            {
                ShowToast(msg);
                _floatingBar?.UpdateStatus(msg);
                if (TxtBatchSyncProgress != null && BorderBatchSyncProgress != null && BorderBatchSyncProgress.Visibility == Visibility.Visible)
                {
                    TxtBatchSyncProgress.Text = $"({email}) {msg}";
                }
            });

            try
            {
                var account = await _oauthService.StartLoginAsync(_oauthCts.Token, email, profileId);
                if (account != null)
                {
                    account.IsNameHidden = _config.HideAllNames;
                    account.IsEmailHidden = _config.HideAllEmails;
                    var list = AntigravityProfileManager.LoadAllAccounts();
                    var existing = list.FirstOrDefault(a => string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        list.Add(account);
                    }
                    else
                    {
                        existing.ApiKey = account.ApiKey;
                        existing.RefreshToken = account.RefreshToken;
                        existing.ExpiryTimestamp = account.ExpiryTimestamp;
                        existing.IdToken = account.IdToken;
                        existing.RawOauthToken = account.RawOauthToken;
                        existing.LastUsed = DateTime.Now;
                        existing.IsNameHidden = _config.HideAllNames;
                        existing.IsEmailHidden = _config.HideAllEmails;
                    }
                    AntigravityProfileManager.SaveAccountsList(list);
                    return true;
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleSync] Lỗi '{email}': {ex.Message}");
                return false;
            }
            finally
            {
                if (_oauthService?.WasBrowserClosedManually == true)
                {
                    _lastSyncWasBrowserClosedManually = true;
                }
                _oauthService = null;
                _oauthCts = null;
            }
        }

        private void BtnCopyAccountEmail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string email && !string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    Clipboard.SetText(email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
            }
        }

        // =====================================================================
        // NAVIGATION TABS (CHROME vs ANTIGRAVITY)
        // =====================================================================
        // TAB NAVIGATION & SMOOTH ANIMATION
        // =====================================================================
        private void AnimateTabFadeIn(UIElement target)
        {
            if (target == null) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            target.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void AnimateHeaderFadeIn(UIElement target)
        {
            if (target == null) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.3,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            target.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void BtnNavChrome_Click(object sender, RoutedEventArgs e)
        {
            if (ViewChrome.Visibility == Visibility.Visible) return;

            BtnNavChrome.Style = (Style)FindResource("NavTabActiveBtn");
            BtnNavAntigravity.Style = (Style)FindResource("NavTabInactiveBtn");

            ViewAntigravity.Visibility = Visibility.Collapsed;
            ViewChrome.Visibility = Visibility.Visible;
            AnimateTabFadeIn(ViewChrome);

            PanelAntigravityActions.Visibility = Visibility.Collapsed;
            PanelChromeActions.Visibility = Visibility.Visible;
            AnimateHeaderFadeIn(PanelChromeActions);

            if (BorderChromeStatus != null) BorderChromeStatus.Visibility = Visibility.Visible;
            if (BorderAntigravityStatus != null) BorderAntigravityStatus.Visibility = Visibility.Collapsed;

            TxtHeaderTitle.Text = "Antigravity Hub";
            TxtHeaderSubtitle.Text = "Chrome Workspaces";
            TxtHeaderIcon.Text = "🚀";
            if (TxtSubHeaderHint != null) TxtSubHeaderHint.Text = LocalizationManager.IsEnglish ? "⚡ Chrome Status & Quick Actions" : "⚡ Trạng thái Chrome & Thao tác nhanh";

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                UpdateChromeRunningStatus();
            }));
        }

        private void BtnNavAntigravity_Click(object sender, RoutedEventArgs e)
        {
            if (ViewAntigravity.Visibility == Visibility.Visible) return;

            BtnNavAntigravity.Style = (Style)FindResource("NavTabActiveBtn");
            BtnNavChrome.Style = (Style)FindResource("NavTabInactiveBtn");

            ViewChrome.Visibility = Visibility.Collapsed;
            ViewAntigravity.Visibility = Visibility.Visible;
            AnimateTabFadeIn(ViewAntigravity);

            PanelChromeActions.Visibility = Visibility.Collapsed;
            PanelAntigravityActions.Visibility = Visibility.Visible;
            AnimateHeaderFadeIn(PanelAntigravityActions);

            if (BorderChromeStatus != null) BorderChromeStatus.Visibility = Visibility.Collapsed;
            if (BorderAntigravityStatus != null) BorderAntigravityStatus.Visibility = Visibility.Visible;

            TxtHeaderTitle.Text = "Antigravity Hub";
            TxtHeaderSubtitle.Text = "AI Quota & Accounts";
            TxtHeaderIcon.Text = "🚀";
            if (TxtSubHeaderHint != null) TxtSubHeaderHint.Text = LocalizationManager.IsEnglish ? "⚡ IDE Status & Quick Actions" : "⚡ Tiến trình IDE & Thao tác nhanh";

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_antigravityAccounts == null || _antigravityAccounts.Count == 0)
                {
                    ReloadAntigravityAccounts();
                }
                UpdateAntigravityRunningStatus();
            }));
        }

        // =====================================================================
        // ANTIGRAVITY STATUS & ACCOUNTS MANAGEMENT
        // =====================================================================
        private void UpdateAntigravityRunningStatus()
        {
            var (procCount, hasLs, totalMemoryBytes) = AntigravityProfileManager.GetAntigravityProcessStats();
            string memStr = ChromeProfileInfo.FormatBytes(totalMemoryBytes);
            bool isDark = ThemeManager.IsDark;

            if (procCount > 0)
            {
                if (TxtAntigravityHeroRam != null)
                {
                    TxtAntigravityHeroRam.Text = $"RAM: {memStr}";
                    BorderAntigravityHeroRam.ToolTip = $"Antigravity IDE & AI Services đang tiêu thụ {memStr} RAM ({procCount} tiến trình)";
                    BorderAntigravityHeroRam.Visibility = Visibility.Visible;
                }

                if (hasLs)
                {
                    AntigravityStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    TxtAntigravityStatus.Text = $"IDE ({procCount}) • {memStr}";
                    TxtAntigravityStatus.Foreground = isDark 
                        ? new SolidColorBrush(Color.FromRgb(110, 231, 183)) 
                        : new SolidColorBrush(Color.FromRgb(6, 95, 70));
                    BorderAntigravityStatus.Background = isDark 
                        ? new SolidColorBrush(Color.FromRgb(6, 78, 59)) 
                        : new SolidColorBrush(Color.FromRgb(236, 253, 245));
                    BorderAntigravityStatus.BorderBrush = isDark 
                        ? new SolidColorBrush(Color.FromRgb(5, 150, 105)) 
                        : new SolidColorBrush(Color.FromRgb(167, 243, 208));
                    TxtAntigravityHeroStatus.Text = LocalizationManager.T("LsConnected");
                    TxtAntigravityHeroStatus.Foreground = isDark 
                        ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) 
                        : new SolidColorBrush(Color.FromRgb(5, 150, 105));
                }
                else
                {
                    AntigravityStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                    TxtAntigravityStatus.Text = $"IDE ({procCount}) • {memStr}";
                    TxtAntigravityStatus.Foreground = isDark 
                        ? new SolidColorBrush(Color.FromRgb(252, 211, 77)) 
                        : new SolidColorBrush(Color.FromRgb(146, 64, 14));
                    BorderAntigravityStatus.Background = isDark 
                        ? new SolidColorBrush(Color.FromRgb(45, 22, 9)) 
                        : new SolidColorBrush(Color.FromRgb(254, 243, 199));
                    BorderAntigravityStatus.BorderBrush = isDark 
                        ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) 
                        : new SolidColorBrush(Color.FromRgb(253, 230, 138));
                    TxtAntigravityHeroStatus.Text = LocalizationManager.T("LsStarting");
                    TxtAntigravityHeroStatus.Foreground = isDark 
                        ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) 
                        : new SolidColorBrush(Color.FromRgb(180, 83, 9));
                }
            }
            else
            {
                AntigravityStatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Grey
                TxtAntigravityStatus.Text = LocalizationManager.T("IdeClosed");
                TxtAntigravityStatus.Foreground = isDark 
                    ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) 
                    : new SolidColorBrush(Color.FromRgb(71, 85, 105));
                BorderAntigravityStatus.Background = isDark 
                    ? new SolidColorBrush(Color.FromRgb(20, 30, 47)) 
                    : new SolidColorBrush(Color.FromRgb(241, 245, 249));
                BorderAntigravityStatus.BorderBrush = isDark 
                    ? new SolidColorBrush(Color.FromRgb(36, 51, 76)) 
                    : new SolidColorBrush(Color.FromRgb(203, 213, 225));
                TxtAntigravityHeroStatus.Text = LocalizationManager.T("LsInactive");
                TxtAntigravityHeroStatus.Foreground = isDark 
                    ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) 
                    : new SolidColorBrush(Color.FromRgb(100, 116, 139));

                if (TxtAntigravityHeroRam != null)
                {
                    TxtAntigravityHeroRam.Text = "RAM: 0 MB";
                    BorderAntigravityHeroRam.ToolTip = "IDE chưa mở (Chưa tiêu thụ RAM)";
                }
            }

            if (BtnCloseAntigravity != null)
            {
                BtnCloseAntigravity.Visibility = procCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BtnCloseAntigravity_Click(object sender, RoutedEventArgs e)
        {
            var (procCount, _, totalMemoryBytes) = AntigravityProfileManager.GetAntigravityProcessStats();
            if (procCount == 0)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Antigravity IDE is not running." : "Antigravity IDE hiện không chạy.");
                UpdateAntigravityRunningStatus();
                return;
            }

            string ramStr = ChromeProfileInfo.FormatBytes(totalMemoryBytes);
            string title = LocalizationManager.IsEnglish ? "Close Antigravity IDE" : "Xác nhận đóng Antigravity IDE";
            string msg = LocalizationManager.IsEnglish
                ? $"There are currently {procCount} Antigravity IDE processes running ({ramStr} RAM).\n\n" +
                  "The system will automatically backup and preserve 100% of your chat history before closing.\n\n" +
                  "Are you sure you want to close Antigravity IDE?"
                : $"Đang có {procCount} tiến trình Antigravity IDE đang hoạt động ({ramStr} RAM).\n\n" +
                  "Hệ thống sẽ tự động sao lưu và bảo toàn toàn bộ lịch sử chat trước khi đóng để tránh mất dữ liệu.\n\n" +
                  "Bạn có chắc chắn muốn đóng Antigravity IDE không?";

            ShowConfirmModal(
                title: title,
                message: msg,
                confirmText: LocalizationManager.IsEnglish ? "🛑 Close Antigravity" : "🛑 Đóng Antigravity",
                isDanger: true,
                onConfirm: async () =>
                {
                    if (BtnCloseAntigravity != null) BtnCloseAntigravity.IsEnabled = false;
                    ShowToast(LocalizationManager.IsEnglish 
                        ? "⏳ Backing up chat history and closing Antigravity..." 
                        : "⏳ Đang sao lưu lịch sử chat và đóng Antigravity...");

                    bool success = false;
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        success = AntigravityProfileManager.SafelyCloseAntigravityWithChatPreservation();
                    });

                    UpdateAntigravityRunningStatus();
                    if (BtnCloseAntigravity != null) BtnCloseAntigravity.IsEnabled = true;

                    if (success)
                    {
                        ShowToast(LocalizationManager.IsEnglish 
                            ? "✓ Antigravity closed safely. All chat histories preserved!" 
                            : "✓ Đã đóng Antigravity an toàn. Toàn bộ lịch sử chat đã được bảo toàn!");
                    }
                    else
                    {
                        ShowToast(LocalizationManager.IsEnglish 
                            ? "Completed closing Antigravity." 
                            : "Đã hoàn tất đóng tiến trình Antigravity.");
                    }
                });
        }

        private void ReloadAntigravityAccounts()
        {
            try
            {
                _antigravityAccounts = AntigravityProfileManager.LoadAllAccounts();
                _activeAntigravityAccount = AntigravityProfileManager.GetCurrentActiveAccount();

                foreach (var acc in _antigravityAccounts)
                {
                    acc.IsRefreshingQuota = false;
                    acc.IsNameHidden = _config.HideAllNames;
                    acc.IsEmailHidden = _config.HideAllEmails;
                }
                if (_activeAntigravityAccount != null)
                {
                    _activeAntigravityAccount.IsRefreshingQuota = false;
                    _activeAntigravityAccount.IsNameHidden = _config.HideAllNames;
                    _activeAntigravityAccount.IsEmailHidden = _config.HideAllEmails;
                }

                UpdatePrivacyUI();

                if (_activeAntigravityAccount != null)
                {
                    TxtAntigravityHeroName.Text = _activeAntigravityAccount.DisplayNameMasked;
                    TxtAntigravityHeroEmail.Text = string.IsNullOrEmpty(_activeAntigravityAccount.Email) ? "Chưa đồng bộ" : _activeAntigravityAccount.EmailMasked;
                    TxtAntigravityHeroInitial.Text = _activeAntigravityAccount.InitialLetter;

                    var savedActive = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (savedActive != null)
                    {
                        // Copy cached quota & avatar from saved active account to _activeAntigravityAccount
                        _activeAntigravityAccount.CopyQuotaFrom(savedActive);

                        if (savedActive.HasAvatar && savedActive.AvatarSource != null)
                        {
                            ImgAntigravityHeroAvatar.ImageSource = savedActive.AvatarSource;
                            BorderAntigravityHeroAvatar.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            BorderAntigravityHeroAvatar.Visibility = Visibility.Collapsed;
                        }
                    }
                    else
                    {
                        BorderAntigravityHeroAvatar.Visibility = Visibility.Collapsed;
                    }

                    // Update Gemini Pro Avatar Ring (matches real Google Gemini Pro UI)
                    if (_activeAntigravityAccount != null && _activeAntigravityAccount.IsProOrHigher)
                    {
                        BorderAntigravityHeroProRing.Visibility = Visibility.Visible;
                        BorderAntigravityHeroNormalRing.Visibility = Visibility.Collapsed;
                        BorderAntigravityHeroInitialBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00382B"));
                    }
                    else
                    {
                        BorderAntigravityHeroProRing.Visibility = Visibility.Collapsed;
                        BorderAntigravityHeroNormalRing.Visibility = Visibility.Visible;
                        BorderAntigravityHeroInitialBg.Background = (Brush)FindResource("ThemeAccountAvatarBg");
                    }

                    // Match active status in list
                    foreach (var acc in _antigravityAccounts)
                    {
                        acc.IsActive = !string.IsNullOrEmpty(acc.Email) &&
                                       string.Equals(acc.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase);
                        acc.NotifyThemeChanged();
                    }
                    _activeAntigravityAccount?.NotifyThemeChanged();
                }
                else
                {
                    TxtAntigravityHeroName.Text = "Chưa kết nối tài khoản";
                    TxtAntigravityHeroEmail.Text = "Chưa đăng nhập";
                    TxtAntigravityHeroInitial.Text = "A";
                    BorderAntigravityHeroAvatar.Visibility = Visibility.Collapsed;
                    BorderAntigravityHeroProRing.Visibility = Visibility.Collapsed;
                    BorderAntigravityHeroNormalRing.Visibility = Visibility.Visible;
                    BorderAntigravityHeroInitialBg.Background = (Brush)FindResource("ThemeAccountAvatarBg");
                }

                // Initial display of hero model quota (from saved cache if available)
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);

                UpdateScopeButtonsUI();
                FilterAntigravityAccountsList();

                // Fetch avatars, refresh tokens, and live model quota in background (non-blocking)
                var accountsToFetch = _antigravityAccounts;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    // Run avatar caching and live model quota concurrently
                    var avatarTask = AntigravityProfileManager.FetchAvatarAndQuotaAsync(accountsToFetch);

                    // Fetch live model quota for active account
                    if (_activeAntigravityAccount != null)
                    {
                        _activeAntigravityAccount.IsRefreshingQuota = true;
                        try
                        {
                            await AntigravityProfileManager.FetchLiveModelQuotaAsync(_activeAntigravityAccount);
                        }
                        catch { }
                        finally
                        {
                            _activeAntigravityAccount.IsRefreshingQuota = false;
                            Dispatcher.Invoke(() => UpdateHeroModelQuotaDisplay(_activeAntigravityAccount));
                        }
                    }

                    // Also fetch live model quota directly for any accounts that don't have quota yet or have unclassified Plan
                    var unmeasured = accountsToFetch.Where(a => (!a.HasModelQuota || string.IsNullOrEmpty(a.Plan) || a.Plan == "Antigravity") && (a.HasFullTokens || !string.IsNullOrEmpty(a.RefreshToken))).ToList();
                    if (unmeasured.Count > 0)
                    {
                        using var preloadSem = new System.Threading.SemaphoreSlim(5);
                        var preloadTasks = unmeasured.Select(async a =>
                        {
                            await preloadSem.WaitAsync();
                            try
                            {
                                a.IsRefreshingQuota = true;
                                await AntigravityProfileManager.FetchLiveModelQuotaAsync(a);
                            }
                            catch { }
                            finally
                            {
                                a.IsRefreshingQuota = false;
                                preloadSem.Release();
                                Dispatcher.Invoke(() => FilterAntigravityAccountsList());
                            }
                        }).ToList();
                        await Task.WhenAll(preloadTasks);
                    }

                    await avatarTask;

                    Dispatcher.Invoke(() =>
                    {
                        // Check if active account got an avatar or quota update
                        if (_activeAntigravityAccount != null)
                        {
                            var saved = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                            if (saved != null)
                            {
                                saved.CopyQuotaFrom(_activeAntigravityAccount);
                                if (saved.HasAvatar && saved.AvatarSource != null)
                                {
                                    ImgAntigravityHeroAvatar.ImageSource = saved.AvatarSource;
                                    BorderAntigravityHeroAvatar.Visibility = Visibility.Visible;
                                }
                            }
                            UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                        }
                        FilterAntigravityAccountsList();
                    });
                });
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"Error loading Antigravity accounts: {ex.Message}" : $"Lỗi khi tải tài khoản Antigravity: {ex.Message}");
            }
        }

        private void UpdateHeroModelQuotaDisplay(AntigravityAccountInfo? acc)
        {
            if (acc == null)
            {
                TxtAntigravityHeroName.Text = LocalizationManager.T("NoAccountConnected");
                TxtAntigravityHeroEmail.Text = LocalizationManager.T("NotLoggedIn");
                TxtHeroGeminiQuota.Text = "--%";
                TxtHeroClaudeQuota.Text = "--%";
                if (BorderAntigravityHeroPlan != null)
                {
                    BorderAntigravityHeroPlan.Effect = null;
                    BorderAntigravityHeroPlan.SetResourceReference(Border.BackgroundProperty, "ThemeCardSubBg");
                    BorderAntigravityHeroPlan.SetResourceReference(Border.BorderBrushProperty, "ThemeCardSubBorder");
                }
                if (TxtAntigravityHeroPlan != null)
                {
                    TxtAntigravityHeroPlan.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
                    TxtAntigravityHeroPlan.Text = "FREE";
                }
                TxtAntigravityHeroCredits.Text = "0 credits";
                HeroItemsCol1.ItemsSource = null;
                HeroItemsCol2.ItemsSource = null;
                HeroItemsCol3.ItemsSource = null;
                HeroItemsWeekly.ItemsSource = null;
                return;
            }

            TxtAntigravityHeroName.Text = acc.DisplayNameMasked;
            TxtAntigravityHeroEmail.Text = string.IsNullOrEmpty(acc.Email) ? "Chưa đồng bộ" : acc.EmailMasked;
            
            if (BorderAntigravityHeroPlan != null && TxtAntigravityHeroPlan != null)
            {
                BorderAntigravityHeroPlan.Background = acc.PlanBadgeBackground;
                BorderAntigravityHeroPlan.BorderBrush = acc.PlanBadgeBorderBrush;
                TxtAntigravityHeroPlan.Foreground = acc.PlanBadgeForeground;

                if (acc.PlanBadgeText == "PRO")
                {
                    BorderAntigravityHeroPlan.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = (Color)ColorConverter.ConvertFromString("#EC4899"),
                        BlurRadius = 10,
                        ShadowDepth = 0,
                        Opacity = 0.7
                    };
                    TxtAntigravityHeroPlan.Text = acc.PlanBadgeDisplay;
                }
                else
                {
                    BorderAntigravityHeroPlan.Effect = null;
                    TxtAntigravityHeroPlan.Text = acc.PlanBadgeDisplay;
                }
            }

            TxtAntigravityHeroCredits.Text = acc.CreditsText;

            string weekSuffix = LocalizationManager.IsEnglish ? "(Week)" : "(Tuần)";
            string pendingText = LocalizationManager.IsEnglish ? "Pending..." : "Chờ đo...";

            if (acc.Gemini5hPercent.HasValue || acc.GeminiWeeklyPercent.HasValue)
            {
                if (_currentQuotaScope == "5h")
                    TxtHeroGeminiQuota.Text = $"{acc.Gemini5hPercent ?? 0}%";
                else if (_currentQuotaScope == "week")
                    TxtHeroGeminiQuota.Text = $"{acc.GeminiWeeklyPercent ?? 0}%";
                else
                    TxtHeroGeminiQuota.Text = $"{acc.Gemini5hPercent ?? 0}% (5h) • {acc.GeminiWeeklyPercent ?? 0}% {weekSuffix}";
                BorderHeroGeminiQuota.ToolTip = acc.GeminiResetText ?? (LocalizationManager.IsEnglish ? "Gemini model quota" : "Hạn mức mô hình Gemini");
            }
            else
            {
                TxtHeroGeminiQuota.Text = pendingText;
            }

            if (acc.Claude5hPercent.HasValue || acc.ClaudeWeeklyPercent.HasValue)
            {
                if (_currentQuotaScope == "5h")
                    TxtHeroClaudeQuota.Text = $"{acc.Claude5hPercent ?? 0}%";
                else if (_currentQuotaScope == "week")
                    TxtHeroClaudeQuota.Text = $"{acc.ClaudeWeeklyPercent ?? 0}%";
                else
                    TxtHeroClaudeQuota.Text = $"{acc.Claude5hPercent ?? 0}% (5h) • {acc.ClaudeWeeklyPercent ?? 0}% {weekSuffix}";
                BorderHeroClaudeQuota.ToolTip = acc.ClaudeResetText ?? (LocalizationManager.IsEnglish ? "Claude & GPT model quota" : "Hạn mức mô hình Claude & GPT");
            }
            else
            {
                TxtHeroClaudeQuota.Text = pendingText;
            }

            // Populate 3 columns and weekly items
            HeroItemsCol1.ItemsSource = acc.Col1Models;
            HeroItemsCol2.ItemsSource = acc.Col2Models;
            HeroItemsCol3.ItemsSource = acc.Col3Models;
            HeroItemsWeekly.ItemsSource = acc.WeeklyModels;
            TxtHeroResetCountdown.Text = acc.ResetCountdownSummary;
        }

        private void BtnToggleHeroQuota_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (BorderHeroQuotaDetails.Visibility == Visibility.Visible)
            {
                BorderHeroQuotaDetails.Visibility = Visibility.Collapsed;
                TxtHeroQuotaToggleIcon.Text = LocalizationManager.T("View20Models");
            }
            else
            {
                BorderHeroQuotaDetails.Visibility = Visibility.Visible;
                TxtHeroQuotaToggleIcon.Text = LocalizationManager.T("CollapseModels");
            }
        }

        private void BtnOpenHeroQuotaWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var target = _activeAntigravityAccount ?? _antigravityAccounts.FirstOrDefault();
            if (target != null)
            {
                var win = new ModelQuotaWindow(target, (acc) =>
                {
                    if (_isSwitching)
                    {
                        ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                        return;
                    }
                    BtnSwitchAntigravityAccount_Click(acc, new RoutedEventArgs());
                });
                win.Owner = this;
                win.Closed += (s, ev) =>
                {
                    var updatedList = AntigravityProfileManager.LoadAllAccounts();
                    foreach (var accItem in _antigravityAccounts)
                    {
                        var match = updatedList.FirstOrDefault(x => string.Equals(x.Email, accItem.Email, StringComparison.OrdinalIgnoreCase));
                        if (match != null && match.HasModelQuota)
                        {
                            accItem.CopyQuotaFrom(match);
                        }
                    }
                    if (_activeAntigravityAccount != null)
                    {
                        var activeSaved = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                        if (activeSaved != null && activeSaved.HasModelQuota)
                        {
                            _activeAntigravityAccount.CopyQuotaFrom(activeSaved);
                        }
                        UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                    }
                    FilterAntigravityAccountsList();
                };
                win.ShowDialog();
            }
            else
            {
                ShowToast(LocalizationManager.IsEnglish ? "No Antigravity accounts to view." : "Chưa có tài khoản Antigravity để xem.");
            }
        }

        private void BtnViewAccountQuota_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AntigravityAccountInfo acc)
            {
                TriggerViewAccountQuota(acc);
            }
        }

        private void TriggerViewAccountQuota(AntigravityAccountInfo acc)
        {
            var win = new ModelQuotaWindow(acc, (targetAcc) =>
            {
                if (_isSwitching)
                {
                    ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                    return;
                }
                _ = ExecuteSwitchToAccountAsync(targetAcc, null);
            });
            win.Owner = this;
            win.Closed += (s, ev) =>
            {
                var updatedList = AntigravityProfileManager.LoadAllAccounts();
                foreach (var accItem in _antigravityAccounts)
                {
                    var match = updatedList.FirstOrDefault(x => string.Equals(x.Email, accItem.Email, StringComparison.OrdinalIgnoreCase));
                    if (match != null && match.HasModelQuota)
                    {
                        accItem.CopyQuotaFrom(match);
                    }
                }
                if (_activeAntigravityAccount != null)
                {
                    var activeSaved = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (activeSaved != null && activeSaved.HasModelQuota)
                    {
                        _activeAntigravityAccount.CopyQuotaFrom(activeSaved);
                    }
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }
                FilterAntigravityAccountsList();
            };
            win.ShowDialog();
        }

        private void BtnRefreshSingleAccountQuota_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AntigravityAccountInfo acc)
            {
                TriggerRefreshSingleAccount(acc, btn);
            }
        }

        private async void TriggerRefreshSingleAccount(AntigravityAccountInfo acc, Button? btn = null)
        {
            if (acc.IsRefreshingQuota) return;
            acc.IsRefreshingQuota = true;
            if (btn != null) btn.IsEnabled = false;
            ShowToast(LocalizationManager.IsEnglish ? $"⚡ Refreshing quota for {acc.Email}..." : $"⚡ Đang refresh hạn mức cho {acc.Email}...");

            try
            {
                bool ok = await AntigravityProfileManager.FetchLiveModelQuotaAsync(acc);
                if (ok)
                {
                    if (_activeAntigravityAccount != null && string.Equals(_activeAntigravityAccount.Email, acc.Email, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeAntigravityAccount.CopyQuotaFrom(acc);
                        UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                    }
                    FilterAntigravityAccountsList();
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Updated quota for {acc.Email}!" : $"✓ Đã cập nhật xong hạn mức {acc.Email}!");
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"⚠️ Unable to update quota for {acc.Email}." : $"⚠️ Không thể cập nhật hạn mức cho {acc.Email}.");
                }
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"❌ Error refreshing {acc.Email}: {ex.Message}" : $"❌ Lỗi refresh {acc.Email}: {ex.Message}");
            }
            finally
            {
                acc.IsRefreshingQuota = false;
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void BtnRefreshLiveQuota_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeAntigravityAccount == null)
            {
                ShowToast(LocalizationManager.IsEnglish ? "No active Antigravity account to measure quota." : "Chưa có tài khoản Antigravity active để đo quota.");
                return;
            }

            _activeAntigravityAccount.IsRefreshingQuota = true;
            TxtHeroGeminiQuota.Text = LocalizationManager.IsEnglish ? "Measuring..." : "Đang đo...";
            TxtHeroClaudeQuota.Text = LocalizationManager.IsEnglish ? "Measuring..." : "Đang đo...";

            try
            {
                bool ok = await AntigravityProfileManager.FetchLiveModelQuotaAsync(_activeAntigravityAccount);
                if (ok)
                {
                    var matching = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (matching != null)
                    {
                        matching.CopyQuotaFrom(_activeAntigravityAccount);
                    }
                    _activeAntigravityAccount.NotifyThemeChanged();
                    _activeAntigravityAccount.NotifyScopeChanged();
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                    FilterAntigravityAccountsList();
                    ShowToast(LocalizationManager.IsEnglish ? "✓ Model Quota updated directly!" : "✓ Đã cập nhật hạn mức Model Quota trực tiếp!");
                }
                else
                {
                    _activeAntigravityAccount.NotifyThemeChanged();
                    _activeAntigravityAccount.NotifyScopeChanged();
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                    ShowToast(LocalizationManager.IsEnglish ? "⚠️ Unable to connect to measure quota." : "⚠️ Không thể kết nối đo hạn mức.");
                }
            }
            finally
            {
                _activeAntigravityAccount.IsRefreshingQuota = false;
            }
        }

        private async void BtnRefreshAllAccountsQuota_Click(object sender, RoutedEventArgs e)
        {
            await RunBatchMeasureAllAccountsQuotaAsync(isSilent: false);
        }

        private async Task RunBatchMeasureAllAccountsQuotaAsync(bool isSilent = false)
        {
            if (_isBatchMeasuring)
            {
                if (!isSilent) ShowToast(LocalizationManager.IsEnglish ? "⏳ Quota measurement in progress, please wait..." : "⏳ Đang trong quá trình đo hạn mức, vui lòng đợi...");
                return;
            }

            var accountsToMeasure = _antigravityAccounts.Where(a => a.HasFullTokens || !string.IsNullOrEmpty(a.RefreshToken)).ToList();
            if (accountsToMeasure.Count == 0)
            {
                if (!isSilent) ShowToast(LocalizationManager.IsEnglish ? "⚠️ No accounts found with valid login tokens." : "⚠️ Không tìm thấy tài khoản nào có đủ token đăng nhập.");
                return;
            }

            int maxConcurrency = Math.Clamp(_config.BatchMeasureConcurrency > 0 ? _config.BatchMeasureConcurrency : 5, 1, 30);
            _isBatchMeasuring = true;
            if (!isSilent)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"⚡ Measuring live quota ({maxConcurrency} parallel threads) for {accountsToMeasure.Count} accounts..." : $"⚡ Đang đo hạn mức trực tiếp ({maxConcurrency} luồng song song) cho {accountsToMeasure.Count} tài khoản...");
            }

            // Bật trạng thái loading cho toàn bộ tài khoản ngay lập tức
            foreach (var acc in accountsToMeasure)
            {
                acc.IsRefreshingQuota = true;
            }

            try
            {
                using var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency);

                var tasks = accountsToMeasure.Select(async acc =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        bool ok = await AntigravityProfileManager.FetchLiveModelQuotaAsync(acc);
                        if (ok && _activeAntigravityAccount != null && string.Equals(acc.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _activeAntigravityAccount.CopyQuotaFrom(acc);
                                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                            });
                        }
                        return (acc, ok);
                    }
                    catch
                    {
                        return (acc, false);
                    }
                    finally
                    {
                        // Tài khoản nào xong thì nhả loading ngay lập tức cho riêng tài khoản đó
                        acc.IsRefreshingQuota = false;
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                if (_activeAntigravityAccount != null)
                {
                    var match = _antigravityAccounts.FirstOrDefault(x => string.Equals(x.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        _activeAntigravityAccount.CopyQuotaFrom(match);
                    }
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }

                FilterAntigravityAccountsList();

                _lastAutoRefreshQuotaTime = DateTime.Now;
                UpdateAutoRefreshQuotaUI();

                if (!isSilent)
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Updated quota for all {accountsToMeasure.Count} accounts!" : $"✓ Đã cập nhật xong hạn mức cho toàn bộ {accountsToMeasure.Count} tài khoản!");
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"⚡ [Auto Quota] Automatically updated quota for {accountsToMeasure.Count} accounts ({DateTime.Now:HH:mm:ss})" : $"⚡ [Auto Quota] Đã tự động cập nhật xong hạn mức cho {accountsToMeasure.Count} tài khoản ({DateTime.Now:HH:mm:ss})");
                }
            }
            catch (Exception ex)
            {
                if (!isSilent) ShowToast(LocalizationManager.IsEnglish ? $"❌ Error measuring quota: {ex.Message}" : $"❌ Lỗi khi đo hạn mức: {ex.Message}");
            }
            finally
            {
                // Đảm bảo không còn tài khoản nào bị sót trạng thái loading
                foreach (var acc in accountsToMeasure)
                {
                    acc.IsRefreshingQuota = false;
                }
                _isBatchMeasuring = false;
            }
        }

        private async void AutoRefreshQuotaTimer_Tick(object? sender, EventArgs e)
        {
            if (!_config.AutoRefreshAllQuotaEnabled) return;
            if (_isBatchMeasuring) return;

            int intervalSec = _config.TotalAutoRefreshIntervalSeconds;
            DateTime now = DateTime.Now;

            if (!_lastAutoRefreshQuotaTime.HasValue || (now - _lastAutoRefreshQuotaTime.Value).TotalSeconds >= intervalSec)
            {
                if (_antigravityAccounts.Any(a => a.HasFullTokens || !string.IsNullOrEmpty(a.RefreshToken)))
                {
                    await RunBatchMeasureAllAccountsQuotaAsync(isSilent: true);
                }
                else
                {
                    _lastAutoRefreshQuotaTime = now;
                }
            }

            UpdateAutoRefreshQuotaUI();
        }

        private void BtnToggleAutoRefresh_Click(object sender, RoutedEventArgs e)
        {
            _config.AutoRefreshAllQuotaEnabled = !_config.AutoRefreshAllQuotaEnabled;
            _config.Save();

            if (_config.AutoRefreshAllQuotaEnabled)
            {
                _lastAutoRefreshQuotaTime = DateTime.Now;
                _autoRefreshQuotaTimer.Start();
                ShowToast(LocalizationManager.IsEnglish
                    ? $"✓ Auto Quota measure ENABLED for all accounts (every {_config.FormatIntervalDetailed()})"
                    : $"✓ Đã BẬT Tự động đo Quota toàn bộ tài khoản (mỗi {_config.FormatIntervalDetailed()})");
            }
            else
            {
                ShowToast(LocalizationManager.IsEnglish
                    ? "ℹ Auto Quota measure DISABLED"
                    : "ℹ Đã TẮT Tự động đo Quota");
            }

            UpdateAutoRefreshQuotaUI();
        }

        private void BtnAutoRefresh_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (BorderAutoRefreshInterval != null)
            {
                ShowAutoRefreshIntervalMenu(BorderAutoRefreshInterval);
            }
        }

        private void BorderAutoRefreshInterval_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement target)
            {
                ShowAutoRefreshIntervalMenu(target);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void ShowAutoRefreshIntervalMenu(FrameworkElement target)
        {
            var cm = new ContextMenu();
            cm.MaxHeight = 340;
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);

            cm.PreviewMouseWheel += (s, ev) =>
            {
                var sv = FindVisualChild<ScrollViewer>(cm);
                if (sv != null)
                {
                    if (ev.Delta > 0)
                        sv.LineUp();
                    else
                        sv.LineDown();
                    ev.Handled = true;
                }
            };

            bool isEn = LocalizationManager.IsEnglish;

            var titleItem = new MenuItem
            {
                Header = LocalizationManager.T("AutoRefreshMenuHeader"),
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            titleItem.SetResourceReference(MenuItem.ForegroundProperty, "ThemeTextSecondary");
            cm.Items.Add(titleItem);
            cm.Items.Add(new Separator());

            var toggleItem = new MenuItem
            {
                Header = (_config.AutoRefreshAllQuotaEnabled ? "✓  " : "    ") + LocalizationManager.T("AutoRefreshToggle"),
                FontWeight = _config.AutoRefreshAllQuotaEnabled ? FontWeights.Bold : FontWeights.Normal
            };
            toggleItem.Click += (s, ev) =>
            {
                _config.AutoRefreshAllQuotaEnabled = !_config.AutoRefreshAllQuotaEnabled;
                _config.Save();

                if (_config.AutoRefreshAllQuotaEnabled)
                {
                    _lastAutoRefreshQuotaTime = DateTime.Now;
                    _autoRefreshQuotaTimer.Start();
                    ShowToast(isEn
                        ? $"✓ Auto Quota measure ENABLED (every {_config.FormatIntervalDetailed()})"
                        : $"✓ Đã BẬT Tự động đo Quota (mỗi {_config.FormatIntervalDetailed()})");
                }
                else
                {
                    ShowToast(isEn
                        ? "ℹ Auto Quota measure DISABLED"
                        : "ℹ Đã TẮT Tự động đo Quota");
                }

                UpdateAutoRefreshQuotaUI();
            };
            cm.Items.Add(toggleItem);
            cm.Items.Add(new Separator());

            int currentSec = _config.TotalAutoRefreshIntervalSeconds;
            var intervals = isEn ? new (int Seconds, string Label, string Desc)[]
            {
                (15, "Every 15 seconds", "Ultra-fast check, best for testing quota"),
                (30, "Every 30 seconds", "Updates every half minute"),
                (60, "Every 1 minute", "Continuous update every minute"),
                (120, "Every 2 minutes", "Very fast, ideal during active coding"),
                (180, "Every 3 minutes", "Quick and steady check"),
                (300, "Every 5 minutes", "Frequent update, suitable while working"),
                (600, "Every 10 minutes", "Steady periodic refresh"),
                (900, "Every 15 minutes (Recommended)", "Optimal balance between updates and resources"),
                (1800, "Every 30 minutes", "Saves resources and keeps light"),
                (3600, "Every 1 hour", "Suitable for general monitoring"),
                (7200, "Every 2 hours", "Lightweight relaxed interval")
            } : new (int Seconds, string Label, string Desc)[]
            {
                (15, "Mỗi 15 giây", "Kiểm tra siêu tốc, phù hợp khi test hạn mức"),
                (30, "Mỗi 30 giây", "Cập nhật nửa phút một lần"),
                (60, "Mỗi 1 phút", "Cập nhật liên tục mỗi phút"),
                (120, "Mỗi 2 phút", "Rất nhanh, phù hợp khi code liên tục"),
                (180, "Mỗi 3 phút", "Nhanh chóng và đều đặn"),
                (300, "Mỗi 5 phút", "Cập nhật thường xuyên, phù hợp khi làm việc"),
                (600, "Mỗi 10 phút", "Nhanh chóng và đều đặn"),
                (900, "Mỗi 15 phút (Khuyên dùng)", "Cân bằng tối ưu giữa cập nhật và tài nguyên"),
                (1800, "Mỗi 30 phút", "Tiết kiệm tài nguyên và nhẹ máy"),
                (3600, "Mỗi 1 giờ", "Thích hợp khi chỉ theo dõi tổng quan"),
                (7200, "Mỗi 2 giờ", "Chu kỳ giãn cách nhẹ nhàng")
            };

            bool matchedAnyPreset = false;
            foreach (var opt in intervals)
            {
                bool isSelected = (_config.AutoRefreshAllQuotaEnabled && currentSec == opt.Seconds);
                if (isSelected) matchedAnyPreset = true;

                var mi = new MenuItem
                {
                    Header = (isSelected ? "●  " : "    ") + opt.Label,
                    ToolTip = opt.Desc,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal
                };
                int capturedSec = opt.Seconds;
                string capturedLabel = opt.Label;
                mi.Click += (s, ev) =>
                {
                    _config.AutoRefreshAllQuotaIntervalSeconds = capturedSec;
                    _config.AutoRefreshAllQuotaIntervalMinutes = (capturedSec >= 60 && capturedSec % 60 == 0) ? (capturedSec / 60) : 0;
                    _config.AutoRefreshAllQuotaEnabled = true;
                    _config.Save();

                    _lastAutoRefreshQuotaTime = DateTime.Now;
                    _autoRefreshQuotaTimer.Start();

                    UpdateAutoRefreshQuotaUI();
                    ShowToast(isEn
                        ? $"✓ Auto Quota measure ENABLED: {capturedLabel.ToLower()}"
                        : $"✓ Đã BẬT Tự động đo Quota: {capturedLabel.ToLower()}");
                };
                cm.Items.Add(mi);
            }

            cm.Items.Add(new Separator());

            // Custom interval option
            bool isCustomSelected = _config.AutoRefreshAllQuotaEnabled && !matchedAnyPreset;
            var customItem = new MenuItem
            {
                Header = (isCustomSelected ? "●  " : "    ") + (isEn ? "⚙  Custom interval..." : "⚙  Tùy chỉnh thời gian...") + (isCustomSelected ? $" ({_config.FormatIntervalDisplay()})" : ""),
                FontWeight = isCustomSelected ? FontWeights.Bold : FontWeights.Normal,
                ToolTip = isEn ? "Enter any interval (e.g. 45s, 20m, 3h...)" : "Nhập khoảng thời gian bất kỳ (ví dụ: 45 giây, 20 phút, 3 giờ...)"
            };
            customItem.Click += (s, ev) =>
            {
                OpenCustomIntervalModal();
            };
            cm.Items.Add(customItem);

            cm.PlacementTarget = target;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void OpenCustomIntervalModal()
        {
            if (CustomIntervalModalOverlay == null) return;

            int currentSec = _config.TotalAutoRefreshIntervalSeconds;
            if (currentSec < 60 || currentSec % 60 != 0)
            {
                TxtCustomIntervalInput.Text = currentSec.ToString();
                CmbCustomIntervalUnit.SelectedIndex = 0; // Giây
            }
            else if (currentSec % 3600 == 0)
            {
                TxtCustomIntervalInput.Text = (currentSec / 3600).ToString();
                CmbCustomIntervalUnit.SelectedIndex = 2; // Giờ
            }
            else
            {
                TxtCustomIntervalInput.Text = (currentSec / 60).ToString();
                CmbCustomIntervalUnit.SelectedIndex = 1; // Phút
            }

            CustomIntervalModalOverlay.Visibility = Visibility.Visible;
            TxtCustomIntervalInput.Focus();
            TxtCustomIntervalInput.SelectAll();
        }

        private void CustomIntervalModalBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (CustomIntervalModalOverlay != null)
            {
                CustomIntervalModalOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnCustomIntervalCancel_Click(object sender, RoutedEventArgs e)
        {
            if (CustomIntervalModalOverlay != null)
            {
                CustomIntervalModalOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtCustomInterval_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private void BtnPresetInterval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                if (tag.EndsWith("s") && int.TryParse(tag.TrimEnd('s'), out int sVal))
                {
                    TxtCustomIntervalInput.Text = sVal.ToString();
                    CmbCustomIntervalUnit.SelectedIndex = 0; // Giây
                }
                else if (tag.EndsWith("m") && int.TryParse(tag.TrimEnd('m'), out int mVal))
                {
                    TxtCustomIntervalInput.Text = mVal.ToString();
                    CmbCustomIntervalUnit.SelectedIndex = 1; // Phút
                }
                else if (tag.EndsWith("h") && int.TryParse(tag.TrimEnd('h'), out int hVal))
                {
                    TxtCustomIntervalInput.Text = hVal.ToString();
                    CmbCustomIntervalUnit.SelectedIndex = 2; // Giờ
                }
            }
        }

        private void BtnCustomIntervalApply_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCustomIntervalInput.Text.Trim(), out int num) || num <= 0)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Please enter an integer greater than 0" : "Vui lòng nhập một số nguyên lớn hơn 0");
                return;
            }

            int unitIdx = CmbCustomIntervalUnit.SelectedIndex;
            int totalSec = unitIdx switch
            {
                0 => num,            // Giây
                1 => num * 60,       // Phút
                2 => num * 3600,     // Giờ
                _ => num * 60
            };

            if (totalSec < 5)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Minimum interval is 5 seconds" : "Khoảng thời gian tối thiểu là 5 giây");
                return;
            }
            if (totalSec > 86400 * 7) // 7 days
            {
                ShowToast(LocalizationManager.IsEnglish ? "Maximum interval is 7 days" : "Khoảng thời gian tối đa là 7 ngày");
                return;
            }

            _config.AutoRefreshAllQuotaIntervalSeconds = totalSec;
            _config.AutoRefreshAllQuotaIntervalMinutes = (totalSec >= 60 && totalSec % 60 == 0) ? (totalSec / 60) : 0;
            _config.AutoRefreshAllQuotaEnabled = true;
            _config.Save();

            _lastAutoRefreshQuotaTime = DateTime.Now;
            _autoRefreshQuotaTimer.Start();

            if (CustomIntervalModalOverlay != null)
            {
                CustomIntervalModalOverlay.Visibility = Visibility.Collapsed;
            }

            UpdateAutoRefreshQuotaUI();
            ShowToast(LocalizationManager.IsEnglish ? $"✓ ENABLED Auto Quota: every {_config.FormatIntervalDetailed()}" : $"✓ Đã BẬT Tự động đo Quota: mỗi {_config.FormatIntervalDetailed()}");
        }

        private void UpdateAutoRefreshQuotaUI()
        {
            if (BorderAutoRefreshQuota == null || TxtAutoRefreshStatus == null || TxtAutoRefreshInterval == null || TxtAutoRefreshIcon == null) return;

            bool isEnabled = _config.AutoRefreshAllQuotaEnabled;
            int intervalSec = _config.TotalAutoRefreshIntervalSeconds;
            string intervalPill = _config.FormatIntervalDisplay();
            string intervalText = _config.FormatIntervalDetailed();

            TxtAutoRefreshInterval.Text = intervalPill;

            if (isEnabled)
            {
                TxtAutoRefreshIcon.Text = "⚡";
                TxtAutoRefreshStatus.Text = LocalizationManager.T("AutoQuotaOn");
                TxtAutoRefreshStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                BorderAutoRefreshQuota.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtAutoRefreshInterval.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

                string nextRunStr = "";
                if (_lastAutoRefreshQuotaTime.HasValue)
                {
                    var nextTime = _lastAutoRefreshQuotaTime.Value.AddSeconds(intervalSec);
                    var remaining = nextTime - DateTime.Now;
                    if (remaining.TotalSeconds > 0)
                    {
                        if (remaining.TotalMinutes >= 1)
                            nextRunStr = LocalizationManager.IsEnglish
                                ? $"\n⏱ Next measure: {nextTime:HH:mm:ss} (in {(int)remaining.TotalMinutes}m {remaining.Seconds}s)"
                                : $"\n⏱ Lần đo kế tiếp: {nextTime:HH:mm:ss} (còn {(int)remaining.TotalMinutes}m {remaining.Seconds}s)";
                        else
                            nextRunStr = LocalizationManager.IsEnglish
                                ? $"\n⏱ Next measure: {nextTime:HH:mm:ss} (in {(int)remaining.TotalSeconds}s)"
                                : $"\n⏱ Lần đo kế tiếp: {nextTime:HH:mm:ss} (còn {(int)remaining.TotalSeconds}s)";
                    }
                }
                BorderAutoRefreshQuota.ToolTip = LocalizationManager.IsEnglish
                    ? $"Auto measure Quota for all accounts every {intervalText} (ENABLED).{nextRunStr}\n• Left-click: Toggle ON/OFF\n• Click arrow: Choose or customize interval"
                    : $"Tự động đo lại Quota cho toàn bộ tài khoản mỗi {intervalText} (ĐANG BẬT).{nextRunStr}\n• Nhấp chuột trái: Tắt/Bật nhanh\n• Nhấp mũi tên: Chọn chu kỳ hoặc tùy chỉnh";
            }
            else
            {
                TxtAutoRefreshIcon.Text = "⏱";
                TxtAutoRefreshStatus.Text = LocalizationManager.T("AutoQuotaOff");
                TxtAutoRefreshStatus.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
                BorderAutoRefreshQuota.SetResourceReference(Border.BorderBrushProperty, "ThemeSecondaryBtnBorder");
                TxtAutoRefreshInterval.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMuted");
                BorderAutoRefreshQuota.ToolTip = LocalizationManager.IsEnglish
                    ? $"Auto measure Quota for all accounts every {intervalText} (DISABLED).\n• Left-click: Turn ON\n• Click arrow: Choose or customize interval"
                    : $"Tự động đo lại Quota cho toàn bộ tài khoản mỗi {intervalText} (ĐANG TẮT).\n• Nhấp chuột trái: Bật ngay\n• Nhấp mũi tên: Chọn chu kỳ hoặc tùy chỉnh";
            }
        }

        private void BtnTogglePrivacyAll_Click(object sender, RoutedEventArgs e)
        {
            bool isAnyCurrentlyHidden = (_antigravityAccounts != null && _antigravityAccounts.Any(a => a.IsNameHidden || a.IsEmailHidden))
                                        || _config.HideAllNames || _config.HideAllEmails;
            bool target = !isAnyCurrentlyHidden;

            SetPrivacyAll(target, target);

            if (target)
            {
                ShowToast(LocalizationManager.IsEnglish ? "🔒 All account names and emails masked" : "🔒 Đã ẩn toàn bộ Tên và Email của tất cả tài khoản");
            }
            else
            {
                ShowToast(LocalizationManager.IsEnglish ? "👁 All account names and emails revealed" : "👁 Đã hiển thị lại đầy đủ Tên và Email");
            }
        }

        private void BtnPrivacy_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (BorderPrivacyToggle != null)
            {
                ShowPrivacyMenu(BorderPrivacyToggle);
            }
        }

        private void BorderPrivacyMenu_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement target)
            {
                ShowPrivacyMenu(target);
            }
        }

        private void ShowPrivacyMenu(FrameworkElement target)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);

            bool isEn = LocalizationManager.IsEnglish;

            var titleItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyMenuHeader"),
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            titleItem.SetResourceReference(MenuItem.ForegroundProperty, "ThemeTextSecondary");
            cm.Items.Add(titleItem);
            cm.Items.Add(new Separator());

            // 1. Ẩn tất cả Tên
            var hideNamesItem = new MenuItem
            {
                Header = (_config.HideAllNames ? "✓  " : "    ") + LocalizationManager.T("PrivacyHideAllNames"),
                FontWeight = _config.HideAllNames ? FontWeights.Bold : FontWeights.Normal
            };
            hideNamesItem.Click += (s, ev) =>
            {
                bool newHideNames = !_config.HideAllNames;
                SetPrivacyAll(newHideNames, _config.HideAllEmails);
                ShowToast(newHideNames 
                    ? (isEn ? "🔒 Hiding all account names" : "🔒 Đã ẩn tất cả Tên tài khoản") 
                    : (isEn ? "👁 Account names are visible" : "👁 Đã hiện lại Tên tài khoản"));
            };
            cm.Items.Add(hideNamesItem);

            // 2. Ẩn tất cả Email
            var hideEmailsItem = new MenuItem
            {
                Header = (_config.HideAllEmails ? "✓  " : "    ") + LocalizationManager.T("PrivacyHideAllEmails"),
                FontWeight = _config.HideAllEmails ? FontWeights.Bold : FontWeights.Normal
            };
            hideEmailsItem.Click += (s, ev) =>
            {
                bool newHideEmails = !_config.HideAllEmails;
                SetPrivacyAll(_config.HideAllNames, newHideEmails);
                ShowToast(newHideEmails 
                    ? (isEn ? "🔒 Hiding all account emails" : "🔒 Đã ẩn tất cả Email tài khoản") 
                    : (isEn ? "👁 Account emails are visible" : "👁 Đã hiện lại Email tài khoản"));
            };
            cm.Items.Add(hideEmailsItem);

            cm.Items.Add(new Separator());

            // 3. Ẩn cả Tên và Email
            var hideAllItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyHideAllBoth"),
                FontWeight = (_config.HideAllNames && _config.HideAllEmails) ? FontWeights.Bold : FontWeights.Normal
            };
            hideAllItem.Click += (s, ev) =>
            {
                SetPrivacyAll(true, true);
                ShowToast(isEn 
                    ? "🔒 Hiding both names and emails for all accounts" 
                    : "🔒 Đã ẩn cả Tên và Email của tất cả tài khoản");
            };
            cm.Items.Add(hideAllItem);

            // 4. Hiện lại tất cả
            var showAllItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyShowAll"),
                FontWeight = (!_config.HideAllNames && !_config.HideAllEmails) ? FontWeights.Bold : FontWeights.Normal
            };
            showAllItem.Click += (s, ev) =>
            {
                SetPrivacyAll(false, false);
                ShowToast(isEn 
                    ? "👁 All account information is now visible" 
                    : "👁 Đã hiển thị lại đầy đủ Tên và Email");
            };
            cm.Items.Add(showAllItem);

            cm.PlacementTarget = target;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void SetPrivacyAll(bool hideNames, bool hideEmails)
        {
            _config.HideAllNames = hideNames;
            _config.HideAllEmails = hideEmails;
            _config.Save();

            foreach (var acc in _antigravityAccounts)
            {
                acc.IsNameHidden = hideNames;
                acc.IsEmailHidden = hideEmails;
            }

            if (_activeAntigravityAccount != null)
            {
                _activeAntigravityAccount.IsNameHidden = hideNames;
                _activeAntigravityAccount.IsEmailHidden = hideEmails;
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
            }

            UpdatePrivacyUI();
        }

        private void UpdatePrivacyUI()
        {
            if (BorderPrivacyToggle == null || TxtPrivacyStatus == null || TxtPrivacyIcon == null) return;

            bool isAllHidden;
            bool isPartialHidden;

            if (_antigravityAccounts != null && _antigravityAccounts.Count > 0)
            {
                bool allNamesHidden = _antigravityAccounts.All(a => a.IsNameHidden);
                bool allEmailsHidden = _antigravityAccounts.All(a => a.IsEmailHidden);
                bool anyNamesHidden = _antigravityAccounts.Any(a => a.IsNameHidden);
                bool anyEmailsHidden = _antigravityAccounts.Any(a => a.IsEmailHidden);

                isAllHidden = allNamesHidden && allEmailsHidden;
                isPartialHidden = anyNamesHidden || anyEmailsHidden;

                if (isAllHidden)
                {
                    _config.HideAllNames = true;
                    _config.HideAllEmails = true;
                }
                else if (!isPartialHidden)
                {
                    _config.HideAllNames = false;
                    _config.HideAllEmails = false;
                }
            }
            else
            {
                isAllHidden = _config.HideAllNames && _config.HideAllEmails;
                isPartialHidden = _config.HideAllNames || _config.HideAllEmails;
            }

            if (isAllHidden)
            {
                TxtPrivacyIcon.Text = "🔒";
                TxtPrivacyStatus.Text = LocalizationManager.T("PrivacyAllHidden");
                TxtPrivacyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                BorderPrivacyToggle.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                BorderPrivacyToggle.ToolTip = LocalizationManager.IsEnglish
                    ? "Hiding all names and emails for all accounts.\n• Left-click: Reveal information\n• Click arrow: Privacy options"
                    : "Đang ẩn toàn bộ tên và email của tất cả tài khoản.\n• Nhấp chuột trái: Hiện lại thông tin\n• Nhấp mũi tên: Tùy chọn ẩn tên / email";
            }
            else if (isPartialHidden)
            {
                TxtPrivacyIcon.Text = "🛡";
                TxtPrivacyStatus.Text = _config.HideAllNames ? LocalizationManager.T("PrivacyNamesHidden") : LocalizationManager.T("PrivacyEmailsHidden");
                TxtPrivacyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                BorderPrivacyToggle.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                BorderPrivacyToggle.ToolTip = LocalizationManager.IsEnglish
                    ? $"Partially hiding {(_config.HideAllNames ? "all names" : "all emails")}.\n• Left-click: Hide all\n• Click arrow: Privacy options"
                    : $"Đang ẩn {(_config.HideAllNames ? "tất cả tên" : "tất cả email")}.\n• Nhấp chuột trái: Ẩn toàn bộ\n• Nhấp mũi tên: Tùy chọn ẩn tên / email";
            }
            else
            {
                TxtPrivacyIcon.Text = "👁";
                TxtPrivacyStatus.Text = LocalizationManager.T("PrivacyHideInfo");
                TxtPrivacyStatus.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
                BorderPrivacyToggle.SetResourceReference(Border.BorderBrushProperty, "ThemeSecondaryBtnBorder");
                BorderPrivacyToggle.ToolTip = LocalizationManager.T("PrivacyTip");
            }
        }

        private void BtnToggleChromePrivacyAll_Click(object sender, RoutedEventArgs e)
        {
            bool isAnyCurrentlyHidden = (_profiles != null && _profiles.Any(p => p.IsNameHidden || p.IsEmailHidden))
                                        || _config.HideChromeNames || _config.HideChromeEmails;
            bool target = !isAnyCurrentlyHidden;

            SetChromePrivacyAll(target, target);

            if (target)
            {
                ShowToast(LocalizationManager.IsEnglish 
                    ? "🔒 All Chrome profile names and emails are now hidden" 
                    : "🔒 Đã ẩn toàn bộ Tên và Email của tất cả profile Chrome");
            }
            else
            {
                ShowToast(LocalizationManager.IsEnglish 
                    ? "👁 Full profile names and emails are now visible" 
                    : "👁 Đã hiển thị lại đầy đủ Tên và Email Chrome");
            }
        }

        private void BtnChromePrivacy_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (BorderChromePrivacyToggle != null)
            {
                ShowChromePrivacyMenu(BorderChromePrivacyToggle);
            }
        }

        private void BorderChromePrivacyMenu_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement target)
            {
                ShowChromePrivacyMenu(target);
            }
        }

        private void ShowChromePrivacyMenu(FrameworkElement target)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);

            var titleItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyChromeMenuHeader"),
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            titleItem.SetResourceReference(MenuItem.ForegroundProperty, "ThemeTextSecondary");
            cm.Items.Add(titleItem);
            cm.Items.Add(new Separator());

            // 1. Ẩn tất cả Tên
            var hideNamesItem = new MenuItem
            {
                Header = (_config.HideChromeNames ? "✓  " : "    ") + LocalizationManager.T("PrivacyHideAllChromeNames"),
                FontWeight = _config.HideChromeNames ? FontWeights.Bold : FontWeights.Normal
            };
            hideNamesItem.Click += (s, ev) =>
            {
                bool newHideNames = !_config.HideChromeNames;
                SetChromePrivacyAll(newHideNames, _config.HideChromeEmails);
                ShowToast(newHideNames 
                    ? (LocalizationManager.IsEnglish ? "🔒 Hiding all profile names" : "🔒 Đã ẩn tất cả Tên profile Chrome") 
                    : (LocalizationManager.IsEnglish ? "👁 Profile names are visible" : "👁 Đã hiện lại Tên profile Chrome"));
            };
            cm.Items.Add(hideNamesItem);

            // 2. Ẩn tất cả Email
            var hideEmailsItem = new MenuItem
            {
                Header = (_config.HideChromeEmails ? "✓  " : "    ") + LocalizationManager.T("PrivacyHideAllChromeEmails"),
                FontWeight = _config.HideChromeEmails ? FontWeights.Bold : FontWeights.Normal
            };
            hideEmailsItem.Click += (s, ev) =>
            {
                bool newHideEmails = !_config.HideChromeEmails;
                SetChromePrivacyAll(_config.HideChromeNames, newHideEmails);
                ShowToast(newHideEmails 
                    ? (LocalizationManager.IsEnglish ? "🔒 Hiding all Google emails" : "🔒 Đã ẩn tất cả Email Google trong profile") 
                    : (LocalizationManager.IsEnglish ? "👁 Google emails are visible" : "👁 Đã hiện lại Email Google trong profile"));
            };
            cm.Items.Add(hideEmailsItem);

            cm.Items.Add(new Separator());

            // 3. Ẩn cả Tên và Email
            var hideAllItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyHideAllBoth"),
                FontWeight = (_config.HideChromeNames && _config.HideChromeEmails) ? FontWeights.Bold : FontWeights.Normal
            };
            hideAllItem.Click += (s, ev) =>
            {
                SetChromePrivacyAll(true, true);
                ShowToast(LocalizationManager.IsEnglish 
                    ? "🔒 Hiding both names and emails for all profiles" 
                    : "🔒 Đã ẩn cả Tên và Email của tất cả profile Chrome");
            };
            cm.Items.Add(hideAllItem);

            // 4. Hiện lại tất cả
            var showAllItem = new MenuItem
            {
                Header = LocalizationManager.T("PrivacyShowAll"),
                FontWeight = (!_config.HideChromeNames && !_config.HideChromeEmails) ? FontWeights.Bold : FontWeights.Normal
            };
            showAllItem.Click += (s, ev) =>
            {
                SetChromePrivacyAll(false, false);
                ShowToast(LocalizationManager.IsEnglish 
                    ? "👁 All profile information is now visible" 
                    : "👁 Đã hiển thị lại đầy đủ thông tin Chrome");
            };
            cm.Items.Add(showAllItem);

            cm.PlacementTarget = target;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void SetChromePrivacyAll(bool hideNames, bool hideEmails)
        {
            _config.HideChromeNames = hideNames;
            _config.HideChromeEmails = hideEmails;
            _config.Save();

            if (_profiles != null)
            {
                foreach (var p in _profiles)
                {
                    p.IsNameHidden = hideNames;
                    p.IsEmailHidden = hideEmails;
                }
            }

            if (_currentDefault != null)
            {
                _currentDefault.IsNameHidden = hideNames;
                _currentDefault.IsEmailHidden = hideEmails;
                UpdateHeroDefaultProfileDisplay(_currentDefault);
            }

            UpdateChromePrivacyUI();
        }

        private void UpdateChromePrivacyUI()
        {
            if (BorderChromePrivacyToggle == null || TxtChromePrivacyStatus == null || TxtChromePrivacyIcon == null) return;

            bool isAllHidden;
            bool isPartialHidden;

            if (_profiles != null && _profiles.Count > 0)
            {
                bool allNamesHidden = _profiles.All(p => p.IsNameHidden);
                bool allEmailsHidden = _profiles.All(p => p.IsEmailHidden);
                bool anyNamesHidden = _profiles.Any(p => p.IsNameHidden);
                bool anyEmailsHidden = _profiles.Any(p => p.IsEmailHidden);

                isAllHidden = allNamesHidden && allEmailsHidden;
                isPartialHidden = anyNamesHidden || anyEmailsHidden;

                if (isAllHidden)
                {
                    _config.HideChromeNames = true;
                    _config.HideChromeEmails = true;
                }
                else if (!isPartialHidden)
                {
                    _config.HideChromeNames = false;
                    _config.HideChromeEmails = false;
                }
            }
            else
            {
                isAllHidden = _config.HideChromeNames && _config.HideChromeEmails;
                isPartialHidden = _config.HideChromeNames || _config.HideChromeEmails;
            }

            if (isAllHidden)
            {
                TxtChromePrivacyIcon.Text = "🔒";
                TxtChromePrivacyStatus.Text = LocalizationManager.T("PrivacyAllHidden");
                TxtChromePrivacyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                BorderChromePrivacyToggle.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                BorderChromePrivacyToggle.ToolTip = LocalizationManager.IsEnglish
                    ? "Hiding all names and emails for all Chrome profiles.\n• Left-click: Reveal information\n• Click arrow: Privacy options"
                    : "Đang ẩn toàn bộ tên và email của tất cả profile Chrome.\n• Nhấp chuột trái: Hiện lại thông tin\n• Nhấp mũi tên: Tùy chọn ẩn tên / email";
            }
            else if (isPartialHidden)
            {
                TxtChromePrivacyIcon.Text = "🛡";
                TxtChromePrivacyStatus.Text = _config.HideChromeNames ? LocalizationManager.T("PrivacyNamesHidden") : LocalizationManager.T("PrivacyEmailsHidden");
                TxtChromePrivacyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                BorderChromePrivacyToggle.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
                BorderChromePrivacyToggle.ToolTip = LocalizationManager.IsEnglish
                    ? $"Partially hiding {(_config.HideChromeNames ? "all profile names" : "all Google emails")}.\n• Left-click: Hide all\n• Click arrow: Privacy options"
                    : $"Đang ẩn {(_config.HideChromeNames ? "tất cả tên profile" : "tất cả email Google")}.\n• Nhấp chuột trái: Ẩn toàn bộ\n• Nhấp mũi tên: Tùy chọn ẩn tên / email";
            }
            else
            {
                TxtChromePrivacyIcon.Text = "👁";
                TxtChromePrivacyStatus.Text = LocalizationManager.T("PrivacyHideInfo");
                TxtChromePrivacyStatus.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
                BorderChromePrivacyToggle.SetResourceReference(Border.BorderBrushProperty, "ThemeSecondaryBtnBorder");
                BorderChromePrivacyToggle.ToolTip = LocalizationManager.T("PrivacyChromeTip");
            }
        }

        private void TxtProfileName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement fe && fe.DataContext is ChromeProfileInfo profile)
            {
                profile.IsNameHidden = !profile.IsNameHidden;
                if (_currentDefault != null && _currentDefault.Id == profile.Id)
                {
                    _currentDefault.IsNameHidden = profile.IsNameHidden;
                    UpdateHeroDefaultProfileDisplay(_currentDefault);
                }
                UpdateChromePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtProfileEmail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement fe && fe.DataContext is ChromeProfileInfo profile)
            {
                profile.IsEmailHidden = !profile.IsEmailHidden;
                if (_currentDefault != null && _currentDefault.Id == profile.Id)
                {
                    _currentDefault.IsEmailHidden = profile.IsEmailHidden;
                    UpdateHeroDefaultProfileDisplay(_currentDefault);
                }
                UpdateChromePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtProfileEmail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChromeProfileInfo profile && !string.IsNullOrWhiteSpace(profile.UserName))
            {
                try
                {
                    Clipboard.SetText(profile.UserName);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {profile.UserName}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void TxtHeroDefaultProfileName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _currentDefault != null)
            {
                _currentDefault.IsNameHidden = !_currentDefault.IsNameHidden;
                var match = _profiles.FirstOrDefault(p => p.Id == _currentDefault.Id);
                if (match != null)
                {
                    match.IsNameHidden = _currentDefault.IsNameHidden;
                }
                UpdateHeroDefaultProfileDisplay(_currentDefault);
                UpdateChromePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtHeroDefaultProfileEmail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _currentDefault != null)
            {
                _currentDefault.IsEmailHidden = !_currentDefault.IsEmailHidden;
                var match = _profiles.FirstOrDefault(p => p.Id == _currentDefault.Id);
                if (match != null)
                {
                    match.IsEmailHidden = _currentDefault.IsEmailHidden;
                }
                UpdateHeroDefaultProfileDisplay(_currentDefault);
                UpdateChromePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtHeroDefaultProfileEmail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_currentDefault != null && !string.IsNullOrWhiteSpace(_currentDefault.UserName))
            {
                try
                {
                    Clipboard.SetText(_currentDefault.UserName);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {_currentDefault.UserName}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void BorderBatchConcurrency_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement target)
            {
                ShowBatchConcurrencyMenu(target);
            }
        }

        private void BtnBatchMeasure_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (BorderBatchConcurrency != null)
            {
                ShowBatchConcurrencyMenu(BorderBatchConcurrency);
            }
        }

        private void ShowBatchConcurrencyMenu(FrameworkElement target)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);

            int current = _config.BatchMeasureConcurrency > 0 ? _config.BatchMeasureConcurrency : 5;
            bool isEn = LocalizationManager.IsEnglish;

            var titleItem = new MenuItem
            {
                Header = isEn ? "⚡ PARALLEL MEASURE THREADS" : "⚡ SỐ LUỒNG ĐO QUOTA SONG SONG",
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            titleItem.SetResourceReference(MenuItem.ForegroundProperty, "ThemeTextSecondary");
            cm.Items.Add(titleItem);
            cm.Items.Add(new Separator());

            var options = isEn ? new (int Threads, string Label, string Desc)[]
            {
                (1, "1 thread", "Sequential per account (lowest CPU, safest)"),
                (2, "2 threads", "Light load (for low specs or slow network)"),
                (3, "3 threads", "Balanced speed and resource usage"),
                (5, "5 threads (Recommended)", "Optimized default for most PCs"),
                (8, "8 threads", "High speed for mid/high-end PCs"),
                (10, "10 threads", "Ultra fast (completes very quickly)"),
                (15, "15 threads (Maximum)", "Maximum parallel threads for all accounts")
            } : new (int Threads, string Label, string Desc)[]
            {
                (1, "1 luồng", "Tuần tự từng tài khoản (nhẹ máy nhất, an toàn tuyệt đối)"),
                (2, "2 luồng", "Nhẹ nhàng (phù hợp máy cấu hình thấp hoặc mạng yếu)"),
                (3, "3 luồng", "Cân bằng tốc độ và tài nguyên"),
                (5, "5 luồng (Khuyên dùng)", "Mặc định tối ưu cho hầu hết máy tính"),
                (8, "8 luồng", "Tốc độ nhanh cho máy cấu hình khá"),
                (10, "10 luồng", "Tốc độ cao (đo xong cực nhanh)"),
                (15, "15 luồng (Tối đa)", "Song song tối đa cho toàn bộ danh sách")
            };

            foreach (var opt in options)
            {
                bool isSelected = (current == opt.Threads);
                var mi = new MenuItem
                {
                    Header = (isSelected ? "✓  " : "    ") + opt.Label,
                    ToolTip = opt.Desc,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal
                };
                int capturedThreads = opt.Threads;
                mi.Click += (s, ev) =>
                {
                    _config.BatchMeasureConcurrency = capturedThreads;
                    _config.Save();

                    UpdateBatchConcurrencyUI();
                    ShowToast(isEn
                        ? $"✓ Concurrency set to: {capturedThreads} parallel threads"
                        : $"✓ Đã thiết lập số luồng đo Quota: {capturedThreads} luồng song song");
                };
                cm.Items.Add(mi);
            }

            cm.PlacementTarget = target;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void UpdateBatchConcurrencyUI()
        {
            if (TxtBatchConcurrency != null)
            {
                int val = _config.BatchMeasureConcurrency > 0 ? _config.BatchMeasureConcurrency : 5;
                TxtBatchConcurrency.Text = LocalizationManager.T("ThreadsFormat", val);
            }
        }

        private double GetAccountTotalQuotaScore(AntigravityAccountInfo a)
        {
            if (a.IsRestricted) return -1;
            double g5 = (a.Gemini5hPercent ?? a.GeminiWeeklyPercent) ?? 0;
            double gw = (a.GeminiWeeklyPercent ?? a.Gemini5hPercent) ?? 0;
            double c5 = (a.Claude5hPercent ?? a.ClaudeWeeklyPercent) ?? 0;
            double cw = (a.ClaudeWeeklyPercent ?? a.Claude5hPercent) ?? 0;

            return _currentQuotaScope switch
            {
                "5h" => (g5 + c5) / 2.0,
                "week" => (gw + cw) / 2.0,
                _ => (g5 + gw + c5 + cw) / 4.0
            };
        }

        private double GetAccountClaudeQuotaScore(AntigravityAccountInfo a)
        {
            if (a.IsRestricted) return -1;
            double c5 = (a.Claude5hPercent ?? a.ClaudeWeeklyPercent) ?? 0;
            double cw = (a.ClaudeWeeklyPercent ?? a.Claude5hPercent) ?? 0;

            return _currentQuotaScope switch
            {
                "5h" => c5,
                "week" => cw,
                _ => (c5 + cw) / 2.0
            };
        }

        private double GetAccountPro3QuotaScore(AntigravityAccountInfo a)
        {
            if (a.IsRestricted) return -1;
            if (_currentQuotaScope == "week")
                return a.GeminiWeeklyPercent ?? 0;
            int? p = a.GetModel5hPercent("gemini-3.1-pro-high", false) 
                  ?? a.GetModel5hPercent("gemini-3.1-pro-low", false)
                  ?? a.GetModel5hPercent("gemini-2.5-pro", false)
                  ?? a.Gemini5hPercent;
            return p ?? 0;
        }

        private double GetAccountFlashQuotaScore(AntigravityAccountInfo a)
        {
            if (a.IsRestricted) return -1;
            if (_currentQuotaScope == "week")
                return a.GeminiWeeklyPercent ?? 0;
            int? f = a.GetModel5hPercent("gemini-3-flash", false) 
                  ?? a.GetModel5hPercent("gemini-3.5-flash-low", false)
                  ?? a.GetModel5hPercent("gemini-3.5-flash-lite", false)
                  ?? a.Gemini5hPercent;
            return f ?? 0;
        }

        private void FilterAntigravityAccountsList()
        {
            string query = TxtSearchAntigravity?.Text?.Trim() ?? "";
            
            // 1. Tìm kiếm theo keyword
            var list = _antigravityAccounts.AsEnumerable();
            if (!string.IsNullOrEmpty(query))
            {
                list = list.Where(a =>
                    (!string.IsNullOrEmpty(a.Email) && a.Email.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(a.Name) && a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                );
            }

            // 2. Lọc theo gói đã chọn (Plan Filter)
            if (!string.IsNullOrEmpty(_selectedPlanFilter))
            {
                list = list.Where(a => string.Equals(a.PlanCategory, _selectedPlanFilter, StringComparison.OrdinalIgnoreCase));
            }

            // 3. Sắp xếp theo tiêu chí và chiều (Lớn ➔ Nhỏ hoặc Nhỏ ➔ Lớn)
            string currentRule = _config.RecommendationRule ?? "pro";

            if (!_isSortAscending)
            {
                list = _currentSortCriteria switch
                {
                    "recent" => list.OrderByDescending(a => a.LastUsed).ThenByDescending(a => a.CreatedAt),
                    "claude" => list.OrderByDescending(a => GetAccountClaudeQuotaScore(a)),
                    "pro3" => list.OrderByDescending(a => GetAccountPro3QuotaScore(a)),
                    "flash" => list.OrderByDescending(a => GetAccountFlashQuotaScore(a)),
                    _ => currentRule switch
                    {
                        "free" => list.OrderByDescending(a => string.Equals(a.PlanBadgeText, "FREE", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                                      .ThenByDescending(a => GetAccountTotalQuotaScore(a)),
                        "ultra" => list.OrderByDescending(a => (a.PlanBadgeText == "ULTRA" || a.PlanBadgeText == "ENTERPRISE") ? 1 : 0)
                                       .ThenByDescending(a => GetAccountTotalQuotaScore(a)),
                        "claude" => list.OrderByDescending(a => GetAccountClaudeQuotaScore(a)),
                        "gemini" => list.OrderByDescending(a => GetAccountFlashQuotaScore(a)),
                        _ => list.OrderByDescending(a => a.IsProOrHigher ? 1 : 0)
                                 .ThenByDescending(a => GetAccountTotalQuotaScore(a)) // Mặc định PRO
                    }
                };
            }
            else
            {
                list = _currentSortCriteria switch
                {
                    "recent" => list.OrderBy(a => a.LastUsed).ThenBy(a => a.CreatedAt),
                    "claude" => list.OrderBy(a => a.IsRestricted ? 9999 : GetAccountClaudeQuotaScore(a)),
                    "pro3" => list.OrderBy(a => a.IsRestricted ? 9999 : GetAccountPro3QuotaScore(a)),
                    "flash" => list.OrderBy(a => a.IsRestricted ? 9999 : GetAccountFlashQuotaScore(a)),
                    _ => list.OrderBy(a => a.IsRestricted ? 9999 : GetAccountTotalQuotaScore(a))
                };
            }

            var filtered = list.ToList();
            _currentFilteredAccounts = filtered;

            // Đồng bộ trạng thái phím chọn (giữ nguyên nếu index hợp lệ)
            if (_selectedAccountIndex >= filtered.Count)
            {
                _selectedAccountIndex = filtered.Count - 1;
            }

            for (int i = 0; i < filtered.Count; i++)
            {
                filtered[i].IsKeyboardSelected = (i == _selectedAccountIndex);
            }

            if (!ReferenceEquals(AntigravityItemsControl.ItemsSource, filtered))
            {
                AntigravityItemsControl.ItemsSource = filtered;
            }
            else
            {
                AntigravityItemsControl.Items.Refresh();
            }

            if (TxtAntigravityCount != null)
            {
                TxtAntigravityCount.Text = $" ({filtered.Count}/{_antigravityAccounts.Count})";
            }

            // Cập nhật text trên nút Filter Plan
            if (TxtPlanFilterTitle != null)
            {
                TxtPlanFilterTitle.Text = string.IsNullOrEmpty(_selectedPlanFilter) 
                    ? (LocalizationManager.IsEnglish ? "All Plans" : "Đặt lại tất cả") 
                    : _selectedPlanFilter;
            }

            // Cập nhật thẻ đề xuất tài khoản tiếp theo nên switch
            UpdateRecommendedAccountUI();
        }

        private void BtnPlanFilter_Click(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);
            cm.Padding = new Thickness(4);

            // Item 1: Tất cả gói (Đặt lại)
            var miReset = new MenuItem();
            bool isAllSelected = string.IsNullOrEmpty(_selectedPlanFilter);

            var gridReset = new Grid { MinWidth = 200 };
            gridReset.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            gridReset.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridReset.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var txtCheckReset = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")),
                Visibility = isAllSelected ? Visibility.Visible : Visibility.Hidden,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtCheckReset, 0);
            gridReset.Children.Add(txtCheckReset);

            var txtNameReset = new TextBlock
            {
                Text = LocalizationManager.IsEnglish ? "All Plans" : "Tất cả gói",
                FontSize = 12,
                FontWeight = isAllSelected ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            txtNameReset.SetResourceReference(TextBlock.ForegroundProperty, isAllSelected ? "ThemeTextPrimary" : "ThemeTextSecondary");
            Grid.SetColumn(txtNameReset, 1);
            gridReset.Children.Add(txtNameReset);

            var txtCountReset = new TextBlock
            {
                Text = _antigravityAccounts.Count.ToString(),
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(txtCountReset, 2);
            gridReset.Children.Add(txtCountReset);

            miReset.Header = gridReset;
            miReset.Click += (s, ev) =>
            {
                _selectedPlanFilter = null;
                FilterAntigravityAccountsList();
            };
            cm.Items.Add(miReset);

            cm.Items.Add(new Separator { Margin = new Thickness(4, 2, 4, 2), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")) });

            // Nhóm theo PlanCategory từ _antigravityAccounts
            var planGroups = _antigravityAccounts
                .GroupBy(a => a.PlanCategory)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var g in planGroups)
            {
                string planName = g.Key;
                int count = g.Count();
                bool isSelected = string.Equals(_selectedPlanFilter, planName, StringComparison.OrdinalIgnoreCase);

                var mi = new MenuItem();
                mi.Padding = new Thickness(8, 6, 12, 6);

                var grid = new Grid { MinWidth = 190 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtCheck = new TextBlock
                {
                    Text = "✓",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")),
                    Visibility = isSelected ? Visibility.Visible : Visibility.Hidden,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtCheck, 0);
                grid.Children.Add(txtCheck);

                var txtName = new TextBlock
                {
                    Text = planName,
                    FontSize = 12,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                txtName.SetResourceReference(TextBlock.ForegroundProperty, isSelected ? "ThemeTextPrimary" : "ThemeTextSecondary");
                Grid.SetColumn(txtName, 1);
                grid.Children.Add(txtName);

                var txtCount = new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0)
                };
                Grid.SetColumn(txtCount, 2);
                grid.Children.Add(txtCount);

                mi.Header = grid;
                mi.Click += (s, ev) =>
                {
                    _selectedPlanFilter = planName;
                    FilterAntigravityAccountsList();
                };

                cm.Items.Add(mi);
            }

            cm.PlacementTarget = BorderPlanFilter;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void BtnSortFilter_Click(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);
            cm.Padding = new Thickness(4);

            var sortItems = LocalizationManager.IsEnglish ? new (string Key, string Label)[]
            {
                ("recent", "Recently Used"),
                ("total", "Total Quota"),
                ("claude", "Claude Quota"),
                ("pro3", "Pro3 Quota"),
                ("flash", "Flash Quota")
            } : new (string Key, string Label)[]
            {
                ("recent", "Dùng gần đây"),
                ("total", "Quota tổng"),
                ("claude", "Quota Claude"),
                ("pro3", "Quota Pro3"),
                ("flash", "Quota Flash")
            };

            foreach (var item in sortItems)
            {
                bool isSelected = string.Equals(_currentSortCriteria, item.Key, StringComparison.OrdinalIgnoreCase);

                var mi = new MenuItem();
                mi.Padding = new Thickness(8, 6, 16, 6);

                var grid = new Grid { MinWidth = 150 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var txtCheck = new TextBlock
                {
                    Text = "✓",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")),
                    Visibility = isSelected ? Visibility.Visible : Visibility.Hidden,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtCheck, 0);
                grid.Children.Add(txtCheck);

                var txtLabel = new TextBlock
                {
                    Text = item.Label,
                    FontSize = 12,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                txtLabel.SetResourceReference(TextBlock.ForegroundProperty, isSelected ? "ThemeTextPrimary" : "ThemeTextSecondary");
                Grid.SetColumn(txtLabel, 1);
                grid.Children.Add(txtLabel);

                mi.Header = grid;
                mi.Click += (s, ev) =>
                {
                    _currentSortCriteria = item.Key;
                    UpdateSortDirectionUI();
                    FilterAntigravityAccountsList();
                };

                cm.Items.Add(mi);
            }

            cm.PlacementTarget = BorderSortFilter;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void UpdateSortDirectionUI()
        {
            if (TxtSortDirectionIcon == null || TxtSortDirectionLabel == null) return;
            TxtSortDirectionIcon.Text = _isSortAscending ? "↑" : "↓";
            string sortName = _currentSortCriteria switch
            {
                "claude" => "Claude",
                "pro3" => "Pro",
                "flash" => "Flash",
                "recent" => LocalizationManager.T("SortRecent"),
                _ => "Quota"
            };
            string orderText = _isSortAscending ? LocalizationManager.T("SortAsc") : LocalizationManager.T("SortDesc");
            TxtSortDirectionLabel.Text = $"{sortName}: {orderText}";
            if (BorderSortDirection != null)
            {
                BorderSortDirection.ToolTip = LocalizationManager.T("SortOrderTip");
            }
        }

        private void BtnSortDirection_Click(object sender, MouseButtonEventArgs e)
        {
            _isSortAscending = !_isSortAscending;
            UpdateSortDirectionUI();
            FilterAntigravityAccountsList();
        }

        private void UpdateRecommendedAccountUI()
        {
            if (BorderRecommendedAccount == null) return;

            string rule = _config.RecommendationRule ?? "pro";
            bool isEn = LocalizationManager.IsEnglish;

            // Update rule display name on button badge
            if (TxtRecRuleName != null)
            {
                TxtRecRuleName.Text = rule switch
                {
                    "free" => (isEn ? "Priority: FREE" : "Ưu tiên: FREE"),
                    "ultra" => (isEn ? "Priority: ULTRA" : "Ưu tiên: ULTRA"),
                    "claude" => (isEn ? "✳ Priority: CLAUDE" : "✳ Ưu tiên: CLAUDE"),
                    "gemini" => (isEn ? "✦ Priority: GEMINI" : "✦ Ưu tiên: GEMINI"),
                    _ => (isEn ? "Priority: PRO" : "Ưu tiên: PRO")
                };
            }

            if (_antigravityAccounts == null || _antigravityAccounts.Count <= 1)
            {
                BorderRecommendedAccount.Visibility = Visibility.Collapsed;
                _recommendedAccount = null;
                return;
            }

            // Lọc các ứng viên hợp lệ: không phải tài khoản đang dùng trong IDE, không bị restricted/forbidden
            var candidates = _antigravityAccounts.Where(a => 
                !a.IsActive && 
                !a.IsRestricted && 
                !a.IsForbidden).ToList();

            if (candidates.Count == 0)
            {
                BorderRecommendedAccount.Visibility = Visibility.Collapsed;
                _recommendedAccount = null;
                return;
            }

            AntigravityAccountInfo? best = null;

            if (rule == "free")
            {
                // Ưu tiên các tài khoản FREE (dùng trước để tiết kiệm Pro)
                var freeCandidates = candidates.Where(a => 
                    string.Equals(a.PlanBadgeText, "FREE", StringComparison.OrdinalIgnoreCase) || 
                    !a.IsProOrHigher).ToList();

                if (freeCandidates.Count > 0)
                {
                    best = freeCandidates
                        .OrderByDescending(a => (a.GeminiWeeklyPercent ?? 0) + (a.ClaudeWeeklyPercent ?? 0))
                        .ThenBy(a => a.LastUsed)
                        .FirstOrDefault();
                }
                else
                {
                    best = candidates.OrderByDescending(a => GetAccountTotalQuotaScore(a)).FirstOrDefault();
                }
            }
            else if (rule == "ultra")
            {
                // Ưu tiên tài khoản gói cao Ultra hoặc Enterprise
                var ultraCandidates = candidates.Where(a => 
                    string.Equals(a.PlanBadgeText, "ULTRA", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(a.PlanBadgeText, "ENTERPRISE", StringComparison.OrdinalIgnoreCase)).ToList();

                if (ultraCandidates.Count > 0)
                {
                    best = ultraCandidates.OrderByDescending(a => GetAccountTotalQuotaScore(a)).FirstOrDefault();
                }
                else
                {
                    best = candidates.Where(a => a.IsProOrHigher).OrderByDescending(a => GetAccountTotalQuotaScore(a)).FirstOrDefault()
                           ?? candidates.FirstOrDefault();
                }
            }
            else if (rule == "claude")
            {
                // Ưu tiên CLAUDE: tài khoản có quota Claude cao nhất (bất kể Pro hay Free)
                best = candidates
                    .OrderByDescending(a => GetAccountClaudeQuotaScore(a))
                    .ThenByDescending(a => a.ClaudeWeeklyPercent ?? 0)
                    .ThenBy(a => a.LastUsed)
                    .FirstOrDefault();
            }
            else if (rule == "gemini")
            {
                // Ưu tiên GEMINI: tài khoản có quota Gemini cao nhất (bất kể Pro hay Free)
                best = candidates
                    .OrderByDescending(a => (a.Gemini5hPercent ?? 0) * 2.0 + (a.GeminiWeeklyPercent ?? 0))
                    .ThenBy(a => a.LastUsed)
                    .FirstOrDefault();
            }
            else // "pro" (Mặc định)
            {
                // Ưu tiên PRO: chỉ chọn tài khoản PRO
                var proCandidates = candidates.Where(a => a.IsProOrHigher).ToList();
                if (proCandidates.Count > 0)
                {
                    best = proCandidates
                        .OrderByDescending(a => (a.Gemini5hPercent ?? 50) * 2.0 + (a.Claude5hPercent ?? 50) * 2.0 + (a.GeminiWeeklyPercent ?? 50) + (a.ClaudeWeeklyPercent ?? 50))
                        .ThenBy(a => a.LastUsed)
                        .FirstOrDefault();
                }
                else
                {
                    best = candidates.OrderByDescending(a => GetAccountTotalQuotaScore(a)).FirstOrDefault();
                }
            }

            if (best == null)
            {
                BorderRecommendedAccount.Visibility = Visibility.Collapsed;
                _recommendedAccount = null;
                return;
            }

            _recommendedAccount = best;
            BorderRecommendedAccount.Visibility = Visibility.Visible;
            if (BtnQuickSwitchRecommended != null)
            {
                BtnQuickSwitchRecommended.IsEnabled = !_isSwitching;
            }

            if (TxtRecInitial != null) TxtRecInitial.Text = best.InitialLetter;
            if (TxtRecName != null) TxtRecName.Text = best.DisplayNameMasked;
            if (TxtRecEmail != null) TxtRecEmail.Text = string.IsNullOrEmpty(best.Email) ? "" : best.EmailMasked;

            if (BorderRecPlanBadge != null && TxtRecPlanBadge != null)
            {
                if (best.IsProOrHigher)
                {
                    BorderRecPlanBadge.Visibility = Visibility.Visible;
                    BorderRecPlanBadge.Background = best.PlanBadgeBackground;
                    BorderRecPlanBadge.BorderBrush = best.PlanBadgeBorderBrush;
                    if (best.PlanBadgeText == "PRO")
                    {
                        BorderRecPlanBadge.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = (Color)ColorConverter.ConvertFromString("#EC4899"),
                            BlurRadius = 9,
                            ShadowDepth = 0,
                            Opacity = 0.65
                        };
                    }
                    else
                    {
                        BorderRecPlanBadge.Effect = null;
                    }
                    TxtRecPlanBadge.Foreground = best.PlanBadgeForeground;
                    TxtRecPlanBadge.Text = best.PlanBadgeDisplay;
                }
                else
                {
                    BorderRecPlanBadge.Visibility = Visibility.Collapsed;
                }
            }

            // Update Gemini Pro Ring for recommended account
            if (BorderRecProRing != null && BorderRecNormalRing != null && BorderRecInitialBg != null)
            {
                if (best.IsProOrHigher)
                {
                    BorderRecProRing.Visibility = Visibility.Visible;
                    BorderRecNormalRing.Visibility = Visibility.Collapsed;
                    BorderRecInitialBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00382B"));
                }
                else
                {
                    BorderRecProRing.Visibility = Visibility.Collapsed;
                    BorderRecNormalRing.Visibility = Visibility.Visible;
                    BorderRecInitialBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                }
            }

            // Quota summary badges
            string weekSuffix = isEn ? "(Week)" : "(Tuần)";
            var quotaParts = new List<string>();
            if (rule == "claude")
            {
                if (best.Claude5hPercent.HasValue) quotaParts.Add($"✳ Claude: {best.Claude5hPercent.Value}% (5h)");
                else if (best.ClaudeWeeklyPercent.HasValue) quotaParts.Add($"✳ Claude: {best.ClaudeWeeklyPercent.Value}% {weekSuffix}");

                if (best.Gemini5hPercent.HasValue) quotaParts.Add($"✦ Gemini: {best.Gemini5hPercent.Value}% (5h)");
                else if (best.GeminiWeeklyPercent.HasValue) quotaParts.Add($"✦ Gemini: {best.GeminiWeeklyPercent.Value}% {weekSuffix}");
            }
            else
            {
                if (best.Gemini5hPercent.HasValue) quotaParts.Add($"✦ Gemini: {best.Gemini5hPercent.Value}% (5h)");
                else if (best.GeminiWeeklyPercent.HasValue) quotaParts.Add($"✦ Gemini: {best.GeminiWeeklyPercent.Value}% {weekSuffix}");

                if (best.Claude5hPercent.HasValue) quotaParts.Add($"✳ Claude: {best.Claude5hPercent.Value}% (5h)");
                else if (best.ClaudeWeeklyPercent.HasValue) quotaParts.Add($"✳ Claude: {best.ClaudeWeeklyPercent.Value}% {weekSuffix}");
            }

            if (quotaParts.Count == 0) quotaParts.Add(isEn ? "Quota ready" : "Hạn mức sẵn sàng");

            if (TxtRecQuotaBadges != null)
            {
                TxtRecQuotaBadges.Text = string.Join(" • ", quotaParts);
                int avgQ = best.Gemini5hPercent ?? (best.GeminiWeeklyPercent ?? 50);
                TxtRecQuotaBadges.Foreground = avgQ > 50 
                    ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                    : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }

            if (TxtRecReason != null)
            {
                TxtRecReason.Text = rule switch
                {
                    "free" => (isEn ? "• Prioritizing FREE accounts to save PRO quota" : "• Đang ưu tiên tài khoản FREE để tiết kiệm gói PRO"),
                    "ultra" => (isEn ? "• Best ULTRA / ENTERPRISE plan account" : "• Tài khoản gói ULTRA / ENTERPRISE tốt nhất"),
                    "claude" => (isEn ? "• Account with highest CLAUDE / GPT quota for coding" : "• Tài khoản có hạn mức CLAUDE / GPT cao nhất để code"),
                    "gemini" => (isEn ? "• Account with highest GEMINI quota" : "• Tài khoản có hạn mức GEMINI cao nhất"),
                    _ => (isEn ? "• Premium PRO plan with optimal quota available" : "• Gói PRO cao cấp, hạn mức tối ưu nhất hiện có")
                };
            }
        }

        private void BorderRecRule_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var cm = new ContextMenu();
            cm.SetResourceReference(ContextMenu.BackgroundProperty, "ThemeContextMenuBg");
            cm.SetResourceReference(ContextMenu.BorderBrushProperty, "ThemeContextMenuBorder");
            cm.BorderThickness = new Thickness(1);

            string current = _config.RecommendationRule ?? "pro";
            bool isEn = LocalizationManager.IsEnglish;

            var rules = isEn ? new (string Key, string Label, string Desc)[]
            {
                ("pro", "👑 Priority: PRO", "Prioritize PRO accounts with highest quota"),
                ("free", "🆓 Priority: FREE", "Prioritize FREE accounts first to save PRO quota"),
                ("ultra", "⚡ Priority: ULTRA", "Prioritize high-tier Ultra / Enterprise accounts"),
                ("claude", "✳ Priority: CLAUDE", "Recommend account with highest Claude / GPT quota"),
                ("gemini", "✦ Priority: GEMINI", "Recommend account with highest Gemini quota")
            } : new (string Key, string Label, string Desc)[]
            {
                ("pro", "👑 Ưu tiên PRO", "Ưu tiên tài khoản PRO có hạn mức cao nhất"),
                ("free", "🆓 Ưu tiên FREE", "Ưu tiên tài khoản FREE (dùng trước để tiết kiệm Pro)"),
                ("ultra", "⚡ Ưu tiên ULTRA", "Ưu tiên tài khoản gói cao Ultra / Enterprise"),
                ("claude", "✳ Ưu tiên CLAUDE", "Đề xuất tài khoản có Quota Claude / GPT cao nhất"),
                ("gemini", "✦ Ưu tiên GEMINI", "Đề xuất tài khoản có Quota Gemini cao nhất")
            };

            foreach (var rule in rules)
            {
                bool isSelected = string.Equals(current, rule.Key, StringComparison.OrdinalIgnoreCase);
                var mi = new MenuItem
                {
                    Header = (isSelected ? "✓  " : "    ") + rule.Label,
                    ToolTip = rule.Desc,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal
                };
                string capturedKey = rule.Key;
                string capturedLabel = rule.Label;
                mi.Click += (s, ev) =>
                {
                    _config.RecommendationRule = capturedKey;
                    _config.Save();

                    FilterAntigravityAccountsList();
                    UpdateSortDirectionUI();
                    ShowToast(isEn ? $"✓ Switched priority: {capturedLabel}" : $"✓ Đã chuyển ưu tiên: {capturedLabel}");
                };
                cm.Items.Add(mi);
            }

            cm.IsOpen = true;
        }

        // ====================================================================
        // KEYBOARD NAVIGATION (Arrows Up/Down, Left/Right, Enter, Space)
        // ====================================================================
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Close About Modal on Escape
            if (AboutModalOverlay?.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    HideAboutModal();
                    e.Handled = true;
                    return;
                }
            }

            // --- TAB CHROME PROFILES ---
            if (ViewChrome?.Visibility == Visibility.Visible)
            {
                if (TxtSearchProfile?.IsFocused == true)
                {
                    if (e.Key == Key.Down && _currentFilteredProfiles.Count > 0)
                    {
                        Keyboard.ClearFocus();
                        SelectProfileIndex(0);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Enter)
                    {
                        _profileSearchDebounceTimer.Stop();
                        ApplyProfileFilter();
                        if (_currentFilteredProfiles.Count > 0)
                        {
                            Keyboard.ClearFocus();
                            SelectProfileIndex(0);
                        }
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Escape)
                    {
                        TxtSearchProfile.Text = string.Empty;
                        Keyboard.ClearFocus();
                        e.Handled = true;
                        return;
                    }
                    return;
                }

                switch (e.Key)
                {
                    case Key.Down:
                        MoveProfileSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        MoveProfileSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        MoveProfileActionSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Left:
                        MoveProfileActionSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                    case Key.Space:
                        ExecuteCurrentProfileAction();
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        ClearKeyboardProfileSelection();
                        e.Handled = true;
                        break;
                }
                return;
            }

            // --- TAB ANTIGRAVITY ACCOUNTS ---
            if (ViewAntigravity?.Visibility == Visibility.Visible)
            {
                if (TxtSearchAntigravity?.IsFocused == true)
                {
                    if (e.Key == Key.Down && _currentFilteredAccounts.Count > 0)
                    {
                        Keyboard.ClearFocus();
                        SelectAccountIndex(0);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Enter)
                    {
                        _antigravitySearchDebounceTimer.Stop();
                        FilterAntigravityAccountsList();
                        if (_currentFilteredAccounts.Count > 0)
                        {
                            Keyboard.ClearFocus();
                            SelectAccountIndex(0);
                        }
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Escape)
                    {
                        TxtSearchAntigravity.Text = string.Empty;
                        Keyboard.ClearFocus();
                        e.Handled = true;
                        return;
                    }
                    return;
                }

                switch (e.Key)
                {
                    case Key.Down:
                        MoveAccountSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        MoveAccountSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        MoveActionSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Left:
                        MoveActionSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                    case Key.Space:
                        ExecuteCurrentActionSelection();
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        ClearKeyboardAccountSelection();
                        e.Handled = true;
                        break;
                }
            }
        }

        // --- ANTIGRAVITY SELECTION METHODS ---
        private void MoveAccountSelection(int delta)
        {
            if (_currentFilteredAccounts == null || _currentFilteredAccounts.Count == 0) return;

            if (_selectedAccountIndex < 0)
            {
                _selectedAccountIndex = delta > 0 ? 0 : _currentFilteredAccounts.Count - 1;
            }
            else
            {
                _selectedAccountIndex += delta;
                if (_selectedAccountIndex < 0) _selectedAccountIndex = _currentFilteredAccounts.Count - 1;
                if (_selectedAccountIndex >= _currentFilteredAccounts.Count) _selectedAccountIndex = 0;
            }

            SelectAccountIndex(_selectedAccountIndex);
        }

        private void SelectAccountIndex(int index)
        {
            if (_currentFilteredAccounts == null || _currentFilteredAccounts.Count == 0) return;
            if (index < 0 || index >= _currentFilteredAccounts.Count) return;

            _selectedAccountIndex = index;

            for (int i = 0; i < _currentFilteredAccounts.Count; i++)
            {
                var acc = _currentFilteredAccounts[i];
                bool isSelected = (i == index);
                if (acc.IsKeyboardSelected != isSelected)
                {
                    acc.IsKeyboardSelected = isSelected;
                }
            }

            ScrollAccountIndexIntoView(index);
        }

        private void MoveActionSelection(int delta)
        {
            if (_selectedAccountIndex < 0 || _selectedAccountIndex >= _currentFilteredAccounts.Count)
            {
                if (_currentFilteredAccounts.Count > 0) SelectAccountIndex(0);
                return;
            }

            var acc = _currentFilteredAccounts[_selectedAccountIndex];
            // 0: Switch, 1: Refresh, 2: 20 Model, 3: Delete
            int next = (acc.FocusedActionIndex + delta) % 4;
            if (next < 0) next += 4;
            acc.FocusedActionIndex = next;
        }

        private void ExecuteCurrentActionSelection()
        {
            if (_selectedAccountIndex < 0 || _selectedAccountIndex >= _currentFilteredAccounts.Count) return;

            var acc = _currentFilteredAccounts[_selectedAccountIndex];
            switch (acc.FocusedActionIndex)
            {
                case 0: // Switch
                    if (!acc.IsActive)
                    {
                        if (_isSwitching)
                        {
                            ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                            return;
                        }
                        _ = ExecuteSwitchToAccountAsync(acc, null);
                    }
                    else
                    {
                        ShowToast(LocalizationManager.IsEnglish ? "ℹ This account is already active in IDE" : "ℹ Tài khoản này hiện đang được kích hoạt trong IDE");
                    }
                    break;

                case 1: // Refresh single account quota
                    TriggerRefreshSingleAccount(acc, null);
                    break;

                case 2: // View 20 Model
                    TriggerViewAccountQuota(acc);
                    break;

                case 3: // Delete account
                    TriggerDeleteAccount(acc);
                    break;
            }
        }

        private void ClearKeyboardAccountSelection()
        {
            _selectedAccountIndex = -1;
            foreach (var acc in _currentFilteredAccounts)
            {
                acc.IsKeyboardSelected = false;
            }
        }

        private void ScrollAccountIndexIntoView(int index)
        {
            try
            {
                if (ScrollAntigravityAccounts == null || _currentFilteredAccounts.Count == 0) return;

                if (index <= 0)
                {
                    ScrollAntigravityAccounts.ScrollToTop();
                    return;
                }

                if (index >= _currentFilteredAccounts.Count - 1)
                {
                    ScrollAntigravityAccounts.ScrollToBottom();
                    return;
                }

                AntigravityItemsControl.UpdateLayout();
                var container = AntigravityItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                if (container != null)
                {
                    container.BringIntoView();
                }
                else
                {
                    double ratio = (double)index / Math.Max(1, _currentFilteredAccounts.Count - 1);
                    ScrollAntigravityAccounts.ScrollToVerticalOffset(ratio * ScrollAntigravityAccounts.ScrollableHeight);
                }
            }
            catch { }
        }

        // --- CHROME PROFILES SELECTION METHODS ---
        private void MoveProfileSelection(int delta)
        {
            if (_currentFilteredProfiles == null || _currentFilteredProfiles.Count == 0) return;

            if (_selectedProfileIndex < 0)
            {
                _selectedProfileIndex = delta > 0 ? 0 : _currentFilteredProfiles.Count - 1;
            }
            else
            {
                _selectedProfileIndex += delta;
                if (_selectedProfileIndex < 0) _selectedProfileIndex = _currentFilteredProfiles.Count - 1;
                if (_selectedProfileIndex >= _currentFilteredProfiles.Count) _selectedProfileIndex = 0;
            }

            SelectProfileIndex(_selectedProfileIndex);
        }

        private void SelectProfileIndex(int index)
        {
            if (_currentFilteredProfiles == null || _currentFilteredProfiles.Count == 0) return;
            if (index < 0 || index >= _currentFilteredProfiles.Count) return;

            _selectedProfileIndex = index;

            for (int i = 0; i < _currentFilteredProfiles.Count; i++)
            {
                _currentFilteredProfiles[i].IsKeyboardSelected = (i == index);
            }

            ScrollProfileIndexIntoView(index);
        }

        private void MoveProfileActionSelection(int delta)
        {
            if (_selectedProfileIndex < 0 || _selectedProfileIndex >= _currentFilteredProfiles.Count)
            {
                if (_currentFilteredProfiles.Count > 0) SelectProfileIndex(0);
                return;
            }

            var p = _currentFilteredProfiles[_selectedProfileIndex];
            // 0: Set Default, 1: Open, 2: Shortcut
            int next = (p.FocusedActionIndex + delta) % 3;
            if (next < 0) next += 3;
            p.FocusedActionIndex = next;
        }

        private void ExecuteCurrentProfileAction()
        {
            if (_selectedProfileIndex < 0 || _selectedProfileIndex >= _currentFilteredProfiles.Count) return;
            var p = _currentFilteredProfiles[_selectedProfileIndex];
            switch (p.FocusedActionIndex)
            {
                case 0: // Set default
                    if (!p.IsDefault)
                    {
                        bool ok = ChromeProfileManager.SetDefaultProfile(
                            p.Id,
                            ChkShortcuts.IsChecked == true,
                            ChkRegistry.IsChecked == true,
                            out string msg);

                        if (ok)
                        {
                            _config.DefaultProfileId = p.Id;
                            _config.Save();
                            ReloadProfiles();
                            ShowToast(LocalizationManager.IsEnglish ? $"✓ Set '{p.Name}' as default!" : $"✓ Đã đặt '{p.Name}' làm mặc định!");
                        }
                        else
                        {
                            ShowAlertModal(LocalizationManager.IsEnglish ? "Configuration Error" : "Lỗi thiết lập", LocalizationManager.IsEnglish ? "An error occurred: " + msg : "Có lỗi xảy ra: " + msg, isError: true);
                        }
                    }
                    else
                    {
                        ShowToast(LocalizationManager.IsEnglish ? "ℹ This profile is already default" : "ℹ Profile này đã là mặc định");
                    }
                    break;

                case 1: // Open Chrome
                    ChromeProfileManager.LaunchChrome(p.Id);
                    ShowToast(LocalizationManager.IsEnglish ? $"🚀 Opening Profile: {p.Name}..." : $"🚀 Đang mở Profile: {p.Name}...");
                    break;

                case 2: // Desktop Shortcut
                    bool scOk = ChromeProfileManager.CreateDedicatedShortcut(p.Id, p.Name);
                    if (scOk) ShowToast(LocalizationManager.IsEnglish ? $"📌 Created shortcut for '{p.Name}' on Desktop!" : $"📌 Đã tạo phím tắt cho '{p.Name}' trên Desktop!");
                    else ShowAlertModal(LocalizationManager.IsEnglish ? "Shortcut Error" : "Lỗi tạo phím tắt", LocalizationManager.IsEnglish ? "Cannot create Desktop shortcut. Please check Administrator privileges." : "Không thể tạo phím tắt ra Desktop. Vui lòng kiểm tra quyền Administrator.", isError: true);
                    break;
            }
        }

        private void ClearKeyboardProfileSelection()
        {
            _selectedProfileIndex = -1;
            foreach (var p in _currentFilteredProfiles)
            {
                p.IsKeyboardSelected = false;
            }
        }

        private void ScrollProfileIndexIntoView(int index)
        {
            try
            {
                if (ScrollChromeProfiles == null || _currentFilteredProfiles.Count == 0) return;

                if (index <= 0)
                {
                    ScrollChromeProfiles.ScrollToTop();
                    return;
                }

                if (index >= _currentFilteredProfiles.Count - 1)
                {
                    ScrollChromeProfiles.ScrollToBottom();
                    return;
                }

                ProfilesItemsControl.UpdateLayout();
                var container = ProfilesItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                if (container != null)
                {
                    container.BringIntoView();
                }
                else
                {
                    double ratio = (double)index / Math.Max(1, _currentFilteredProfiles.Count - 1);
                    ScrollChromeProfiles.ScrollToVerticalOffset(ratio * ScrollChromeProfiles.ScrollableHeight);
                }
            }
            catch { }
        }

        private void TxtAccountName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement elem && elem.DataContext is AntigravityAccountInfo acc)
            {
                acc.IsNameHidden = !acc.IsNameHidden;
                if (_activeAntigravityAccount != null && string.Equals(_activeAntigravityAccount.Email, acc.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _activeAntigravityAccount.IsNameHidden = acc.IsNameHidden;
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void BtnCopyEmailBadge_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement elem && elem.DataContext is AntigravityAccountInfo acc)
            {
                acc.IsEmailHidden = !acc.IsEmailHidden;
                if (_activeAntigravityAccount != null && string.Equals(_activeAntigravityAccount.Email, acc.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _activeAntigravityAccount.IsEmailHidden = acc.IsEmailHidden;
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void BtnCopyEmailBadge_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is AntigravityAccountInfo acc && !string.IsNullOrEmpty(acc.Email))
            {
                try
                {
                    Clipboard.SetText(acc.Email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {acc.Email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void TxtHeroName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _activeAntigravityAccount != null)
            {
                _activeAntigravityAccount.IsNameHidden = !_activeAntigravityAccount.IsNameHidden;
                var match = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.IsNameHidden = _activeAntigravityAccount.IsNameHidden;
                }
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtHeroEmail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _activeAntigravityAccount != null)
            {
                _activeAntigravityAccount.IsEmailHidden = !_activeAntigravityAccount.IsEmailHidden;
                var match = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _activeAntigravityAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.IsEmailHidden = _activeAntigravityAccount.IsEmailHidden;
                }
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtHeroEmail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeAntigravityAccount != null && !string.IsNullOrEmpty(_activeAntigravityAccount.Email))
            {
                try
                {
                    Clipboard.SetText(_activeAntigravityAccount.Email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {_activeAntigravityAccount.Email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void TxtRecName_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _recommendedAccount != null)
            {
                _recommendedAccount.IsNameHidden = !_recommendedAccount.IsNameHidden;
                var match = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _recommendedAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (match != null) match.IsNameHidden = _recommendedAccount.IsNameHidden;
                if (_activeAntigravityAccount != null && string.Equals(_activeAntigravityAccount.Email, _recommendedAccount.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _activeAntigravityAccount.IsNameHidden = _recommendedAccount.IsNameHidden;
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtRecEmail_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _recommendedAccount != null)
            {
                _recommendedAccount.IsEmailHidden = !_recommendedAccount.IsEmailHidden;
                var match = _antigravityAccounts.FirstOrDefault(a => string.Equals(a.Email, _recommendedAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (match != null) match.IsEmailHidden = _recommendedAccount.IsEmailHidden;
                if (_activeAntigravityAccount != null && string.Equals(_activeAntigravityAccount.Email, _recommendedAccount.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _activeAntigravityAccount.IsEmailHidden = _recommendedAccount.IsEmailHidden;
                    UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
                }
                UpdateRecommendedAccountUI();
                UpdatePrivacyUI();
                e.Handled = true;
            }
        }

        private void TxtRecEmail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_recommendedAccount != null && !string.IsNullOrEmpty(_recommendedAccount.Email))
            {
                try
                {
                    Clipboard.SetText(_recommendedAccount.Email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {_recommendedAccount.Email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
                e.Handled = true;
            }
        }

        private void ApplyScopeChange()
        {
            AntigravityAccountInfo.CurrentScope = _currentQuotaScope;
            foreach (var a in _antigravityAccounts)
            {
                a.NotifyScopeChanged();
            }
            if (_activeAntigravityAccount != null)
            {
                _activeAntigravityAccount.NotifyScopeChanged();
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);
            }
            UpdateScopeButtonsUI();
            FilterAntigravityAccountsList();
        }

        private void BtnScopeBoth_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _currentQuotaScope = "both";
            ApplyScopeChange();
        }

        private void BtnScope5h_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _currentQuotaScope = "5h";
            ApplyScopeChange();
        }

        private void BtnScopeWeekly_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _currentQuotaScope = "week";
            ApplyScopeChange();
        }

        private void BtnTargetIde_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SetSwitchTarget("ide");
        }

        private void BtnTargetCli_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SetSwitchTarget("cli");
        }

        private void BtnTargetAntigravity_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SetSwitchTarget("antigravity");
        }

        private void SetSwitchTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) target = "ide";
            _config.AntigravitySwitchTarget = target.ToLowerInvariant();
            _config.Save();
            UpdateTargetSwitchUi();

            string targetName = _config.AntigravitySwitchTarget switch
            {
                "cli" => "Antigravity CLI (agy)",
                "antigravity" or "classic" => (LocalizationManager.IsEnglish ? "Antigravity (Original)" : "Antigravity (Bản gốc)"),
                _ => "Antigravity IDE (2.0)"
            };
            ShowToast(LocalizationManager.IsEnglish ? $"🎯 Switch target: {targetName}" : $"🎯 Mục tiêu switch: {targetName}");
        }

        private void UpdateTargetSwitchUi()
        {
            if (BtnTargetIde == null || BtnTargetCli == null || BtnTargetAntigravity == null) return;

            string target = (_config.AntigravitySwitchTarget ?? "ide").ToLowerInvariant();
            var activeBg = (Brush)(FindResource("ThemeNavActiveBg") ?? new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            var activeFg = (Brush)(FindResource("ThemeNavActiveFg") ?? new SolidColorBrush(Color.FromRgb(15, 23, 42)));
            var activeBorder = (Brush)(FindResource("ThemeNavActiveBorder") ?? new SolidColorBrush(Color.FromRgb(203, 213, 225)));
            var inactiveBg = Brushes.Transparent;
            var inactiveFg = (Brush)(FindResource("ThemeNavInactiveFg") ?? new SolidColorBrush(Color.FromRgb(100, 116, 139)));

            void StyleTargetBtn(Border btn, TextBlock txt, bool isActive)
            {
                btn.Background = isActive ? activeBg : inactiveBg;
                btn.BorderBrush = isActive ? activeBorder : Brushes.Transparent;
                btn.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
                btn.Effect = isActive ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = 0.08
                } : null;

                txt.Foreground = isActive ? activeFg : inactiveFg;
                txt.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            }

            bool isIde = target == "ide";
            bool isCli = target == "cli";
            bool isClassic = target == "antigravity" || target == "classic";

            StyleTargetBtn(BtnTargetIde, TxtTargetIde, isIde);
            StyleTargetBtn(BtnTargetCli, TxtTargetCli, isCli);
            StyleTargetBtn(BtnTargetAntigravity, TxtTargetAntigravity, isClassic);

            if (TxtHeroReloadIde != null)
            {
                if (isCli)
                {
                    TxtHeroReloadIde.Text = LocalizationManager.IsEnglish ? "Refresh CLI" : "Làm mới CLI";
                    BtnHeroReloadIde.ToolTip = LocalizationManager.IsEnglish ? "Refresh Language Server / CLI process" : "Làm mới tiến trình Language Server / CLI";
                }
                else if (isClassic)
                {
                    TxtHeroReloadIde.Text = LocalizationManager.IsEnglish ? "Reload App" : "Tải lại App";
                    BtnHeroReloadIde.ToolTip = LocalizationManager.IsEnglish ? "Restart Antigravity Classic" : "Khởi động lại Antigravity Classic";
                }
                else
                {
                    TxtHeroReloadIde.Text = LocalizationManager.T("ReloadIdeBtn");
                    BtnHeroReloadIde.ToolTip = LocalizationManager.T("ReloadIdeTip");
                }
            }
        }

        private void UpdateScopeButtonsUI()
        {
            UpdateTargetSwitchUi();

            if (BtnScopeBoth == null || BtnScope5h == null || BtnScopeWeekly == null) return;

            var activeBg = (Brush)(FindResource("ThemeNavActiveBg") ?? new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            var activeFg = (Brush)(FindResource("ThemeNavActiveFg") ?? new SolidColorBrush(Color.FromRgb(15, 23, 42)));
            var activeBorder = (Brush)(FindResource("ThemeNavActiveBorder") ?? new SolidColorBrush(Color.FromRgb(203, 213, 225)));
            var inactiveBg = Brushes.Transparent;
            var inactiveFg = (Brush)(FindResource("ThemeNavInactiveFg") ?? new SolidColorBrush(Color.FromRgb(100, 116, 139)));

            void StyleButton(Border btn, TextBlock txt, bool isActive)
            {
                btn.Background = isActive ? activeBg : inactiveBg;
                btn.BorderBrush = isActive ? activeBorder : Brushes.Transparent;
                btn.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
                btn.Effect = isActive ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = 0.08
                } : null;

                txt.Foreground = isActive ? activeFg : inactiveFg;
                txt.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            }

            if (TxtScopeBoth != null) TxtScopeBoth.Text = LocalizationManager.T("ScopeBoth");
            if (TxtScope5h != null) TxtScope5h.Text = LocalizationManager.T("Scope5h");
            if (TxtScopeWeekly != null) TxtScopeWeekly.Text = LocalizationManager.T("ScopeWeek");

            StyleButton(BtnScopeBoth, TxtScopeBoth, _currentQuotaScope == "both");
            StyleButton(BtnScope5h, TxtScope5h, _currentQuotaScope == "5h");
            StyleButton(BtnScopeWeekly, TxtScopeWeekly, _currentQuotaScope == "week");
        }


        private void TxtSearchAntigravity_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = TxtSearchAntigravity.Text ?? "";
            if (BtnClearAntigravitySearch != null)
            {
                BtnClearAntigravitySearch.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (string.IsNullOrEmpty(text))
            {
                _antigravitySearchDebounceTimer.Stop();
                FilterAntigravityAccountsList();
            }
            else
            {
                _antigravitySearchDebounceTimer.Stop();
                _antigravitySearchDebounceTimer.Start();
            }
        }

        private void BtnClearAntigravitySearch_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn)
            {
                btn.ReleaseMouseCapture();
            }
            Mouse.Capture(null);

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                TxtSearchAntigravity.Text = string.Empty;
                TxtSearchAntigravity.Focus();
            }));
        }

        private void SetSwitchInProgress(bool isSwitching, AntigravityAccountInfo? target = null)
        {
            _isSwitching = isSwitching;

            if (BtnHeroReloadIde != null)
            {
                BtnHeroReloadIde.IsEnabled = !isSwitching;
            }

            if (BtnQuickSwitchRecommended != null)
            {
                BtnQuickSwitchRecommended.IsEnabled = !isSwitching && _recommendedAccount != null;
            }

            if (_antigravityAccounts != null)
            {
                foreach (var acc in _antigravityAccounts)
                {
                    acc.IsSwitchingInProgress = isSwitching;
                    acc.IsTargetOfSwitch = isSwitching && target != null && string.Equals(acc.Email, target.Email, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private async void BtnSwitchAntigravityAccount_Click(object sender, RoutedEventArgs e)
        {
            if (_isSwitching)
            {
                ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                return;
            }
            if (sender is Button btn && btn.Tag is AntigravityAccountInfo target)
            {
                await ExecuteSwitchToAccountAsync(target, btn);
            }
        }

        private async void BtnQuickSwitchRecommended_Click(object sender, RoutedEventArgs e)
        {
            if (_isSwitching)
            {
                ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                return;
            }
            if (_recommendedAccount != null)
            {
                await ExecuteSwitchToAccountAsync(_recommendedAccount, BtnQuickSwitchRecommended);
            }
        }

        private async Task ExecuteSwitchToAccountAsync(AntigravityAccountInfo target, Button? btn = null)
        {
            if (_isSwitching)
            {
                ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                return;
            }

            SetSwitchInProgress(true, target);

            try
            {
                if (btn != null) btn.IsEnabled = false;

                // ═══ Auto-login if tokens are incomplete ═══════════════════
                if (!target.HasFullTokens)
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"🔑 Account '{target.Email}' does not have full tokens. Opening OAuth login..." : $"🔑 Account '{target.Email}' chưa có đủ tokens. Đang mở OAuth login...");

                    if (_oauthService != null)
                    {
                        try { _oauthCts?.Cancel(); _oauthService?.Cancel(); } catch { }
                        int waitCount = 0;
                        while (_oauthService != null && waitCount < 15) { await Task.Delay(100); waitCount++; }
                        await Task.Delay(150);
                    }

                    _oauthCts = new CancellationTokenSource();
                    _oauthService = new OAuthLoginService();
                    _oauthService.StatusChanged += msg => Dispatcher.Invoke(() => ShowToast(msg));

                    var loggedIn = await _oauthService.StartLoginAsync(_oauthCts.Token, target.Email);

                    if (loggedIn != null)
                    {
                        // Update target with new tokens
                        target.ApiKey = loggedIn.ApiKey;
                        target.RefreshToken = loggedIn.RefreshToken;
                        target.IdToken = loggedIn.IdToken;
                        target.ExpiryTimestamp = loggedIn.ExpiryTimestamp;
                        target.RawOauthToken = loggedIn.RawOauthToken;
                        target.RawUserStatus = loggedIn.RawUserStatus;

                        // Save updated tokens
                        var list = AntigravityProfileManager.LoadAllAccounts();
                        var existing = list.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.ApiKey = target.ApiKey;
                            existing.RefreshToken = target.RefreshToken;
                            existing.IdToken = target.IdToken;
                            existing.ExpiryTimestamp = target.ExpiryTimestamp;
                            existing.RawOauthToken = target.RawOauthToken;
                            existing.RawUserStatus = target.RawUserStatus;
                        }
                        AntigravityProfileManager.SaveAccountsList(list);
                        ShowToast(LocalizationManager.IsEnglish ? $"✓ Acquired tokens for '{target.EmailMasked}'! Switching..." : $"✓ Đã lấy tokens cho '{target.EmailMasked}'! Đang switch...");
                    }
                    else
                    {
                        ShowToast(LocalizationManager.IsEnglish ? "⚠️ OAuth login cancelled. Cannot switch." : "⚠️ OAuth login bị hủy. Không thể switch.");
                        return;
                    }

                    _oauthService = null;
                }

                // ═══ Auto-refresh token if expired or about to expire (< 3 min) ═══
                if (!string.IsNullOrEmpty(target.RefreshToken))
                {
                    bool isExpired = target.ExpiryTimestamp > 0 &&
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= (target.ExpiryTimestamp - 180);
                    if (isExpired || string.IsNullOrEmpty(target.ApiKey))
                    {
                        ShowToast(LocalizationManager.IsEnglish ? $"🔄 Automatically refreshing access token for '{target.EmailMasked}'..." : $"🔄 Đang tự động làm mới access token cho '{target.EmailMasked}'...");
                        var refreshed = await OAuthLoginService.RefreshAccessTokenAsync(target.RefreshToken);
                        if (refreshed != null)
                        {
                            target.ApiKey = refreshed.AccessToken;
                            target.ExpiryTimestamp = refreshed.ExpiresAt;
                            if (!string.IsNullOrEmpty(refreshed.IdToken)) target.IdToken = refreshed.IdToken;

                            var all = AntigravityProfileManager.LoadAllAccounts();
                            var cur = all.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                            if (cur != null)
                            {
                                cur.ApiKey = target.ApiKey;
                                cur.ExpiryTimestamp = target.ExpiryTimestamp;
                                if (!string.IsNullOrEmpty(target.IdToken)) cur.IdToken = target.IdToken;
                                AntigravityProfileManager.SaveAccountsList(all);
                            }
                        }
                    }
                }

                // ═══ Switch account ════════════════════════════════════════
                string switchTarget = _config.AntigravitySwitchTarget ?? "ide";
                string targetDisplayName = switchTarget switch
                {
                    "cli" => "Antigravity CLI",
                    "antigravity" or "classic" => (LocalizationManager.IsEnglish ? "Antigravity (Original)" : "Antigravity"),
                    _ => "Antigravity IDE"
                };

                ShowToast(LocalizationManager.IsEnglish ? $"⏳ Switching to '{target.EmailMasked}' ({targetDisplayName})..." : $"⏳ Đang switch sang '{target.EmailMasked}' ({targetDisplayName})...");

                bool success = false;
                string message = "";

                if (switchTarget == "cli")
                {
                    await System.Threading.Tasks.Task.Run(() =>
                        success = AntigravityProfileManager.SwitchAccountToCli(target, out message));
                }
                else if (switchTarget == "antigravity" || switchTarget == "classic")
                {
                    await System.Threading.Tasks.Task.Run(() =>
                        success = AntigravityProfileManager.SwitchAccountToClassic(target, out message));
                }
                else
                {
                    // "ide" (Mặc định)
                    await System.Threading.Tasks.Task.Run(() =>
                        success = AntigravityProfileManager.SilentSwitchAccount(target, out message));

                    if (success)
                    {
                        ShowToast(LocalizationManager.IsEnglish ? $"✓ '{target.Email}' — Applying to IDE..." : $"✓ '{target.Email}' — Đang áp dụng vào IDE...");
                        await System.Threading.Tasks.Task.Run(() =>
                            success = AntigravityProfileManager.ApplyAccountToIde(target, out message));
                    }
                }

                if (success)
                {
                    // ── Immediate ProxySwitcher UI update ──────────────────────────
                    target.IsNameHidden = _config.HideAllNames;
                    target.IsEmailHidden = _config.HideAllEmails;
                    TxtAntigravityHeroName.Text = target.DisplayNameMasked;
                    TxtAntigravityHeroEmail.Text = string.IsNullOrEmpty(target.Email)
                        ? (LocalizationManager.IsEnglish ? "Not synced" : "Chưa đồng bộ") : target.EmailMasked;
                    TxtAntigravityHeroInitial.Text = target.InitialLetter;

                    if (target.HasAvatar && target.AvatarSource != null)
                    {
                        ImgAntigravityHeroAvatar.ImageSource = target.AvatarSource;
                        BorderAntigravityHeroAvatar.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        BorderAntigravityHeroAvatar.Visibility = Visibility.Collapsed;
                    }

                    _activeAntigravityAccount = target;
                    foreach (var acc in _antigravityAccounts)
                        acc.IsActive = string.Equals(acc.Email, target.Email, StringComparison.OrdinalIgnoreCase);

                    FilterAntigravityAccountsList();
                    UpdateAntigravityRunningStatus();

                    ShowToast(message);

                    // Trigger live model quota refresh after switch
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(3500);
                        if (await AntigravityProfileManager.FetchLiveModelQuotaAsync(target))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                UpdateHeroModelQuotaDisplay(target);
                                AntigravityItemsControl.ItemsSource = null;
                                FilterAntigravityAccountsList();
                            });
                        }
                    });
                }
                else
                {
                    ShowAlertModal(LocalizationManager.IsEnglish ? "Switch Account Error" : "Lỗi chuyển tài khoản", message, isError: true);
                }
            }
            catch (Exception ex)
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Exception Error" : "Lỗi ngoại lệ", ex.Message, isError: true);
            }
            finally
            {
                SetSwitchInProgress(false, null);
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void BtnHeroReloadIde_Click(object sender, RoutedEventArgs e)
        {
            if (_isSwitching)
            {
                ShowToast(LocalizationManager.IsEnglish ? "⏳ An account switch is in progress, please wait..." : "⏳ Đang có tiến trình chuyển tài khoản, vui lòng chờ...");
                return;
            }

            SetSwitchInProgress(true, null);
            try
            {
                string switchTarget = (_config.AntigravitySwitchTarget ?? "ide").ToLowerInvariant();
                if (switchTarget == "cli")
                {
                    ShowToast(LocalizationManager.IsEnglish ? "⏳ Refreshing Antigravity CLI token..." : "⏳ Đang làm mới token Antigravity CLI...");
                    var activeAcc = AntigravityProfileManager.GetCurrentActiveAccount() 
                        ?? AntigravityProfileManager.LoadAllAccounts().FirstOrDefault(a => a.IsActive);
                    if (activeAcc != null)
                    {
                        AntigravityProfileManager.SyncCliAndGoogleTokens(activeAcc);
                    }
                    ShowToast(LocalizationManager.IsEnglish ? "✓ Token synced for Antigravity CLI (agy)!" : "✓ Đã đồng bộ token cho Antigravity CLI (agy)!");
                    return;
                }
                else if (switchTarget == "antigravity" || switchTarget == "classic")
                {
                    ShowToast(LocalizationManager.IsEnglish ? "⏳ Restarting Antigravity..." : "⏳ Đang khởi động lại Antigravity...");
                    string? exePath = AntigravityProfileManager.FindAntigravityClassicExePath();
                    var procs = Process.GetProcessesByName("Antigravity");
                    foreach (var p in procs) { try { p.Kill(); } catch { } }
                    await Task.Delay(300);
                    if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                        ShowToast(LocalizationManager.IsEnglish ? "✓ Antigravity restarted!" : "✓ Đã khởi động lại Antigravity!");
                    }
                    else
                    {
                        ShowToast(LocalizationManager.IsEnglish ? "⚠️ Antigravity exe not found. Please open manually." : "⚠️ Không tìm thấy exe Antigravity. Vui lòng mở thủ công.");
                    }
                    return;
                }

                ShowToast(LocalizationManager.IsEnglish ? "⏳ Restarting Antigravity IDE..." : "⏳ Đang khởi động lại Antigravity IDE...");
                bool ok = false;
                await System.Threading.Tasks.Task.Run(() =>
                    ok = AntigravityProfileManager.KillAndRestartAntigravityIde());
                if (ok)
                {
                    ShowToast(LocalizationManager.IsEnglish ? "✓ Restarted Antigravity IDE with active account!" : "✓ Đã khởi động lại Antigravity IDE với account hiện tại!");
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? "⚠️ IDE exe not found. Please open Antigravity IDE manually." : "⚠️ Không tìm thấy exe IDE. Vui lòng mở lại Antigravity IDE thủ công.");
                }
            }
            finally
            {
                SetSwitchInProgress(false, null);
            }
        }


        private void BtnCaptureActiveAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var captured = AntigravityProfileManager.CaptureCurrentAccount();
                if (captured != null)
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Saved account '{captured.Email}' to managed list!" : $"✓ Đã lưu tài khoản '{captured.Email}' vào danh sách quản lý!");
                    ReloadAntigravityAccounts();
                }
                else
                {
                    ShowAlertModal(LocalizationManager.IsEnglish ? "Notice" : "Thông báo", LocalizationManager.IsEnglish ? "Valid login session not found in Antigravity IDE state.vscdb." : "Không tìm thấy phiên đăng nhập hợp lệ trong state.vscdb của Antigravity IDE.", isError: false);
                }
            }
            catch (Exception ex)
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Error Saving Account" : "Lỗi lưu tài khoản", ex.Message, isError: true);
            }
        }

        private void BtnImportAntigravityTools_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int count = AntigravityProfileManager.ImportFromAntigravityTools();
                if (count > 0)
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Successfully imported {count} accounts from Antigravity Tools!" : $"✓ Đã import thành công {count} tài khoản từ Antigravity Tools!");
                    ReloadAntigravityAccounts();
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? "No new accounts found in Antigravity Tools directory." : "Không tìm thấy tài khoản mới nào trong thư mục Antigravity Tools.");
                }
            }
            catch (Exception ex)
            {
                ShowAlertModal(LocalizationManager.IsEnglish ? "Import Error" : "Lỗi import", ex.Message, isError: true);
            }
        }

        private void BtnCopyAntigravityEmail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string email && !string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    Clipboard.SetText(email);
                    ShowToast($"✓ {(LocalizationManager.IsEnglish ? "Copied" : "Đã sao chép")}: {email}");
                }
                catch
                {
                    ShowToast(LocalizationManager.IsEnglish ? "Cannot copy to clipboard." : "Không thể sao chép vào bộ nhớ đệm.");
                }
            }
        }

        private void BtnDeleteAntigravityAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AntigravityAccountInfo target)
            {
                TriggerDeleteAccount(target);
            }
        }

        private void TriggerDeleteAccount(AntigravityAccountInfo target)
        {
            ShowConfirmModal(
                LocalizationManager.IsEnglish ? "Delete Account" : "Xóa tài khoản",
                LocalizationManager.IsEnglish 
                    ? $"Are you sure you want to remove account '{target.DisplayName}' ({target.Email}) from the managed list?\n\n(Note: This only removes it from the manager, it does not affect your Google account)." 
                    : $"Bạn có chắc chắn muốn xóa tài khoản '{target.DisplayName}' ({target.Email}) khỏi danh sách quản lý không?\n\n(Lưu ý: Thao tác này chỉ xóa khỏi danh sách quản lý, không ảnh hưởng đến tài khoản Google của bạn).",
                LocalizationManager.IsEnglish ? "Delete" : "Đồng ý xóa",
                () =>
                {
                    var list = AntigravityProfileManager.LoadAllAccounts();
                    list.RemoveAll(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                    AntigravityProfileManager.SaveAccountsList(list);
                    ShowToast(LocalizationManager.IsEnglish ? $"✓ Removed account {target.Email}" : $"✓ Đã xóa tài khoản {target.Email}");
                    ReloadAntigravityAccounts();
                },
                isDanger: true);
        }

        private void BtnRefreshAntigravity_Click(object sender, RoutedEventArgs e)
        {
            ReloadAntigravityAccounts();
            UpdateAntigravityRunningStatus();
            ShowToast(LocalizationManager.IsEnglish ? "✓ Antigravity account data refreshed" : "✓ Đã làm mới dữ liệu tài khoản Antigravity");
        }

        private async void BtnRestoreChatHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (procCount, _, _) = AntigravityProfileManager.GetAntigravityProcessStats();
                bool wasRunning = procCount > 0;

                if (wasRunning)
                {
                    var mbr = MessageBox.Show(
                        LocalizationManager.T("RestoreChatConfirm"),
                        LocalizationManager.IsEnglish ? "Restore Chat History" : "Khôi phục lịch sử chat",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (mbr != MessageBoxResult.Yes) return;
                }

                if (BtnRestoreChatHistory != null) BtnRestoreChatHistory.IsEnabled = false;
                ShowToast(LocalizationManager.IsEnglish ? "⏳ Restoring chat history..." : "⏳ Đang khôi phục lịch sử chat...");

                AntigravityChatRestorer.RestoreResult? res = null;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    // Always kill lingering/crashed Antigravity and helper processes to ensure SQLite DB locks are freed
                    AntigravityProfileManager.KillAllAntigravityProcesses();
                    System.Threading.Thread.Sleep(wasRunning ? 1200 : 400);

                    res = AntigravityChatRestorer.RestoreConversations(forceCloseIde: false);

                    if (wasRunning)
                    {
                        AntigravityProfileManager.TriggerSeamlessWindowReload();
                    }
                });

                if (res != null && res.Success)
                {
                    string msg = LocalizationManager.T("RestoreChatSuccess", res.TotalRestoredCount, res.WorkspacesCount);
                    ShowToast(msg);
                    ShowAlertModal(
                        LocalizationManager.IsEnglish ? "Chat History Restored" : "Khôi phục thành công",
                        msg + (LocalizationManager.IsEnglish
                            ? "\n\nAll conversations have been re-indexed into your sidebar and conversation history!"
                            : "\n\nToàn bộ lịch sử chat đã được ánh xạ lại vào sidebar và lịch sử hội thoại của Antigravity!"),
                        isError: false);
                }
                else
                {
                    ShowToast(res?.Message ?? (LocalizationManager.IsEnglish ? "Unknown error" : "Lỗi không xác định"));
                }
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationManager.IsEnglish ? $"Restore error: {ex.Message}" : $"Lỗi khôi phục: {ex.Message}");
            }
            finally
            {
                if (BtnRestoreChatHistory != null) BtnRestoreChatHistory.IsEnabled = true;
                UpdateAntigravityRunningStatus();
            }
        }

        // ====================================================================
        // OAUTH BROWSER LOGIN
        // ====================================================================
        private OAuthLoginService? _oauthService;
        private CancellationTokenSource? _oauthCts;

        /// <summary>
        /// Opens browser for OAuth login (same flow as lbjlaq/Antigravity-Manager).
        /// After login, account is saved and appears in the list for silent switching.
        /// </summary>
        private async void BtnOAuthLoginAntigravity_Click(object sender, RoutedEventArgs e)
        {
            if (_oauthService != null)
            {
                ShowToast(LocalizationManager.IsEnglish ? "Stopping previous login session to start a new one..." : "Đang dừng phiên đăng nhập cũ để bắt đầu phiên mới...");
                try
                {
                    _oauthCts?.Cancel();
                    _oauthService?.Cancel();
                    if (GoogleOAuthAutoConsentService.LastOAuthHwnd != IntPtr.Zero)
                    {
                        GoogleOAuthAutoConsentService.CloseOAuthTab(GoogleOAuthAutoConsentService.LastOAuthHwnd);
                    }
                }
                catch { }

                int waitLimit = 0;
                while (_oauthService != null && waitLimit < 15)
                {
                    await Task.Delay(100);
                    waitLimit++;
                }
                await Task.Delay(150);
            }

            // Check if port 8888 is currently occupied
            int? pid = OAuthLoginService.GetProcessIdUsingPort(OAuthLoginService.DefaultPort);
            if (pid.HasValue && pid.Value != Process.GetCurrentProcess().Id)
            {
                string procName = "Unknown";
                try { procName = Process.GetProcessById(pid.Value).ProcessName; } catch { }

                if (procName.Equals("antigravity-manager", StringComparison.OrdinalIgnoreCase))
                {
                    ShowConfirmModal(
                        LocalizationManager.T("OAuthPortConflict"),
                        LocalizationManager.T("OAuthPortConflictDesc"),
                        LocalizationManager.T("OAuthCloseAndLogin"),
                        () =>
                        {
                            try
                            {
                                foreach (var p in Process.GetProcessesByName("antigravity-manager"))
                                {
                                    try { p.Kill(); } catch { }
                                }
                                System.Threading.Thread.Sleep(500);
                            }
                            catch { }
                            _ = ExecuteOAuthLoginAsync();
                        },
                        isDanger: false);
                    return;
                }
                else
                {
                    ShowAlertModal(
                        LocalizationManager.T("OAuthPortBusy"),
                        string.Format(LocalizationManager.T("OAuthPortBusyDesc"), procName, pid.Value),
                        isError: true);
                    return;
                }
            }

            await ExecuteOAuthLoginAsync();
        }

        private async Task ExecuteOAuthLoginAsync(string? loginHint = null, string? chromeProfileId = null)
        {
            if (BtnOAuthLogin != null)
            {
                BtnOAuthLogin.IsEnabled = false;
                BtnOAuthLogin.Content = LocalizationManager.T("OAuthWaiting");
            }
            if (BtnCancelOAuthLogin != null)
            {
                BtnCancelOAuthLogin.Visibility = Visibility.Visible;
            }
            if (BtnCancelSyncInModal != null)
            {
                BtnCancelSyncInModal.Visibility = Visibility.Visible;
            }

            _oauthCts = new CancellationTokenSource();
            _oauthService = new OAuthLoginService();

            // Show status updates in a toast
            _oauthService.StatusChanged += msg => Dispatcher.Invoke(() =>
            {
                ShowToast(msg);
                if (TxtBatchSyncProgress != null && BorderBatchSyncProgress != null && BorderBatchSyncProgress.Visibility == Visibility.Visible)
                {
                    TxtBatchSyncProgress.Text = msg;
                }
            });

            try
            {
                if (!string.IsNullOrWhiteSpace(chromeProfileId))
                {
                    ShowToast(LocalizationManager.IsEnglish ? $"⚡ Opening Chrome Profile '{chromeProfileId}' to sync '{loginHint}'..." : $"⚡ Đang mở Chrome Profile '{chromeProfileId}' để sync '{loginHint}'...");
                }
                else
                {
                    ShowToast(LocalizationManager.IsEnglish ? "🌐 Opening browser for sign-in..." : "🌐 Đang mở trình duyệt đăng nhập...");
                }

                var account = await _oauthService.StartLoginAsync(_oauthCts.Token, loginHint, chromeProfileId);

                if (account != null)
                {
                    account.IsNameHidden = _config.HideAllNames;
                    account.IsEmailHidden = _config.HideAllEmails;

                    // Save to local accounts list
                    var list = AntigravityProfileManager.LoadAllAccounts();
                    bool exists = list.Any(a => string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        list.Add(account);
                        AntigravityProfileManager.SaveAccountsList(list);
                        ShowToast(LocalizationManager.IsEnglish ? $"✓ Synced account '{account.EmailMasked}' to Antigravity!" : $"✓ Đã đồng bộ tài khoản '{account.EmailMasked}' vào Antigravity!");
                    }
                    else
                    {
                        // Update existing account's tokens
                        var existing = list.First(a => string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase));
                        existing.ApiKey = account.ApiKey;
                        existing.RefreshToken = account.RefreshToken;
                        existing.ExpiryTimestamp = account.ExpiryTimestamp;
                        existing.IdToken = account.IdToken;
                        existing.RawOauthToken = account.RawOauthToken;
                        existing.LastUsed = DateTime.Now;
                        existing.IsNameHidden = _config.HideAllNames;
                        existing.IsEmailHidden = _config.HideAllEmails;
                        AntigravityProfileManager.SaveAccountsList(list);
                        ShowToast(LocalizationManager.IsEnglish ? $"✓ Updated Antigravity token for '{account.EmailMasked}'!" : $"✓ Đã cập nhật token Antigravity cho '{account.EmailMasked}'!");
                    }

                    ReloadAntigravityAccounts();

                    // If modal is currently open, refresh account buttons
                    if (AccountsModalOverlay.Visibility == Visibility.Visible && _currentViewingAccountsProfile != null)
                    {
                        OpenAccountsModal(_currentViewingAccountsProfile);
                    }
                }
                else
                {
                    ShowToast(LocalizationManager.T("OAuthCancelled"));
                }
            }
            catch (Exception ex)
            {
                ShowAlertModal("Lỗi đăng nhập OAuth", ex.Message, isError: true);
            }
            finally
            {
                if (BtnOAuthLogin != null)
                {
                    BtnOAuthLogin.IsEnabled = true;
                    BtnOAuthLogin.Content = LocalizationManager.T("OAuthLogin");
                }
                if (BtnCancelOAuthLogin != null)
                {
                    BtnCancelOAuthLogin.Visibility = Visibility.Collapsed;
                }
                if (BtnCancelSyncInModal != null)
                {
                    BtnCancelSyncInModal.Visibility = Visibility.Collapsed;
                }
                _oauthService = null;
                _oauthCts = null;
            }
        }

        private void BtnCancelOAuthLogin_Click(object sender, RoutedEventArgs e)
        {
            _oauthCts?.Cancel();
            _oauthService?.Cancel();
            try
            {
                if (GoogleOAuthAutoConsentService.LastOAuthHwnd != IntPtr.Zero)
                {
                    GoogleOAuthAutoConsentService.CloseOAuthTab(GoogleOAuthAutoConsentService.LastOAuthHwnd);
                }
            }
            catch { }
            if (BtnOAuthLogin != null)
            {
                BtnOAuthLogin.IsEnabled = true;
                BtnOAuthLogin.Content = LocalizationManager.T("OAuthLogin");
            }
            if (BtnCancelOAuthLogin != null)
            {
                BtnCancelOAuthLogin.Visibility = Visibility.Collapsed;
            }
            if (BtnCancelSyncInModal != null)
            {
                BtnCancelSyncInModal.Visibility = Visibility.Collapsed;
            }
            ShowToast(LocalizationManager.T("OAuthCancelled"));
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
            _config.ThemeMode = ThemeManager.CurrentTheme;
            _config.Save();
            UpdateThemeButtonUI();
            UpdateScopeButtonsUI();
            UpdateAntigravityRunningStatus();
            UpdateChromeRunningStatus();
            if (_antigravityAccounts != null)
            {
                foreach (var acc in _antigravityAccounts)
                {
                    acc.NotifyThemeChanged();
                }
            }
            _activeAntigravityAccount?.NotifyThemeChanged();
            FilterAntigravityAccountsList();
        }

        private void UpdateThemeButtonUI()
        {
            if (IconSun != null && IconMoon != null)
            {
                IconSun.Visibility = ThemeManager.IsDark ? Visibility.Collapsed : Visibility.Visible;
                IconMoon.Visibility = ThemeManager.IsDark ? Visibility.Visible : Visibility.Collapsed;
            }
            if (TxtThemeText != null)
                TxtThemeText.Text = ThemeManager.IsDark ? LocalizationManager.T("ThemeDark") : LocalizationManager.T("ThemeLight");
        }

        private void BtnLanguageToggle_Click(object sender, RoutedEventArgs e)
        {
            LocalizationManager.ToggleLanguage();
            _config.Language = LocalizationManager.CurrentLanguage;
            _config.Save();
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            // Window title
            this.Title = LocalizationManager.T("AppTitle");

            // Language Toggle Button UI
            if (TxtLanguageFlag != null) TxtLanguageFlag.Text = LocalizationManager.IsEnglish ? "🇺🇸" : "🇻🇳";
            if (TxtLanguageText != null) TxtLanguageText.Text = LocalizationManager.IsEnglish ? "EN" : "VI";
            if (BtnLanguageToggle != null) BtnLanguageToggle.ToolTip = LocalizationManager.T("LangToggleTip");

            // Header & Theme
            if (TxtHeaderSubtitle != null) TxtHeaderSubtitle.Text = LocalizationManager.T("HeaderSubtitle");
            UpdateThemeButtonUI();
            if (BtnThemeToggle != null) BtnThemeToggle.ToolTip = LocalizationManager.T("ThemeToggleTip");

            // Navigation Tabs
            if (BtnNavChrome != null) BtnNavChrome.Content = LocalizationManager.T("TabChrome");
            if (BtnNavAntigravity != null) BtnNavAntigravity.Content = LocalizationManager.T("TabAntigravity");

            // Window Caption Controls
            if (BtnWinMinimize != null) BtnWinMinimize.ToolTip = LocalizationManager.IsEnglish ? "Minimize" : "Thu nhỏ";
            if (BtnWinMaximize != null) BtnWinMaximize.ToolTip = (this.WindowState == WindowState.Maximized)
                ? (LocalizationManager.IsEnglish ? "Restore Down" : "Khôi phục kích thước")
                : (LocalizationManager.IsEnglish ? "Maximize" : "Phóng to");
            if (BtnWinClose != null) BtnWinClose.ToolTip = LocalizationManager.IsEnglish ? "Close" : "Đóng";
            if (BtnAbout != null) BtnAbout.ToolTip = LocalizationManager.IsEnglish ? "About Application & Author" : "Thông tin tác giả & Dự án";

            // About Modal Localized Strings
            if (TxtAboutModalSubtitle != null) TxtAboutModalSubtitle.Text = LocalizationManager.IsEnglish ? "Chrome & Antigravity Zero-Crash Switcher" : "Chuyển đổi hồ sơ Chrome & Antigravity không crash";
            if (TxtAboutLabelAuthor != null) TxtAboutLabelAuthor.Text = LocalizationManager.IsEnglish ? "DEVELOPER / AUTHOR" : "TÁC GIẢ / DEVELOPER";
            if (TxtAboutDescGithub != null) TxtAboutDescGithub.Text = LocalizationManager.IsEnglish ? "Source code & Latest releases" : "Mã nguồn & Bản cập nhật mới";
            if (TxtAboutDescTele != null) TxtAboutDescTele.Text = LocalizationManager.IsEnglish ? "Direct contact & Support" : "Liên hệ & Hỗ trợ trực tiếp";
            if (TxtAboutBtnOpenGithub != null) TxtAboutBtnOpenGithub.Text = LocalizationManager.IsEnglish ? "Open" : "Mở";
            if (TxtAboutBtnOpenTele != null) TxtAboutBtnOpenTele.Text = LocalizationManager.IsEnglish ? "Chat" : "Nhắn";

            if (TxtSubHeaderHint != null)
            {
                bool isChrome = (ViewChrome != null && ViewChrome.Visibility == Visibility.Visible);
                TxtSubHeaderHint.Text = isChrome
                    ? (LocalizationManager.IsEnglish ? "⚡ Chrome Status & Quick Actions" : "⚡ Trạng thái Chrome & Thao tác nhanh")
                    : (LocalizationManager.IsEnglish ? "⚡ IDE Status & Quick Actions" : "⚡ Tiến trình IDE & Thao tác nhanh");
            }

            // Top Actions
            if (BtnCloseChrome != null)
            {
                BtnCloseChrome.Content = LocalizationManager.T("CloseChrome");
                BtnCloseChrome.ToolTip = LocalizationManager.T("CloseChromeTip");
            }
            if (BtnRefreshChrome != null)
            {
                BtnRefreshChrome.Content = LocalizationManager.T("Refresh");
                BtnRefreshChrome.ToolTip = LocalizationManager.T("RefreshTip");
            }
            if (BtnCloseAntigravity != null)
            {
                BtnCloseAntigravity.Content = LocalizationManager.T("CloseIde");
                BtnCloseAntigravity.ToolTip = LocalizationManager.T("CloseIdeTip");
            }
            if (BtnOAuthLogin != null)
            {
                BtnOAuthLogin.Content = (_oauthService != null) ? LocalizationManager.T("OAuthWaiting") : LocalizationManager.T("OAuthLogin");
                BtnOAuthLogin.ToolTip = LocalizationManager.T("OAuthLoginTip");
            }
            if (BtnCancelOAuthLogin != null)
            {
                BtnCancelOAuthLogin.Content = LocalizationManager.T("OAuthCancel");
                BtnCancelOAuthLogin.ToolTip = LocalizationManager.T("OAuthCancelTip");
            }
            if (BtnRestoreChatHistory != null)
            {
                BtnRestoreChatHistory.Content = LocalizationManager.T("RestoreChat");
                BtnRestoreChatHistory.ToolTip = LocalizationManager.T("RestoreChatTip");
            }
            if (BtnRefreshAntigravity != null)
            {
                BtnRefreshAntigravity.Content = LocalizationManager.T("Refresh");
                BtnRefreshAntigravity.ToolTip = LocalizationManager.T("RefreshAccountsTip");
            }

            // Tab 1: Chrome View
            if (TxtHeroDefaultBadge != null)
                TxtHeroDefaultBadge.Text = "★ " + (LocalizationManager.IsEnglish ? "DEFAULT PROFILE ON CHROME STARTUP" : "PROFILE MẶC ĐỊNH KHI MỞ CHROME");
            if (BtnHeroLaunch != null)
            {
                BtnHeroLaunch.ToolTip = LocalizationManager.T("OpenChromeTip");
                if (TxtHeroLaunchText != null) TxtHeroLaunchText.Text = LocalizationManager.T("OpenChrome");
            }
            if (TxtSearchProfilePlaceholder != null) TxtSearchProfilePlaceholder.Text = LocalizationManager.T("SearchChromePlaceholder");
            if (TxtChromeTotalLabel != null) TxtChromeTotalLabel.Text = LocalizationManager.T("Total");
            if (TxtEmptySearchText != null) TxtEmptySearchText.Text = LocalizationManager.IsEnglish ? "No matching profiles found!" : "Không tìm thấy profile nào phù hợp!";
            if (BtnClearSearchBottom != null) BtnClearSearchBottom.Content = LocalizationManager.IsEnglish ? "Clear Search" : "Xóa tìm kiếm";

            // Tab 1 Bottom Settings
            if (TxtBottomSettingsHeader != null) TxtBottomSettingsHeader.Text = LocalizationManager.IsEnglish ? "CONFIGURATION & AUTO-PROTECTION" : "CƠ CHẾ THIẾT LẬP & TỰ ĐỘNG BẢO VỆ";
            if (TxtBottomSettingsSub != null) TxtBottomSettingsSub.Text = LocalizationManager.IsEnglish ? " • Click to toggle options" : " • Bấm để bật/tắt tính năng";
            if (TxtChkShortcutsTitle != null) TxtChkShortcutsTitle.Text = LocalizationManager.IsEnglish ? "Desktop/Taskbar shortcuts" : "Phím tắt Desktop/Taskbar";
            if (TxtChkShortcutsSub != null) TxtChkShortcutsSub.Text = LocalizationManager.IsEnglish ? "Shortcut opens Default Profile" : "Shortcut mở đúng Profile";
            if (TxtChkRegistryTitle != null) TxtChkRegistryTitle.Text = LocalizationManager.IsEnglish ? "External links (Zalo, Mail)" : "Mở link ngoài (Zalo, Mail)";
            if (TxtChkRegistrySub != null) TxtChkRegistrySub.Text = LocalizationManager.IsEnglish ? "Configure open correct Profile" : "Cấu hình mở đúng Profile";
            if (TxtChkGuardTitle != null) TxtChkGuardTitle.Text = LocalizationManager.IsEnglish ? "Auto Protect Profile" : "Tự động bảo vệ Profile";
            if (TxtChkGuardSub != null) TxtChkGuardSub.Text = LocalizationManager.IsEnglish ? "Restore on Chrome close" : "Khôi phục khi tắt Chrome";
            if (TxtChkStartupTitle != null) TxtChkStartupTitle.Text = LocalizationManager.IsEnglish ? "Start with Windows" : "Khởi động cùng Windows";
            if (TxtChkStartupSub != null) TxtChkStartupSub.Text = LocalizationManager.IsEnglish ? "Run in background to protect system" : "Chạy nền bảo vệ hệ thống";
            UpdateSilentSyncUI();

            // Google Accounts Modal
            if (TxtAccountsModalTitle != null) TxtAccountsModalTitle.Text = LocalizationManager.IsEnglish ? "Google Accounts in Profile" : "Tài khoản Google trong Profile";
            if (BtnCloseAccountsModalTop != null) BtnCloseAccountsModalTop.ToolTip = LocalizationManager.IsEnglish ? "Close" : "Đóng";
            if (BtnBatchSyncAllInModal != null)
            {
                BtnBatchSyncAllInModal.Content = LocalizationManager.IsEnglish ? "⚡ Sync All to Antigravity" : "⚡ Đồng bộ tất cả vào Antigravity";
                BtnBatchSyncAllInModal.ToolTip = LocalizationManager.IsEnglish 
                    ? "Sync all Google accounts in this Profile to Antigravity (confirm in browser, tab closes automatically when done)"
                    : "Đồng bộ toàn bộ tài khoản Google trong Profile này vào Antigravity (bấm xác nhận trên trình duyệt, tab tự đóng khi hoàn tất)";
            }
            if (TxtCancelSyncBannerText != null) TxtCancelSyncBannerText.Text = LocalizationManager.IsEnglish ? "Stop Sync" : "Dừng đồng bộ";
            if (BtnCancelSyncBanner != null) BtnCancelSyncBanner.ToolTip = LocalizationManager.IsEnglish ? "Immediately stop the batch sync process" : "Dừng ngay lập tức toàn bộ quá trình đồng bộ hàng loạt";
            if (TxtCancelSyncInModalText != null) TxtCancelSyncInModalText.Text = LocalizationManager.IsEnglish ? "Stop Sync" : "Dừng đồng bộ";
            if (BtnCancelSyncInModal != null) BtnCancelSyncInModal.ToolTip = LocalizationManager.IsEnglish ? "Immediately stop the batch sync process" : "Dừng ngay lập tức toàn bộ quá trình đồng bộ đang chạy";
            if (BtnCloseAccountsModalBottom != null) BtnCloseAccountsModalBottom.Content = LocalizationManager.IsEnglish ? "Close" : "Đóng";

            // Custom Interval Modal
            if (TxtCustomIntervalTitle != null) TxtCustomIntervalTitle.Text = LocalizationManager.T("CustomIntervalTitle");
            if (TxtCustomIntervalSub != null) TxtCustomIntervalSub.Text = LocalizationManager.T("CustomIntervalSubtitle");
            if (TxtCustomIntervalHint != null) TxtCustomIntervalHint.Text = LocalizationManager.T("CustomIntervalDesc");
            if (TxtCustomIntervalInputLabel != null) TxtCustomIntervalInputLabel.Text = LocalizationManager.IsEnglish ? "Interval duration:" : "Khoảng thời gian chu kỳ:";
            if (CmbItemSeconds != null) CmbItemSeconds.Content = LocalizationManager.T("UnitSeconds");
            if (CmbItemMinutes != null) CmbItemMinutes.Content = LocalizationManager.T("UnitMinutes");
            if (CmbItemHours != null) CmbItemHours.Content = LocalizationManager.T("UnitHours");
            if (TxtCustomIntervalPresetsLabel != null) TxtCustomIntervalPresetsLabel.Text = LocalizationManager.IsEnglish ? "⚡ Quick Presets:" : "⚡ Chọn nhanh chu kỳ gợi ý:";
            if (BtnCustomIntervalCancel != null) BtnCustomIntervalCancel.Content = LocalizationManager.T("Cancel");
            if (TxtCustomIntervalApplyText != null) TxtCustomIntervalApplyText.Text = LocalizationManager.T("ApplyAndEnable");

            // Batch Sync Guidance / Confirmation Modal
            if (TxtBatchSyncHeaderTitle != null) TxtBatchSyncHeaderTitle.Text = LocalizationManager.IsEnglish ? "ACCOUNTS PENDING SYNC" : "DANH SÁCH TÀI KHOẢN CHỜ ĐỒNG BỘ";
            if (TxtBatchSyncGuidance != null) TxtBatchSyncGuidance.Text = LocalizationManager.IsEnglish 
                ? "Chrome will open each account in turn for confirmation. The app will auto-close tabs and save tokens to Antigravity upon completion."
                : "Trình duyệt Chrome sẽ lần lượt mở từng tài khoản để bạn xác nhận. Hệ thống sẽ tự đóng tab và lưu token vào Antigravity ngay khi hoàn tất.";
            if (TxtAutoClickInModalTitle != null) TxtAutoClickInModalTitle.Text = LocalizationManager.IsEnglish ? "⚡ Auto-click Sign in button" : "⚡ Tự động bấm nút Đăng nhập (Auto-Click)";
            if (TxtAutoClickInModalSub != null) TxtAutoClickInModalSub.Text = LocalizationManager.IsEnglish ? "Only clicks when the Sign in button is detected on screen" : "Chỉ click khi phát hiện đúng nút Đăng nhập trên màn hình";

            // Tab 2: Antigravity View - Hero
            if (TxtHeroActiveBadge != null) TxtHeroActiveBadge.Text = LocalizationManager.T("HeroActiveBadge");
            if (TxtHeroRemodulateQuota != null) TxtHeroRemodulateQuota.Text = LocalizationManager.T("RemodulateQuota");
            if (TxtHeroSeparateWindow != null) TxtHeroSeparateWindow.Text = LocalizationManager.T("SeparateWindow");
            if (BtnHeroReloadIde != null)
            {
                BtnHeroReloadIde.ToolTip = LocalizationManager.T("ReloadIdeTip");
                if (TxtHeroReloadIde != null) TxtHeroReloadIde.Text = LocalizationManager.T("ReloadIdeBtn");
            }
            if (TxtHero5hLabel != null) TxtHero5hLabel.Text = LocalizationManager.T("5Hours");
            if (TxtHeroWeeklyLabel != null) TxtHeroWeeklyLabel.Text = LocalizationManager.T("Weekly");

            // Tab 2: Toolbar
            if (TxtSearchAntigravityPlaceholder != null) TxtSearchAntigravityPlaceholder.Text = LocalizationManager.T("SearchAntigravityPlaceholder");
            if (TxtBatchMeasureLabel != null) TxtBatchMeasureLabel.Text = LocalizationManager.T("MeasureAllQuota");
            if (BtnRefreshAllAccountsQuota != null) BtnRefreshAllAccountsQuota.ToolTip = LocalizationManager.T("MeasureAllQuotaTip");
            if (BorderBatchConcurrency != null) BorderBatchConcurrency.ToolTip = LocalizationManager.T("ConcurrencyTip");
            if (TxtAntigravityTotalLabel != null) TxtAntigravityTotalLabel.Text = LocalizationManager.T("Total");
            if (BorderPlanFilter != null) BorderPlanFilter.ToolTip = LocalizationManager.T("FilterPlanTip");
            if (BorderSortFilter != null) BorderSortFilter.ToolTip = LocalizationManager.T("SortTip");

            if (TxtPlanFilterTitle != null)
                TxtPlanFilterTitle.Text = string.IsNullOrEmpty(_selectedPlanFilter) 
                    ? (LocalizationManager.IsEnglish ? "All Plans" : "Đặt lại tất cả") 
                    : _selectedPlanFilter;

            if (TxtRecBadge != null)
                TxtRecBadge.Text = LocalizationManager.IsEnglish ? "✨ RECOMMENDED NEXT" : "✨ ĐỀ XUẤT TIẾP THEO";
            if (TxtRecSwitchBtn != null)
                TxtRecSwitchBtn.Text = "Switch";
            if (BtnQuickSwitchRecommended != null)
                BtnQuickSwitchRecommended.ToolTip = LocalizationManager.IsEnglish 
                    ? "Switch immediately to this best quota account" 
                    : "Chuyển ngay sang tài khoản có hạn mức tốt nhất này";
            if (BorderRecRule != null)
                BorderRecRule.ToolTip = LocalizationManager.IsEnglish 
                    ? "Click to change recommendation rule (Priority: PRO, FREE, Claude, Gemini...)" 
                    : "Nhấp chuột để thay đổi quy định đề xuất tài khoản (Ưu tiên PRO, Chỉ PRO, Claude cao nhất...)";

            if (TxtHeroQuotaToggleIcon != null)
            {
                if (BorderHeroQuotaDetails != null && BorderHeroQuotaDetails.Visibility == Visibility.Visible)
                    TxtHeroQuotaToggleIcon.Text = LocalizationManager.T("CollapseModels");
                else
                    TxtHeroQuotaToggleIcon.Text = LocalizationManager.T("View20Models");
            }

            // Sub-components UI
            UpdateBatchConcurrencyUI();
            UpdateAutoRefreshQuotaUI();
            UpdatePrivacyUI();
            UpdateChromePrivacyUI();
            UpdateSortDirectionUI();
            UpdateScopeButtonsUI();
            UpdateChromeRunningStatus();
            UpdateAntigravityRunningStatus();
            UpdateRecommendedAccountUI();
            if (_activeAntigravityAccount != null)
                UpdateHeroModelQuotaDisplay(_activeAntigravityAccount);

            // Refresh cards and list items
            if (_profiles != null)
            {
                foreach (var p in _profiles)
                {
                    p.NotifyLanguageChanged();
                }
            }
            if (_antigravityAccounts != null)
            {
                foreach (var a in _antigravityAccounts)
                {
                    a.NotifyThemeChanged();
                    a.NotifyLanguageChanged();
                }
            }
            _activeAntigravityAccount?.NotifyThemeChanged();
            _activeAntigravityAccount?.NotifyLanguageChanged();
            if (_currentDefault != null)
            {
                _currentDefault.NotifyLanguageChanged();
                UpdateHeroDefaultProfileDisplay(_currentDefault);
            }

            FilterAntigravityAccountsList();
            ApplyProfileFilter(force: true);
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
                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                // 1. Kích hoạt native Windows 11 Rounded Corners (DWMWCP_ROUND = 2)
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

                // 2. Kích hoạt native DWM Drop Shadow xung quanh cửa sổ
                MARGINS margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 1, cyBottomHeight = 0 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch { }
        }

        #region DWM Win32 Interop for Native Rounded Corners & Shadow
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2; // Bo tròn tiêu chuẩn Windows 11
        #endregion

        private void MainWindow_StateChanged(object? sender, EventArgs e)
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
            this.Close();
        }
        #endregion
    }
}
