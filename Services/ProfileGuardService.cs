using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ProfileSwitchChrome.Services
{
    public class ProfileGuardService : IDisposable
    {
        private readonly AppConfig _config;
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private bool _wasRunning = false;

        public event Action<string>? LogMessage;

        public ProfileGuardService(AppConfig config)
        {
            _config = config;
        }

        public void Start()
        {
            if (_monitorTask != null && !_monitorTask.IsCompleted)
                return;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
            LogMessage?.Invoke("Dịch vụ bảo vệ Profile mặc định đã khởi động.");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _monitorTask?.Wait(1000);
            }
            catch { }
            _monitorTask = null;
            LogMessage?.Invoke("Dịch vụ bảo vệ Profile đã dừng.");
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_config.AutoProtectEnabled && !string.IsNullOrEmpty(_config.DefaultProfileId))
                    {
                        bool isRunning = ChromeProfileManager.IsChromeRunning();

                        // If Chrome just closed (transitioned from running to not running)
                        if (_wasRunning && !isRunning)
                        {
                            await Task.Delay(1500, token); // Wait for Chrome file locks to release
                            EnforceDefaultProfile();
                        }

                        _wasRunning = isRunning;
                    }

                    await Task.Delay(3000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Guard exception: " + ex.Message);
                    await Task.Delay(5000, token);
                }
            }
        }

        public void EnforceDefaultProfile()
        {
            if (string.IsNullOrEmpty(_config.DefaultProfileId))
                return;

            string localStatePath = ChromeProfileManager.GetLocalStatePath();
            if (!File.Exists(localStatePath))
                return;

            try
            {
                string jsonText = File.ReadAllText(localStatePath, Encoding.UTF8);
                var root = JObject.Parse(jsonText);
                var profile = root["profile"] as JObject;
                if (profile == null) return;

                string currentLast = profile["last_used"]?.ToString() ?? "";
                var lastActiveArray = profile["last_active_profiles"] as JArray;
                string currentActive = lastActiveArray != null && lastActiveArray.Count > 0 ? lastActiveArray[0]?.ToString() ?? "" : "";

                bool needUpdate = !string.Equals(currentLast, _config.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(currentActive, _config.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
                               || (lastActiveArray != null && lastActiveArray.Count > 1);

                if (needUpdate)
                {
                    profile["last_used"] = _config.DefaultProfileId;
                    profile["show_picker_on_startup"] = false;
                    profile["picker_shown"] = true;
                    profile["last_active_profiles"] = new JArray(_config.DefaultProfileId);

                    // Update active_time for this default profile
                    if (profile["info_cache"] is JObject infoCache && infoCache[_config.DefaultProfileId] is JObject targetData)
                    {
                        double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                        targetData["active_time"] = nowUnix;
                    }

                    // Place at the top of profiles_order
                    if (profile["profiles_order"] is JArray order)
                    {
                        var list = order.ToObject<System.Collections.Generic.List<string>>() ?? new();
                        list.Remove(_config.DefaultProfileId);
                        list.Insert(0, _config.DefaultProfileId);
                        profile["profiles_order"] = JArray.FromObject(list);
                    }

                    if (root["browser"] is JObject browserObj)
                    {
                        browserObj["show_profile_picker_on_startup"] = false;
                    }

                    File.WriteAllText(localStatePath, root.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
                    LogMessage?.Invoke($"[Tự động bảo vệ] Đã khôi phục Profile mặc định: {_config.DefaultProfileId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error enforcing default profile: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
