import { Injectable, signal, computed } from '@angular/core';

export interface TourStep {
  targetId: string;
  title: string;
  body: string;
  position: 'top' | 'bottom' | 'left' | 'right';
}

const TOURS: Record<string, TourStep[]> = {
  '/dashboard': [
    {
      targetId: 'tour-dashboard-kpis',
      title: 'Métricas clave del período',
      body: 'Estos KPIs resumen el estado financiero de la empresa: total de movimientos, clasificados, pendientes y monto operado en el mes. Son el termómetro de tu trabajo contable.',
      position: 'bottom',
    },
    {
      targetId: 'tour-dashboard-classification',
      title: 'Tasa de clasificación',
      body: 'Indica qué porcentaje de los movimientos ya tienen cuenta contable asignada. Apuntá al 100% antes de generar asientos: cualquier movimiento sin cuenta queda fuera del libro diario.',
      position: 'top',
    },
    {
      targetId: 'tour-dashboard-period',
      title: 'Filtro de período',
      body: 'Cambiá el mes y año para analizar cualquier período histórico. Los KPIs y la tasa de clasificación se actualizan al instante.',
      position: 'bottom',
    },
  ],

  '/': [
    {
      targetId: 'tour-upload-btn',
      title: 'Subí el extracto bancario',
      body: 'PDF, CSV o Excel. El banco se detecta automáticamente — sin configuración previa.',
      position: 'bottom',
    },
    {
      targetId: 'tour-stats-cards',
      title: 'Ingresos, egresos y saldo neto',
      body: 'Resumen financiero del extracto cargado. Ingresos son los créditos bancarios, egresos los débitos. El saldo neto es la diferencia. Son de solo lectura: se calculan de los movimientos.',
      position: 'bottom',
    },
    {
      targetId: 'tour-tx-grid',
      title: 'Revisá y asigná cuentas',
      body: 'Cada fila es un movimiento bancario. Asignale una cuenta contable manualmente o dejá que las reglas lo clasifiquen en bloque. El semáforo verde indica que el movimiento está listo para asentar.',
      position: 'top',
    },
    {
      targetId: 'tour-generate-btn',
      title: 'Generá el asiento',
      body: 'Un clic y tenés el libro diario en doble entrada con todos los movimientos clasificados. Podés asentar todos o solo los seleccionados. Exportable a Excel, Holistor o Bejerman.',
      position: 'bottom',
    },
  ],

  '/rules': [
    {
      targetId: 'tour-rules-toolbar',
      title: 'Reglas de clasificación',
      body: 'Las reglas asignan automáticamente una cuenta contable a cada movimiento que contenga la palabra clave. Cuantas más reglas, menos clasificación manual necesitás.',
      position: 'bottom',
    },
    {
      targetId: 'tour-rules-new-btn',
      title: 'Crear una nueva regla',
      body: 'Definí una palabra clave (p.ej. "MERCADOPAGO") y la cuenta contable destino. Desde aquí también podés aplicarla retroactivamente a movimientos ya cargados.',
      position: 'bottom',
    },
    {
      targetId: 'tour-rules-table',
      title: 'Tu lista de reglas',
      body: 'Acá ves todas las reglas activas. Las reglas propias de empresa tienen prioridad sobre las globales del estudio. Podés editarlas, desactivarlas o reordenar su precedencia.',
      position: 'top',
    },
  ],

  '/journal': [
    {
      targetId: 'tour-journal-header',
      title: 'Libro Diario',
      body: 'Acá encontrás todos los asientos generados en doble entrada. Filtrá por cuenta, mes o año para analizar períodos específicos.',
      position: 'bottom',
    },
    {
      targetId: 'tour-journal-export',
      title: 'Exportar asientos',
      body: 'Descargá los asientos en el formato de tu sistema contable: Excel para uso general, Holistor o Bejerman para importación directa, CSV genérico para cualquier otro sistema.',
      position: 'bottom',
    },
  ],

  '/chart-of-accounts': [
    {
      targetId: 'tour-coa-header',
      title: 'Plan de Cuentas',
      body: 'El plan de cuentas define todas las cuentas contables disponibles para clasificar movimientos. Hay cuentas globales del sistema (protegidas) y cuentas propias que podés crear libremente.',
      position: 'bottom',
    },
    {
      targetId: 'tour-coa-new-btn',
      title: 'Crear una cuenta nueva',
      body: 'Las cuentas propias son las que usarás en tus reglas de clasificación. Creá acá las cuentas específicas de tu estudio antes de configurar reglas — sin cuenta no hay regla posible.',
      position: 'bottom',
    },
    {
      targetId: 'tour-coa-table',
      title: 'Cuentas del sistema y propias',
      body: 'El ícono de globo indica cuentas globales (no editables). El ícono de edificio indica cuentas propias del estudio. El código externo sirve para integrar con Holistor, Bejerman o Tango.',
      position: 'top',
    },
  ],
};

const STORAGE_KEY = 'contableai_tour_done';

@Injectable({ providedIn: 'root' })
export class TourService {
  private _step  = signal(-1);
  private _steps = signal<TourStep[]>([]);

  readonly currentStep = this._step.asReadonly();
  readonly isActive    = computed(() => this._step() >= 0);
  readonly step        = computed(() => this._steps()[this._step()] ?? null);
  readonly isFirst     = computed(() => this._step() === 0);
  readonly isLast      = computed(() => this._step() === this._steps().length - 1);
  readonly progress    = computed(() => `${this._step() + 1} de ${this._steps().length}`);
  readonly pct         = computed(() => ((this._step() + 1) / this._steps().length) * 100);

  get wasDone(): boolean {
    return localStorage.getItem(STORAGE_KEY) === 'true';
  }

  /** Start the tour for the given route (or default reconciliation tour). */
  start(route?: string): void {
    const key   = route ?? '/';
    const steps = TOURS[key] ?? TOURS['/'];
    if (steps.length === 0) return;
    this._steps.set(steps);
    this._step.set(0);
  }

  /** Returns true if a tour is defined for this route. */
  hasTour(route: string): boolean {
    return !!TOURS[route]?.length;
  }

  next(): void {
    if (this._step() < this._steps().length - 1) {
      this._step.update(s => s + 1);
    } else {
      this.stop();
    }
  }

  prev(): void {
    if (this._step() > 0) {
      this._step.update(s => s - 1);
    }
  }

  stop(): void {
    this._step.set(-1);
    this._steps.set([]);
    localStorage.setItem(STORAGE_KEY, 'true');
  }
}
