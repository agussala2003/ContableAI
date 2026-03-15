import { Component, input, output } from '@angular/core';
import { UploadZone } from '../upload-zone/upload-zone';
import { AfipZone } from '../afip-zone/afip-zone';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-upload-modal',
  standalone: true,
  imports: [UploadZone, AfipZone, LucideAngularModule],
  templateUrl: './upload-modal.html',
})
export class UploadModal {
  isLoading    = input<boolean>(false);
  companyId    = input<string | undefined>(undefined);
  pendingCount = input<number>(0);

  close         = output<void>();
  fileDropped   = output<{ files: File[]; bankCode: string; companyId?: string }>();
  matchComplete = output<void>();
}
