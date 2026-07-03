using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CatFoil.Licensing;

/// <summary>
/// Activates license keys against the Lemon Squeezy license API. Activation
/// happens once, online; after that the cached activation in settings.json is
/// trusted permanently (honor system by design — the source is public anyway).
/// </summary>
public sealed class LemonSqueezyProvider : ILicenseProvider
{
    // TODO: replace with the real product URL once the Lemon Squeezy store exists.
    public const string BuyUrl = "https://catfoil.lemonsqueezy.com/buy/REPLACE-WITH-PRODUCT-ID";

    private const string ActivateEndpoint = "https://api.lemonsqueezy.com/v1/licenses/activate";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Settings _settings;

    public LemonSqueezyProvider(Settings settings)
    {
        _settings = settings;
    }

    public bool IsLicensed =>
        !string.IsNullOrWhiteSpace(_settings.LicenseKey) &&
        !string.IsNullOrWhiteSpace(_settings.LicenseInstanceId);

    public async Task<LicenseActivationResult> ActivateAsync(string licenseKey)
    {
        licenseKey = licenseKey.Trim();
        if (licenseKey.Length == 0)
            return new LicenseActivationResult(false, "Enter a license key first.");

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["license_key"] = licenseKey,
                ["instance_name"] = Environment.MachineName,
            });
            using var response = await Http.PostAsync(ActivateEndpoint, content);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (root.TryGetProperty("activated", out var activated) && activated.GetBoolean())
            {
                string? instanceId = root.GetProperty("instance").GetProperty("id").GetString();
                _settings.LicenseKey = licenseKey;
                _settings.LicenseInstanceId = instanceId;
                _settings.Save();
                return new LicenseActivationResult(true, "License activated — thank you!");
            }

            string message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()!
                : "The license key was not accepted.";
            return new LicenseActivationResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LicenseActivationResult(false,
                "Could not reach the license server. Check your internet connection and try again.");
        }
    }
}
