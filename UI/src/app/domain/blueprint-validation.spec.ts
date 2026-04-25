import { describe, expect, it } from 'vitest';
import { validateBlueprintGraph } from './blueprint-validation';
import { ConnectionModel } from './connection-model';
import { NodeModel, TaskPropertyType } from './node-model';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

interface TestNodeOptions {
  inputProperties?: readonly ITaskPropertyDefinition[];
  outputChannels?: Record<string, readonly ITaskPropertyDefinition[]>;
  designTimeInputOverrides?: Record<string, unknown>;
}

describe('validateBlueprintGraph', () => {
  it('reports cyclic back-edges as errors', () => {
    const first = createNode(1, 'First', {
      inputProperties: [],
      outputChannels: { Success: [] },
    });
    const second = createNode(2, 'Second', {
      inputProperties: [],
      outputChannels: { Success: [] },
    });
    const connections = [
      connect(first, second, 'first-to-second'),
      connect(second, first, 'second-to-first'),
    ];

    const summary = validateBlueprintGraph({
      blueprintName: 'Cycle',
      nodes: [first, second],
      connections,
    });

    expect(summary.errors.map((issue) => issue.code)).toContain('connection.cycle');
    expect(summary.canSave).toBe(false);
  });

  it('reports nodes that are unreachable from the selected start node', () => {
    const start = createNode(1, 'Start', { outputChannels: { Success: [] } });
    const reachable = createNode(2, 'Reachable', { inputProperties: [], outputChannels: {} });
    const stranded = createNode(3, 'Stranded', { outputChannels: {} });

    const summary = validateBlueprintGraph({
      blueprintName: 'Reachability',
      nodes: [start, reachable, stranded],
      connections: [connect(start, reachable)],
    });

    expect(summary.errors).toContainEqual(expect.objectContaining({
      code: 'node.unreachable',
      nodeId: stranded.id,
    }));
  });

  it('allows required non-start inputs satisfied by incoming mappings', () => {
    const source = createNode(1, 'Source', {
      outputChannels: {
        Success: [property('town')],
      },
    });
    const target = createNode(2, 'Target', {
      inputProperties: [property('town', true)],
    });

    const summary = validateBlueprintGraph({
      blueprintName: 'Mapped',
      nodes: [source, target],
      connections: [connect(source, target, 'mapped', [{ sourceProperty: 'town', targetProperty: 'town' }])],
    });

    expect(summary.errors.map((issue) => issue.code)).not.toContain('node.input.requiredMissing');
    expect(summary.canSave).toBe(true);
  });

  it('allows required non-start inputs satisfied by design-time overrides', () => {
    const source = createNode(1, 'Source', {
      outputChannels: {
        Success: [property('other')],
      },
    });
    const target = createNode(2, 'Target', {
      inputProperties: [property('town', true)],
      designTimeInputOverrides: { town: 'New Alexandria' },
    });

    const summary = validateBlueprintGraph({
      blueprintName: 'Configured',
      nodes: [source, target],
      connections: [connect(source, target)],
    });

    expect(summary.errors.map((issue) => issue.code)).not.toContain('node.input.requiredMissing');
    expect(summary.canSave).toBe(true);
  });

  it('does not require optional input properties to be mapped', () => {
    const source = createNode(1, 'Source', {
      outputChannels: {
        Success: [property('other')],
      },
    });
    const target = createNode(2, 'Target', {
      inputProperties: [property('town', false)],
    });

    const summary = validateBlueprintGraph({
      blueprintName: 'Optional',
      nodes: [source, target],
      connections: [connect(source, target)],
    });

    expect(summary.errors.map((issue) => issue.code)).not.toContain('node.input.requiredMissing');
    expect(summary.canSave).toBe(true);
  });

  it('reports stale source and target property mappings as errors', () => {
    const source = createNode(1, 'Source', {
      outputChannels: {
        Success: [property('actualSource')],
      },
    });
    const target = createNode(2, 'Target', {
      inputProperties: [property('actualTarget', false)],
    });

    const summary = validateBlueprintGraph({
      blueprintName: 'Stale',
      nodes: [source, target],
      connections: [
        connect(source, target, 'stale', [
          { sourceProperty: 'missingSource', targetProperty: 'actualTarget' },
          { sourceProperty: 'actualSource', targetProperty: 'missingTarget' },
        ]),
      ],
    });

    expect(summary.errors.map((issue) => issue.code)).toEqual(expect.arrayContaining([
      'connection.mapping.sourceMissing',
      'connection.mapping.targetMissing',
    ]));
    expect(summary.canSave).toBe(false);
  });

  it('warns when non-end output properties and channels are unused', () => {
    const source = createNode(1, 'Source', {
      outputChannels: {
        Success: [property('town'), property('temperature')],
        Audit: [property('event')],
      },
    });
    const target = createNode(2, 'Target', {
      inputProperties: [property('town', false)],
    });

    const summary = validateBlueprintGraph({
      blueprintName: 'Warnings',
      nodes: [source, target],
      connections: [connect(source, target, 'mapped', [{ sourceProperty: 'town', targetProperty: 'town' }])],
    });

    expect(summary.warnings).toEqual(expect.arrayContaining([
      expect.objectContaining({ code: 'node.output.propertyUnused', propertyName: 'temperature' }),
      expect.objectContaining({ code: 'node.output.channelUnmapped', channelName: 'Audit' }),
    ]));
    expect(summary.canSave).toBe(true);
  });

  it('includes route obstruction warnings from the canvas snapshot', () => {
    const source = createNode(1, 'Source', { outputChannels: { Success: [] } });
    const target = createNode(2, 'Target', { inputProperties: [] });
    const connection = connect(source, target, 'blocked');

    const summary = validateBlueprintGraph({
      blueprintName: 'Route warnings',
      nodes: [source, target],
      connections: [connection],
      routeValidationErrors: [
        {
          connectionId: connection.id,
          routeIndex: 0,
          message: "Connection crosses another node's reserved routing buffer.",
        },
      ],
    });

    expect(summary.warnings).toContainEqual(expect.objectContaining({
      code: 'connection.route.obstructed',
      connectionId: connection.id,
      routeIndex: 0,
    }));
  });
});

function createNode(id: number, name: string, options: TestNodeOptions = {}): NodeModel {
  const inputProperties = options.inputProperties ?? [];
  const outputChannels = options.outputChannels ?? {};
  const outputChannelNames = Object.keys(outputChannels);
  return new NodeModel(
    id,
    id,
    0,
    0,
    id,
    '0.0.1',
    name,
    id * 420,
    0,
    320,
    220,
    inputProperties.length > 0 ? 'Input' : null,
    outputChannelNames,
    false,
    null,
    null,
    Object.fromEntries(inputProperties.map((item) => [item.name, item.kind] as const)),
    Object.fromEntries(outputChannelNames.map((channelName) => [
      channelName,
      Object.fromEntries((outputChannels[channelName] ?? []).map((item) => [item.name, item.kind] as const)),
    ])),
    options.designTimeInputOverrides ?? {},
    [...inputProperties],
    inputProperties.length > 0 ? 'Input' : null,
    outputChannelNames,
    Object.fromEntries(outputChannelNames.map((channelName) => [channelName, [...(outputChannels[channelName] ?? [])]])),
  );
}

function property(name: string, required = false, kind: TaskPropertyType = 'String'): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required,
    enumValues: [],
  };
}

function connect(
  source: NodeModel,
  target: NodeModel,
  id = `${source.id}-${target.id}`,
  propertyMappings: ConnectionModel['propertyMappings'] = [],
): ConnectionModel {
  const anchor = source.outputAnchors()[0]?.index ?? 0;
  return new ConnectionModel(
    source,
    target,
    anchor,
    'left',
    target.inputAnchorSnap()?.pos ?? 62,
    id,
    propertyMappings,
  );
}
