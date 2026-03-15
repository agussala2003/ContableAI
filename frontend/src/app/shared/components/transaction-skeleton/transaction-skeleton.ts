import { Component } from '@angular/core';

/**
 * Skeleton loader shown in place of the transaction grid while data is loading.
 * Uses Tailwind's animate-pulse with staggered per-row animation-delay for a
 * cascading shimmer effect.
 */
@Component({
  selector: 'app-transaction-skeleton',
  standalone: true,
  templateUrl: './transaction-skeleton.html',
})
export class TransactionSkeleton {
  /** Generate N placeholder rows */
  protected rows = Array.from({ length: 8 }, (_, i) => i);

  /** Varying description widths for organic look */
  protected descWidths = ['72%', '55%', '88%', '62%', '80%', '45%', '70%', '58%'];
}
