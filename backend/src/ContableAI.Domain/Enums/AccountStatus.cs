namespace ContableAI.Domain.Enums;

public enum AccountStatus
{
    Pending   = 0, // Recién registrado, esperando activación manual
    Active    = 1, // Habilitado para usar el sistema
    Suspended = 2, // Bloqueado por falta de pago u otro motivo
}
