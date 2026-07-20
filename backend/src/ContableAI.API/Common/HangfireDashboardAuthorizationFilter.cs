using ContableAI.Domain.Enums;
using Hangfire.Dashboard;

namespace ContableAI.API.Common;

/// <summary>
/// Filtro de autorización del dashboard de Hangfire (fix A-2).
///
/// El dashboard (/hangfire) queda protegido: solo lo pueden abrir peticiones autenticadas
/// por JWT cuyo usuario tenga el rol <see cref="UserRole.SystemAdmin"/>. Reemplaza al filtro
/// por defecto de Hangfire (solo-local), que es ambiguo y frágil detrás de un reverse proxy.
///
/// La autenticación JWT (UseAuthentication) ya corrió cuando este filtro se ejecuta, por lo que
/// <see cref="DashboardContext.GetHttpContext"/> expone el <c>ClaimsPrincipal</c> validado.
/// El token debe viajar en el header <c>Authorization: Bearer</c> (no hay cookie de sesión).
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;

        return user.Identity?.IsAuthenticated == true
            && user.IsInRole(UserRole.SystemAdmin.ToString());
    }
}
