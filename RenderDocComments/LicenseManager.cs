using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RenderDocComments
{
    public static class LicenseManager
    {
        // ── Endpoints ─────────────────────────────────────────────────────────────

        // Switch to https://live.dodopayments.com before shipping.
        private const string DodoBase = "https://test.dodopayments.com";

        // Your Cloudflare Worker URL — replace before shipping.
        private const string WorkerUrl = "https://dodo-test-checkout.amit-ku-p-2806.workers.dev";

        private const string UriScheme = "renderdoccomments";

        // ── Public surface ────────────────────────────────────────────────────────

        public static bool PremiumUnlocked => RenderDocOptions.Instance.PremiumUnlocked;

        /// <summary>
        /// Calls the Cloudflare Worker to create a Dodo checkout session,
        /// then opens the returned checkout_url in the default browser.
        /// </summary>
        public static (bool Success, string Message) OpenCheckoutPage()
        {
            try
            {
                return Task.Run(() => OpenCheckoutPageAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return (false, $"Could not open checkout: {ex.Message}");
            }
        }

        private static async Task<(bool Success, string Message)> OpenCheckoutPageAsync()
        {
            var json = BuildJson(("quantity", "1"));
            var resp = await PostJsonAsync(WorkerUrl, json).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, $"Checkout unavailable (HTTP {(int)resp.StatusCode}). Please try again later.");

            var checkoutUrl = ReadStringField(body, "url")
                           ?? ReadStringField(body, "checkout_url")
                           ?? ReadStringField(body, "payment_url");

            if (string.IsNullOrEmpty(checkoutUrl))
                return (false, $"Checkout unavailable: unexpected server response. Raw: {body}");

            Process.Start(new ProcessStartInfo(checkoutUrl) { UseShellExecute = true });
            return (true, string.Empty);
        }

        // ── Activate / Deactivate / Validate ─────────────────────────────────────

        /// <summary>
        /// Activate a licence key on this machine.
        /// Returns (success, userFacingMessage).
        /// </summary>
        public static (bool Success, string Message) Activate(string licenseKey)
        {
            try
            {
                return Task.Run(() => ActivateAsync(licenseKey)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return (false, $"Activation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Deactivate this machine's licence instance, freeing up the activation slot.
        /// </summary>
        public static (bool Success, string Message) Deactivate()
        {
            try
            {
                return Task.Run(() => DeactivateAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return (false, $"Deactivation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silently re-validate the stored key on VS startup.
        /// Revokes local Premium if the key has been refunded or revoked server-side.
        /// </summary>
        public static async Task RevalidateOnStartupAsync(IServiceProvider serviceProvider)
        {
            var key = RenderDocOptions.Instance.LicenseKey;
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                var instanceId = RenderDocOptions.Instance.LicenseInstanceId;
                bool valid = await ValidateLicenseAsync(key, instanceId).ConfigureAwait(false);
                if (!valid)
                {
                    RenderDocOptions.Instance.SetPremiumUnlocked(false);
                    RenderDocOptions.Instance.Save(serviceProvider);
                }
            }
            catch
            {
                // Network unreachable — do not revoke; give benefit of the doubt.
            }
        }

        /// <summary>
        /// Start a background timer that re-validates every 12 hours while VS is open.
        /// </summary>
        public static void StartPeriodicValidation(IServiceProvider serviceProvider)
        {
            var key = RenderDocOptions.Instance.LicenseKey;
            if (string.IsNullOrEmpty(key)) return;

            _validationTimer?.Dispose();
            _validationTimer = new Timer(async _ =>
            {
                try
                {
                    var instanceId = RenderDocOptions.Instance.LicenseInstanceId;
                    bool valid = await ValidateLicenseAsync(key, instanceId).ConfigureAwait(false);
                    if (!valid)
                    {
                        RenderDocOptions.Instance.SetPremiumUnlocked(false);
                        RenderDocOptions.Instance.Save(serviceProvider);
                    }
                }
                catch { }
            }, null, TimeSpan.FromHours(12), TimeSpan.FromHours(12));
        }

        // ── Private implementation ────────────────────────────────────────────────

        private static Timer _validationTimer;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static async Task<(bool Success, string Message)> ActivateAsync(string licenseKey)
        {
            var json = BuildJson(("license_key", licenseKey), ("name", BuildDeviceName()));
            var resp = await PostJsonAsync($"{DodoBase}/licenses/activate", json).ConfigureAwait(false);

            if ((int)resp.StatusCode == 422)
                return (false, "Activation limit reached. Please deactivate an existing machine first.");

            if (resp.StatusCode == HttpStatusCode.Forbidden)
                return (false, "This licence key is inactive or has been revoked.");

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (false, $"Activation failed (HTTP {(int)resp.StatusCode}). {ReadStringField(body, "message")}");
            }

            var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var id = ReadStringField(responseBody, "id");
            if (id == null)
                return (false, "Activation succeeded but the server returned an unexpected response.");

            RenderDocOptions.Instance.LicenseKey = licenseKey;
            RenderDocOptions.Instance.LicenseInstanceId = id;
            RenderDocOptions.Instance.SetPremiumUnlocked(true);

            return (true, "Premium activated — Thank you ❤️ for your purchase!");
        }

        private static async Task<(bool Success, string Message)> DeactivateAsync()
        {
            var key = RenderDocOptions.Instance.LicenseKey;
            var instanceId = RenderDocOptions.Instance.LicenseInstanceId;

            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(instanceId))
            {
                RenderDocOptions.Instance.SetPremiumUnlocked(false);
                RenderDocOptions.Instance.LicenseKey = null;
                RenderDocOptions.Instance.LicenseInstanceId = null;
                return (true, "Licence removed from this machine.");
            }

            var json = BuildJson(
                ("license_key", key),
                ("license_key_instance_id", instanceId));

            var resp = await PostJsonAsync($"{DodoBase}/licenses/deactivate", json).ConfigureAwait(false);

            RenderDocOptions.Instance.SetPremiumUnlocked(false);
            RenderDocOptions.Instance.LicenseKey = null;
            RenderDocOptions.Instance.LicenseInstanceId = null;

            if (!resp.IsSuccessStatusCode)
                return (true, "Licence removed locally. Note: server-side deactivation may have failed — contact support if you cannot reactivate.");

            return (true, "Licence deactivated. This machine's slot has been freed.");
        }

        private static async Task<bool> ValidateLicenseAsync(string licenseKey, string instanceId = null)
        {
            string json = string.IsNullOrEmpty(instanceId)
                ? BuildJson(("license_key", licenseKey))
                : BuildJson(("license_key", licenseKey), ("license_key_instance_id", instanceId));

            var resp = await PostJsonAsync($"{DodoBase}/licenses/validate", json).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ReadBoolField(body, "valid");
        }

        private static async Task<HttpResponseMessage> PostJsonAsync(string url, string json)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _http.PostAsync(url, content).ConfigureAwait(false);
        }

        private static string BuildDeviceName()
        {
            try
            {
                var name = $"{Environment.UserName ?? "User"}@{Environment.MachineName ?? "Unknown"}";
                return name.Length > 64 ? name.Substring(0, 64) : name;
            }
            catch { return "VS-Extension-Device"; }
        }

        // ── Zero-dependency JSON builder / parser ─────────────────────────────────

        private static string BuildJson(params (string Key, string Value)[] fields)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(JsonEscape(fields[i].Key)).Append("\":\"")
                  .Append(JsonEscape(fields[i].Value)).Append('"');
            }
            return sb.Append('}').ToString();
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string ReadStringField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var needle = $"\"{field}\":\"";
            int start = json.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) return null;
            start += needle.Length;
            var sb = new StringBuilder();
            while (start < json.Length)
            {
                char c = json[start++];
                if (c == '"') break;
                if (c == '\\' && start < json.Length)
                {
                    char esc = json[start++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u' when start + 3 < json.Length:
                            if (int.TryParse(json.Substring(start, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    null, out int code))
                                sb.Append((char)code);
                            start += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool ReadBoolField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var needle = $"\"{field}\":";
            int start = json.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) return false;
            start += needle.Length;
            while (start < json.Length && json[start] == ' ') start++;
            return start + 3 < json.Length
                && json[start] == 't' && json[start + 1] == 'r'
                && json[start + 2] == 'u' && json[start + 3] == 'e';
        }
    }
}