using System;

namespace RenderDocComments
{
    /// <summary>
    /// Stub for the Premium licence / payment system.
    ///
    /// HOW TO IMPLEMENT FREEMIUM LATER
    /// ────────────────────────────────
    /// 1. Replace <see cref="ValidateLicenseKey"/> with a real call to your
    ///    licensing back-end (e.g. Paddle, LemonSqueezy, your own API).
    /// 2. Store the validated key / token in <see cref="RenderDocOptions"/> and
    ///    persist it via <see cref="RenderDocOptions.Save"/>.
    /// 3. Call <see cref="Activate"/> from the options window's "Activate" button.
    /// 4. Call <see cref="Deactivate"/> from the options window's "Deactivate" button.
    /// 5. Optionally add a periodic re-validation check in
    ///    <see cref="RenderDocCommentsPackage.InitializeAsync"/>.
    ///
    /// While this stub returns <c>false</c> for every key, all Premium UI is shown
    /// but disabled — making it trivial to flip to freemium without any other
    /// code changes.
    ///
    /// To make everything FREE for everyone (no gate), change
    /// <see cref="PremiumUnlocked"/> to always return <c>true</c> — one line.
    /// </summary>
    public static class LicenseManager
    {
        // ── Public surface ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when the user has a valid Premium licence.
        /// Currently always <c>false</c> — replace body with real validation.
        /// To remove the gate entirely (everything free) just return <c>true</c>.
        /// </summary>
        public static bool PremiumUnlocked => true;

        /// <summary>
        /// Try to activate with the supplied licence key.
        /// Returns (success, message) tuple.
        /// </summary>
        public static (bool Success, string Message) Activate(string licenseKey)
        {
            /* ── TODO: replace this block with real validation ────────────────────
             *
             *  var result = await MyLicensingApi.ValidateAsync(licenseKey);
             *  if (result.Valid)
             *  {
             *      RenderDocOptions.Instance.SetPremiumUnlocked(true);
             *      return (true, "Premium licence activated. Thank you!");
             *  }
             *  return (false, result.ErrorMessage);
             *
             * ──────────────────────────────────────────────────────────────────── */

            // Stub: always reject.
            return (false, "Licence validation not yet implemented.");
        }

        /// <summary>Deactivate the Premium licence on this machine.</summary>
        public static void Deactivate()
        {
            RenderDocOptions.Instance.SetPremiumUnlocked(false);
            /* TODO: optionally call the server to release the seat */
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Placeholder: validate a licence key against your back-end.
        /// Returns <c>true</c> when the key is genuine.
        /// </summary>
        private static bool ValidateLicenseKey(string key)
        {
            // TODO: HTTP call, HMAC check, or SDK call here.
            return false;
        }
    }
}
