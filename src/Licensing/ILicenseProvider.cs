using System.Threading.Tasks;

namespace CatFoil.Licensing;

/// <summary>
/// Abstraction over "does this user own a license" so the portable build
/// (Lemon Squeezy) and a future Microsoft Store build (StoreContext) can
/// share the rest of the app unchanged.
/// </summary>
public interface ILicenseProvider
{
    bool IsLicensed { get; }
    Task<LicenseActivationResult> ActivateAsync(string licenseKey);
}

public sealed record LicenseActivationResult(bool Success, string Message);
