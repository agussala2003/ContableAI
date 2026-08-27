using ContableAI.API.Common;
using ContableAI.API.Endpoints;
using ContableAI.API.Extensions;
using ContableAI.API.Middleware;
using ContableAI.Infrastructure.BackgroundJobs;
using ContableAI.Infrastructure.Services;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// ── Configurar Serilog antes de que el host arranque ──────────────────────────
// O-2: solo sink de Console (stdout estructurado). En contenedores efímeros (Render)
// el sink de archivo se perdía en cada redeploy y no se agregaba entre réplicas; la
// plataforma ya captura stdout. Para agregación centralizada, sumar un sink de red
// (Seq/Elastic/Datadog) — nunca de archivo local.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System",    LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{

var builder = WebApplication.CreateBuilder(args);

// Permite sobreescribir cualquier config via variables de entorno (ej: Frontend__BaseUrl, Jwt__Key)
// Formato: sección__clave  →  doble guión bajo como separador de jerarquía
builder.Configuration.AddEnvironmentVariables();

// Lotes de PDFs escaneados (OCR) superan el límite por defecto de 30 MB de Kestrel.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 150 * 1024 * 1024);

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
    // O-2: solo Console (stdout). Ver nota en el bootstrap logger de arriba.
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.AddOpenApi();
builder.Services.AddContableCors(builder.Configuration);
builder.Services.AddContableInfrastructure(builder.Configuration);
builder.Services.AddContableAuth(builder.Configuration);

// ── Forwarded Headers (M-1) ───────────────────────────────────────────────────
// La API corre detrás del reverse proxy de Render, que termina TLS y agrega
// X-Forwarded-For / X-Forwarded-Proto. Sin esto, Connection.RemoteIpAddress sería
// la IP del proxy: el rate-limiter anti-fuerza-bruta particionaría a TODOS los
// clientes en un único bucket y los logs registrarían la IP equivocada.
// KnownIPNetworks/KnownProxies se limpian porque la IP del proxy de Render es dinámica.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
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

// M-1: debe ir PRIMERO en el pipeline para que la IP/esquema reales estén disponibles
// para el rate-limiter, el request logging de Serilog y el resto de middlewares.
app.UseForwardedHeaders();

// O-1: Correlation ID lo antes posible (tras conocer la IP/esquema reales) para que el
// ID esté disponible en el manejo global de excepciones y en el request logging de Serilog.
app.UseMiddleware<CorrelationIdMiddleware>();

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

// ── Health checks (para el proveedor cloud / Docker) — sin auth ─────────────
// R-2/R-3: dos endpoints con semántica distinta.
static async Task WriteHealthResponse(HttpContext ctx, HealthReport report)
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

// Liveness: ¿el proceso está vivo? No corre ningún check con dependencias externas
// (Predicate = _ => false), así un micro-corte de la base NUNCA reinicia el contenedor.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate      = _ => false,
    ResponseWriter = WriteHealthResponse,
}).AllowAnonymous();

// Readiness: ¿puede servir tráfico útil? Corre solo los checks etiquetados "ready"
// (PostgreSQL). Si falla, el balanceador deja de rutear a esta instancia.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate      = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse,
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapCompanyEndpoints();
app.MapRulesEndpoints();
app.MapTransactionEndpoints();
app.MapAfipEndpoints();
app.MapChartOfAccountsEndpoints();
app.MapBankAccountsEndpoints();
app.MapJournalEntriesEndpoints();
app.MapAuditEndpoints();
app.MapPeriodEndpoints();
app.MapDashboardEndpoints();
app.MapJobsEndpoints();

// A-2: dashboard de Hangfire protegido — requiere JWT válido con rol SystemAdmin.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthorizationFilter() }
});

// R-5: registra/actualiza el análisis proactivo como recurring job diario. Idempotente por
// RecurringJobId, así que reejecutar el arranque en cualquier réplica solo reconcilia la
// definición; Hangfire garantiza corrida única vía lock distribuido sobre PostgreSQL.
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<ProactiveLearningJob>(
        ProactiveLearningJob.RecurringJobId,
        job => job.AnalyzeTransactionsAsync(CancellationToken.None),
        Cron.Daily);

    // P-4/P-5: retención de datos — purga diaria de UploadJobResults vencidos, staged files
    // huérfanos y empresas soft-deleted con ventana de 90 días cumplida (P-2).
    recurringJobs.AddOrUpdate<DataRetentionJob>(
        DataRetentionJob.RecurringJobId,
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily);
}

app.MapGet("/api/banks", (BankParserFactory factory) =>
    Results.Ok(factory.AvailableBanks.Select(b => new { code = b.Code, displayName = b.DisplayName })))
   .AllowAnonymous()
   .WithName("GetAvailableBanks");

// Catálogo de bancos asignables a una cuenta bancaria. NO es el mismo conjunto que /api/banks:
// ese lista los formatos que el factory sabe leer (incluye el pseudo-banco "PDF" y bancos con
// parser CSV pero sin soporte de extracto PDF). Este es el que valida BankCodes.IsSupported, o
// sea el único que el alta de cuentas acepta y por el que se puede filtrar.
// El formulario de cuentas lo tenía hardcodeado y se le había quedado Santander afuera: con la
// lista servida desde el catálogo, agregar un banco es un solo cambio en el dominio.
app.MapGet("/api/bank-codes", () =>
    Results.Ok(ContableAI.Domain.Constants.BankCodes.All
        .Select(code => new { code, displayName = ContableAI.Domain.Constants.BankCodes.DisplayName(code) })))
   .RequireAuthorization()
   .WithName("GetAssignableBankCodes")
   .WithTags("Cuentas bancarias")
   .WithSummary("Bancos que se pueden asignar a una cuenta bancaria y usar como filtro.");

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