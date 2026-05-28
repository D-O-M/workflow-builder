import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  User,
  UserManager,
  UserManagerSettings,
  WebStorageStateStore,
} from 'oidc-client-ts';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

export interface AuthUserProfile {
  subject: string;
  displayName: string;
  email: string | null;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);
  private readonly router = inject(Router);
  private readonly authenticated = signal(!this.runtimeClientConfig.authentication.enabled);
  private readonly profile = signal<AuthUserProfile | null>(
    this.runtimeClientConfig.authentication.enabled ? null : {
      subject: 'local-development',
      displayName: 'Local Development',
      email: null,
    },
  );

  private userManager: UserManager | null = null;
  private initializePromise: Promise<void> | null = null;

  readonly isAuthenticated = computed(() => this.authenticated());
  readonly userProfile = computed(() => this.profile());

  get authenticationEnabled(): boolean {
    return this.runtimeClientConfig.authentication.enabled;
  }

  async initialize(): Promise<void> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return;
    }

    this.initializePromise ??= this.loadUser();
    await this.initializePromise;
  }

  async ensureAuthenticated(returnUrl: string): Promise<boolean> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return true;
    }

    await this.initialize();
    if (this.authenticated()) {
      return true;
    }

    await this.login(returnUrl);
    return false;
  }

  async getAccessToken(): Promise<string | null> {
    if (!this.authenticationEnabled) {
      return null;
    }

    await this.initialize();
    const user = await this.getUserManager().getUser();
    if (!user || user.expired) {
      await this.clearUser();
      return null;
    }

    return user.access_token ?? null;
  }

  async login(returnUrl = this.router.url): Promise<void> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return;
    }

    await this.getUserManager().signinRedirect({
      state: { returnUrl },
    });
  }

  async completeLoginCallback(): Promise<string> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return '/dashboard';
    }

    const user = await this.getUserManager().signinRedirectCallback();
    this.applyUser(user);
    const state = user.state as { returnUrl?: unknown } | null;
    return typeof state?.returnUrl === 'string' ? state.returnUrl : '/dashboard';
  }

  async logout(): Promise<void> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return;
    }

    const manager = this.getUserManager();
    const user = await manager.getUser();
    if (!user) {
      await this.clearUser();
      await this.router.navigateByUrl('/dashboard');
      return;
    }

    await manager.signoutRedirect();
  }

  async completeLogoutCallback(): Promise<void> {
    if (!this.authenticationEnabled) {
      this.setDisabledMode();
      return;
    }

    await this.getUserManager().signoutRedirectCallback();
    await this.clearUser();
  }

  private async loadUser(): Promise<void> {
    const user = await this.getUserManager().getUser();
    if (!user || user.expired) {
      await this.clearUser();
      return;
    }

    this.applyUser(user);
  }

  private getUserManager(): UserManager {
    this.userManager ??= new UserManager(this.createUserManagerSettings());
    return this.userManager;
  }

  private createUserManagerSettings(): UserManagerSettings {
    const auth = this.runtimeClientConfig.authentication;
    const origin = typeof window === 'undefined' ? '' : window.location.origin;
    return {
      authority: auth.authority,
      client_id: auth.clientId,
      redirect_uri: `${origin}/auth/callback`,
      post_logout_redirect_uri: `${origin}/auth/logout-callback`,
      response_type: 'code',
      scope: auth.scope || 'openid profile email',
      loadUserInfo: true,
      filterProtocolClaims: true,
      userStore: typeof window === 'undefined'
        ? undefined
        : new WebStorageStateStore({ store: window.sessionStorage }),
    };
  }

  private applyUser(user: User): void {
    this.authenticated.set(true);
    this.profile.set({
      subject: String(user.profile.sub ?? ''),
      displayName: this.readDisplayName(user),
      email: typeof user.profile.email === 'string' ? user.profile.email : null,
    });
  }

  private async clearUser(): Promise<void> {
    if (this.userManager) {
      await this.userManager.removeUser();
    }

    this.authenticated.set(false);
    this.profile.set(null);
  }

  private setDisabledMode(): void {
    this.authenticated.set(true);
    this.profile.set({
      subject: 'local-development',
      displayName: 'Local Development',
      email: null,
    });
  }

  private readDisplayName(user: User): string {
    const profile = user.profile;
    return String(
      profile.preferred_username ??
      profile.name ??
      profile.email ??
      profile.sub ??
      'User',
    );
  }
}
