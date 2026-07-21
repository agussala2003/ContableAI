using ContableAI.API.Common;
using ContableAI.Application.Common;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using Hangfire;
using Hangfire.PostgreSql;
using ContableAI.Infrastructure.Features.Afip;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Resilience;
using ContableAI.Infrastructure.Services;
using ContableAI.Infrastructure.Services.Classification;
using ContableAI.Infrastructure.BackgroundJobs;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

namespace ContableAI.API.Extensions;

public static class ServiceExtensions
{
    /// <summary>
    /// Registra CORS para el cliente Angular.
    /// El origen permitido se lee desde la configuración (Frontend:BaseUrl),
    /// lo que permite sobreescribirlo con variables de entorno en producción.
    /// </summary>
    public static IServiceCollection AddContableCors(
        this IServiceCollection services, IConfiguration configuration)
    {
        var frontendUrl = configuration["Frontend:BaseUrl"]
            ?? throw new InvalidOperationException(
                "La variable de configuración 'Frontend:BaseUrl' es obligatoria. " +
                "Agregá la variable de entorno 'Frontend__BaseUrl' en el servidor de producción.");

        services.AddCors(options =>
            options.AddPolicy("AllowAngular", policy =>
                policy.WithOrigins(frontendUrl)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      // A-3: necesario para que el navegador envíe/reciba la cookie HttpOnly del
                      // refresh token. Requiere un origen explícito (no comodín), que ya es el caso.
                      .AllowCredentials()));
        return services;
    }

    /// <summary>
    /// Registra la infraestructura: base de datos, parsers, AFIP y clasificación por reglas.
    /// </summary>
    public static IServiceCollection AddContableInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<FrontendOptions>(configuration.GetSection(FrontendOptions.SectionName));
        services.Configure<DataRetentionOptions>(configuration.GetSection(DataRetentionOptions.SectionName));

        // ── MediatR — scans Application + Infrastructure for commands/handlers ─
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblies(
                typeof(ValidationBehavior<,>).Assembly,           // ContableAI.Application
                typeof(ContableAIDbContext).Assembly);             // ContableAI.Infrastructure
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });

        // ── FluentValidation — register all validators from Application assembly ─
        services.AddValidatorsFromAssembly(typeof(ValidationBehavior<,>).Assembly);

        // ── Classification strategy pipeline (HardRule only) ──────────────────
        services.AddScoped<HardRuleStrategy>();
        services.AddScoped<IClassificationService, ClassificationService>();

        // ── Parsers de banco y AFIP ───────────────────────────────────────────
        services.AddSingleton<BankParserFactory>();
        services.AddSingleton<IBankParserService>(sp => sp.GetRequiredService<BankParserFactory>());
        services.AddScoped<IAfipParserService, PdfAfipParserService>();
        services.AddScoped<AfipCombinationService>();
        services.AddScoped<IExportService, ExcelExportService>();

        // ── Canonicalización de cuentas (evita duplicados por casing) ─────────
        services.AddScoped<IAccountNameResolver, AccountNameResolver>();


        // ── Autenticación y tenant ────────────────────────────────────────────
        services.AddHttpContextAccessor();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IQuotaService, QuotaService>();

        // ── Resiliencia (Polly) — pipelines reutilizables (email hoy, HTTP a futuro) ──
        services.AddContableResilience();

        // ── Email (SMTP) ──────────────────────────────────────────────────────
        services.AddTransient<IEmailService, SmtpEmailService>();

        // ── Base de datos con interceptor de auditoría ────────────────────────
        services.AddSingleton<AuditInterceptor>();
        services.AddDbContext<ContableAIDbContext>((sp, options) =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql =>
                {
                    // R-1: resiliencia de conexión ante micro-cortes / failovers del PostgreSQL
                    // gestionado (Render). EF reintenta los fallos transitorios de Npgsql con
                    // backoff en lugar de propagarlos como excepción al usuario.
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount:      5,
                        maxRetryDelay:      TimeSpan.FromSeconds(10),
                        errorCodesToAdd:    null);
                    // Corte por comando: evita que una consulta colgada bloquee el hilo indefinidamente.
                    npgsql.CommandTimeout(30);
                });
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });

        // ── Tareas en segundo plano (Background Services y Hangfire) ──────────
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            // O-1: propaga el Correlation ID del request que encola un job hasta su ejecución.
            .UseFilter(new HangfireCorrelationIdFilter())
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(configuration.GetConnectionString("DefaultConnection"))));

        services.AddHangfireServer();

        // R-5: el análisis proactivo se resuelve desde DI y corre como recurring job de Hangfire
        // (ver AddOrUpdate en Program.cs), no como BackgroundService por réplica.
        services.AddScoped<ProactiveLearningJob>();

        return services;
    }

    /// <summary>
    /// Registra JWT, autorización, rate limiting, Problem Details y health checks.
    /// </summary>
    public static IServiceCollection AddContableAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Missing Jwt configuration section.");

        if (string.IsNullOrWhiteSpace(jwtOptions.Key) ||
            string.IsNullOrWhiteSpace(jwtOptions.Issuer) ||
            string.IsNullOrWhiteSpace(jwtOptions.Audience))
        {
            throw new InvalidOperationException("Jwt configuration must include Key, Issuer and Audience.");
        }

        // ── RFC 7807 Problem Details ──────────────────────────────────────────
        services.AddProblemDetails();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey        = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
                    ValidateIssuer          = true,
                    ValidIssuer             = jwtOptions.Issuer,
                    ValidateAudience        = true,
                    ValidAudience           = jwtOptions.Audience,
                    ValidateLifetime        = true,
                    ClockSkew               = TimeSpan.Zero,
                };
            });

        services.AddAuthorization(opts =>
        {
            // Todo endpoint exige autenticación salvo que declare AllowAnonymous.
            opts.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // M-4: acciones de gestión/destructivas reservadas al dueño del estudio
            // (o al SystemAdmin, operador de plataforma). DataEntry queda excluido.
            opts.AddPolicy(AuthorizationPolicies.RequireStudioOwner, p =>
                p.RequireRole(UserRole.StudioOwner.ToString(), UserRole.SystemAdmin.ToString()));
        });

        // ── Rate Limiting ─────────────────────────────────────────────────────
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 10 req/min por IP — anti brute-force en login y registro
            options.AddPolicy("auth", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0,
                    }));

            // 20 req/min por usuario — evita abusar la API de AFIP/ARCA
            options.AddPolicy("afip", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                               ?? ctx.Connection.RemoteIpAddress?.ToString()
                               ?? "anon",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0,
                    }));
        });

        // ── Health Checks ─────────────────────────────────────────────────────
        // R-2/R-3: liveness vs readiness.
        //   • Liveness  → ¿el proceso responde? Sin dependencias externas (no tags).
        //   • Readiness → ¿puede servir tráfico útil? Depende de PostgreSQL (tag "ready").
        // El proveedor cloud saca la instancia del balanceador si /health/ready falla,
        // sin reiniciarla (eso lo decide /health/live), evitando reinicios en cascada
        // ante un micro-corte de la base.
        services.AddHealthChecks()
            .AddNpgSql(
                connectionStringFactory: sp =>
                    configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException(
                        "La cadena de conexión 'DefaultConnection' es obligatoria para el health check de readiness."),
                name:  "postgres",
                tags:  new[] { "ready" });

        return services;
    }
}
