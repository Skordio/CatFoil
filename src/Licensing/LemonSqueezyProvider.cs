using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CatFoil.Licensing;

/// <summary>
/// Activates license keys against the Lemon Squeezy license API. Activation
/// happens once, online; after that the cached activation in settings.json is
/// trusted permanently, guarded by a machine-bound signature so that editing
/// settings.json by hand isn't enough to unlock — bypassing requires reading
/// this source and doing the work yourself (which, being public source, is
/// deliberately possible; the honor system starts at that line).
/// </summary>
public sealed class LemonSqueezyProvider : ILicenseProvider
{
    // TODO: replace with the real product URL once the Lemon Squeezy store exists.
    public const string BuyUrl = "https://catfoil.lemonsqueezy.com/buy/REPLACE-WITH-PRODUCT-ID";

    private const string ActivateEndpoint = "https://api.lemonsqueezy.com/v1/licenses/activate";

    // Not a secret in any real sense — this is public source. It exists so a
    // stored activation must be produced by this code (or by someone who read
    // this code), not by typing values into settings.json.
    private const string SignatureSalt = "CatFoil-activation-v1|9f4c2a71-6e8d-4b02-a3d5-1c7e9b0f5a38";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Settings _settings;

    public LemonSqueezyProvider(Settings settings)
    {
        _settings = settings;
    }

    public bool IsLicensed =>
        !string.IsNullOrWhiteSpace(_settings.LicenseKey) &&
        !string.IsNullOrWhiteSpace(_settings.LicenseInstanceId) &&
        _settings.LicenseSignature == ComputeSignature(_settings.LicenseKey!, _settings.LicenseInstanceId!);

    /// <summary>
    /// HMAC over the activation data plus a per-machine id, so a settings.json
    /// can't be handcrafted or copied from another (paid) machine.
    /// </summary>
    private static string ComputeSignature(string licenseKey, string instanceId)
    {
        string payload = $"{licenseKey}\n{instanceId}\n{MachineId()}";
        byte[] hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SignatureSalt), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static string MachineId()
    {
        try
        {
            // Stable per Windows install, readable without admin rights.
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0)
                return guid;
        }
        catch
        {
            // Fall through to the machine name.
        }
        return Environment.MachineName;
    }

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
                if (string.IsNullOrWhiteSpace(instanceId))
                    return new LicenseActivationResult(false, "The license server sent an unexpected response.");

                _settings.LicenseKey = licenseKey;
                _settings.LicenseInstanceId = instanceId;
                _settings.LicenseSignature = ComputeSignature(licenseKey, instanceId);
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
