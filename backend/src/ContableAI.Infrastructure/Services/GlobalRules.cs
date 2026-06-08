using ContableAI.Domain.Entities;
using ContableAI.Domain.Enums;

namespace ContableAI.Infrastructure.Services;

/// <summary>
/// Reglas globales predeterminadas que aplican a TODAS las empresas.
/// Las reglas específicas por empresa (cargadas desde la BD) pueden sobreescribir estas.
/// </summary>
public static class GlobalRules
{
    public static IReadOnlyList<AccountingRule> GetDefaults() =>
    [
        // ── AFIP / ARCA (prioridad máxima) ───────────────────────────────────
        new() { Keyword = "DEBIN AFIP",                   Direction = null,                  TargetAccount = "AFIP A DETERMINAR",            Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "PAGOS AFIP",                   Direction = null,                  TargetAccount = "AFIP A DETERMINAR",            Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "VEP AFIP",                     Direction = null,                  TargetAccount = "AFIP A DETERMINAR",            Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "ARCA VEP",                     Direction = null,                  TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "PAGO DE OBLIGACIONES A ARCA",  Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "TRANSF. AFIP",                 Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "TRANSF AFIP",                  Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "AFIP DGI",                     Direction = TransactionType.Debit, TargetAccount = "PLANES DE PAGO AFIP",          Priority = 2,  RequiresTaxMatching = false },
        new() { Keyword = "ARCA Recaud",                  Direction = null,                  TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "DEBA AFIP",                    Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "ARBA",                         Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 3,  RequiresTaxMatching = true  },
        new() { Keyword = "AGIP",                         Direction = TransactionType.Debit, TargetAccount = "AFIP A DETERMINAR",            Priority = 3,  RequiresTaxMatching = true  },

        // ── IIBB / Percepciones ──────────────────────────────────────────────
        new() { Keyword = "RECAUDACION SIRCREB",          Direction = null,                  TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 3  },
        new() { Keyword = "SIRCREB",                      Direction = null,                  TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },
        new() { Keyword = "Recaudacion I.B",              Direction = null,                  TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },
        new() { Keyword = "PERC.CABA ING.BRUTOS",         Direction = null,                  TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },
        new() { Keyword = "Percep Ingr Brutos",           Direction = null,                  TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },

        // ── IVA ──────────────────────────────────────────────────────────────
        new() { Keyword = "IVA - Debito Fiscal",          Direction = TransactionType.Debit, TargetAccount = "IVA DEBITO FISCAL",            Priority = 6  },
        new() { Keyword = "INTERES RESARCIT",             Direction = TransactionType.Debit, TargetAccount = "INTERESES RESARCITORIOS AFIP", Priority = 5  },

        // ── Ingresos de plataformas (MercadoPago) ────────────────────────────
        new() { Keyword = "Liquidación de dinero",        Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Liquidacion de dinero",        Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Entrada de dinero",            Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Rendimientos",                 Direction = TransactionType.Credit, TargetAccount = "RENTAS FINANCIERAS",           Priority = 8  },

        // ── Delivery (liquidaciones crédito) ─────────────────────────────────
        new() { Keyword = "RAPPI",                        Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 34 },
        new() { Keyword = "PEDIDOSYA",                    Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 35 },
        new() { Keyword = "DELIVERY HERO",                Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 35 },

        // ── Tarjetas de crédito ──────────────────────────────────────────────
        new() { Keyword = "CUPONES",                      Direction = null,                  TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "AMERICAN EXPRESS",             Direction = null,                  TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "CABAL",                        Direction = TransactionType.Credit, TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "TARJETA NARANJA",              Direction = TransactionType.Credit, TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "TRANSFER",                     Direction = TransactionType.Credit, TargetAccount = "TARJETAS DE CREDITO",          Priority = 12 },

        // ── Cobros de clientes ────────────────────────────────────────────────
        new() { Keyword = "TRANSFERENCIA DE TERCEROS",    Direction = TransactionType.Credit, TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Credito Inmediato",            Direction = TransactionType.Credit, TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "DEBIN",                        Direction = TransactionType.Credit, TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "DEPOSITO EFECTIVO",            Direction = TransactionType.Credit, TargetAccount = "VENTAS EFECTIVO/MOSTRADOR",    Priority = 11 },

        // ── Pagos a proveedores ───────────────────────────────────────────────
        new() { Keyword = "TRANSF. A TERCEROS",           Direction = TransactionType.Debit,  TargetAccount = "PROVEEDORES",                  Priority = 14 },
        new() { Keyword = "TRANSF INMED",                 Direction = TransactionType.Debit,  TargetAccount = "PROVEEDORES",                  Priority = 14 },
        new() { Keyword = "TRANSFER",                     Direction = TransactionType.Debit,  TargetAccount = "PROVEEDORES",                  Priority = 15 },
        new() { Keyword = "PAGO CHEQUE",                  Direction = TransactionType.Debit,  TargetAccount = "PROVEEDORES",                  Priority = 50 },

        // ── Transferencias entre cuentas propias ──────────────────────────────
        new() { Keyword = "MISMA TITULARIDAD",            Direction = null,                  TargetAccount = "CAJA Y BANCOS",                Priority = 12 },

        // ── Sueldos ───────────────────────────────────────────────────────────
        new() { Keyword = "HABERES",                      Direction = TransactionType.Debit,  TargetAccount = "SUELDOS",                      Priority = 20 },
        new() { Keyword = "SUELDO",                       Direction = TransactionType.Debit,  TargetAccount = "SUELDOS",                      Priority = 20 },
        new() { Keyword = "PAGO DE VACACIONES",           Direction = TransactionType.Debit,  TargetAccount = "SUELDOS",                      Priority = 20 },

        // ── Fondos comunes de inversión ───────────────────────────────────────
        new() { Keyword = "FONDO COMUN",                  Direction = TransactionType.Credit, TargetAccount = "FIMA CREDITO",                 Priority = 29 },
        new() { Keyword = "FONDO COMUN",                  Direction = TransactionType.Debit,  TargetAccount = "FIMA DEBITO",                  Priority = 29 },

        // ── Devoluciones ──────────────────────────────────────────────────────
        new() { Keyword = "DEBITO DEVOLUCION VENTA",      Direction = TransactionType.Debit,  TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },

        // ── Impuesto al Cheque (Ley 25.413) ──────────────────────────────────
        new() { Keyword = "IMP.CHEQUES",                  Direction = TransactionType.Debit,  TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "IMP CHEQUE",                   Direction = TransactionType.Debit,  TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "LEY NRO 25.413",               Direction = null,                  TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "LEY 25413",                    Direction = TransactionType.Debit,  TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },
        new() { Keyword = "LEY 25.413",                   Direction = TransactionType.Debit,  TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },
        new() { Keyword = "DEV.IMP.CRED.LEY",             Direction = TransactionType.Credit, TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },

        // ── Gastos bancarios ─────────────────────────────────────────────────
        new() { Keyword = "COMISION",                     Direction = TransactionType.Debit,  TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "INTERES",                      Direction = TransactionType.Debit,  TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "SELLOS",                       Direction = TransactionType.Debit,  TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "IVA",                          Direction = TransactionType.Debit,  TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },

        // ── Débito automático ─────────────────────────────────────────────────
        new() { Keyword = "DEBITO DIRECTO",               Direction = TransactionType.Debit,  TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Debito Automatico Directo",    Direction = TransactionType.Debit,  TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
    ];

    /// <summary>
    /// Plan de cuentas global predeterminado (StudioTenantId = null).
    /// Fuente única de verdad para el seeder y el db-reset.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultAccounts() =>
    [
        // Activo / Caja
        "CAJA Y BANCOS",
        "CUENTAS A COBRAR",
        // Ventas
        "VENTAS EFECTIVO/MOSTRADOR",
        "VENTAS CON TARJETA / MARKETPLACE",
        // IVA
        "IVA VENTAS",
        "IVA COMPRAS",
        "IVA DEBITO FISCAL",
        // Pasivo / Egresos
        "PROVEEDORES",
        "SUELDOS",
        "TARJETAS DE CREDITO",
        // Inversiones
        "FIMA DEBITO",
        "FIMA CREDITO",
        "FIMA / INVERSIONES",
        "RENTAS FINANCIERAS",
        // AFIP / Impuestos
        "AFIP A DETERMINAR",
        "PLANES DE PAGO AFIP",
        "INTERESES RESARCITORIOS AFIP",
        "IMPUESTO AL CHEQUE",
        "RECAUDACION SIRCREB / IIBB",
        // Cuentas destino del cruce AFIP (deben coincidir con PdfAfipParserService.TaxNameMap /
        // BodyTaxHints). Sembradas para que el cruce y la carga manual converjan en una única cuenta.
        "Cargas Sociales",
        "IVA A Pagar",
        "Pago IIBB",
        "Impuesto Ganancias",
        "Honorarios Fiscales",
        "VEP Consolidado",
        "Seg. Riesgo Trabajo",
        "Plan de Facilidades",
        "Imp. Ley 25413",
        // Gastos
        "INT Y GSTOS BANCARIOS",
        "SERVICIOS PUBLICOS",
        "GASTOS GENERALES",
        "COMISIONES PLATAFORMAS",
        // Devoluciones
        "DEVOLUCIONES Y REINTEGROS",
    ];
}
