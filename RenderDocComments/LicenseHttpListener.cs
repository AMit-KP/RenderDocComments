using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RenderDocComments
{
    internal static class LicenseHttpListener
    {
        private const int Port = 54321;
        private static HttpListener _listener;
        private static CancellationTokenSource _cts;

        public static event Action<string> LicenseKeyReceived;

        public static event Action ListenerStopped;

        public static void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            ListenerStopped?.Invoke();
        }

        public static void Start()
        {
            if (_listener != null) return; // already running

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/auth/");
            _listener.Start();

            Task.Run(() => ListenLoop(_cts.Token));
        }

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