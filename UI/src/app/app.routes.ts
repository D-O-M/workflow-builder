import { Routes } from '@angular/router';
import { DashboardPageComponent } from './pages/dashboard-page.component';
import { BlueprintEditorPageComponent } from './pages/blueprint-editor-page.component';
import { StartProcessPageComponent } from './pages/start-process-page.component';
import { ProcessExplorerPageComponent } from './pages/process-explorer-page.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    component: DashboardPageComponent,
    data: {
      shellTitle: 'Dashboard',
      navId: 'dashboard',
    },
  },
  {
    path: 'blueprints',
    component: BlueprintEditorPageComponent,
    data: {
      shellTitle: 'Blueprint Builder',
      navId: 'blueprints',
    },
  },
  {
    path: 'processes/new',
    component: StartProcessPageComponent,
    data: {
      shellTitle: 'Start Process',
      navId: 'start-process',
    },
  },
  {
    path: 'processes',
    component: ProcessExplorerPageComponent,
    data: {
      shellTitle: 'Process Explorer',
      navId: 'processes',
    },
  },
];
