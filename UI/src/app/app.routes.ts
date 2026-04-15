import { Routes } from '@angular/router';
import { BlueprintEditorPageComponent } from './pages/blueprint-editor-page.component';
import { ExecuteFloPageComponent } from './pages/execute-flo-page.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'blueprints' },
  { path: 'blueprints', component: BlueprintEditorPageComponent },
  { path: 'execute', component: ExecuteFloPageComponent },
];
