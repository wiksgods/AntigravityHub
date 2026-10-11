using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json.Linq;
using ProfileSwitchChrome.Models;

namespace ProfileSwitchChrome.Services
{
    /// <summary>
    /// Handles OAuth login flow for Antigravity accounts.
    /// Opens browser → local HTTP callback server → captures token → saves account.
    /// After logging in once, the refresh_token is stored so no re-login needed.
    ///
    /// Flow (same approach as lbjlaq/Antigravity-Manager):
    ///   1. Start local HTTP server on random port
    ///   2. Open browser to Antigravity auth page with redirect_uri=localhost:{port}/callback
    ///   3. User logs in → browser redirected back with access_token/refresh_token
    ///   4. Fetch user info (email, name) 
    ///   5. Save account to local storage
    /// </summary>
    public class OAuthLoginService
    {
        // Google OAuth credentials extracted from Antigravity Manager (Protected XOR encoding)
        private static readonly byte[] _rawOId = new byte[] { 0x6B, 0x6A, 0x6D, 0x6B, 0x6A, 0x6A, 0x6C, 0x6A, 0x6C, 0x6A, 0x6F, 0x63, 0x6B, 0x77, 0x2E, 0x37, 0x32, 0x29, 0x29, 0x33, 0x34, 0x68, 0x32, 0x68, 0x6B, 0x36, 0x39, 0x28, 0x3F, 0x68, 0x69, 0x6F, 0x2C, 0x2E, 0x35, 0x36, 0x35, 0x30, 0x32, 0x6E, 0x3D, 0x6E, 0x6A, 0x69, 0x3F, 0x2A, 0x74, 0x3B, 0x2A, 0x2A, 0x29, 0x74, 0x3D, 0x35, 0x35, 0x3D, 0x36, 0x3F, 0x2F, 0x29, 0x3F, 0x28, 0x39, 0x35, 0x34, 0x2E, 0x3F, 0x34, 0x2E, 0x74, 0x39, 0x35, 0x37 };
        private static readonly byte[] _rawOSec = new byte[] { 0x1D, 0x15, 0x19, 0x09, 0x0A, 0x02, 0x77, 0x11, 0x6F, 0x62, 0x1C, 0x0D, 0x08, 0x6E, 0x62, 0x6C, 0x16, 0x3E, 0x16, 0x10, 0x6B, 0x37, 0x16, 0x18, 0x62, 0x29, 0x02, 0x19, 0x6E, 0x20, 0x6C, 0x2B, 0x1E, 0x1B, 0x3C };
        private static string DecodeOAuth(byte[] raw) { var b = new byte[raw.Length]; for (int i = 0; i < raw.Length; i++) b[i] = (byte)(raw[i] ^ 90); return System.Text.Encoding.UTF8.GetString(b); }
        public static readonly string ClientId = DecodeOAuth(_rawOId);
        public static readonly string ClientSecret = DecodeOAuth(_rawOSec);
        public const int DefaultPort = 8888;
        public const string RedirectUri = "http://127.0.0.1:8888/oauth-callback";
        public const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        public const string TokenUrl = "https://oauth2.googleapis.com/token";
        public const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
        public const string Scopes = "openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/cclog https://www.googleapis.com/auth/experimentsandconfigs https://www.googleapis.com/auth/aicode";

        private int _port;
        private HttpListener? _listener;
        private CancellationTokenSource _cts = new();
        private TaskCompletionSource<OAuthResult?> _resultTcs = new();

        public event Action<string>? StatusChanged;
        public bool WasBrowserClosedManually { get; private set; }

        public OAuthLoginService()
        {
            _port = DefaultPort;
        }

        public static int? GetProcessIdUsingPort(int port)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c netstat -ano | findstr :{port}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1000);
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 5 && int.TryParse(parts[parts.Length - 1], out int pid) && pid > 0)
                            {
                                return pid;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void CleanupListener()
        {
            try
            {
                if (_listener != null)
                {
                    if (_listener.IsListening)
                    {
                        try { _listener.Stop(); } catch { }
                    }
                    try { _listener.Abort(); } catch { }
                    try { _listener.Close(); } catch { }
                }
            }
            catch { }
            finally
            {
                _listener = null;
            }
        }

        /// <summary>
        /// Starts the OAuth login flow asynchronously.
        /// Returns AntigravityAccountInfo if successful, null if cancelled/failed.
        /// </summary>
        public Task<AntigravityAccountInfo?> StartLoginAsync(CancellationToken externalCt = default)
            => StartLoginAsync(externalCt, loginHint: null, chromeProfileId: null);

        /// <summary>
        /// Starts the OAuth login flow with a login_hint (pre-fills email in Google login page)
        /// and optional chromeProfileId (opens directly inside that Chrome Profile without manual login/2FA).
        /// </summary>
        public async Task<AntigravityAccountInfo?> StartLoginAsync(
            CancellationToken externalCt, 
            string? loginHint, 
            string? chromeProfileId = null,
            bool isSilent = false)
        {
            CleanupListener();
            _port = DefaultPort;
            _resultTcs = new TaskCompletionSource<OAuthResult?>();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

            // 1. Start local HTTP callback server on port 8888 (với cơ chế retry nếu vừa đóng socket trước đó)
            bool started = false;
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Start();
                    started = true;
                    break;
                }
                catch
                {
                    CleanupListener();
                    if (retry < 4)
                    {
                        await Task.Delay(200);
                    }
                }
            }

            if (!started)
            {
                StatusChanged?.Invoke(LocalizationManager.IsEnglish ? $"Cannot open port {_port}. Port 8888 is already in use." : $"Không thể mở cổng {_port}. Cổng 8888 đang bị chiếm dụng.");
                CleanupListener();
                return null;
            }

            StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "Opening browser for sign-in..." : "Đang mở trình duyệt đăng nhập...");

            string redirectUri = RedirectUri;
            string state = Guid.NewGuid().ToString("N");

            // 2. Build Google OAuth URL
            string loginUrl = $"{AuthUrl}?client_id={Uri.EscapeDataString(ClientId)}" +
                              $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                              $"&response_type=code" +
                              $"&scope={Uri.EscapeDataString(Scopes)}" +
                              $"&access_type=offline" +
                              $"&prompt=consent" +
                              $"&state={state}";

            // Add login_hint to pre-fill email (user doesn't need to type it)
            if (!string.IsNullOrWhiteSpace(loginHint))
                loginUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

            // 3. Start auto-consent watcher and callback listener (2 minute timeout)
            // Starts BEFORE browser launch to capture the user's active window and keep it focused
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

            _ = Task.Run(() => GoogleOAuthAutoConsentService.StartWatchingAsync(
                linkedCts.Token,
                msg => StatusChanged?.Invoke(msg),
                loginHint,
                onBrowserClosed: () =>
                {
                    WasBrowserClosedManually = true;
                    Cancel();
                }),
                linkedCts.Token);

            _ = Task.Run(() => ListenForCallbackAsync(redirectUri, linkedCts.Token), linkedCts.Token);

            try
            {
                bool launched = false;
                if (!string.IsNullOrWhiteSpace(chromeProfileId))
                {
                    launched = ChromeProfileManager.LaunchChrome(chromeProfileId, loginUrl, forceAccessibility: false, isPopup: true);
                }

                if (!launched)
                {
                    string? chromeExe = ChromeProfileManager.FindChromeExePath();
                    if (!string.IsNullOrEmpty(chromeExe))
                    {
                        int sw = 1920, sh = 1080;
                        try
                        {
                            sw = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                            sh = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                        }
                        catch { }
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

                        var psi = new ProcessStartInfo
                        {
                            FileName = chromeExe,
                            Arguments = $"--new-window --app=\"{loginUrl}\" --window-size={tw},{th} --window-position={px},{py} --force-renderer-accessibility",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        };
                        Process.Start(psi);
                        launched = true;
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo(loginUrl) 
                        { 
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(LocalizationManager.IsEnglish ? $"Cannot open browser: {ex.Message}" : $"Không thể mở trình duyệt: {ex.Message}");
                CleanupListener();
                GoogleOAuthAutoConsentService.StopWatching();
                return null;
            }

            StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "Please confirm sign-in in your browser..." : "Vui lòng bấm xác nhận trên trình duyệt...");

            OAuthResult? result = null;
            try
            {
                result = await _resultTcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "Sign-in cancelled or timed out." : "Đăng nhập bị hủy hoặc hết thời gian.");
                return null;
            }
            finally
            {
                CleanupListener();
                GoogleOAuthAutoConsentService.StopWatching();
            }

            if (result != null)
            {
                // Ensure OAuth browser window is definitely closed
                GoogleOAuthAutoConsentService.CloseAllOAuthWindows();
            }

            if (result == null)
            {
                StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "No token received from Google." : "Không nhận được token từ Google.");
                return null;
            }

            // 4. Fetch user info (email, name, picture) using the access token
            StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "Fetching Google account info..." : "Đang lấy thông tin tài khoản Google...");
            var userInfo = await FetchUserInfoAsync(result.AccessToken);
            if (userInfo == null)
            {
                StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "Failed to retrieve account info." : "Không lấy được thông tin tài khoản.");
                return null;
            }

            // 5. Build and return account info
            var account = new AntigravityAccountInfo
            {
                Email = userInfo.Email,
                Name = userInfo.Name,
                ApiKey = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiryTimestamp = result.ExpiresAt,
                IdToken = result.IdToken,
                AvatarUrl = userInfo.AvatarUrl,
                CreatedAt = DateTime.Now,
                LastUsed = DateTime.Now,
            };

            // Build raw OAuth token blob for state.vscdb injection
            account.RawOauthToken = AntigravityProfileManager.BuildUnifiedOAuthTokenProtobuf(
                result.AccessToken,
                result.RefreshToken,
                result.ExpiresAt,
                result.IdToken,
                false,
                userInfo.Email
            );

            account.RawUserStatus = AntigravityProfileManager.BuildUserStatusProtobuf(userInfo.Email);

            StatusChanged?.Invoke(LocalizationManager.IsEnglish ? $"✓ Sign-in successful: {userInfo.Email}" : $"✓ Đăng nhập thành công: {userInfo.Email}");
            return account;
        }

        private async Task ListenForCallbackAsync(string redirectUri, CancellationToken ct)
        {
            using var reg = ct.Register(() => CleanupListener());
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { break; }

                var req = context.Request;
                var resp = context.Response;

                // Skip favicon
                if (req.Url?.AbsolutePath == "/favicon.ico")
                {
                    resp.StatusCode = 204;
                    resp.Close();
                    continue;
                }

                try
                {
                    var query = HttpUtility.ParseQueryString(req.Url?.Query ?? "");
                    string? error = query["error"];
                    string? accessToken = query["access_token"];
                    string? refreshToken = query["refresh_token"];
                    string? code = query["code"];

                    if (!string.IsNullOrEmpty(error))
                    {
                        await SendHtmlResponse(resp, BuildErrorHtml(error));
                        _resultTcs.TrySetResult(null);
                        return;
                    }

                    // Case A: Direct tokens in query string
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        long expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
                        if (long.TryParse(query["expires_in"], out long ei))
                            expiresAt = DateTimeOffset.UtcNow.AddSeconds(ei).ToUnixTimeMilliseconds();

                        GoogleOAuthAutoConsentService.CloseAllOAuthWindows();
                        await SendHtmlResponse(resp, BuildSuccessHtml());
                        _resultTcs.TrySetResult(new OAuthResult
                        {
                            AccessToken = accessToken,
                            RefreshToken = refreshToken ?? "",
                            ExpiresAt = expiresAt,
                            IdToken = query["id_token"] ?? ""
                        });
                        return;
                    }

                    // Case B: Authorization code → exchange for tokens
                    if (!string.IsNullOrEmpty(code))
                    {
                        // Đóng ngay lập tức trình duyệt không cần chờ đợi
                        GoogleOAuthAutoConsentService.CloseAllOAuthWindows();
                        await SendHtmlResponse(resp, BuildSuccessHtml());
                        StatusChanged?.Invoke(LocalizationManager.IsEnglish ? "✓ Sign-in successful! Saving account..." : "✓ Đăng nhập thành công! Đang lưu thông tin...");

                        var tokenResult = await ExchangeCodeAsync(code, redirectUri);
                        _resultTcs.TrySetResult(tokenResult);
                        return;
                    }

                    await SendHtmlResponse(resp, BuildErrorHtml(LocalizationManager.IsEnglish ? "No token received." : "Không nhận được token."));
                    _resultTcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Callback processing error: {ex.Message}");
                    try { resp.Close(); } catch { }
                    _resultTcs.TrySetException(ex);
                    return;
                }
            }
        }

        private async Task<OAuthResult?> ExchangeCodeAsync(string code, string redirectUri)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Add("User-Agent", "Antigravity/1.0 (Windows NT 10.0; Win64; x64)");

                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code"
                });

                var resp = await http.PostAsync(TokenUrl, content);
                string body = await resp.Content.ReadAsStringAsync();

                JObject json;
                try { json = JObject.Parse(body); }
                catch { return null; }

                string? at = json["access_token"]?.ToString();
                string? rt = json["refresh_token"]?.ToString();
                string? idToken = json["id_token"]?.ToString();
                if (string.IsNullOrEmpty(at))
                {
                    Debug.WriteLine($"Token exchange failed: {body}");
                    return null;
                }

                long expiryTimestamp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
                if (long.TryParse(json["expires_in"]?.ToString(), out long ei))
                    expiryTimestamp = DateTimeOffset.UtcNow.AddSeconds(ei).ToUnixTimeSeconds();

                return new OAuthResult
                {
                    AccessToken = at,
                    RefreshToken = rt ?? "",
                    ExpiresAt = expiryTimestamp,
                    IdToken = idToken ?? ""
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Token exchange error: {ex.Message}");
                return null;
            }
        }

        private async Task<UserInfoResult?> FetchUserInfoAsync(string accessToken)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                http.DefaultRequestHeaders.Add("User-Agent", "Antigravity/1.0 (Windows NT 10.0; Win64; x64)");

                var resp = await http.GetAsync(UserInfoUrl);
                string body = await resp.Content.ReadAsStringAsync();

                JObject json;
                try { json = JObject.Parse(body); }
                catch { return null; }

                string? email = json["email"]?.ToString();
                string? name = json["name"]?.ToString() ?? email;
                string? picture = json["picture"]?.ToString();

                if (string.IsNullOrEmpty(email)) return null;
                return new UserInfoResult
                {
                    Email = email,
                    Name = name ?? email,
                    AvatarUrl = picture
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UserInfo error: {ex.Message}");
                return null;
            }
        }

        private static async Task SendHtmlResponse(HttpListenerResponse resp, string html)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            resp.ContentType = "text/html; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.StatusCode = 200;
            await resp.OutputStream.WriteAsync(bytes);
            resp.Close();
        }

        private static string BuildSuccessHtml() => @"<!DOCTYPE html><html><head><meta charset='utf-8'><title></title><script>window.open('','_self','');window.close();</script></head><body style='background:#0d1117;margin:0;'></body></html>";

        private static string BuildErrorHtml(string error) => $@"<!DOCTYPE html><html><head><meta charset='utf-8'>
<title>{(LocalizationManager.IsEnglish ? "Sign-in Error" : "Lỗi đăng nhập")}</title>
<style>body{{font-family:'Segoe UI',sans-serif;background:#0d1117;color:#e6edf3;display:flex;align-items:center;
justify-content:center;height:100vh;margin:0}}.card{{background:#161b22;border:1px solid #f85149;
border-radius:12px;padding:40px 48px;text-align:center;max-width:400px}}.icon{{font-size:48px;margin-bottom:16px}}
h1{{color:#f85149;font-size:22px;margin:0 0 8px}}p{{color:#8b949e;margin:0}}</style></head>
<body><div class='card'><div class='icon'>❌</div><h1>{(LocalizationManager.IsEnglish ? "Sign-in Failed" : "Đăng nhập thất bại")}</h1>
<p>{error}</p></div></body></html>";

        private static string BuildLoadingHtml() => $@"<!DOCTYPE html><html><head><meta charset='utf-8'>
<title>{(LocalizationManager.IsEnglish ? "Processing..." : "Đang xử lý...")}</title>
<style>body{{font-family:'Segoe UI',sans-serif;background:#0d1117;color:#e6edf3;display:flex;align-items:center;
justify-content:center;height:100vh;margin:0}}.card{{background:#161b22;border:1px solid #30363d;
border-radius:12px;padding:40px 48px;text-align:center;max-width:400px}}
.spin{{font-size:40px;animation:s 1s linear infinite;display:inline-block}}
@keyframes s{{from{{transform:rotate(0)}}to{{transform:rotate(360deg)}}}}
h1{{color:#58a6ff;font-size:22px;margin:16px 0 8px}}p{{color:#8b949e;margin:0}}</style></head>
<body><div class='card'><div class='spin'>⚙️</div><h1>{(LocalizationManager.IsEnglish ? "Processing..." : "Đang xử lý...")}</h1>
<p>{(LocalizationManager.IsEnglish ? "Please wait a moment." : "Vui lòng đợi trong giây lát.")}</p></div></body></html>";

        /// <summary>
        /// Uses refresh_token to acquire a fresh access_token from Google OAuth.
        /// </summary>
        public static async Task<OAuthResult?> RefreshAccessTokenAsync(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken)) return null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "Antigravity/1.0 (Windows NT 10.0; Win64; x64)");

                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                });

                var resp = await http.PostAsync(TokenUrl, content);
                if (!resp.IsSuccessStatusCode) return null;

                string body = await resp.Content.ReadAsStringAsync();
                var json = JObject.Parse(body);
                string? at = json["access_token"]?.ToString();
                if (string.IsNullOrEmpty(at)) return null;

                string? idToken = json["id_token"]?.ToString();
                long expiryTimestamp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
                if (long.TryParse(json["expires_in"]?.ToString(), out long ei))
                    expiryTimestamp = DateTimeOffset.UtcNow.AddSeconds(ei).ToUnixTimeSeconds();

                return new OAuthResult
                {
                    AccessToken = at,
                    RefreshToken = refreshToken,
                    ExpiresAt = expiryTimestamp,
                    IdToken = idToken ?? ""
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshAccessToken error: {ex.Message}");
                return null;
            }
        }

        public void Cancel()
        {
            try { _cts.Cancel(); } catch { }
            _resultTcs.TrySetResult(null);
            CleanupListener();
            GoogleOAuthAutoConsentService.StopWatching();
            try
            {
                if (GoogleOAuthAutoConsentService.LastOAuthHwnd != IntPtr.Zero)
                {
                    GoogleOAuthAutoConsentService.CloseOAuthTab(GoogleOAuthAutoConsentService.LastOAuthHwnd);
                }
            }
            catch { }
        }
    }

    public class OAuthResult
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public long ExpiresAt { get; set; }
        public string IdToken { get; set; } = "";
    }

    public class UserInfoResult
    {
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string? AvatarUrl { get; set; }
    }
}
