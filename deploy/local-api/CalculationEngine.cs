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
    string[]? filterTags = null, bool? sortByWeight = null, int? levels = null, string? topic = null);

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
        "AllPlanetData", "AllHouseData", "NatalEvidence", "ReadingEvidence", "HoroscopePredictions", "DasaAtTime"];
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
        else if (operation == "ReadingEvidence") result = Reading(time, input);
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

    private static JObject Reading(Time time, CalculationInput input)
    {
        var topicHouse = input.topic switch { "marriage" => 7, "career" => 10, "education" => 5, "finance" => 2,
            _ => throw new ArgumentException("invalid_topic") };
        var check = input.checkTime?.ToTime() ?? throw new ArgumentException("check_time_required");
        if (check.GetStdDateTimeOffset() < time.GetStdDateTimeOffset()) throw new ArgumentException("check_before_birth");
        if (input.planetName is not null || input.houseName is not null || input.filterTags is not null ||
            input.levels is not null || input.sortByWeight is not null) throw new ArgumentException("unsupported_options");
        var natal = Natal(time);
        // The product retains whole-sign D1 readings. Derive the ruler explicitly
        // from signs; never label the engine's bhava-house rules as whole-sign.
        var asc = (int)Calculate.HouseZodiacSign(HouseName.House1, time).GetSignName() - 1;
        PlanetName[] lords = [PlanetName.Mars, PlanetName.Venus, PlanetName.Mercury, PlanetName.Moon,
            PlanetName.Sun, PlanetName.Mercury, PlanetName.Venus, PlanetName.Mars,
            PlanetName.Jupiter, PlanetName.Saturn, PlanetName.Saturn, PlanetName.Jupiter];
        var ruler = lords[(asc + topicHouse - 1) % 12];
        var components = new JObject();
        foreach (var method in PlanetMethods.Where(name => name.EndsWith("Bala", StringComparison.Ordinal)))
        {
            var strength = (double)Invoke(method, time, ruler);
            if (!double.IsFinite(strength)) throw new InvalidOperationException("invalid_strength_component");
            components[method] = strength;
        }
        // Upstream Pinda catches exceptions and returns zero. Cross-check against
        // all six components so that a failed computation cannot become success.
        var total = Calculate.PlanetShadbalaPinda(ruler, time).ToDouble();
        if (!double.IsFinite(total) || total <= 0 || Math.Abs(total - components.Properties().Sum(p => (double)p.Value)) > 0.011)
            throw new InvalidOperationException("invalid_strength_total");
        var periods = VimshottariDasa.CurrentDasa8Levels(time, check);
        return new JObject
        {
            ["schema"] = "vedastro-reading-evidence-v1", ["topic"] = input.topic,
            ["natal"] = natal, ["checkTime"] = check.ToJson(),
            ["topicHouse"] = topicHouse, ["topicRuler"] = ruler.ToString(),
            ["interpretationHouseSystem"] = "whole_sign",
            ["navamsaSign"] = Calculate.PlanetNavamsaSign(ruler, time).ToString(),
            ["period"] = new JObject { ["PD1"] = periods.PD1.ToString(), ["PD2"] = periods.PD2.ToString() },
            ["timingContext"] = TimingContext(time, check, periods.PD1, periods.PD2),
            ["futureTimingSamples"] = input.topic == "marriage" &&
                Environment.GetEnvironmentVariable("VEDASTRO_MARRIAGE_ESTIMATE_ENABLED") == "1"
                ? FutureTimingSamples(time, check) : null,
            ["strength"] = new JObject
            {
                ["planet"] = ruler.ToString(), ["totalVirupas"] = total, ["totalRupas"] = total / 60,
                ["componentsVirupas"] = components, ["nativeHouseSystem"] = "vedastro_bhava",
                ["meetsEngineStrengthTest"] = Calculate.IsPlanetStrongInShadbala(ruler, time),
            },
            ["eventTimingAvailable"] = false,
        };
    }

    private static JObject TimingContext(Time birth, Time check, PlanetName major, PlanetName minor)
    {
        // Expose the source table's categories, never its fatalistic prose. These
        // cyclic period rules are not a personal event-window calculation.
        var ruleName = major + minor.ToString() + "PD2";
        var rule = EventDataListStatic.Rows.Single(row => row.Name.ToString() == ruleName);
        return new JObject
        {
            ["schema"] = "vedastro-timing-context-v1", ["scope"] = "current_period_and_transits",
            ["periodRule"] = new JObject
            {
                ["id"] = ruleName,
                ["ratings"] = new JObject
                {
                    ["family"] = rule.SpecializedSummary.Family.Nature.ToString(),
                    ["relationship"] = rule.SpecializedSummary.Love.Nature.ToString(),
                    ["study"] = rule.SpecializedSummary.Studies.Nature.ToString(),
                },
            },
            ["transits"] = new JObject(new[] { PlanetName.Jupiter, PlanetName.Saturn }.Select(planet =>
                new JProperty(planet.ToString(), new JObject
                {
                    ["longitude"] = Calculate.PlanetNirayanaLongitude(planet, check).TotalDegrees,
                    ["sign"] = Calculate.PlanetZodiacSign(planet, check).GetSignName().ToString(),
                    ["houseFromNatalMoon"] = Calculate.GocharaZodiacSignCountFromMoon(birth, check, planet),
                }))),
            ["obstructionEvaluated"] = false, ["eventPredictionAvailable"] = false,
        };
    }

    private static JArray FutureTimingSamples(Time birth, Time check)
    {
        // Astronomical samples only. The product's explicitly experimental
        // interpretation is separate from this pinned upstream calculator.
        var start = check.GetStdDateTimeOffset().ToUniversalTime();
        if (start.Year > 2094) throw new ArgumentException("timing_horizon_out_of_range");
        var first = new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new JArray();
        for (var month = 0; month < 60; month++)
        {
            var instant = month == 0 ? start : first.AddMonths(month);
            var sample = new Time(instant, birth.GetGeoLocation());
            var phase = VimshottariDasa.CurrentDasa8Levels(birth, sample);
            rows.Add(new JObject
            {
                ["at"] = instant.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["major"] = phase.PD1.ToString(), ["minor"] = phase.PD2.ToString(),
                ["jupiter"] = Calculate.PlanetNirayanaLongitude(PlanetName.Jupiter, sample).TotalDegrees,
                ["saturn"] = Calculate.PlanetNirayanaLongitude(PlanetName.Saturn, sample).TotalDegrees,
            });
        }
        return rows;
    }

    public static JObject Settings() => new()
    {
        ["ayanamsa"] = "LAHIRI", ["node"] = "true", ["dasha_year_days"] = 365.25,
        ["house_system"] = "vedastro_bhava", ["engine"] = "VedAstro.Library",
    };
}
