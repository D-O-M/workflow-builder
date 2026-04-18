import { Injectable, effect, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class NavigationDrawerService {
  private readonly storageKey = 'workflow-builder.nav-drawer-collapsed';
  readonly collapsed = signal(this.readInitialState());

  constructor() {
    effect(() => {
      if (typeof window === 'undefined') {
        return;
      }

      window.localStorage.setItem(this.storageKey, this.collapsed() ? 'true' : 'false');
    });
  }

  toggle(): void {
    this.collapsed.update((collapsed) => !collapsed);
  }

  private readInitialState(): boolean {
    if (typeof window === 'undefined') {
      return false;
    }

    return window.localStorage.getItem(this.storageKey) === 'true';
  }
}
