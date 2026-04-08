using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RenderDocComments
{
    /// <summary>
    /// Manages the Premium license lifecycle for the Render Doc Comments extension,<br/>
    /// including checkout creation, activation, deactivation, and periodic validation<br/>
    /// against the Dodo Payments licensing platform.
    /// </summary>
    /// <remarks>
    /// <para>This static class provides the following functionality:</para>
    /// <list type="bullet">
    /// <item><description><b>Checkout:</b> Creates payment sessions via a Cloudflare Worker proxy to Dodo Payments (<see cref="OpenCheckoutPage"/>).</description></item>
    /// <item><description><b>Activation:</b> Registers the current machine with a license key (<see cref="Activate"/>).</description></item>
    /// <item><description><b>Deactivation:</b> Frees the activation slot for use on another machine (<see cref="Deactivate"/>).</description></item>
    /// <item><description><b>Validation:</b> Silently verifies license status on startup and every 12 hours (<see cref="RevalidateOnStartupAsync"/>, <see cref="StartPeriodicValidation"/>).</description></item>
    /// </list>
    /// <para>All network communication uses a zero-dependency approach — a shared <see cref="HttpClient"/> instance<br/>
    /// with custom JSON parsing (no <c>System.Text.Json</c> or <c>Newtonsoft.Json</c> dependency) to minimize<br/>
    /// the extension's footprint and compatibility issues.</para>
    /// </remarks>
    public static class LicenseManager
    {
        // ── Endpoints ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The base URL for the Dodo Payments live API.<br/>
        /// Used for license activation, deactivation, and validation requests.
        /// </summary>
        private const string DodoBase = "https://live.dodopayments.com";

        /// <summary>
        /// The Cloudflare Worker URL used as a proxy for checkout session creation.<br/>
        /// This worker handles payment gateway integration and returns a checkout URL.
        /// </summary>
        private const string WorkerUrl = "https://dodo-live-checkout.amit-ku-p-2806.workers.dev";

        /// <summary>
        /// The custom URI scheme used for the local HTTP listener callback.<br/>
        /// The listener runs on <c>http://127.0.0.1:54321/auth/</c> to receive<br/>
        /// payment status notifications from the checkout page.
        /// </summary>
        private const string UriScheme = "renderdoccomments";

        // ── Public surface ────────────────────────────────────────────────────────

        /// <summary>
        /// Gets a value indicating whether the Premium tier is currently unlocked.<br/>
        /// Shorthand for <see cref="RenderDocOptions.Instance.PremiumUnlocked"/>.
        /// </summary>
        public static bool PremiumUnlocked => RenderDocOptions.Instance.PremiumUnlocked;

        /// <summary>
        /// Calls the Cloudflare Worker to create a Dodo checkout session,<br/>
        /// then opens the returned checkout URL in the default browser.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item><description><c>Success</c> — <c>true</c> if the checkout page was opened successfully.</description></item>
        /// <item><description><c>Message</c> — A user-facing error message if the operation failed, or empty string on success.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>The method performs the following steps synchronously (using <see cref="Task.Run"/>):</para>
        /// <list type="number">
        /// <item><description>Sends a POST request to <see cref="WorkerUrl"/> with a JSON payload containing <c>quantity: 1</c>.</description></item>
        /// <item><description>Parses the response to extract the checkout URL (checking <c>url</c>, <c>checkout_url</c>, <c>payment_url</c> fields).</description></item>
        /// <item><description>Opens the URL in the default browser via <see cref="Process.Start"/> with <c>UseShellExecute = true</c>.</description></item>
        /// </list>
        /// <para>Exceptions are caught and returned as error tuples rather than thrown,<br/>
        /// ensuring the calling UI thread is never disrupted by network failures.</para>
        /// </remarks>
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

        /// <summary>
        /// Asynchronously creates a checkout session and opens the payment page in the browser.
        /// </summary>
        /// <returns>
        /// A task containing a tuple of <c>(Success, Message)</c> indicating whether<br/>
        /// the checkout page was successfully opened.
        /// </returns>
        /// <remarks>
        /// <para>The method sends a POST request with JSON payload <c>{"quantity":"1"}</c> to the<br/>
        /// Cloudflare Worker at <see cref="WorkerUrl"/>. The worker responds with a JSON object<br/>
        /// containing the checkout URL.</para>
        /// <para>Response field detection order: <c>url</c> → <c>checkout_url</c> → <c>payment_url</c>.</para>
        /// </remarks>
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
        /// Activates a licence key on this machine by registering it with the Dodo Payments API.
        /// </summary>
        /// <param name="licenseKey">
        /// The licence key provided by the user after purchase.
        /// </param>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item><description><c>Success</c> — <c>true</c> if the key was activated and Premium was unlocked.</description></item>
        /// <item><description><c>Message</c> — A user-facing status message (success or error description).</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>On successful activation, the method:</para>
        /// <list type="number">
        /// <item><description>Stores the license key and instance ID in <see cref="RenderDocOptions.Instance"/>.</description></item>
        /// <item><description>Sets <see cref="RenderDocOptions.Instance.PremiumUnlocked"/> to <c>true</c>.</description></item>
        /// </list>
        /// <para>Error handling for specific HTTP status codes:</para>
        /// <list type="bullet">
        /// <item><description><c>422 Unprocessable Entity</c> → "Activation limit reached" (user must deactivate another machine first).</description></item>
        /// <item><description><c>403 Forbidden</c> → "Licence key is inactive or has been revoked."</description></item>
        /// <item><description>Other errors → Generic HTTP error with server message if available.</description></item>
        /// </list>
        /// </remarks>
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
        /// Deactivates this machine's licence instance, freeing up the activation slot<br/>
        /// for use on another machine.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item><description><c>Success</c> — Always <c>true</c> (deactivation always succeeds locally).</description></item>
        /// <item><description><c>Message</c> — A user-facing status message indicating local and server-side status.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>The method clears all license-related data from <see cref="RenderDocOptions.Instance"/>:<br/>
        /// <see cref="RenderDocOptions.LicenseKey"/>, <see cref="RenderDocOptions.LicenseInstanceId"/>, and<br/>
        /// <see cref="RenderDocOptions.PremiumUnlocked"/>.</para>
        /// <para>If no key or instance ID is stored, the method still succeeds (idempotent),<br/>
        /// ensuring the user can always reset the local license state.</para>
        /// <para>If the server-side deactivation fails, the method returns a warning message<br/>
        /// advising the user to contact support if they cannot reactivate elsewhere.</para>
        /// </remarks>
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
        /// Silently re-validates the stored license key on Visual Studio startup.<br/>
        /// Revokes local Premium access if the key has been refunded, revoked, or<br/>
        /// deactivated server-side.
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to save settings if the license is invalidated.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous validation operation. The result is not returned<br/>
        /// to the caller — the method directly updates <see cref="RenderDocOptions.Instance"/> on failure.
        /// </returns>
        /// <remarks>
        /// <para>The method is called during <see cref="RenderDocCommentsPackage.InitializeAsync"/> and performs:</para>
        /// <list type="number">
        /// <item><description>Checks if a license key is stored — returns immediately if not.</description></item>
        /// <item><description>Calls <see cref="ValidateLicenseAsync"/> with the stored key and instance ID.</description></item>
        /// <item><description>If validation fails, sets <see cref="RenderDocOptions.PremiumUnlocked"/> to <c>false</c> and saves settings.</description></item>
        /// </list>
        /// <para>Network errors are silently caught (benefit of the doubt) — the user retains Premium<br/>
        /// access if the server is temporarily unreachable, but will be re-checked during<br/>
        /// the next periodic validation cycle.</para>
        /// </remarks>
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
        /// Starts a background timer that re-validates the stored license key every 12 hours<br/>
        /// while Visual Studio is open, ensuring revoked or refunded licenses are detected.
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to save settings if the license is invalidated.
        /// </param>
        /// <remarks>
        /// <para>The method creates a <see cref="Timer"/> with:</para>
        /// <list type="bullet">
        /// <item><description><b>Due time:</b> 12 hours before the first validation fires.</description></item>
        /// <item><description><b>Period:</b> Every 12 hours thereafter.</description></item>
        /// </list>
        /// <para>Each validation cycle:</para>
        /// <list type="number">
        /// <item><description>Calls <see cref="ValidateLicenseAsync"/> with the stored key and instance ID.</description></item>
        /// <item><description>If invalid, revokes Premium access and saves settings.</description></item>
        /// <item><description>Catches all exceptions silently to prevent background task crashes from affecting the IDE.</description></item>
        /// </list>
        /// <para>If no license key is stored, the method returns immediately without creating a timer.<br/>
        /// Any existing timer is disposed before creating a new one to prevent duplicate timers.</para>
        /// </remarks>
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

        /// <summary>
        /// The periodic validation timer. Disposed when a new timer is started or<br/>
        /// when the package is unloaded.
        /// </summary>
        private static Timer _validationTimer;

        /// <summary>
        /// Shared HTTP client instance for all license-related network requests.<br/>
        /// Configured with a 15-second timeout to prevent hanging requests from blocking the UI.
        /// </summary>
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        /// <summary>
        /// Asynchronously activates a license key by sending an activation request to<br/>
        /// the Dodo Payments API and storing the response on success.
        /// </summary>
        /// <param name="licenseKey">
        /// The license key to activate.
        /// </param>
        /// <returns>
        /// A task containing a tuple of <c>(Success, Message)</c> with the activation result.
        /// </returns>
        /// <remarks>
        /// <para>The activation request is sent to <c>{DodoBase}/licenses/activate</c> with JSON payload:</para>
        /// <list type="bullet">
        /// <item><description><c>license_key</c> — The user-provided license key.</description></item>
        /// <item><description><c>name</c> — A device identifier generated by <see cref="BuildDeviceName"/> (capped at 64 characters).</description></item>
        /// </list>
        /// <para>On success, the server returns the instance <c>id</c> which is stored alongside<br/>
        /// the license key for future deactivation and validation requests.</para>
        /// </remarks>
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

        /// <summary>
        /// Asynchronously deactivates the current machine's license instance,<br/>
        /// freeing the activation slot for use elsewhere.
        /// </summary>
        /// <returns>
        /// A task containing a tuple of <c>(Success, Message)</c> with the deactivation result.
        /// </returns>
        /// <remarks>
        /// <para>If no license key or instance ID is stored, the method immediately succeeds<br/>
        /// (idempotent — the machine is already in a "not activated" state).</para>
        /// <para>The deactivation request is sent to <c>{DodoBase}/licenses/deactivate</c> with JSON payload:</para>
        /// <list type="bullet">
        /// <item><description><c>license_key</c> — The stored license key.</description></item>
        /// <item><description><c>license_key_instance_id</c> — The stored instance ID for this machine.</description></item>
        /// </list>
        /// <para>Regardless of server response, the method always clears local license data<br/>
        /// and sets <see cref="RenderDocOptions.PremiumUnlocked"/> to <c>false</c>. This ensures<br/>
        /// the user's machine is always freed up even if the server is temporarily unavailable.</para>
        /// </remarks>
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

        /// <summary>
        /// Asynchronously validates a license key against the Dodo Payments API<br/>
        /// to determine if the license is still active and valid.
        /// </summary>
        /// <param name="licenseKey">
        /// The license key to validate.
        /// </param>
        /// <param name="instanceId">
        /// The optional instance ID for this machine. When provided, the server validates<br/>
        /// that this specific instance is still active (not just the key in general).
        /// </param>
        /// <returns>
        /// <c>true</c> if the license is valid and active; <c>false</c> if the key is invalid,<br/>
        /// revoked, refunded, or the instance has been deactivated.
        /// </returns>
        /// <remarks>
        /// <para>The validation request is sent to <c>{DodoBase}/licenses/validate</c> with JSON payload:</para>
        /// <list type="bullet">
        /// <item><description><c>license_key</c> — Always included.</description></item>
        /// <item><description><c>license_key_instance_id</c> — Included only when <paramref name="instanceId"/> is non-null.</description></item>
        /// </list>
        /// <para>The method extracts the <c>valid</c> boolean field from the JSON response.<br/>
        /// Any non-success HTTP status code results in returning <c>false</c>.</para>
        /// </remarks>
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

        /// <summary>
        /// Sends a POST request with JSON content to the specified URL using the shared HTTP client.
        /// </summary>
        /// <param name="url">
        /// The target URL for the POST request.
        /// </param>
        /// <param name="json">
        /// The JSON string to send as the request body.
        /// </param>
        /// <returns>
        /// A task containing the <see cref="HttpResponseMessage"/> from the server.
        /// </returns>
        private static async Task<HttpResponseMessage> PostJsonAsync(string url, string json)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _http.PostAsync(url, content).ConfigureAwait(false);
        }

        /// <summary>
        /// Generates a device identifier string for license activation, based on the<br/>
        /// current user name and machine name.
        /// </summary>
        /// <returns>
        /// A string in the format <c>"username@machinename"</c>, truncated to 64 characters<br/>
        /// if longer. Returns <c>"VS-Extension-Device"</c> if the environment variables are unavailable.
        /// </returns>
        /// <remarks>
        /// <para>The device name is sent to the Dodo Payments API during activation to identify<br/>
        /// which machine is consuming the license slot. This allows users to see and manage<br/>
        /// active activations across their devices.</para>
        /// <para>Exceptions during environment variable access are caught silently to prevent<br/>
        /// activation failures due to unusual system configurations.</para>
        /// </remarks>
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

        /// <summary>
        /// Builds a minimal JSON object string from the provided key-value pairs.<br/>
        /// This is a zero-dependency JSON serializer that avoids external library dependencies.
        /// </summary>
        /// <param name="fields">
        /// An array of (key, value) tuples representing the JSON object's properties.<br/>
        /// Both keys and values are treated as strings.
        /// </param>
        /// <returns>
        /// A JSON object string (e.g., <c>{"key1":"value1","key2":"value2"}</c>).
        /// </returns>
        /// <remarks>
        /// <para>Values are escaped via <see cref="JsonEscape"/> to handle special characters<br/>
        /// like quotes, backslashes, and control characters.</para>
        /// <para>This method is intentionally simple — it only supports string values and does<br/>
        /// not handle nested objects, arrays, numbers, or booleans. For the license API,<br/>
        /// all required fields are string-based, making this sufficient.</para>
        /// </remarks>
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

        /// <summary>
        /// Escapes a string value for safe inclusion in a JSON string literal.<br/>
        /// Handles quotes, backslashes, newlines, carriage returns, tabs, and control characters.
        /// </summary>
        /// <param name="value">
        /// The raw string value to escape.
        /// </param>
        /// <returns>
        /// The escaped string with special characters replaced by their JSON escape sequences.
        /// </returns>
        /// <remarks>
        /// <para>Escaped characters:</para>
        /// <list type="bullet">
        /// <item><description><c>"</c> → <c>\"</c></description></item>
        /// <item><description><c>\</c> → <c>\\</c></description></item>
        /// <item><description><c>\n</c> → <c>\\n</c></description></item>
        /// <item><description><c>\r</c> → <c>\\r</c></description></item>
        /// <item><description><c>\t</c> → <c>\\t</c></description></item>
        /// <item><description>Control characters (below 0x20) → <c>\\uXXXX</c> hex escape.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Extracts a string value for a given field name from a minimal JSON response.<br/>
        /// This is a zero-dependency JSON parser that searches for <c>"field":"value"</c> patterns.
        /// </summary>
        /// <param name="json">
        /// The raw JSON response string to search.
        /// </param>
        /// <param name="field">
        /// The field name to search for (without quotes or colon).
        /// </param>
        /// <returns>
        /// The extracted string value with JSON escape sequences decoded, or <c>null</c> if the field is not found.
        /// </returns>
        /// <remarks>
        /// <para>The method searches for the needle <c>"field":"</c> in the JSON string, then reads<br/>
        /// characters until the closing <c>"</c>, handling escape sequences:</para>
        /// <list type="bullet">
        /// <item><description><c>\"</c> → quote character.</description></item>
        /// <item><description><c>\\</c> → backslash.</description></item>
        /// <item><description><c>\/</c> → forward slash.</description></item>
        /// <item><description><c>\n</c>, <c>\r</c>, <c>\t</c> → newline, carriage return, tab.</description></item>
        /// <item><description><c>\uXXXX</c> → Unicode character from 4-digit hex code.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Extracts a boolean value for a given field name from a minimal JSON response.<br/>
        /// Searches for the pattern <c>"field":true</c> or <c>"field":false</c>.
        /// </summary>
        /// <param name="json">
        /// The raw JSON response string to search.
        /// </param>
        /// <param name="field">
        /// The field name to search for (without quotes or colon).
        /// </param>
        /// <returns>
        /// <c>true</c> if the field is found with value <c>true</c>; <c>false</c> otherwise<br/>
        /// (including when the field is <c>false</c> or not found at all).
        /// </returns>
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