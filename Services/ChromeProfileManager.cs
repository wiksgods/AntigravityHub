using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProfileSwitchChrome.Models;

namespace ProfileSwitchChrome.Services
{
    public class ChromeProfileManager
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        public static string GetUserDataPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data");
        }

        public static string GetLocalStatePath()
        {
            return Path.Combine(GetUserDataPath(), "Local State");
        }

        public static string? FindChromeExePath()
        {
            // 1. Registry App Paths
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                if (key != null)
                {
                    var val = key.GetValue("")?.ToString();
                    if (!string.IsNullOrEmpty(val) && File.Exists(val))
                        return val;
                }
            }
            catch { }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                if (key != null)
                {
                    var val = key.GetValue("")?.ToString();
                    if (!string.IsNullOrEmpty(val) && File.Exists(val))
                        return val;
                }
            }
            catch { }

            // 2. Standard filesystem paths
            string[] candidatePaths = new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        public static bool IsChromeRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("chrome");
                return procs.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static int GetChromeProcessCount()
        {
            try
            {
                return Process.GetProcessesByName("chrome").Length;
            }
            catch
            {
                return 0;
            }
        }

        #region PDH Memory Native Interop
        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArray(
            IntPtr hCounter,
            uint dwFormat,
            ref uint lpdwBufferSize,
            out uint lpdwItemCount,
            IntPtr itemBuffer);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhCloseQuery(IntPtr hQuery);

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        #endregion

        /// <summary>
        /// Gets the process count and Private Working Set RAM usage of Chrome (matching Windows Task Manager).
        /// </summary>
        public static (int procCount, long totalMemoryBytes) GetChromeProcessStats()
        {
            try
            {
                var procs = Process.GetProcessesByName("chrome");
                int count = procs.Length;
                if (count == 0)
                    return (0, 0);

                // Use PDH to query exact Private Working Set matching Windows Task Manager
                long privateBytes = GetChromePrivateWorkingSetBytes();
                if (privateBytes > 0)
                {
                    return (count, privateBytes);
                }

                // Safe fallback: sum Private Working Set approximation if PDH fails
                // Private Working Set is bounded by min(WorkingSet64, PrivateMemorySize64)
                long totalMemory = 0;
                foreach (var p in procs)
                {
                    try 
                    { 
                        long ws = p.WorkingSet64;
                        long priv = p.PrivateMemorySize64;
                        totalMemory += (priv > 0 && ws > 0) ? Math.Min(ws, priv) : (ws > 0 ? ws : priv);
                    } 
                    catch { }
                }
                return (count, totalMemory);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static long GetChromePrivateWorkingSetBytes()
        {
            IntPtr hQuery = IntPtr.Zero;
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out hQuery) != 0)
                    return 0;

                IntPtr hCounter;
                if (PdhAddEnglishCounter(hQuery, @"\Process(chrome*)\Working Set - Private", IntPtr.Zero, out hCounter) != 0)
                    return 0;

                if (PdhCollectQueryData(hQuery) != 0)
                    return 0;

                uint bufferSize = 0;
                uint itemCount = 0;
                PdhGetFormattedCounterArray(hCounter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, IntPtr.Zero);
                if (bufferSize == 0)
                    return 0;

                IntPtr pBuffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    if (PdhGetFormattedCounterArray(hCounter, PDH_FMT_DOUBLE, ref bufferSize, out itemCount, pBuffer) == 0)
                    {
                        double totalBytes = 0;
                        int itemSize = IntPtr.Size == 8 ? 24 : 16;
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr itemPtr = IntPtr.Add(pBuffer, i * itemSize);
                            IntPtr namePtr = Marshal.ReadIntPtr(itemPtr);
                            string? name = Marshal.PtrToStringUni(namePtr);
                            if (!string.IsNullOrEmpty(name) &&
                                (name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("chrome#", StringComparison.OrdinalIgnoreCase)))
                            {
                                int valOffset = IntPtr.Size == 8 ? 16 : 8;
                                byte[] valBytes = new byte[8];
                                Marshal.Copy(IntPtr.Add(itemPtr, valOffset), valBytes, 0, 8);
                                double dVal = BitConverter.ToDouble(valBytes, 0);
                                if (!double.IsNaN(dVal) && !double.IsInfinity(dVal) && dVal > 0 && dVal < 64L * 1024 * 1024 * 1024)
                                {
                                    totalBytes += dVal;
                                }
                            }
                        }
                        if (totalBytes > 0)
                            return (long)totalBytes;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuffer);
                }
            }
            catch
            {
                // Fallback to 0 so caller uses standard method
            }
            finally
            {
                if (hQuery != IntPtr.Zero)
                    PdhCloseQuery(hQuery);
            }
            return 0;
        }

        public static HashSet<string> GetActiveProfiles()
        {
            var activeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IsChromeRunning())
                return activeSet;

            string userDataDir = GetUserDataPath();
            if (!Directory.Exists(userDataDir))
                return activeSet;

            string[] testFiles = new[] { "Web Data", "History", "Network\\Cookies" };

            try
            {
                var subDirs = Directory.GetDirectories(userDataDir);
                foreach (var dir in subDirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (!dirName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                        !dirName.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var tf in testFiles)
                    {
                        string filePath = Path.Combine(dir, tf);
                        if (File.Exists(filePath))
                        {
                            try
                            {
                                using var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            }
                            catch (IOException)
                            {
                                // File is locked by Chrome -> Profile is actively running!
                                activeSet.Add(dirName);
                                break;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return activeSet;
        }

        public static bool KillChromeProcesses()
        {
            try
            {
                var procs = Process.GetProcessesByName("chrome");
                foreach (var p in procs)
                {
                    try { p.Kill(); } catch { }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<ChromeProfileInfo> LoadProfiles()
        {
            var result = new List<ChromeProfileInfo>();
            string localStatePath = GetLocalStatePath();
            string userDataDir = GetUserDataPath();

            if (!File.Exists(localStatePath))
                return result;

            try
            {
                string jsonText = File.ReadAllText(localStatePath, Encoding.UTF8);
                var root = JObject.Parse(jsonText);

                var profileObj = root["profile"] as JObject;
                if (profileObj == null)
                    return result;

                string currentLastUsed = profileObj["last_used"]?.ToString() ?? "Default";
                var infoCache = profileObj["info_cache"] as JObject;

                if (infoCache != null)
                {
                    foreach (var prop in infoCache.Properties())
                    {
                        string profileId = prop.Name;
                        if (prop.Value is not JObject pData)
                            continue;

                        string name = pData["name"]?.ToString() ?? profileId;
                        string userName = pData["user_name"]?.ToString() ?? "";
                        string gaiaPic = pData["gaia_picture_file_name"]?.ToString() ?? "";
                        int colorSeed = pData["profile_color_seed"]?.Value<int>() ?? 0;

                        string avatarPath = "";
                        if (!string.IsNullOrEmpty(gaiaPic))
                        {
                            string testPath = Path.Combine(userDataDir, profileId, gaiaPic);
                            if (File.Exists(testPath))
                                avatarPath = testPath;
                        }

                        if (string.IsNullOrEmpty(avatarPath))
                        {
                            string testDefaultPic = Path.Combine(userDataDir, profileId, "Google Profile Picture.png");
                            if (File.Exists(testDefaultPic))
                                avatarPath = testDefaultPic;
                        }

                        var googleAccounts = LoadGoogleAccountsForProfile(userDataDir, profileId, userName, pData);

                        // If userName was empty but googleAccounts has accounts, use the first account's email
                        if (string.IsNullOrWhiteSpace(userName) && googleAccounts.Count > 0)
                        {
                            userName = googleAccounts[0].Email;
                        }

                        result.Add(new ChromeProfileInfo
                        {
                            Id = profileId,
                            Name = name,
                            UserName = userName,
                            AvatarPath = avatarPath,
                            ColorSeed = colorSeed,
                            IsDefault = string.Equals(profileId, currentLastUsed, StringComparison.OrdinalIgnoreCase),
                            GoogleAccounts = googleAccounts
                        });
                    }
                }

                // If info_cache didn't have Default, check if Default folder exists
                if (!result.Any(p => p.Id.Equals("Default", StringComparison.OrdinalIgnoreCase)) &&
                    Directory.Exists(Path.Combine(userDataDir, "Default")))
                {
                    var defAccounts = LoadGoogleAccountsForProfile(userDataDir, "Default", "", null);
                    string defUserName = defAccounts.Count > 0 ? defAccounts[0].Email : "";

                    result.Insert(0, new ChromeProfileInfo
                    {
                        Id = "Default",
                        Name = "Default Profile",
                        UserName = defUserName,
                        AvatarPath = Path.Combine(userDataDir, "Default", "Google Profile Picture.png"),
                        IsDefault = string.Equals("Default", currentLastUsed, StringComparison.OrdinalIgnoreCase),
                        GoogleAccounts = defAccounts
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error parsing Chrome Local State: " + ex.Message);
            }

            // Order so current default comes first, then by name
            return result
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Id == "Default" ? 0 : 1)
                .ThenBy(p => p.Name)
                .ToList();
        }

        public static List<GoogleAccountInfo> LoadGoogleAccountsForProfile(
            string userDataDir,
            string profileId,
            string fallbackUserName,
            JObject? pData)
        {
            var accounts = new List<GoogleAccountInfo>();
            string prefPath = Path.Combine(userDataDir, profileId, "Preferences");

            if (File.Exists(prefPath))
            {
                try
                {
                    string prefJson = File.ReadAllText(prefPath, Encoding.UTF8);
                    var prefRoot = JObject.Parse(prefJson);
                    var accountInfoArr = prefRoot["account_info"] as JArray;

                    if (accountInfoArr != null)
                    {
                        foreach (var item in accountInfoArr)
                        {
                            string email = item["email"]?.ToString() ?? "";
                            if (string.IsNullOrWhiteSpace(email))
                                continue;

                            string fullName = item["full_name"]?.ToString() ?? "";
                            string givenName = item["given_name"]?.ToString() ?? "";
                            string accountId = item["account_id"]?.ToString() ?? "";
                            bool isPrimary = string.Equals(email, fallbackUserName, StringComparison.OrdinalIgnoreCase);

                            accounts.Add(new GoogleAccountInfo
                            {
                                Email = email,
                                FullName = fullName,
                                GivenName = givenName,
                                AccountId = accountId,
                                IsPrimary = isPrimary,
                                ProfileId = profileId
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading Preferences for {profileId}: {ex.Message}");
                }
            }

            // Fallback: If no account_info found, but user_name exists in Local State
            if (accounts.Count == 0 && !string.IsNullOrWhiteSpace(fallbackUserName) && fallbackUserName.Contains("@"))
            {
                string gaiaName = pData?["gaia_name"]?.ToString() ?? "";
                string gaiaId = pData?["gaia_id"]?.ToString() ?? "";

                accounts.Add(new GoogleAccountInfo
                {
                    Email = fallbackUserName,
                    FullName = gaiaName,
                    GivenName = gaiaName,
                    AccountId = gaiaId,
                    IsPrimary = true,
                    ProfileId = profileId
                });
            }

            // Ensure primary account is first and marked as primary
            if (accounts.Count > 0)
            {
                if (!accounts.Any(a => a.IsPrimary))
                {
                    accounts[0].IsPrimary = true;
                }
                // Order: primary first, then by email
                accounts = accounts
                    .OrderByDescending(a => a.IsPrimary)
                    .ThenBy(a => a.Email)
                    .ToList();
            }

            return accounts;
        }

        public static bool SetDefaultProfile(
            string profileId,
            bool updateShortcuts,
            bool updateRegistry,
            out string statusMessage)
        {
            string localStatePath = GetLocalStatePath();
            if (!File.Exists(localStatePath))
            {
                statusMessage = "Không tìm thấy file cấu hình Chrome (Local State)!";
                return false;
            }

            var sb = new StringBuilder();

            try
            {
                // 1. Update Local State
                string jsonText = File.ReadAllText(localStatePath, Encoding.UTF8);
                var root = JObject.Parse(jsonText);

                // Create backup if not exists
                string bakPath = localStatePath + ".bak";
                if (!File.Exists(bakPath))
                {
                    File.Copy(localStatePath, bakPath, true);
                }

                if (root["profile"] is not JObject profileObj)
                {
                    profileObj = new JObject();
                    root["profile"] = profileObj;
                }

                profileObj["last_used"] = profileId;
                profileObj["show_picker_on_startup"] = false;
                profileObj["picker_shown"] = true;
                profileObj["last_active_profiles"] = new JArray(profileId);

                // Update active_time for this profile so Chrome treats it as the most recently active profile
                var infoCache = profileObj["info_cache"] as JObject;
                if (infoCache != null && infoCache[profileId] is JObject targetData)
                {
                    double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    targetData["active_time"] = nowUnix;
                }

                // Place this profile at the top of profiles_order
                if (profileObj["profiles_order"] is JArray order)
                {
                    var list = order.ToObject<List<string>>() ?? new List<string>();
                    list.Remove(profileId);
                    list.Insert(0, profileId);
                    profileObj["profiles_order"] = JArray.FromObject(list);
                }

                if (root["browser"] is JObject browserObj)
                {
                    browserObj["show_profile_picker_on_startup"] = false;
                }

                File.WriteAllText(localStatePath, root.ToString(Formatting.Indented), Encoding.UTF8);
                sb.AppendLine($"✓ Đã đồng bộ cấu hình Chrome (last_used, active_time, last_active_profiles).");

                // 2. Update Shortcuts
                if (updateShortcuts)
                {
                    int updatedCount = UpdateAllChromeShortcuts(profileId);
                    sb.AppendLine($"✓ Đã cập nhật {updatedCount} phím tắt Chrome trên máy.");
                }

                // 3. Update Registry
                if (updateRegistry)
                {
                    if (UpdateRegistryCommand(profileId))
                    {
                        sb.AppendLine("✓ Đã cập nhật Registry (khi click link ngoài sẽ mở đúng Profile này).");
                    }
                    else
                    {
                        sb.AppendLine("⚠ Không thể cập nhật Registry (vẫn hoạt động tốt qua shortcut).");
                    }
                }

                statusMessage = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = "Lỗi khi ghi dữ liệu: " + ex.Message;
                return false;
            }
        }

        public static int UpdateAllChromeShortcuts(string profileId)
        {
            string? chromeExe = FindChromeExePath();
            if (string.IsNullOrEmpty(chromeExe))
                return 0;

            int count = 0;
            var shortcutPaths = new List<string>();

            // Desktop current user
            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (Directory.Exists(userDesktop))
            {
                shortcutPaths.AddRange(Directory.GetFiles(userDesktop, "*.lnk", SearchOption.TopDirectoryOnly));
            }

            // Public Desktop
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (Directory.Exists(publicDesktop))
            {
                shortcutPaths.AddRange(Directory.GetFiles(publicDesktop, "*.lnk", SearchOption.TopDirectoryOnly));
            }

            // Start Menu user
            string userStart = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            if (Directory.Exists(userStart))
            {
                shortcutPaths.AddRange(Directory.GetFiles(userStart, "*.lnk", SearchOption.AllDirectories));
            }

            // Common Start Menu (All Users)
            string commonStart = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            if (Directory.Exists(commonStart))
            {
                shortcutPaths.AddRange(Directory.GetFiles(commonStart, "*.lnk", SearchOption.AllDirectories));
            }

            // Quick Launch (includes TaskBar, User Pinned, and root Quick Launch)
            string quickLaunch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch");
            if (Directory.Exists(quickLaunch))
            {
                shortcutPaths.AddRange(Directory.GetFiles(quickLaunch, "*.lnk", SearchOption.AllDirectories));
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return 0;

            dynamic shell = Activator.CreateInstance(shellType)!;

            // 1. Update existing shortcuts
            foreach (var file in shortcutPaths.Distinct())
            {
                try
                {
                    dynamic sc = shell.CreateShortcut(file);
                    string target = sc.TargetPath?.ToString() ?? "";

                    if (target.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(file).Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                    {
                        sc.TargetPath = chromeExe;
                        sc.Arguments = $"--profile-directory=\"{profileId}\"";
                        sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                        sc.IconLocation = $"{chromeExe},0";
                        sc.Save();
                        count++;
                    }
                }
                catch
                {
                    // Ignore access errors on protected shortcuts
                }
            }

            // 2. ALWAYS enforce User Desktop 'Google Chrome.lnk'
            try
            {
                string mainDesktopLnk = Path.Combine(userDesktop, "Google Chrome.lnk");
                dynamic sc = shell.CreateShortcut(mainDesktopLnk);
                sc.TargetPath = chromeExe;
                sc.Arguments = $"--profile-directory=\"{profileId}\"";
                sc.Description = "Google Chrome";
                sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                sc.IconLocation = $"{chromeExe},0";
                sc.Save();
                count++;
            }
            catch { }

            // 3. ALWAYS enforce User Start Menu 'Google Chrome.lnk'
            try
            {
                string mainStartLnk = Path.Combine(userStart, "Google Chrome.lnk");
                dynamic sc = shell.CreateShortcut(mainStartLnk);
                sc.TargetPath = chromeExe;
                sc.Arguments = $"--profile-directory=\"{profileId}\"";
                sc.Description = "Google Chrome";
                sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                sc.IconLocation = $"{chromeExe},0";
                sc.Save();
                count++;
            }
            catch { }

            // 4. ALWAYS enforce Quick Launch 'Google Chrome.lnk'
            try
            {
                string qlLnk = Path.Combine(quickLaunch, "Google Chrome.lnk");
                dynamic sc = shell.CreateShortcut(qlLnk);
                sc.TargetPath = chromeExe;
                sc.Arguments = $"--profile-directory=\"{profileId}\"";
                sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                sc.IconLocation = $"{chromeExe},0";
                sc.Save();
                count++;
            }
            catch { }

            // 5. Update taskbar if Google Chrome.lnk exists there
            try
            {
                string taskbarChromeLnk = Path.Combine(quickLaunch, "User Pinned", "TaskBar", "Google Chrome.lnk");
                dynamic sc = shell.CreateShortcut(taskbarChromeLnk);
                sc.TargetPath = chromeExe;
                sc.Arguments = $"--profile-directory=\"{profileId}\"";
                sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                sc.IconLocation = $"{chromeExe},0";
                sc.Save();
                count++;
            }
            catch { }

            return count;
        }

        public static bool CreateDedicatedShortcut(string profileId, string profileDisplayName)
        {
            try
            {
                string? chromeExe = FindChromeExePath();
                if (string.IsNullOrEmpty(chromeExe))
                    return false;

                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string cleanName = string.Join("_", profileDisplayName.Split(Path.GetInvalidFileNameChars()));
                string shortcutPath = Path.Combine(userDesktop, $"Chrome - {cleanName}.lnk");

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic sc = shell.CreateShortcut(shortcutPath);
                sc.TargetPath = chromeExe;
                sc.Arguments = $"--profile-directory=\"{profileId}\"";
                sc.Description = $"Google Chrome ({profileDisplayName})";
                sc.WorkingDirectory = Path.GetDirectoryName(chromeExe);
                sc.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool UpdateRegistryCommand(string profileId)
        {
            try
            {
                string? chromeExe = FindChromeExePath();
                if (string.IsNullOrEmpty(chromeExe))
                    return false;

                // Format: "C:\...\chrome.exe" --profile-directory="Profile 1" --single-argument %1
                string cmdValue = $"\"{chromeExe}\" --profile-directory=\"{profileId}\" --single-argument %1";

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ChromeHTML\shell\open\command"))
                {
                    if (key != null)
                    {
                        key.SetValue("", cmdValue);
                    }
                }

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\http\shell\open\command"))
                {
                    if (key != null)
                    {
                        key.SetValue("", cmdValue);
                    }
                }

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\https\shell\open\command"))
                {
                    if (key != null)
                    {
                        key.SetValue("", cmdValue);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Silently launches Chrome in background with the target profile and --no-startup-window.
        /// This registers the profile into Chrome's in-memory state so subsequent clicks on Chrome
        /// shortcuts open the correct profile WITHOUT killing the existing Chrome session.
        /// The process closes on its own after a short delay since no window is shown.
        /// </summary>
        public static void SilentRegisterProfile(string profileId)
        {
            try
            {
                string? chromeExe = FindChromeExePath();
                if (string.IsNullOrEmpty(chromeExe))
                    return;

                var psi = new ProcessStartInfo
                {
                    FileName = chromeExe,
                    // --no-startup-window: launches Chrome into RAM without showing any window
                    // --profile-directory: registers this profile as the active one in Chrome's RAM
                    Arguments = $"--profile-directory=\"{profileId}\" --no-startup-window --disable-features=ChromeWhatsNewUI",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                var proc = Process.Start(psi);

                // Give Chrome 1.5s to register the profile then forget about the process
                if (proc != null)
                {
                    System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                // Don't kill - Chrome will manage its own background process lifecycle
                                // Just let it live in the background (Chrome background mode)
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        public static bool LaunchChrome(
            string profileId, 
            string url = "", 
            bool forceAccessibility = false, 
            bool hidden = false,
            bool isPopup = false)
        {
            try
            {
                string? chromeExe = FindChromeExePath();
                if (string.IsNullOrEmpty(chromeExe))
                    return false;

                string args;
                if (isPopup && !string.IsNullOrWhiteSpace(url))
                {
                    int sw = GetSystemMetrics(SM_CXSCREEN);
                    int sh = GetSystemMetrics(SM_CYSCREEN);
                    if (sw <= 0) sw = 1920;
                    if (sh <= 0) sh = 1080;
                    int tw = Math.Min(1020, Math.Max(840, sw - 320));
                    int th = Math.Min(880, Math.Max(660, sh - 80));
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

                    // Đặt --app LÊN ĐẦU: Buộc Chrome mở cửa sổ độc lập (App window) không có thanh tìm kiếm / tabs
                    if (!string.IsNullOrWhiteSpace(profileId))
                    {
                        args = $"--app=\"{url}\" --profile-directory=\"{profileId}\" --window-size={tw},{th} --window-position={px},{py} --force-renderer-accessibility";
                    }
                    else
                    {
                        args = $"--app=\"{url}\" --window-size={tw},{th} --window-position={px},{py} --force-renderer-accessibility";
                    }
                }
                else
                {
                    args = $"--profile-directory=\"{profileId}\"";
                    if (forceAccessibility)
                    {
                        args += " --force-renderer-accessibility --disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";
                    }
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        args += $" \"{url}\"";
                    }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = chromeExe,
                    Arguments = args,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
