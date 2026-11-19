import { Routes } from '@angular/router';
import { DashboardPageComponent } from './pages/dashboard-page.component';
import { BlueprintEditorPageComponent } from './pages/blueprint-editor-page.component';
import { PhasesPageComponent } from './pages/phases-page.component';
import { StartProcessPageComponent } from './pages/start-process-page.component';
import { ProcessExplorerPageComponent } from './pages/process-explorer-page.component';
import { AuthCallbackPageComponent } from './auth/auth-callback-page.component';
import { AuthLogoutCallbackPageComponent } from './auth/auth-logout-callback-page.component';
import { authGuard } from './auth/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'auth/callback', component: AuthCallbackPageComponent },
  { path: 'auth/logout-callback', component: AuthLogoutCallbackPageComponent },
  {
    path: 'dashboard',
    component: DashboardPageComponent,
    canActivate: [authGuard],
    data: {
      shellTitle: 'Dashboard',
      navId: 'dashboard',
    },
  },
  {
    path: 'blueprints',
    component: BlueprintEditorPageComponent,
    canActivate: [authGuard],
    data: {
      shellTitle: 'Blueprint Builder',
      navId: 'blueprints',
    },
  },
  {
    path: 'phases',
    component: PhasesPageComponent,
    canActivate: [authGuard],
    data: {
      shellTitle: 'Phases',
      navId: 'phases',
    },
  },
  {
    path: 'processes/new',
    component: StartProcessPageComponent,
    canActivate: [authGuard],
    data: {
      shellTitle: 'Start Process',
      navId: 'start-process',
    },
  },
  {
    path: 'processes/phase-series/:definitionId/:parentId',
    loadComponent: () => import('./pages/phase-series-overview-page.component')
      .then(module => module.PhaseSeriesOverviewPageComponent),
    canActivate: [authGuard],
    data: { shellTitle: 'Process Explorer', navId: 'processes' },
  },
  {
    path: 'processes/phase-series',
    loadComponent: () => import('./pages/phase-series-list-page.component')
      .then(module => module.PhaseSeriesListPageComponent),
    canActivate: [authGuard],
    data: { shellTitle: 'Process Explorer', navId: 'processes' },
  },
  {
    path: 'processes',
    component: ProcessExplorerPageComponent,
    canActivate: [authGuard],
    data: {
      shellTitle: 'Process Explorer',
      navId: 'processes',
    },
  },
];
