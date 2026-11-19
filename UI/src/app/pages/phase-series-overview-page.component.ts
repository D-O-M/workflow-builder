import { DatePipe, DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Clipboard } from '@angular/cdk/clipboard';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { fromEvent, Subscription } from 'rxjs';
import {
  PhaseSeriesExplorerService,
  PhaseSeriesOverview,
  PhaseSeriesPhase,
  phaseSeriesStatusLabels,
} from '../domain/phase-series-explorer.service';
import { PhaseSeriesHandoffComponent } from './phase-series-handoff.component';

@Component({
  selector: 'app-phase-series-overview-page',
  imports: [DatePipe, MatIconModule, RouterLink, PhaseSeriesHandoffComponent],
  templateUrl: './phase-series-overview-page.component.html',
  styleUrls: ['./phase-series-explorer.css', './phase-series-overview-page.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhaseSeriesOverviewPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(PhaseSeriesExplorerService);
  private readonly document = inject(DOCUMENT);
  private readonly clipboard = inject(Clipboard);
  private readonly destroy = inject(DestroyRef);
  private definitionId = 0;
  private parentId = '';
  private timer?: ReturnType<typeof setTimeout>;
  private request?: Subscription;
  private stopPolling = false;

  protected readonly overview = signal<PhaseSeriesOverview | null>(null);
  protected readonly busy = signal(false);
  protected readonly invalidLink = signal(false);
  protected readonly error = signal('');
  protected readonly copied = signal('');
  protected readonly expandedPhase = signal<string | null>(null);
  protected readonly labels = phaseSeriesStatusLabels;
  protected readonly phases = computed(() =>
    [...(this.overview()?.phases ?? [])].sort((left, right) => left.phaseIndex - right.phaseIndex));
  protected readonly current = computed(() =>
    this.phases().find(phase => phase.phaseId === this.overview()?.currentPhaseId));
  protected readonly completedCount = computed(() => this.phases().filter(phase => phase.status === 'Completed').length);
  protected readonly outgoing = computed(() =>
    new Map(this.overview()?.transitions.map(transition => [transition.sourcePhaseId, transition]) ?? []));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => {
      this.pause();
      this.overview.set(null);
      this.error.set('');
      this.expandedPhase.set(null);
      this.copied.set('');
      this.definitionId = Number(params.get('definitionId'));
      this.parentId = params.get('parentId') ?? '';
      const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i;
      this.stopPolling = !Number.isInteger(this.definitionId) || this.definitionId <= 0 || !guid.test(this.parentId);
      this.invalidLink.set(this.stopPolling);
      if (this.stopPolling) {
        this.error.set('This Phase Series link is invalid.');
      } else {
        this.resume();
      }
    });
    fromEvent(this.document, 'visibilitychange').pipe(takeUntilDestroyed()).subscribe(() => {
      if (this.document.hidden) this.pause();
      else this.resume();
    });
    const window = this.document.defaultView;
    if (window) {
      fromEvent(window, 'focus').pipe(takeUntilDestroyed()).subscribe(() => this.resume());
    }
    this.destroy.onDestroy(() => this.pause());
  }

  private pause(): void {
    clearTimeout(this.timer);
    this.request?.unsubscribe();
    this.busy.set(false);
  }

  private resume(): void {
    if (!this.stopPolling && !this.document.hidden) this.refresh();
  }

  protected refresh(): void {
    if (this.busy() || this.invalidLink()) return;
    clearTimeout(this.timer);
    this.busy.set(true);
    this.request = this.api.overview(this.definitionId, this.parentId).subscribe({
      next: overview => {
        this.overview.set(overview);
        this.error.set('');
        this.stopPolling = overview.status === 'Completed' || overview.status === 'Failed';
        this.finish();
      },
      error: (response: HttpErrorResponse) => {
        this.stopPolling = [400, 401, 403, 404].includes(response.status);
        if (response.status === 401 || response.status === 403) this.overview.set(null);
        if (this.overview()) this.error.set('Unable to refresh the overview. Showing the last update.');
        else if (response.status === 404) this.error.set('This Phase Series could not be found.');
        else this.error.set('Unable to load this Phase Series.');
        this.finish();
      },
    });
  }

  private finish(): void {
    this.busy.set(false);
    if (!this.stopPolling && !this.document.hidden) {
      this.timer = setTimeout(() => this.resume(), 5000);
    }
  }

  protected phaseStatus(phase: PhaseSeriesPhase): keyof typeof phaseSeriesStatusLabels {
    if (this.overview()?.status === 'WaitingForTrigger' && this.current()?.phaseId === phase.phaseId) {
      return 'WaitingForTrigger';
    }
    return phase.status;
  }

  protected copyId(id: string): void {
    this.copied.set(this.clipboard.copy(id) ? 'Parent ID copied.' : 'Select the parent ID to copy it.');
  }

  protected toggleHandoff(sourcePhaseId: string): void {
    this.expandedPhase.update(current => current === sourcePhaseId ? null : sourcePhaseId);
  }
}
