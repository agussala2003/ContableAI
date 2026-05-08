import { Injectable, signal, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';

@Injectable({ providedIn: 'root' })
export class KeyboardService implements OnDestroy {
  private _showShortcutsModal = signal(false);
  readonly showShortcutsModal = this._showShortcutsModal.asReadonly();

  // Tick-based signals: MainLayout watches these to open panels
  private _openSuggestionsTick = signal(0);
  readonly openSuggestionsTick = this._openSuggestionsTick.asReadonly();

  private gActive = false;
  private gTimer?: ReturnType<typeof setTimeout>;
  private readonly boundKeydown = this.handleKeydown.bind(this);

  constructor(private router: Router) {
    document.addEventListener('keydown', this.boundKeydown);
  }

  ngOnDestroy(): void {
    document.removeEventListener('keydown', this.boundKeydown);
    clearTimeout(this.gTimer);
  }

  toggleShortcutsModal(): void {
    this._showShortcutsModal.update(v => !v);
  }

  closeShortcutsModal(): void {
    this._showShortcutsModal.set(false);
  }

  triggerOpenSuggestions(): void {
    this._openSuggestionsTick.update(n => n + 1);
  }

  private handleKeydown(e: KeyboardEvent): void {
    if (this.isInputFocused()) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;

    // Escape closes the shortcuts modal (drawer close handled in MainLayout)
    if (e.key === 'Escape') {
      this._showShortcutsModal.set(false);
      return;
    }

    // ? — toggle shortcuts modal
    if (e.key === '?') {
      e.preventDefault();
      this._showShortcutsModal.update(v => !v);
      return;
    }

    // G — start two-key navigation combo
    if (e.key === 'g' || e.key === 'G') {
      e.preventDefault();
      this.gActive = true;
      clearTimeout(this.gTimer);
      this.gTimer = setTimeout(() => { this.gActive = false; }, 1500);
      return;
    }

    // Second key of G-combo
    if (this.gActive) {
      this.gActive = false;
      clearTimeout(this.gTimer);
      e.preventDefault();
      switch (e.key.toLowerCase()) {
        case 'r': this.router.navigate(['/']);                   break;
        case 'j': this.router.navigate(['/journal']);            break;
        case 'e': this.router.navigate(['/rules']);              break;
        case 't': this.router.navigate(['/studio-rules']);       break;
        case 'c': this.router.navigate(['/chart-of-accounts']); break;
        case 's': this._openSuggestionsTick.update(n => n + 1); break;
      }
    }
  }

  private isInputFocused(): boolean {
    const el = document.activeElement;
    if (!el) return false;
    const tag = el.tagName.toLowerCase();
    return (
      tag === 'input' ||
      tag === 'textarea' ||
      tag === 'select' ||
      (el as HTMLElement).isContentEditable
    );
  }
}
