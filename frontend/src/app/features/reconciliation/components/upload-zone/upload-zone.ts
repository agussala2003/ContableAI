import { Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { BankAccount, BankAccountService } from '../../../../core/services/bank-account.service';

/** Valor del selector que deja decidir la cuenta al OCR. */
export const AUTO_BANK_ACCOUNT = '';

@Component({
  selector: 'app-upload-zone',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './upload-zone.html',
})
export class UploadZone {

  isLoading = input<boolean>(false);
  companyId = input<string | undefined>(undefined);
  fileDropped = output<{
    files: File[];
    bankCode: string;
    companyId?: string;
    withoutDateFilter: boolean;
    bankAccountId?: string;
  }>();

  private fileInput = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');

  private bankAccountService = inject(BankAccountService);

  /** Cuentas ofrecidas en el selector. El estado lo mantiene el servicio (ver refresh()). */
  readonly bankAccounts = this.bankAccountService.activeAccounts;

  constructor() {
    // El catálogo de bancos alimenta la etiqueta de cada cuenta; se pide una sola vez por sesión.
    this.bankAccountService.loadBankCodes();
  }

  /**
   * Etiqueta de la cuenta en el selector de destino: "Banco Santander · CC Pesos (ARS)".
   *
   * El banco va adelante porque con tres bancos en la misma empresa los alias se repiten —"CC
   * Pesos" existe en todos— y elegir mal manda el extracto a la cuenta equivocada. Las cuentas sin
   * banco cargado muestran solo el alias, sin separador huérfano.
   */
  accountLabel(account: BankAccount): string {
    const bank = this.bankAccountService.bankLabel(account.bankCode);
    const name = bank ? `${bank} · ${account.alias}` : account.alias;
    return `${name} (${account.currency})`;
  }

  selectedFiles: File[] = [];
  isDragging        = signal(false);
  withoutDateFilter = signal(false);
  showAdvanced      = signal(false);

  /**
   * Cuenta elegida a mano. Vacío = "Automático": el backend la deduce del encabezado del extracto.
   * Una elección explícita tiene precedencia sobre el OCR y es, además, la salida para los
   * resúmenes consolidados que el backend rechaza por identificar más de una cuenta.
   */
  selectedBankAccountId = signal<string>(AUTO_BANK_ACCOUNT);

  removeFile(index: number) {
    this.selectedFiles = this.selectedFiles.filter((_, i) => i !== index);
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    this.addFiles(Array.from(input.files ?? []));
    // Permite volver a elegir el mismo archivo (p. ej. tras quitarlo de la lista):
    // sin esto el <input> no dispara "change" si la selección nativa no cambió.
    input.value = '';
  }

  /** Acumula archivos a la selección actual, ignorando los que ya están en la lista. */
  private addFiles(files: File[]) {
    if (!files.length) return;
    const merged = [...this.selectedFiles];
    for (const file of files) {
      if (!merged.some(f => f.name === file.name)) merged.push(file);
    }
    this.selectedFiles = merged;
  }

  onUploadClick() {
    if (this.selectedFiles.length) {
      // 'AUTO' le indica al backend que detecte el banco automáticamente del contenido
      this.fileDropped.emit({
        files: this.selectedFiles,
        bankCode: 'AUTO',
        companyId: this.companyId(),
        withoutDateFilter: this.withoutDateFilter(),
        bankAccountId: this.selectedBankAccountId() || undefined,
      });
    }
  }

  openFilePicker() {
    this.fileInput().nativeElement.click();
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent) {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    event.stopPropagation();
    this.isDragging.set(false);
    this.addFiles(Array.from(event.dataTransfer?.files ?? []));
  }
}
