import { Component, inject, signal, OnInit } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators, AbstractControl } from '@angular/forms';
import { NgClass } from '@angular/common';
import { Company, CompanyService, CreateCompanyRequest, UpdateCompanyRequest } from '../../../../core/services/company.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-company-bar',
  standalone: true,
  imports: [ReactiveFormsModule, NgClass, LucideAngularModule],
  templateUrl: './company-bar.html',
})
export class CompanyBar implements OnInit {

  companyService  = inject(CompanyService);
  private fb      = inject(FormBuilder);
  private toast   = inject(ToastService);
  private confirmDialog = inject(ConfirmDialogService);

  showNewForm = signal(false);
  isSaving = signal(false);
  editingCompany = signal<Company | null>(null);

  createSubmitted = signal(false);
  editSubmitted = signal(false);

  readonly cuitRegex = /^\d{2}-\d{8}-\d{1}$/;

  createForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    cuit: ['', [Validators.required, Validators.pattern(this.cuitRegex)]],
    businessType: ['GENERAL', [Validators.required]],
    bankAccountName: ['', [Validators.maxLength(120)]],
  });

  editForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    businessType: ['GENERAL', [Validators.required]],
    splitChequeTax: [false],
    bankAccountName: ['', [Validators.maxLength(120)]],
  });

  readonly businessTypes = ['GENERAL', 'RESTAURANTE', 'PELUQUERIA', 'COMERCIO', 'SERVICIO'];

  ngOnInit() {
    this.companyService.loadCompanies().subscribe({
      error: () => this.toast.error('No se pudieron cargar las empresas.'),
    });
  }

  selectCompany(company: Company) {
    this.companyService.selectCompany(company);
    this.editingCompany.set(null);
  }

  // ── Create ──────────────────────────────────────────────────────────────

  saveCompany() {
    this.createSubmitted.set(true);
    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();
      return;
    }

    const formValue = this.createForm.getRawValue();
    this.isSaving.set(true);
    const req: CreateCompanyRequest = {
      name: formValue.name!.trim(),
      cuit: formValue.cuit!.trim(),
      businessType: formValue.businessType ?? undefined,
      bankAccountName: (formValue.bankAccountName ?? '').trim() || undefined,
    };
    this.companyService.createCompany(req).subscribe({
      next: (company) => {
        this.toast.success(`Empresa "${company.name}" creada y seleccionada.`);
        this.showNewForm.set(false);
        this.createSubmitted.set(false);
        this.createForm.reset({
          name: '',
          cuit: '',
          businessType: 'GENERAL',
          bankAccountName: '',
        });
        this.isSaving.set(false);
      },
      error: (err) => {
        const msg = err.status === 409
          ? `El CUIT ${req.cuit} ya está registrado.`
          : 'Error al guardar la empresa.';
        this.toast.error(msg);
        this.isSaving.set(false);
      },
    });
  }

  cancelNew() {
    this.showNewForm.set(false);
    this.createSubmitted.set(false);
    this.createForm.reset({
      name: '',
      cuit: '',
      businessType: 'GENERAL',
      bankAccountName: '',
    });
  }

  // ── Edit ────────────────────────────────────────────────────────────────

  startEdit(company: Company, event: Event) {
    event.stopPropagation();
    this.showNewForm.set(false);
    this.editSubmitted.set(false);
    this.editForm.reset({
      name: company.name,
      businessType: company.businessType,
      splitChequeTax: company.splitChequeTax ?? false,
      bankAccountName: company.bankAccountName ?? '',
    });
    this.editingCompany.set(company);
  }

  cancelEdit() {
    this.editSubmitted.set(false);
    this.editingCompany.set(null);
  }

  saveEdit() {
    const company = this.editingCompany();
    if (!company) return;

    this.editSubmitted.set(true);
    if (this.editForm.invalid) {
      this.editForm.markAllAsTouched();
      return;
    }

    const formValue = this.editForm.getRawValue();
    this.isSaving.set(true);
    const req: UpdateCompanyRequest = {
      name: formValue.name!.trim(),
      businessType: formValue.businessType ?? undefined,
      splitChequeTax: formValue.splitChequeTax ?? false,
      bankAccountName: (formValue.bankAccountName ?? '').trim(),
    };
    this.companyService.updateCompany(company.id, req).subscribe({
      next: (updated) => {
        this.toast.success(`Empresa "${updated.name}" actualizada.`);
        this.editSubmitted.set(false);
        this.editingCompany.set(null);
        this.isSaving.set(false);
      },
      error: (err) => {
        const detail = err?.error?.detail ?? err?.error?.title;
        this.toast.error(detail ?? 'Error al actualizar la empresa.');
        this.isSaving.set(false);
      },
    });
  }

  // ── Delete ──────────────────────────────────────────────────────────────

  async deleteCompany(company: Company, event: Event) {
    event.stopPropagation();
    const ok = await this.confirmDialog.confirm({
      title: `¿Eliminar "${company.name}"?`,
      message: 'Los movimientos históricos se conservan, pero la empresa dejará de aparecer.',
      confirmLabel: 'Eliminar',
    });
    if (!ok) return;
    this.companyService.deleteCompany(company.id).subscribe({
      next: () => this.toast.success(`Empresa "${company.name}" eliminada.`),
      error: () => this.toast.error('Error al eliminar la empresa.'),
    });
  }

  controlHasError(controlName: string, errorKey: string, formType: 'create' | 'edit'): boolean {
    const submitted = formType === 'create' ? this.createSubmitted() : this.editSubmitted();
    const control = this.getControl(formType, controlName);
    if (!control) return false;
    return control.hasError(errorKey) && (control.touched || submitted);
  }

  controlErrorMessage(controlName: string, formType: 'create' | 'edit'): string | null {
    const submitted = formType === 'create' ? this.createSubmitted() : this.editSubmitted();
    const control = this.getControl(formType, controlName);
    if (!control || (!control.touched && !submitted)) return null;

    if (control.hasError('required')) {
      return controlName === 'cuit' ? 'El CUIT es obligatorio.' : 'Este campo es obligatorio.';
    }
    if (control.hasError('pattern') && controlName === 'cuit') {
      return 'El formato del CUIT debe ser 30-12345678-9.';
    }
    if (control.hasError('maxlength')) {
      return 'El valor ingresado es demasiado largo.';
    }
    return null;
  }

  private getControl(formType: 'create' | 'edit', controlName: string): AbstractControl | null {
    if (formType === 'create') {
      return this.createForm.get(controlName);
    }
    return this.editForm.get(controlName);
  }
}
