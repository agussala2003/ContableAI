import { Component, inject, signal } from '@angular/core';
import { ToastService, ToastType } from '../../services/toast.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-toast',
  standalone: true,
  imports: [LucideAngularModule],
  host: {
    class:
      'fixed bottom-6 right-6 z-[9999] flex flex-col gap-3 items-end pointer-events-none',
  },
  templateUrl: './toast.component.html',
})
export class ToastComponent {
  protected toastService = inject(ToastService);
  protected dismissingIds = signal<Set<number>>(new Set());

  dismiss(id: number): void {
    // Flag as leaving so exit animation starts
    this.dismissingIds.update(s => new Set([...s, id]));
    // Remove from service after animation completes (220ms matches slide-out-right)
    setTimeout(() => {
      this.toastService.dismiss(id);
      this.dismissingIds.update(s => {
        const next = new Set(s);
        next.delete(id);
        return next;
      });
    }, 240);
  }

  isLeaving(id: number): boolean {
    return this.dismissingIds().has(id);
  }

  variantClasses(type: ToastType): string {
    const base =
      'bg-white dark:bg-slate-800/95 border shadow-xl shadow-black/[0.08] dark:shadow-black/30 text-slate-700 dark:text-slate-200';
    switch (type) {
      case 'success':
        return `${base} border-emerald-200 dark:border-emerald-500/30`;
      case 'warning':
        return `${base} border-amber-200 dark:border-amber-500/30`;
      case 'error':
        return `${base} border-red-200 dark:border-red-500/30`;
    }
  }

  iconColorClass(type: ToastType): string {
    switch (type) {
      case 'success':
        return 'text-emerald-500 dark:text-emerald-400';
      case 'warning':
        return 'text-amber-500 dark:text-amber-400';
      case 'error':
        return 'text-red-500 dark:text-red-400';
    }
  }

  progressColorClass(type: ToastType): string {
    switch (type) {
      case 'success':
        return 'bg-emerald-400 dark:bg-emerald-500';
      case 'warning':
        return 'bg-amber-400 dark:bg-amber-500';
      case 'error':
        return 'bg-red-400 dark:bg-red-500';
    }
  }
}
