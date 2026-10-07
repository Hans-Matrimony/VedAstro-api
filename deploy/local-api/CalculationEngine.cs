using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;
using VedAstro.Library;

namespace VedAstro.LocalApi;

public sealed record LocationInput(string Name, double Latitude, double Longitude);
public sealed record TimeInput(string StdTime, LocationInput Location)
{
    public Time ToTime()
    {
        if (Location is null || string.IsNullOrWhiteSpace(Location.Name) || Location.Name.Length > 128 ||
            Location.Name.Any(char.IsControl) || !double.IsFinite(Location.Latitude) ||
            !double.IsFinite(Location.Longitude) || Math.Abs(Location.Latitude) >= 66 ||
            Math.Abs(Location.Longitude) > 180 ||
            !DateTimeOffset.TryParseExact(StdTime, "HH:mm dd/MM/yyyy zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date.Year < 1900 || date.Year > 2100)
            throw new ArgumentException("invalid_time_or_location");
        // Avoid the upstream string constructor's silent fallback to an empty time.
        return new Time(date, new GeoLocation(Location.Name, Location.Longitude, Location.Latitude));
    }
}

public sealed record CalculationInput(
    TimeInput? time = null, TimeInput? birthTime = null, TimeInput? checkTime = null,
    string? planetName = null, string? houseName = null,
    [property: JsonPropertyName("Ayanamsa")] string? Ayanamsa = null,
    string[]? filterTags = null, bool? sortByWeight = null, int? levels = null);

public static class CalculationEngine
{
    public const string Revision = "40763952742f76369a505d8db2e9e9fa67f75d78";
    public static readonly string[] PlanetMethods = [
        "PlanetNirayanaLongitude", "PlanetZodiacSign", "PlanetConstellation", "PlanetNavamsaSign",
        "PlanetShadbalaPinda", "HousesInAspect", "PlanetSthanaBala",
        "PlanetDigBala", "PlanetKalaBala", "PlanetChestaBala", "PlanetNaisargikaBala", "PlanetDrikBala",
    ];
    public static readonly string[] HouseMethods = ["HouseZodiacSign", "LordOfHouse", "PlanetsInHouseBasedOnSign"];
    public static readonly string[] Operations = [.. PlanetMethods, .. HouseMethods,
        "AllPlanetData", "AllHouseData", "NatalEvidence", "HoroscopePredictions", "DasaAtTime"];
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    };

    public static void Initialize()
    {
        // Fixed conventions for this instance; no per-request global-setting races.
        Calculate.Ayanamsa = (int)VedAstro.Library.Ayanamsa.LAHIRI;
        Calculate.SolarYearTimeSpan = 365.25;
        Calculate.UseMeanRahuKetu = false;
        foreach (var name in PlanetMethods.Concat(HouseMethods)) Resolve(name);
        var sample = new Time(new DateTimeOffset(2001, 1, 3, 5, 0, 0, TimeSpan.FromHours(5.5)),
            new GeoLocation("Delhi", 77.209, 28.6139));
        var longitude = Calculate.PlanetNirayanaLongitude(PlanetName.Moon, sample).TotalDegrees;
        if (!double.IsFinite(longitude) || longitude < 0 || longitude >= 360)
            throw new InvalidOperationException("startup_calculation_failed");
    }

    public static CalculationInput Parse(string raw)
    {
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 12 });
        CheckUniqueProperties(document.RootElement);
        var input = System.Text.Json.JsonSerializer.Deserialize<CalculationInput>(raw, InputOptions)
            ?? throw new ArgumentException("invalid_request");
        if (input.Ayanamsa is not null && input.Ayanamsa != "LAHIRI")
            throw new ArgumentException("unsupported_ayanamsa");
        if (input.time is not null && input.birthTime is not null)
            throw new ArgumentException("ambiguous_time_input");
        if (input.levels is < 1 or > 8 || input.sortByWeight == true || input.filterTags?.Length > 3)
            throw new ArgumentException("unsupported_options");
        return input;
    }

    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("duplicate_property");
                CheckUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }

    private static MethodInfo Resolve(string name)
    {
        var methods = typeof(Calculate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name && m.GetParameters().Count(p => !p.IsOptional) == 2 &&
                m.GetParameters().Any(p => p.ParameterType == typeof(Time))).ToArray();
        if (methods.Length != 1) throw new InvalidOperationException("engine_capability_missing:" + name);
        return methods[0];
    }

    private static JToken Invoke(string name, Time time, object subject)
    {
        var method = Resolve(name);
        return Encode(method.Invoke(null, method.GetParameters()
            .Select(p => p.ParameterType == typeof(Time) ? (object)time :
                p.ParameterType.IsInstanceOfType(subject) ? subject : p.DefaultValue).ToArray()));
    }

    private static JToken Encode(object? value)
    {
        if (value is null) return JValue.CreateNull();
        if (value is JToken token) return token;
        if (value is IToJson json) return json.ToJson();
        if (value is Constellation star) return new JObject
        {
            ["Name"] = star.GetConstellationName().ToString(), ["Quarter"] = star.GetQuarter(),
            ["DegreesInConstellation"] = star.GetDegreesInConstellation().TotalDegrees,
        };
        if (value is Enum || value is PlanetName) return value.ToString()!;
        if (value is IEnumerable list && value is not string) return new JArray(list.Cast<object>().Select(Encode));
        if (value is Shashtiamsa strength) return strength.ToDouble();
        return JToken.FromObject(value);
    }

    public static JObject Run(string operation, CalculationInput input)
    {
        if (!Operations.Contains(operation)) throw new ArgumentException("unknown_calculator");
        var value = input.time ?? input.birthTime ?? throw new ArgumentException("time_required");
        var time = value.ToTime();
        JToken result;
        var directPayload = false;
        if (operation == "NatalEvidence") result = Natal(time);
        else if (operation == "HoroscopePredictions")
        {
            var tags = input.filterTags?.Select(tag => Enum.TryParse<EventTag>(tag, false, out var parsed) &&
                Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("invalid_tag")).ToArray() ?? [];
            var rows = HoroscopeDataListStatic.Rows.Where(row => tags.Length == 0 || tags.Any(row.EventTags.Contains));
            // Do not use the upstream helper that converts a calculator exception to an empty success.
            var predictions = rows.Where(row => row.IsEventOccuring(time)).Select(row =>
                new HoroscopePrediction(row.Name, row.Description, row.RelatedBody).ToJson()).ToArray();
            result = new JArray(predictions);
            directPayload = true;
        }
        else if (operation == "DasaAtTime")
        {
            var current = input.checkTime?.ToTime() ?? throw new ArgumentException("check_time_required");
            if (current.GetStdDateTimeOffset() < time.GetStdDateTimeOffset()) throw new ArgumentException("check_before_birth");
            var phase = VimshottariDasa.CurrentDasa8Levels(time, current);
            var periods = JObject.FromObject(phase);
            result = new JObject(periods.Properties().Take(input.levels ?? 2));
        }
        else if (operation == "AllPlanetData" || PlanetMethods.Contains(operation))
        {
            if (!PlanetName.TryParse(input.planetName ?? "", out var planet) || !PlanetName.All9Planets.Contains(planet))
                throw new ArgumentException("invalid_planet");
            if ((planet == PlanetName.Rahu || planet == PlanetName.Ketu) && operation.Contains("bala", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("unsupported_node_strength");
            result = operation == "AllPlanetData" ? Planet(time, planet) : Invoke(operation, time, planet);
        }
        else
        {
            if (!Enum.TryParse<HouseName>(input.houseName, false, out var house) || !Enum.IsDefined(house) ||
                (int)house < 1 || (int)house > 12) throw new ArgumentException("invalid_house");
            result = operation == "AllHouseData" ? new JObject(HouseMethods.Select(name =>
                new JProperty(name, Invoke(name, time, house)))) : Invoke(operation, time, house);
        }
        return new JObject
        {
            ["Status"] = "Pass", ["Input"] = new JObject { ["Ayanamsa"] = 1 },
            ["Payload"] = directPayload ? result : new JObject { [operation] = result },
            ["CalculationSettings"] = Settings(), ["ProviderRevision"] = Revision, ["ModelCalls"] = 0,
        };
    }

    private static JObject Planet(Time time, PlanetName planet) => new(PlanetMethods.Select(name =>
        new JProperty(name, planet == PlanetName.Rahu || planet == PlanetName.Ketu
            ? name.Contains("bala", StringComparison.OrdinalIgnoreCase) ? JValue.CreateNull() : Invoke(name, time, planet)
            : Invoke(name, time, planet))));

    private static JObject Natal(Time time) => new()
    {
        ["schema"] = "vedastro-natal-evidence-v1", ["birthTime"] = time.ToJson(),
        ["ascendant"] = Calculate.HouseZodiacSign(HouseName.House1, time).ToJson(),
        ["planets"] = new JObject(PlanetName.All9Planets.Select(planet =>
            new JProperty(planet.ToString(), new JObject
            {
                ["longitude"] = Calculate.PlanetNirayanaLongitude(planet, time).TotalDegrees,
                ["sign"] = Calculate.PlanetZodiacSign(planet, time).GetSignName().ToString(),
                ["nakshatra"] = Encode(Calculate.PlanetConstellation(planet, time)),
            }))),
    };

    public static JObject Settings() => new()
    {
        ["ayanamsa"] = "LAHIRI", ["node"] = "true", ["dasha_year_days"] = 365.25,
        ["house_system"] = "vedastro_bhava", ["engine"] = "VedAstro.Library",
    };
}
