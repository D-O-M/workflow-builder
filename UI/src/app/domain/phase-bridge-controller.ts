import { signal } from '@angular/core';
import { CanvasOutputPropertyDragEvent } from '../components/blueprint-canvas-contract';
import {
  bridgeOutputDragStateFromMapping,
  PhaseBridgeDeleteEffect,
  PhaseBridgeDragState,
  PhaseBridgeGuidance,
  PhaseBridgeOutputDragCallbacks,
  PhaseBridgePointerEventTarget,
  PhaseBridgeSelection,
  PhaseBridgeSourceDropResult,
  sourceDragStateFromCanvasEvent,
} from './phase-workbench-contract';
import { DataBridgeMappingDraft, DataBridgeSourceProperty } from './phase-series-canvas';

export class PhaseBridgeController {
  readonly dropMessage = signal<string | null>(null);
  readonly drag = signal<PhaseBridgeDragState | null>(null);
  readonly previewPosition = signal<number | null>(null);
  readonly selection = signal<PhaseBridgeSelection | null>(null);

  private outputDragCleanup: (() => void) | null = null;

  constructor(
    private readonly scheduleRefresh: () => void = () => undefined,
    private readonly pointerTarget: PhaseBridgePointerEventTarget | null = typeof window === 'undefined' ? null : window,
  ) {}

  guidance(mappingCount: number, incompleteMappingCount: number): PhaseBridgeGuidance {
    const dropMessage = this.dropMessage();
    if (dropMessage) {
      return {
        icon: 'warning',
        text: dropMessage,
        tone: 'warning',
      };
    }

    const drag = this.drag();
    if (drag?.kind === 'source-output') {
      return {
        icon: 'vertical_align_center',
        text: 'Drop to stage this property.',
        tone: 'info',
      };
    }

    if (drag?.kind === 'bridge-output') {
      return {
        icon: 'input',
        text: 'Drop on a compatible destination input.',
        tone: 'info',
      };
    }

    if (mappingCount > 0) {
      if (incompleteMappingCount === 0) {
        return {
          icon: 'check_circle',
          text: mappingCount === 1 ? 'Mapping complete.' : 'Mappings complete.',
          tone: 'success',
        };
      }

      return {
        icon: 'hub',
        text: 'Drag the lower handle to a compatible destination input.',
        tone: 'warning',
      };
    }

    return {
      icon: 'drag_indicator',
      text: 'Drag an expanded source output here.',
      tone: 'neutral',
    };
  }

  mappingCountLabel(mappingCount: number, incompleteMappingCount: number): string | null {
    if (mappingCount === 0) {
      return null;
    }

    if (incompleteMappingCount === 0) {
      return mappingCount === 1 ? '1 completed mapping' : `${mappingCount} completed mappings`;
    }

    return mappingCount === 1 ? '1 staged mapping' : `${mappingCount} staged mappings`;
  }

  guidanceLabel(mappingCount: number, incompleteMappingCount: number): string {
    const countLabel = this.mappingCountLabel(mappingCount, incompleteMappingCount);
    const guidance = this.guidance(mappingCount, incompleteMappingCount);
    return countLabel ? `${guidance.text} ${countLabel}.` : guidance.text;
  }

  startSourceDrag(event: CanvasOutputPropertyDragEvent, bridgePosition: number | null): void {
    this.dropMessage.set(null);
    this.previewPosition.set(bridgePosition);
    this.setDrag(sourceDragStateFromCanvasEvent(event, bridgePosition));
  }

  moveSourceDrag(event: CanvasOutputPropertyDragEvent, bridgePosition: number | null): void {
    this.previewPosition.set(bridgePosition);
    this.setDrag(sourceDragStateFromCanvasEvent(event, bridgePosition));
  }

  finishSourceDrag(request: {
    readonly clientX: number;
    readonly clientY: number;
    readonly cancelled: boolean;
    readonly bridgePosition: number | null;
    readonly event?: CanvasOutputPropertyDragEvent;
  }): PhaseBridgeSourceDropResult {
    const drag = this.drag();
    if (drag?.kind !== 'source-output' && !request.event) {
      return { kind: 'none' };
    }

    this.setDrag(null);
    this.previewPosition.set(null);
    if (request.cancelled) {
      return { kind: 'none' };
    }

    if (request.bridgePosition === null) {
      this.dropMessage.set('Drop on the bridge.');
      return { kind: 'none' };
    }

    const sourceProperty = request.event?.propertyName ?? drag?.sourceProperty ?? null;
    if (!sourceProperty) {
      this.dropMessage.set('Drop on the bridge.');
      return { kind: 'none' };
    }

    const source: DataBridgeSourceProperty = {
      sourceNodeId: request.event?.nodeId ?? drag?.sourceNodeId ?? null,
      sourceBlueprintNodeId: request.event?.nodeInstanceId ?? drag?.sourceBlueprintNodeId ?? null,
      sourceNodeName: request.event?.nodeName ?? drag?.sourceNodeName ?? drag?.label ?? 'Source',
      sourceChannelName: request.event?.channelName ?? drag?.sourceChannelName ?? null,
      sourceProperty,
      propertyKind: request.event?.propertyKind ?? drag?.propertyKind ?? 'String',
    };
    this.dropMessage.set(null);
    return { kind: 'stageSource', source, bridgePosition: request.bridgePosition };
  }

  startBridgeOutputDrag(
    event: PointerEvent,
    mapping: DataBridgeMappingDraft,
    callbacks: PhaseBridgeOutputDragCallbacks,
  ): void {
    event.preventDefault();
    event.stopPropagation();
    this.dropMessage.set(null);
    this.stopOutputDragListeners();
    this.setDrag(bridgeOutputDragStateFromMapping(mapping, event.clientX, event.clientY));

    const target = this.pointerTarget;
    if (!target) {
      return;
    }

    const startClientX = event.clientX;
    const startClientY = event.clientY;
    let movedBeyondClick = false;
    const onMove = (moveEvent: PointerEvent) => {
      movedBeyondClick ||= Math.abs(moveEvent.clientX - startClientX) > 3 || Math.abs(moveEvent.clientY - startClientY) > 3;
      this.setDrag(bridgeOutputDragStateFromMapping(mapping, moveEvent.clientX, moveEvent.clientY));
    };
    const onUp = (upEvent: PointerEvent) => {
      if (!movedBeyondClick) {
        this.stopOutputDragListeners();
        this.setDrag(null);
        this.select(mapping.id, 'destination');
        return;
      }

      callbacks.complete(mapping.id, upEvent.clientX, upEvent.clientY, false);
    };
    const onCancel = (cancelEvent: PointerEvent) => {
      callbacks.complete(mapping.id, cancelEvent.clientX, cancelEvent.clientY, true);
    };
    target.addEventListener('pointermove', onMove);
    target.addEventListener('pointerup', onUp, { once: true });
    target.addEventListener('pointercancel', onCancel, { once: true });
    this.outputDragCleanup = () => {
      target.removeEventListener('pointermove', onMove);
      target.removeEventListener('pointerup', onUp);
      target.removeEventListener('pointercancel', onCancel);
      this.outputDragCleanup = null;
    };
  }

  prepareDestinationDrop(cancelled: boolean): boolean {
    this.stopOutputDragListeners();
    this.setDrag(null);
    return !cancelled;
  }

  rejectDestinationDrop(): void {
    this.dropMessage.set('Drop on a compatible destination input.');
  }

  acceptDestinationDrop(mappingId: string): void {
    this.select(mappingId, 'destination');
    this.dropMessage.set(null);
  }

  cancelDrag(): void {
    this.stopOutputDragListeners();
    this.setDrag(null);
    this.previewPosition.set(null);
    this.dropMessage.set(null);
  }

  select(mappingId: string, side: PhaseBridgeSelection['side']): void {
    this.selection.set({ mappingId, side });
  }

  clearSelection(): void {
    this.selection.set(null);
  }

  isAnchorSelected(mappingId: string, side: PhaseBridgeSelection['side']): boolean {
    const selection = this.selection();
    return selection?.mappingId === mappingId && selection.side === side;
  }

  deleteSelectionEffect(): PhaseBridgeDeleteEffect | null {
    const selection = this.selection();
    if (!selection) {
      return null;
    }

    this.clearSelection();
    return selection.side === 'source'
      ? { kind: 'removeMapping', mappingId: selection.mappingId }
      : { kind: 'clearTarget', mappingId: selection.mappingId };
  }

  stopOutputDragListeners(): void {
    this.outputDragCleanup?.();
  }

  setDrag(drag: PhaseBridgeDragState | null): void {
    this.drag.set(drag);
    this.scheduleRefresh();
  }
}
