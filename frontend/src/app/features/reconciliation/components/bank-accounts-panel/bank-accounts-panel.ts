import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';
import { BankAccount, BankAccountService, SaveBankAccountRequest } from '../../../../core/services/bank-account.service';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';
import { AccountCombobox } from '../../../../shared/components/account-combobox/account-combobox';
import { Currency } from '../../../../core/services/transaction';

/**
 * ABM de las cuentas bancarias de una empresa (F1 — multi-cuenta). Reemplaza a los dos inputs
 * sueltos de contrapartida que vivían en la ficha de empresa.
 */
@Component({
  selector: 'app-bank-accounts-panel',
  standalone: true,
  imports: [ReactiveFormsModule, LucideAngularModule, AccountCombobox],
  templateUrl: './bank-accounts-panel.html',
})
export class BankAccountsPanel {
  companyId = input.required<string>();

  private bankAccountService = inject(BankAccountService);
  private fb                 = inject(FormBuilder);
  private toast              = inject(ToastService);
  private confirmDialog      = inject(ConfirmDialogService);
  private readonly destroyRef = inject(DestroyRef);

  chartOfAccountService = inject(ChartOfAccountService);

  accounts   = signal<BankAccount[]>([]);
  isLoading  = signal(false);
  isSaving   = signal(false);
  busyId     = signal<string | null>(null);

  /** null = panel en modo lista; '' = alta; id = edición de esa cuenta. */
  editingId  = signal<string | null>(null);
  formOpen   = signal(false);

  readonly currencies: Currency[] = ['ARS', 'USD'];
  /** Catálogo servido por el backend. Antes estaba hardcodeado acá y le faltaba Santander. */
  readonly bankCodes = this.bankAccountService.bankCodes;

  form = this.fb.group({
    alias:             ['', [Validators.required, Validators.maxLength(120)]],
    accountNumber:     ['', [Validators.maxLength(60)]],
    cbu:               ['', [Validators.maxLength(30)]],
    bankCode:          [''],
    currency:          ['ARS' as Currency, [Validators.required]],
    contraAccountName: ['', [Validators.maxLength(120)]],
  });

  constructor() {
    this.bankAccountService.loadBankCodes();

    effect(() => {
      const id = this.companyId();
      if (id) this.load(id);
    });
  }

  load(companyId: string): void {
    this.isLoading.set(true);
    // includeInactive: la baja es lógica, así que la lista muestra también las dadas de baja
    // para poder reactivarlas — si no, quedarían inalcanzables.
    this.bankAccountService.list(companyId, true).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.accounts.set(list);
        this.isLoading.set(false);
      },
      error: () => {
        this.toast.error('No se pudieron cargar las cuentas bancarias.');
        this.isLoading.set(false);
      },
    });
  }

  openCreate(): void {
    this.editingId.set('');
    this.form.reset({ alias: '', accountNumber: '', cbu: '', bankCode: '', currency: 'ARS', contraAccountName: '' });
    this.formOpen.set(true);
  }

  openEdit(account: BankAccount): void {
    this.editingId.set(account.id);
    this.form.reset({
      alias:             account.alias,
      accountNumber:     account.accountNumber ?? '',
      cbu:               account.cbu ?? '',
      bankCode:          account.bankCode ?? '',
      currency:          account.currency,
      contraAccountName: account.contraAccountName,
    });
    this.formOpen.set(true);
  }

  closeForm(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
  }

  onContraAccountSelected(value: string): void {
    this.form.patchValue({ contraAccountName: value });
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    const req: SaveBankAccountRequest = {
      alias:             v.alias!.trim(),
      accountNumber:     (v.accountNumber ?? '').trim() || null,
      cbu:               (v.cbu ?? '').trim() || null,
      bankCode:          (v.bankCode ?? '').trim() || null,
      currency:          v.currency as Currency,
      contraAccountName: (v.contraAccountName ?? '').trim() || null,
    };

    this.isSaving.set(true);
    const editingId = this.editingId();

    const request$ = editingId
      ? this.bankAccountService.update(editingId, req)
      : this.bankAccountService.create(this.companyId(), req);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: saved => {
        this.accounts.update(list => editingId
          ? list.map(a => a.id === saved.id ? saved : a)
          : [...list, saved]);
        this.isSaving.set(false);
        this.closeForm();
        // Mantiene en sintonía el selector de la Dropzone, que vive en otra pantalla.
        this.bankAccountService.refresh(this.companyId());
        this.toast.success(editingId ? 'Cuenta actualizada.' : 'Cuenta bancaria creada.');
      },
      error: err => {
        this.isSaving.set(false);
        this.toast.error(this.errorMessage(err, 'No se pudo guardar la cuenta bancaria.'));
      },
    });
  }

  async toggleStatus(account: BankAccount): Promise<void> {
    if (account.isActive) {
      const ok = await this.confirmDialog.confirm({
        title: `¿Dar de baja "${account.alias}"?`,
        message: 'Los movimientos y asientos ya cargados se conservan; la cuenta deja de ofrecerse para nuevas cargas.',
        confirmLabel: 'Dar de baja',
      });
      if (!ok) return;
    }

    this.busyId.set(account.id);
    const request$ = account.isActive
      ? this.bankAccountService.deactivate(account.id)
      : this.bankAccountService.activate(account.id);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: updated => {
        this.accounts.update(list => list.map(a => a.id === updated.id ? updated : a));
        this.busyId.set(null);
        this.bankAccountService.refresh(this.companyId());
        this.toast.success(updated.isActive ? 'Cuenta reactivada.' : 'Cuenta dada de baja.');
      },
      error: () => {
        this.busyId.set(null);
        this.toast.error('No se pudo cambiar el estado de la cuenta.');
      },
    });
  }

  /** Sin contrapartida la cuenta existe y recibe movimientos, pero no puede generar asientos. */
  isProvisional(account: BankAccount): boolean {
    return !account.contraAccountName?.trim();
  }

  private errorMessage(err: unknown, fallback: string): string {
    const e = err as { status?: number; error?: unknown } | null;
    if (e?.status === 409) return typeof e.error === 'string' ? e.error : 'Ya existe una cuenta con ese número.';
    if (e?.status === 400) return typeof e.error === 'string' ? e.error : fallback;
    return fallback;
  }
}
