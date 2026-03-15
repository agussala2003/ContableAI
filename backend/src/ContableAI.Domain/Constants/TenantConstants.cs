namespace ContableAI.Domain.Constants;

/// <summary>
/// Identificadores de tenant reservados por el sistema.
/// </summary>
public static class TenantConstants
{
    /// <summary>
    /// Tenant del administrador del sistema (SystemAdmin).
    /// No pertenece a ningún estudio contable real.
    /// </summary>
    public const string System = "SYSTEM";

    /// <summary>
    /// Tenant heredado usado antes de la migración multi-empresa.
    /// Se mantiene por compatibilidad con datos existentes; no usar en código nuevo.
    /// </summary>
    [System.Obsolete("Usar Company.Id como clave real de tenant. Este literal existe sólo para compatibilidad.")]
    public const string EstudioDefault = "ESTUDIO_DEFAULT";
}
