import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/services/auth.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-forgot-password-page',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule],
  templateUrl: './forgot-password-page.html',
})
export class ForgotPasswordPage {
  private auth = inject(AuthService);
  private fb = inject(FormBuilder);

  form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  isLoading = signal(false);
  error     = signal<string | null>(null);
  success   = signal(false);

  emailInvalid(): boolean {
    const email = this.form.controls.email;
    return email.invalid && (email.touched || email.dirty);
  }

  emailError(): string | null {
    if (!this.emailInvalid()) return null;
    const email = this.form.controls.email;
    if (email.hasError('required')) return 'El email es obligatorio.';
    if (email.hasError('email')) return 'Formato de email inválido.';
    return 'Email inválido.';
  }

  submit() {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.error.set(null);
    this.isLoading.set(true);
    const { email } = this.form.getRawValue();

    this.auth.forgotPassword(email).subscribe({
      next: () => {
        this.isLoading.set(false);
        this.success.set(true);
      },
      error: () => {
        this.isLoading.set(false);
        // Always show success for security — don't reveal email existence
        this.success.set(true);
      },
    });
  }
}
