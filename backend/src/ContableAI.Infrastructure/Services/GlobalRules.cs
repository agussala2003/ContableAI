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
        // ── AFIP (prioridad máxima) ──────────────────────────────────────────
        new() { Keyword = "DEBIN AFIP",                    Direction = null,                     TargetAccount = "AFIP A DETERMINAR",           Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "PAGOS AFIP",                    Direction = null,                     TargetAccount = "AFIP A DETERMINAR",           Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "VEP AFIP",                      Direction = null,                     TargetAccount = "AFIP A DETERMINAR",           Priority = 1,  RequiresTaxMatching = true  },
        new() { Keyword = "ARCA VEP",                      Direction = null,                     TargetAccount = "AFIP A DETERMINAR",           Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "PAGO DE OBLIGACIONES A ARCA",   Direction = TransactionType.Debit,    TargetAccount = "AFIP A DETERMINAR",           Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "TRANSF. AFIP",                  Direction = TransactionType.Debit,    TargetAccount = "AFIP A DETERMINAR",           Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "TRANSF AFIP",                   Direction = TransactionType.Debit,    TargetAccount = "AFIP A DETERMINAR",           Priority = 2,  RequiresTaxMatching = true  },
        new() { Keyword = "AFIP DGI",                      Direction = TransactionType.Debit,    TargetAccount = "PLANES DE PAGO AFIP",         Priority = 2,  RequiresTaxMatching = false },
        new() { Keyword = "ARBA",                          Direction = TransactionType.Debit,    TargetAccount = "AFIP A DETERMINAR",           Priority = 3,  RequiresTaxMatching = true  },
        new() { Keyword = "AGIP",                          Direction = TransactionType.Debit,    TargetAccount = "AFIP A DETERMINAR",           Priority = 3,  RequiresTaxMatching = true  },

        // ── IIBB / SIRCREB ───────────────────────────────────────────────────
        new() { Keyword = "RECAUDACION SIRCREB",           Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",  Priority = 3  },
        new() { Keyword = "REG REC SIRCREB",               Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",  Priority = 4  },
        new() { Keyword = "SIRCREB",                       Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",  Priority = 4  },
        new() { Keyword = "Recaudacion I.B",               Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",  Priority = 4  },

        // ── IVA ─────────────────────────────────────────────────────────────
        new() { Keyword = "I.V.A. − Debito Fiscal",        Direction = TransactionType.Debit,    TargetAccount = "IVA DEBITO FISCAL",           Priority = 6  },
        new() { Keyword = "IVA − Debito Fiscal",            Direction = TransactionType.Debit,    TargetAccount = "IVA DEBITO FISCAL",           Priority = 6  },
        new() { Keyword = "IVA - Debito Fiscal",            Direction = TransactionType.Debit,    TargetAccount = "IVA DEBITO FISCAL",           Priority = 6  },

        // ── Intereses resarcitorios AFIP ─────────────────────────────────────
        new() { Keyword = "INTERES RESARCIT",              Direction = TransactionType.Debit,    TargetAccount = "INTERESES RESARCITORIOS AFIP", Priority = 5  },

        // ── Ingresos de plataformas (MercadoPago / NAVE) ─────────────────────
        new() { Keyword = "Liquidación de dinero",         Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Liquidacion de dinero",         Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Entrada de dinero",             Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 7 },
        new() { Keyword = "Rendimientos",                  Direction = TransactionType.Credit,   TargetAccount = "RENTAS FINANCIERAS",           Priority = 8  },

        // ── Tarjetas de crédito (acreditaciones desde redes) ─────────────────
        new() { Keyword = "NAVE - VENTA CON TARJETA",     Direction = TransactionType.Credit,   TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "NAVE PAGO CON TRANSFERENCIA",  Direction = TransactionType.Credit,   TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "ACREDITAMIENTO PRISMA-COMERCIOS", Direction = TransactionType.Credit, TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "CABAL",                         Direction = TransactionType.Credit,   TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "CUENTA VISA NRO",               Direction = TransactionType.Debit,    TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "CUPONES",                       Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "PAGOS YA",                      Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "DNET",                          Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "ACRED. CUPONES",                Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },
        new() { Keyword = "AMERICAN EXPRESS",              Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },

        // ── Cobros de clientes (DEBIN / Crédito Inmediato) ───────────────────
        // Prioridad más alta que la regla genérica TRANSFER para distinguir entre
        // "cobro de cliente" y "liquidación de tarjeta"
        new() { Keyword = "TRANSFERENCIA DE TERCEROS",     Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Credito Inmediato",             Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "DEBIN",                         Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "TRANSFER",                      Direction = TransactionType.Credit,   TargetAccount = "TARJETAS DE CREDITO",          Priority = 12 },

        // ── Pagos a proveedores (transferencias salientes) ────────────────────
        new() { Keyword = "TRF INMED PROVEED",             Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 13 },
        new() { Keyword = "TRANSF. A TERCEROS",            Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 14 },
        new() { Keyword = "TRANSF INMED",                  Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 14 },
        new() { Keyword = "TRANSFER",                      Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 15 },
        new() { Keyword = "PAGO CHEQUE",                   Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 50 },

        // ── Transferencias entre cuentas propias ──────────────────────────────
        new() { Keyword = "MISMA TITULARIDAD",             Direction = null,                     TargetAccount = "CAJA Y BANCOS",                Priority = 12 },
        new() { Keyword = "E/CTAS.BBVA",                   Direction = null,                     TargetAccount = "CAJA Y BANCOS",                Priority = 12 },

        // ── Sueldos ──────────────────────────────────────────────────────────
        new() { Keyword = "HABERES",                       Direction = TransactionType.Debit,    TargetAccount = "SUELDOS",                      Priority = 20 },
        new() { Keyword = "SUELDO",                        Direction = TransactionType.Debit,    TargetAccount = "SUELDOS",                      Priority = 20 },

        // ── Fondos comunes de inversión ──────────────────────────────────────
        new() { Keyword = "Rescate Fondo Comun",           Direction = TransactionType.Credit,   TargetAccount = "FIMA CREDITO",                 Priority = 28 },
        new() { Keyword = "Suscripcion a Fondo Comun",     Direction = TransactionType.Debit,    TargetAccount = "FIMA DEBITO",                  Priority = 28 },
        new() { Keyword = "Ajuste p/ Operacion de Titulos",Direction = null,                     TargetAccount = "FIMA / INVERSIONES",           Priority = 28 },
        new() { Keyword = "FONDO COMUN",                   Direction = TransactionType.Credit,   TargetAccount = "FIMA CREDITO",                 Priority = 29 },
        new() { Keyword = "FONDO COMUN",                   Direction = TransactionType.Debit,    TargetAccount = "FIMA DEBITO",                  Priority = 29 },
        new() { Keyword = "OPER.FONDO COMUN DE INVERSI",   Direction = TransactionType.Debit,    TargetAccount = "FIMA DEBITO",                  Priority = 30 },
        new() { Keyword = "OPER.FONDO COMUN DE INVERSI",   Direction = TransactionType.Credit,   TargetAccount = "FIMA CREDITO",                 Priority = 30 },

        // ── Sindicato ────────────────────────────────────────────────────────
        new() { Keyword = "PAGO BTOB IB",                  Direction = TransactionType.Debit,    TargetAccount = "SINDICATO",                    Priority = 40 },
        new() { Keyword = "FEDERACION PATRO",              Direction = TransactionType.Debit,    TargetAccount = "SINDICATO",                    Priority = 40 },
        new() { Keyword = "UTGRA",                         Direction = TransactionType.Debit,    TargetAccount = "SINDICATO",                    Priority = 40 },
        new() { Keyword = "GASTRONOMI",                    Direction = TransactionType.Debit,    TargetAccount = "SINDICATO",                    Priority = 40 },

        // ── Comisiones de plataformas de delivery ────────────────────────────
        new() { Keyword = "RAPPI",                         Direction = TransactionType.Debit,    TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 35 },
        new() { Keyword = "Bonificación por envío",        Direction = TransactionType.Credit,   TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 35 },
        new() { Keyword = "Bonificacion por envio",        Direction = TransactionType.Credit,   TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 35 },

        // ── Devoluciones y contracargos ──────────────────────────────────────
        new() { Keyword = "DEBITO DEVOLUCION VENTA",       Direction = TransactionType.Debit,    TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },
        new() { Keyword = "Débito por deuda",              Direction = TransactionType.Debit,    TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },
        new() { Keyword = "Debito por deuda",              Direction = TransactionType.Debit,    TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },
        new() { Keyword = "Dinero retenido",               Direction = TransactionType.Debit,    TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },
        new() { Keyword = "Devolución de dinero",          Direction = TransactionType.Credit,   TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },
        new() { Keyword = "Devolucion de dinero",          Direction = TransactionType.Credit,   TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },

        // ── Gastos de logística y servicios ─────────────────────────────────
        new() { Keyword = "Pago MiCorreo",                 Direction = TransactionType.Debit,    TargetAccount = "GASTOS DE ENVIO",              Priority = 37 },
        new() { Keyword = "Pago SUBE",                     Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Pago Personal",                 Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Pago de servicios",             Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Pago con QR",                   Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },

        // ── Impuesto al Cheque (Ley 25.413) ──────────────────────────────────
        new() { Keyword = "IMP.CHEQUES",                   Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "IMP CHEQUE",                    Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "IMP. CRE. LEY",                 Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "LEY NRO 25.413",                Direction = null,                     TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "LEY NRO 25413",                 Direction = null,                     TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "LEY 25413",                     Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },
        new() { Keyword = "LEY 25.413",                    Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },
        new() { Keyword = "DEV.IMP.CRED.LEY",              Direction = TransactionType.Credit,   TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "IMP.LEY 25413",                 Direction = null,                     TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 54 },
        new() { Keyword = "Impuesto por extracción",       Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },
        new() { Keyword = "Impuesto por extraccion",       Direction = TransactionType.Debit,    TargetAccount = "IMPUESTO AL CHEQUE",           Priority = 55 },

        // ── Intereses y gastos bancarios ─────────────────────────────────────
        new() { Keyword = "COMISION",                      Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "COM ",                          Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "COM.",                          Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "GESTION PAGO",                  Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "INTERES",                       Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "SELLOS",                        Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "IVA",                           Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "COMI TRANSFERENCIA",            Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "Comis acred Camara",            Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
        new() { Keyword = "Servicio Modulo Pyme",          Direction = TransactionType.Debit,    TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },

        // ── IIBB/Percepciones CABA ───────────────────────────────────────────
        new() { Keyword = "PERC.CABA ING.BRUTOS",          Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },

        // ── ARCA (ex-AFIP): retenciones y aportes previsionales ──────────────
        new() { Keyword = "AFIP-DGI PLANRG",               Direction = null,                     TargetAccount = "PLANES DE PAGO AFIP",          Priority = 2  },
        new() { Keyword = "ARCA Recaud",                   Direction = null,                     TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true },

        // ── Credicoop: Transferencias entrantes (cobros) ─────────────────────
        new() { Keyword = "Transf. Inmediata",             Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Transf. Interbanking",          Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Transf. Recibida",              Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "ECHQ − Acreditac",              Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Deposito Inmediato en Cta",     Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Deposito Ensobrado",            Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },
        new() { Keyword = "Deposito por caja",             Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 11 },

        // ── Credicoop: Transferencias salientes (pagos a proveedores) ────────
        new() { Keyword = "Transf.Inmediata",              Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 14 },
        new() { Keyword = "Debito Inmediato (DEBIN)",       Direction = TransactionType.Debit,    TargetAccount = "PROVEEDORES",                  Priority = 13 },

        // ── Credicoop: Compra con tarjeta de débito ──────────────────────────
        new() { Keyword = "Compra Local con Tarjeta de Debito", Direction = TransactionType.Debit, TargetAccount = "GASTOS GENERALES",           Priority = 38 },

        // ── BBVA: Fondos comunes de inversión (RENPEB) ───────────────────────
        new() { Keyword = "FBA RENPEB",                    Direction = TransactionType.Credit,   TargetAccount = "FIMA CREDITO",                 Priority = 29 },
        new() { Keyword = "FBA RENPEB",                    Direction = TransactionType.Debit,    TargetAccount = "FIMA DEBITO",                  Priority = 29 },

        // ── BBVA: Transferencias entre cuentas propias ───────────────────────
        new() { Keyword = "E/ CTAS. BBVA",                 Direction = null,                     TargetAccount = "CAJA Y BANCOS",                Priority = 12 },
        new() { Keyword = "TRANSF. CLIENTE CTA.",          Direction = TransactionType.Debit,    TargetAccount = "CAJA Y BANCOS",                Priority = 12 },

        // ── BBVA: Cobro de cheques acreditados ───────────────────────────────
        new() { Keyword = "PAGO CHEQUE 48HS",              Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 49 },
        new() { Keyword = "DEPOSITO AUTOSERVICIO PLUS",    Direction = TransactionType.Credit,   TargetAccount = "VENTAS EFECTIVO/MOSTRADOR",    Priority = 11 },

        // ── Sueldos: pago de vacaciones ──────────────────────────────────────
        new() { Keyword = "PAGO DE VACACIONES",            Direction = TransactionType.Debit,    TargetAccount = "SUELDOS",                      Priority = 20 },

        // ── Plataformas delivery: liquidación de ventas (crédito) ────────────
        new() { Keyword = "RAPPI",                         Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 34 },
        new() { Keyword = "PEDIDOSYA",                     Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 35 },
        new() { Keyword = "DELIVERY HERO",                 Direction = TransactionType.Credit,   TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 35 },
        new() { Keyword = "TARJETA NARANJA",               Direction = TransactionType.Credit,   TargetAccount = "TARJETAS DE CREDITO",          Priority = 10 },

        // ── Cashback ─────────────────────────────────────────────────────────
        new() { Keyword = "CASHBACK CC EMPRESAS",          Direction = TransactionType.Credit,   TargetAccount = "DEVOLUCIONES Y REINTEGROS",    Priority = 36 },

        // ── Liquidación cancelada (más específica; debe ir antes de la regla genérica de Liquidación) ──
        new() { Keyword = "Liquidación de dinero cancelada", Direction = TransactionType.Credit, TargetAccount = "DEVOLUCIONES Y REINTEGROS",   Priority = 6  },
        new() { Keyword = "Liquidacion de dinero cancelada", Direction = TransactionType.Credit, TargetAccount = "DEVOLUCIONES Y REINTEGROS",   Priority = 6  },
        new() { Keyword = "Liquidación de dinero cancelada Venta cancelada", Direction = TransactionType.Debit, TargetAccount = "DEVOLUCIONES Y REINTEGROS", Priority = 6  },
        new() { Keyword = "Liquidacion de dinero cancelada Venta cancelada", Direction = TransactionType.Debit, TargetAccount = "DEVOLUCIONES Y REINTEGROS", Priority = 6  },

        // ── BBVA: Cobro vía GESTION PAGO (crédito = pago colectado) ─────────
        new() { Keyword = "GESTION PAGO",                  Direction = TransactionType.Credit,   TargetAccount = "CUENTAS A COBRAR",             Priority = 59 },

        // ── IIBB Percepciones (Credicoop) ────────────────────────────────────
        new() { Keyword = "Percep Ingr Brutos",            Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },
        new() { Keyword = "Percep. Ingresos Brutos",       Direction = null,                     TargetAccount = "RECAUDACION SIRCREB / IIBB",   Priority = 4  },

        // ── AFIP: Débito directo de recaudaciones ────────────────────────────
        new() { Keyword = "Debito Directo − Recaudaciones", Direction = TransactionType.Debit,   TargetAccount = "AFIP A DETERMINAR",            Priority = 2,  RequiresTaxMatching = true },

        // ── Fondos de inversión GALICIA ──────────────────────────────────────
        new() { Keyword = "SUSCRIPCION FIMA",              Direction = TransactionType.Debit,    TargetAccount = "FIMA DEBITO",                  Priority = 29 },
        new() { Keyword = "RESCATE FIMA",                  Direction = TransactionType.Credit,   TargetAccount = "FIMA CREDITO",                 Priority = 29 },
        new() { Keyword = "AJUSTE FIMA",                   Direction = null,                     TargetAccount = "FIMA / INVERSIONES",           Priority = 29 },

        // ── MercadoPago: ingresos por envíos ─────────────────────────────────
        new() { Keyword = "Mercado Envíos",                Direction = TransactionType.Credit,   TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 35 },
        new() { Keyword = "Mercado Envios",                Direction = TransactionType.Credit,   TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 35 },
        new() { Keyword = "Bonificación por envío cancelada Mercado Envíos", Direction = TransactionType.Debit, TargetAccount = "COMISIONES PLATAFORMAS", Priority = 35 },
        new() { Keyword = "Bonificacion por envio cancelada Mercado Envios", Direction = TransactionType.Debit, TargetAccount = "COMISIONES PLATAFORMAS", Priority = 35 },

        // ── MercadoPago: pagos y compras frecuentes sin clasificar ───────────
        new() { Keyword = "Pago Pizzeria Muraroa",         Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },
        new() { Keyword = "Pago Sergio Andres Duprat",     Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },
        new() { Keyword = "Pago Mecubro",                  Direction = TransactionType.Debit,    TargetAccount = "COMISIONES PLATAFORMAS",       Priority = 38 },
        new() { Keyword = "Pago Movistar",                 Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Pago Anibal Rogelio Jalife",    Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },
        new() { Keyword = "Pago Federico Nestor Zaupa",    Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },
        new() { Keyword = "Compra Mercado Libre",          Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 38 },
        new() { Keyword = "Compra de ",                    Direction = TransactionType.Debit,    TargetAccount = "GASTOS GENERALES",             Priority = 39 },
        new() { Keyword = "Dinero recibido Ingreso de dinero correspondiente a tu reclamo", Direction = TransactionType.Credit, TargetAccount = "DEVOLUCIONES Y REINTEGROS", Priority = 36 },
        new() { Keyword = "Dinero recibido Ingreso de dinero por tu envío", Direction = TransactionType.Credit, TargetAccount = "VENTAS CON TARJETA / MARKETPLACE", Priority = 34 },

        // ── CREDICOOP: cargos recurrentes ───────────────────────────────────
        new() { Keyword = "Suscripcion al Periodico Accion", Direction = TransactionType.Debit,  TargetAccount = "GASTOS GENERALES",             Priority = 38 },

        // ── BBVA: ingresos y cobranzas sin clasificar ───────────────────────
        new() { Keyword = "DEPOSITO EFECTIVO",             Direction = TransactionType.Credit,   TargetAccount = "VENTAS EFECTIVO/MOSTRADOR",    Priority = 11 },

        // ── Tarjetas de crédito: débito automático desde cuenta ──────────────
        new() { Keyword = "Tarjeta Cabal",                 Direction = TransactionType.Debit,    TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "Tarjeta Visa",                  Direction = TransactionType.Debit,    TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },
        new() { Keyword = "ANUL. PAGO COMERCIOS",          Direction = null,                     TargetAccount = "TARJETAS DE CREDITO",          Priority = 9  },

        // ── Débito automático genérico (Credicoop / BBVA) ────────────────────
        new() { Keyword = "DEBITO DIRECTO",                Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Debito Automatico Directo",     Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },
        new() { Keyword = "Debito/Credito Automatico",     Direction = TransactionType.Debit,    TargetAccount = "SERVICIOS PUBLICOS",           Priority = 37 },

        // ── Galicia: ajustes promocionales ──────────────────────────────────
        new() { Keyword = "AJUSTE APORTES PROMOCION",      Direction = null,                     TargetAccount = "INT Y GSTOS BANCARIOS",        Priority = 60 },
    ];

    /// <summary>
    /// Plan de cuentas global predeterminado (StudioTenantId = null).
    /// Fuente única de verdad para el seeder y el db-reset.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultAccounts() =>
    [
        // Genéricas
        "CUENTA PARTICULAR SOCIO", "GASTOS A RENDIR", "CAJA Y BANCOS", "DEVOLUCIONES Y REINTEGROS",
        // Ventas y cobros
        "VENTAS EFECTIVO/MOSTRADOR", "VENTAS CON TARJETA / MARKETPLACE", "CUENTAS A COBRAR",
        // IVA
        "IVA VENTAS", "IVA COMPRAS", "IVA DEBITO FISCAL",
        // Proveedores y pagos
        "PROVEEDORES", "SUELDOS", "SINDICATO / UTGRA", "COMISIONES", "COMISIONES PLATAFORMAS",
        // Bancos e inversiones
        "INT Y GSTOS BANCARIOS", "RENTAS FINANCIERAS",
        // Tarjetas
        "TARJETAS DE CREDITO",
        // Fondos comunes
        "FIMA DEBITO", "FIMA CREDITO", "FIMA / INVERSIONES",
        // AFIP / Impuestos
        "AFIP A DETERMINAR", "PLANES DE PAGO AFIP", "INTERESES RESARCITORIOS AFIP",
        "IMPUESTO AL CHEQUE", "IMPUESTOS Y TASAS",
        "IMPUESTOS NACIONALES AFIP", "PLAN DE PAGO AFIP DGI",
        // IIBB
        "ARBA / IIBB", "AGIP / IIBB", "RECAUDACION SIRCREB / IIBB",
        // Otros gastos
        "GASTOS DE ENVIO", "SERVICIOS PUBLICOS", "GASTOS GENERALES",
    ];
}
