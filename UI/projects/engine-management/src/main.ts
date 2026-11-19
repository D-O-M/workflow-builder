import { APP_BASE_HREF } from '@angular/common';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { inject, provideAppInitializer, provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { AuthCallbackPageComponent } from '../../../src/app/auth/auth-callback-page.component';
import { AuthLogoutCallbackPageComponent } from '../../../src/app/auth/auth-logout-callback-page.component';
import { authGuard } from '../../../src/app/auth/auth.guard';
import { AppShellComponent } from '../../../src/app/core/app-shell.component';
import { SHELL_NAVIGATION } from '../../../src/app/domain/app-navigation';
import { RuntimeClientConfigService } from '../../../src/app/domain/runtime-client-config.service';
import { apiPrefixInterceptor } from '../../../src/app/interceptors/api-prefix.interceptor';
import { authTokenInterceptor } from '../../../src/app/interceptors/auth-token.interceptor';
import { ManagementPage } from './management-page';

const items = [
  { id: 'settings', route: '/settings', label: 'Capability settings', icon: 'tune' },
  { id: 'plugins', route: '/plugins', label: 'Plugins', icon: 'extension' },
];
bootstrapApplication(AppShellComponent, {
  providers: [
    provideZonelessChangeDetection(), provideAnimations(),
    { provide: APP_BASE_HREF, useValue: '/management/' },
    { provide: SHELL_NAVIGATION, useValue: { items, subtitle: 'Engine management', productToolbar: false } },
    provideHttpClient(withInterceptors([apiPrefixInterceptor, authTokenInterceptor])),
    provideAppInitializer(() => inject(RuntimeClientConfigService).load('management')),
    provideRouter([
      { path: '', pathMatch: 'full', redirectTo: 'settings' },
      { path: 'auth/callback', component: AuthCallbackPageComponent },
      { path: 'auth/logout-callback', component: AuthLogoutCallbackPageComponent },
      ...items.map(item => ({
        path: item.id, component: ManagementPage, canActivate: [authGuard],
        data: { shellTitle: item.label, navId: item.id },
      })),
      { path: '**', redirectTo: 'settings' },
    ]),
  ],
}).catch(error => console.error('Engine management startup failed', error));
