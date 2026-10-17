# Local VedAstro calculation service

This service runs VedAstro calculations locally. It makes no hosted VedAstro,
geocoding, model or account calls while handling requests. The existing
`deploy/dev-gateway` remains available as a separate hosted forwarding service.

The current public checkout lacks the complete calculator definitions needed
for a direct build. This deployment uses the official stable source revision
`40763952742f76369a505d8db2e9e9fa67f75d78` (`40763952-2665-stable`).
`source-lock.json` pins all 167 library files by SHA-256. Only library files
are downloaded at build time; no engine source is copied into OpenClaw.
This older release is not claimed to match every feature of the hosted API.

The build targets .NET 10 and pins dependency lock files. The infrastructure
updates replace the legacy unbounded cache and BinaryFormatter disk persistence
with a bounded memory cache, and update caching, JSInterop and JSON dependencies.
Calculation and classical-rule source is unchanged.

## API contract

The default profile remains `lahiri365_25`. A separate development instance can
select `VEDASTRO_NATIVE_PROFILE=raman360` together with
`VEDASTRO_NATIVE_PROFILE_ENVIRONMENT=development`. Other profile names and Raman
without that development gate fail startup. This is an experimental profile;
existing consumers must be configured and validated for it before use.

The profile is fixed at process startup because the pinned library uses global
calculation settings and caches. Requests cannot switch profiles: an explicit
`Ayanamsa` must match the instance. Raman uses the library's 360-day dasha year,
while Lahiri uses 365.25 days. `/ready`, `CalculationSettings` and the numeric
`Input.Ayanamsa` echo identify the active settings. A separate instance is required
to compare the profiles; do not change global settings inside a request.

`GET /health` and `GET /ready` are public. Startup verifies calculator
availability and a real Moon calculation before either endpoint can succeed.
Calculation requests require `x-api-key: <VEDASTRO_SERVICE_TOKEN>`; the token
must contain at least 32 bytes and is supplied as a runtime secret.

`GET /api/ListAllCalls` lists the deliberately limited exposed operations.
`POST /api/Calculate/<operation>` accepts JSON, for example:

```json
{
  "time": {
    "StdTime": "10:30 14/03/1995 +05:30",
    "Location": {"Name": "Delhi", "Latitude": 28.6139, "Longitude": 77.209}
  },
  "Ayanamsa": "LAHIRI"
}
```

`NatalEvidence` returns nine planetary longitudes, signs, constellations and
the ascendant in one response. `AllPlanetData` requires `planetName` and returns
the exposed strength, aspect and D9 calculations. Node strength is unavailable.
`AllHouseData` requires `houseName`. `DasaAtTime` requires `birthTime` and
`checkTime` and accepts 1–8 levels, defaulting to two. `HoroscopePredictions`
accepts up to three engine `filterTags`; it returns raw classical descriptions.
These descriptions must undergo product interpretation and safety review before
being shown to a user. Period names alone do not establish event predictions.

`ReadingEvidence` is a single bounded call for `topic` (`marriage`, `career`
or `education`, `finance`) plus `time` and `checkTime`. It returns natal coordinates,
the whole-sign topic ruler, native D9 sign, current major/minor phase and six
Shadbala components with their checked sum. Native bhava-based strength retains
its own house convention; it does not replace whole-sign D1 placement rules.
Its optional `timingContext` adds current Jupiter/Saturn longitudes, signs and
houses counted from the natal Moon, plus the active PD2 rule's family,
relationship and study categories from the pinned engine table. The client
checks those categories against the source-locked table and checks transit
geometry independently. This is current-period context, not an event window.
No raw classical descriptions, predicted event dates or model output are included.
Transit obstruction is not evaluated: the pinned `IsGocharaObstructed` method
passes `gocharaHouse` into `PlanetsInGocharaHouse` after calculating `vedhanka`.
Its obstruction result has not been admitted into reviewed readings.

`DailyEvidence` accepts `time` and `checkTime` without topic/planet/house/filter
options. One local call returns natal coordinates, all nine current longitudes,
signs, constellations and houses counted from the natal Moon, native major/minor
periods, the same period categories and seven native `GocharaKakshas` rows.
Kaksha, Bhinna Ashtakavarga and Sarva Ashtakavarga scores retain their native
meaning; they are not probabilities or verified event forecasts. No raw
catastrophic descriptions, model output or obstruction verdict is returned.
Callers must independently validate geometry, input echoes, settings and scores
before composing a daily reading. An evaluation time before birth is rejected.

An explicit `"includeVedha": true` option on `DailyEvidence` adds
`gocharaVedha` and sets its top-level `obstructionEvaluated` to true. Omitting
the option, or passing false, preserves the existing payload. The optional
bundle uses the pinned library's `Vedhanka` table and seven-planet occupancy,
but queries the **obstruction house** rather than the transit's own house.
The upstream calculation source remains unchanged; this service correction is
identified by `occupancyCorrection: query_obstruction_house`. Sun/Saturn and
Moon/Mercury pair exceptions are preserved. Each row exposes the transit house,
selected obstruction point, actual blockers and exempt occupants. No natal
or daily event, outcome probability, or favorable/adverse classification is
inferred from this geometry. The optional bundle is not used in chat until its
client validation and interpretation are independently reviewed.

Table selection explicitly retains the pinned source's first-match behavior,
including overlapping Venus branches. This is a named source convention, not
a claim that all published tables agree: the website's
[modern chapter guide](https://vedastro.org/blog/Hindu-Predictive-Astrology-Chapter-34-Gocharaphala-or-Results-of-Transits.html)
contains different Jupiter entries. The source revision and table method are
returned with the evidence. The older `timingContext.obstructionEvaluated`
remains false because that separate period bundle has no obstruction result.
`includeVedha` on other operations, and non-boolean option values, are rejected.

Conventions are fixed: **Lahiri, true nodes, 365.25-day dasha year, VedAstro
bhava houses**. Bhava-house rule results must not be merged into a whole-sign
reading. Other ayanamsas are rejected. Inputs require an explicit UTC offset;
dates are limited to 1900–2100 and locations to latitudes strictly between
−66 and +66 degrees because this engine uses Placidus-based bhava calculations.
The caller remains responsible for historical timezone/DST and birth precision.

Unknown fields, duplicate JSON properties, ambiguous input times, malformed
dates, unsupported settings and invalid coordinates fail explicitly. Responses
are limited to 128 KiB and requests to 16 KiB. Calculations are serialized to
protect the engine's static settings. Response cache entries expire after
30 minutes; both response and internal calculation caches have size limits.
No birth data, cache or account records are persisted by this service.

## Development

From this directory, with Python 3 and the .NET 10 SDK:

```sh
python fetch_engine.py
dotnet restore LocalApi.csproj --locked-mode
dotnet build LocalApi.csproj -c Release --no-restore
node --test local_api.test.mjs
VEDASTRO_SERVICE_TOKEN=<runtime-secret> ASPNETCORE_URLS=http://127.0.0.1:8080 dotnet bin/Release/net10.0/VedAstro.LocalApi.dll
```

Coolify uses branch `dev`, repository-root build context,
`/deploy/local-api/Dockerfile`, port `8080` and health path `/ready`.
Use an isolated dev application for validation. Keep the working gateway and
current chat provider available until the new provider passes comparison tests.
No production migration is implied by deploying this service.
