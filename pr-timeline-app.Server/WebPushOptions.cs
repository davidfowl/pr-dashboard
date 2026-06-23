using System.Security.Cryptography;
using System.Text;

// Configuration for VAPID-based Web Push. The feature is intentionally opt-in: when the
// section is missing or incomplete the server treats push as disabled and every push code
// path no-ops, so local development without keys keeps working.
sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public bool Enabled { get; init; }

    public string? PublicKey { get; init; }

    public string? PrivateKey { get; init; }

    // VAPID requires a contact (mailto: or https URL) so push services can reach the
    // application owner about a misbehaving sender.
    public string? Subject { get; init; }

    // Optional explicit key id/version. When omitted we derive a stable id from the public
    // key so the client can still detect a key rotation and re-subscribe.
    public string? KeyId { get; init; }

    // How often the detector scans the cached PRs for new review requests. ~5 min keeps push
    // latency low while leaning on the warmed public cache (≈hourly) for the underlying data.
    public int DetectionIntervalMinutes { get; init; } = 5;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);

    public string EffectiveKeyId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(KeyId))
            {
                return KeyId.Trim();
            }

            if (string.IsNullOrWhiteSpace(PublicKey))
            {
                return "none";
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(PublicKey));
            return Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }
    }
}
