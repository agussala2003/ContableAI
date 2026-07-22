using ContableAI.Application.Common;
using ContableAI.Application.Features.Admin.Commands;
using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;
using ContableAI.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ContableAI.Infrastructure.Features.Admin;

/// <summary>
/// Cierre formal de cuenta de un estudio (derecho al olvido, P-1). Ejecuta la cascada completa
/// de <see cref="StudioTenantPurger"/> dentro de una transacción (todo o nada) y deja un
/// AuditLog de cierre con el solicitante, el motivo y el conteo de lo eliminado.
/// </summary>
public sealed class DeleteStudioTenantHandler
    : IRequestHandler<DeleteStudioTenantCommand, Result<DeleteStudioTenantResponse>>
{
    private readonly ContableAIDbContext _db;
    private readonly ILogger<DeleteStudioTenantHandler> _logger;

    public DeleteStudioTenantHandler(ContableAIDbContext db, ILogger<DeleteStudioTenantHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<Result<DeleteStudioTenantResponse>> Handle(
        DeleteStudioTenantCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.StudioTenantId))
            return Result<DeleteStudioTenantResponse>.Failure("StudioTenantId requerido.");

        var users = await _db.Users.AsNoTracking()
            .Where(u => u.StudioTenantId == cmd.StudioTenantId)
            .ToListAsync(ct);

        var hasCompanies = await _db.Companies.IgnoreQueryFilters()
            .AnyAsync(c => c.StudioTenantId == cmd.StudioTenantId, ct);

        if (users.Count == 0 && !hasCompanies)
            return Result<DeleteStudioTenantResponse>.NotFound(
                $"No existe ningún estudio con StudioTenantId '{cmd.StudioTenantId}'.");

        if (users.Any(u => u.Role == UserRole.SystemAdmin))
            return Result<DeleteStudioTenantResponse>.Failure(
                "El tenant contiene un SystemAdmin: no se puede cerrar por esta vía.", 400);

        // EnableRetryOnFailure (R-1) exige envolver la transacción explícita en la execution
        // strategy. En el proveedor InMemory (tests) no hay transacciones: se ejecuta directo.
        var strategy = _db.Database.CreateExecutionStrategy();

        StudioTenantPurger.PurgeCounts counts = null!;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;

            counts = await StudioTenantPurger.PurgeAsync(_db, cmd.StudioTenantId, ct);

            // Registro legal de la ejecución: quién pidió el cierre, cuándo y qué se eliminó.
            // Se escribe DESPUÉS de la seudonimización para no ser anonimizado él mismo.
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId   = cmd.StudioTenantId,
                UserId     = "system",
                UserEmail  = cmd.RequestedBy,
                Action     = AuditAction.Deleted.ToString(),
                EntityName = "StudioTenant",
                EntityId   = cmd.StudioTenantId,
                Changes    = JsonSerializer.Serialize(new
                {
                    cmd.Reason,
                    counts.Users,
                    counts.Companies,
                    counts.BankTransactions,
                    counts.JournalEntries,
                    counts.AccountingRules,
                    counts.AuditLogsAnonymized,
                }),
            });
            await _db.SaveChangesAsync(ct);

            if (tx is not null)
                await tx.CommitAsync(ct);
        });

        _logger.LogWarning(
            "[PRIVACY] Cierre de cuenta ejecutado — Tenant={Tenant}, SolicitadoPor={RequestedBy}, " +
            "Usuarios={Users}, Empresas={Companies}, Transacciones={Txs}, Asientos={Entries}, AuditLogsAnonimizados={Anon}",
            cmd.StudioTenantId, cmd.RequestedBy, counts.Users, counts.Companies,
            counts.BankTransactions, counts.JournalEntries, counts.AuditLogsAnonymized);

        return Result<DeleteStudioTenantResponse>.Success(new DeleteStudioTenantResponse(
            cmd.StudioTenantId,
            counts.Users, counts.RefreshTokens, counts.Companies, counts.BankTransactions,
            counts.JournalEntries, counts.JournalEntryLines, counts.AccountingRules,
            counts.RuleSuggestions, counts.AfipVouchers, counts.ChartOfAccounts,
            counts.ClosedPeriods, counts.UploadJobResults, counts.StagedFilesPurged,
            counts.AuditLogsAnonymized));
    }
}
