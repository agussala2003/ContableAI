import { Component, inject, input, output, signal } from '@angular/core';
import { AfipMatchResponse, Transaction } from '../../../../core/services/transaction';
import { ToastService } from '../../../../core/services/toast.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-afip-zone',
  standalone: true,
  templateUrl: './afip-zone.html',
  imports: [LucideAngularModule],
})
export class AfipZone {

  private txService = inject(Transaction);
  private toast = inject(ToastService);

  companyId = input<string | undefined>(undefined);
  pendingCount = input<number>(0);
  matchComplete = output<void>();

  isLoading = signal(false);
  lastResult = signal<AfipMatchResponse | null>(null);
  selectedFiles = signal<File[]>([]);
  isDragging = signal(false);

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

  runMatch() {
    const files = this.selectedFiles();
    if (!files.length) { this.toast.error('Seleccioná al menos un PDF de AFIP primero.'); return; }

    this.isLoading.set(true);
    this.lastResult.set(null);

    this.txService.matchAfip(files, this.companyId()).subscribe({
      next: (result) => {
        this.lastResult.set(result);
        this.isLoading.set(false);
        this.matchComplete.emit();
        if (result.successfulMatches > 0) {
          this.toast.success(`¡${result.successfulMatches} cruce${result.successfulMatches > 1 ? 's' : ''} encontrado${result.successfulMatches > 1 ? 's' : ''}! Las banderas violetas desaparecieron.`);
        } else {
          this.toast.warning('No se encontraron cruces. Verificá los montos y fechas de los PDFs de AFIP.');
        }
      },
      error: () => {
        this.isLoading.set(false);
        this.toast.error('Error al procesar los archivos de AFIP.');
      },
    });
  }
}
