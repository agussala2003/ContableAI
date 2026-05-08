import { Component, inject, input, output, signal, effect, computed } from '@angular/core';
import { DecimalPipe, DatePipe } from '@angular/common';
import { AfipService, AfipVoucher } from '../../afip.service';
import { ToastService } from '../../../../core/services/toast.service';
import { SkippedDuplicate } from '../../../../core/services/transaction';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-afip-zone',
  standalone: true,
  templateUrl: './afip-zone.html',
  imports: [LucideAngularModule, DecimalPipe, DatePipe],
})
export class AfipZone {

  private afipService = inject(AfipService);
  private toast = inject(ToastService);

  companyId         = input<string | undefined>(undefined);
  uploadComplete    = output<number>();
  skippedDuplicates = output<SkippedDuplicate[]>();

  isLoading     = signal(false);
  isLoadingList = signal(false);
  isRematching  = signal(false);
  vouchers      = signal<AfipVoucher[]>([]);
  selectedFiles = signal<File[]>([]);
  isDragging    = signal(false);

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
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    if (files.length) this.selectedFiles.set(files);
  }

  removeFile(index: number) {
    this.selectedFiles.set(this.selectedFiles().filter((_, i) => i !== index));
  }

  onDragOver(e: DragEvent) { e.preventDefault(); this.isDragging.set(true); }
  onDragLeave(e: DragEvent) { e.preventDefault(); this.isDragging.set(false); }
  onDrop(e: DragEvent) {
    e.preventDefault();
    this.isDragging.set(false);
    const files = Array.from(e.dataTransfer?.files ?? []).filter(f => f.type === 'application/pdf' || f.name.toLowerCase().endsWith('.pdf'));
    if (files.length) this.selectedFiles.set(files);
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
