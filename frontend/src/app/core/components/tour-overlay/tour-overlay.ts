import { Component, inject, effect, signal, OnDestroy } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { TourService } from '../../services/tour.service';

@Component({
  selector: 'app-tour-overlay',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './tour-overlay.html',
})
export class TourOverlay implements OnDestroy {
  readonly tour = inject(TourService);

  private highlighted: HTMLElement | null = null;
  readonly showDone = signal(false);

  constructor() {
    effect(() => {
      this.clearHighlight();

      const step = this.tour.step();
      if (!step) { this.showDone.set(false); return; }

      setTimeout(() => {
        const el = document.getElementById(step.targetId);
        if (!el) return;

        el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        el.classList.add('tour-highlight');
        this.highlighted = el;
      }, 80);
    });
  }

  ngOnDestroy(): void {
    this.clearHighlight();
  }

  next(): void {
    if (this.tour.isLast()) {
      this.showDone.set(true);
      this.tour.stop();
    } else {
      this.tour.next();
    }
  }

  private clearHighlight(): void {
    this.highlighted?.classList.remove('tour-highlight');
    this.highlighted = null;
  }
}
