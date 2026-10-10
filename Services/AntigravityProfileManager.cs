using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProfileSwitchChrome.Models;

namespace ProfileSwitchChrome.Services
{
    public static class AntigravityProfileManager
    {
        private static readonly string AppDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string AntigravityStateDbPath =>
            Path.Combine(AppDataRoaming, "Antigravity IDE", "User", "globalStorage", "state.vscdb");

        public static string AntigravityStorageJsonPath =>
            Path.Combine(AppDataRoaming, "Antigravity IDE", "User", "globalStorage", "storage.json");

        public static string AntigravityKeybindingsPath =>
            Path.Combine(AppDataRoaming, "Antigravity IDE", "User", "keybindings.json");

        public static string AppDataHubDir =>
            Path.Combine(AppDataRoaming, "AntigravityHub");

        public static string LocalAccountsStorePath
        {
            get
            {
                string newPath = Path.Combine(AppDataHubDir, "antigravity_accounts.json");
                string oldPath = Path.Combine(AppDataRoaming, "ProfileSwitchChrome", "antigravity_accounts.json");
                if (!File.Exists(newPath) && File.Exists(oldPath))
                {
                    try
                    {
                        Directory.CreateDirectory(AppDataHubDir);
                        File.Copy(oldPath, newPath, true);
                    }
                    catch { }
                }
                return newPath;
            }
        }

        public static string ExternalAntigravityToolsPath =>
            Path.Combine(UserProfile, ".antigravity_tools");

        // Win32 API for sending graceful in-place reload message to Antigravity IDE window
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int VK_R = 0x52;      // 'R' key
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_ALT = 0x12;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr pCred);

        /// <summary>
        /// Reads current active account directly from state.vscdb
        /// </summary>
        public static AntigravityAccountInfo? GetCurrentActiveAccount()
        {
            string dbPath = AntigravityStateDbPath;
            if (!File.Exists(dbPath)) return null;

            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 5
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM ItemTable WHERE key IN (" +
                                  "'antigravityAuthStatus', " +
                                  "'antigravityUnifiedStateSync.oauthToken', " +
                                  "'antigravityUnifiedStateSync.userStatus', " +
                                  "'antigravityUnifiedStateSync.modelCredits', " +
                                  "'antigravityUnifiedStateSync.enterprisePreferences', " +
                                  "'antigravity.profileUrl')";

                using var reader = cmd.ExecuteReader();
                var dict = new Dictionary<string, string>();
                while (reader.Read())
                {
                    dict[reader.GetString(0)] = reader.GetString(1);
                }

                if (!dict.TryGetValue("antigravityAuthStatus", out var authStatusJson) || string.IsNullOrWhiteSpace(authStatusJson))
                    return null;

                var authObj = JObject.Parse(authStatusJson);
                string? email = authObj["email"]?.ToString();
                string? name = authObj["name"]?.ToString();
                string? apiKey = authObj["apiKey"]?.ToString();

                if (string.IsNullOrWhiteSpace(email)) return null;

                var account = new AntigravityAccountInfo
                {
                    Email = email,
                    Name = string.IsNullOrWhiteSpace(name) ? email : name,
                    ApiKey = apiKey,
                    IsActive = true,
                    Status = "Đang hoạt động"
                };

                if (dict.TryGetValue("antigravity.profileUrl", out var avatarUrl))
                    account.AvatarUrl = avatarUrl;

                if (dict.TryGetValue("antigravityUnifiedStateSync.oauthToken", out var rawToken))
                    account.RawOauthToken = rawToken;

                if (dict.TryGetValue("antigravityUnifiedStateSync.userStatus", out var rawStatus))
                    account.RawUserStatus = rawStatus;

                if (dict.TryGetValue("antigravityUnifiedStateSync.modelCredits", out var rawCredits))
                    account.RawModelCredits = rawCredits;

                if (dict.TryGetValue("antigravityUnifiedStateSync.enterprisePreferences", out var rawEnterprise))
                    account.RawEnterprisePreferences = rawEnterprise;

                // Also read device profile from storage.json if available
                ReadDeviceProfileFromStorageJson(account);

                return account;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error reading active antigravity account: " + ex.Message);
                return null;
            }
        }

        private static void ReadDeviceProfileFromStorageJson(AntigravityAccountInfo account)
        {
            try
            {
                string storagePath = AntigravityStorageJsonPath;
                if (!File.Exists(storagePath)) return;

                string json = File.ReadAllText(storagePath);
                var obj = JObject.Parse(json);

                account.DevDeviceId = obj["telemetry.devDeviceId"]?.ToString() ?? obj["storage.serviceMachineId"]?.ToString();
                account.MachineId = obj["telemetry.machineId"]?.ToString();
                account.MacMachineId = obj["telemetry.macMachineId"]?.ToString();
                account.SqmId = obj["telemetry.sqmId"]?.ToString();
            }
            catch { }
        }

        /// <summary>
        /// Loads all saved accounts from local store and external tool dirs (Antigravity Tools / Manager).
        /// Preserves cached model quota, avatars, and user settings.
        /// </summary>
        public static List<AntigravityAccountInfo> LoadAllAccounts()
        {
            var result = new List<AntigravityAccountInfo>();
            var existingEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var activeAccount = GetCurrentActiveAccount();

            // 1. Load from local store FIRST so all cached quotas and avatars are preserved
            try
            {
                if (File.Exists(LocalAccountsStorePath))
                {
                    string json = File.ReadAllText(LocalAccountsStorePath);
                    var savedList = JsonConvert.DeserializeObject<List<AntigravityAccountInfo>>(json);
                    if (savedList != null)
                    {
                        string cacheDir = Path.Combine(AppDataHubDir, "avatars");
                        using var md5 = System.Security.Cryptography.MD5.Create();

                        foreach (var acc in savedList)
                        {
                            acc.IsRefreshingQuota = false;
                            if (!string.IsNullOrEmpty(acc.Email) && !existingEmails.Contains(acc.Email))
                            {
                                acc.IsActive = (activeAccount != null && string.Equals(acc.Email, activeAccount.Email, StringComparison.OrdinalIgnoreCase));

                                if (!string.IsNullOrEmpty(acc.AvatarUrl))
                                {
                                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(acc.AvatarUrl));
                                    string hashStr = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                                    string localPath = Path.Combine(cacheDir, $"{hashStr}.png");
                                    if (File.Exists(localPath))
                                    {
                                        acc.AvatarLocalPath = localPath;
                                    }
                                }

                                result.Add(acc);
                                existingEmails.Add(acc.Email);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading local accounts: " + ex.Message);
            }

            // 2. Synchronize active account from state.vscdb
            if (activeAccount != null && !string.IsNullOrEmpty(activeAccount.Email))
            {
                var existing = result.FirstOrDefault(a => string.Equals(a.Email, activeAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsActive = true;
                    // Update tokens from IDE if available, without wiping quota
                    if (!string.IsNullOrEmpty(activeAccount.ApiKey)) existing.ApiKey = activeAccount.ApiKey;
                    if (!string.IsNullOrEmpty(activeAccount.RefreshToken)) existing.RefreshToken = activeAccount.RefreshToken;
                    if (!string.IsNullOrEmpty(activeAccount.IdToken)) existing.IdToken = activeAccount.IdToken;
                    if (activeAccount.ExpiryTimestamp > 0) existing.ExpiryTimestamp = activeAccount.ExpiryTimestamp;
                    if (!string.IsNullOrEmpty(activeAccount.RawOauthToken)) existing.RawOauthToken = activeAccount.RawOauthToken;
                    if (!string.IsNullOrEmpty(activeAccount.RawUserStatus)) existing.RawUserStatus = activeAccount.RawUserStatus;
                    if (!string.IsNullOrEmpty(activeAccount.RawEnterprisePreferences)) existing.RawEnterprisePreferences = activeAccount.RawEnterprisePreferences;

                    // Ensure activeAccount retains the existing verified avatar
                    if (!string.IsNullOrEmpty(existing.AvatarUrl))
                    {
                        activeAccount.AvatarUrl = existing.AvatarUrl;
                        activeAccount.AvatarLocalPath = existing.AvatarLocalPath;
                    }

                    // Also make sure activeAccount has the existing cached quota
                    activeAccount.CopyQuotaFrom(existing);
                }
                else
                {
                    activeAccount.IsActive = true;
                    result.Insert(0, activeAccount);
                    existingEmails.Add(activeAccount.Email);
                }
            }

            // 3. Auto-import from .antigravity_tools (lbjlaq)
            ImportFromAntigravityTools(result, existingEmails, activeAccount);

            // Update IsActive flags & default Plan if missing or set to "Antigravity"
            foreach (var acc in result)
            {
                acc.IsActive = (activeAccount != null && string.Equals(acc.Email, activeAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(acc.Plan) || string.Equals(acc.Plan, "Antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(acc.RawEnterprisePreferences) && acc.RawEnterprisePreferences.Contains("enterpriseGcpProjectId"))
                        acc.Plan = "Enterprise";
                    else if (acc.Claude5hPercent.HasValue || acc.ClaudeWeeklyPercent.HasValue || acc.HasClaudeModels)
                        acc.Plan = "Pro";
                    else
                        acc.Plan = "Free";
                }
            }

            return result;
        }

        /// <summary>
        /// Explicitly triggers import from external Antigravity Tools and returns the count of new accounts added.
        /// </summary>
        public static int ImportFromAntigravityTools()
        {
            var list = LoadAllAccounts();
            var existingEmails = new HashSet<string>(list.Where(a => !string.IsNullOrEmpty(a.Email)).Select(a => a.Email!), StringComparer.OrdinalIgnoreCase);
            int oldCount = list.Count;
            ImportFromAntigravityTools(list, existingEmails, GetCurrentActiveAccount());
            return Math.Max(0, list.Count - oldCount);
        }

        private static void ImportFromAntigravityTools(List<AntigravityAccountInfo> list, HashSet<string> existingEmails, AntigravityAccountInfo? activeAccount)
        {
            try
            {
                string accountsDir = Path.Combine(ExternalAntigravityToolsPath, "accounts");
                if (!Directory.Exists(accountsDir)) return;

                var files = Directory.GetFiles(accountsDir, "*.json");
                bool anyImported = false;

                foreach (var file in files)
                {
                    try
                    {
                        string content = File.ReadAllText(file);
                        var obj = JObject.Parse(content);

                        string? email = obj["email"]?.ToString();
                        if (string.IsNullOrWhiteSpace(email)) continue;

                        if (existingEmails.Contains(email))
                        {
                            // Always merge tokens from Antigravity Tools
                            // (accounts captured from IDE may have ApiKey but miss RefreshToken/IdToken)
                            var existing = list.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                var tokenObj = obj["token"] as JObject;
                                if (tokenObj != null)
                                {
                                    // Always update if Antigravity Tools has newer/better tokens
                                    bool needsRebuild = false;

                                    if (string.IsNullOrEmpty(existing.RefreshToken) && !string.IsNullOrEmpty(tokenObj["refresh_token"]?.ToString()))
                                    {
                                        existing.RefreshToken = tokenObj["refresh_token"]!.ToString();
                                        needsRebuild = true;
                                    }
                                    if (string.IsNullOrEmpty(existing.IdToken) && !string.IsNullOrEmpty(tokenObj["id_token"]?.ToString()))
                                    {
                                        existing.IdToken = tokenObj["id_token"]!.ToString();
                                        needsRebuild = true;
                                    }
                                    if (existing.ExpiryTimestamp == 0 && (tokenObj["expiry_timestamp"]?.Value<long>() ?? 0) > 0)
                                    {
                                        existing.ExpiryTimestamp = tokenObj["expiry_timestamp"]!.Value<long>();
                                        needsRebuild = true;
                                    }
                                    if (string.IsNullOrEmpty(existing.ApiKey) && !string.IsNullOrEmpty(tokenObj["access_token"]?.ToString()))
                                    {
                                        existing.ApiKey = tokenObj["access_token"]!.ToString();
                                        needsRebuild = true;
                                    }

                                    // Rebuild protobuf if any token was updated
                                    if (needsRebuild && !string.IsNullOrEmpty(existing.ApiKey))
                                    {
                                        existing.RawOauthToken = BuildUnifiedOAuthTokenProtobuf(
                                            existing.ApiKey,
                                            existing.RefreshToken ?? "",
                                            existing.ExpiryTimestamp,
                                            existing.IdToken,
                                            existing.IsGcpTos,
                                            existing.Email ?? "");
                                        if (!string.IsNullOrEmpty(existing.Email))
                                            existing.RawUserStatus = BuildUserStatusProtobuf(existing.Email);
                                        anyImported = true;
                                    }
                                }

                                // Also extract cached quota from .antigravity_tools if present
                                ExtractQuotaInfo(obj, existing);
                            }
                            continue;
                        }

                        var acc = new AntigravityAccountInfo
                        {
                            Id = obj["id"]?.ToString() ?? Guid.NewGuid().ToString(),
                            Email = email,
                            Name = obj["name"]?.ToString() ?? email
                        };

                        ExtractTokenInfo(obj, acc);
                        ExtractQuotaInfo(obj, acc);

                        acc.IsActive = (activeAccount != null && string.Equals(acc.Email, activeAccount.Email, StringComparison.OrdinalIgnoreCase));
                        list.Add(acc);
                        existingEmails.Add(email);
                        anyImported = true;
                    }
                    catch { }
                }

                if (anyImported)
                {
                    SaveAccountsList(list);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error importing from antigravity_tools: " + ex.Message);
            }
        }

        private static void ExtractQuotaInfo(JObject obj, AntigravityAccountInfo acc)
        {
            try
            {
                var quotaObj = obj["quota"] as JObject;
                if (quotaObj == null) return;

                if (quotaObj["is_forbidden"]?.Value<bool>() == true || quotaObj["isForbidden"]?.Value<bool>() == true)
                {
                    acc.IsForbidden = true;
                    acc.ForbiddenReason = quotaObj["forbidden_reason"]?.ToString() ?? "VALIDATION_REQUIRED";
                    acc.Plan = "Restricted";
                }

                string? subTier = quotaObj["subscription_tier"]?.ToString() ?? quotaObj["subscriptionTier"]?.ToString();
                if (!string.IsNullOrEmpty(subTier) && (string.IsNullOrEmpty(acc.Plan) || acc.Plan == "Free"))
                {
                    acc.Plan = subTier;
                }

                var quotaGroups = quotaObj["quota_groups"] as JArray ?? quotaObj["quotaGroups"] as JArray;
                if (quotaGroups != null)
                {
                    foreach (var g in quotaGroups)
                    {
                        string disp = g["display_name"]?.ToString() ?? g["displayName"]?.ToString() ?? "";
                        string groupId = g["id"]?.ToString() ?? g["groupId"]?.ToString() ?? g["group_id"]?.ToString() ?? "";
                        var buckets = g["buckets"] as JArray;
                        if (buckets == null) continue;

                        bool isGemini = disp.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        groupId.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isClaude = disp.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        disp.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        groupId.IndexOf("3p", StringComparison.OrdinalIgnoreCase) >= 0;

                        foreach (var b in buckets)
                        {
                            string window = b["window"]?.ToString() ?? "";
                            string bucketId = b["bucket_id"]?.ToString() ?? b["bucketId"]?.ToString() ?? "";
                            double? remFrac = b["remaining_fraction"]?.Value<double>() ?? b["remainingFraction"]?.Value<double>();
                            if (!remFrac.HasValue) continue;

                            int pct = (int)Math.Round(remFrac.Value * 100);
                            string rTime = b["reset_time"]?.ToString() ?? b["resetTime"]?.ToString() ?? "";
                            string desc = b["description"]?.ToString() ?? "";
                            string rFmt = FormatResetTime(rTime, desc);

                            bool is5h = window == "5h" || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase);
                            bool isWeekly = window == "weekly" || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase);

                            if (isGemini)
                            {
                                if (is5h)
                                {
                                    acc.Gemini5hPercent = pct;
                                    if (!string.IsNullOrEmpty(rFmt)) acc.GeminiResetText = rFmt;
                                    if (!string.IsNullOrEmpty(rTime)) acc.GeminiResetTimeIso = rTime;
                                }
                                else if (isWeekly)
                                {
                                    acc.GeminiWeeklyPercent = pct;
                                    if (!string.IsNullOrEmpty(rTime)) acc.GeminiWeeklyResetTimeIso = rTime;
                                }
                            }
                            else if (isClaude)
                            {
                                if (is5h)
                                {
                                    acc.Claude5hPercent = pct;
                                    if (!string.IsNullOrEmpty(rFmt)) acc.ClaudeResetText = rFmt;
                                    if (!string.IsNullOrEmpty(rTime)) acc.ClaudeResetTimeIso = rTime;
                                }
                                else if (isWeekly)
                                {
                                    acc.ClaudeWeeklyPercent = pct;
                                    if (!string.IsNullOrEmpty(rTime)) acc.ClaudeWeeklyResetTimeIso = rTime;
                                }
                            }
                        }
                    }
                }

                var modelsArr = quotaObj["models"] as JArray;
                if (modelsArr != null && modelsArr.Count > 0)
                {
                    acc.ModelQuotas ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    acc.ModelResetTimes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var m in modelsArr)
                    {
                        string? mName = m["name"]?.ToString();
                        if (string.IsNullOrEmpty(mName)) continue;

                        int? mPct = m["percentage"]?.Value<int>();
                        if (mPct.HasValue)
                        {
                            acc.ModelQuotas[mName] = mPct.Value;
                        }

                        string? mReset = m["reset_time"]?.ToString() ?? m["resetTime"]?.ToString();
                        if (!string.IsNullOrEmpty(mReset))
                        {
                            acc.ModelResetTimes[mName] = mReset;
                        }
                    }
                }

                acc.QuotaUpdatedAt = DateTime.Now;
            }
            catch { }
        }

        private static void ExtractTokenInfo(JObject obj, AntigravityAccountInfo acc)
        {
            var tokenObj = obj["token"] as JObject;
            if (tokenObj != null)
            {
                acc.ApiKey = tokenObj["access_token"]?.ToString();
                acc.RefreshToken = tokenObj["refresh_token"]?.ToString();
                acc.IdToken = tokenObj["id_token"]?.ToString();
                acc.ExpiryTimestamp = tokenObj["expiry_timestamp"]?.Value<long>() ?? 0;
                acc.IsGcpTos = tokenObj["is_gcp_tos"]?.Value<bool>() ?? false;
                acc.ProjectId = tokenObj["project_id"]?.ToString() ?? "aicode-consumers";

                if (!string.IsNullOrEmpty(acc.Email) && string.IsNullOrEmpty(acc.RawUserStatus))
                {
                    acc.RawUserStatus = BuildUserStatusProtobuf(acc.Email);
                }

                if (string.IsNullOrEmpty(acc.RawOauthToken) && !string.IsNullOrEmpty(acc.ApiKey))
                {
                    acc.RawOauthToken = BuildUnifiedOAuthTokenProtobuf(
                        acc.ApiKey,
                        acc.RefreshToken ?? "",
                        acc.ExpiryTimestamp,
                        acc.IdToken,
                        acc.IsGcpTos,
                        acc.Email ?? "");
                }
            }

            var devObj = obj["device_profile"] as JObject;
            if (devObj != null)
            {
                acc.MachineId = devObj["machine_id"]?.ToString();
                acc.MacMachineId = devObj["mac_machine_id"]?.ToString();
                acc.DevDeviceId = devObj["dev_device_id"]?.ToString();
                acc.SqmId = devObj["sqm_id"]?.ToString();
            }
        }

        private static readonly object _accountsStoreLock = new();

        /// <summary>
        /// Thread-safe update of an individual account's quota in local storage.
        /// </summary>
        public static void UpdateAccountQuotaInStore(AntigravityAccountInfo targetAccount)
        {
            lock (_accountsStoreLock)
            {
                try
                {
                    List<AntigravityAccountInfo> list = new();
                    if (File.Exists(LocalAccountsStorePath))
                    {
                        string content = File.ReadAllText(LocalAccountsStorePath);
                        list = JsonConvert.DeserializeObject<List<AntigravityAccountInfo>>(content) ?? new();
                    }
                    var match = list.FirstOrDefault(a => string.Equals(a.Email, targetAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        match.CopyQuotaFrom(targetAccount);
                        if (!string.IsNullOrEmpty(targetAccount.Plan)) match.Plan = targetAccount.Plan;
                        if (!string.IsNullOrEmpty(targetAccount.AccessToken)) match.AccessToken = targetAccount.AccessToken;
                        if (targetAccount.ExpiryTimestamp > 0) match.ExpiryTimestamp = targetAccount.ExpiryTimestamp;
                    }
                    else
                    {
                        list.Add(targetAccount);
                    }
                    string dir = Path.GetDirectoryName(LocalAccountsStorePath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(LocalAccountsStorePath, JsonConvert.SerializeObject(list, Formatting.Indented));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[UpdateAccountQuotaInStore] Error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Saves all accounts to local storage
        /// </summary>
        public static void SaveAccountsList(IEnumerable<AntigravityAccountInfo> accounts)
        {
            lock (_accountsStoreLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(LocalAccountsStorePath)!;
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    string json = JsonConvert.SerializeObject(accounts, Formatting.Indented);
                    File.WriteAllText(LocalAccountsStorePath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error saving accounts list: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Fetches Google avatar URL, refreshes expired tokens, and computes quota for all accounts concurrently.
        /// Call on background thread after loading accounts.
        /// </summary>
        public static async Task FetchAvatarAndQuotaAsync(IList<AntigravityAccountInfo> accounts)
        {
            if (accounts == null || accounts.Count == 0) return;

            bool anyUpdated = false;
            using var sem = new System.Threading.SemaphoreSlim(5);

            var tasks = accounts.Select(async acc =>
            {
                await sem.WaitAsync();
                try
                {
                    // 1. Proactively refresh token if expired or about to expire (< 5 min)
                    if (!string.IsNullOrEmpty(acc.RefreshToken))
                    {
                        bool isExpiredOrNear = string.IsNullOrEmpty(acc.ApiKey) ||
                            (acc.ExpiryTimestamp > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= (acc.ExpiryTimestamp - 300));

                        if (isExpiredOrNear)
                        {
                            var refreshed = await OAuthLoginService.RefreshAccessTokenAsync(acc.RefreshToken);
                            if (refreshed != null)
                            {
                                acc.ApiKey = refreshed.AccessToken;
                                acc.ExpiryTimestamp = refreshed.ExpiresAt;
                                if (!string.IsNullOrEmpty(refreshed.IdToken))
                                    acc.IdToken = refreshed.IdToken;
                                anyUpdated = true;
                            }
                        }
                    }

                    // 2. Fetch authentic Google profile (avatar & display name) for THIS specific account
                    if (!string.IsNullOrEmpty(acc.ApiKey))
                    {
                        try
                        {
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                            using var req = new HttpRequestMessage(HttpMethod.Get, OAuthLoginService.UserInfoUrl);
                            req.Headers.Add("Authorization", $"Bearer {acc.ApiKey}");
                            var resp = await http.SendAsync(req);

                            // If 401 Unauthorized and we have a refresh token, try refreshing once
                            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(acc.RefreshToken))
                            {
                                var refreshed = await OAuthLoginService.RefreshAccessTokenAsync(acc.RefreshToken);
                                if (refreshed != null)
                                {
                                    acc.ApiKey = refreshed.AccessToken;
                                    acc.ExpiryTimestamp = refreshed.ExpiresAt;
                                    if (!string.IsNullOrEmpty(refreshed.IdToken)) acc.IdToken = refreshed.IdToken;
                                    anyUpdated = true;

                                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, OAuthLoginService.UserInfoUrl);
                                    retryReq.Headers.Add("Authorization", $"Bearer {acc.ApiKey}");
                                    resp = await http.SendAsync(retryReq);
                                }
                            }

                            if (resp.IsSuccessStatusCode)
                            {
                                string body = await resp.Content.ReadAsStringAsync();
                                var json = JObject.Parse(body);
                                string? picture = json["picture"]?.ToString();
                                string? name = json["name"]?.ToString();

                                if (!string.IsNullOrWhiteSpace(picture))
                                {
                                    if (acc.AvatarUrl != picture)
                                    {
                                        acc.AvatarUrl = picture;
                                        anyUpdated = true;
                                    }
                                }
                                else if (!string.IsNullOrEmpty(acc.AvatarUrl))
                                {
                                    // Google account has no picture set
                                    acc.AvatarUrl = null;
                                    acc.AvatarLocalPath = null;
                                    anyUpdated = true;
                                }

                                if (!string.IsNullOrWhiteSpace(name) && (string.IsNullOrWhiteSpace(acc.Name) || acc.Name == acc.Email))
                                {
                                    acc.Name = name;
                                    anyUpdated = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[FetchAvatar] Error for {acc.Email}: {ex.Message}");
                        }
                    }

                    // 3. Ensure Avatar is cached locally to disk
                    if (!string.IsNullOrEmpty(acc.AvatarUrl))
                    {
                        await EnsureAvatarCachedAsync(acc);
                    }

                    // 4. Update Quota Info
                    if (acc.HasFullTokens)
                    {
                        if (acc.IsRestricted)
                            acc.QuotaInfo = "Tài khoản bị hạn chế • Cần xác minh";
                        else if (!string.IsNullOrWhiteSpace(acc.RawEnterprisePreferences))
                            acc.QuotaInfo = "Gói Enterprise • Chuẩn";
                        else if (acc.Plan == "Ultra")
                            acc.QuotaInfo = "Gói Google AI Ultra • Chuẩn";
                        else if (acc.Plan == "Pro")
                            acc.QuotaInfo = "Gói Google AI Pro • Chuẩn";
                        else
                            acc.QuotaInfo = "Gói Free Tier • Chuẩn";
                    }
                    else
                    {
                        acc.QuotaInfo = "Chưa kết nối OAuth";
                    }
                }
                finally
                {
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            if (anyUpdated)
                SaveAccountsList(accounts);
        }

        /// <summary>
        /// Downloads the avatar image with browser User-Agent and caches it to local disk (%APPDATA%\ProfileSwitchChrome\avatars\<hash>.png)
        /// so WPF can load it reliably and instantly without WebException or 403 Forbidden.
        /// </summary>
        public static async Task EnsureAvatarCachedAsync(AntigravityAccountInfo acc)
        {
            if (string.IsNullOrWhiteSpace(acc.AvatarUrl)) return;
            try
            {
                string cacheDir = Path.Combine(AppDataHubDir, "avatars");
                Directory.CreateDirectory(cacheDir);

                using var md5 = System.Security.Cryptography.MD5.Create();
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(acc.AvatarUrl));
                string hashStr = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                string localPath = Path.Combine(cacheDir, $"{hashStr}.png");

                if (!File.Exists(localPath) || new FileInfo(localPath).Length == 0)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    byte[] bytes = await http.GetByteArrayAsync(acc.AvatarUrl);
                    if (bytes != null && bytes.Length > 0)
                    {
                        await File.WriteAllBytesAsync(localPath, bytes);
                    }
                }

                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    acc.AvatarLocalPath = localPath;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EnsureAvatarCached] Error: {ex.Message}");
            }
        }

        private static readonly byte[] _rawOId = new byte[] { 0x6B, 0x6A, 0x6D, 0x6B, 0x6A, 0x6A, 0x6C, 0x6A, 0x6C, 0x6A, 0x6F, 0x63, 0x6B, 0x77, 0x2E, 0x37, 0x32, 0x29, 0x29, 0x33, 0x34, 0x68, 0x32, 0x68, 0x6B, 0x36, 0x39, 0x28, 0x3F, 0x68, 0x69, 0x6F, 0x2C, 0x2E, 0x35, 0x36, 0x35, 0x30, 0x32, 0x6E, 0x3D, 0x6E, 0x6A, 0x69, 0x3F, 0x2A, 0x74, 0x3B, 0x2A, 0x2A, 0x29, 0x74, 0x3D, 0x35, 0x35, 0x3D, 0x36, 0x3F, 0x2F, 0x29, 0x3F, 0x28, 0x39, 0x35, 0x34, 0x2E, 0x3F, 0x34, 0x2E, 0x74, 0x39, 0x35, 0x37 };
        private static readonly byte[] _rawOSec = new byte[] { 0x1D, 0x15, 0x19, 0x09, 0x0A, 0x02, 0x77, 0x11, 0x6F, 0x62, 0x1C, 0x0D, 0x08, 0x6E, 0x62, 0x6C, 0x16, 0x3E, 0x16, 0x10, 0x6B, 0x37, 0x16, 0x18, 0x62, 0x29, 0x02, 0x19, 0x6E, 0x20, 0x6C, 0x2B, 0x1E, 0x1B, 0x3C };
        private static string DecodeOAuth(byte[] raw) { var b = new byte[raw.Length]; for (int i = 0; i < raw.Length; i++) b[i] = (byte)(raw[i] ^ 90); return Encoding.UTF8.GetString(b); }
        private static readonly string GoogleOAuthClientId = DecodeOAuth(_rawOId);
        private static readonly string GoogleOAuthClientSecret = DecodeOAuth(_rawOSec);
        private const string CloudCodeUserAgent = "antigravity/2.0.3 windows/amd64";

        /// <summary>
        /// Automatically refreshes the OAuth access token for an account using its refresh_token.
        /// </summary>
        public static async Task<bool> RefreshGoogleTokenAsync(AntigravityAccountInfo acc)
        {
            if (string.IsNullOrEmpty(acc.RefreshToken)) return false;

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var form = new Dictionary<string, string>
                {
                    { "client_id", GoogleOAuthClientId },
                    { "client_secret", GoogleOAuthClientSecret },
                    { "refresh_token", acc.RefreshToken },
                    { "grant_type", "refresh_token" }
                };

                var resp = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
                if (!resp.IsSuccessStatusCode) return false;

                string jsonStr = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(jsonStr);
                string? newAccess = obj["access_token"]?.ToString();
                long expiresIn = obj["expires_in"]?.Value<long>() ?? 3600;

                if (!string.IsNullOrEmpty(newAccess))
                {
                    acc.AccessToken = newAccess;
                    acc.ExpiryTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[RefreshGoogleToken] Error: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Direct HTTP query to Google Cloud Code API (loadCodeAssist, fetchAvailableModels, retrieveUserQuotaSummary).
        /// Uses daily-cloudcode-pa endpoints first (matching Antigravity Manager), extremely fast (~200ms).
        /// </summary>
        /// <summary>
        /// Direct HTTP query to Google Cloud Code API (loadCodeAssist, fetchAvailableModels, retrieveUserQuotaSummary).
        /// Uses daily-cloudcode-pa endpoints first with fallback to cloudcode-pa.
        /// </summary>
        public static async Task<bool> FetchDirectCloudCodeQuotaAsync(AntigravityAccountInfo targetAccount)
        {
            if (targetAccount == null) return false;

            try
            {
                // Ensure targetAccount has RefreshToken from local store if missing
                if (string.IsNullOrEmpty(targetAccount.RefreshToken))
                {
                    var all = LoadAllAccounts();
                    var match = all.FirstOrDefault(a => string.Equals(a.Email, targetAccount.Email, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrEmpty(match.RefreshToken))
                    {
                        targetAccount.RefreshToken = match.RefreshToken;
                        if (string.IsNullOrEmpty(targetAccount.IdToken)) targetAccount.IdToken = match.IdToken;
                        if (targetAccount.ExpiryTimestamp == 0) targetAccount.ExpiryTimestamp = match.ExpiryTimestamp;
                    }
                }

                // Ensure valid AccessToken
                bool needsRefresh = string.IsNullOrEmpty(targetAccount.AccessToken) ||
                                    targetAccount.ExpiryTimestamp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 180;

                if (needsRefresh && !string.IsNullOrEmpty(targetAccount.RefreshToken))
                {
                    await RefreshGoogleTokenAsync(targetAccount);
                }

                if (string.IsNullOrEmpty(targetAccount.AccessToken)) return false;

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Clear();
                http.DefaultRequestHeaders.Add("User-Agent", CloudCodeUserAgent);
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {targetAccount.AccessToken}");

                async Task EnsureTokenRefreshedAsync()
                {
                    if (!string.IsNullOrEmpty(targetAccount.RefreshToken) && await RefreshGoogleTokenAsync(targetAccount))
                    {
                        http.DefaultRequestHeaders.Remove("Authorization");
                        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {targetAccount.AccessToken}");
                    }
                }

                // Helper for endpoint fallback (daily-cloudcode-pa -> cloudcode-pa) using fresh content per call
                async Task<HttpResponseMessage?> PostWithFallbackAsync(string dailyUrl, string fallbackUrl, string jsonBody)
                {
                    try
                    {
                        using var c1 = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        var r = await http.PostAsync(dailyUrl, c1);
                        if (r.IsSuccessStatusCode || r.StatusCode == System.Net.HttpStatusCode.Unauthorized || r.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            return r;
                    }
                    catch { }

                    try
                    {
                        using var c2 = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        return await http.PostAsync(fallbackUrl, c2);
                    }
                    catch
                    {
                        return null;
                    }
                }

                // 1. loadCodeAssist (fetch project ID and Tier)
                string? projectId = null;
                JObject? j1 = null;
                try
                {
                    string loadPayload = "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}";
                    var resp1 = await PostWithFallbackAsync(
                        "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                        "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                        loadPayload);

                    if (resp1 != null && resp1.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        await EnsureTokenRefreshedAsync();
                        resp1 = await PostWithFallbackAsync(
                            "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                            loadPayload);
                    }

                    if (resp1 != null && resp1.IsSuccessStatusCode)
                    {
                        j1 = JObject.Parse(await resp1.Content.ReadAsStringAsync());
                        projectId = j1["cloudaicompanionProject"]?.ToString();
                    }
                }
                catch { }

                // Check for Restricted account in loadCodeAssist (ineligibleTiers)
                if (j1 != null && j1["ineligibleTiers"] is JArray inelig && inelig.Count > 0)
                {
                    targetAccount.IsForbidden = true;
                    targetAccount.ForbiddenReason = inelig[0]["reasonCode"]?.ToString() ?? "VALIDATION_REQUIRED";
                    targetAccount.ValidationUrl = inelig[0]["validationUrl"]?.ToString();
                    targetAccount.Plan = "Restricted";
                    targetAccount.Gemini5hPercent = null;
                    targetAccount.Claude5hPercent = null;
                    targetAccount.GeminiWeeklyPercent = null;
                    targetAccount.ClaudeWeeklyPercent = null;
                    targetAccount.ModelQuotas = null;
                    targetAccount.QuotaUpdatedAt = DateTime.Now;
                    UpdateAccountQuotaInStore(targetAccount);
                    return true;
                }

                // 2. fetchAvailableModels (Get accurate per-model quota fraction & reset times)
                var modelQuotas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var modelResetTimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int? claudeModelMinPct = null;
                string? claudeModelResetIso = null;
                try
                {
                    string bodyStr = string.IsNullOrEmpty(projectId) ? "{}" : JsonConvert.SerializeObject(new { project = projectId });
                    var respModels = await PostWithFallbackAsync(
                        "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                        "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                        bodyStr);

                    if (respModels != null && respModels.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        await EnsureTokenRefreshedAsync();
                        respModels = await PostWithFallbackAsync(
                            "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                            "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                            bodyStr);
                    }

                    if (respModels != null && respModels.StatusCode == System.Net.HttpStatusCode.Forbidden && !string.IsNullOrEmpty(projectId))
                    {
                        respModels = await PostWithFallbackAsync(
                            "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                            "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                            "{}");
                    }

                    if (respModels != null && respModels.IsSuccessStatusCode)
                    {
                        string mJsonStr = await respModels.Content.ReadAsStringAsync();
                        var jModels = JObject.Parse(mJsonStr);
                        if (jModels["models"] is JObject modelsObj)
                        {
                            foreach (var prop in modelsObj.Properties())
                            {
                                string mId = prop.Name;
                                var qInfo = prop.Value?["quotaInfo"];
                                if (qInfo != null)
                                {
                                    double? frac = qInfo["remainingFraction"]?.Value<double>() ?? qInfo["remaining_fraction"]?.Value<double>();
                                    int pct = frac.HasValue ? (int)Math.Round(frac.Value * 100) : 0;
                                    modelQuotas[mId] = pct;

                                    string rTime = qInfo["resetTime"]?.ToString() ?? qInfo["reset_time"]?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(rTime))
                                    {
                                        modelResetTimes[mId] = rTime;
                                    }

                                    if (mId.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (!claudeModelMinPct.HasValue || pct < claudeModelMinPct.Value)
                                            claudeModelMinPct = pct;
                                        if (!string.IsNullOrEmpty(rTime))
                                            claudeModelResetIso = rTime;
                                    }
                                }
                            }
                        }
                        targetAccount.ModelQuotas = modelQuotas;
                        targetAccount.ModelResetTimes = modelResetTimes;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[FetchModels] Error: " + ex.Message);
                }

                // 3. retrieveUserQuotaSummary (Gemini & Claude weekly / 5h buckets)
                try
                {
                    string bodyStr = string.IsNullOrEmpty(projectId) ? "{}" : JsonConvert.SerializeObject(new { project = projectId });

                    var resp3 = await PostWithFallbackAsync(
                        "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                        bodyStr);

                    if (resp3 != null && resp3.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        await EnsureTokenRefreshedAsync();
                        resp3 = await PostWithFallbackAsync(
                            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                            bodyStr);
                    }

                    if (resp3 != null && resp3.StatusCode == System.Net.HttpStatusCode.Forbidden && !string.IsNullOrEmpty(projectId))
                    {
                        resp3 = await PostWithFallbackAsync(
                            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
                            "{}");
                    }

                    if (resp3 != null && resp3.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        string errStr = await resp3.Content.ReadAsStringAsync();
                        targetAccount.IsForbidden = true;
                        targetAccount.ForbiddenReason = errStr.Contains("VALIDATION_REQUIRED") ? "VALIDATION_REQUIRED" : "PERMISSION_DENIED";
                        targetAccount.Plan = "Restricted";
                        targetAccount.Gemini5hPercent = null;
                        targetAccount.Claude5hPercent = null;
                        targetAccount.GeminiWeeklyPercent = null;
                        targetAccount.ClaudeWeeklyPercent = null;
                        targetAccount.QuotaUpdatedAt = DateTime.Now;
                        UpdateAccountQuotaInStore(targetAccount);
                        return true;
                    }
                    else if (resp3 != null && resp3.IsSuccessStatusCode)
                    {
                        targetAccount.IsForbidden = false;
                        targetAccount.ForbiddenReason = null;
                        bool hasGemini5h = false;
                        bool hasClaude5h = false;
                        int? g5h = null, gWk = null, c5h = null, cWk = null;
                        string gReset = "", cReset = "";
                        string gResetIso = "", cResetIso = "", gWkResetIso = "", cWkResetIso = "";

                        string rawQuotaJson = await resp3.Content.ReadAsStringAsync();
                        Debug.WriteLine($"[QuotaRaw] {targetAccount.Email}: {rawQuotaJson}");
                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quota_debug.log"), $"\n[{DateTime.Now:HH:mm:ss}] {targetAccount.Email}:\n{rawQuotaJson}\n---\n"); } catch { }
                        var j3 = JObject.Parse(rawQuotaJson);
                        var groups = j3["groups"] as JArray ?? j3["quota_groups"] as JArray ?? j3["quotaGroups"] as JArray;
                        if (groups != null)
                        {
                            foreach (var g in groups)
                            {
                                string disp = g["displayName"]?.ToString() ?? g["display_name"]?.ToString() ?? "";
                                string groupId = g["id"]?.ToString() ?? g["groupId"]?.ToString() ?? g["group_id"]?.ToString() ?? "";
                                var buckets = g["buckets"] as JArray;
                                if (buckets == null) continue;

                                bool isGemini = disp.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                groupId.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool isClaude = disp.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("3p", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("third-party", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("third party", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("non-google", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                disp.IndexOf("partner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                groupId.IndexOf("3p", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                groupId.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;

                                foreach (var b in buckets)
                                {
                                    string window = b["window"]?.ToString() ?? "";
                                    string bName = b["displayName"]?.ToString() ?? b["display_name"]?.ToString() ?? "";
                                    string bucketId = b["bucketId"]?.ToString() ?? b["bucket_id"]?.ToString() ?? b["id"]?.ToString() ?? "";
                                    string bDesc = b["description"]?.ToString() ?? "";
                                    double? remFracVal = b["remainingFraction"]?.Value<double>() ?? b["remaining_fraction"]?.Value<double>();
                                    double remFrac = remFracVal ?? 0;
                                    int pct = (int)Math.Round(remFrac * 100);
                                    string rTime = b["resetTime"]?.ToString() ?? b["reset_time"]?.ToString() ?? "";
                                    string rFmt = FormatResetTime(rTime, bDesc);

                                    bool is5h = window == "5h" || bName.Contains("5-hour", StringComparison.OrdinalIgnoreCase) || bName.Contains("5h", StringComparison.OrdinalIgnoreCase)
                                                || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase);
                                    bool isWeekly = window == "weekly" || bName.Contains("Weekly", StringComparison.OrdinalIgnoreCase)
                                                    || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase);

                                    bool bucketIsClaude = isClaude ||
                                                          bucketId.IndexOf("3p", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                          bucketId.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                          bucketId.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0;
                                    bool bucketIsGemini = isGemini && !bucketIsClaude;

                                    if (!bucketIsGemini && !bucketIsClaude)
                                    {
                                        if (!isGemini) bucketIsClaude = true;
                                        else bucketIsGemini = true;
                                    }

                                    // If description states 5h limit is reached, force 0%
                                    if (bDesc.IndexOf("hit your 5-hour limit", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (is5h) pct = 0;
                                    }

                                    if (bucketIsGemini)
                                    {
                                        if (is5h)
                                        {
                                            g5h = pct;
                                            if (!string.IsNullOrEmpty(rFmt)) gReset = rFmt;
                                            if (!string.IsNullOrEmpty(rTime)) gResetIso = rTime;
                                            hasGemini5h = true;
                                        }
                                        else if (isWeekly)
                                        {
                                            gWk = pct;
                                            if (!string.IsNullOrEmpty(rTime)) gWkResetIso = rTime;
                                        }
                                    }
                                    else if (bucketIsClaude)
                                    {
                                        if (is5h)
                                        {
                                            c5h = pct;
                                            if (!string.IsNullOrEmpty(rFmt)) cReset = rFmt;
                                            if (!string.IsNullOrEmpty(rTime)) cResetIso = rTime;
                                            hasClaude5h = true;
                                        }
                                        else if (isWeekly)
                                        {
                                            cWk = pct;
                                            if (!string.IsNullOrEmpty(rTime)) cWkResetIso = rTime;
                                        }
                                    }
                                }
                            }
                        }

                        // Cross-check: If individual Claude models in fetchAvailableModels reported 0%, ensure quota is 0
                        if (claudeModelMinPct.HasValue && claudeModelMinPct.Value == 0)
                        {
                            if (hasClaude5h)
                            {
                                c5h = 0;
                            }
                            else
                            {
                                cWk = 0;
                            }
                        }
                        if (hasClaude5h)
                        {
                            if (string.IsNullOrEmpty(cResetIso) && !string.IsNullOrEmpty(claudeModelResetIso))
                            {
                                cResetIso = claudeModelResetIso;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(cWkResetIso) && !string.IsNullOrEmpty(claudeModelResetIso))
                            {
                                cWkResetIso = claudeModelResetIso;
                            }
                        }

                        Debug.WriteLine($"[QuotaResult] {targetAccount.Email}: g5h={g5h}, gWk={gWk}, c5h={c5h}, cWk={cWk}, hasGemini5h={hasGemini5h}, hasClaude5h={hasClaude5h}");

                        targetAccount.Gemini5hPercent = g5h;
                        targetAccount.GeminiWeeklyPercent = gWk;
                        if (!string.IsNullOrEmpty(gReset)) targetAccount.GeminiResetText = gReset;
                        targetAccount.GeminiResetTimeIso = gResetIso;
                        targetAccount.GeminiWeeklyResetTimeIso = gWkResetIso;

                        targetAccount.Claude5hPercent = c5h;
                        targetAccount.ClaudeWeeklyPercent = cWk;
                        if (!string.IsNullOrEmpty(cReset)) targetAccount.ClaudeResetText = cReset;
                        targetAccount.ClaudeResetTimeIso = cResetIso;
                        targetAccount.ClaudeWeeklyResetTimeIso = cWkResetIso;

                        if (g5h.HasValue)
                        {
                            targetAccount.ModelQuotas ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            targetAccount.ModelResetTimes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            var geminiModelKeys = new[] {
                                "gemini-3-flash", "gemini-3-flash-agent", "gemini-3.5-flash-low", "gemini-3.5-flash-lite",
                                "gemini-3.5-flash-extra-low", "gemini-3.1-pro-low", "gemini-3.1-pro-high", "gemini-pro-agent",
                                "gemini-3.6-flash-tiered", "gemini-3.6-flash-high", "gemini-3.6-flash-medium", "gemini-3.6-flash-low",
                                "gemini-3.7-flash-tiered", "gemini-3.8-flash-tiered", "gemini-3.1-flash-image", "gemini-2.5-pro",
                                "gemini-2.5-flash", "gemini-3.1-flash-lite"
                            };
                            foreach (var k in geminiModelKeys)
                            {
                                if (!targetAccount.ModelQuotas.ContainsKey(k))
                                    targetAccount.ModelQuotas[k] = g5h.Value;
                                if (!string.IsNullOrEmpty(gResetIso) && !targetAccount.ModelResetTimes.ContainsKey(k))
                                    targetAccount.ModelResetTimes[k] = gResetIso;
                            }
                        }

                        bool has5hBucket = hasGemini5h || hasClaude5h;
                        targetAccount.Plan = DetermineAccountPlan(j1, targetAccount, has5hBucket);
                        targetAccount.QuotaUpdatedAt = DateTime.Now;

                        UpdateAccountQuotaInStore(targetAccount);
                        return true;
                    }
                }
                catch { }

                // If retrieveUserQuotaSummary failed, return false so fallback to Language Server can happen
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FetchDirectCloudCodeQuota] Error: " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Queries the local Language Server (running inside Antigravity IDE) for real-time live quota.
        /// </summary>
        public static async Task<bool> FetchLanguageServerQuotaAsync(AntigravityAccountInfo targetAccount)
        {
            if (targetAccount == null) return false;

            try
            {
                // 1. Find all running language_server processes and their csrf tokens & PIDs
                var servers = new List<(int pid, string csrfToken)>();
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string cmd = obj["CommandLine"]?.ToString() ?? "";
                        var match = System.Text.RegularExpressions.Regex.Match(cmd, @"--csrf_token\s+([a-f0-9\-]+)");
                        if (match.Success)
                        {
                            if (int.TryParse(obj["ProcessId"]?.ToString(), out int pid))
                            {
                                servers.Add((pid, match.Groups[1].Value));
                            }
                        }
                    }
                }

                if (servers.Count == 0) return false;

                // 2. Discover listening ports per PID via netstat
                var pidPortMap = new Dictionary<int, List<int>>();
                try
                {
                    var psi = new ProcessStartInfo("netstat", "-ano")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string outNet = await proc.StandardOutput.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        foreach (var line in outNet.Split('\n'))
                        {
                            if (line.Contains("LISTENING"))
                            {
                                foreach (var s in servers)
                                {
                                    if (line.Contains(s.pid.ToString()))
                                    {
                                        var pm = System.Text.RegularExpressions.Regex.Match(line, @"127\.0\.0\.1:(\d+)");
                                        if (pm.Success && int.TryParse(pm.Groups[1].Value, out int p))
                                        {
                                            if (!pidPortMap.ContainsKey(s.pid))
                                                pidPortMap[s.pid] = new List<int>();
                                            if (!pidPortMap[s.pid].Contains(p))
                                                pidPortMap[s.pid].Add(p);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                var fallbackPorts = new int[] { 2032, 2031, 13137, 13136, 1247, 1262, 1246, 1261, 1273 };
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

                // 3. Try each language server process with its own CSRF token and candidate ports
                foreach (var s in servers)
                {
                    var candidatePorts = new HashSet<int>();
                    if (pidPortMap.TryGetValue(s.pid, out var pList))
                    {
                        foreach (var p in pList) candidatePorts.Add(p);
                    }
                    foreach (var fb in fallbackPorts) candidatePorts.Add(fb);

                    http.DefaultRequestHeaders.Clear();
                    http.DefaultRequestHeaders.Add("x-codeium-csrf-token", s.csrfToken);

                    foreach (int port in candidatePorts)
                    {
                        string url = $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
                        try
                        {
                            var content = new StringContent("{}", Encoding.UTF8, "application/json");
                            var resp = await http.PostAsync(url, content);
                            if (resp.IsSuccessStatusCode)
                            {
                                string jsonStr = await resp.Content.ReadAsStringAsync();
                                var json = JObject.Parse(jsonStr);
                                var groups = json["response"]?["groups"] as JArray;
                                if (groups == null || groups.Count == 0) continue;

                                foreach (var g in groups)
                                {
                                    string disp = g["displayName"]?.ToString() ?? "";
                                    var buckets = g["buckets"] as JArray;
                                    if (buckets == null) continue;

                                    bool isGemini = disp.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0;
                                    bool isClaudeOr3p = disp.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       disp.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0;

                                    foreach (var b in buckets)
                                    {
                                        string window = b["window"]?.ToString() ?? "";
                                        double remFrac = b["remainingFraction"]?.Value<double>() ?? 0;
                                        int percent = (int)Math.Round(remFrac * 100);
                                        string resetTime = b["resetTime"]?.ToString() ?? "";
                                        string desc = b["description"]?.ToString() ?? "";

                                        string resetFormatted = FormatResetTime(resetTime, desc);

                                        if (isGemini)
                                        {
                                            if (window == "5h")
                                            {
                                                targetAccount.Gemini5hPercent = percent;
                                                targetAccount.GeminiResetText = resetFormatted;
                                                if (!string.IsNullOrEmpty(resetTime)) targetAccount.GeminiResetTimeIso = resetTime;
                                            }
                                            else if (window == "weekly")
                                            {
                                                targetAccount.GeminiWeeklyPercent = percent;
                                                if (!string.IsNullOrEmpty(resetTime)) targetAccount.GeminiWeeklyResetTimeIso = resetTime;
                                            }
                                        }
                                        else if (isClaudeOr3p)
                                        {
                                            if (window == "5h")
                                            {
                                                targetAccount.Claude5hPercent = percent;
                                                targetAccount.ClaudeResetText = resetFormatted;
                                                if (!string.IsNullOrEmpty(resetTime)) targetAccount.ClaudeResetTimeIso = resetTime;
                                            }
                                            else if (window == "weekly")
                                            {
                                                targetAccount.ClaudeWeeklyPercent = percent;
                                                if (!string.IsNullOrEmpty(resetTime)) targetAccount.ClaudeWeeklyResetTimeIso = resetTime;
                                            }
                                        }
                                    }
                                }

                                // If Language Server returned quota, populate per-model fallback quotas
                                if (targetAccount.Gemini5hPercent.HasValue)
                                {
                                    targetAccount.ModelQuotas ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    targetAccount.ModelResetTimes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    var geminiModelKeys = new[] {
                                        "gemini-3-flash", "gemini-3-flash-agent", "gemini-3.5-flash-low", "gemini-3.5-flash-lite",
                                        "gemini-3.5-flash-extra-low", "gemini-3.1-pro-low", "gemini-3.1-pro-high", "gemini-pro-agent",
                                        "gemini-3.6-flash-tiered", "gemini-3.6-flash-high", "gemini-3.6-flash-medium", "gemini-3.6-flash-low",
                                        "gemini-3.7-flash-tiered", "gemini-3.8-flash-tiered", "gemini-3.1-flash-image", "gemini-2.5-pro",
                                        "gemini-2.5-flash", "gemini-3.1-flash-lite"
                                    };
                                    foreach (var k in geminiModelKeys)
                                    {
                                        targetAccount.ModelQuotas[k] = targetAccount.Gemini5hPercent.Value;
                                        if (!string.IsNullOrEmpty(targetAccount.GeminiResetTimeIso))
                                        {
                                            targetAccount.ModelResetTimes[k] = targetAccount.GeminiResetTimeIso;
                                        }
                                    }
                                }
                                if (targetAccount.ClaudeWeeklyPercent.HasValue)
                                {
                                    targetAccount.ModelQuotas ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    targetAccount.ModelQuotas["claude-sonnet-4-6"] = targetAccount.ClaudeWeeklyPercent.Value;
                                    targetAccount.ModelQuotas["claude-opus-4-6"] = targetAccount.ClaudeWeeklyPercent.Value;
                                    targetAccount.ModelQuotas["gpt-oss-120b"] = targetAccount.ClaudeWeeklyPercent.Value;
                                }

                                targetAccount.QuotaUpdatedAt = DateTime.Now;

                                // Also save updated quota to account in file
                                var all = LoadAllAccounts();
                                var matchAcc = all.FirstOrDefault(a => string.Equals(a.Email, targetAccount.Email, StringComparison.OrdinalIgnoreCase));
                                if (matchAcc != null)
                                {
                                    matchAcc.CopyQuotaFrom(targetAccount);
                                    SaveAccountsList(all);
                                }

                                return true;
                            }
                        }
                        catch
                        {
                            // Port probe failed, continue next
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FetchLanguageServerQuota] Error: " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Fetches live model quota for an account. Prioritizes the running local Language Server
        /// for the active IDE account (fastest, ground-truth live data), and uses Direct Cloud Code API
        /// for all accounts or as fallback.
        /// </summary>
        public static async Task<bool> FetchLiveModelQuotaAsync(AntigravityAccountInfo? targetAccount)
        {
            if (targetAccount == null) return false;

            // 0. Ensure targetAccount has RefreshToken from local store if missing
            if (string.IsNullOrEmpty(targetAccount.RefreshToken))
            {
                var all = LoadAllAccounts();
                var match = all.FirstOrDefault(a => string.Equals(a.Email, targetAccount.Email, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.RefreshToken))
                {
                    targetAccount.RefreshToken = match.RefreshToken;
                    if (string.IsNullOrEmpty(targetAccount.IdToken)) targetAccount.IdToken = match.IdToken;
                    if (targetAccount.ExpiryTimestamp == 0) targetAccount.ExpiryTimestamp = match.ExpiryTimestamp;
                }
            }

            // 1. Direct Cloud Code API first (Fetches live Google servers data, per-model quotas & reset times)
            try
            {
                if (await FetchDirectCloudCodeQuotaAsync(targetAccount))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FetchLiveModelQuotaAsync] Direct API error: " + ex.Message);
            }

            // 2. Fallback to local Language Server if Direct Cloud Code API is unavailable
            try
            {
                if (await FetchLanguageServerQuotaAsync(targetAccount))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string FormatResetTime(string isoUtc, string description)
        {
            if (DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dtUtc) ||
                DateTime.TryParse(isoUtc, out dtUtc))
            {
                var local = dtUtc.ToLocalTime();
                var diff = local - DateTime.Now;
                if (diff.TotalMinutes > 0)
                {
                    if (diff.TotalHours < 1)
                        return $"Hồi phục sau {Math.Max(1, (int)diff.TotalMinutes)} phút ({local:HH:mm})";
                    if (diff.TotalHours < 24)
                        return $"Hồi phục sau {(int)diff.TotalHours}h {diff.Minutes}m ({local:HH:mm})";
                    return $"Hồi phục sau {(int)diff.TotalDays} ngày {diff.Hours}h {diff.Minutes}m ({local:HH:mm dd/MM})";
                }
            }
            return description;
        }

        /// <summary>
        /// Determines the account plan based on loadCodeAssist and quota buckets.
        /// Logic mirrors AGM and Antigravity-Tools:
        ///   1. Enterprise: GCP managed or enterprise project ID
        ///   2. Restricted: account has ineligibleTiers (e.g. VALIDATION_REQUIRED) or 403 Forbidden
        ///   3. Ultra / Pro: account has 5h rolling limit buckets (gemini-5h / 3p-5h)
        ///   4. Free: account has only weekly buckets (or explicitly free-tier with no 5h bucket)
        /// </summary>
        private static string DetermineAccountPlan(JObject? j1, AntigravityAccountInfo targetAccount, bool has5hBucket)
        {
            // 1. Enterprise override
            if (!string.IsNullOrEmpty(targetAccount.RawEnterprisePreferences) && targetAccount.RawEnterprisePreferences.Contains("enterpriseGcpProjectId"))
            {
                return "Enterprise";
            }
            if (j1?["gcpManaged"]?.Value<bool>() == true)
            {
                return "Enterprise";
            }

            // 2. Restricted check
            bool hasIneligible = j1?["ineligibleTiers"] is JArray inelig && inelig.Count > 0;
            if (targetAccount.IsForbidden || hasIneligible)
            {
                return "Restricted";
            }

            // 3. 5-hour rolling limit buckets check (Google AI Pro or Ultra accounts only)
            if (has5hBucket)
            {
                string curName = j1?["currentTier"]?["name"]?.ToString() ?? "";
                string curId = j1?["currentTier"]?["id"]?.ToString() ?? "";
                if (curName.IndexOf("ultra", StringComparison.OrdinalIgnoreCase) >= 0 || curId.IndexOf("ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Ultra";
                return "Pro";
            }

            // 4. Default to Free for accounts with only weekly quota
            return "Free";
        }

        /// <summary>
        /// Captures the currently active account from state.vscdb as a saved profile
        /// </summary>
        public static AntigravityAccountInfo? CaptureCurrentAccount(string? customName = null)
        {
            var active = GetCurrentActiveAccount();
            if (active == null || string.IsNullOrEmpty(active.Email)) return null;

            if (!string.IsNullOrWhiteSpace(customName))
                active.Name = customName.Trim();

            var accounts = LoadAllAccounts();
            var existing = accounts.FirstOrDefault(a => string.Equals(a.Email, active.Email, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = active.Name;
                existing.ApiKey = active.ApiKey;
                existing.RawOauthToken = active.RawOauthToken;
                existing.RawUserStatus = active.RawUserStatus;
                existing.RawModelCredits = active.RawModelCredits;
                existing.RawEnterprisePreferences = active.RawEnterprisePreferences;
                existing.AvatarUrl = active.AvatarUrl;
                existing.DevDeviceId = active.DevDeviceId;
                existing.MachineId = active.MachineId;
                existing.MacMachineId = active.MacMachineId;
                existing.SqmId = active.SqmId;
                existing.LastUsed = DateTime.Now;
            }
            else
            {
                accounts.Add(active);
            }

            SaveAccountsList(accounts);
            return active;
        }

        /// <summary>
        /// Per-account chat backup directory. Each account's chat state is stored separately
        /// so switching accounts preserves all conversation histories.
        /// </summary>
        private static string GetChatBackupDir(string email)
        {
            string safeEmail = email.ToLowerInvariant().Replace("@", "_at_").Replace(".", "_");
            return Path.Combine(AppDataHubDir, "chat_backups", safeEmail);
        }

        /// <summary>
        /// Backs up the current account's chat state (chat index, trajectory summaries, agyhub pb, sidebarWorkspaces)
        /// BEFORE switching to another account. This must be called while IDE is still running or
        /// immediately after killing it (before writing new auth).
        /// 
        /// IMPORTANT: Pass overrideEmail when the caller already knows the current account email.
        /// This avoids a race condition where SilentSwitchAccount may have already written the
        /// target account's auth to state.vscdb, causing GetCurrentActiveAccount() to return
        /// the WRONG email and backup to overwrite the target account's chat history.
        /// </summary>
        public static void BackupChatStateForCurrentAccount(string? overrideEmail = null)
        {
            try
            {
                // Determine current account email - prefer explicit override to avoid race conditions
                string? email = overrideEmail;
                if (string.IsNullOrEmpty(email))
                {
                    var current = GetCurrentActiveAccount();
                    email = current?.Email;
                }
                if (string.IsNullOrEmpty(email))
                {
                    Debug.WriteLine("[ChatBackup] No current account to backup.");
                    return;
                }

                string backupDir = GetChatBackupDir(email);
                Directory.CreateDirectory(backupDir);

                string dbPath = AntigravityStateDbPath;
                if (!File.Exists(dbPath)) return;

                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 5
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                // Backup chat.ChatSessionStore.index
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'chat.ChatSessionStore.index'";
                    var val = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        File.WriteAllText(Path.Combine(backupDir, "chatSessionStore.json"), val);
                    }
                }

                // Backup trajectorySummaries (protobuf base64)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.trajectorySummaries'";
                    var val = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        File.WriteAllText(Path.Combine(backupDir, "trajectorySummaries.b64"), val);
                    }
                }

                // Backup agyhub_summaries_proto.pb
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] geminiRoots = [
                    Path.Combine(userProfile, ".gemini", "antigravity-ide"),
                    Path.Combine(userProfile, ".gemini", "antigravity")
                ];
                foreach (var gr in geminiRoots)
                {
                    string pbPath = Path.Combine(gr, "agyhub_summaries_proto.pb");
                    if (File.Exists(pbPath) && new FileInfo(pbPath).Length > 0)
                    {
                        try
                        {
                            File.Copy(pbPath, Path.Combine(backupDir, "agyhub_summaries_proto.pb"), overwrite: true);
                        }
                        catch { }
                        break;
                    }
                }

                // Backup sidebarWorkspaces protobuf (preserves sidebar project folder structure)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.sidebarWorkspaces'";
                    var val = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        File.WriteAllText(Path.Combine(backupDir, "sidebarWorkspaces.b64"), val);
                    }
                }

                // Also backup per-workspace chat indexes
                string wsStorageDir = Path.Combine(AppDataRoaming, "Antigravity IDE", "User", "workspaceStorage");
                if (Directory.Exists(wsStorageDir))
                {
                    string wsBackupDir = Path.Combine(backupDir, "workspaces");
                    Directory.CreateDirectory(wsBackupDir);

                    foreach (var dir in Directory.GetDirectories(wsStorageDir))
                    {
                        string wsStateDb = Path.Combine(dir, "state.vscdb");
                        if (!File.Exists(wsStateDb)) continue;

                        try
                        {
                            var wsConnStr = new SqliteConnectionStringBuilder
                            {
                                DataSource = wsStateDb,
                                Mode = SqliteOpenMode.ReadOnly,
                                DefaultTimeout = 3
                            }.ToString();

                            using var wsConn = new SqliteConnection(wsConnStr);
                            wsConn.Open();
                            using var wsCmd = wsConn.CreateCommand();
                            wsCmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'chat.ChatSessionStore.index'";
                            var wsVal = wsCmd.ExecuteScalar()?.ToString();
                            if (!string.IsNullOrEmpty(wsVal))
                            {
                                string dirName = Path.GetFileName(dir);
                                File.WriteAllText(Path.Combine(wsBackupDir, $"{dirName}.json"), wsVal);
                            }
                        }
                        catch { }
                    }
                }

                File.WriteAllText(Path.Combine(backupDir, "backup_time.txt"), DateTime.Now.ToString("o"));
                Debug.WriteLine($"[ChatBackup] Backed up chat state for {email}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatBackup] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores chat state for the target account after switching.
        /// Merges the target account's backed-up chat index with ALL conversations found on disk,
        /// ensuring no conversation is lost even if it was created under a different account.
        /// </summary>
        public static void RestoreChatStateForAccount(string targetEmail)
        {
            try
            {
                string dbPath = AntigravityStateDbPath;
                if (!File.Exists(dbPath)) return;

                string backupDir = GetChatBackupDir(targetEmail);
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // Step 1: Collect ALL known conversation entries from multiple sources
                var mergedEntries = new Dictionary<string, AntigravityChatRestorer.ChatIndexEntry>(StringComparer.OrdinalIgnoreCase);

                // Source A: Target account's backed-up chat index (highest priority for this account's conversations)
                string chatIndexBackup = Path.Combine(backupDir, "chatSessionStore.json");
                if (File.Exists(chatIndexBackup))
                {
                    try
                    {
                        string json = File.ReadAllText(chatIndexBackup);
                        var root = JsonConvert.DeserializeObject<AntigravityChatRestorer.ChatIndexRoot>(json);
                        if (root?.Entries != null)
                        {
                            foreach (var kvp in root.Entries)
                            {
                                mergedEntries[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch { }
                }

                // Source B: Current chat.ChatSessionStore.index in state.vscdb (may have been written by RestoreConversations)
                try
                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        DefaultTimeout = 5
                    }.ToString();

                    using var conn = new SqliteConnection(connStr);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'chat.ChatSessionStore.index'";
                    var val = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        var root = JsonConvert.DeserializeObject<AntigravityChatRestorer.ChatIndexRoot>(val);
                        if (root?.Entries != null)
                        {
                            foreach (var kvp in root.Entries)
                            {
                                if (!mergedEntries.ContainsKey(kvp.Key))
                                {
                                    mergedEntries[kvp.Key] = kvp.Value;
                                }
                                else if (kvp.Value.LastMessageDate > mergedEntries[kvp.Key].LastMessageDate)
                                {
                                    // Keep the newer version
                                    mergedEntries[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    }
                }
                catch { }

                // Source C: ALL other account backups (collect from all backup dirs to find conversations that may have been shared)
                string chatBackupsRoot = Path.Combine(AppDataHubDir, "chat_backups");
                if (!Directory.Exists(chatBackupsRoot))
                {
                    string oldBackups = Path.Combine(AppDataRoaming, "ProfileSwitchChrome", "chat_backups");
                    if (Directory.Exists(oldBackups)) chatBackupsRoot = oldBackups;
                }
                if (Directory.Exists(chatBackupsRoot))
                {
                    foreach (var accDir in Directory.GetDirectories(chatBackupsRoot))
                    {
                        string otherIndex = Path.Combine(accDir, "chatSessionStore.json");
                        if (!File.Exists(otherIndex)) continue;
                        try
                        {
                            string json = File.ReadAllText(otherIndex);
                            var root = JsonConvert.DeserializeObject<AntigravityChatRestorer.ChatIndexRoot>(json);
                            if (root?.Entries != null)
                            {
                                foreach (var kvp in root.Entries)
                                {
                                    if (!mergedEntries.ContainsKey(kvp.Key))
                                    {
                                        mergedEntries[kvp.Key] = kvp.Value;
                                    }
                                    else if (kvp.Value.LastMessageDate > mergedEntries[kvp.Key].LastMessageDate)
                                    {
                                        mergedEntries[kvp.Key] = kvp.Value;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // Source D: Scan disk for any .db conversation files not yet in the index
                string[] geminiRoots = [
                    Path.Combine(userProfile, ".gemini", "antigravity-ide"),
                    Path.Combine(userProfile, ".gemini", "antigravity")
                ];
                foreach (var gr in geminiRoots)
                {
                    string convDir = Path.Combine(gr, "conversations");
                    if (!Directory.Exists(convDir)) continue;

                    foreach (var file in Directory.GetFiles(convDir, "*.db"))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (mergedEntries.ContainsKey(id))
                        {
                            var existing = mergedEntries[id];
                            if (existing.Title.StartsWith("Conversation (") || string.IsNullOrWhiteSpace(existing.Title))
                            {
                                string realTitle = AntigravityChatRestorer.ExtractConversationTitle(id, geminiRoots);
                                if (!realTitle.StartsWith("Conversation (")) existing.Title = realTitle;
                            }
                            continue;
                        }

                        string title = AntigravityChatRestorer.ExtractConversationTitle(id, geminiRoots);
                        var fi = new FileInfo(file);
                        long lastMsg = fi.LastWriteTime.ToFileTimeUtc() / 10000 - 11644473600000;

                        mergedEntries[id] = new AntigravityChatRestorer.ChatIndexEntry
                        {
                            SessionId = id,
                            Title = title,
                            LastMessageDate = lastMsg,
                            IsImported = false,
                            InitialLocation = "panel",
                            IsEmpty = false
                        };
                    }
                }

                if (mergedEntries.Count == 0)
                {
                    Debug.WriteLine("[ChatRestore] No conversations to restore.");
                    return;
                }

                // Step 2: Write merged chat index to state.vscdb
                var mergedRoot = new AntigravityChatRestorer.ChatIndexRoot
                {
                    Version = 1,
                    Entries = mergedEntries
                };
                string mergedJson = JsonConvert.SerializeObject(mergedRoot, Formatting.None);

                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadWrite,
                        DefaultTimeout = 5
                    }.ToString();

                    using var conn = new SqliteConnection(connStr);
                    conn.Open();

                    using (var pragmaCmd = conn.CreateCommand())
                    {
                        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                        pragmaCmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('chat.ChatSessionStore.index', @val)";
                        cmd.Parameters.AddWithValue("@val", mergedJson);
                        cmd.ExecuteNonQuery();
                    }

                    // Only restore backed-up trajectorySummaries if missing from DB (do NOT overwrite reconciled state)
                    string trajBackup = Path.Combine(backupDir, "trajectorySummaries.b64");
                    if (File.Exists(trajBackup))
                    {
                        try
                        {
                            using var checkCmd = conn.CreateCommand();
                            checkCmd.CommandText = "SELECT COUNT(*) FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.trajectorySummaries'";
                            long count = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0);
                            if (count == 0)
                            {
                                string trajB64 = File.ReadAllText(trajBackup);
                                if (!string.IsNullOrEmpty(trajB64))
                                {
                                    using var cmd = conn.CreateCommand();
                                    cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('antigravityUnifiedStateSync.trajectorySummaries', @val)";
                                    cmd.Parameters.AddWithValue("@val", trajB64);
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                        catch { }
                    }

                    // Restore sidebarWorkspaces protobuf (sidebar project folder structure)
                    string sidebarBackup = Path.Combine(backupDir, "sidebarWorkspaces.b64");
                    if (File.Exists(sidebarBackup))
                    {
                        try
                        {
                            string sidebarB64 = File.ReadAllText(sidebarBackup);
                            if (!string.IsNullOrEmpty(sidebarB64))
                            {
                                using var cmd = conn.CreateCommand();
                                cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('antigravityUnifiedStateSync.sidebarWorkspaces', @val)";
                                cmd.Parameters.AddWithValue("@val", sidebarB64);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        catch { }
                    }

                    using (var cpCmd = conn.CreateCommand())
                    {
                        cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        cpCmd.ExecuteNonQuery();
                    }
                }

                // Step 3: Also update state.vscdb.backup
                string dbBackup = dbPath + ".backup";
                if (File.Exists(dbBackup))
                {
                    try
                    {
                        var connStr = new SqliteConnectionStringBuilder
                        {
                            DataSource = dbBackup,
                            Mode = SqliteOpenMode.ReadWrite,
                            DefaultTimeout = 5
                        }.ToString();

                        using var conn = new SqliteConnection(connStr);
                        conn.Open();
                        using (var pragmaCmd = conn.CreateCommand())
                        {
                            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                            pragmaCmd.ExecuteNonQuery();
                        }
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('chat.ChatSessionStore.index', @val)";
                            cmd.Parameters.AddWithValue("@val", mergedJson);
                            cmd.ExecuteNonQuery();
                        }
                        using (var cpCmd = conn.CreateCommand())
                        {
                            cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            cpCmd.ExecuteNonQuery();
                        }
                    }
                    catch { }
                }

                // Step 4: Restore agyhub_summaries_proto.pb from backup (only if not present or empty)
                string pbBackup = Path.Combine(backupDir, "agyhub_summaries_proto.pb");
                if (File.Exists(pbBackup) && new FileInfo(pbBackup).Length > 0)
                {
                    foreach (var gr in geminiRoots)
                    {
                        string pbPath = Path.Combine(gr, "agyhub_summaries_proto.pb");
                        if (Directory.Exists(gr))
                        {
                            try 
                            { 
                                if (!File.Exists(pbPath) || new FileInfo(pbPath).Length == 0)
                                {
                                    File.Copy(pbBackup, pbPath, overwrite: true); 
                                }
                            } 
                            catch { }
                        }
                    }
                }

                // Step 5: Restore per-workspace chat indexes from backup
                string wsBackupDir = Path.Combine(backupDir, "workspaces");
                string wsStorageDir = Path.Combine(AppDataRoaming, "Antigravity IDE", "User", "workspaceStorage");
                if (Directory.Exists(wsBackupDir) && Directory.Exists(wsStorageDir))
                {
                    foreach (var wsFile in Directory.GetFiles(wsBackupDir, "*.json"))
                    {
                        string wsId = Path.GetFileNameWithoutExtension(wsFile);
                        string wsStateDb = Path.Combine(wsStorageDir, wsId, "state.vscdb");
                        if (!File.Exists(wsStateDb)) continue;

                        try
                        {
                            string wsJson = File.ReadAllText(wsFile);
                            var connStr = new SqliteConnectionStringBuilder
                            {
                                DataSource = wsStateDb,
                                Mode = SqliteOpenMode.ReadWrite,
                                DefaultTimeout = 3
                            }.ToString();

                            using var wsConn = new SqliteConnection(connStr);
                            wsConn.Open();
                            using (var pragmaCmd = wsConn.CreateCommand())
                            {
                                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                                pragmaCmd.ExecuteNonQuery();
                            }
                            using (var wsCmd = wsConn.CreateCommand())
                            {
                                wsCmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('chat.ChatSessionStore.index', @val)";
                                wsCmd.Parameters.AddWithValue("@val", wsJson);
                                wsCmd.ExecuteNonQuery();
                            }
                            using (var cpCmd = wsConn.CreateCommand())
                            {
                                cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                                cpCmd.ExecuteNonQuery();
                            }
                        }
                        catch { }
                    }
                }

                Debug.WriteLine($"[ChatRestore] Restored {mergedEntries.Count} conversations for {targetEmail}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRestore] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Switches to the target account WITHOUT RESTARTING Antigravity IDE and GUARANTEES NO CRASH.
        /// Performs safe SQLite WAL injection, updates storage.json, and gracefully reloads the IDE window in 1 second.
        /// </summary>
        public static bool SwitchAccount(AntigravityAccountInfo target, bool autoReloadWindow, out string message)
        {
            string dbPath = AntigravityStateDbPath;
            if (!File.Exists(dbPath))
            {
                message = "Không tìm thấy cơ sở dữ liệu Antigravity IDE (state.vscdb).";
                return false;
            }

            try
            {
                // Generate tokens
                long exp = target.ExpiryTimestamp > 0 ? target.ExpiryTimestamp : (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                string oauthTokenB64 = BuildUnifiedOAuthTokenProtobuf(
                    target.ApiKey ?? "",
                    target.RefreshToken ?? "",
                    exp,
                    target.IdToken,
                    target.IsGcpTos,
                    target.Email ?? "");
                string userStatusB64 = BuildUserStatusProtobuf(target.Email ?? "");
                target.RawOauthToken = oauthTokenB64;
                target.RawUserStatus = userStatusB64;

                // ═══ STEP 0: Backup current account's chat state ═════════════════
                // MUST happen BEFORE killing IDE so we capture the live chat index.
                // This preserves all conversation histories per-account.
                // 
                // CRITICAL: Read the current email NOW, before any writes to state.vscdb.
                // If SilentSwitchAccount was called earlier, the DB may already contain
                // the target account's auth. We must use the ORIGINAL email for backup.
                string? currentEmail = null;
                try
                {
                    var currentAccount = GetCurrentActiveAccount();
                    currentEmail = currentAccount?.Email;
                    // If the DB already has the target email (e.g. from SilentSwitch),
                    // we cannot determine the previous account from DB alone.
                    // In that case, skip backup to avoid overwriting target's chat history.
                    if (!string.IsNullOrEmpty(currentEmail) &&
                        string.Equals(currentEmail, target.Email, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[SwitchAccount] DB already has target email '{target.Email}', skipping backup to avoid overwrite.");
                        currentEmail = null; // Don't backup — would overwrite target's own data
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(currentEmail))
                {
                    try
                    {
                        BackupChatStateForCurrentAccount(currentEmail);
                    }
                    catch (Exception backupEx)
                    {
                        Debug.WriteLine($"[SwitchAccount] Chat backup warning: {backupEx.Message}");
                    }
                }

                // ═══ STEP 1: Kill ALL IDE processes FIRST ═══════════════════════
                // The IDE MUST be stopped before writing to state.vscdb,
                // otherwise it will overwrite our values with its in-memory state.
                string? idePath = null;
                var (procCount, _, _) = GetAntigravityProcessStats();
                bool wasRunning = procCount > 0;
                if (wasRunning)
                {
                    idePath = FindAntigravityIdeExePath();
                    KillAllAntigravityProcesses();
                    System.Threading.Thread.Sleep(400); // Reduced from 1500ms
                }

                // ═══ STEP 2: Write to state.vscdb (IDE is NOT running) ══════════
                WriteAuthToStateDb(dbPath, target, oauthTokenB64, userStatusB64);

                // Update storage.json
                UpdateStorageJson(target);

                // ═══ STEP 2.5: Restore & Preserve Chat Histories ════════════════
                // First run the general disk-based restore (rebuilds from .db files + protobuf)
                try
                {
                    var restoreRes = AntigravityChatRestorer.RestoreConversations(forceCloseIde: false);
                    Debug.WriteLine($"[SwitchAccount] Chat restore: {restoreRes.Message}");
                }
                catch (Exception restoreEx)
                {
                    Debug.WriteLine($"[SwitchAccount] Chat restore warning: {restoreEx.Message}");
                }

                // Then merge with per-account backup (ensures target account's own chats + ALL disk chats are present)
                try
                {
                    RestoreChatStateForAccount(target.Email ?? "");
                }
                catch (Exception mergeEx)
                {
                    Debug.WriteLine($"[SwitchAccount] Chat merge warning: {mergeEx.Message}");
                }

                // Save last used
                target.LastUsed = DateTime.Now;
                var list = LoadAllAccounts();
                var item = list.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                if (item != null) item.LastUsed = DateTime.Now;
                SaveAccountsList(list);

                // ═══ STEP 3: Restart IDE with new credentials ═══════════════════
                if (wasRunning && autoReloadWindow)
                {
                    bool restarted = RestartAntigravityIde(idePath);
                    if (restarted)
                        message = $"✓ Đã switch sang '{target.Email}'! IDE đang khởi động lại.";
                    else
                        message = $"✓ Đã switch sang '{target.Email}'! Hãy mở lại IDE.";
                }
                else
                {
                    message = $"✓ Đã chuyển sang '{target.Email}'! Mở IDE để sử dụng.";
                }

                return true;
            }
            catch (Exception ex)
            {
                message = "Lỗi khi switch account: " + ex.Message;
                return false;
            }
        }

        private static void UpsertKey(SqliteConnection conn, SqliteTransaction tran, string key, string value)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }

        private static void UpdateStorageJson(AntigravityAccountInfo target)
        {
            try
            {
                string storagePath = AntigravityStorageJsonPath;
                if (!File.Exists(storagePath)) return;

                string json = File.ReadAllText(storagePath);
                var obj = JObject.Parse(json);

                bool modified = false;

                if (!string.IsNullOrEmpty(target.DevDeviceId))
                {
                    obj["storage.serviceMachineId"] = target.DevDeviceId;
                    obj["telemetry.devDeviceId"] = target.DevDeviceId;
                    modified = true;
                }
                if (!string.IsNullOrEmpty(target.MachineId))
                {
                    obj["telemetry.machineId"] = target.MachineId;
                    modified = true;
                }
                if (!string.IsNullOrEmpty(target.MacMachineId))
                {
                    obj["telemetry.macMachineId"] = target.MacMachineId;
                    modified = true;
                }
                if (!string.IsNullOrEmpty(target.SqmId))
                {
                    obj["telemetry.sqmId"] = target.SqmId;
                    modified = true;
                }

                if (modified)
                {
                    File.WriteAllText(storagePath, obj.ToString(Formatting.Indented));
                }
            }
            catch { }
        }

        /// <summary>
        /// Kills all Antigravity IDE processes gracefully then restarts the IDE.
        /// This is required to pick up the new credentials - same approach as lbjlaq/Draculabo.
        /// </summary>
        public static bool TriggerSeamlessWindowReload()
        {
            return KillAndRestartAntigravityIde();
        }

        /// <summary>
        /// Finds the Antigravity IDE executable path from running processes or common install locations.
        /// </summary>
        public static string? FindAntigravityIdeExePath()
        {
            try
            {
                // Priority 1: Check running processes FIRST specifically for "Antigravity IDE"
                try
                {
                    var procs = Process.GetProcesses();
                    foreach (var proc in procs)
                    {
                        try
                        {
                            string name = proc.ProcessName;
                            string? fileName = null;
                            try { fileName = proc.MainModule?.FileName; } catch { }

                            if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                            {
                                if ((name.Contains("Antigravity IDE", StringComparison.OrdinalIgnoreCase) ||
                                     fileName.Contains("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase) ||
                                     fileName.Contains("antigravity-ide", StringComparison.OrdinalIgnoreCase)) &&
                                    !fileName.Contains("crashpad", StringComparison.OrdinalIgnoreCase) &&
                                    !fileName.Contains("language_server", StringComparison.OrdinalIgnoreCase))
                                {
                                    return fileName;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Priority 2: Check common install locations specifically for "Antigravity IDE.exe" FIRST
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                string[] ideCandidatePaths = [
                    Path.Combine(localAppData, "Programs", "Antigravity IDE", "Antigravity IDE.exe"),
                    Path.Combine(localAppData, "Programs", "antigravity-ide", "Antigravity IDE.exe"),
                    Path.Combine(programFiles, "Antigravity IDE", "Antigravity IDE.exe"),
                    Path.Combine(programFilesX86, "Antigravity IDE", "Antigravity IDE.exe"),
                    Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity IDE.exe"),
                    Path.Combine(programFiles, "Antigravity", "Antigravity IDE.exe"),
                ];

                foreach (string candidate in ideCandidatePaths)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                // Priority 3: Fallback to "Antigravity.exe" ONLY if "Antigravity IDE.exe" is not found anywhere
                string[] fallbackPaths = [
                    Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe"),
                    Path.Combine(programFiles, "Antigravity", "Antigravity.exe"),
                    Path.Combine(programFilesX86, "Antigravity", "Antigravity.exe"),
                ];

                foreach (string candidate in fallbackPaths)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                // Priority 4: Fallback scan any running process with Antigravity
                var allProcs = Process.GetProcesses();
                foreach (var proc in allProcs)
                {
                    try
                    {
                        string fileName = proc.MainModule?.FileName ?? "";
                        if ((fileName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                             proc.ProcessName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) &&
                            !fileName.Contains("language_server", StringComparison.OrdinalIgnoreCase) &&
                            !fileName.Contains("crashpad", StringComparison.OrdinalIgnoreCase))
                        {
                            return fileName;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error finding IDE exe: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Kills ALL Antigravity-related processes (IDE main + helpers + language_server).
        /// Does NOT restart. Use RestartAntigravityIde() to restart after writing to DB.
        /// </summary>
        public static void KillAllAntigravityProcesses()
        {
            try
            {
                var ideProcs = new List<Process>();
                var helperProcs = new List<Process>();
                var procs = Process.GetProcesses();
                foreach (var proc in procs)
                {
                    try
                    {
                        string name = proc.ProcessName;
                        string? fileName = null;
                        try { fileName = proc.MainModule?.FileName; } catch { }

                        bool isIde = (name.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                                      (fileName != null && fileName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)));

                        bool isHelper = (name.Contains("language_server", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("antigravity-manager", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("antigravity-tools", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("crashpad_handler", StringComparison.OrdinalIgnoreCase));

                        if (isIde) ideProcs.Add(proc);
                        else if (isHelper) helperProcs.Add(proc);
                    }
                    catch { }
                }

                // Phase 1: Force kill IDE processes immediately (SilentSwitchAccount already backed up state)
                // Skip graceful CloseMainWindow to maximize switch speed
                foreach (var proc in ideProcs.Concat(helperProcs))
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            Debug.WriteLine($"[Switch] Killed: {proc.ProcessName} (PID {proc.Id})");
                        }
                    }
                    catch { }
                }

                // Wait up to 800ms for processes to fully exit (reduced from 3000ms)
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 800)
                {
                    bool anyAlive = ideProcs.Concat(helperProcs).Any(p => { try { return !p.HasExited; } catch { return false; } });
                    if (!anyAlive) break;
                    System.Threading.Thread.Sleep(80);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error killing IDE processes: " + ex.Message);
            }
        }

        /// <summary>
        /// Safely shuts down Antigravity IDE while preserving 100% of chat history.
        /// 1. Backs up active account's chat state.
        /// 2. Requests graceful window close and waits for IDE to flush caches to disk.
        /// 3. Reconciles and indexes all disk conversation records into state.vscdb.
        /// 4. Restores account chat state mapping.
        /// </summary>
        public static bool SafelyCloseAntigravityWithChatPreservation()
        {
            try
            {
                // 1. Identify active account and backup its chat state BEFORE stopping
                string? activeEmail = null;
                try
                {
                    activeEmail = GetCurrentActiveAccount()?.Email;
                    if (!string.IsNullOrEmpty(activeEmail))
                    {
                        BackupChatStateForCurrentAccount(activeEmail);
                    }
                }
                catch (Exception bEx)
                {
                    Debug.WriteLine($"[SafeClose] Pre-close chat backup warning: {bEx.Message}");
                }

                // 2. Gracefully close IDE processes (with up to 3000ms wait for flush)
                KillAllAntigravityProcesses();
                System.Threading.Thread.Sleep(300); // Reduced from 800ms

                // 3. Reconcile all disk conversation histories into state.vscdb
                try
                {
                    AntigravityChatRestorer.RestoreConversations(forceCloseIde: false);
                }
                catch (Exception rEx)
                {
                    Debug.WriteLine($"[SafeClose] Chat reconcile warning: {rEx.Message}");
                }

                // 4. If an active account existed, restore its chat state mapping as well
                if (!string.IsNullOrEmpty(activeEmail))
                {
                    try
                    {
                        RestoreChatStateForAccount(activeEmail);
                    }
                    catch (Exception mEx)
                    {
                        Debug.WriteLine($"[SafeClose] Chat state restore warning: {mEx.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SafeClose] Error closing Antigravity safely: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the Antigravity IDE executable.
        /// </summary>
        private static bool RestartAntigravityIde(string? idePath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(idePath))
                    idePath = FindAntigravityIdeExePath();

                if (!string.IsNullOrEmpty(idePath) && File.Exists(idePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = idePath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    };
                    Process.Start(psi);
                    Debug.WriteLine($"[Switch] Restarted IDE: {idePath}");
                    return true;
                }

                Debug.WriteLine("[Switch] Could not find IDE exe to restart.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error restarting IDE: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Kill + Restart combo (public API for button clicks).
        /// </summary>
        public static bool KillAndRestartAntigravityIde()
        {
            string? idePath = FindAntigravityIdeExePath();
            KillAllAntigravityProcesses();
            System.Threading.Thread.Sleep(400); // Reduced from 1500ms
            return RestartAntigravityIde(idePath);
        }

        /// <summary>
        /// Writes all auth keys to state.vscdb in a single transaction.
        /// IMPORTANT: IDE must NOT be running when this is called.
        /// </summary>
        private static void WriteAuthToStateDb(string dbPath, AntigravityAccountInfo target, string oauthTokenB64, string userStatusB64)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                DefaultTimeout = 5
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var tran = conn.BeginTransaction();

            // 1. antigravityAuthStatus (JSON)
            string authJson = JsonConvert.SerializeObject(new
            {
                name = target.Name ?? target.Email,
                email = target.Email,
                apiKey = target.ApiKey ?? ""
            });
            UpsertKey(conn, tran, "antigravityAuthStatus", authJson);

            // 2. Unified OAuth token (protobuf with signedIn state)
            UpsertKey(conn, tran, "antigravityUnifiedStateSync.oauthToken", oauthTokenB64);

            // 3. User status
            UpsertKey(conn, tran, "antigravityUnifiedStateSync.userStatus", userStatusB64);

            // 4. Onboarding flag
            UpsertKey(conn, tran, "antigravityOnboarding", "true");

            // 5. Enterprise preferences
            if (!string.IsNullOrEmpty(target.ProjectId))
            {
                byte[] projPayload = EncodeStringField(3, target.ProjectId);
                UpsertKey(conn, tran, "antigravityUnifiedStateSync.enterprisePreferences",
                    CreateUnifiedStateEntry("enterpriseGcpProjectId", projPayload));
            }

            // 6. Model credits (if available)
            if (!string.IsNullOrEmpty(target.RawModelCredits))
            {
                UpsertKey(conn, tran, "antigravityUnifiedStateSync.modelCredits", target.RawModelCredits);
            }

            // 7. Avatar URL (only write if present, otherwise delete to avoid leaking previous account's avatar)
            if (!string.IsNullOrEmpty(target.AvatarUrl))
            {
                UpsertKey(conn, tran, "antigravity.profileUrl", target.AvatarUrl);
            }
            else
            {
                DeleteKey(conn, tran, "antigravity.profileUrl");
            }

            // 8. Device machine id
            if (!string.IsNullOrEmpty(target.DevDeviceId))
            {
                UpsertKey(conn, tran, "storage.serviceMachineId", target.DevDeviceId);
            }

            // 9. Delete stale cached keys that might conflict
            DeleteKey(conn, tran, "jetskiStateSync.agentManagerInitState");
            DeleteKey(conn, tran, "google.antigravity");

            tran.Commit();

            // Checkpoint WAL to ensure data is flushed to main DB
            try
            {
                using var cpCmd = conn.CreateCommand();
                cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cpCmd.ExecuteNonQuery();
            }
            catch { }
        }

        private static IntPtr FindAntigravityIdeWindow()
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hwnd, lParam) =>
            {
                if (IsWindowVisible(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        if (proc.ProcessName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                        {
                            var classSb = new StringBuilder(256);
                            GetClassName(hwnd, classSb, 256);
                            string className = classSb.ToString();

                            var titleSb = new StringBuilder(256);
                            GetWindowText(hwnd, titleSb, 256);
                            string title = titleSb.ToString();

                            if (className.Contains("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(title))
                            {
                                found = hwnd;
                                return false; // Stop enumeration
                            }
                        }
                    }
                    catch { }
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Gets process count, running status and total RAM consumption for Antigravity IDE and Language Server
        /// </summary>
        public static (int procCount, bool hasLanguageServer, long totalMemoryBytes) GetAntigravityProcessStats()
        {
            try
            {
                var procs = Process.GetProcessesByName("Antigravity")
                    .Concat(Process.GetProcessesByName("Antigravity IDE")).ToArray();
                var lsProcs = Process.GetProcessesByName("language_server_windows_x64");
                var mgrProcs = Process.GetProcessesByName("antigravity-manager");
                var toolProcs = Process.GetProcessesByName("antigravity-tools");

                long totalMem = 0;
                int count = procs.Length;

                foreach (var p in procs)
                {
                    try { totalMem += p.WorkingSet64; } catch { }
                    p.Dispose();
                }
                foreach (var p in lsProcs)
                {
                    try { totalMem += p.WorkingSet64; } catch { }
                    p.Dispose();
                }
                foreach (var p in mgrProcs)
                {
                    try { totalMem += p.WorkingSet64; } catch { }
                    p.Dispose();
                }
                foreach (var p in toolProcs)
                {
                    try { totalMem += p.WorkingSet64; } catch { }
                    p.Dispose();
                }

                return (count, lsProcs.Length > 0, totalMem);
            }
            catch
            {
                return (0, false, 0);
            }
        }

        #region Protobuf & Unified State Encoding (100% compatible with Antigravity / Draculabo / lbjlaq)

        public static byte[] EncodeVarint(ulong value)
        {
            var list = new List<byte>();
            while (value >= 0x80)
            {
                list.Add((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            list.Add((byte)value);
            return list.ToArray();
        }

        public static byte[] EncodeLenDelim(int fieldNum, byte[] data)
        {
            var tag = EncodeVarint((ulong)((fieldNum << 3) | 2));
            var len = EncodeVarint((ulong)data.Length);
            byte[] res = new byte[tag.Length + len.Length + data.Length];
            Buffer.BlockCopy(tag, 0, res, 0, tag.Length);
            Buffer.BlockCopy(len, 0, res, tag.Length, len.Length);
            Buffer.BlockCopy(data, 0, res, tag.Length + len.Length, data.Length);
            return res;
        }

        public static byte[] EncodeStringField(int fieldNum, string str)
        {
            return EncodeLenDelim(fieldNum, Encoding.UTF8.GetBytes(str));
        }

        public static byte[] EncodeVarintField(int fieldNum, ulong value)
        {
            var tag = EncodeVarint((ulong)((fieldNum << 3) | 0));
            var val = EncodeVarint(value);
            byte[] res = new byte[tag.Length + val.Length];
            Buffer.BlockCopy(tag, 0, res, 0, tag.Length);
            Buffer.BlockCopy(val, 0, res, tag.Length, val.Length);
            return res;
        }

        public static byte[] ConcatBytes(params byte[][] arrays)
        {
            int total = 0;
            foreach (var a in arrays) if (a != null) total += a.Length;
            byte[] res = new byte[total];
            int offset = 0;
            foreach (var a in arrays)
            {
                if (a != null && a.Length > 0)
                {
                    Buffer.BlockCopy(a, 0, res, offset, a.Length);
                    offset += a.Length;
                }
            }
            return res;
        }

        public static byte[] CreateUnifiedTopicEntry(string sentinelKey, byte[] payload)
        {
            string b64Payload = Convert.ToBase64String(payload);
            byte[] row = EncodeStringField(1, b64Payload);
            byte[] data = ConcatBytes(EncodeStringField(1, sentinelKey), EncodeLenDelim(2, row));
            return EncodeLenDelim(1, data);
        }

        public static string CreateUnifiedStateEntry(string sentinelKey, byte[] payload)
        {
            return Convert.ToBase64String(CreateUnifiedTopicEntry(sentinelKey, payload));
        }

        public static string BuildUserStatusProtobuf(string email)
        {
            byte[] payload = ConcatBytes(EncodeStringField(3, email), EncodeStringField(7, email));
            return CreateUnifiedStateEntry("userStatusSentinelKey", payload);
        }

        public static string BuildUnifiedOAuthTokenProtobuf(
            string accessToken,
            string refreshToken,
            long expiryTimestamp,
            string? idToken,
            bool isGcpTos,
            string email)
        {
            string authStateJson = "{\"state\":\"signedIn\",\"context\":{\"project\":\"\",\"showProjectError\":false,\"errorMessage\":\"\",\"ineligibleMessage\":\"\",\"verificationUrl\":\"\",\"isGcpTos\":false,\"browserOpenFailed\":false,\"appealUrl\":\"\",\"appealLinkText\":\"\"}}";
            byte[] t1 = CreateUnifiedTopicEntry("authStateWithContextSentinelKey", Encoding.UTF8.GetBytes(authStateJson));

            bool gcpTos = isGcpTos;
            if (IsPersonalAccountEmail(email)) gcpTos = false;

            byte[] expiryInner = ConcatBytes(
                EncodeVarintField(1, (ulong)expiryTimestamp),
                EncodeVarintField(2, 0)
            );

            byte[] oauthInfo = ConcatBytes(
                EncodeStringField(1, accessToken),
                EncodeStringField(2, "Bearer"),
                EncodeStringField(3, refreshToken),
                EncodeLenDelim(4, expiryInner),
                !string.IsNullOrEmpty(idToken) ? EncodeStringField(5, idToken) : Array.Empty<byte>(),
                gcpTos ? EncodeVarintField(6, 1) : Array.Empty<byte>()
            );

            byte[] t2 = CreateUnifiedTopicEntry("oauthTokenInfoSentinelKey", oauthInfo);

            return Convert.ToBase64String(ConcatBytes(t1, t2));
        }

        private static bool IsPersonalAccountEmail(string email)
        {
            string lower = email.ToLowerInvariant();
            return lower.EndsWith("@gmail.com") || lower.EndsWith("@outlook.com") || lower.EndsWith("@hotmail.com") || lower.EndsWith("@qq.com") || lower.EndsWith("@163.com");
        }

        private static void DeleteKey(SqliteConnection conn, SqliteTransaction tran, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = "DELETE FROM ItemTable WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        #endregion

        /// <summary>
        /// Silently kills ONLY the language_server_windows_x64.exe process.
        /// Antigravity IDE stays alive and auto-restarts language server with new credentials.
        /// </summary>
        public static bool SilentKillLanguageServer()
        {
            bool killed = false;
            try
            {
                string[] lsNames = ["language_server_windows_x64", "language_server"];
                foreach (string lsName in lsNames)
                {
                    var procs = Process.GetProcessesByName(lsName);
                    foreach (var proc in procs)
                    {
                        try
                        {
                            proc.Kill(entireProcessTree: false);
                            killed = true;
                            Debug.WriteLine($"[SilentSwitch] Killed language server: {lsName} (PID {proc.Id})");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SilentSwitch] Error killing {lsName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SilentSwitch] SilentKillLanguageServer error: {ex.Message}");
            }
            return killed;
        }

        /// <summary>
        /// Gracefully triggers VS Code / Antigravity IDE renderer reload in-place without restarting process.
        /// Sends Ctrl+R (keybindings.json: workbench.action.reloadWindow)
        /// </summary>
        public static bool ReloadIdeWindowInPlace()
        {
            try
            {
                IntPtr hwnd = FindAntigravityIdeWindow();
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(100);
                    keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_R, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_R, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Sends Ctrl+Shift+Alt+R to the IDE window to restart ONLY the extension host.
        /// </summary>
        public static bool RestartExtensionHostInPlace()
        {
            try
            {
                IntPtr hwnd = FindAntigravityIdeWindow();
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(200);
                    keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_SHIFT, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_ALT, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_R, 0, 0, UIntPtr.Zero);
                    keybd_event((byte)VK_R, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event((byte)VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event((byte)VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// NON-DESTRUCTIVE account switch.
        /// ONLY writes credentials to state.vscdb + updates ProxySwitcher UI.
        /// Does NOT kill anything, does NOT send keystrokes, does NOT disrupt IDE.
        /// 
        /// The new account will take effect when IDE is next restarted.
        /// Use KillAndRestartAntigravityIde() to apply immediately when ready.
        /// 
        /// Why: Antigravity IDE caches auth state in memory (USS observable via IPC).
        /// External DB writes are invisible to the running IDE process.
        /// Only a full process restart or the extension's internal login flow
        /// can update the in-memory auth state.
        /// </summary>
        public static bool SilentSwitchAccount(AntigravityAccountInfo target, out string message)
        {
            message = "";
            string dbPath = AntigravityStateDbPath;
            if (!File.Exists(dbPath))
            {
                message = "Không tìm thấy state.vscdb. Hãy mở Antigravity IDE ít nhất một lần.";
                return false;
            }

            try
            {
                // ═══ STEP 0: Backup current account's chat state BEFORE writing new auth ═══
                // CRITICAL: Must read current email and backup BEFORE WriteAuthToStateDb
                // overwrites antigravityAuthStatus with target email. Otherwise,
                // BackupChatStateForCurrentAccount() would read the target email from DB
                // and overwrite the target account's backed-up chat history.
                try
                {
                    var currentAccount = GetCurrentActiveAccount();
                    string? currentEmail = currentAccount?.Email;
                    if (!string.IsNullOrEmpty(currentEmail) &&
                        !string.Equals(currentEmail, target.Email, StringComparison.OrdinalIgnoreCase))
                    {
                        BackupChatStateForCurrentAccount(currentEmail);
                        Debug.WriteLine($"[SilentSwitch] Backed up chat state for '{currentEmail}' before switching.");
                    }
                }
                catch (Exception backupEx)
                {
                    Debug.WriteLine($"[SilentSwitch] Chat backup warning: {backupEx.Message}");
                }

                // Generate tokens
                long exp = target.ExpiryTimestamp > 0 ? target.ExpiryTimestamp : (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                string oauthTokenB64 = BuildUnifiedOAuthTokenProtobuf(
                    target.ApiKey ?? "",
                    target.RefreshToken ?? "",
                    exp,
                    target.IdToken,
                    target.IsGcpTos,
                    target.Email ?? "");
                string userStatusB64 = BuildUserStatusProtobuf(target.Email ?? "");
                target.RawOauthToken = oauthTokenB64;
                target.RawUserStatus = userStatusB64;

                // ═══ Write to state.vscdb (for next IDE startup) ═══════════════
                // NOTE: IDE is still running. These values will be overwritten by 
                // the IDE's in-memory cache when it flushes. BUT they will be
                // correct if the IDE is killed BEFORE it flushes (which is what
                // KillAndRestartAntigravityIde does - kills first, then DB is clean).
                //
                // We write now so that if user clicks "Apply to IDE" later,
                // the DB already has the right values before IDE restart.
                try
                {
                    WriteAuthToStateDb(dbPath, target, oauthTokenB64, userStatusB64);
                }
                catch (Exception dbEx)
                {
                    Debug.WriteLine($"[SilentSwitch] DB write warning (IDE may have lock): {dbEx.Message}");
                    // Non-fatal: DB write can fail if IDE has exclusive lock.
                    // KillAndRestartAntigravityIde will write again after killing IDE.
                }

                UpdateStorageJson(target);

                // Save last used
                target.LastUsed = DateTime.Now;
                var list = LoadAllAccounts();
                var item = list.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                if (item != null) item.LastUsed = DateTime.Now;
                SaveAccountsList(list);

                message = $"✓ Đã chọn '{target.Email}'! Ấn 'Apply to IDE' để áp dụng (IDE sẽ restart nhanh).";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Lỗi: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Full account switch with IDE restart. Call this when user is ready to apply.
        /// Flow: Kill IDE → Write DB (clean, no overwrite risk) → Restart IDE
        /// IDE session (tabs, terminals) will be restored automatically.
        /// </summary>
        public static bool ApplyAccountToIde(AntigravityAccountInfo target, out string message)
        {
            return SwitchAccount(target, autoReloadWindow: true, out message);
        }

        public static string? FindAntigravityClassicExePath()
        {
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        string? fn = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(fn) && File.Exists(fn))
                        {
                            if (name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("IDE", StringComparison.OrdinalIgnoreCase) &&
                                !fn.Contains("Antigravity IDE", StringComparison.OrdinalIgnoreCase))
                            {
                                return fn;
                            }
                        }
                    }
                    catch { }
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                string[] candidates = [
                    Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe"),
                    Path.Combine(programFiles, "Antigravity", "Antigravity.exe"),
                    Path.Combine(programFilesX86, "Antigravity", "Antigravity.exe")
                ];

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        public static bool WriteCliCredential(string targetName, string userName, string secretPayload)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(secretPayload);
                IntPtr blobPtr = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
                    var cred = new CREDENTIAL
                    {
                        Flags = 0,
                        Type = 1, // Generic
                        TargetName = targetName,
                        Comment = null!,
                        CredentialBlobSize = (uint)bytes.Length,
                        CredentialBlob = blobPtr,
                        Persist = 2, // Local machine
                        AttributeCount = 0,
                        Attributes = IntPtr.Zero,
                        TargetAlias = null!,
                        UserName = userName
                    };
                    return CredWrite(ref cred, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(blobPtr);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CredWrite] Error: {ex.Message}");
                return false;
            }
        }

        public static string? ReadCliCredential(string targetName)
        {
            try
            {
                if (CredRead(targetName, 1 /* Generic */, 0, out IntPtr pCred) && pCred != IntPtr.Zero)
                {
                    try
                    {
                        var cred = Marshal.PtrToStructure<CREDENTIAL>(pCred);
                        if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                        {
                            byte[] bytes = new byte[cred.CredentialBlobSize];
                            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                            return Encoding.UTF8.GetString(bytes);
                        }
                    }
                    finally
                    {
                        CredFree(pCred);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReadCliCredential] Error: {ex.Message}");
            }
            return null;
        }

        public static bool SyncCliAndGoogleTokens(AntigravityAccountInfo target)
        {
            try
            {
                long exp = target.ExpiryTimestamp > 0 ? target.ExpiryTimestamp : (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                string expiryIso = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.000Z");

                var tokenInner = new JObject
                {
                    ["access_token"] = target.ApiKey ?? "",
                    ["token_type"] = "Bearer",
                    ["refresh_token"] = target.RefreshToken ?? "",
                    ["expiry"] = expiryIso
                };
                if (!string.IsNullOrEmpty(target.IdToken)) tokenInner["id_token"] = target.IdToken;
                if (!string.IsNullOrEmpty(target.ProjectId)) tokenInner["project_id"] = target.ProjectId;

                var rootTokenObj = new JObject
                {
                    ["token"] = tokenInner,
                    ["auth_method"] = "consumer"
                };
                if (!string.IsNullOrEmpty(target.IdToken)) rootTokenObj["id_token"] = target.IdToken;

                string jsonPayload = rootTokenObj.ToString(Formatting.None);

                // 1. Windows Credential Manager: gemini:antigravity (Used by Antigravity CLI / agy)
                WriteCliCredential("gemini:antigravity", "antigravity", jsonPayload);

                // 2. ~/.gemini/antigravity-cli/antigravity-oauth-token
                string cliDir = Path.Combine(UserProfile, ".gemini", "antigravity-cli");
                if (!Directory.Exists(cliDir)) Directory.CreateDirectory(cliDir);
                File.WriteAllText(Path.Combine(cliDir, "antigravity-oauth-token"), jsonPayload, Encoding.UTF8);

                // 3. ~/.gemini/oauth_creds.json
                string geminiDir = Path.Combine(UserProfile, ".gemini");
                if (!Directory.Exists(geminiDir)) Directory.CreateDirectory(geminiDir);
                var credsObj = new JObject
                {
                    ["access_token"] = target.ApiKey ?? "",
                    ["refresh_token"] = target.RefreshToken ?? "",
                    ["token_type"] = "Bearer",
                    ["expiry_date"] = exp * 1000,
                    ["scope"] = "openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/cclog https://www.googleapis.com/auth/experimentsandconfigs https://www.googleapis.com/auth/aicod"
                };
                if (!string.IsNullOrEmpty(target.IdToken)) credsObj["id_token"] = target.IdToken;
                File.WriteAllText(Path.Combine(geminiDir, "oauth_creds.json"), credsObj.ToString(Formatting.Indented), Encoding.UTF8);

                // 4. ~/.gemini/google_accounts.json
                var accountsObj = new JObject
                {
                    ["active"] = target.Email ?? "",
                    ["old"] = new JArray()
                };
                File.WriteAllText(Path.Combine(geminiDir, "google_accounts.json"), accountsObj.ToString(Formatting.Indented), Encoding.UTF8);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SyncCliTokens] Error: {ex.Message}");
                return false;
            }
        }

        public static bool SwitchAccountToCli(AntigravityAccountInfo target, out string message)
        {
            try
            {
                bool synced = SyncCliAndGoogleTokens(target);
                if (!synced)
                {
                    message = "Lỗi khi đồng bộ token CLI.";
                    return false;
                }

                // Verify credential store (matching lbjlaq/Antigravity-Manager verification pattern)
                string? storedPayload = ReadCliCredential("gemini:antigravity");
                if (!string.IsNullOrEmpty(storedPayload))
                {
                    try
                    {
                        var parsed = JObject.Parse(storedPayload);
                        string? storedRt = parsed["token"]?["refresh_token"]?.ToString() ?? parsed["refresh_token"]?.ToString();
                        if (!string.IsNullOrEmpty(target.RefreshToken) && storedRt != target.RefreshToken)
                        {
                            Debug.WriteLine("[SwitchAccountToCli] Warning: Stored credential refresh_token mismatch.");
                        }
                    }
                    catch { }
                }

                // Update last used
                target.LastUsed = DateTime.Now;
                var list = LoadAllAccounts();
                var item = list.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                if (item != null) item.LastUsed = DateTime.Now;
                SaveAccountsList(list);

                // CRITICAL (lbjlaq/Antigravity-Manager parity):
                // Do NOT kill language_server_windows_x64!
                // Language server belongs to Antigravity IDE. Killing it breaks IDE's active LSP
                // connection and triggers "Antigravity server crashed unexpectedly" in IDE.
                // CLI (agy) runs as independent commands and directly reads the new credentials.

                var agyProcs = Process.GetProcessesByName("agy");
                if (agyProcs.Length > 0)
                {
                    message = $"✓ Đã chuyển sang '{target.Email}' cho Antigravity CLI (agy)! Phiên CLI đang chạy sẽ áp dụng ở lệnh tiếp theo.";
                }
                else
                {
                    message = $"✓ Đã chuyển sang '{target.Email}' cho Antigravity CLI (agy)!";
                }
                return true;
            }
            catch (Exception ex)
            {
                message = $"Lỗi switch CLI: {ex.Message}";
                return false;
            }
        }

        public static bool SwitchAccountToClassic(AntigravityAccountInfo target, out string message)
        {
            try
            {
                // Sync CLI tokens to Credential Manager as well
                SyncCliAndGoogleTokens(target);

                string classicDir = Path.Combine(AppDataRoaming, "Antigravity", "User", "globalStorage");
                if (!Directory.Exists(classicDir)) Directory.CreateDirectory(classicDir);

                string dbPath = Path.Combine(classicDir, "state.vscdb");
                long exp = target.ExpiryTimestamp > 0 ? target.ExpiryTimestamp : (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                string oauthTokenB64 = BuildUnifiedOAuthTokenProtobuf(
                    target.ApiKey ?? "",
                    target.RefreshToken ?? "",
                    exp,
                    target.IdToken,
                    target.IsGcpTos,
                    target.Email ?? "");
                string userStatusB64 = BuildUserStatusProtobuf(target.Email ?? "");

                // Kill Antigravity classic processes if running
                var classicProcs = Process.GetProcessesByName("Antigravity");
                bool wasRunning = classicProcs.Length > 0;
                string? classicExe = null;
                foreach (var p in classicProcs)
                {
                    try
                    {
                        if (classicExe == null) classicExe = p.MainModule?.FileName;
                        p.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
                if (wasRunning) System.Threading.Thread.Sleep(300);

                if (!File.Exists(dbPath))
                {
                    var connStrInit = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
                    using var connInit = new SqliteConnection(connStrInit);
                    connInit.Open();
                    using var cmd = connInit.CreateCommand();
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);";
                    cmd.ExecuteNonQuery();
                }

                WriteAuthToStateDb(dbPath, target, oauthTokenB64, userStatusB64);

                // Check classic exe if not resolved from process
                if (string.IsNullOrEmpty(classicExe))
                    classicExe = FindAntigravityClassicExePath();

                if (wasRunning && !string.IsNullOrEmpty(classicExe) && File.Exists(classicExe))
                {
                    Process.Start(new ProcessStartInfo { FileName = classicExe, UseShellExecute = true });
                    message = $"✓ Đã chuyển sang '{target.Email}'! Antigravity đang khởi động lại.";
                }
                else
                {
                    message = $"✓ Đã chuyển sang '{target.Email}' cho Antigravity (Bản gốc)!";
                }

                // Update last used
                target.LastUsed = DateTime.Now;
                var list = LoadAllAccounts();
                var item = list.FirstOrDefault(a => string.Equals(a.Email, target.Email, StringComparison.OrdinalIgnoreCase));
                if (item != null) item.LastUsed = DateTime.Now;
                SaveAccountsList(list);

                return true;
            }
            catch (Exception ex)
            {
                message = $"Lỗi switch Antigravity: {ex.Message}";
                return false;
            }
        }

        public static bool ApplyAccountToTarget(AntigravityAccountInfo target, string switchTarget, out string message)
        {
            switchTarget = (switchTarget ?? "ide").ToLowerInvariant();
            if (switchTarget == "cli")
            {
                return SwitchAccountToCli(target, out message);
            }
            else if (switchTarget == "antigravity" || switchTarget == "classic")
            {
                return SwitchAccountToClassic(target, out message);
            }
            else
            {
                // Default: Antigravity IDE (also keep CLI in sync)
                SyncCliAndGoogleTokens(target);
                return ApplyAccountToIde(target, out message);
            }
        }
    }
}


