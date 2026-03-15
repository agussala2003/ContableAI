import { Component, inject, signal, OnInit } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/services/auth.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-reset-password-page',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule],
  templateUrl: './reset-password-page.html',
})
export class ResetPasswordPage implements OnInit {
  private auth  = inject(AuthService);
  private route = inject(ActivatedRoute);
  private fb = inject(FormBuilder);

  token       = '';
  email       = '';

  form = this.fb.nonNullable.group({
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirm: ['', [Validators.required]],
  }, { validators: this.passwordsMatchValidator });

  isLoading = signal(false);
  error     = signal<string | null>(null);
  success   = signal(false);
  invalid   = signal(false);  // token missing from URL

  private passwordsMatchValidator(control: AbstractControl): ValidationErrors | null {
    const password = control.get('newPassword')?.value as string | undefined;
    const confirm = control.get('confirm')?.value as string | undefined;
    if (!password || !confirm) return null;
    return password === confirm ? null : { passwordMismatch: true };
  }

  controlInvalid(name: 'newPassword' | 'confirm'): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || c.dirty);
  }

  controlError(name: 'newPassword' | 'confirm'): string | null {
    const c = this.form.controls[name];
    if (!this.controlInvalid(name)) return null;

    if (c.hasError('required')) return 'Este campo es obligatorio.';
    if (name === 'newPassword' && c.hasError('minlength')) return 'La contraseña debe tener al menos 8 caracteres.';
    if (name === 'confirm' && this.form.hasError('passwordMismatch') && (c.touched || c.dirty)) {
      return 'Las contraseñas no coinciden.';
    }

    return 'Valor inválido.';
  }

  ngOnInit() {
    this.token = this.route.snapshot.queryParamMap.get('token') ?? '';
    this.email = decodeURIComponent(this.route.snapshot.queryParamMap.get('email') ?? '');
    if (!this.token || !this.email) {
      this.invalid.set(true);
    }
  }

  submit() {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { newPassword } = this.form.getRawValue();
    this.error.set(null);
    this.isLoading.set(true);

    this.auth.resetPassword(this.token, this.email, newPassword).subscribe({
      next: () => {
        this.isLoading.set(false);
        this.success.set(true);
      },
      error: (err) => {
        this.isLoading.set(false);
        if (err.status === 400) {
          this.error.set(err.error?.message ?? 'El enlace es inválido o ya expiró.');
        } else {
          this.error.set('Error al restablecer la contraseña. Intentá de nuevo.');
        }
      },
    });
  }
}
