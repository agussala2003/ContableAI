import { Component, inject, input, output, signal, effect, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AfipService, AfipVoucher, AfipComboSuggestion } from '../../afip.service';
import { ToastService } from '../../../../core/services/toast.service';
import { SkippedDuplicate } from '../../../../core/services/transaction';
import { LucideAngularModule } from 'lucide-angular';
import { CombinationSuggestionModal } from '../combination-suggestion-modal/combination-suggestion-modal';
import { CurrencyAmountPipe } from '../../../../shared/pipes/currency-amount.pipe';

@Component({
  selector: 'app-afip-zone',
  standalone: true,
  templateUrl: './afip-zone.html',
  imports: [LucideAngularModule, DatePipe, CombinationSuggestionModal, CurrencyAmountPipe],
})
export class AfipZone {

  private afipService = inject(AfipService);
  private toast = inject(ToastService);

  companyId         = input<string | undefined>(undefined);
  uploadComplete    = output<number>();
  skippedDuplicates = output<SkippedDuplicate[]>();
  /** Emitido al aplicar un cruce múltiple: el padre debe refrescar la grilla de movimientos. */
  combinationApplied = output<void>();

  isLoading     = signal(false);
  isLoadingList = signal(false);
  isRematching  = signal(false);
  vouchers      = signal<AfipVoucher[]>([]);
  selectedFiles = signal<File[]>([]);
  isDragging    = signal(false);

  comboSuggestions     = signal<AfipComboSuggestion[]>([]);
  showSuggestionsModal = signal(false);

  pendingCount = computed(() => this.vouchers().filter(v => !v.isMatched).length);
  matchedCount = computed(() => this.vouchers().filter(v => v.isMatched).length);

  constructor() {
    effect(() => {
      const id = this.companyId();
      if (id) this.loadVouchers(id);
    });
  }

  loadVouchers(companyId: string) {
    this.isLoadingList.set(true);
    this.afipService.getVouchers(companyId).subscribe({
      next: (v) => { this.vouchers.set(v); this.isLoadingList.set(false); },
      error: () => this.isLoadingList.set(false),
    });
    this.loadComboSuggestions(companyId);
  }

  loadComboSuggestions(companyId: string) {
    this.afipService.getCombinationSuggestions(companyId).subscribe({
      next: (s) => this.comboSuggestions.set(s),
      error: () => this.comboSuggestions.set([]),
    });
  }

  onCombinationApplied() {
    const id = this.companyId();
    if (id) this.loadVouchers(id);
    this.combinationApplied.emit();
  }

  onSuggestionsModalClosed() {
    this.showSuggestionsModal.set(false);
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    this.addFiles(Array.from(input.files ?? []));
    // Permite volver a elegir el mismo archivo (p. ej. tras quitarlo de la lista):
    // sin esto el <input> no dispara "change" si la selección nativa no cambió.
    input.value = '';
  }

  /**
   * Acumula archivos a la selección actual (misma ergonomía que la dropzone
   * de extractos, FL-1), avisando cuáles se omitieron por nombre duplicado.
   */
  private addFiles(files: File[]) {
    const pdfs = files.filter(f => f.type === 'application/pdf' || f.name.toLowerCase().endsWith('.pdf'));
    if (files.length && !pdfs.length) {
      this.toast.warning('Solo se aceptan archivos PDF de AFIP.');
      return;
    }

    const merged = [...this.selectedFiles()];
    const skipped: string[] = [];
    for (const file of pdfs) {
      if (merged.some(f => f.name === file.name)) skipped.push(file.name);
      else merged.push(file);
    }
    this.selectedFiles.set(merged);

    if (skipped.length === 1) {
      this.toast.warning(`"${skipped[0]}" ya estaba en la lista y no se volvió a agregar.`);
    } else if (skipped.length > 1) {
      this.toast.warning(`${skipped.length} archivos ya estaban en la lista y no se volvieron a agregar.`);
    }
  }

  removeFile(index: number) {
    this.selectedFiles.set(this.selectedFiles().filter((_, i) => i !== index));
  }

  onDragOver(e: DragEvent) { e.preventDefault(); this.isDragging.set(true); }
  onDragLeave(e: DragEvent) { e.preventDefault(); this.isDragging.set(false); }
  onDrop(e: DragEvent) {
    e.preventDefault();
    this.isDragging.set(false);
    this.addFiles(Array.from(e.dataTransfer?.files ?? []));
  }

  triggerRematch() {
    const id = this.companyId();
    if (!id) return;
    this.isRematching.set(true);
    this.afipService.triggerRematch(id).subscribe({
      next: () => {
        this.toast.success('Cruce iniciado en segundo plano. Recargá en unos instantes.');
        this.isRematching.set(false);
        setTimeout(() => this.loadVouchers(id), 3000);
      },
      error: () => {
        this.toast.error('No se pudo iniciar el cruce.');
        this.isRematching.set(false);
      },
    });
  }

  runUpload() {
    const files = this.selectedFiles();
    if (!files.length) { this.toast.error('Seleccioná al menos un PDF de AFIP primero.'); return; }
    const id = this.companyId();
    if (!id) return;

    this.isLoading.set(true);

    this.afipService.uploadVouchers(id, files).subscribe({
      next: (result) => {
        this.isLoading.set(false);
        this.selectedFiles.set([]);
        this.uploadComplete.emit(result.added);
        this.loadVouchers(id);

        if (result.added > 0) {
          this.toast.success(`¡${result.added} comprobante${result.added > 1 ? 's' : ''} cargado${result.added > 1 ? 's' : ''}! El cruce se procesa en segundo plano.`);
        } else if (!result.skippedDuplicates?.length) {
          this.toast.info('No se encontraron comprobantes válidos en los archivos subidos.');
        }

        if (result.skippedDuplicates?.length) {
          this.skippedDuplicates.emit(result.skippedDuplicates);
        }
      },
      error: () => {
        this.isLoading.set(false);
        this.toast.error('Error al subir los archivos de AFIP.');
      },
    });
  }
}
