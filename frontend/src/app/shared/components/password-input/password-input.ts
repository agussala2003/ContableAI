import { ChangeDetectionStrategy, Component, forwardRef, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';

/**
 * Campo de contraseña con ojito para mostrar/ocultar lo tecleado.
 *
 * Vive en shared y no copiado en cada pantalla porque son tres campos en dos formularios
 * (login/registro y el reset), y la clase del input tiene que ser exactamente la misma en todos:
 * duplicar el markup garantizaba que la próxima corrección de estilo se hiciera en dos de tres.
 *
 * Implementa ControlValueAccessor para que se use igual que un <input>: formControlName y las
 * validaciones del form siguen viviendo en la página.
 */
@Component({
  selector: 'app-password-input',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './password-input.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{
    provide: NG_VALUE_ACCESSOR,
    useExisting: forwardRef(() => PasswordInput),
    multi: true,
  }],
})
export class PasswordInput implements ControlValueAccessor {
  /** Id del <input>, para el `for` del <label> de la página. */
  inputId = input<string>('');
  placeholder = input('••••••••');
  /** 'current-password' al ingresar, 'new-password' al registrarse o resetear. */
  autocomplete = input('current-password');

  protected readonly visible  = signal(false);
  protected readonly value    = signal('');
  protected readonly disabled = signal(false);

  private onChange: (value: string) => void = () => {};
  protected onTouched: () => void = () => {};

  protected toggle(): void {
    this.visible.update(v => !v);
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.value.set(value);
    this.onChange(value);
  }

  // ── ControlValueAccessor ────────────────────────────────────────────────
  writeValue(value: string | null): void {
    this.value.set(value ?? '');
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }
}
