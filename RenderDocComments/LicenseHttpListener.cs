using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RenderDocComments
{
    /// <summary>
    /// A local HTTP listener that runs on <c>http://127.0.0.1:54321/auth/</c> to receive<br/>
    /// payment status notifications from the Dodo Payments checkout page.
    /// </summary>
    /// <remarks>
    /// <para>The listener serves as a bridge between the browser-based checkout flow<br/>
    /// and the extension's activation process:</para>
    /// <list type="number">
    /// <item><description>The user clicks "Buy Premium" in the options dialog.</description></item>
    /// <item><description>The listener starts on port 54321 and waits for a callback.</description></item>
    /// <item><description>The checkout page redirects back to <c>http://127.0.0.1:54321/auth/?key=...&amp;status=...</c> upon payment completion.</description></item>
    /// <item><description>The listener extracts the license key and status, displays a success/failure HTML page, and raises <see cref="LicenseKeyReceived"/>.</description></item>
    /// </list>
    /// <para>The listener handles the following payment statuses:</para>
    /// <list type="bullet">
    /// <item><description><c>succeeded</c> — Payment completed; key is extracted and activation proceeds.</description></item>
    /// <item><description><c>failed</c> — Payment failed; error page shown.</description></item>
    /// <item><description><c>processing</c> — Payment is being processed; informational page shown, listener stops after 5 minutes.</description></item>
    /// <item><description><c>cancelled</c> — Payment cancelled by user; informational page shown.</description></item>
    /// <item><description>Other — Generic error page shown.</description></item>
    /// </list>
    /// </remarks>
    internal static class LicenseHttpListener
    {
        /// <summary>
        /// The TCP port on which the local HTTP listener accepts requests.
        /// </summary>
        /// <remarks>
        /// Port <c>54321</c> was chosen as an uncommon high port unlikely to conflict<br/>
        /// with other services. The listener binds to <c>127.0.0.1</c> (loopback only)<br/>
        /// for security — it is not accessible from the network.
        /// </remarks>
        private const int Port = 54321;

        /// <summary>
        /// The underlying HTTP listener instance.
        /// </summary>
        private static HttpListener _listener;

        /// <summary>
        /// Cancellation token source used to signal the listener loop to stop.
        /// </summary>
        private static CancellationTokenSource _cts;

        /// <summary>
        /// Event raised when a license key is successfully received from the checkout callback.<br/>
        /// Subscribers should handle the key and proceed with license activation.
        /// </summary>
        /// <remarks>
        /// <para>The event carries the license key string as its argument.<br/>
        /// This is raised only when the checkout status is <c>"succeeded"</c> and<br/>
        /// a non-empty key is present in the query string.</para>
        /// <para>After this event fires, the listener is automatically stopped via <see cref="Stop"/>.</para>
        /// </remarks>
        public static event Action<string> LicenseKeyReceived;

        /// <summary>
        /// Event raised when the listener stops, for any reason<br/>
        /// (successful key receipt, payment failure, or manual cancellation).
        /// </summary>
        /// <remarks>
        /// <para>Subscribers should use this event to reset UI state (e.g., re-enable the<br/>
        /// "Buy Premium" button and restore its text) in the <see cref="PurchaseActivationWindow"/>.</para>
        /// </remarks>
        public static event Action ListenerStopped;

        /// <summary>
        /// Stops the HTTP listener and cleans up all resources.<br/>
        /// Safe to call multiple times; subsequent calls are no-ops.
        /// </summary>
        /// <remarks>
        /// <para>The method performs the following cleanup steps:</para>
        /// <list type="number">
        /// <item><description>Cancels the <see cref="_cts"/> to signal the listener loop to exit.</description></item>
        /// <item><description>Calls <see cref="HttpListener.Stop"/> to close the listening port (wrapped in try-catch for safety).</description></item>
        /// <item><description>Sets <see cref="_listener"/> to <c>null</c> to allow a fresh start via <see cref="Start"/>.</description></item>
        /// <item><description>Raises the <see cref="ListenerStopped"/> event to notify subscribers.</description></item>
        /// </list>
        /// </remarks>
        public static void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            ListenerStopped?.Invoke();
        }

        /// <summary>
        /// Starts the HTTP listener on <c>http://127.0.0.1:54321/auth/</c> if it is not already running.
        /// </summary>
        /// <remarks>
        /// <para>If the listener is already running (<see cref="_listener"/> is not <c>null</c>),<br/>
        /// this method returns immediately without creating a duplicate listener.</para>
        /// <para>The listener runs its request processing loop on a background thread<br/>
        /// via <see cref="Task.Run"/> to avoid blocking the calling UI thread.</para>
        /// </remarks>
        public static void Start()
        {
            if (_listener != null) return; // already running

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/auth/");
            _listener.Start();

            Task.Run(() => ListenLoop(_cts.Token));
        }

        /// <summary>
        /// The main listener loop that processes incoming HTTP requests until cancelled.
        /// </summary>
        /// <param name="ct">
        /// The cancellation token used to signal loop termination.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous listener loop. The task completes<br/>
        /// when the cancellation token is triggered or the listener is stopped.
        /// </returns>
        /// <remarks>
        /// <para>For each incoming request, the method:</para>
        /// <list type="number">
        /// <item><description>Waits for a context via <see cref="HttpListener.GetContextAsync"/>.</description></item>
        /// <item><description>Extracts <c>key</c> and <c>status</c> from the query string.</description></item>
        /// <item><description>Determines success based on <c>status == "succeeded"</c> and non-empty key.</description></item>
        /// <item><description>Renders an HTML response page appropriate to the payment status.</description></item>
        /// <item><description>Writes the HTML response to the client's browser.</description></item>
        /// <item><description>For success: raises <see cref="LicenseKeyReceived"/> and stops the listener.</description></item>
        /// <item><description>For processing: schedules a 5-minute auto-stop timer.</description></item>
        /// <item><description>For all other statuses: stops the listener immediately.</description></item>
        /// </list>
        /// <para>The loop exits when the cancellation token is triggered (via <see cref="Stop"/>)<br/>
        /// or when <see cref="HttpListener.GetContextAsync"/> throws (indicating the listener has been stopped).</para>
        /// </remarks>
        private static async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch { break; }

                var key = ctx.Request.QueryString["key"] ?? string.Empty;
                var status = ctx.Request.QueryString["status"] ?? string.Empty;

                bool isSuccess = status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrEmpty(key);

                string html;
                if (isSuccess)
                {
                    html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'>"
                         + "<h2 style='color:#4caf50'>&#10004; Payment successful</h2>"
                         + "<p>Your licence key is being activated. You can close this tab.</p>"
                         + "</body></html>";
                }
                else if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                {
                    html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'>"
                         + "<h2 style='color:#f44336'>&#10008; Payment failed</h2>"
                         + "<p>Please try again or contact support after closing this tab</p>"
                         + "</body></html>";
                }
                else if (status.Equals("processing", StringComparison.OrdinalIgnoreCase))
                {
                    html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'>"
                         + "<h2 style='color:#2196f3'>&#8987; Payment processing</h2>"
                         + "<p>Your payment is being processed. Your licence key will arrive shortly via email.</p>"
                         + "<p>You can close this tab.</p>"
                         + "</body></html>";
                }
                else if (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'>"
                         + "<h2 style='color:#ff9800'>Payment cancelled</h2>"
                         + "<p>You can close this tab and try again.</p>"
                         + "</body></html>";
                }
                else
                {
                    html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'>"
                         + "<h2 style='color:#ff9800'>Something went wrong</h2>"
                         + "<p>No licence key was received. Please contact support.</p>"
                         + "<p>You can close this tab.</p>"
                         + "</body></html>";
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
                ctx.Response.OutputStream.Close();

                if (isSuccess)
                {
                    LicenseKeyReceived?.Invoke(key);
                    Stop();
                    break;
                }
                else if (status.Equals("processing", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Delay(TimeSpan.FromMinutes(5), ct).ContinueWith(_ =>
                    {
                        if (_listener != null) Stop();
                    });
                }
                else
                {
                    Stop();
                    break;
                }
            }
        }
    }
}