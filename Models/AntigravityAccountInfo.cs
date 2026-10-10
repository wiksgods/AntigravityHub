using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace ProfileSwitchChrome.Models
{
    public static class QuotaTimeHelper
    {
        public static string FormatRemainingShort(string? isoUtc, string? fallbackText = null)
        {
            bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
            if (string.IsNullOrWhiteSpace(isoUtc))
            {
                if (string.IsNullOrWhiteSpace(fallbackText)) return "";
                var m = System.Text.RegularExpressions.Regex.Match(fallbackText, @"sau\s+([^(\r\n]+)");
                if (m.Success) return (isEn ? "in " : "còn ") + m.Groups[1].Value.Trim();
                return fallbackText;
            }

            DateTime dtUtc;
            if (!DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out dtUtc))
            {
                if (!DateTime.TryParse(isoUtc, out dtUtc))
                    return fallbackText ?? "";
            }

            var local = dtUtc.ToLocalTime();
            var diff = local - DateTime.Now;

            if (diff.TotalSeconds <= 0) return isEn ? "ready" : "đã hồi phục";

            if (diff.TotalDays >= 1)
            {
                int days = (int)diff.TotalDays;
                int hours = diff.Hours;
                if (isEn)
                    return hours > 0 ? $"in {days}d {hours}h" : $"in {days}d";
                return hours > 0 ? $"còn {days} ngày {hours}h" : $"còn {days} ngày";
            }

            if (diff.TotalHours >= 1)
            {
                int hours = (int)diff.TotalHours;
                int mins = diff.Minutes;
                if (isEn)
                    return mins > 0 ? $"in {hours}h {mins}m" : $"in {hours}h";
                return mins > 0 ? $"còn {hours}h {mins}m" : $"còn {hours}h";
            }

            int mMinutes = Math.Max(1, (int)diff.TotalMinutes);
            return isEn ? $"in {mMinutes}m" : $"còn {mMinutes}m";
        }

        public static string FormatRemainingDetailed(string? isoUtc, string? fallbackText = null)
        {
            if (string.IsNullOrWhiteSpace(isoUtc))
                return fallbackText ?? "";

            bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
            DateTime dtUtc;
            if (!DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out dtUtc))
            {
                if (!DateTime.TryParse(isoUtc, out dtUtc))
                    return fallbackText ?? "";
            }

            var local = dtUtc.ToLocalTime();
            var diff = local - DateTime.Now;

            if (diff.TotalSeconds <= 0) return isEn ? $"Restored (at {local:HH:mm})" : $"Đã hồi phục (lúc {local:HH:mm})";

            if (diff.TotalDays >= 1)
            {
                int days = (int)diff.TotalDays;
                int hours = diff.Hours;
                int mins = diff.Minutes;
                return isEn 
                    ? $"Resets in {days}d {hours}h {mins}m (at {local:HH:mm MM/dd})" 
                    : $"Hồi phục sau {days} ngày {hours}h {mins}m (lúc {local:HH:mm dd/MM})";
            }

            if (diff.TotalHours >= 1)
            {
                int hours = (int)diff.TotalHours;
                int mins = diff.Minutes;
                return isEn 
                    ? $"Resets in {hours}h {mins}m (at {local:HH:mm})" 
                    : $"Hồi phục sau {hours}h {mins}m (lúc {local:HH:mm})";
            }

            int mMinutes = Math.Max(1, (int)diff.TotalMinutes);
            return isEn 
                ? $"Resets in {mMinutes} mins (at {local:HH:mm})" 
                : $"Hồi phục sau {mMinutes} phút (lúc {local:HH:mm})";
        }
    }

    public class AntigravityAccountInfo : INotifyPropertyChanged
    {
        private bool _isActive;
        private string? _name;
        private string? _email;
        private string? _status;
        private string? _quotaInfo;

        private int? _gemini5hPercent;
        private int? _geminiWeeklyPercent;
        private int? _claude5hPercent;
        private int? _claudeWeeklyPercent;
        private string? _claudeResetText;
        private string? _geminiResetText;
        private DateTime? _quotaUpdatedAt;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(InitialLetter)); }
        }

        public string? Email
        {
            get => _email;
            set { _email = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : (!string.IsNullOrWhiteSpace(Email) ? Email : "Google Account");

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
                    OnPropertyChanged(nameof(DisplayEmailMasked));
                }
            }
        }

        public string NameMasked => IsNameHidden ? "••••••••" : (Name ?? (!string.IsNullOrWhiteSpace(Email) ? Email : "Google Account"));
        public string EmailMasked => IsEmailHidden ? "••••••••••••" : (Email ?? "Chưa rõ email");
        public string DisplayNameMasked => IsNameHidden ? "••••••••" : DisplayName;
        public string DisplayEmailMasked => IsEmailHidden ? "••••••••••••" : (Email ?? "Chưa rõ email");

        public void TogglePrivacyHidden()
        {
            bool target = !(IsNameHidden && IsEmailHidden);
            IsNameHidden = target;
            IsEmailHidden = target;
        }

        public string? ApiKey { get; set; } // AccessToken (ya29...)
        public string? AccessToken
        {
            get => ApiKey;
            set => ApiKey = value;
        }
        private string? _plan;
        public string? Plan
        {
            get => _plan;
            set
            {
                if (_plan != value)
                {
                    _plan = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PlanBadgeText));
                    OnPropertyChanged(nameof(PlanBadgeDisplay));
                    OnPropertyChanged(nameof(PlanBadgeForeground));
                    OnPropertyChanged(nameof(PlanBadgeBorderBrush));
                    OnPropertyChanged(nameof(PlanBadgeBackground));
                }
            }
        }
        public string? RefreshToken { get; set; }
        public long ExpiryTimestamp { get; set; }
        public string? IdToken { get; set; }
        public bool IsGcpTos { get; set; }
        private string? _avatarUrl;
        private string? _avatarLocalPath;
        public string? AvatarUrl
        {
            get => _avatarUrl;
            set
            {
                if (_avatarUrl != value)
                {
                    _avatarUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAvatar));
                    OnPropertyChanged(nameof(AvatarSource));
                }
            }
        }
        public string? AvatarLocalPath
        {
            get => _avatarLocalPath;
            set
            {
                if (_avatarLocalPath != value)
                {
                    _avatarLocalPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAvatar));
                    OnPropertyChanged(nameof(AvatarSource));
                }
            }
        }
        public string? ProjectId { get; set; } = "aicode-consumers";

        /// <summary>
        /// True if account has all required tokens for Antigravity IDE switch.
        /// Missing RefreshToken or IdToken = switch will fail with auth error.
        /// </summary>
        public bool HasFullTokens => !string.IsNullOrEmpty(ApiKey) 
                                     && !string.IsNullOrEmpty(RefreshToken) 
                                     && !string.IsNullOrEmpty(IdToken);

        public string? RawOauthToken { get; set; }
        public string? RawUserStatus { get; set; }
        public string? RawModelCredits { get; set; }
        public string? RawEnterprisePreferences { get; set; }

        public bool IsForbidden { get; set; }
        public string? ForbiddenReason { get; set; }
        public string? ValidationUrl { get; set; }

        public bool IsRestricted => IsForbidden || 
                                    string.Equals(Plan, "Restricted", StringComparison.OrdinalIgnoreCase) || 
                                    (Plan != null && Plan.IndexOf("Restricted", StringComparison.OrdinalIgnoreCase) >= 0);

        public string? MachineId { get; set; }
        public string? MacMachineId { get; set; }
        public string? DevDeviceId { get; set; }
        public string? SqmId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; } = DateTime.Now;

        private bool _isSwitchingInProgress;
        [JsonIgnore]
        public bool IsSwitchingInProgress
        {
            get => _isSwitchingInProgress;
            set
            {
                if (_isSwitchingInProgress != value)
                {
                    _isSwitchingInProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanSwitchAccount));
                    OnPropertyChanged(nameof(SwitchButtonText));
                }
            }
        }

        private bool _isTargetOfSwitch;
        [JsonIgnore]
        public bool IsTargetOfSwitch
        {
            get => _isTargetOfSwitch;
            set
            {
                if (_isTargetOfSwitch != value)
                {
                    _isTargetOfSwitch = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanSwitchAccount));
                    OnPropertyChanged(nameof(SwitchButtonText));
                }
            }
        }

        [JsonIgnore]
        public bool CanSwitchAccount => !IsActive && !IsSwitchingInProgress;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveBadgeText));
                    OnPropertyChanged(nameof(StatusBorderBrush));
                    OnPropertyChanged(nameof(CanSwitchAccount));
                    OnPropertyChanged(nameof(SwitchButtonText));
                }
            }
        }

        public string Status
        {
            get => _status ?? "Sẵn sàng";
            set { _status = value; OnPropertyChanged(); }
        }

        public string QuotaInfo
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_quotaInfo)) return _quotaInfo;
                if (HasFullTokens)
                {
                    if (!string.IsNullOrWhiteSpace(RawEnterprisePreferences))
                        return "Gói Enterprise • Chuẩn";
                    return "Gói Free Tier • Chuẩn";
                }
                return "Chưa kết nối OAuth";
            }
            set { _quotaInfo = value; OnPropertyChanged(); }
        }

        public int? Gemini5hPercent
        {
            get => _gemini5hPercent;
            set 
            { 
                _gemini5hPercent = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(GeminiQuotaBadge)); 
                OnPropertyChanged(nameof(GeminiQuotaColor)); 
                OnPropertyChanged(nameof(HasModelQuota)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); 
                OnPropertyChanged(nameof(Col1Models));
                OnPropertyChanged(nameof(Col2Models));
                OnPropertyChanged(nameof(Col3Models));
                OnPropertyChanged(nameof(Gemini5hDisplay));
                OnPropertyChanged(nameof(GeminiMiniBarWidth));
                OnPropertyChanged(nameof(GeminiMiniBarBrush));
                OnPropertyChanged(nameof(GeminiScopedLabel));
                OnPropertyChanged(nameof(GeminiScopedDisplay));
                OnPropertyChanged(nameof(GeminiScopedBarWidth));
                OnPropertyChanged(nameof(GeminiScopedBarBrush));
            }
        }

        public int? GeminiWeeklyPercent
        {
            get => _geminiWeeklyPercent;
            set 
            { 
                _geminiWeeklyPercent = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(GeminiQuotaBadge)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); 
                OnPropertyChanged(nameof(WeeklyModels));
                OnPropertyChanged(nameof(GeminiScopedLabel));
                OnPropertyChanged(nameof(GeminiScopedDisplay));
                OnPropertyChanged(nameof(GeminiScopedBarWidth));
                OnPropertyChanged(nameof(GeminiScopedBarBrush));
                OnPropertyChanged(nameof(Gemini5hDisplay));
                OnPropertyChanged(nameof(GeminiMiniBarWidth));
                OnPropertyChanged(nameof(GeminiMiniBarBrush));
            }
        }

        public int? Claude5hPercent
        {
            get => _claude5hPercent;
            set 
            { 
                _claude5hPercent = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ClaudeQuotaBadge)); 
                OnPropertyChanged(nameof(ClaudeQuotaColor)); 
                OnPropertyChanged(nameof(HasModelQuota)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); 
                OnPropertyChanged(nameof(Col1Models));
                OnPropertyChanged(nameof(Col2Models));
                OnPropertyChanged(nameof(Col3Models));
                OnPropertyChanged(nameof(ClaudeScopedLabel));
                OnPropertyChanged(nameof(ClaudeScopedDisplay));
                OnPropertyChanged(nameof(ClaudeScopedBarWidth));
                OnPropertyChanged(nameof(ClaudeScopedBarBrush));
                OnPropertyChanged(nameof(Claude5hDisplay));
                OnPropertyChanged(nameof(ClaudeMiniBarWidth));
                OnPropertyChanged(nameof(ClaudeMiniBarBrush));
                OnPropertyChanged(nameof(ClaudeMiniBarTextColor));
            }
        }

        public int? ClaudeWeeklyPercent
        {
            get => _claudeWeeklyPercent;
            set 
            { 
                _claudeWeeklyPercent = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ClaudeQuotaBadge)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); 
                OnPropertyChanged(nameof(WeeklyModels));
                OnPropertyChanged(nameof(ClaudeScopedLabel));
                OnPropertyChanged(nameof(ClaudeScopedDisplay));
                OnPropertyChanged(nameof(ClaudeScopedBarWidth));
                OnPropertyChanged(nameof(ClaudeScopedBarBrush));
                OnPropertyChanged(nameof(Claude5hDisplay));
                OnPropertyChanged(nameof(ClaudeMiniBarWidth));
                OnPropertyChanged(nameof(ClaudeMiniBarBrush));
                OnPropertyChanged(nameof(ClaudeMiniBarTextColor));
            }
        }

        public string? ClaudeResetText
        {
            get => _claudeResetText;
            set { _claudeResetText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); OnPropertyChanged(nameof(Claude5hDisplay)); }
        }

        public string? GeminiResetText
        {
            get => _geminiResetText;
            set { _geminiResetText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); OnPropertyChanged(nameof(Gemini5hDisplay)); }
        }

        private string? _claudeResetTimeIso;
        private string? _geminiResetTimeIso;
        private string? _claudeWeeklyResetTimeIso;
        private string? _geminiWeeklyResetTimeIso;
        private Dictionary<string, string>? _modelResetTimes;

        public string? ClaudeResetTimeIso
        {
            get => _claudeResetTimeIso;
            set 
            { 
                _claudeResetTimeIso = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ClaudeResetShort)); 
                OnPropertyChanged(nameof(Claude5hDisplay)); 
                OnPropertyChanged(nameof(ResetCountdownSummary)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip));
                OnPropertyChanged(nameof(Col1Models));
                OnPropertyChanged(nameof(Col2Models));
                OnPropertyChanged(nameof(Col3Models));
            }
        }

        public string? GeminiResetTimeIso
        {
            get => _geminiResetTimeIso;
            set 
            { 
                _geminiResetTimeIso = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(GeminiResetShort)); 
                OnPropertyChanged(nameof(Gemini5hDisplay)); 
                OnPropertyChanged(nameof(ResetCountdownSummary)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip));
                OnPropertyChanged(nameof(Col1Models));
                OnPropertyChanged(nameof(Col2Models));
                OnPropertyChanged(nameof(Col3Models));
            }
        }

        public string? ClaudeWeeklyResetTimeIso
        {
            get => _claudeWeeklyResetTimeIso;
            set 
            { 
                _claudeWeeklyResetTimeIso = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(ClaudeWeeklyResetShort));
                OnPropertyChanged(nameof(WeeklyModels)); 
                OnPropertyChanged(nameof(ResetCountdownSummary)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip));
                OnPropertyChanged(nameof(ClaudeScopedDisplay));
                OnPropertyChanged(nameof(Claude5hDisplay));
            }
        }

        public string? GeminiWeeklyResetTimeIso
        {
            get => _geminiWeeklyResetTimeIso;
            set 
            { 
                _geminiWeeklyResetTimeIso = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(GeminiWeeklyResetShort));
                OnPropertyChanged(nameof(WeeklyModels)); 
                OnPropertyChanged(nameof(ResetCountdownSummary)); 
                OnPropertyChanged(nameof(ModelQuotaDetailedTooltip));
                OnPropertyChanged(nameof(GeminiScopedDisplay));
                OnPropertyChanged(nameof(Gemini5hDisplay));
            }
        }

        public Dictionary<string, string>? ModelResetTimes
        {
            get => _modelResetTimes;
            set 
            { 
                _modelResetTimes = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(Col1Models)); 
                OnPropertyChanged(nameof(Col2Models)); 
                OnPropertyChanged(nameof(Col3Models)); 
            }
        }

        public DateTime? QuotaUpdatedAt
        {
            get => _quotaUpdatedAt;
            set { _quotaUpdatedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModelQuotaDetailedTooltip)); }
        }

        private bool _isRefreshingQuota = false;

        [JsonIgnore]
        public bool IsRefreshingQuota
        {
            get => _isRefreshingQuota;
            set
            {
                if (_isRefreshingQuota != value)
                {
                    _isRefreshingQuota = value;
                    if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            OnPropertyChanged();
                            OnPropertyChanged(nameof(RefreshingBadgeVisibility));
                            OnPropertyChanged(nameof(QuotaContentVisibility));
                        });
                    }
                    else
                    {
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(RefreshingBadgeVisibility));
                        OnPropertyChanged(nameof(QuotaContentVisibility));
                    }
                }
            }
        }

        [JsonIgnore]
        public System.Windows.Visibility RefreshingBadgeVisibility => IsRefreshingQuota ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        [JsonIgnore]
        public System.Windows.Visibility QuotaContentVisibility => IsRefreshingQuota ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        [JsonIgnore]
        public bool HasModelQuota => Gemini5hPercent.HasValue || Claude5hPercent.HasValue || GeminiWeeklyPercent.HasValue || ClaudeWeeklyPercent.HasValue;

        [JsonIgnore]
        public string GeminiQuotaBadge => (Gemini5hPercent ?? GeminiWeeklyPercent).HasValue
            ? $"✦ Gemini: {(Gemini5hPercent ?? GeminiWeeklyPercent)!.Value}%"
            : "✦ Gemini: --%";

        [JsonIgnore]
        public string ClaudeQuotaBadge => (Claude5hPercent ?? ClaudeWeeklyPercent).HasValue
            ? $"✳ Claude: {(Claude5hPercent ?? ClaudeWeeklyPercent)!.Value}%"
            : "✳ Claude: --%";

        public string ClaudeResetShort => QuotaTimeHelper.FormatRemainingShort(ClaudeResetTimeIso, ClaudeResetText);
        public string GeminiResetShort => QuotaTimeHelper.FormatRemainingShort(GeminiResetTimeIso, GeminiResetText);
        public string ClaudeWeeklyResetShort => QuotaTimeHelper.FormatRemainingShort(ClaudeWeeklyResetTimeIso, null);
        public string GeminiWeeklyResetShort => QuotaTimeHelper.FormatRemainingShort(GeminiWeeklyResetTimeIso, null);

        public string Gemini5hDisplay
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (IsRestricted) return isEn ? "Restricted" : "Bị khóa";
                if (Gemini5hPercent.HasValue)
                {
                    if (Gemini5hPercent.Value == 0)
                    {
                        string shortTime = GeminiResetShort;
                        return !string.IsNullOrEmpty(shortTime) ? $"0% ({shortTime})" : "0%";
                    }
                    return $"{Gemini5hPercent.Value}%";
                }
                if (GeminiWeeklyPercent.HasValue)
                {
                    if (GeminiWeeklyPercent.Value == 0 && !string.IsNullOrEmpty(GeminiWeeklyResetShort))
                        return $"0% ({GeminiWeeklyResetShort})";
                    return isEn ? $"{GeminiWeeklyPercent.Value}% (Week)" : $"{GeminiWeeklyPercent.Value}% (Tuần)";
                }
                return "--%";
            }
        }

        public string Claude5hDisplay
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (IsRestricted) return isEn ? "Verify Required" : "Cần xác minh";
                if (Claude5hPercent.HasValue)
                {
                    if (Claude5hPercent.Value == 0)
                    {
                        string shortTime = ClaudeResetShort;
                        return !string.IsNullOrEmpty(shortTime) ? $"0% ({shortTime})" : "0%";
                    }
                    return $"{Claude5hPercent.Value}%";
                }
                if (ClaudeWeeklyPercent.HasValue)
                {
                    if (ClaudeWeeklyPercent.Value <= 10 && !string.IsNullOrEmpty(ClaudeWeeklyResetShort))
                        return $"{ClaudeWeeklyPercent.Value}% ({ClaudeWeeklyResetShort})";
                    return isEn ? $"{ClaudeWeeklyPercent.Value}% (Week)" : $"{ClaudeWeeklyPercent.Value}% (Tuần)";
                }
                return "--%";
            }
        }

        public double GeminiMiniBarWidth
        {
            get
            {
                if (IsRestricted) return 0;
                int pct = Gemini5hPercent ?? GeminiWeeklyPercent ?? 0;
                return 175.0 * Math.Max(0, Math.Min(100, pct)) / 100.0;
            }
        }

        public double ClaudeMiniBarWidth
        {
            get
            {
                if (IsRestricted) return 0;
                int pct = Claude5hPercent ?? ClaudeWeeklyPercent ?? 0;
                return 175.0 * Math.Max(0, Math.Min(100, pct)) / 100.0;
            }
        }

        public Brush GeminiMiniBarBrush
        {
            get
            {
                if (IsRestricted) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                int pct = Gemini5hPercent ?? GeminiWeeklyPercent ?? 0;
                return pct > 30
                    ? new SolidColorBrush(Color.FromRgb(0, 186, 136))
                    : (pct > 10 ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : new SolidColorBrush(Color.FromRgb(239, 68, 68)));
            }
        }

        public Brush ClaudeMiniBarBrush
        {
            get
            {
                if (IsRestricted) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                int pct = Claude5hPercent ?? ClaudeWeeklyPercent ?? 0;
                return pct > 30
                    ? new SolidColorBrush(Color.FromRgb(0, 186, 136))
                    : (pct > 0 ? new SolidColorBrush(Color.FromRgb(249, 115, 22)) : new SolidColorBrush(Color.FromRgb(71, 85, 105)));
            }
        }

        public static string CurrentScope { get; set; } = "both";

        public string GeminiScopedLabel
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                string weekLabel = isEn ? "(Week)" : "(Tuần)";
                if (CurrentScope == "5h")
                {
                    if (!Gemini5hPercent.HasValue && GeminiWeeklyPercent.HasValue)
                        return $"✦ Gemini {weekLabel}";
                    return "✦ Gemini (5h)";
                }
                if (CurrentScope == "week")
                {
                    if (!GeminiWeeklyPercent.HasValue && Gemini5hPercent.HasValue)
                        return "✦ Gemini (5h)";
                    return $"✦ Gemini {weekLabel}";
                }
                return "✦ Gemini";
            }
        }

        public string ClaudeScopedLabel
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                string weekLabel = isEn ? "(Week)" : "(Tuần)";
                if (CurrentScope == "5h")
                {
                    if (!Claude5hPercent.HasValue && ClaudeWeeklyPercent.HasValue)
                        return $"✳ Claude/GPT {weekLabel}";
                    return "✳ Claude/GPT (5h)";
                }
                if (CurrentScope == "week")
                {
                    if (!ClaudeWeeklyPercent.HasValue && Claude5hPercent.HasValue)
                        return "✳ Claude/GPT (5h)";
                    return $"✳ Claude/GPT {weekLabel}";
                }
                return "✳ Claude/GPT";
            }
        }

        public string GeminiScopedDisplay
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (IsRestricted) return isEn ? "Restricted" : "Bị khóa";
                string weekSuffix = isEn ? "(Week)" : "(Tuần)";
                if (CurrentScope == "5h")
                {
                    if (Gemini5hPercent.HasValue)
                    {
                        return Gemini5hPercent.Value == 0 && !string.IsNullOrEmpty(GeminiResetShort) 
                            ? $"0% ({GeminiResetShort})" 
                            : $"{Gemini5hPercent.Value}%";
                    }
                    if (GeminiWeeklyPercent.HasValue)
                    {
                        return GeminiWeeklyPercent.Value == 0 && !string.IsNullOrEmpty(GeminiWeeklyResetShort)
                            ? $"0% ({GeminiWeeklyResetShort})"
                            : $"{GeminiWeeklyPercent.Value}%";
                    }
                    return "--%";
                }
                if (CurrentScope == "week")
                {
                    if (GeminiWeeklyPercent.HasValue)
                    {
                        return GeminiWeeklyPercent.Value == 0 && !string.IsNullOrEmpty(GeminiWeeklyResetShort)
                            ? $"0% ({GeminiWeeklyResetShort})"
                            : $"{GeminiWeeklyPercent.Value}%";
                    }
                    if (Gemini5hPercent.HasValue)
                    {
                        return Gemini5hPercent.Value == 0 && !string.IsNullOrEmpty(GeminiResetShort) 
                            ? $"0% ({GeminiResetShort})" 
                            : $"{Gemini5hPercent.Value}%";
                    }
                    return "--%";
                }
                // both
                if (Gemini5hPercent.HasValue && GeminiWeeklyPercent.HasValue)
                    return $"{Gemini5hPercent.Value}% (5h) • {GeminiWeeklyPercent.Value}% {weekSuffix}";
                if (Gemini5hPercent.HasValue) return $"{Gemini5hPercent.Value}% (5h)";
                if (GeminiWeeklyPercent.HasValue) return $"{GeminiWeeklyPercent.Value}% {weekSuffix}";
                return "--%";
            }
        }

        public string ClaudeScopedDisplay
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (IsRestricted) return isEn ? "Verify Required" : "Cần xác minh";
                string weekSuffix = isEn ? "(Week)" : "(Tuần)";
                if (CurrentScope == "5h")
                {
                    if (Claude5hPercent.HasValue)
                    {
                        return Claude5hPercent.Value == 0 && !string.IsNullOrEmpty(ClaudeResetShort)
                            ? $"0% ({ClaudeResetShort})"
                            : $"{Claude5hPercent.Value}%";
                    }
                    if (ClaudeWeeklyPercent.HasValue)
                    {
                        return ClaudeWeeklyPercent.Value <= 10 && !string.IsNullOrEmpty(ClaudeWeeklyResetShort)
                            ? $"{ClaudeWeeklyPercent.Value}% ({ClaudeWeeklyResetShort})"
                            : $"{ClaudeWeeklyPercent.Value}%";
                    }
                    return "--%";
                }
                if (CurrentScope == "week")
                {
                    if (ClaudeWeeklyPercent.HasValue)
                    {
                        return ClaudeWeeklyPercent.Value <= 10 && !string.IsNullOrEmpty(ClaudeWeeklyResetShort)
                            ? $"{ClaudeWeeklyPercent.Value}% ({ClaudeWeeklyResetShort})"
                            : $"{ClaudeWeeklyPercent.Value}%";
                    }
                    if (Claude5hPercent.HasValue)
                    {
                        return Claude5hPercent.Value == 0 && !string.IsNullOrEmpty(ClaudeResetShort)
                            ? $"0% ({ClaudeResetShort})"
                            : $"{Claude5hPercent.Value}%";
                    }
                    return "--%";
                }
                // both
                if (Claude5hPercent.HasValue && ClaudeWeeklyPercent.HasValue)
                    return $"{Claude5hPercent.Value}% (5h) • {ClaudeWeeklyPercent.Value}% {weekSuffix}";
                if (Claude5hPercent.HasValue) return $"{Claude5hPercent.Value}% (5h)";
                if (ClaudeWeeklyPercent.HasValue) return $"{ClaudeWeeklyPercent.Value}% {weekSuffix}";
                return "--%";
            }
        }

        public double GeminiScopedBarWidth
        {
            get
            {
                if (IsRestricted) return 0;
                int? pct = CurrentScope == "week" 
                    ? (GeminiWeeklyPercent ?? Gemini5hPercent) 
                    : (Gemini5hPercent ?? GeminiWeeklyPercent);
                if (!pct.HasValue) return 0;
                return 175.0 * Math.Max(0, Math.Min(100, pct.Value)) / 100.0;
            }
        }

        public double ClaudeScopedBarWidth
        {
            get
            {
                if (IsRestricted) return 0;
                int? pct = CurrentScope == "week" 
                    ? (ClaudeWeeklyPercent ?? Claude5hPercent) 
                    : (Claude5hPercent ?? ClaudeWeeklyPercent);
                if (!pct.HasValue) return 0;
                return 175.0 * Math.Max(0, Math.Min(100, pct.Value)) / 100.0;
            }
        }

        public Brush GeminiScopedBarBrush
        {
            get
            {
                if (IsRestricted) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                int? pctVal = CurrentScope == "week" 
                    ? (GeminiWeeklyPercent ?? Gemini5hPercent) 
                    : (Gemini5hPercent ?? GeminiWeeklyPercent);
                if (!pctVal.HasValue) return new SolidColorBrush(Color.FromRgb(71, 85, 105));
                int pct = pctVal.Value;
                return pct > 30
                    ? new SolidColorBrush(Color.FromRgb(0, 186, 136))
                    : (pct > 10 ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : new SolidColorBrush(Color.FromRgb(239, 68, 68)));
            }
        }

        public Brush ClaudeScopedBarBrush
        {
            get
            {
                if (IsRestricted) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                int? pctVal = CurrentScope == "week" 
                    ? (ClaudeWeeklyPercent ?? Claude5hPercent) 
                    : (Claude5hPercent ?? ClaudeWeeklyPercent);
                if (!pctVal.HasValue) return new SolidColorBrush(Color.FromRgb(71, 85, 105));
                int pct = pctVal.Value;
                return pct > 30
                    ? new SolidColorBrush(Color.FromRgb(0, 186, 136))
                    : (pct > 0 ? new SolidColorBrush(Color.FromRgb(239, 68, 68)) : new SolidColorBrush(Color.FromRgb(71, 85, 105)));
            }
        }

        public void NotifyScopeChanged()
        {
            OnPropertyChanged(nameof(GeminiScopedLabel));
            OnPropertyChanged(nameof(ClaudeScopedLabel));
            OnPropertyChanged(nameof(GeminiScopedDisplay));
            OnPropertyChanged(nameof(ClaudeScopedDisplay));
            OnPropertyChanged(nameof(GeminiScopedBarWidth));
            OnPropertyChanged(nameof(ClaudeScopedBarWidth));
            OnPropertyChanged(nameof(GeminiScopedBarBrush));
            OnPropertyChanged(nameof(ClaudeScopedBarBrush));
        }

        public string ClaudeMiniBarTextColor
        {
            get
            {
                bool isLight = !Services.ThemeManager.IsDark;
                if (IsRestricted) return isLight ? "#DC2626" : "#F87171";
                int pct = Claude5hPercent ?? ClaudeWeeklyPercent ?? 0;
                return pct > 0 ? (isLight ? "#7C3AED" : "#C084FC") : (isLight ? "#DC2626" : "#F87171");
            }
        }

        public string GeminiQuotaColor => (Gemini5hPercent ?? GeminiWeeklyPercent ?? -1) switch
        {
            > 40 => "#34D399",
            > 10 => "#FBBF24",
            >= 0 => "#F87171",
            _ => "#64748B"  // Gray when not measured
        };

        public string ClaudeQuotaColor => (Claude5hPercent ?? ClaudeWeeklyPercent ?? -1) switch
        {
            > 40 => "#A78BFA",
            > 10 => "#FBBF24",
            >= 0 => "#F87171",
            _ => "#64748B"  // Gray when not measured
        };

        public string ModelQuotaDetailedTooltip
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                var sb = new StringBuilder();
                sb.AppendLine(isEn ? "📊 AI MODEL QUOTA (CURRENT STATUS):" : "📊 HẠN MỨC MÔ HÌNH (QUOTA HIỆN TẠI):");
                if (Gemini5hPercent.HasValue)
                {
                    sb.AppendLine($"• Gemini Models (Flash, Pro):");
                    sb.AppendLine(isEn ? $"  - 5-Hour rolling limit: {Gemini5hPercent.Value}% remaining" : $"  - Hạn mức 5 giờ: {Gemini5hPercent.Value}% còn lại");
                    if (!string.IsNullOrEmpty(GeminiResetTimeIso) || !string.IsNullOrEmpty(GeminiResetText))
                        sb.AppendLine($"    ↳ {QuotaTimeHelper.FormatRemainingDetailed(GeminiResetTimeIso, GeminiResetText)}");
                    sb.AppendLine(isEn ? $"  - Weekly limit: {GeminiWeeklyPercent ?? 0}% remaining" : $"  - Hạn mức Tuần: {GeminiWeeklyPercent ?? 0}% còn lại");
                    if (!string.IsNullOrEmpty(GeminiWeeklyResetTimeIso))
                        sb.AppendLine($"    ↳ {QuotaTimeHelper.FormatRemainingDetailed(GeminiWeeklyResetTimeIso)}");
                }
                if (Claude5hPercent.HasValue)
                {
                    sb.AppendLine($"• Claude & GPT (Opus, Sonnet, GPT-OSS):");
                    sb.AppendLine(isEn ? $"  - 5-Hour rolling limit: {Claude5hPercent.Value}% remaining" : $"  - Hạn mức 5 giờ: {Claude5hPercent.Value}% còn lại");
                    if (!string.IsNullOrEmpty(ClaudeResetTimeIso) || !string.IsNullOrEmpty(ClaudeResetText))
                        sb.AppendLine($"    ↳ {QuotaTimeHelper.FormatRemainingDetailed(ClaudeResetTimeIso, ClaudeResetText)}");
                    sb.AppendLine(isEn ? $"  - Weekly limit: {ClaudeWeeklyPercent ?? 0}% remaining" : $"  - Hạn mức Tuần: {ClaudeWeeklyPercent ?? 0}% còn lại");
                    if (!string.IsNullOrEmpty(ClaudeWeeklyResetTimeIso))
                        sb.AppendLine($"    ↳ {QuotaTimeHelper.FormatRemainingDetailed(ClaudeWeeklyResetTimeIso)}");
                }
                if (QuotaUpdatedAt.HasValue)
                    sb.AppendLine(isEn ? $"Updated at: {QuotaUpdatedAt.Value:HH:mm:ss MM/dd}" : $"Cập nhật lúc: {QuotaUpdatedAt.Value:HH:mm:ss dd/MM}");
                return sb.ToString().TrimEnd();
            }
        }

        public string InitialLetter
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    string trimmed = Name.Trim();
                    return char.ToUpperInvariant(trimmed[0]).ToString();
                }
                if (!string.IsNullOrWhiteSpace(Email))
                {
                    return char.ToUpperInvariant(Email.Trim()[0]).ToString();
                }
                return "A";
            }
        }

        public string ActiveBadgeText => IsActive 
            ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "🟢 Active" : "🟢 Đang sử dụng") 
            : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "⚪ Ready" : "⚪ Sẵn sàng");

        public Brush StatusBorderBrush => IsActive
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : new SolidColorBrush(Color.FromRgb(30, 41, 59));

        public ImageSource? AvatarSource
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(AvatarLocalPath) && File.Exists(AvatarLocalPath))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(AvatarLocalPath, UriKind.Absolute);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        return bi;
                    }
                    if (HasAvatar)
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(AvatarUrl!, UriKind.Absolute);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        return bi;
                    }
                }
                catch
                {
                    return null;
                }
                return null;
            }
        }

        public string FormattedExpiry
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (ExpiryTimestamp <= 0) 
                    return HasFullTokens 
                        ? (isEn ? "✓ Ready (Auto-refresh)" : "✓ Sẵn sàng (Auto Refresh)") 
                        : (isEn ? "⚠ Missing tokens" : "⚠ Thiếu tokens");
                try
                {
                    DateTime expTime;
                    if (ExpiryTimestamp > 1000000000000L)
                        expTime = DateTimeOffset.FromUnixTimeMilliseconds(ExpiryTimestamp).LocalDateTime;
                    else
                        expTime = DateTimeOffset.FromUnixTimeSeconds(ExpiryTimestamp).LocalDateTime;

                    var diff = expTime - DateTime.Now;
                    if (diff.TotalMinutes <= 0)
                        return HasFullTokens 
                            ? (isEn ? "✓ Auto-refresh ready" : "✓ Auto Refresh sẵn sàng") 
                            : (isEn ? "⚠ Token expired (Login required)" : "⚠ Token hết hạn (cần login)");
                    if (diff.TotalHours < 1)
                        return isEn 
                            ? $"Token: {Math.Max(1, (int)diff.TotalMinutes)}m left (Auto-refresh)" 
                            : $"Token còn {Math.Max(1, (int)diff.TotalMinutes)}p (Tự cấp mới)";
                    return isEn 
                        ? $"Token until {expTime:HH:mm MM/dd}" 
                        : $"Token đến {expTime:HH:mm dd/MM}";
                }
                catch
                {
                    return isEn ? "Unknown" : "Không rõ";
                }
            }
        }

        /// <summary>
        /// Short status text showing token completeness
        /// </summary>
        public string TokenStatusText => HasFullTokens 
            ? (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "✓ Ready to switch" : "✓ Sẵn sàng switch")
            : (ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "🔑 OAuth login required" : "🔑 Cần OAuth login");

        public string SwitchButtonText
        {
            get
            {
                if (IsActive)
                    return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "✓ In Use" : "✓ Đang dùng";
                if (IsTargetOfSwitch)
                    return ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "⏳ Switching..." : "⏳ Đang chuyển...";
                return "⚡ Switch";
            }
        }

        public string MeasuringQuotaText => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Measuring quota..." : "Đang đo quota...";

        public string RefreshTooltipText => ProfileSwitchChrome.Services.LocalizationManager.IsEnglish ? "Measure quota (Press Enter to run)" : "Đo lại hạn mức (Nhấn Enter để chạy)";

        /// <summary>
        /// Only show warning badge when tokens are missing/expired. Hide redundant 'Sẵn sàng switch' when tokens are ready.
        /// </summary>
        public Visibility TokenWarningVisibility => HasFullTokens ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Whether this account belongs to Pro, Ultra, or Enterprise tier (Acc pro hay gói cao)
        /// </summary>
        public bool IsProOrHigher => PlanBadgeText == "PRO" || PlanBadgeText == "ULTRA" || PlanBadgeText == "ENTERPRISE";

        /// <summary>
        /// Visibility of the luminous Gemini Pro avatar ring (matches real Google Gemini Pro UI)
        /// </summary>
        public Visibility ProRingVisibility => IsProOrHigher ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Visibility of the subtle normal avatar border (when NOT Pro)
        /// </summary>
        public Visibility NormalRingVisibility => IsProOrHigher ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Real Google Gemini Pro monogram circular background (#00382B dark emerald-teal from real UI) or theme fallback
        /// </summary>
        public Brush AvatarBackgroundBrush => IsProOrHigher
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00382B"))
            : (Brush)(Application.Current?.Resources["ThemeAccountAvatarBg"] ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")));

        /// <summary>
        /// Color for token status
        /// </summary>
        public string TokenStatusColor => HasFullTokens 
            ? (Services.ThemeManager.IsDark ? "#34D399" : "#059669") 
            : (Services.ThemeManager.IsDark ? "#FBBF24" : "#D97706");

        // Keyboard navigation and focused action item state
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
                    OnPropertyChanged(nameof(IsSwitchFocused));
                    OnPropertyChanged(nameof(IsRefreshFocused));
                    OnPropertyChanged(nameof(IsModelFocused));
                    OnPropertyChanged(nameof(IsDeleteFocused));
                }
            }
        }

        private int _focusedActionIndex = 0; // 0: Switch, 1: Refresh, 2: 20 Model, 3: Delete
        public int FocusedActionIndex
        {
            get => _focusedActionIndex;
            set
            {
                if (_focusedActionIndex != value)
                {
                    _focusedActionIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSwitchFocused));
                    OnPropertyChanged(nameof(IsRefreshFocused));
                    OnPropertyChanged(nameof(IsModelFocused));
                    OnPropertyChanged(nameof(IsDeleteFocused));
                }
            }
        }

        public bool IsSwitchFocused => IsKeyboardSelected && FocusedActionIndex == 0;
        public bool IsRefreshFocused => IsKeyboardSelected && FocusedActionIndex == 1;
        public bool IsModelFocused => IsKeyboardSelected && FocusedActionIndex == 2;
        public bool IsDeleteFocused => IsKeyboardSelected && FocusedActionIndex == 3;

        public void NotifyThemeChanged()
        {
            OnPropertyChanged(nameof(TokenStatusColor));
            OnPropertyChanged(nameof(GeminiQuotaColor));
            OnPropertyChanged(nameof(ClaudeQuotaColor));
            OnPropertyChanged(nameof(PlanBadgeForeground));
            OnPropertyChanged(nameof(PlanBadgeBorderBrush));
            OnPropertyChanged(nameof(PlanBadgeBackground));
            OnPropertyChanged(nameof(ClaudeMiniBarTextColor));
            OnPropertyChanged(nameof(IsProOrHigher));
            OnPropertyChanged(nameof(ProRingVisibility));
            OnPropertyChanged(nameof(NormalRingVisibility));
            OnPropertyChanged(nameof(AvatarBackgroundBrush));
            OnPropertyChanged(nameof(TokenWarningVisibility));
            OnPropertyChanged(nameof(ActiveBadgeText));
        }

        /// <summary>
        /// Whether this account has an avatar URL or local avatar
        /// </summary>
        public bool HasAvatar => (!string.IsNullOrWhiteSpace(AvatarLocalPath) && File.Exists(AvatarLocalPath)) ||
                                 (!string.IsNullOrWhiteSpace(AvatarUrl) &&
                                  (AvatarUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                   AvatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// True if this account has Claude or GPT models available in its quota breakdown
        /// </summary>
        public bool HasClaudeModels =>
            (Col1Models != null && Col1Models.Any(m => m.Group == "Claude/GPT")) ||
            (Col2Models != null && Col2Models.Any(m => m.Group == "Claude/GPT")) ||
            (Col3Models != null && Col3Models.Any(m => m.Group == "Claude/GPT")) ||
            (WeeklyModels != null && WeeklyModels.Any(m => m.Group == "Claude/GPT"));

        public string GoogleBadgeText => "GOOGLE";
        public string PlanBadgeText
        {
            get
            {
                if (IsRestricted)
                    return "RESTRICTED";

                if (!string.IsNullOrEmpty(RawEnterprisePreferences) && RawEnterprisePreferences.Contains("enterpriseGcpProjectId"))
                    return "ENTERPRISE";

                string plan = Plan ?? "";
                if (string.Equals(plan, "Restricted", StringComparison.OrdinalIgnoreCase) || plan.IndexOf("Restricted", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "RESTRICTED";
                if (string.Equals(plan, "Enterprise", StringComparison.OrdinalIgnoreCase) || plan.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "ENTERPRISE";
                if (string.Equals(plan, "Ultra", StringComparison.OrdinalIgnoreCase) || plan.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "ULTRA";
                if (string.Equals(plan, "Pro", StringComparison.OrdinalIgnoreCase) || plan.IndexOf("Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "PRO";
                if (string.Equals(plan, "Free", StringComparison.OrdinalIgnoreCase) || plan.IndexOf("Free", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "FREE";

                return "FREE";
            }
        }

        public string PlanBadgeDisplay
        {
            get
            {
                string p = PlanBadgeText;
                if (p == "PRO") return "✦ PRO";
                if (p == "ULTRA") return "★ ULTRA";
                if (p == "ENTERPRISE") return "👑 ENTERPRISE";
                return p;
            }
        }

        public string PlanCategory
        {
            get
            {
                if (IsRestricted) return "Restricted";
                string p = Plan ?? "";
                if (p.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    (!string.IsNullOrEmpty(RawEnterprisePreferences) && RawEnterprisePreferences.Contains("enterpriseGcpProjectId")))
                    return "Enterprise";
                if (p.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Google AI Ultra";
                if (p.IndexOf("Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Google AI Pro";
                return "Antigravity Starter Quota";
            }
        }

        public Brush PlanBadgeForeground
        {
            get
            {
                bool isLight = !Services.ThemeManager.IsDark;
                string text = PlanBadgeText;
                if (text == "RESTRICTED") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#DC2626" : "#F87171"));
                if (text == "ULTRA") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#7E22CE" : "#C084FC"));
                if (text == "ENTERPRISE") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#B45309" : "#FBBF24"));
                if (text == "PRO") return new SolidColorBrush(Color.FromRgb(255, 255, 255)); // Crisp Pure White
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#475569" : "#94A3B8"));
            }
        }

        public Brush PlanBadgeBorderBrush
        {
            get
            {
                bool isLight = !Services.ThemeManager.IsDark;
                string text = PlanBadgeText;
                if (text == "RESTRICTED") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#FECDD3" : "#EF4444"));
                if (text == "ULTRA") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#E9D5FF" : "#9333EA"));
                if (text == "ENTERPRISE") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#FDE68A" : "#D97706"));
                if (text == "PRO")
                {
                    // Luminous neon border: Indigo -> Fuchsia -> Sun Gold
                    var borderBrush = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(1, 1)
                    };
                    borderBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#A5B4FC"), 0.0));
                    borderBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F472B6"), 0.55));
                    borderBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FDE68A"), 1.0));
                    return borderBrush;
                }
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#E2E8F0" : "#334155"));
            }
        }

        public Brush PlanBadgeBackground
        {
            get
            {
                bool isLight = !Services.ThemeManager.IsDark;
                string text = PlanBadgeText;
                if (text == "RESTRICTED") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#FEE2E2" : "#3B1A1A"));
                if (text == "ULTRA") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#F3E8FF" : "#2E1065"));
                if (text == "ENTERPRISE") return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#FEF3C7" : "#451A03"));
                if (text == "PRO")
                {
                    // Cyberpunk Cosmic Aurora Sunset: Indigo -> Royal Violet -> Neon Fuchsia -> Sunset Amber Gold
                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(1, 1)
                    };
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#4F46E5"), 0.0));
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#9333EA"), 0.35));
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#EC4899"), 0.72));
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F97316"), 1.0));
                    return brush;
                }
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#F1F5F9" : "#1E293B"));
            }
        }

        public string CreditsText => "0 credits";

        public void CopyQuotaFrom(AntigravityAccountInfo source)
        {
            if (string.IsNullOrEmpty(RefreshToken) && !string.IsNullOrEmpty(source.RefreshToken)) RefreshToken = source.RefreshToken;
            if (string.IsNullOrEmpty(IdToken) && !string.IsNullOrEmpty(source.IdToken)) IdToken = source.IdToken;
            if (string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(source.ApiKey)) ApiKey = source.ApiKey;
            if (ExpiryTimestamp == 0 && source.ExpiryTimestamp > 0) ExpiryTimestamp = source.ExpiryTimestamp;

            if (!string.IsNullOrEmpty(source.Plan))
            {
                Plan = source.Plan;
            }
            IsForbidden = source.IsForbidden;
            ForbiddenReason = source.ForbiddenReason;
            ValidationUrl = source.ValidationUrl;
            Gemini5hPercent = source.Gemini5hPercent;
            GeminiWeeklyPercent = source.GeminiWeeklyPercent;
            Claude5hPercent = source.Claude5hPercent;
            ClaudeWeeklyPercent = source.ClaudeWeeklyPercent;
            GeminiResetText = source.GeminiResetText;
            ClaudeResetText = source.ClaudeResetText;
            ClaudeResetTimeIso = source.ClaudeResetTimeIso;
            GeminiResetTimeIso = source.GeminiResetTimeIso;
            ClaudeWeeklyResetTimeIso = source.ClaudeWeeklyResetTimeIso;
            GeminiWeeklyResetTimeIso = source.GeminiWeeklyResetTimeIso;
            QuotaUpdatedAt = source.QuotaUpdatedAt;
            if (source.ModelQuotas != null)
            {
                ModelQuotas = new Dictionary<string, int>(source.ModelQuotas, StringComparer.OrdinalIgnoreCase);
            }
            if (source.ModelResetTimes != null)
            {
                ModelResetTimes = new Dictionary<string, string>(source.ModelResetTimes, StringComparer.OrdinalIgnoreCase);
            }
            OnPropertyChanged(nameof(PlanBadgeText));
            OnPropertyChanged(nameof(PlanBadgeDisplay));
            OnPropertyChanged(nameof(PlanBadgeForeground));
            OnPropertyChanged(nameof(PlanBadgeBorderBrush));
            OnPropertyChanged(nameof(PlanBadgeBackground));
            OnPropertyChanged(nameof(IsProOrHigher));
            OnPropertyChanged(nameof(ProRingVisibility));
            OnPropertyChanged(nameof(NormalRingVisibility));
            OnPropertyChanged(nameof(AvatarBackgroundBrush));
            OnPropertyChanged(nameof(TokenWarningVisibility));
        }

        private Dictionary<string, int>? _modelQuotas;
        public Dictionary<string, int>? ModelQuotas
        {
            get => _modelQuotas;
            set
            {
                _modelQuotas = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Col1Models));
                OnPropertyChanged(nameof(Col2Models));
                OnPropertyChanged(nameof(Col3Models));
            }
        }

        public int? GetModel5hPercent(string modelKey, bool isClaude)
        {
            if (ModelQuotas != null && ModelQuotas.Count > 0)
            {
                if (ModelQuotas.TryGetValue(modelKey, out int exact))
                    return exact;

                foreach (var kvp in ModelQuotas)
                {
                    if (kvp.Key.IndexOf(modelKey, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        modelKey.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return kvp.Value;
                    }
                }
            }
            return isClaude ? Claude5hPercent : Gemini5hPercent;
        }

        public string? GetModel5hResetTime(string modelKey, bool isClaude)
        {
            if (ModelResetTimes != null && ModelResetTimes.Count > 0)
            {
                if (ModelResetTimes.TryGetValue(modelKey, out string? exact))
                    return exact;

                foreach (var kvp in ModelResetTimes)
                {
                    if (kvp.Key.IndexOf(modelKey, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        modelKey.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return kvp.Value;
                    }
                }
            }
            if (isClaude)
            {
                if ((!Claude5hPercent.HasValue || Claude5hPercent.Value >= 100) && !string.IsNullOrEmpty(ClaudeWeeklyResetTimeIso))
                    return ClaudeWeeklyResetTimeIso;
                return ClaudeResetTimeIso;
            }
            return GeminiResetTimeIso;
        }

        public int? GetModelPercent(string modelKey, bool isClaude)
        {
            return GetModel5hPercent(modelKey, isClaude);
        }

        public string? GetModelResetTime(string modelKey, bool isClaude)
        {
            return GetModel5hResetTime(modelKey, isClaude);
        }

        public string GetModelResetText(string modelKey, bool isClaude)
        {
            return isClaude ? (ClaudeResetText ?? "") : (GeminiResetText ?? "");
        }

        public string ResetCountdownSummary
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (IsRestricted)
                    return isEn ? "⚠️ Restricted account - Google verification required" : "⚠️ Tài khoản bị hạn chế - Google yêu cầu xác minh tài khoản";

                var parts = new List<string>();

                if (Claude5hPercent.HasValue)
                {
                    if (Claude5hPercent.Value == 0)
                    {
                        string det = QuotaTimeHelper.FormatRemainingDetailed(ClaudeResetTimeIso, ClaudeResetText);
                        parts.Add($"Claude/GPT (5h): {(string.IsNullOrEmpty(det) ? (isEn ? "Out of quota" : "Hết hạn mức") : det)}");
                    }
                    else
                    {
                        parts.Add($"Claude/GPT (5h): {Claude5hPercent.Value}%");
                    }
                }

                if (ClaudeWeeklyPercent.HasValue && !string.IsNullOrEmpty(ClaudeWeeklyResetTimeIso))
                {
                    string wkDet = QuotaTimeHelper.FormatRemainingShort(ClaudeWeeklyResetTimeIso);
                    if (!string.IsNullOrEmpty(wkDet))
                        parts.Add($"{(isEn ? "Week" : "Tuần")}: {ClaudeWeeklyPercent.Value}% ({wkDet})");
                }

                if (Gemini5hPercent.HasValue && Gemini5hPercent.Value == 0)
                {
                    string det = QuotaTimeHelper.FormatRemainingDetailed(GeminiResetTimeIso, GeminiResetText);
                    parts.Add($"Gemini (5h): {(string.IsNullOrEmpty(det) ? (isEn ? "Out of quota" : "Hết hạn mức") : det)}");
                }

                if (parts.Count > 0)
                    return "⏳ " + string.Join(" • ", parts);

                if (Gemini5hPercent.HasValue || Claude5hPercent.HasValue)
                    return isEn ? "✓ Quota ready" : "✓ Hạn mức sẵn sàng";

                if (GeminiWeeklyPercent.HasValue || ClaudeWeeklyPercent.HasValue)
                    return isEn ? "✓ Weekly quota ready (Free tier)" : "✓ Hạn mức tuần sẵn sàng (Gói Free)";

                return isEn ? "Not measured yet" : "Chưa đo hạn mức";
            }
        }

        // 3-Column 20 Model Layout (Matching Antigravity IDE UI)
        public List<ModelQuotaItem> Col1Models => IsRestricted ? new() : new()
        {
            new() { Name = "Gemini 3 Flash", Percent = GetModel5hPercent("gemini-3-flash", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3-flash", false), ResetTimeIso = GetModel5hResetTime("gemini-3-flash", false) },
            new() { Name = "Gemini 3.5 Flash Low", Percent = GetModel5hPercent("gemini-3.5-flash-low", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.5-flash-low", false), ResetTimeIso = GetModel5hResetTime("gemini-3.5-flash-low", false) },
            new() { Name = "Gemini Pro Agent", Percent = GetModel5hPercent("gemini-pro-agent", false), Group = "Gemini", ResetText = GetModelResetText("gemini-pro-agent", false), ResetTimeIso = GetModel5hResetTime("gemini-pro-agent", false) },
            new() { Name = "Gemini 3.6 Flash Tiered", Percent = GetModel5hPercent("gemini-3.6-flash-tiered", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.6-flash-tiered", false), ResetTimeIso = GetModel5hResetTime("gemini-3.6-flash-tiered", false) },
            new() { Name = "Gemini 3.6 Flash Low", Percent = GetModel5hPercent("gemini-3.6-flash-low", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.6-flash-low", false), ResetTimeIso = GetModel5hResetTime("gemini-3.6-flash-low", false) },
            new() { Name = "Gemini Flash Image", Percent = GetModel5hPercent("gemini-3.1-flash-image", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.1-flash-image", false), ResetTimeIso = GetModel5hResetTime("gemini-3.1-flash-image", false) },
            new() { Name = "Gpt Oss 120b", Percent = GetModel5hPercent("gpt-oss-120b", true), Group = "Claude/GPT", ResetText = GetModelResetText("gpt-oss-120b", true), ResetTimeIso = GetModel5hResetTime("gpt-oss-120b", true), SecondaryPercent = ClaudeWeeklyPercent, SecondaryResetTimeIso = ClaudeWeeklyResetTimeIso }
        };

        public List<ModelQuotaItem> Col2Models => IsRestricted ? new() : new()
        {
            new() { Name = "Gemini 3 Flash Agent", Percent = GetModel5hPercent("gemini-3-flash-agent", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3-flash-agent", false), ResetTimeIso = GetModel5hResetTime("gemini-3-flash-agent", false) },
            new() { Name = "Gemini 3.5 Flash Lite", Percent = GetModel5hPercent("gemini-3.5-flash-lite", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.5-flash-lite", false), ResetTimeIso = GetModel5hResetTime("gemini-3.5-flash-lite", false) },
            new() { Name = "Gemini 3.1 Pro High", Percent = GetModel5hPercent("gemini-3.1-pro-high", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.1-pro-high", false), ResetTimeIso = GetModel5hResetTime("gemini-3.1-pro-high", false) },
            new() { Name = "Gemini 3.6 Flash High", Percent = GetModel5hPercent("gemini-3.6-flash-high", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.6-flash-high", false), ResetTimeIso = GetModel5hResetTime("gemini-3.6-flash-high", false) },
            new() { Name = "Gemini 3.6 Flash Medium", Percent = GetModel5hPercent("gemini-3.6-flash-medium", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.6-flash-medium", false), ResetTimeIso = GetModel5hResetTime("gemini-3.6-flash-medium", false) },
            new() { Name = "Gemini 2.5 Pro", Percent = GetModel5hPercent("gemini-2.5-pro", false), Group = "Gemini", ResetText = GetModelResetText("gemini-2.5-pro", false), ResetTimeIso = GetModel5hResetTime("gemini-2.5-pro", false) },
            new() { Name = "Claude 4.6 Sonnet", Percent = GetModel5hPercent("claude-sonnet-4-6", true), Group = "Claude/GPT", ResetText = GetModelResetText("claude-sonnet-4-6", true), ResetTimeIso = GetModel5hResetTime("claude-sonnet-4-6", true), SecondaryPercent = ClaudeWeeklyPercent, SecondaryResetTimeIso = ClaudeWeeklyResetTimeIso }
        };

        public List<ModelQuotaItem> Col3Models => IsRestricted ? new() : new()
        {
            new() { Name = "Gemini 3.5 Flash Extra Low", Percent = GetModel5hPercent("gemini-3.5-flash-extra-low", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.5-flash-extra-low", false), ResetTimeIso = GetModel5hResetTime("gemini-3.5-flash-extra-low", false) },
            new() { Name = "Gemini 3.1 Pro Low", Percent = GetModel5hPercent("gemini-3.1-pro-low", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.1-pro-low", false), ResetTimeIso = GetModel5hResetTime("gemini-3.1-pro-low", false) },
            new() { Name = "Gemini Flash Lite", Percent = GetModel5hPercent("gemini-3.1-flash-lite", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.1-flash-lite", false), ResetTimeIso = GetModel5hResetTime("gemini-3.1-flash-lite", false) },
            new() { Name = "Gemini 3.7 Flash Tiered", Percent = GetModel5hPercent("gemini-3.7-flash-tiered", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.7-flash-tiered", false), ResetTimeIso = GetModel5hResetTime("gemini-3.7-flash-tiered", false) },
            new() { Name = "Gemini 3.8 Flash Tiered", Percent = GetModel5hPercent("gemini-3.8-flash-tiered", false), Group = "Gemini", ResetText = GetModelResetText("gemini-3.8-flash-tiered", false), ResetTimeIso = GetModel5hResetTime("gemini-3.8-flash-tiered", false) },
            new() { Name = "Claude Opus 4 6", Percent = GetModel5hPercent("claude-opus-4-6", true), Group = "Claude/GPT", ResetText = GetModelResetText("claude-opus-4-6", true), ResetTimeIso = GetModel5hResetTime("claude-opus-4-6", true), SecondaryPercent = ClaudeWeeklyPercent, SecondaryResetTimeIso = ClaudeWeeklyResetTimeIso }
        };

        public List<ModelQuotaItem> WeeklyModels => IsRestricted ? new() : new()
        {
            new() { 
                Name = "Gemini", 
                Percent = GeminiWeeklyPercent, 
                Group = "Gemini", 
                ResetText = GeminiResetText ?? "Chủ Nhật", 
                ResetTimeIso = GeminiWeeklyResetTimeIso 
            },
            new() { 
                Name = "Claude and GPT", 
                Percent = ClaudeWeeklyPercent, 
                Group = "Claude/GPT", 
                ResetText = ClaudeResetText ?? "Chủ Nhật", 
                ResetTimeIso = ClaudeWeeklyResetTimeIso 
            }
        };

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(ActiveBadgeText));
            OnPropertyChanged(nameof(FormattedExpiry));
            OnPropertyChanged(nameof(TokenStatusText));
            OnPropertyChanged(nameof(SwitchButtonText));
            OnPropertyChanged(nameof(MeasuringQuotaText));
            OnPropertyChanged(nameof(RefreshTooltipText));
            OnPropertyChanged(nameof(GeminiScopedLabel));
            OnPropertyChanged(nameof(ClaudeScopedLabel));
            OnPropertyChanged(nameof(GeminiScopedDisplay));
            OnPropertyChanged(nameof(ClaudeScopedDisplay));
            OnPropertyChanged(nameof(Gemini5hDisplay));
            OnPropertyChanged(nameof(Claude5hDisplay));
            OnPropertyChanged(nameof(ResetCountdownSummary));
            OnPropertyChanged(nameof(ModelQuotaDetailedTooltip));
            OnPropertyChanged(nameof(EmailMasked));
            OnPropertyChanged(nameof(DisplayEmailMasked));
            OnPropertyChanged(nameof(Col1Models));
            OnPropertyChanged(nameof(Col2Models));
            OnPropertyChanged(nameof(Col3Models));
            OnPropertyChanged(nameof(WeeklyModels));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    /// <summary>
    /// Individual AI Model Quota item matching Antigravity IDE UI
    /// </summary>
    public class ModelQuotaItem
    {
        public string Name { get; set; } = "";
        public int? Percent { get; set; }
        public string Group { get; set; } = "Gemini";
        public string ResetText { get; set; } = "";
        public string? ResetTimeIso { get; set; }
        public int? SecondaryPercent { get; set; }
        public string? SecondaryResetTimeIso { get; set; }

        public double PercentValue
        {
            get => Percent.HasValue ? Math.Max(0, Math.Min(100, Percent.Value)) : 0;
            set { }
        }

        public Brush BarBrush
        {
            get
            {
                if (!Percent.HasValue)
                    return new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate-400
                if (Percent.Value <= 0)
                    return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red-500
                if (Percent.Value > 30)
                    return new SolidColorBrush(Color.FromRgb(0, 186, 136)); // Turquoise #00BA88 as in IDE UI
                if (Percent.Value > 15)
                    return new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber-500
                return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red-500
            }
        }

        // Exact pixel width of progress fill (calculated for 170px width)
        public double BarWidth => Percent.HasValue ? Math.Max(0, Math.Min(100, Percent.Value)) * 1.7 : 0;
        public double TrackWidth => 170;

        public string ResetRemainingShort => QuotaTimeHelper.FormatRemainingShort(ResetTimeIso, ResetText);

        public string ResetCountdownText
        {
            get
            {
                if (Percent.HasValue && Percent.Value >= 100) return "";
                string rem = ResetRemainingShort;
                if (string.IsNullOrWhiteSpace(rem) || rem.Equals("đã hồi phục", StringComparison.OrdinalIgnoreCase) || rem.Equals("ready", StringComparison.OrdinalIgnoreCase))
                    return "";

                if (rem.StartsWith("còn ", StringComparison.OrdinalIgnoreCase))
                    return "reset: " + rem.Substring(4).Trim();
                if (rem.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
                    return "reset: " + rem.Substring(3).Trim();

                return "reset: " + rem;
            }
        }

        public Visibility ResetCountdownVisibility => string.IsNullOrWhiteSpace(ResetCountdownText) ? Visibility.Collapsed : Visibility.Visible;

        public string PercentDisplay
        {
            get
            {
                if (!Percent.HasValue) return "--%";
                return $"{Percent.Value}%";
            }
        }

        public Brush PercentForeground
        {
            get
            {
                if (!Percent.HasValue) return new SolidColorBrush(Color.FromRgb(148, 163, 184));
                if (Percent.Value == 0) return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red #EF4444
                if (Percent.Value <= 15) return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                if (Percent.Value <= 30) return new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                return new SolidColorBrush(Color.FromRgb(15, 23, 42)); // Slate #0F172A
            }
        }

        public string TooltipText
        {
            get
            {
                bool isEn = ProfileSwitchChrome.Services.LocalizationManager.IsEnglish;
                if (!Percent.HasValue) 
                    return isEn ? $"{Name}: Quota not measured (Waiting for IDE)" : $"{Name}: Chưa đo hạn mức (Chờ IDE phản hồi)";
                string det = QuotaTimeHelper.FormatRemainingDetailed(ResetTimeIso, ResetText);
                var sb = new StringBuilder();
                sb.Append(isEn ? $"{Name}: {Percent.Value}% remaining" : $"{Name}: {Percent.Value}% còn lại");
                if (!string.IsNullOrEmpty(det)) sb.Append($" • {det}");
                if (SecondaryPercent.HasValue)
                {
                    string secDet = QuotaTimeHelper.FormatRemainingShort(SecondaryResetTimeIso);
                    sb.Append(isEn 
                        ? $"\n↳ Weekly limit: {SecondaryPercent.Value}% remaining{(string.IsNullOrEmpty(secDet) ? "" : $" ({secDet})")}"
                        : $"\n↳ Hạn mức tuần: {SecondaryPercent.Value}% còn lại{(string.IsNullOrEmpty(secDet) ? "" : $" ({secDet})")}");
                }
                return sb.ToString();
            }
        }
    }
}
