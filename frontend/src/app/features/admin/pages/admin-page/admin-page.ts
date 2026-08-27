import { Component, inject, signal, computed, DestroyRef } from '@angular/core';
import { DecimalPipe, DatePipe, NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AdminService, AdminStats, AdminUserRow } from '../../../../core/services/admin.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';

@Component({
  selector: 'app-admin-page',
  standalone: true,
  imports: [DecimalPipe, DatePipe, NgClass, LucideAngularModule, FormsModule],
  templateUrl: './admin-page.html',
})
export class AdminPage {

  private adminService = inject(AdminService);
  private toast = inject(ToastService);
  private confirmDialog = inject(ConfirmDialogService);
  private readonly destroyRef = inject(DestroyRef);

  stats = signal<AdminStats | null>(null);
  users = signal<AdminUserRow[]>([]);
  isLoading = signal(false);
  isResetting = signal(false);
  isNormalizing = signal(false);
  actionInProgress = signal<string | null>(null);

  readonly plans = ['Free', 'Pro', 'Enterprise'];

  // ── Carga de saldo prepago ──────────────────────────────────────────────
  // El saldo pertenece al ESTUDIO (StudioTenantId), no al usuario. La fila desde la que se abre
  // el modal solo sirve para identificar el estudio: acreditar desde cualquier usuario del mismo
  // estudio tiene idéntico efecto, y el modal lo dice para que no parezca un error.

  /** Packs comerciales vigentes. Precargados para no tipear el número más usado. */
  readonly packs = [
    { amount: 20,  label: 'Básico'  },
    { amount: 50,  label: 'Estudio' },
    { amount: 150, label: 'Volumen' },
  ];

  topUpTarget    = signal<AdminUserRow | null>(null);
  topUpAmount    = signal(50);
  topUpReference = signal('');
  isToppingUp    = signal(false);

  /** Sin comprobante no hay idempotencia, así que el botón no se habilita. */
  canConfirmTopUp = computed(() =>
    this.topUpAmount() > 0 && this.topUpReference().trim().length > 0
  );

  openTopUp(user: AdminUserRow): void {
    this.topUpTarget.set(user);
    this.topUpAmount.set(50);
    this.topUpReference.set('');
  }

  closeTopUp(): void {
    if (this.isToppingUp()) return;
    this.topUpTarget.set(null);
  }

  confirmTopUp(): void {
    const target = this.topUpTarget();
    if (!target || !this.canConfirmTopUp()) return;

    this.isToppingUp.set(true);
    this.adminService
      .topUpQuota(target.studioTenantId, this.topUpAmount(), this.topUpReference().trim())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: res => {
          this.isToppingUp.set(false);
          this.topUpTarget.set(null);

          // `applied: false` NO es un error: es el comprobante repetido. Se avisa distinto para
          // que el admin no crea que acreditó dos packs cuando en realidad acreditó uno.
          if (res.applied) this.toast.success(res.message);
          else             this.toast.warning(res.message);

          this.reload();
        },
        error: err => {
          this.isToppingUp.set(false);
          this.toast.error(err?.error?.message ?? 'No se pudo acreditar el saldo.');
        },
      });
  }

  readonly statusLabel: Record<number, string> = { 0: 'Pendiente', 1: 'Activo', 2: 'Suspendido' };
  readonly statusClass: Record<number, string> = {
    0: 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300',
    1: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300',
    2: 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300',
  };

  constructor() {
    this.reload();
  }

  reload(): void {
    this.isLoading.set(true);
    this.adminService.getStats().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: stats => { this.stats.set(stats); this.loadUsers(); },
      error: () => { this.isLoading.set(false); this.toast.error('No se pudieron cargar las métricas.'); },
    });
  }

  private loadUsers(): void {
    this.adminService.getUsers().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: users => { this.users.set(users); this.isLoading.set(false); },
      error: () => { this.isLoading.set(false); this.toast.error('No se pudo cargar el registro de usuarios.'); },
    });
  }

  activate(user: AdminUserRow): void {
    this.actionInProgress.set(user.id);
    this.adminService.activateUser(user.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.toast.success(`${user.email} activado.`); this.reload(); },
      error: () => { this.actionInProgress.set(null); this.toast.error('No se pudo activar el usuario.'); },
    });
  }

  suspend(user: AdminUserRow): void {
    this.actionInProgress.set(user.id);
    this.adminService.suspendUser(user.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.toast.success(`${user.email} suspendido.`); this.reload(); },
      error: () => { this.actionInProgress.set(null); this.toast.error('No se pudo suspender el usuario.'); },
    });
  }

  changePlan(user: AdminUserRow, plan: string): void {
    if (plan === user.plan) return;
    this.actionInProgress.set(user.id + '-plan');
    this.adminService.updatePlan(user.id, plan).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.toast.success(`Plan de ${user.email} cambiado a ${plan}.`); this.reload(); },
      error: () => { this.actionInProgress.set(null); this.toast.error('No se pudo cambiar el plan.'); },
    });
  }

  async deleteUser(user: AdminUserRow): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: `¿Eliminar a ${user.displayName}?`,
      message: `Se borrarán todos los datos del estudio de ${user.email}. Esta acción es irreversible.`,
      confirmLabel: 'Sí, eliminar',
    });
    if (!ok) return;

    this.actionInProgress.set(user.id + '-delete');
    this.adminService.deleteUser(user.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.toast.success(`Usuario ${user.email} eliminado.`); this.reload(); },
      error: () => { this.actionInProgress.set(null); this.toast.error('No se pudo eliminar el usuario.'); },
    });
  }

  async normalizeAccounts(): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: '¿Normalizar cuentas de los movimientos?',
      message: 'Reescribe las cuentas con mayúsculas/minúsculas mezcladas a su forma canónica del plan. Es seguro e idempotente; no toca reglas ni asientos.',
      confirmLabel: 'Sí, normalizar',
    });
    if (!ok) return;

    this.isNormalizing.set(true);
    this.adminService.normalizeAccounts().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (res) => {
        this.isNormalizing.set(false);
        this.toast.success(`${res.transactionsUpdated} movimiento(s) normalizado(s) de ${res.transactionsScanned} revisados.`);
      },
      error: (err) => {
        this.isNormalizing.set(false);
        this.toast.error(err?.error?.detail ?? err?.error?.message ?? 'No se pudo normalizar las cuentas.');
      },
    });
  }

  async resetDatabase(): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: '¿Vaciar base de datos?',
      message: 'Esta acción elimina datos operativos y solo debería usarse en entorno de desarrollo.',
      confirmLabel: 'Sí, vaciar BD',
    });
    if (!ok) return;

    this.isResetting.set(true);
    this.adminService.resetDatabase().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (res) => { this.isResetting.set(false); this.toast.success(res.message); this.reload(); },
      error: (err) => {
        this.isResetting.set(false);
        this.toast.error(err?.error?.detail ?? err?.error?.message ?? 'No se pudo vaciar la base de datos.');
      },
    });
  }
}
