using ContableAI.Application.Common;
using ContableAI.Application.Features.Admin.Commands;
using ContableAI.Application.Features.Admin.Queries;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Options;
using ContableAI.Infrastructure.Persistence;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContableAI.Infrastructure.Features.Admin;

public sealed class GetAdminUsersHandler : IRequestHandler<GetAdminUsersQuery, Result<List<AdminUserRowResponse>>>
{
    private readonly ContableAIDbContext _db;

    public GetAdminUsersHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<List<AdminUserRowResponse>>> Handle(GetAdminUsersQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var users = await _db.Users
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);

        var tenantIds = users.Select(u => u.StudioTenantId).Distinct().ToList();

        var companiesByTenant = await _db.Companies
            .AsNoTracking()
            .Where(c => tenantIds.Contains(c.StudioTenantId) && c.IsActive)
            .GroupBy(c => c.StudioTenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);

        var companyIdsByTenant = await _db.Companies
            .AsNoTracking()
            .Where(c => tenantIds.Contains(c.StudioTenantId) && c.IsActive)
            .Select(c => new { c.StudioTenantId, c.Id })
            .ToListAsync(ct);

        var allCompanyIds = companyIdsByTenant.Select(x => x.Id).ToList();

        var monthlyTxByCompany = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.CompanyId != null
                        && allCompanyIds.Contains(t.CompanyId.Value)
                        && t.Date.Year == now.Year
                        && t.Date.Month == now.Month)
            .GroupBy(t => t.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId!.Value, x => x.Count, ct);

        var monthlyTxByTenant = companyIdsByTenant
            .GroupBy(x => x.StudioTenantId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => monthlyTxByCompany.GetValueOrDefault(x.Id, 0))
            );

        var rows = users.Select(u =>
        {
            var limits = QuotaLimits.ForPlan(u.Plan);
            return new AdminUserRowResponse(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role.ToString(),
                (int)u.AccountStatus,
                u.StudioTenantId,
                u.CreatedAt,
                u.Plan.ToString(),
                companiesByTenant.GetValueOrDefault(u.StudioTenantId, 0),
                limits.MaxCompanies,
                monthlyTxByTenant.GetValueOrDefault(u.StudioTenantId, 0),
                limits.MaxMonthlyTransactions
            );
        }).ToList();

        return Result<List<AdminUserRowResponse>>.Success(rows);
    }
}

public sealed class ActivateUserHandler : IRequestHandler<ActivateUserCommand, Result<AdminMessageResponse>>
{
    private readonly ContableAIDbContext _db;

    public ActivateUserHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminMessageResponse>> Handle(ActivateUserCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminMessageResponse>.NotFound();

        user.AccountStatus = AccountStatus.Active;
        user.IsActive = true;
        await _db.SaveChangesAsync(ct);

        return Result<AdminMessageResponse>.Success(new AdminMessageResponse($"Usuario {user.Email} activado correctamente."));
    }
}

public sealed class SuspendUserHandler : IRequestHandler<SuspendUserCommand, Result<AdminMessageResponse>>
{
    private readonly ContableAIDbContext _db;

    public SuspendUserHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminMessageResponse>> Handle(SuspendUserCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminMessageResponse>.NotFound();

        user.AccountStatus = AccountStatus.Suspended;
        user.IsActive = false;
        await _db.SaveChangesAsync(ct);

        return Result<AdminMessageResponse>.Success(new AdminMessageResponse($"Usuario {user.Email} suspendido."));
    }
}

public sealed class UpdateUserPlanHandler : IRequestHandler<UpdateUserPlanCommand, Result<AdminUserPlanResponse>>
{
    private readonly ContableAIDbContext _db;

    public UpdateUserPlanHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminUserPlanResponse>> Handle(UpdateUserPlanCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminUserPlanResponse>.NotFound();

        if (!Enum.TryParse<StudioPlan>(request.Plan, ignoreCase: true, out var plan))
            return Result<AdminUserPlanResponse>.Failure($"Plan inválido. Opciones: {string.Join(", ", Enum.GetNames<StudioPlan>())}", 400);

        user.Plan = plan;
        await _db.SaveChangesAsync(ct);

        return Result<AdminUserPlanResponse>.Success(new AdminUserPlanResponse(user.Id, user.Email, plan.ToString()));
    }
}

public sealed class UpdateUserRoleHandler : IRequestHandler<UpdateUserRoleCommand, Result<AdminUserRoleResponse>>
{
    private readonly ContableAIDbContext _db;

    public UpdateUserRoleHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminUserRoleResponse>> Handle(UpdateUserRoleCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminUserRoleResponse>.NotFound();

        if (user.Role == UserRole.SystemAdmin)
            return Result<AdminUserRoleResponse>.Failure("No se puede cambiar el rol de un SystemAdmin.", 400);

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            return Result<AdminUserRoleResponse>.Failure($"Rol inválido. Opciones: {string.Join(", ", Enum.GetNames<UserRole>())}", 400);

        if (role == UserRole.SystemAdmin)
            return Result<AdminUserRoleResponse>.Failure("No se puede asignar el rol SystemAdmin desde este endpoint.", 400);

        user.Role = role;
        await _db.SaveChangesAsync(ct);

        return Result<AdminUserRoleResponse>.Success(new AdminUserRoleResponse(user.Id, user.Email, role.ToString()));
    }
}

public sealed class UpdateUserDisplayNameHandler : IRequestHandler<UpdateUserDisplayNameCommand, Result<AdminUserDisplayNameResponse>>
{
    private readonly ContableAIDbContext _db;

    public UpdateUserDisplayNameHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminUserDisplayNameResponse>> Handle(UpdateUserDisplayNameCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Result<AdminUserDisplayNameResponse>.Failure("El nombre no puede estar vacío.", 400);

        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminUserDisplayNameResponse>.NotFound();

        user.DisplayName = request.DisplayName.Trim();
        await _db.SaveChangesAsync(ct);

        return Result<AdminUserDisplayNameResponse>.Success(new AdminUserDisplayNameResponse(user.Id, user.Email, user.DisplayName));
    }
}

public sealed class DeleteUserHandler : IRequestHandler<DeleteUserCommand, Result<AdminMessageResponse>>
{
    private readonly ContableAIDbContext _db;

    public DeleteUserHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminMessageResponse>> Handle(DeleteUserCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminMessageResponse>.NotFound();

        if (user.Role == UserRole.SystemAdmin)
            return Result<AdminMessageResponse>.Failure("No se puede eliminar un SystemAdmin.", 400);

        var studioMates = await _db.Users.CountAsync(u => u.StudioTenantId == user.StudioTenantId && u.Id != request.Id, ct);

        if (studioMates == 0)
        {
            var companyIds = await _db.Companies
                .Where(c => c.StudioTenantId == user.StudioTenantId)
                .Select(c => c.Id)
                .ToListAsync(ct);

            var jeIds = await _db.JournalEntries
                .Where(je => je.CompanyId != null && companyIds.Contains(je.CompanyId.Value))
                .Select(je => je.Id)
                .ToListAsync(ct);

            await _db.JournalEntryLines
                .Where(l => jeIds.Contains(l.JournalEntryId))
                .ExecuteDeleteAsync(ct);
            await _db.JournalEntries
                .Where(je => je.CompanyId != null && companyIds.Contains(je.CompanyId.Value))
                .ExecuteDeleteAsync(ct);
            await _db.BankTransactions
                .Where(t => t.CompanyId != null && companyIds.Contains(t.CompanyId.Value))
                .ExecuteDeleteAsync(ct);
            await _db.AccountingRules
                .Where(r => r.CompanyId != null && companyIds.Contains(r.CompanyId.Value))
                .ExecuteDeleteAsync(ct);
            await _db.ClosedPeriods
                .Where(p => p.StudioTenantId == user.StudioTenantId)
                .ExecuteDeleteAsync(ct);
            await _db.Companies
                .Where(c => c.StudioTenantId == user.StudioTenantId)
                .ExecuteDeleteAsync(ct);

            if (Guid.TryParse(user.StudioTenantId, out var tenantGuid))
            {
                await _db.ChartOfAccounts
                    .Where(c => c.StudioTenantId == tenantGuid)
                    .ExecuteDeleteAsync(ct);
            }
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        return Result<AdminMessageResponse>.Success(new AdminMessageResponse($"Usuario {user.Email} eliminado."));
    }
}

public sealed class GetAdminStatsHandler : IRequestHandler<GetAdminStatsQuery, Result<AdminStatsResponse>>
{
    private readonly ContableAIDbContext _db;

    public GetAdminStatsHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminStatsResponse>> Handle(GetAdminStatsQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var totalUsers = await _db.Users.AsNoTracking().CountAsync(ct);
        var activeUsers = await _db.Users.AsNoTracking().CountAsync(u => u.AccountStatus == AccountStatus.Active, ct);
        var pendingUsers = await _db.Users.AsNoTracking().CountAsync(u => u.AccountStatus == AccountStatus.Pending, ct);
        var suspendedUsers = await _db.Users.AsNoTracking().CountAsync(u => u.AccountStatus == AccountStatus.Suspended, ct);
        var totalCompanies = await _db.Companies.AsNoTracking().CountAsync(c => c.IsActive, ct);
        var totalTx = await _db.BankTransactions.AsNoTracking().CountAsync(ct);
        var monthlyTx = await _db.BankTransactions.AsNoTracking().CountAsync(t => t.Date.Year == now.Year && t.Date.Month == now.Month, ct);
        var totalJe = await _db.JournalEntries.AsNoTracking().CountAsync(ct);

        var planCounts = await _db.Users
            .AsNoTracking()
            .GroupBy(u => u.Plan)
            .Select(g => new AdminPlanCountResponse(g.Key.ToString(), g.Count()))
            .ToListAsync(ct);

        var response = new AdminStatsResponse(
            totalUsers,
            activeUsers,
            pendingUsers,
            suspendedUsers,
            totalCompanies,
            totalTx,
            monthlyTx,
            totalJe,
            planCounts
        );

        return Result<AdminStatsResponse>.Success(response);
    }
}

public sealed class SendAdminPasswordResetHandler : IRequestHandler<SendAdminPasswordResetCommand, Result<AdminMessageResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IEmailService _emailService;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<SendAdminPasswordResetHandler> _logger;

    public SendAdminPasswordResetHandler(
        ContableAIDbContext db,
        IEmailService emailService,
        IOptions<FrontendOptions> frontendOptions,
        ILogger<SendAdminPasswordResetHandler> logger)
    {
        _db = db;
        _emailService = emailService;
        _frontendOptions = frontendOptions.Value;
        _logger = logger;
    }

    public async Task<Result<AdminMessageResponse>> Handle(SendAdminPasswordResetCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([request.Id], ct);
        if (user is null) return Result<AdminMessageResponse>.NotFound();

        var token = Guid.NewGuid().ToString("N");
        user.PasswordResetToken = token;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync(ct);

        var frontendUrl = string.IsNullOrWhiteSpace(_frontendOptions.BaseUrl)
            ? "http://localhost:4200"
            : _frontendOptions.BaseUrl;
        var resetUrl = $"{frontendUrl}/reset-password?token={token}&email={Uri.EscapeDataString(user.Email)}";

        try
        {
            await _emailService.SendPasswordResetEmailAsync(user.Email, user.DisplayName, resetUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Admin] No se pudo enviar reset email a {Email}", user.Email);
            return Result<AdminMessageResponse>.Failure($"Link generado pero no se pudo enviar el email: {ex.Message}", 500);
        }

        return Result<AdminMessageResponse>.Success(new AdminMessageResponse($"Correo de recuperación enviado a {user.Email}."));
    }
}

public sealed class GetAdminGlobalRulesHandler : IRequestHandler<GetAdminGlobalRulesQuery, Result<List<AdminGlobalRuleResponse>>>
{
    private readonly ContableAIDbContext _db;

    public GetAdminGlobalRulesHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<List<AdminGlobalRuleResponse>>> Handle(GetAdminGlobalRulesQuery request, CancellationToken ct)
    {
        var rules = await _db.AccountingRules
            .AsNoTracking()
            .Where(r => r.CompanyId == null)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Keyword)
            .Select(r => new AdminGlobalRuleResponse(
                r.Id,
                r.Keyword,
                r.TargetAccount,
                r.Direction == null ? null : r.Direction.ToString(),
                r.Priority,
                r.RequiresTaxMatching
            ))
            .ToListAsync(ct);

        return Result<List<AdminGlobalRuleResponse>>.Success(rules);
    }
}

public sealed class CreateAdminGlobalRuleHandler : IRequestHandler<CreateAdminGlobalRuleCommand, Result<AdminGlobalRuleResponse>>
{
    private readonly ContableAIDbContext _db;

    public CreateAdminGlobalRuleHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminGlobalRuleResponse>> Handle(CreateAdminGlobalRuleCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            return Result<AdminGlobalRuleResponse>.Failure("Keyword requerido.", 400);

        if (string.IsNullOrWhiteSpace(request.TargetAccount))
            return Result<AdminGlobalRuleResponse>.Failure("Cuenta destino requerida.", 400);

        var direction = AdminHelpers.ParseDirection(request.Direction);

        var rule = new AccountingRule
        {
            Keyword = request.Keyword.Trim().ToUpperInvariant(),
            TargetAccount = request.TargetAccount.Trim(),
            Direction = direction,
            Priority = request.Priority ?? 100,
            RequiresTaxMatching = request.RequiresTaxMatching ?? false,
            CompanyId = null,
        };

        _db.AccountingRules.Add(rule);
        await _db.SaveChangesAsync(ct);

        return Result<AdminGlobalRuleResponse>.Success(
            new AdminGlobalRuleResponse(
                rule.Id,
                rule.Keyword,
                rule.TargetAccount,
                rule.Direction?.ToString(),
                rule.Priority,
                rule.RequiresTaxMatching
            ),
            201);
    }
}

public sealed class UpdateAdminGlobalRuleHandler : IRequestHandler<UpdateAdminGlobalRuleCommand, Result<AdminGlobalRuleResponse>>
{
    private readonly ContableAIDbContext _db;

    public UpdateAdminGlobalRuleHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminGlobalRuleResponse>> Handle(UpdateAdminGlobalRuleCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword))
            return Result<AdminGlobalRuleResponse>.Failure("Keyword requerido.", 400);

        if (string.IsNullOrWhiteSpace(request.TargetAccount))
            return Result<AdminGlobalRuleResponse>.Failure("Cuenta destino requerida.", 400);

        var exists = await _db.AccountingRules
            .AsNoTracking()
            .AnyAsync(r => r.Id == request.Id && r.CompanyId == null, ct);

        if (!exists)
            return Result<AdminGlobalRuleResponse>.NotFound();

        var direction = AdminHelpers.ParseDirection(request.Direction);

        await _db.AccountingRules
            .Where(r => r.Id == request.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Keyword, request.Keyword.Trim().ToUpperInvariant())
                .SetProperty(r => r.TargetAccount, request.TargetAccount.Trim())
                .SetProperty(r => r.Direction, direction)
                .SetProperty(r => r.Priority, request.Priority ?? 100)
                .SetProperty(r => r.RequiresTaxMatching, request.RequiresTaxMatching ?? false), ct);

        return Result<AdminGlobalRuleResponse>.Success(
            new AdminGlobalRuleResponse(
                request.Id,
                request.Keyword.Trim().ToUpperInvariant(),
                request.TargetAccount.Trim(),
                direction?.ToString(),
                request.Priority ?? 100,
                request.RequiresTaxMatching ?? false
            ));
    }
}

public sealed class DeleteAdminGlobalRuleHandler : IRequestHandler<DeleteAdminGlobalRuleCommand, Result<AdminMessageResponse>>
{
    private readonly ContableAIDbContext _db;

    public DeleteAdminGlobalRuleHandler(ContableAIDbContext db) => _db = db;

    public async Task<Result<AdminMessageResponse>> Handle(DeleteAdminGlobalRuleCommand request, CancellationToken ct)
    {
        var rule = await _db.AccountingRules.FirstOrDefaultAsync(r => r.Id == request.Id && r.CompanyId == null, ct);
        if (rule is null) return Result<AdminMessageResponse>.NotFound();

        _db.AccountingRules.Remove(rule);
        await _db.SaveChangesAsync(ct);

        return Result<AdminMessageResponse>.Success(new AdminMessageResponse($"Regla '{rule.Keyword}' eliminada."));
    }
}

public sealed class AdminDbResetHandler : IRequestHandler<AdminDbResetCommand, Result<AdminDbResetResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly IHostEnvironment _env;

    public AdminDbResetHandler(ContableAIDbContext db, IHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public async Task<Result<AdminDbResetResponse>> Handle(AdminDbResetCommand request, CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return Result<AdminDbResetResponse>.Forbidden("db-reset solo está disponible en entorno Development.");

        await _db.Database.ExecuteSqlRawAsync("DELETE FROM JournalEntryLines", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM JournalEntries", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM BankTransactions", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM ClosedPeriods", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM AuditLogs", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM AccountingRules", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Users", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Companies", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM ChartOfAccounts", ct);

        var globalRules = GlobalRules.GetDefaults()
            .Select(r => new AccountingRule
            {
                Keyword = r.Keyword,
                Direction = r.Direction,
                TargetAccount = r.TargetAccount,
                Priority = r.Priority,
                RequiresTaxMatching = r.RequiresTaxMatching,
                CompanyId = null,
            })
            .ToList();
        _db.AccountingRules.AddRange(globalRules);

        var accountNames = GlobalRules.GetDefaultAccounts();
        _db.ChartOfAccounts.AddRange(accountNames.Select(n => new ChartOfAccount { Name = n, StudioTenantId = null }));

        await _db.SaveChangesAsync(ct);

        return Result<AdminDbResetResponse>.Success(new AdminDbResetResponse(
            "Base de datos vaciada y datos iniciales re-sembrados correctamente.",
            "Llamar a POST /api/auth/seed-admin para recrear el usuario administrador.",
            "/api/auth/seed-admin",
            globalRules.Count,
            accountNames.Count
        ));
    }
}

file static class AdminHelpers
{
    internal static TransactionType? ParseDirection(string? direction) => direction?.ToUpperInvariant() switch
    {
        "DEBIT" => TransactionType.Debit,
        "CREDIT" => TransactionType.Credit,
        _ => null,
    };
}
