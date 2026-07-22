namespace ContableAI.API.Common;

/// <summary>
/// Nombres de las políticas de autorización de la API (M-4). Centralizados para evitar
/// strings mágicos dispersos por los endpoints.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Solo el dueño del estudio (<c>StudioOwner</c>) o el <c>SystemAdmin</c>. Se aplica a acciones
    /// de gestión y destructivas (empresas, reglas, borrado de asientos/transacciones, períodos).
    /// El rol operativo <c>DataEntry</c> queda excluido.
    /// </summary>
    public const string RequireStudioOwner = "RequireStudioOwner";
}
