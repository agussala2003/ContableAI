using ContableAI.Application.Common;
using ContableAI.Application.Features.Companies.Commands;
using ContableAI.Application.Features.Companies.Queries;
using ContableAI.API.Common;
using ContableAI.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ContableAI.API.Endpoints;

public static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/companies", async (
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var result = await mediator.Send(new GetCompaniesQuery(tenant.StudioTenantId!));
            return result.ToHttpResult();
        })
        .WithName("GetCompanies")
        .WithTags("Empresas")
        .WithSummary("Listar todas las empresas activas del estudio autenticado.")
        .Produces<List<CompanyResponse>>(200);

        app.MapGet("/api/companies/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetCompanyQuery(id));
            return result.ToHttpResult();
        })
        .WithName("GetCompany")
        .WithTags("Empresas")
        .WithSummary("Obtener una empresa por ID.")
        .Produces<CompanyResponse>(200)
        .Produces<ProblemDetails>(404);

        app.MapPost("/api/companies", async (
            CreateCompanyRequest  req,
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var cmd    = new CreateCompanyCommand(req.Name, req.Cuit, req.BusinessType, req.BankAccountName, tenant.StudioTenantId ?? "ESTUDIO_DEFAULT");
            var result = await mediator.Send(cmd);
            return result.StatusCode == 201
                ? result.ToCreatedResult($"/api/companies/{result.Value?.Id}")
                : result.ToHttpResult();
        })
        .WithName("CreateCompany")
        .WithTags("Empresas")
        .WithSummary("Crear una nueva empresa.")
        .WithDescription("Body: { name, cuit, businessType (opcional), bankAccountName (opcional) }. Valida unicidad de CUIT y cuota del plan antes de persistir. Devuelve 402 si se alcanzó el límite de empresas.")
        .Produces<CompanyResponse>(201)
        .Produces<ProblemDetails>(409)
        .Produces<ProblemDetails>(402);

        app.MapPut("/api/companies/{id:guid}", async (
            Guid                 id,
            UpdateCompanyRequest req,
            IMediator            mediator) =>
        {
            var cmd    = new UpdateCompanyCommand(id, req.Name, req.BusinessType, req.SplitChequeTax, req.BankAccountName);
            var result = await mediator.Send(cmd);
            return result.ToHttpResult();
        })
        .WithName("UpdateCompany")
        .WithTags("Empresas")
        .WithSummary("Actualizar parcialmente una empresa.")
        .WithDescription("Body: { name, businessType, splitChequeTax (bool), bankAccountName }. Solo los campos no-null se actualizan. bankAccountName es la cuenta contable bancaria usada para los asientos.")
        .Produces<CompanyResponse>(200)
        .Produces<ProblemDetails>(404);

        app.MapDelete("/api/companies/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new DeleteCompanyCommand(id));
            return result.IsSuccess ? Results.NoContent() : result.ToHttpResult();
        })
        .WithName("DeleteCompany")
        .WithTags("Empresas")
        .WithSummary("Dar de baja una empresa (IsActive = false).")
        .WithDescription("Soft-delete: la empresa deja de aparecer en listados pero su historial de transacciones y asientos se conserva.")
        .Produces(204)
        .Produces<ProblemDetails>(404);

        // ── Rules nested under company ────────────────────────────────────────
        app.MapGet("/api/companies/{companyId:guid}/rules", async (Guid companyId, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetCompanyRulesQuery(companyId));
            return result.ToHttpResult();
        })
        .WithName("GetCompanyRules")
        .WithTags("Reglas")
        .WithSummary("Listar todas las reglas de clasificación de una empresa, ordenadas por prioridad.")
        .Produces<List<RuleResponse>>(200);

        app.MapPost("/api/companies/{companyId:guid}/rules", async (
            Guid                  companyId,
            CreateRuleRequest     req,
            ICurrentTenantService tenant,
            IMediator             mediator) =>
        {
            var cmd    = new CreateCompanyRuleCommand(companyId, req.Keyword, req.TargetAccount, req.Direction, req.Priority, req.RequiresTaxMatching, tenant.StudioTenantId!);
            var result = await mediator.Send(cmd);
            return result.StatusCode == 201
                ? result.ToCreatedResult($"/api/companies/{companyId}/rules/{result.Value?.Id}")
                : result.ToHttpResult();
        })
        .WithName("CreateCompanyRule")
        .WithTags("Reglas")
        .WithSummary("Agregar una regla de clasificación a una empresa.")
        .WithDescription("Body: { keyword: string, targetAccount: string, direction: \"DEBIT\" | \"CREDIT\" | null, priority: int, requiresTaxMatching: bool }. El keyword se compara case-insensitive contra descripciones de transacciones. Incluye validación de cuota del plan.")
        .Produces<RuleResponse>(201)
        .Produces<ProblemDetails>(402)
        .Produces<ProblemDetails>(404);
    }
}
