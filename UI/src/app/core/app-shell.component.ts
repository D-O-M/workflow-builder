import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet, ActivatedRouteSnapshot, NavigationEnd } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { filter, map, startWith } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { APP_PRIMARY_NAV_ITEMS } from '../domain/app-navigation';
import { NavigationDrawerService } from '../domain/navigation-drawer.service';

interface ShellRouteState {
  title: string;
  navId: string;
}

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, MatIconModule],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly navigationDrawer = inject(NavigationDrawerService);

  protected readonly navItems = APP_PRIMARY_NAV_ITEMS;
  protected readonly sidebarCollapsed = this.navigationDrawer.collapsed;
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

  protected toggleNavigationDrawer(): void {
    if (this.isCompactViewport()) {
      this.mobileSidebarOpen.update((open) => !open);
      return;
    }

    this.navigationDrawer.toggle();
  }

  protected closeNavigationDrawer(): void {
    this.mobileSidebarOpen.set(false);
  }

  protected navigationDrawerExpanded(): boolean {
    return this.isCompactViewport() ? this.mobileSidebarOpen() : !this.sidebarCollapsed();
  }

  protected navigationDrawerIcon(): string {
    return this.mobileSidebarOpen() && this.isCompactViewport() ? 'close' : 'menu';
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
}
