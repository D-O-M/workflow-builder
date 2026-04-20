import { Routes } from '@angular/router';
import { DashboardPageComponent } from './pages/dashboard-page.component';
import { BlueprintEditorPageComponent } from './pages/blueprint-editor-page.component';
import { ExecuteFloPageComponent } from './pages/execute-flo-page.component';
import { WorkflowExecutionsPageComponent } from './pages/workflow-executions-page.component';

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
      navId: 'workflows',
    },
  },
  {
    path: 'execute',
    component: ExecuteFloPageComponent,
    data: {
      shellTitle: 'Trigger Workflow',
      navId: 'trigger',
    },
  },
  {
    path: 'executions',
    component: WorkflowExecutionsPageComponent,
    data: {
      shellTitle: 'Execution Explorer',
      navId: 'executions',
    },
  },
];
