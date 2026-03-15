import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/services/auth.service';

@Component({
  selector: 'app-login-page',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './login-page.html',
})
export class LoginPage {
  private auth   = inject(AuthService);
  private router = inject(Router);
  private fb = inject(FormBuilder);

  form = this.fb.nonNullable.group({
    displayName: [''],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  isLoading         = signal(false);
  error             = signal<string | null>(null);
  showRegister      = signal(false);

  toggleMode() {
    this.showRegister.update(v => !v);
    this.error.set(null);
    this.form.markAsPristine();
    this.form.markAsUntouched();
  }

  controlInvalid(name: 'email' | 'password' | 'displayName'): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  controlError(name: 'email' | 'password' | 'displayName'): string | null {
    const control = this.form.controls[name];
    if (!this.controlInvalid(name)) return null;

    if (name === 'email') {
      if (control.hasError('required')) return 'El email es obligatorio.';
      if (control.hasError('email')) return 'Formato de email inválido.';
    }

    if (name === 'password') {
      if (control.hasError('required')) return 'La contraseña es obligatoria.';
      if (control.hasError('minlength')) return 'La contraseña debe tener al menos 8 caracteres.';
    }

    return 'Valor inválido.';
  }

  submit() {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password, displayName } = this.form.getRawValue();
    this.error.set(null);
    this.isLoading.set(true);

    if (this.showRegister()) {
      this.auth.registerStudio({
        studioName: displayName.trim() || email,
        email,
        password,
      }).subscribe({
        next: () => {
          this.isLoading.set(false);
          this.router.navigate(['/']);
        },
        error: (err) => {
          this.isLoading.set(false);
          if (err.status === 409) {
            this.error.set('Ya existe un usuario con ese email.');
          } else if (err.status === 400) {
            const body = err.error;
            if (body?.errors) {
              // FluentValidation: { errors: { Field: ["msg1", "msg2"] } }
              const messages = (Object.values(body.errors) as string[][]).flat().join(' ');
              this.error.set(messages || 'Datos inválidos. Revisá los campos.');
            } else if (body?.detail) {
              this.error.set(body.detail);
            } else {
              this.error.set('Datos inválidos. Revisá los campos.');
            }
          } else {
            this.error.set('Error de conexión. Intentá de nuevo.');
          }
        },
      });
    } else {
      this.auth.login({ email, password })
        .subscribe({
          next: () => this.router.navigate(['/']),
          error: (err) => {
            this.isLoading.set(false);
            const code = err.error?.code;
            if (code === 'ACCOUNT_PENDING')
              this.error.set('Tu cuenta está pendiente de activación. Contactá a soporte.');
            else if (code === 'ACCOUNT_SUSPENDED')
              this.error.set('Tu cuenta fue suspendida. Contactá a soporte para más información.');
            else if (err.status === 401)
              this.error.set('Email o contraseña incorrectos.');
            else
              this.error.set('Error de conexión. Intentá de nuevo.');
          },
        });
    }
  }
}
