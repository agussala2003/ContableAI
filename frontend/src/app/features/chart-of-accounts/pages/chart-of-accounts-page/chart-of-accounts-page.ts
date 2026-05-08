import { Component, inject, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { LucideAngularModule } from 'lucide-angular';
import { ChartOfAccountService, ChartOfAccountItem } from '../../../../core/services/chart-of-account.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';

@Component({
  selector: 'app-chart-of-accounts-page',
  standalone: true,
  imports: [FormsModule, LucideAngularModule],
  templateUrl: './chart-of-accounts-page.html',
})
export class ChartOfAccountsPage {
  chartService  = inject(ChartOfAccountService);
  toast         = inject(ToastService);
  confirm       = inject(ConfirmDialogService);

  showCreateForm      = signal(false);
  newName             = signal('');
  newExternalCode     = signal('');
  saving              = signal(false);
  importing           = signal(false);
  showInactive        = signal(false);

  editingId           = signal<string | null>(null);
  editValue           = signal('');

  filterText          = signal('');

  filteredAccounts = computed(() => {
    const q = this.filterText().trim().toLowerCase();
    const showAll = this.showInactive();
    return this.chartService.accounts().filter(a => {
      if (!showAll && !a.isActive) return false;
      if (!q) return true;
      return a.name.toLowerCase().includes(q) ||
             (a.externalCode?.toLowerCase().includes(q) ?? false);
    });
  });

  globalCount  = computed(() => this.chartService.accounts().filter(a =>  a.isGlobal && a.isActive).length);
  customCount  = computed(() => this.chartService.accounts().filter(a => !a.isGlobal && a.isActive).length);

  createAccount(): void {
    const name = this.newName().trim();
    if (!name || this.saving()) return;
    this.saving.set(true);
    const externalCode = this.newExternalCode().trim() || undefined;
    this.chartService.create(name, externalCode).subscribe({
      next: (created) => {
        this.chartService.accounts.update(list =>
          [...list, created].sort((a, b) => {
            if (a.isGlobal !== b.isGlobal) return a.isGlobal ? -1 : 1;
            return a.name.localeCompare(b.name, 'es');
          })
        );
        this.chartService.accountNames.update(names =>
          [...names, created.name].sort((a, b) => a.localeCompare(b, 'es'))
        );
        this.newName.set('');
        this.newExternalCode.set('');
        this.showCreateForm.set(false);
        this.saving.set(false);
        this.toast.success(`Cuenta "${created.name}" creada con éxito.`);
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('No se pudo crear la cuenta. El nombre puede estar en uso.');
      },
    });
  }

  cancelCreate(): void {
    this.showCreateForm.set(false);
    this.newName.set('');
    this.newExternalCode.set('');
  }

  startEdit(account: ChartOfAccountItem): void {
    this.editingId.set(account.id);
    this.editValue.set(account.externalCode ?? '');
  }

  cancelEdit(): void {
    this.editingId.set(null);
    this.editValue.set('');
  }

  saveEdit(account: ChartOfAccountItem): void {
    const value = this.editValue().trim() || null;
    this.chartService.updateExternalCode(account.id, value).subscribe({
      next: (updated) => {
        this.chartService.accounts.update(list =>
          list.map(a => a.id === account.id ? { ...a, externalCode: updated.externalCode } : a)
        );
        this.cancelEdit();
        this.toast.success('Código externo actualizado.');
      },
      error: () => {
        this.toast.error('No se pudo guardar el código externo.');
      },
    });
  }

  async deleteAccount(account: ChartOfAccountItem): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Eliminar cuenta contable',
      message: `¿Eliminar "${account.name}"? Esta acción no se puede deshacer.`,
      confirmLabel: 'Eliminar',
    });
    if (!ok) return;
    this.chartService.delete(account.id).subscribe({
      next: () => {
        this.chartService.accounts.update(list => list.filter(a => a.id !== account.id));
        this.chartService.accountNames.update(names => names.filter(n => n !== account.name));
        this.toast.success(`Cuenta "${account.name}" eliminada.`);
      },
      error: (err: HttpErrorResponse) => {
        if (err.status === 409) {
          this.toast.warning(err.error?.detail ?? 'La cuenta está en uso. Podés desactivarla para ocultarla sin perder el historial.');
        } else {
          this.toast.error('No se pudo eliminar la cuenta.');
        }
      },
    });
  }

  deactivateAccount(account: ChartOfAccountItem): void {
    this.chartService.deactivate(account.id).subscribe({
      next: (updated) => {
        this.chartService.accounts.update(list => list.map(a => a.id === account.id ? { ...a, isActive: updated.isActive } : a));
        this.chartService.accountNames.update(names => names.filter(n => n !== account.name));
        this.toast.success(`Cuenta "${account.name}" desactivada.`);
      },
      error: () => this.toast.error('No se pudo desactivar la cuenta.'),
    });
  }

  activateAccount(account: ChartOfAccountItem): void {
    this.chartService.activate(account.id).subscribe({
      next: (updated) => {
        this.chartService.accounts.update(list => list.map(a => a.id === account.id ? { ...a, isActive: updated.isActive } : a));
        this.chartService.accountNames.update(names => [...names, account.name].sort((a, b) => a.localeCompare(b, 'es')));
        this.toast.success(`Cuenta "${account.name}" reactivada.`);
      },
      error: () => this.toast.error('No se pudo reactivar la cuenta.'),
    });
  }

  toggleShowInactive(): void {
    const next = !this.showInactive();
    this.showInactive.set(next);
    // Reload to get inactive accounts if needed
    this.chartService.load(next);
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) return;

    const file = input.files[0];
    this.importing.set(true);

    this.chartService.importExcel(file).subscribe({
      next: (res) => {
        this.toast.success(res.message);
        this.chartService.load(); // Refresh full list
        this.importing.set(false);
        input.value = ''; // Reset input
      },
      error: (err: HttpErrorResponse) => {
        this.importing.set(false);
        input.value = '';
        this.toast.error(err.error?.title || typeof err.error === 'string' ? err.error : 'Ocurrió un error al importar el archivo.');
      }
    });
  }
}
