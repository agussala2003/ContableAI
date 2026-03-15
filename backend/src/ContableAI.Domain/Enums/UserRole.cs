namespace ContableAI.Domain.Enums;

public enum UserRole
{
    StudioOwner = 0,  // Dueño del estudio — puede gestionar todo
    DataEntry   = 1,  // Operativo — solo puede procesar extractos
    SystemAdmin = 99, // Administrador de la plataforma ContableAI
}
