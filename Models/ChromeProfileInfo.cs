using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProfileSwitchChrome.Models
{
    public class GoogleAccountInfo : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string GivenName { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public bool IsPrimary { get; set; } = false;
        public string ProfileId { get; set; } = string.Empty;
        public bool IsAlreadyInAntigravity { get; set; } = false;

        private bool _isNameHidden;
        public bool IsNameHidden
        {
            get => _isNameHidden;
            set
            {
                if (_isNameHidden != value)
                {
                    _isNameHidden = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayNameMasked));
                }
            }
        }

        private bool _isEmailHidden;
        public bool IsEmailHidden
        {
            get => _isEmailHidden;
            set
            {
                if (_isEmailHidden != value)
                {
                    _isEmailHidden = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EmailMasked));
                }
            }
        }

        public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Email : FullName;
        public string DisplayNameMasked => IsNameHidden ? "••••••••" : DisplayName;
        public string EmailMasked => IsEmailHidden ? "••••••••••••" : Email;

        public void TogglePrivacyHidden()
        {
            bool target = !(IsNameHidden && IsEmailHidden);
            IsNameHidden = target;
            IsEmailHidden = target;
        }

        public string SyncButtonText => IsAlreadyInAntigravity ? "🔄 Cập nhật token" : "⚡ Sync Antigravity";
        public string SyncButtonTooltip => IsAlreadyInAntigravity
            ? "Tài khoản này đã có trong Antigravity. Bấm để làm mới/cấp lại token bằng Chrome Profile này (không cần gõ pass/2FA)."
            : "Tự động mở Chrome Profile này để đăng nhập vào Antigravity (Google tự nhận diện, không cần gõ pass/2FA).";

        public string InitialLetter
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(FullName))
                    return FullName.Trim().Substring(0, 1).ToUpper();
                if (!string.IsNullOrWhiteSpace(Email))
                    return Email.Trim().Substring(0, 1).ToUpper();
                return "?";
            }
        }

        public Brush InitialBrush
        {
            get
            {
                Color[] colors = new[]
                {
                    Color.FromRgb(239, 68, 68),
                    Color.FromRgb(249, 115, 22),
                    Color.FromRgb(245, 158, 11),
                    Color.FromRgb(16, 185, 129),
                    Color.FromRgb(14, 165, 233),
                    Color.FromRgb(59, 130, 246),
                    Color.FromRgb(99, 102, 241),
                    Color.FromRgb(168, 85, 247),
                    Color.FromRgb(236, 72, 153)
                };
                int idx = Math.Abs((Email ?? "").GetHashCode()) % colors.Length;
                return new SolidColorBrush(colors[idx]);
            }
        }
    }

    public class ChromeProfileInfo : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string AvatarPath { get; set; } = string.Empty;
        public int ColorSeed { get; set; } = 0;
        public bool IsDefault { get; set; } = false;

        private bool _isNameHidden;
        public bool IsNameHidden
        {
            get => _isNameHidden;
            set
            {
                if (_isNameHidden != value)
                {
                    _isNameHidden = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(NameMasked));
                }
            }
        }

        private bool _isEmailHidden;
        public bool IsEmailHidden
        {
            get => _isEmailHidden;
            set
            {
                if (_isEmailHidden != value)
                {
                    _isEmailHidden = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UserNameMasked));
                }
            }
        }

        public string NameMasked => IsNameHidden ? "••••••••" : (string.IsNullOrWhiteSpace(Name) ? Id : Name);

        public string UserNameMasked
        {
            get
            {
                if (string.IsNullOrWhiteSpace(UserName))
                    return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Not signed in" : "Chưa liên kết email";
                return IsEmailHidden ? "••••••••••••" : UserName;
            }
        }

        public void TogglePrivacyHidden()
        {
            bool target = !(IsNameHidden && IsEmailHidden);
            IsNameHidden = target;
            IsEmailHidden = target;
        }

        private bool _isKeyboardSelected;
        public bool IsKeyboardSelected
        {
            get => _isKeyboardSelected;
            set
            {
                if (_isKeyboardSelected != value)
                {
                    _isKeyboardSelected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSetDefaultFocused));
                    OnPropertyChanged(nameof(IsOpenFocused));
                    OnPropertyChanged(nameof(IsShortcutFocused));
                }
            }
        }

        private int _focusedActionIndex = 0; // 0: Set Default, 1: Open, 2: Shortcut
        public int FocusedActionIndex
        {
            get => _focusedActionIndex;
            set
            {
                if (_focusedActionIndex != value)
                {
                    _focusedActionIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSetDefaultFocused));
                    OnPropertyChanged(nameof(IsOpenFocused));
                    OnPropertyChanged(nameof(IsShortcutFocused));
                }
            }
        }

        public bool IsSetDefaultFocused => IsKeyboardSelected && FocusedActionIndex == 0;
        public bool IsOpenFocused => IsKeyboardSelected && FocusedActionIndex == 1;
        public bool IsShortcutFocused => IsKeyboardSelected && FocusedActionIndex == 2;

        public List<GoogleAccountInfo> GoogleAccounts { get; set; } = new();

        public int GoogleAccountCount => GoogleAccounts.Count;
        public bool HasMultipleAccounts => GoogleAccounts.Count > 1;
        public bool HasGoogleAccount => GoogleAccounts.Count > 0;

        public bool IsRunning { get; set; } = false;
        public long MemoryBytes { get; set; } = 0;
        public string MemoryText => MemoryBytes > 0 ? FormatBytes(MemoryBytes) : "";
        public string RunningBadgeText => IsRunning 
            ? (MemoryBytes > 0 
                ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? $"Running • ~{MemoryText}" : $"Đang chạy • ~{MemoryText}") 
                : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Running" : "Đang chạy")) 
            : "";
        public string RunningBadgeTooltip => IsRunning 
            ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? $"This profile is active and using approx {MemoryText} RAM" : $"Profile này đang mở và chiếm khoảng {MemoryText} RAM") 
            : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Profile is closed" : "Profile đang đóng");

        public string AccountsBadgeText
        {
            get
            {
                if (GoogleAccounts.Count > 1)
                    return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? $"{GoogleAccounts.Count} Google Accounts" : $"{GoogleAccounts.Count} tài khoản Google";
                if (GoogleAccounts.Count == 1)
                    return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "1 Google Account" : "1 tài khoản Google";
                return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "No Google accounts" : "Chưa có tài khoản";
            }
        }

        public string DefaultBadgeText => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "CURRENT DEFAULT" : "MẶC ĐỊNH HIỆN TẠI";
        public string FolderPrefixText => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Folder:" : "Thư mục:";
        public string UnsignedText => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Not signed in Google" : "Chưa đăng nhập Google";

        public string SetDefaultButtonContent => IsDefault 
            ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "✓ Default" : "✓ Đang mặc định") 
            : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "⭐ Set as Default" : "⭐ Đặt làm mặc định");

        public string SetDefaultTooltip => IsDefault 
            ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "This profile is already set as default" : "Profile này đang là mặc định") 
            : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Set as default Chrome profile" : "Đặt làm Profile mặc định khi mở Chrome");

        public string LaunchButtonContent => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "🚀 Open" : "🚀 Mở";
        public string LaunchButtonTooltip => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Open Chrome with this profile immediately" : "Mở ngay cửa sổ Chrome với Profile này";
        public string ShortcutButtonTooltip => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Create desktop shortcut for this profile" : "Tạo phím tắt riêng cho Profile này ra màn hình Desktop";
        public string ProfileNameTooltip => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Click to toggle hiding this profile name" : "Nhấp chuột để ẩn/hiện tên profile này";
        public string ProfileEmailTooltip => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Left-click: Hide/Show email | Right-click: Copy email" : "Nhấp chuột trái: Ẩn/Hiện email | Nhấp chuột phải: Sao chép email";

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(DefaultBadgeText));
            OnPropertyChanged(nameof(FolderPrefixText));
            OnPropertyChanged(nameof(UnsignedText));
            OnPropertyChanged(nameof(SetDefaultButtonContent));
            OnPropertyChanged(nameof(SetDefaultTooltip));
            OnPropertyChanged(nameof(LaunchButtonContent));
            OnPropertyChanged(nameof(LaunchButtonTooltip));
            OnPropertyChanged(nameof(ShortcutButtonTooltip));
            OnPropertyChanged(nameof(ProfileNameTooltip));
            OnPropertyChanged(nameof(ProfileEmailTooltip));
            OnPropertyChanged(nameof(RunningBadgeText));
            OnPropertyChanged(nameof(RunningBadgeTooltip));
            OnPropertyChanged(nameof(AccountsBadgeText));
            OnPropertyChanged(nameof(AccountsTooltipText));
            OnPropertyChanged(nameof(NameMasked));
            OnPropertyChanged(nameof(UserNameMasked));
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes >= 1024L * 1024 * 1024)
            {
                double gb = bytes / (1024.0 * 1024 * 1024);
                return gb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            }
            if (bytes >= 1024L * 1024)
            {
                double mb = bytes / (1024.0 * 1024);
                return mb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            }
            if (bytes >= 1024L)
            {
                double kb = bytes / 1024.0;
                return kb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }
            return $"{bytes} B";
        }

        public string AccountsTooltipText
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (GoogleAccounts.Count == 0)
                    return isEn ? "No Google accounts logged into this profile." : "Chưa đăng nhập tài khoản Google nào trên Profile này.";

                var sb = new StringBuilder();
                sb.AppendLine(isEn ? $"GOOGLE ACCOUNTS LIST ({GoogleAccounts.Count}):" : $"DANH SÁCH TÀI KHOẢN GOOGLE ({GoogleAccounts.Count}):");
                int idx = 1;
                foreach (var acc in GoogleAccounts)
                {
                    string tag = acc.IsPrimary ? (isEn ? " ★ [Primary]" : " ★ [Chính]") : "";
                    string name = string.IsNullOrWhiteSpace(acc.FullName) ? "" : $" ({acc.FullName})";
                    sb.AppendLine($"{idx++}. {acc.Email}{name}{tag}");
                }
                sb.Append(isEn ? "\n(Click button to view details & copy)" : "\n(Bấm vào nút để xem chi tiết & sao chép)");
                return sb.ToString();
            }
        }

        public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim().Substring(0, 1).ToUpper();

        public ImageSource? AvatarSource
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(AvatarPath) && File.Exists(AvatarPath))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(AvatarPath);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { }
                return null;
            }
        }

        public Brush InitialBrush
        {
            get
            {
                if (ColorSeed != 0)
                {
                    byte r = (byte)((ColorSeed >> 16) & 0xFF);
                    byte g = (byte)((ColorSeed >> 8) & 0xFF);
                    byte b = (byte)(ColorSeed & 0xFF);
                    if (r > 35 || g > 35 || b > 35)
                    {
                        return new SolidColorBrush(Color.FromRgb(r, g, b));
                    }
                }

                // Fallback palette
                Color[] colors = new[]
                {
                    Color.FromRgb(239, 68, 68),
                    Color.FromRgb(249, 115, 22),
                    Color.FromRgb(245, 158, 11),
                    Color.FromRgb(16, 185, 129),
                    Color.FromRgb(14, 165, 233),
                    Color.FromRgb(59, 130, 246),
                    Color.FromRgb(99, 102, 241),
                    Color.FromRgb(168, 85, 247),
                    Color.FromRgb(236, 72, 153)
                };

                int idx = Math.Abs((Name ?? "").GetHashCode()) % colors.Length;
                return new SolidColorBrush(colors[idx]);
            }
        }
    }
}
