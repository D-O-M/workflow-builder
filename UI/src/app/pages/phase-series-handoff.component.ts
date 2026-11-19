import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { createRequestStateStream, loadingRequestState } from '../domain/request-state';
import { HandoffValues, PhaseSeriesExplorerService } from '../domain/phase-series-explorer.service';
import type { PhasedProcessStatus } from '../domain/blueprint-editor.service';

export function handoffTargetLabels(
  mappings: ReadonlyArray<Pick<HandoffValues['mappings'][number], 'targetNodeId' | 'targetNodeName'>>,
): Map<string, string> {
  const nodesByName = new Map<string, Set<string>>();
  for (const { targetNodeName, targetNodeId } of mappings) {
    if (!targetNodeName) continue;
    const nodes = nodesByName.get(targetNodeName) ?? new Set<string>();
    nodes.add(targetNodeId);
    nodesByName.set(targetNodeName, nodes);
  }
  const labels = new Map<string, string>();
  for (const { targetNodeName, targetNodeId } of mappings) {
    let label = targetNodeName || targetNodeId;
    if (targetNodeName && nodesByName.get(targetNodeName)!.size > 1) {
      label = `${targetNodeName} (${targetNodeId})`;
    }
    labels.set(targetNodeId, label);
  }
  return labels;
}

@Component({
  selector: 'app-phase-series-handoff',
  template: `
    <div class="handoff" data-testid="phase-series-values">
      @let state = values();
      @if (state.status === 'ready') {
        @for (mapping of state.data.mappings; track $index) {
          <div class="mapping">
            <div class="endpoints">
              <span [title]="mapping.sourceNodeId ?? ''">
                {{ mapping.sourceNodeName || mapping.sourceNodeId }} · {{ mapping.sourceChannel }}.{{ mapping.sourceProperty }}
              </span>
              <span aria-label="maps to">→</span>
              <span [title]="mapping.targetNodeId">{{ targets().get(mapping.targetNodeId) }} · {{ mapping.targetProperty }}</span>
            </div>
            @if (mapping.hasValue) {
              <span>Recorded handoff value</span><pre>{{ mapping.valueJson }}</pre>
            }
            @else { <span class="missing">No recorded value</span> }
          </div>
        }
        @for (entry of state.data.unattributedValues; track entry.property) {
          <div class="mapping">
            <span>Recorded result · {{ entry.property }} <span class="missing">(destination unavailable)</span></span>
            <pre>{{ entry.valueJson }}</pre>
          </div>
        }
        @if (!state.data.mappings.length && !state.data.unattributedValues.length) {
          <span>No Data Bridge values recorded for this handoff.</span>
        }
      } @else if (state.status === 'error') {
        <div role="alert">{{ state.message }} <button type="button" (click)="retry()">Retry</button></div>
      } @else {
        <span role="status">Loading Data Bridge values…</span>
      }
    </div>`,
  styles: `
    .handoff { margin: .4rem 0 .2rem; border-left: 2px solid var(--app-status-info-container); padding: .25rem .9rem; color: var(--app-text-secondary); font-size: .88rem; }
    .mapping { display: grid; gap: .35rem; min-width: 0; padding: .45rem 0; overflow-wrap: anywhere; }
    .mapping + .mapping { border-top: 1px solid var(--app-outline); }
    .endpoints { display: flex; flex-wrap: wrap; align-items: baseline; column-gap: .45rem; font-weight: 560; }
    pre { margin: 0; width: fit-content; max-width: 100%; max-height: 15rem; overflow: auto; padding: .35rem .55rem; border-radius: .25rem; background: var(--app-surface-muted); font: .87rem/1.45 var(--app-font-family-mono); white-space: pre-wrap; overflow-wrap: anywhere; color: var(--app-text); }
    .missing { color: var(--app-text-secondary); font-style: italic; }
    button { border: 0; background: transparent; color: var(--app-status-info); text-decoration: underline; cursor: pointer; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhaseSeriesHandoffComponent {
  readonly definitionId = input.required<number>();
  readonly parentId = input.required<string>();
  readonly sourceId = input.required<string>();
  readonly sourceStatus = input.required<PhasedProcessStatus>();
  private readonly api = inject(PhaseSeriesExplorerService);
  private readonly attempt = signal(0);

  // Re-read an open handoff when its source finishes. Routine parent polls
  // otherwise refresh only the lightweight overview.
  private readonly selection = computed(() => ({
    definitionId: this.definitionId(), parentId: this.parentId(), source: this.sourceId(),
    status: this.sourceStatus(), attempt: this.attempt(),
  }));
  protected readonly values = toSignal(createRequestStateStream(
    toObservable(this.selection),
    key => this.api.handoff(key.definitionId, key.parentId, key.source),
    'Unable to load Data Bridge values.',
  ), { initialValue: loadingRequestState<HandoffValues>() });
  protected readonly targets = computed(() => {
    const state = this.values();
    return handoffTargetLabels(state.status === 'ready' ? state.data.mappings : []);
  });

  protected retry(): void {
    this.attempt.update(value => value + 1);
  }
}
