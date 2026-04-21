import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import {
  ActivatedRouteSnapshot,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs';
import { APP_PRIMARY_NAV_ITEMS } from '../domain/app-navigation';
import { NavigationDrawerService } from '../domain/navigation-drawer.service';

interface ShellRouteState {
  title: string;
  navId: string;
}

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatDividerModule,
    MatIconModule,
    MatListModule,
    MatSidenavModule,
    MatToolbarModule,
  ],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly navigationDrawer = inject(NavigationDrawerService);

  protected readonly navItems = APP_PRIMARY_NAV_ITEMS;
  protected readonly sidebarCollapsed = this.navigationDrawer.collapsed;
  protected readonly compactViewport = signal(this.isCompactViewport());
  protected readonly mobileSidebarOpen = signal(false);
  protected readonly shellState = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      startWith(null),
      map(() => this.readShellState()),
    ),
    { initialValue: this.readShellState() },
  );
  protected readonly activeNavId = computed(() => this.shellState().navId);
  protected readonly sidenavMode = computed(() => this.compactViewport() ? 'over' : 'side');
  protected readonly sidenavOpened = computed(() =>
    this.compactViewport() ? this.mobileSidebarOpen() : true,
  );
  protected readonly profileInitial = computed(() =>
    this.shellState().title.trim().charAt(0).toUpperCase() || 'W',
  );

  constructor() {
    effect(() => {
      this.shellState();
      if (this.compactViewport()) {
        this.mobileSidebarOpen.set(false);
      }
    });
  }

  protected toggleNavigationDrawer(): void {
    if (this.compactViewport()) {
      this.mobileSidebarOpen.update((open) => !open);
      return;
    }

    this.navigationDrawer.toggle();
  }

  protected closeNavigationDrawer(): void {
    this.mobileSidebarOpen.set(false);
  }

  private readShellState(): ShellRouteState {
    let current: ActivatedRouteSnapshot = this.router.routerState.snapshot.root;
    while (current.firstChild) {
      current = current.firstChild;
    }

    return {
      title: current.data['shellTitle'] as string ?? 'Workflow Builder',
      navId: current.data['navId'] as string ?? 'dashboard',
    };
  }

  private isCompactViewport(): boolean {
    return typeof window !== 'undefined' && window.matchMedia('(max-width: 1100px)').matches;
  }

  @HostListener('window:resize')
  protected onWindowResize(): void {
    const compact = this.isCompactViewport();
    this.compactViewport.set(compact);

    if (!compact) {
      this.mobileSidebarOpen.set(false);
    }
  }
}
