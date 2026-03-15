using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ContableAI.Infrastructure.Services;

public interface ICurrentTenantService
{
    /// <summary>ID del estudio contable del usuario autenticado.</summary>
    string? StudioTenantId { get; }
    bool IsAuthenticated    { get; }
}

public class CurrentTenantService : ICurrentTenantService
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentTenantService(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? StudioTenantId =>
        _accessor.HttpContext?.User?.FindFirst("studioTenantId")?.Value;

    public bool IsAuthenticated =>
        _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true;
}
