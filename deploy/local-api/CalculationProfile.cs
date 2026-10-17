using VedAstro.Library;

namespace VedAstro.LocalApi;

public sealed record CalculationProfile(string Id, string AyanamsaName, int AyanamsaCode, double DashaYearDays)
{
    public static readonly CalculationProfile Lahiri = new("lahiri365_25", "LAHIRI", (int)Ayanamsa.LAHIRI, 365.25);
    public static readonly CalculationProfile Raman = new("raman360", "RAMAN", (int)Ayanamsa.RAMAN, 360);

    public static CalculationProfile FromEnvironment()
    {
        var selected = Environment.GetEnvironmentVariable("VEDASTRO_NATIVE_PROFILE");
        if (string.IsNullOrEmpty(selected) || selected == Lahiri.Id) return Lahiri;
        if (selected == Raman.Id &&
            Environment.GetEnvironmentVariable("VEDASTRO_NATIVE_PROFILE_ENVIRONMENT") == "development")
            return Raman;
        throw new InvalidOperationException("Unsupported or non-development calculation profile");
    }
}
