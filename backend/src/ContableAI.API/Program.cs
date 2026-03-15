using ContableAI.API.Endpoints;
using ContableAI.API.Extensions;
using ContableAI.API.Middleware;
using ContableAI.Infrastructure.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// ── Configurar Serilog antes de que el host arranque ──────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System",    LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        path:            "logs/contableai-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{

var builder = WebApplication.CreateBuilder(args);

// Reemplaza el logging por defecto de .NET con Serilog
builder.Host.UseSerilog((ctx, services, config) => config
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .MinimumLevel.Override("Microsoft",                   Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System",                      Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        path:            "logs/contableai-.log",
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.AddOpenApi();
builder.Services.AddContableCors();
builder.Services.AddContableInfrastructure(builder.Configuration);
builder.Services.AddContableAuth(builder.Configuration);
// ── Global exception handler (RFC 7807) ──────────────────────────────────────
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var app = builder.Build();

await app.SeedDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options => 
    {
        options.Theme = ScalarTheme.DeepSpace;
    }).AllowAnonymous();
}
app.UseCors("AllowAngular");
app.UseExceptionHandler();
app.UseSerilogRequestLogging(opts =>
{
    opts.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

// ── Health check (para Docker / monitoreo) — sin auth ───────────────────────
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status  = report.Status.ToString(),
            checks  = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
            elapsed = report.TotalDuration.TotalMilliseconds,
        });
        await ctx.Response.WriteAsync(result);
    }
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapCompanyEndpoints();
app.MapRulesEndpoints();
app.MapTransactionEndpoints();
app.MapAfipEndpoints();
app.MapChartOfAccountsEndpoints();
app.MapJournalEntriesEndpoints();
app.MapAuditEndpoints();
app.MapPeriodEndpoints();

app.MapGet("/api/banks", (BankParserFactory factory) =>
    Results.Ok(factory.AvailableBanks.Select(b => new { code = b.Code, displayName = b.DisplayName })))
   .AllowAnonymous()
   .WithName("GetAvailableBanks");

// Banner de "listo para operar" — se imprime una sola vez cuando el servidor ya
// está escuchando y puede aceptar requests (IHostApplicationLifetime.ApplicationStarted).
app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = string.Join(" | ", app.Urls);
    app.Logger.LogInformation("╔══════════════════════════════════════════════╗");
    app.Logger.LogInformation("║   ContableAI API  ▶  LISTO PARA OPERAR      ║");
    app.Logger.LogInformation("║   {Urls,-44}║", urls);
    app.Logger.LogInformation("╚══════════════════════════════════════════════╝");
});

app.Run();

} // end try
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ContableAI se detuvo inesperadamente al iniciar.");
}
finally
{
    Log.CloseAndFlush();
}