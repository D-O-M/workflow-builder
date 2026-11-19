import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { EMPTY, Subject, catchError, exhaustMap, finalize, map, merge, startWith, switchMap, timer } from 'rxjs';
import { AuthService } from '../../../src/app/auth/auth.service';
import { CapabilitySetting, Fleet, ManagedPlugin, ManagementApi, managementError } from './management-api';
import { Change, ChangeDialog } from './change-dialog';

@Component({
  selector: 'management-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule],
  templateUrl: './management-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagementPage {
  private readonly api = inject(ManagementApi);
  private readonly dialog = inject(MatDialog);
  private readonly destroy = inject(DestroyRef);
  private readonly reload = new Subject<void>();
  private readonly refreshFleet = new Subject<void>();
  readonly settingsPage = inject(ActivatedRoute).snapshot.data['navId'] === 'settings';
  readonly signedInDeployment = inject(AuthService).authenticationEnabled;
  readonly settings = signal<CapabilitySetting[]>([]);
  readonly plugins = signal<ManagedPlugin[]>([]);
  readonly fleet = signal<Fleet | null>(null);
  readonly revision = signal(0);
  readonly busy = signal(false);
  readonly loading = signal(true);
  readonly lastSave = signal<number | null>(null);
  readonly error = signal('');
  readonly fleetError = signal('');
  readonly readOnly = computed(() => !this.fleet()?.writesEnabled || this.busy() || this.loading());
  readonly groups = computed(() => {
    const groups = new Map<string, CapabilitySetting[]>();
    for (const field of this.settings()) {
      const group = groups.get(field.capabilityName) ?? [];
      group.push(field);
      groups.set(field.capabilityName, group);
    }
    return Array.from(groups, ([name, fields]) => ({name, fields}));
  });

  constructor() {
    if (!this.signedInDeployment) {
      this.loading.set(false);
      this.error.set('Sign-in must be enabled on this deployment to use Engine Management.');
      return;
    }
    this.reload.pipe(startWith(undefined), switchMap(() => {
      this.loading.set(true);
      const catalog = this.settingsPage
        ? this.api.settings().pipe(map(response => { this.settings.set(response.settings); return response.revision; }))
        : this.api.plugins().pipe(map(response => { this.plugins.set(response.plugins); return response.revision; }));
      return catalog.pipe(catchError(error => {
        this.settings.set([]);
        this.plugins.set([]);
        this.error.set(managementError(error));
        return EMPTY;
      }), finalize(() => this.loading.set(false)));
    }), takeUntilDestroyed()).subscribe(revision => this.revision.set(revision));

    merge(timer(0, 5000), this.refreshFleet).pipe(exhaustMap(() => this.api.fleet().pipe(
      catchError(error => {
        this.fleet.set(null);
        this.fleetError.set(managementError(error));
        return EMPTY;
      }),
    )), takeUntilDestroyed()).subscribe(next => {
      const previous = this.fleet();
      this.fleet.set(next);
      this.fleetError.set('');
      if (previous && (previous.revision !== next.revision || previous.status !== next.status)) this.reload.next();
    });
  }

  refresh() {
    this.error.set('');
    this.reload.next();
    this.refreshFleet.next();
  }

  change(change: Change) {
    const expectedRevision = this.revision();
    this.dialog.open<ChangeDialog, Change, {value: string}>(ChangeDialog, {
      data: change, width: '30rem', maxWidth: '94vw',
    }).afterClosed().pipe(takeUntilDestroyed(this.destroy)).subscribe(result => {
      if (!result) return;
      this.busy.set(true);
      this.error.set('');
      const save = change.action === 'toggle'
        ? this.api.enable(change.plugin, change.version, change.enable, expectedRevision)
        : change.action === 'remove'
          ? this.api.remove(change.setting.key, expectedRevision)
          : this.api.save(change.setting.key, result.value, expectedRevision);
      save.pipe(takeUntilDestroyed(this.destroy), finalize(() => this.busy.set(false))).subscribe({
        next: response => { this.lastSave.set(response.revision); this.refresh(); },
        error: error => { this.error.set(managementError(error)); this.reload.next(); this.refreshFleet.next(); },
      });
    });
  }
}
