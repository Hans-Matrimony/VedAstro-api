using Newtonsoft.Json.Linq;
using VedAstro.Library;

namespace VedAstro.LocalApi;

/// <summary>Native obstruction-table geometry with an explicit occupancy correction.</summary>
public static class ReviewedVedha
{
    public static JObject Calculate(Time birth, Time check)
    {
        var rows = new JObject();
        foreach (var planet in PlanetName.All7Planets)
        {
            var house = VedAstro.Library.Calculate.GocharaZodiacSignCountFromMoon(birth, check, planet);
            var point = VedAstro.Library.Calculate.Vedhanka(planet, house);
            if (house is < 1 or > 12 || point is < 0 or > 12)
                throw new InvalidOperationException("invalid_vedha_geometry");
            // The pinned source computes point but queries house. Query point.
            // Preserve its seven-planet scope and pair exceptions, without
            // mutating a library collection or its global calculation options.
            var occupants = point == 0 ? [] : VedAstro.Library.Calculate
                .PlanetsInGocharaHouse(birth, check, point).ToArray();
            var ignored = occupants.Where(other => Exempt(planet, other)).ToArray();
            var blockers = occupants.Where(other => other != planet && !Exempt(planet, other)).ToArray();
            rows[planet.ToString()] = new JObject
            {
                ["houseFromNatalMoon"] = house,
                ["obstructionHouseFromNatalMoon"] = point,
                ["obstructingPlanets"] = Names(blockers),
                ["exemptPlanetsAtPoint"] = Names(ignored),
                ["obstructed"] = blockers.Length > 0,
            };
        }
        return new JObject
        {
            ["schema"] = "reviewed-vedha-geometry-v1",
            ["sourceRevision"] = CalculationEngine.Revision,
            ["tableMethod"] = "Calculate.Vedhanka",
            ["tableSelection"] = "pinned_upstream_first_match",
            ["occupancyMethod"] = "Calculate.PlanetsInGocharaHouse",
            ["occupancyCorrection"] = "query_obstruction_house",
            ["planetScope"] = "seven_classical_planets",
            ["houseReference"] = "natal_moon_whole_sign",
            ["eventPredictionAvailable"] = false,
            ["planets"] = rows,
        };
    }

    private static JArray Names(IEnumerable<PlanetName> planets) => new(planets
        .Select(planet => planet.ToString()).OrderBy(name => name, StringComparer.Ordinal));

    private static bool Exempt(PlanetName planet, PlanetName other) =>
        ((planet == PlanetName.Sun || planet == PlanetName.Saturn) &&
         (other == PlanetName.Sun || other == PlanetName.Saturn)) ||
        ((planet == PlanetName.Moon || planet == PlanetName.Mercury) &&
         (other == PlanetName.Moon || other == PlanetName.Mercury));
}
