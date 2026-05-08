import { Component, input, output, signal, viewChild, ElementRef } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-upload-zone',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './upload-zone.html',
})
export class UploadZone {

  isLoading = input<boolean>(false);
  companyId = input<string | undefined>(undefined);
  fileDropped = output<{ files: File[]; bankCode: string; companyId?: string; withoutDateFilter: boolean }>();

  private fileInput = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');

  selectedFiles: File[] = [];
  isDragging        = signal(false);
  withoutDateFilter = signal(false);
  showAdvanced      = signal(false);

  removeFile(index: number) {
    this.selectedFiles = this.selectedFiles.filter((_, i) => i !== index);
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    if (files.length) this.selectedFiles = files;
  }

  onUploadClick() {
    if (this.selectedFiles.length) {
      // 'AUTO' le indica al backend que detecte el banco automáticamente del contenido
      this.fileDropped.emit({
        files: this.selectedFiles,
        bankCode: 'AUTO',
        companyId: this.companyId(),
        withoutDateFilter: this.withoutDateFilter(),
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
    const files = Array.from(event.dataTransfer?.files ?? []);
    if (files.length) this.selectedFiles = files;
  }
}
