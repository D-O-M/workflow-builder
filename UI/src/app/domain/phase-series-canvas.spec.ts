import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import { ConnectionModel } from './connection-model';
import { NodeModel, TaskPropertyType } from './node-model';
import {
  DataBridgeMappingDraft,
  PhaseSeriesCanvasModel,
  clearDataBridgeMappingTarget,
  completeDataBridgeMappingTarget,
  createCorrelationMapping,
  createPhasePairOptions,
  createTransitionDraft,
  reconcileTransitionsForPhaseVersionChange,
  serializeTransitionMappings,
  upsertDataBridgeSourceMapping,
  validatePhaseSeries,
} from './phase-series-canvas';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

function property(name: string, kind: TaskPropertyType): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required: false,
    enumValues: [],
  };
}

function createTaskNode(options: {
  id: number;
  name: string;
  x?: number;
  y?: number;
  instanceId: string;
  inputProperties?: Record<string, TaskPropertyType>;
  outputProperties?: Record<string, TaskPropertyType>;
}): NodeModel {
  const inputProperties = options.inputProperties ?? {};
  const outputProperties = options.outputProperties ?? {};
  const outputChannelNames = Object.keys(outputProperties).length > 0 ? ['Success'] : [];
  return new NodeModel(
    options.id,
    options.id * 10,
    1,
    0,
    options.id * 100,
    '1.0.0',
    options.name,
    options.x ?? 100,
    options.y ?? 100,
    320,
    220,
    Object.keys(inputProperties).length > 0 ? 'Input' : null,
    outputChannelNames,
    false,
    null,
    null,
    inputProperties,
    outputChannelNames.length > 0 ? { Success: outputProperties } : {},
    {},
    Object.entries(inputProperties).map(([name, kind]) => property(name, kind)),
    Object.keys(inputProperties).length > 0 ? 'Input' : null,
    outputChannelNames,
    outputChannelNames.length > 0
      ? { Success: Object.entries(outputProperties).map(([name, kind]) => property(name, kind)) }
      : {},
    true,
    'PluginTask',
    null,
    options.instanceId,
  );
}

function createModel(mappingOverrides?: Partial<DataBridgeMappingDraft>): PhaseSeriesCanvasModel {
  const source = createTaskNode({
    id: 1,
    name: 'Lookup',
    instanceId: 'source-node',
    outputProperties: {
      result: 'String',
    },
  });
  const destination = createTaskNode({
    id: 2,
    name: 'Start',
    instanceId: 'destination-node',
    inputProperties: {
      result: 'String',
    },
  });
  const mapping: DataBridgeMappingDraft = {
    id: 'mapping-1',
    sourceNodeId: source.id,
    sourceBlueprintNodeId: source.instanceId,
    sourceNodeName: source.name,
    sourceChannelName: 'Success',
    sourceProperty: 'result',
    propertyKind: 'String',
    bridgePosition: 0.5,
    targetNodeId: null,
    targetBlueprintNodeId: null,
    targetNodeName: null,
    targetProperty: null,
    ...mappingOverrides,
  };

  return {
    phases: [
      {
        phaseId: 'phase-1',
        name: 'Phase 1',
        blueprintName: 'Lookup Blueprint',
        blueprintDefinitionId: 10,
        blueprintVersionId: 101,
        blueprintVersionNumber: 1,
        nodes: [source],
        connections: [],
      },
      {
        phaseId: 'phase-2',
        name: 'Phase 2',
        blueprintName: 'Start Blueprint',
        blueprintDefinitionId: 20,
        blueprintVersionId: 201,
        blueprintVersionNumber: 1,
        nodes: [destination],
        connections: [],
      },
    ],
    transitions: [
      createTransitionDraft('phase-1', 'phase-2', [mapping], 'transition-1'),
    ],
  };
}

describe('phase-series canvas utilities', () => {
  it('creates adjacent phase-pair options', () => {
    const model = createModel();

    expect(createPhasePairOptions(model.phases)).toEqual([
      {
        index: 0,
        label: 'Phase 1 -> Phase 2',
      },
    ]);
  });

  it('creates and serializes Data Bridge mappings from divider interactions', () => {
    const model = createModel();
    const source = model.phases[0]!.nodes[0]!;
    const destination = model.phases[1]!.nodes[0]!;
    const transition = upsertDataBridgeSourceMapping(
      createTransitionDraft('phase-1', 'phase-2', [], 'transition-1'),
      {
        sourceNodeId: source.id,
        sourceBlueprintNodeId: source.instanceId,
        sourceNodeName: source.name,
        sourceChannelName: 'Success',
        sourceProperty: 'result',
        propertyKind: 'String',
      },
      0.25,
    );
    const result = completeDataBridgeMappingTarget(
      transition,
      model.phases[1]!,
      transition.mappings[0]!.id,
      {
        targetNodeId: destination.id,
        targetBlueprintNodeId: destination.instanceId,
        targetNodeName: destination.name,
        targetProperty: 'result',
        propertyKind: 'String',
      },
    );

    expect(result.accepted).toBe(true);
    expect(result.transition.mappings).toEqual([
      expect.objectContaining({
        sourceBlueprintNodeId: 'source-node',
        sourceChannelName: 'Success',
        sourceProperty: 'result',
        propertyKind: 'String',
        bridgePosition: 0.25,
        targetNodeId: destination.id,
        targetBlueprintNodeId: 'destination-node',
        targetNodeName: 'Start',
        targetProperty: 'result',
      }),
    ]);

    const payload = serializeTransitionMappings({
      ...model.transitions[0]!,
      mappings: result.transition.mappings,
    });
    expect(payload.handoffMappings).toEqual([
      {
        sourceKind: 'Output',
        sourceBlueprintNodeId: 'source-node',
        sourceChannelName: 'Success',
        sourceProperty: 'result',
        bridgePosition: 0.25,
        targetBlueprintNodeId: 'destination-node',
        targetProperty: 'result',
      },
    ]);
  });

  it('serializes explicit correlation mappings against Data Bridge result properties', () => {
    const model = createModel({
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });
    const transition = {
      ...model.transitions[0]!,
      correlationMappings: [
        createCorrelationMapping(' result ', ' BuildId ', 'correlation-1'),
      ],
    };

    expect(serializeTransitionMappings(transition).correlationMappings).toEqual([
      {
        sourceResultProperty: 'result',
        triggerProperty: 'BuildId',
      },
    ]);
    expect(validatePhaseSeries({
      ...model,
      transitions: [transition],
    })).toEqual([]);
  });

  it('flags duplicate correlation trigger property names', () => {
    const model = createModel({
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });
    const transition = {
      ...model.transitions[0]!,
      correlationMappings: [
        createCorrelationMapping('result', 'BuildId', 'correlation-1'),
        createCorrelationMapping('result', 'BuildId', 'correlation-2'),
      ],
    };

    expect(validatePhaseSeries({
      ...model,
      transitions: [transition],
    }).map((issue) => issue.message)).toContain('BuildId is already used as a trigger property.');
  });

  it('marks incompatible bridge mappings incomplete after a phase version change', () => {
    const model = createModel({
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });
    const changedDestination = createTaskNode({
      id: 2,
      name: 'Start',
      instanceId: 'destination-node',
      inputProperties: {
        result: 'Boolean',
      },
    });
    const changedModel: PhaseSeriesCanvasModel = {
      ...model,
      phases: [
        model.phases[0]!,
        {
          ...model.phases[1]!,
          nodes: [changedDestination],
        },
      ],
    };

    const transitions = reconcileTransitionsForPhaseVersionChange(changedModel, 1);
    const reconciledModel = {
      ...changedModel,
      transitions,
    };

    expect(transitions[0]!.mappings[0]!.targetProperty).toBeNull();
    expect(validatePhaseSeries(reconciledModel).map((issue) => issue.message)).toContain(
      'result needs a destination input.',
    );
  });

  it('moves an existing source bridge anchor without clearing a compatible destination', () => {
    const model = createModel({
      bridgePosition: 0.2,
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });
    const source = model.phases[0]!.nodes[0]!;

    const transition = upsertDataBridgeSourceMapping(
      model.transitions[0]!,
      {
        sourceNodeId: source.id,
        sourceBlueprintNodeId: source.instanceId,
        sourceNodeName: source.name,
        sourceChannelName: 'Success',
        sourceProperty: 'result',
        propertyKind: 'String',
      },
      0.8,
    );

    expect(transition.mappings).toHaveLength(1);
    expect(transition.mappings[0]).toEqual(expect.objectContaining({
      id: 'mapping-1',
      bridgePosition: 0.8,
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    }));
  });

  it('clears only the destination side while keeping the source bridge anchor', () => {
    const model = createModel({
      bridgePosition: 0.35,
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });

    const transition = clearDataBridgeMappingTarget(model.transitions[0]!, 'mapping-1');

    expect(transition.mappings[0]).toEqual(expect.objectContaining({
      bridgePosition: 0.35,
      sourceBlueprintNodeId: 'source-node',
      sourceProperty: 'result',
      targetNodeId: null,
      targetBlueprintNodeId: null,
      targetNodeName: null,
      targetProperty: null,
    }));
  });

  it('serializes bridge output drops onto any compatible destination node', () => {
    const model = createModel();
    const nonStartDestination = createTaskNode({
      id: 3,
      name: 'Later',
      x: 460,
      instanceId: 'later-node',
      inputProperties: {
        result: 'String',
      },
    });
    const destinationPhase = model.phases[1]!;
    const changedModel: PhaseSeriesCanvasModel = {
      ...model,
      phases: [
        model.phases[0]!,
        {
          ...destinationPhase,
          nodes: [...destinationPhase.nodes, nonStartDestination],
          connections: [
            new ConnectionModel(
              destinationPhase.nodes[0]!,
              nonStartDestination,
              0,
              'left',
              nonStartDestination.inputAnchorSnap()!.pos,
            ),
          ],
        },
      ],
    };
    const result = completeDataBridgeMappingTarget(
      changedModel.transitions[0]!,
      changedModel.phases[1]!,
      changedModel.transitions[0]!.mappings[0]!.id,
      {
        targetNodeId: nonStartDestination.id,
        targetBlueprintNodeId: nonStartDestination.instanceId,
        targetNodeName: nonStartDestination.name,
        targetProperty: 'result',
        propertyKind: 'String',
      },
    );

    expect(result.accepted).toBe(true);
    expect(serializeTransitionMappings({
      ...changedModel.transitions[0]!,
      mappings: result.transition.mappings,
    }).handoffMappings).toEqual([
      expect.objectContaining({
        targetBlueprintNodeId: 'later-node',
        targetProperty: 'result',
      }),
    ]);
  });

  it('rejects duplicate Data Bridge drops to the same destination node property', () => {
    const model = createModel({
      targetNodeId: 2,
      targetBlueprintNodeId: 'destination-node',
      targetNodeName: 'Start',
      targetProperty: 'result',
    });
    const transition = {
      ...model.transitions[0]!,
      mappings: [
        model.transitions[0]!.mappings[0]!,
        {
          ...model.transitions[0]!.mappings[0]!,
          id: 'mapping-2',
          sourceProperty: 'otherResult',
          targetNodeId: null,
          targetBlueprintNodeId: null,
          targetNodeName: null,
          targetProperty: null,
        },
      ],
    };
    const destination = model.phases[1]!.nodes[0]!;

    const result = completeDataBridgeMappingTarget(
      transition,
      model.phases[1]!,
      'mapping-2',
      {
        targetNodeId: destination.id,
        targetBlueprintNodeId: destination.instanceId,
        targetNodeName: destination.name,
        targetProperty: 'result',
        propertyKind: 'String',
      },
    );

    expect(result.accepted).toBe(false);
  });
});
