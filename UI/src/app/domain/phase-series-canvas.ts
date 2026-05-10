import { ConnectionModel } from './connection-model';
import {
  PhasedBlueprintHandoffMappingInputModel,
  PhasedBlueprintTransitionInputModel,
} from './blueprint-editor.service';
import { NodeModel, TaskPropertyType } from './node-model';

export interface DataBridgeSourceProperty {
  sourceNodeId: number | null;
  sourceBlueprintNodeId: string | null;
  sourceNodeName: string;
  sourceChannelName: string | null;
  sourceProperty: string;
  propertyKind: TaskPropertyType;
}

export interface DataBridgeTargetProperty {
  targetNodeId: number;
  targetBlueprintNodeId: string | null;
  targetNodeName: string;
  targetProperty: string;
  propertyKind: TaskPropertyType;
}

export interface DataBridgeMappingDraft extends DataBridgeSourceProperty {
  id: string;
  bridgePosition: number;
  targetNodeId: number | null;
  targetBlueprintNodeId: string | null;
  targetNodeName: string | null;
  targetProperty: string | null;
}

export interface PhaseSeriesPhaseCanvas {
  phaseId: string;
  name: string;
  blueprintName: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number | null;
  blueprintVersionNumber: number | null;
  nodes: NodeModel[];
  connections: ConnectionModel[];
}

export interface PhaseSeriesTransitionCanvas {
  transitionId: string;
  sourcePhaseId: string;
  destinationPhaseId: string;
  mappings: DataBridgeMappingDraft[];
}

export interface PhaseSeriesCanvasModel {
  phases: PhaseSeriesPhaseCanvas[];
  transitions: PhaseSeriesTransitionCanvas[];
}

export interface PhasePairOption {
  index: number;
  label: string;
}

export interface DataBridgeValidationIssue {
  transitionIndex: number;
  mappingId: string | null;
  message: string;
}

export function createPhasePairOptions(phases: readonly PhaseSeriesPhaseCanvas[]): PhasePairOption[] {
  return phases.slice(0, -1).map((phase, index) => ({
    index,
    label: `${phase.name || `Phase ${index + 1}`} -> ${phases[index + 1]?.name || `Phase ${index + 2}`}`,
  }));
}

export function createTransitionDraft(
  sourcePhaseId: string,
  destinationPhaseId: string,
  mappings: DataBridgeMappingDraft[] = [],
  transitionId: string = crypto.randomUUID(),
): PhaseSeriesTransitionCanvas {
  return {
    transitionId,
    sourcePhaseId,
    destinationPhaseId,
    mappings,
  };
}

export function createDataBridgeMappingFromHandoff(
  handoffMapping: PhasedBlueprintHandoffMappingInputModel,
  sourcePhase: PhaseSeriesPhaseCanvas,
  destinationPhase: PhaseSeriesPhaseCanvas,
): DataBridgeMappingDraft | null {
  if (handoffMapping.sourceKind !== 'Output' || !handoffMapping.sourceBlueprintNodeId) {
    return null;
  }

  const sourceNode = sourcePhase.nodes.find((node) => node.instanceId === handoffMapping.sourceBlueprintNodeId) ?? null;
  const sourceChannelName = handoffMapping.sourceChannelName ?? null;
  const sourceAnchor = sourceNode ? outputAnchorIndexByChannel(sourceNode, sourceChannelName) : null;
  const sourceProperties = sourceNode && sourceAnchor !== null
    ? sourceNode.outputPropertiesForAnchor(sourceAnchor)
    : {};
  const propertyKind = sourceProperties[handoffMapping.sourceProperty];
  if (!sourceNode || !propertyKind) {
    return null;
  }

  const targetNode = destinationPhase.nodes.find((node) => node.instanceId === handoffMapping.targetBlueprintNodeId) ?? null;
  const target = targetNode && handoffMapping.targetProperty in targetNode.inputProperties
    ? {
        targetNodeId: targetNode.id,
        targetBlueprintNodeId: targetNode.instanceId,
        targetNodeName: targetNode.name,
        targetProperty: handoffMapping.targetProperty,
      }
    : null;

  return createDataBridgeMappingFromSource({
    sourceNodeId: sourceNode.id,
    sourceBlueprintNodeId: sourceNode.instanceId,
    sourceNodeName: sourceNode.name,
    sourceChannelName,
    sourceProperty: handoffMapping.sourceProperty,
    propertyKind,
  }, normalizeBridgePosition(handoffMapping.bridgePosition), target);
}

export function createDataBridgeMappingFromSource(
  source: DataBridgeSourceProperty,
  bridgePosition: number,
  target: Pick<DataBridgeMappingDraft, 'targetNodeId' | 'targetBlueprintNodeId' | 'targetNodeName' | 'targetProperty'> | null = null,
  id: string = crypto.randomUUID(),
): DataBridgeMappingDraft {
  return {
    id,
    ...source,
    bridgePosition: normalizeBridgePosition(bridgePosition),
    targetNodeId: target?.targetNodeId ?? null,
    targetBlueprintNodeId: target?.targetBlueprintNodeId ?? null,
    targetNodeName: target?.targetNodeName ?? null,
    targetProperty: target?.targetProperty ?? null,
  };
}

export function upsertDataBridgeSourceMapping(
  transition: PhaseSeriesTransitionCanvas,
  source: DataBridgeSourceProperty,
  bridgePosition: number,
): PhaseSeriesTransitionCanvas {
  const key = bridgeMappingSourceKey(source);
  const existing = transition.mappings.find((mapping) => bridgeMappingSourceKey(mapping) === key) ?? null;
  const nextMapping = createDataBridgeMappingFromSource(
    source,
    bridgePosition,
    existing
      ? {
          targetNodeId: existing.targetNodeId,
          targetBlueprintNodeId: existing.targetBlueprintNodeId,
          targetNodeName: existing.targetNodeName,
          targetProperty: existing.targetProperty,
        }
      : null,
    existing?.id,
  );
  const mappings = existing
    ? transition.mappings.map((mapping) => mapping.id === existing.id ? nextMapping : mapping)
    : [...transition.mappings, nextMapping];

  return {
    ...transition,
    mappings: sortMappings(mappings),
  };
}

export function completeDataBridgeMappingTarget(
  transition: PhaseSeriesTransitionCanvas,
  destinationPhase: PhaseSeriesPhaseCanvas,
  mappingId: string,
  target: DataBridgeTargetProperty,
): { transition: PhaseSeriesTransitionCanvas; accepted: boolean } {
  const mapping = transition.mappings.find((candidate) => candidate.id === mappingId) ?? null;
  const targetNode = findPhaseNode(destinationPhase.nodes, target.targetNodeId, target.targetBlueprintNodeId);
  if (!mapping || !targetNode) {
    return { transition, accepted: false };
  }

  if (mapping.propertyKind !== target.propertyKind || !(target.targetProperty in targetNode.inputProperties)) {
    return { transition, accepted: false };
  }

  const targetKey = `${targetNode.instanceId ?? targetNode.id}:${target.targetProperty}`;
  const duplicateTarget = transition.mappings.some((candidate) => {
    if (candidate.id === mappingId || candidate.targetProperty !== target.targetProperty) {
      return false;
    }

    const candidateNode = findPhaseNode(destinationPhase.nodes, candidate.targetNodeId, candidate.targetBlueprintNodeId);
    return !!candidateNode && `${candidateNode.instanceId ?? candidateNode.id}:${candidate.targetProperty}` === targetKey;
  });
  if (duplicateTarget) {
    return { transition, accepted: false };
  }

  return {
    accepted: true,
    transition: {
      ...transition,
      mappings: transition.mappings.map((candidate) =>
        candidate.id === mappingId
          ? {
              ...candidate,
              targetNodeId: targetNode.id,
              targetBlueprintNodeId: targetNode.instanceId,
              targetNodeName: targetNode.name,
              targetProperty: target.targetProperty,
            }
          : candidate,
      ),
    },
  };
}

export function clearDataBridgeMappingTarget(
  transition: PhaseSeriesTransitionCanvas,
  mappingId: string,
): PhaseSeriesTransitionCanvas {
  return {
    ...transition,
    mappings: transition.mappings.map((mapping) =>
      mapping.id === mappingId
        ? {
            ...mapping,
            targetNodeId: null,
            targetBlueprintNodeId: null,
            targetNodeName: null,
            targetProperty: null,
          }
        : mapping,
    ),
  };
}

export function removeDataBridgeMapping(
  transition: PhaseSeriesTransitionCanvas,
  mappingId: string,
): PhaseSeriesTransitionCanvas {
  return {
    ...transition,
    mappings: transition.mappings.filter((mapping) => mapping.id !== mappingId),
  };
}

export function serializeTransitionMappings(
  transition: PhaseSeriesTransitionCanvas,
): PhasedBlueprintTransitionInputModel {
  return {
    transitionId: transition.transitionId,
    sourcePhaseId: transition.sourcePhaseId,
    destinationPhaseId: transition.destinationPhaseId,
    handoffMappings: transition.mappings
      .filter((mapping) =>
        mapping.sourceBlueprintNodeId !== null &&
        mapping.sourceChannelName !== null &&
        mapping.targetBlueprintNodeId !== null &&
        mapping.targetProperty !== null)
      .map((mapping) => ({
        sourceKind: 'Output',
        sourceBlueprintNodeId: mapping.sourceBlueprintNodeId,
        sourceChannelName: mapping.sourceChannelName,
        sourceProperty: mapping.sourceProperty,
        bridgePosition: normalizeBridgePosition(mapping.bridgePosition),
        targetBlueprintNodeId: mapping.targetBlueprintNodeId!,
        targetProperty: mapping.targetProperty!,
      })),
    correlationMappings: [],
  };
}

export function validatePhaseSeries(model: PhaseSeriesCanvasModel): DataBridgeValidationIssue[] {
  const issues: DataBridgeValidationIssue[] = [];
  if (model.phases.length < 2 || model.phases.length > 4) {
    issues.push({
      transitionIndex: 0,
      mappingId: null,
      message: 'A phase series must contain between 2 and 4 phases.',
    });
  }

  for (const [transitionIndex, transition] of model.transitions.entries()) {
    const sourcePhase = model.phases.find((phase) => phase.phaseId === transition.sourcePhaseId) ?? null;
    const destinationPhase = model.phases.find((phase) => phase.phaseId === transition.destinationPhaseId) ?? null;
    const targetKeys = new Set<string>();
    for (const mapping of transition.mappings) {
      if (!mapping.sourceBlueprintNodeId || !mapping.sourceChannelName || !mapping.sourceProperty) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.sourceNodeName || 'Source node'} has an incomplete Data Bridge source.`,
        });
        continue;
      }

      const sourceNode = mapping.sourceBlueprintNodeId
        ? sourcePhase?.nodes.find((node) => node.instanceId === mapping.sourceBlueprintNodeId) ?? null
        : mapping.sourceNodeId !== null
          ? sourcePhase?.nodes.find((node) => node.id === mapping.sourceNodeId) ?? null
          : null;
      const sourceAnchor = sourceNode ? outputAnchorIndexByChannel(sourceNode, mapping.sourceChannelName) : null;
      const sourceKind = sourceNode && sourceAnchor !== null
        ? sourceNode.outputPropertiesForAnchor(sourceAnchor)[mapping.sourceProperty]
        : null;
      if (!sourceNode || !sourceKind) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.sourceNodeName || 'Source node'} no longer exposes ${mapping.sourceProperty}.`,
        });
        continue;
      }

      if (sourceKind !== mapping.propertyKind) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.sourceProperty} changed from ${mapping.propertyKind} to ${sourceKind}.`,
        });
        continue;
      }

      if (!mapping.targetBlueprintNodeId || !mapping.targetProperty) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.sourceProperty} needs a destination input.`,
        });
        continue;
      }

      const targetNode = destinationPhase
        ? findPhaseNode(destinationPhase.nodes, mapping.targetNodeId, mapping.targetBlueprintNodeId)
        : null;
      if (!targetNode) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.targetNodeName || 'Destination node'} is no longer in the destination phase.`,
        });
        continue;
      }

      const targetKey = `${targetNode.instanceId ?? targetNode.id}:${mapping.targetProperty}`;
      if (targetKeys.has(targetKey)) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${targetNode.name}.${mapping.targetProperty} already has a Data Bridge mapping.`,
        });
        continue;
      }
      targetKeys.add(targetKey);

      const targetKind = targetNode.inputProperties[mapping.targetProperty];
      if (!targetKind) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${targetNode.name} no longer exposes ${mapping.targetProperty}.`,
        });
        continue;
      }

      if (targetKind !== mapping.propertyKind) {
        issues.push({
          transitionIndex,
          mappingId: mapping.id,
          message: `${mapping.sourceProperty} (${mapping.propertyKind}) cannot map to ${targetNode.name}.${mapping.targetProperty} (${targetKind}).`,
        });
      }
    }
  }

  return issues;
}

export function reconcileTransitionsForPhaseVersionChange(
  model: PhaseSeriesCanvasModel,
  changedPhaseIndex: number,
): PhaseSeriesTransitionCanvas[] {
  return model.transitions.map((transition) => {
    const sourcePhaseIndex = model.phases.findIndex((phase) => phase.phaseId === transition.sourcePhaseId);
    const destinationPhaseIndex = model.phases.findIndex((phase) => phase.phaseId === transition.destinationPhaseId);
    if (sourcePhaseIndex !== changedPhaseIndex && destinationPhaseIndex !== changedPhaseIndex) {
      return transition;
    }

    const sourcePhase = model.phases[sourcePhaseIndex];
    const destinationPhase = model.phases[destinationPhaseIndex];
    if (!sourcePhase || !destinationPhase) {
      return transition;
    }

    return {
      ...transition,
      mappings: transition.mappings.map((mapping): DataBridgeMappingDraft => {
        const sourceNode = mapping.sourceBlueprintNodeId
          ? sourcePhase.nodes.find((node) => node.instanceId === mapping.sourceBlueprintNodeId)
          : mapping.sourceNodeId !== null
            ? sourcePhase.nodes.find((node) => node.id === mapping.sourceNodeId)
            : null;
        const sourceAnchor = sourceNode ? outputAnchorIndexByChannel(sourceNode, mapping.sourceChannelName) : null;
        const sourceProperties = sourceNode && sourceAnchor !== null
          ? sourceNode.outputPropertiesForAnchor(sourceAnchor)
          : {};
        const propertyKind = sourceProperties[mapping.sourceProperty];
        if (!sourceNode || !propertyKind) {
          return {
            ...mapping,
            bridgePosition: normalizeBridgePosition(mapping.bridgePosition),
            sourceNodeId: sourceNode?.id ?? null,
            sourceBlueprintNodeId: sourceNode?.instanceId ?? mapping.sourceBlueprintNodeId,
            sourceNodeName: sourceNode?.name ?? mapping.sourceNodeName,
          };
        }

        const targetNode = mapping.targetBlueprintNodeId || mapping.targetNodeId !== null
          ? findPhaseNode(destinationPhase.nodes, mapping.targetNodeId, mapping.targetBlueprintNodeId)
          : null;
        const targetKind = mapping.targetProperty && targetNode
          ? targetNode.inputProperties[mapping.targetProperty]
          : null;
        return {
          ...mapping,
          bridgePosition: normalizeBridgePosition(mapping.bridgePosition),
          sourceNodeId: sourceNode.id,
          sourceBlueprintNodeId: sourceNode.instanceId,
          sourceNodeName: sourceNode.name,
          propertyKind,
          targetNodeId: targetKind === propertyKind ? targetNode!.id : null,
          targetBlueprintNodeId: targetKind === propertyKind ? targetNode!.instanceId : null,
          targetNodeName: targetKind === propertyKind ? targetNode!.name : null,
          targetProperty: targetKind === propertyKind ? mapping.targetProperty : null,
        };
      }),
    };
  });
}

function findPhaseNode(nodes: readonly NodeModel[], nodeId: number | null, instanceId: string | null): NodeModel | null {
  return nodes.find((node) =>
    (!!instanceId && node.instanceId === instanceId) ||
    (nodeId !== null && node.id === nodeId)) ?? null;
}

function outputAnchorIndexByChannel(node: NodeModel, channelName: string | null): number | null {
  if (!channelName) {
    return null;
  }

  const outputIndex = node.outputChannelNames.indexOf(channelName);
  return outputIndex < 0 ? null : outputIndex + (node.inputChannelName ? 1 : 0);
}

function bridgeMappingSourceKey(
  mapping: Pick<DataBridgeMappingDraft, 'sourceBlueprintNodeId' | 'sourceNodeId' | 'sourceChannelName' | 'sourceProperty'>,
): string {
  return [
    mapping.sourceBlueprintNodeId ?? `local:${mapping.sourceNodeId ?? 'unknown'}`,
    mapping.sourceChannelName ?? '',
    mapping.sourceProperty,
  ].join('|');
}

function sortMappings(mappings: readonly DataBridgeMappingDraft[]): DataBridgeMappingDraft[] {
  return [...mappings].sort((left, right) =>
    left.sourceNodeName.localeCompare(right.sourceNodeName) ||
    left.sourceProperty.localeCompare(right.sourceProperty));
}

function normalizeBridgePosition(value: number | null | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 0.5;
  }

  return Math.min(1, Math.max(0, value));
}
