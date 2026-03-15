import { Component, inject, signal } from '@angular/core';
import { ConfirmDialogService } from '../../services/confirm-dialog.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './confirm-dialog.component.html',
})
export class ConfirmDialogComponent {
  protected svc      = inject(ConfirmDialogService);
  protected isLeaving = signal(false);

  private close(accepted: boolean) {
    // Kick off exit animation
    this.isLeaving.set(true);
    setTimeout(() => {
      this.isLeaving.set(false);
      accepted ? this.svc.accept() : this.svc.cancel();
    }, 180);
  }

  onConfirm() { this.close(true);  }
  onCancel()  { this.close(false); }

  /** Cerrar al hacer click en el backdrop */
  onBackdropClick(event: MouseEvent) {
    if ((event.target as HTMLElement).dataset['backdrop']) {
      this.onCancel();
    }
  }
}
