import { ChangeDetectionStrategy, Component } from '@angular/core';
import { AppShellComponent } from './core/app-shell.component';

@Component({
    selector: 'app-root',
    imports: [AppShellComponent],
    templateUrl: './app.html',
    styleUrl: './app.css',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
