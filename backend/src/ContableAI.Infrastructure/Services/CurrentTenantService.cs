using ContableAI.Domain.Enums;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ContableAI.Infrastructure.Services;

public interface ICurrentTenantService
{
    /// <summary>ID del estudio contable del usuario autenticado.</summary>
    string? StudioTenantId { get; }
    bool IsAuthenticated    { get; }

    /// <summary>
    /// <c>true</c> si el usuario autenticado tiene el rol <see cref="UserRole.SystemAdmin"/>.
    /// El SystemAdmin es el operador de la plataforma y accede legítimamente a datos de todos
    /// los estudios; por eso los Global Query Filters de aislamiento por tenant se desactivan
    /// para él (ver <c>ContableAIDbContext</c>).
    /// </summary>
    bool IsSystemAdmin { get; }
}

public class CurrentTenantService : ICurrentTenantService
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentTenantService(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? StudioTenantId =>
        _accessor.HttpContext?.User?.FindFirst("studioTenantId")?.Value;

    public bool IsAuthenticated =>
        _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

    public bool IsSystemAdmin =>
        _accessor.HttpContext?.User?.IsInRole(UserRole.SystemAdmin.ToString()) == true;
}
