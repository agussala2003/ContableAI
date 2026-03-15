import { Component, inject, signal } from '@angular/core';
import { DecimalPipe, DatePipe } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';
import { AdminService, AdminStats, AdminUserRow } from '../../../../core/services/admin.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';

@Component({
  selector: 'app-admin-page',
  standalone: true,
  imports: [DecimalPipe, DatePipe, LucideAngularModule],
  templateUrl: './admin-page.html',
})
export class AdminPage {

  private adminService = inject(AdminService);
  private toast = inject(ToastService);
  private confirmDialog = inject(ConfirmDialogService);

  stats = signal<AdminStats | null>(null);
  users = signal<AdminUserRow[]>([]);
  isLoading = signal(false);
  isResetting = signal(false);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.isLoading.set(true);
    this.adminService.getStats().subscribe({
      next: stats => {
        this.stats.set(stats);
        this.loadUsers();
      },
      error: () => {
        this.isLoading.set(false);
        this.toast.error('No se pudieron cargar las métricas de administración.');
      },
    });
  }

  private loadUsers(): void {
    this.adminService.getUsers().subscribe({
      next: users => {
        this.users.set(users);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
        this.toast.error('No se pudo cargar el registro de usuarios.');
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
    this.adminService.resetDatabase().subscribe({
      next: (res) => {
        this.isResetting.set(false);
        this.toast.success(res.message);
        this.reload();
      },
      error: (err) => {
        this.isResetting.set(false);
        const detail: string = err?.error?.detail ?? err?.error?.message ?? 'No se pudo vaciar la base de datos.';
        this.toast.error(detail);
      },
    });
  }
}
