import { Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, of, timer } from 'rxjs';
import { catchError, finalize, map, switchMap, take, takeWhile, tap } from 'rxjs/operators';
import { LucideAngularModule } from 'lucide-angular';
import { AccountingRule, ReapplyRuleReport, ReapplyScope, RuleService } from '../../../../core/services/rule.service';
import { ToastService } from '../../../../core/services/toast.service';

/**
 * Reaplicación forzada de una regla sobre los movimientos históricos de su empresa.
 *
 * Encapsula el flujo completo del Epic E —preview con dryRun, confirmación destructiva, encolado
 * del job de Hangfire y polling de su estado— para que lo puedan disparar tanto la pantalla de
 * Reglas como la "regla rápida" de la grilla de conciliación sin duplicar la lógica ni, peor, sin
 * que una de las dos se quede con una versión distinta de las advertencias.
 *
 * Se abre solo con que <c>rule</c> deje de ser null.
 */
@Component({
  selector: 'app-reapply-rule-modal',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './reapply-rule-modal.html',
})
export class ReapplyRuleModal {
  /** Regla a reaplicar. `null` mantiene el modal cerrado. */
  rule = input<AccountingRule | null>(null);

  /** El usuario cerró o canceló; el padre debe volver `rule` a null. */
  closed = output<void>();
  /** El job terminó bien: el padre puede recargar sus datos. */
  completed = output<ReapplyRuleReport>();
  /**
   * Hay o no un job en vuelo. Lo consume la grilla para el spinner de la fila: sin esto el padre
   * solo sabe si el modal está abierto, y el kebab giraba desde que se abría el preview —cuando
   * todavía no hay nada corriendo— hasta que alguien cerrara el modal.
   */
  running = output<boolean>();

  private ruleService = inject(RuleService);
  private toast       = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  preview          = signal<ReapplyRuleReport | null>(null);
  isLoadingPreview = signal(false);
  /** true desde que se confirma hasta que el job de Hangfire termina. */
  isReapplying     = signal(false);
  /** Estado crudo del job mientras se pollea ("Enqueued" | "Processing" | ...). */
  jobState         = signal<string | null>(null);

  /**
   * Alcance elegido. Arranca en "pending" a propósito: es la opción que no pisa nada. La versión
   * destructiva tiene que ser una elección deliberada, no el default que sale por inercia.
   */
  scope = signal<ReapplyScope>('pending');

  /** Cuántas asignaciones manuales se van a perder — dispara el bloque de advertencia. */
  manualCount = computed(() => this.preview()?.manual ?? 0);

  /**
   * Movimientos que quedarían fuera por el alcance elegido. Es lo que convierte al selector en una
   * decisión informada en vez de una adivinanza: el usuario ve cuánto más alcanzaría con Reemplazar.
   */
  outOfScopeCount = computed(() => this.preview()?.skippedOutOfScope ?? 0);

  /**
   * Solo mueve el signal. El recálculo del preview lo dispara el effect del constructor, que
   * depende de `scope` — pedirlo también acá mandaba DOS requests por cada cambio de alcance,
   * justo al endpoint más caro del flujo (recorre todo el historial de la empresa).
   */
  setScope(scope: ReapplyScope): void {
    this.scope.set(scope);
  }

  /** Total de coincidencias que quedan intactas por alguna de las exclusiones. */
  skippedTotal = computed(() => {
    const p = this.preview();
    if (!p) return 0;
    return p.skippedSettled + p.skippedClosedPeriod + p.skippedAfipCombo;
  });

  /** Intervalo y tope del polling del job, mismos valores que el resto de los jobs de la app. */
  private readonly POLL_INTERVAL_MS = 2000;
  private readonly POLL_TIMEOUT_MS  = 5 * 60_000;

  /** Canal del preview: switchMap cancela el cálculo anterior al cambiar el alcance. */
  private readonly previewRequests = new Subject<{ rule: AccountingRule; scope: ReapplyScope }>();

  constructor() {
    this.subscribeToPreviewRequests();

    effect(() => {
      const rule = this.rule();
      // `scope` se lee acá y no dentro de loadPreview a propósito: la dependencia del effect
      // sobre el alcance tiene que ser explícita. El preview mide el impacto de UN alcance
      // concreto, así que cambiarlo obliga a recalcularlo — si no, el modal mostraría el número
      // de la otra opción y el usuario confirmaría sobre un dato que no corresponde.
      const scope = this.scope();
      if (rule) this.loadPreview(rule, scope);
      else      this.resetState();
    });
  }

  private resetState(): void {
    this.preview.set(null);
    this.isLoadingPreview.set(false);
    this.setRunning(false);
    this.jobState.set(null);
    // Vuelve al alcance seguro: que la elección destructiva de una regla no se herede a la próxima.
    this.scope.set('pending');
  }

  /** Pide el impacto sin escribir: cuántos movimientos se van a sobrescribir y de qué origen. */
  private loadPreview(rule: AccountingRule, scope: ReapplyScope): void {
    this.preview.set(null);
    this.jobState.set(null);
    this.isLoadingPreview.set(true);
    this.previewRequests.next({ rule, scope });
  }

  /**
   * Suscripción única del preview.
   *
   * El switchMap importa acá más que en ningún otro lado: cambiar el alcance dispara un cálculo
   * nuevo, y si el viejo llegaba último el modal mostraba el número del OTRO alcance. El usuario
   * confirmaba una acción destructiva sobre un dato que no correspondía. Además, isLoadingPreview
   * se apaga siempre —haya respuesta o error—, así que el modal nunca queda en "Calculando...".
   */
  private subscribeToPreviewRequests(): void {
    this.previewRequests.pipe(
      switchMap(({ rule, scope }) =>
        this.ruleService.reapplyPreview(rule.id, scope).pipe(
          map(preview => ({ preview, error: null as unknown })),
          catchError(error => of({ preview: null, error })),
        )
      ),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(({ preview, error }) => {
      this.isLoadingPreview.set(false);

      if (error) {
        this.toast.error(this.problemMessage(error, 'No se pudo calcular el impacto de la reaplicación.'));
        this.close();
        return;
      }

      this.preview.set(preview);
    });
  }

  close(): void {
    this.resetState();
    this.closed.emit();
  }

  /**
   * Encola el job y arranca el polling. El modal queda abierto mostrando el progreso: sobre años
   * de historia puede tardar, y cerrarlo dejaría al usuario sin saber si terminó.
   */
  confirm(): void {
    const rule = this.rule();
    if (!rule) return;

    // El reporte se captura ACÁ: si el usuario manda el modal al fondo mientras el job corre,
    // resetState() vacía `preview` y el mensaje final anunciaría "0 movimientos".
    const report = this.preview();

    this.setRunning(true);
    this.jobState.set('Enqueued');

    this.ruleService.reapplyAsync(rule.id, this.scope()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: ({ jobId }) => this.pollJob(jobId, rule, report),
      error: err => {
        this.setRunning(false);
        this.jobState.set(null);
        this.toast.error(this.problemMessage(err, 'No se pudo iniciar la reaplicación.'));
      },
    });
  }

  /**
   * Polling del estado del job. Termina en "Succeeded" o en "Failed"/"Deleted"; si se agota el
   * tope de intentos se avisa sin dar el proceso por perdido, porque el job sigue corriendo del
   * lado del servidor.
   */
  private pollJob(jobId: string, rule: AccountingRule, report: ReapplyRuleReport | null): void {
    const maxPolls = Math.ceil(this.POLL_TIMEOUT_MS / this.POLL_INTERVAL_MS);
    let polls = 0;
    let finished = false;

    timer(this.POLL_INTERVAL_MS, this.POLL_INTERVAL_MS).pipe(
      take(maxPolls),
      tap(() => polls++),
      switchMap(() => this.ruleService.getJobStatus(jobId)),
      takeWhile(status => !this.isFinalJobState(status.state), true),
      // ÚNICA salida del estado "procesando". Antes lo apagaba cada rama por su cuenta y la del
      // error se olvidaba de avisarle al padre: el spinner de la fila —que se apaga con (running)
      // / (closed)— quedaba girando para siempre sobre una regla sin nada en vuelo. finalize
      // corre en éxito, en error, al agotarse el polling y al destruirse el componente.
      finalize(() => {
        this.setRunning(false);
        this.jobState.set(null);
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: status => {
        this.jobState.set(status.state);
        if (!this.isFinalJobState(status.state)) return;

        finished = true;

        if (status.state === 'Succeeded') {
          const updated = report?.totalToUpdate ?? 0;
          this.toast.success(
            `"${rule.keyword}" se reaplicó a ${updated} movimiento${updated !== 1 ? 's' : ''}.`
          );
          // Los movimientos cambiaron: la grilla de conciliación tiene que releerlos.
          this.ruleService.triggerTransactionRefresh();
          if (report) this.completed.emit(report);
        } else {
          this.toast.error('La reaplicación falló. Revisá el estado del trabajo o reintentá.');
        }
        this.close();
      },
      error: () => {
        this.toast.error('Se perdió la conexión mientras se seguía el progreso. El proceso puede haber continuado.');
        // Cerrar también acá: el job sigue del lado del servidor, pero la UI no tiene nada más
        // que mostrar y dejar el modal abierto deja la fila marcada como ocupada.
        this.close();
      },
      complete: () => {
        // Solo se llega acá sin resultado si se agotaron los intentos (al destruir el componente
        // polls < maxPolls, así que no hay toast espurio al navegar a otra pantalla).
        if (!finished && polls >= maxPolls) {
          this.toast.warning('La reaplicación está tardando más de lo normal. Sigue corriendo en segundo plano.');
          this.close();
        }
      },
    });
  }

  /** Único punto que mueve `isReapplying`, para que el padre nunca se entere a destiempo. */
  private setRunning(running: boolean): void {
    this.isReapplying.set(running);
    this.running.emit(running);
  }

  private isFinalJobState(state: string): boolean {
    return state === 'Succeeded' || state === 'Failed' || state === 'Deleted';
  }

  /** El backend devuelve ProblemDetails en los 422 (regla de estudio, regla inexistente). */
  private problemMessage(err: unknown, fallback: string): string {
    const problem = (err as { error?: { detail?: string; title?: string } } | null)?.error;
    return problem?.detail ?? problem?.title ?? fallback;
  }
}
