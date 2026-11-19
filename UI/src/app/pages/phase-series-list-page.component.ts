import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  PhaseSeriesExplorerService,
  phaseSeriesFilterError,
  phaseSeriesStatusLabels,
  PhaseSeriesSummary,
  readPhaseSeriesFilters,
} from '../domain/phase-series-explorer.service';

@Component({
  selector: 'app-phase-series-list-page',
  imports: [DatePipe, MatIconModule, ReactiveFormsModule, RouterLink],
  templateUrl: './phase-series-list-page.component.html',
  styleUrls: ['./phase-series-explorer.css', './phase-series-list-page.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhaseSeriesListPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(PhaseSeriesExplorerService);
  private readonly destroy = inject(DestroyRef);
  private request?: Subscription;

  protected readonly form = new FormGroup({
    q: new FormControl('', { nonNullable: true }),
    status: new FormControl('', { nonNullable: true }),
  });
  protected readonly labels = phaseSeriesStatusLabels;
  protected readonly statusOptions = Object.entries(phaseSeriesStatusLabels);
  protected readonly filters = signal(readPhaseSeriesFilters(this.route.snapshot.queryParamMap));
  protected readonly rows = signal<PhaseSeriesSummary[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly formError = signal('');

  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(params => {
      const next = readPhaseSeriesFilters(params);
      this.filters.set(next);
      this.form.setValue(next);
      this.formError.set(phaseSeriesFilterError(next) ?? '');
      this.restart();
    });
    this.destroy.onDestroy(() => this.request?.unsubscribe());
  }

  private restart(): void {
    this.request?.unsubscribe();
    this.busy.set(false);
    this.rows.set([]);
    this.cursor.set(null);
    this.error.set('');
    if (!this.formError()) this.load();
  }

  protected submit(): void {
    const next = this.form.getRawValue();
    next.q = next.q.trim();
    this.formError.set(phaseSeriesFilterError(next) ?? '');
    if (this.formError()) return;
    if (next.q === this.filters().q && next.status === this.filters().status) {
      this.restart();
      return;
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: next.q || null, status: next.status || null },
      queryParamsHandling: 'merge',
    });
  }

  protected clear(): void {
    this.form.reset();
    this.submit();
  }

  protected load(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.request = this.api.search(this.filters(), this.cursor()).subscribe({
      next: page => {
        const merged = new Map(this.rows().map(row => [row.phasedProcessId, row]));
        for (const row of page.items) merged.set(row.phasedProcessId, row);
        this.rows.set([...merged.values()]);
        this.cursor.set(page.continuationToken || null);
        this.busy.set(false);
      },
      error: () => {
        this.error.set('Unable to load Phase Series.');
        this.busy.set(false);
      },
    });
  }
}
