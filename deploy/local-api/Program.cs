using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using VedAstro.LocalApi;

if (args.Contains("--healthcheck"))
{
    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await healthClient.GetAsync("http://127.0.0.1:8080/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

var token = Environment.GetEnvironmentVariable("VEDASTRO_SERVICE_TOKEN") ?? "";
if (Encoding.UTF8.GetByteCount(token) < 32) throw new InvalidOperationException("Service token must contain at least 32 bytes");
CalculationEngine.Initialize();
var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 16384; });
var app = builder.Build();
using var replies = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
using var engineLock = new SemaphoreSlim(1);

app.MapGet("/health", () => Results.Json(new { status = "healthy", mode = "local-calculator",
    source_revision = CalculationEngine.Revision, model_calls = 0 }));
app.MapGet("/ready", () => Results.Json(new { status = "ready", mode = "local-calculator",
    source_revision = CalculationEngine.Revision, ayanamsa = "LAHIRI", node = "true", dasha_year_days = 365.25 }));

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = context.Request.Headers["x-api-key"].ToString();
        if (supplied.Length > 512 || !CryptographicOperations.FixedTimeEquals(tokenHash,
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { Status = "Fail", Payload = "unauthorized" });
            return;
        }
    }
    await next(context);
});
app.MapGet("/api/ListAllCalls", () => Results.Json(new { Status = "Pass", Payload = CalculationEngine.Operations,
    ProviderRevision = CalculationEngine.Revision }));
app.MapPost("/api/Calculate/{operation}", async (string operation, HttpContext context) =>
{
    if (!CalculationEngine.Operations.Contains(operation)) return Results.Json(new { Status = "Fail", Payload = "unknown_calculator" }, statusCode: 404);
    if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json")
        return Results.Json(new { Status = "Fail", Payload = "json_required" }, statusCode: 415);
    try
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var raw = await reader.ReadToEndAsync(context.RequestAborted);
        if (Encoding.UTF8.GetByteCount(raw) > 16384) return Results.StatusCode(413);
        var input = CalculationEngine.Parse(raw);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + raw)));
        if (replies.TryGetValue(cacheKey, out string? cached))
        {
            context.Response.Headers["X-Calculation-Cache"] = "hit";
            return Results.Text(cached!, "application/json");
        }
        if (!await engineLock.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
        {
            context.Response.Headers.RetryAfter = "2";
            return Results.Json(new { Status = "Fail", Payload = "calculator_busy" }, statusCode: 503);
        }
        try
        {
            if (replies.TryGetValue(cacheKey, out cached))
            {
                context.Response.Headers["X-Calculation-Cache"] = "hit";
                return Results.Text(cached!, "application/json");
            }
            var json = CalculationEngine.Run(operation, input).ToString(Newtonsoft.Json.Formatting.None);
            if (Encoding.UTF8.GetByteCount(json) > 131072) throw new InvalidOperationException("response_too_large");
            replies.Set(cacheKey, json, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });
            context.Response.Headers["X-Calculation-Cache"] = "miss";
            return Results.Text(json, "application/json");
        }
        finally { engineLock.Release(); }
    }
    catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
    {
        return Results.Json(new { Status = "Fail", Payload = "invalid_calculation_input" }, statusCode: 400);
    }
    catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Results.StatusCode(413); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (Exception)
    {
        return Results.Json(new { Status = "Fail", Payload = "local_calculation_failed" }, statusCode: 503);
    }
});
await app.RunAsync();
return 0;
