import { Component, inject, signal, OnDestroy } from '@angular/core';
import { ConfirmDialogService } from '../../services/confirm-dialog.service';
import { LucideAngularModule } from 'lucide-angular';
import { ModalA11yDirective } from '../../../shared/directives/modal-a11y.directive';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [LucideAngularModule, ModalA11yDirective],
  templateUrl: './confirm-dialog.component.html',
})
export class ConfirmDialogComponent implements OnDestroy {
  protected svc       = inject(ConfirmDialogService);
  protected isLeaving = signal(false);

  private closeTimer?: ReturnType<typeof setTimeout>;

  ngOnDestroy(): void {
    clearTimeout(this.closeTimer);
  }

  private close(accepted: boolean) {
    this.isLeaving.set(true);
    this.closeTimer = setTimeout(() => {
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
