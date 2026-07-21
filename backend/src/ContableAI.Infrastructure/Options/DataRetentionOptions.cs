namespace ContableAI.Infrastructure.Options;

/// <summary>
/// Ventanas de retención del <c>DataRetentionJob</c> (P-4/P-5). Los defaults son la política
/// oficial documentada en <c>docs/RETENCION_DATOS.md</c>; la sección <c>DataRetention</c> de
/// appsettings permite ajustarlos por entorno sin recompilar.
/// </summary>
public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    /// <summary>
    /// Días que se conserva el resultado JSON de cada job de subida (P-4). El frontend lo
    /// consume por polling en los segundos posteriores a la subida; 30 días deja margen de
    /// sobra para soporte/debugging sin acumular datos financieros indefinidamente.
    /// </summary>
    public int UploadJobResultsDays { get; set; } = 30;

    /// <summary>
    /// Horas tras las cuales un <c>StagedUploadFile</c> se considera huérfano (P-5). El job de
    /// Hangfire consume y borra la fila en segundos/minutos; a las 24 h ya no queda ningún
    /// reintento vivo que pueda reclamarla.
    /// </summary>
    public int StagedFileOrphanHours { get; set; } = 24;

    /// <summary>
    /// Días de gracia entre la baja de una empresa (<c>Company.DeletedAt</c>, P-2) y su
    /// hard-delete en cascada. La ventana permite deshacer bajas accidentales por soporte.
    /// </summary>
    public int SoftDeletedCompanyDays { get; set; } = 90;
}
