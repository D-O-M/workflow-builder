import {
  bridgePointFromDividerRect,
  BridgeCurvePathOptions,
  createPhaseBridgeCurvePath,
  normalizeBridgePosition,
  PhaseSplitMode,
} from './phase-bridge-geometry';
import {
  PhaseBridgeCurve,
  PhaseBridgeDragState,
  PhaseCanvasPort,
  PhaseWorkbenchDomRect,
} from './phase-workbench-contract';
import {
  DataBridgeMappingDraft,
  PhaseSeriesTransitionCanvas,
} from './phase-series-canvas';

export interface PhaseBridgeCurveRequest {
  readonly workbenchRect: Pick<PhaseWorkbenchDomRect, 'left' | 'top'>;
  readonly dividerRect: PhaseWorkbenchDomRect;
  readonly splitMode: PhaseSplitMode;
  readonly transition: PhaseSeriesTransitionCanvas | null;
  readonly drag: PhaseBridgeDragState | null;
  readonly sourceCanvas: PhaseCanvasPort | undefined;
  readonly destinationCanvas: PhaseCanvasPort | undefined;
  readonly mappingIssueMessage: (mappingId: string) => string | null;
}

export interface PhaseBridgeCurvePlan {
  readonly curves: readonly PhaseBridgeCurve[];
  readonly dragCurve: string | null;
}

export class PhaseBridgeCurveFactory {
  bridgePositionFromClientPoint(
    clientX: number,
    clientY: number,
    dividerRect: PhaseWorkbenchDomRect | null,
    splitMode: PhaseSplitMode,
  ): number | null {
    if (!dividerRect) {
      return null;
    }

    if (splitMode === 'vertical') {
      const horizontalHitSlop = 24;
      if (clientX < dividerRect.left - horizontalHitSlop || clientX > dividerRect.right + horizontalHitSlop) {
        return null;
      }

      if (clientY < dividerRect.top || clientY > dividerRect.bottom || dividerRect.height <= 0) {
        return null;
      }

      return normalizeBridgePosition((clientY - dividerRect.top) / dividerRect.height);
    }

    const verticalHitSlop = 24;
    if (clientY < dividerRect.top - verticalHitSlop || clientY > dividerRect.bottom + verticalHitSlop) {
      return null;
    }

    if (clientX < dividerRect.left || clientX > dividerRect.right || dividerRect.width <= 0) {
      return null;
    }

    return normalizeBridgePosition((clientX - dividerRect.left) / dividerRect.width);
  }

  bridgeClientPoint(
    kind: 'input' | 'output',
    bridgePosition: number,
    dividerRect: PhaseWorkbenchDomRect | null,
    splitMode: PhaseSplitMode,
  ): { clientX: number; clientY: number } | null {
    return dividerRect
      ? bridgePointFromDividerRect(kind, bridgePosition, splitMode, dividerRect)
      : null;
  }

  createCurvePlan(request: PhaseBridgeCurveRequest): PhaseBridgeCurvePlan {
    if (!request.transition) {
      return { curves: [], dragCurve: null };
    }

    const curves: PhaseBridgeCurve[] = [];
    for (const mapping of request.transition.mappings) {
      const sourcePoint = this.sourceMappingPoint(mapping, request.sourceCanvas);
      const bridgeInputPoint = this.bridgeClientPoint('input', mapping.bridgePosition, request.dividerRect, request.splitMode);
      if (sourcePoint && bridgeInputPoint) {
        curves.push({
          id: `${mapping.id}:input`,
          mappingId: mapping.id,
          side: 'source',
          path: this.createBridgeCurvePath(request.workbenchRect, sourcePoint, bridgeInputPoint, {
            startOutward: 'right',
          }),
          state: request.mappingIssueMessage(mapping.id) ? 'issue' : 'complete',
        });
      }

      const bridgeOutputPoint = this.bridgeClientPoint('output', mapping.bridgePosition, request.dividerRect, request.splitMode);
      const targetPoint = this.targetMappingPoint(mapping, request.destinationCanvas);
      if (bridgeOutputPoint && targetPoint) {
        curves.push({
          id: `${mapping.id}:output`,
          mappingId: mapping.id,
          side: 'destination',
          path: this.createBridgeCurvePath(request.workbenchRect, bridgeOutputPoint, targetPoint, {
            endOutward: 'left',
          }),
          state: request.mappingIssueMessage(mapping.id) ? 'issue' : 'complete',
        });
      }
    }

    return {
      curves,
      dragCurve: this.createDragCurvePath(request),
    };
  }

  createBridgeCurvePath(
    workbenchRect: Pick<PhaseWorkbenchDomRect, 'left' | 'top'>,
    startClient: { clientX: number; clientY: number },
    endClient: { clientX: number; clientY: number },
    options: BridgeCurvePathOptions = {},
  ): string {
    return createPhaseBridgeCurvePath(workbenchRect, startClient, endClient, options);
  }

  private createDragCurvePath(request: PhaseBridgeCurveRequest): string | null {
    const drag = request.drag;
    if (!drag) {
      return null;
    }

    if (drag.kind === 'source-output' && drag.sourceNodeId !== undefined && drag.sourceChannelName !== undefined && drag.sourceProperty) {
      const sourcePoint = this.visibleCanvasPoint(
        request.sourceCanvas,
        request.sourceCanvas?.bridge.outputPropertyClientPoint(
          drag.sourceNodeId ?? -1,
          drag.sourceChannelName,
          drag.sourceProperty,
        ) ?? null,
      );
      const endPoint = drag.bridgePosition === null || drag.bridgePosition === undefined
        ? { clientX: drag.clientX, clientY: drag.clientY }
        : this.bridgeClientPoint('input', drag.bridgePosition, request.dividerRect, request.splitMode);
      return sourcePoint && endPoint
        ? this.createBridgeCurvePath(request.workbenchRect, sourcePoint, endPoint, { startOutward: 'right' })
        : null;
    }

    if (drag.kind === 'bridge-output' && drag.mappingId) {
      const mapping = request.transition?.mappings.find((candidate) => candidate.id === drag.mappingId) ?? null;
      const bridgeOutputPoint = mapping
        ? this.bridgeClientPoint('output', mapping.bridgePosition, request.dividerRect, request.splitMode)
        : null;
      const endPoint = { clientX: drag.clientX, clientY: drag.clientY };
      return bridgeOutputPoint ? this.createBridgeCurvePath(request.workbenchRect, bridgeOutputPoint, endPoint) : null;
    }

    return null;
  }

  private sourceMappingPoint(
    mapping: DataBridgeMappingDraft,
    sourceCanvas: PhaseCanvasPort | undefined,
  ): { clientX: number; clientY: number } | null {
    return mapping.sourceNodeId !== null && mapping.sourceChannelName !== null
      ? this.visibleCanvasPoint(
          sourceCanvas,
          sourceCanvas?.bridge.outputPropertyClientPoint(
            mapping.sourceNodeId,
            mapping.sourceChannelName,
            mapping.sourceProperty,
          ) ?? null,
        )
      : null;
  }

  private targetMappingPoint(
    mapping: DataBridgeMappingDraft,
    destinationCanvas: PhaseCanvasPort | undefined,
  ): { clientX: number; clientY: number } | null {
    return mapping.targetNodeId !== null && mapping.targetProperty
      ? this.visibleCanvasPoint(
          destinationCanvas,
          destinationCanvas?.bridge.inputPropertyClientPoint(mapping.targetNodeId, mapping.targetProperty) ?? null,
        )
      : null;
  }

  private visibleCanvasPoint(
    canvas: PhaseCanvasPort | undefined,
    point: { clientX: number; clientY: number } | null,
  ): { clientX: number; clientY: number } | null {
    return canvas && point && canvas.viewport.containsClientPoint(point) ? point : null;
  }
}

