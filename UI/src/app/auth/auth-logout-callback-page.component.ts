import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-auth-logout-callback-page',
  template: `
    <section class="auth-callback" aria-live="polite">
      @if (error()) {
        <h1>Sign-out failed</h1>
        <p>{{ error() }}</p>
      } @else {
        <h1>Signing out</h1>
      }
    </section>
  `,
  styles: [`
    .auth-callback {
      display: grid;
      min-height: 100dvh;
      place-content: center;
      gap: 0.75rem;
      padding: 2rem;
      text-align: center;
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthLogoutCallbackPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.complete();
  }

  private async complete(): Promise<void> {
    try {
      await this.auth.completeLogoutCallback();
      await this.router.navigateByUrl(this.auth.homeRoute);
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Unable to complete sign-out.');
    }
  }
}
