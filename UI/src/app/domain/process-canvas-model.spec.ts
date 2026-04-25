import { describe, expect, it } from 'vitest';
import { buildProcessCanvasNodes, type ProcessCanvasDetail } from './process-canvas-model';
import type { ITaskDefinition, ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

describe('buildProcessCanvasNodes', () => {
  it('hydrates readonly process nodes with resolved task-version input and output properties', () => {
    const [node] = buildProcessCanvasNodes(createProcessDetail(), [createTaskDefinition()]);

    expect(node.inputChannelName).toBe('Request');
    expect(node.inputChannelLabel).toBe('Launch request');
    expect(node.inputProperties).toEqual({
      airportCode: 'String',
      operationDate: 'Date',
    });
    expect(node.inputPropertyDefinitions.map((property) => property.name)).toEqual([
      'airportCode',
      'operationDate',
    ]);
    expect(node.outputChannelNames).toEqual(['Weather']);
    expect(node.outputChannelDisplayNames).toEqual(['Weather context']);
    expect(node.outputChannelProperties).toEqual({
      Weather: {
        summary: 'String',
        severity: 'Integer',
      },
    });
    expect(node.outputChannelPropertyDefinitions.Weather?.map((property) => property.name)).toEqual([
      'summary',
      'severity',
    ]);
  });

  it('falls back to process channel names when task metadata is unavailable', () => {
    const [node] = buildProcessCanvasNodes(createProcessDetail(), []);

    expect(node.inputChannelName).toBe('LegacyInput');
    expect(node.outputChannelNames).toEqual(['LegacyOutput']);
    expect(node.inputProperties).toEqual({});
    expect(node.outputChannelProperties).toEqual({});
  });
});

function createProcessDetail(): ProcessCanvasDetail {
  return {
    nodes: [
      {
        taskDefinitionId: 42,
        allowedMajor: 0,
        allowedMinor: 1,
        resolvedVersionId: 4201,
        resolvedVersion: '0.1.0',
        name: 'Airport Weather',
        status: 'Completed',
        position: { x: 120, y: 80 },
        inputChannelName: 'LegacyInput',
        outputChannelNames: ['LegacyOutput'],
      },
    ],
  };
}

function createTaskDefinition(): ITaskDefinition {
  return {
    id: 42,
    name: 'Airport Weather',
    versions: [
      {
        id: 4201,
        version: '0.1.0',
        state: 'Active',
        description: null,
        requiredGlobalState: null,
        inputChannel: {
          id: 1,
          name: 'Request',
          displayName: 'Launch request',
          properties: [
            property('airportCode', 'String', true),
            property('operationDate', 'Date', true),
          ],
        },
        outputChannels: [
          {
            id: 2,
            name: 'Weather',
            displayName: 'Weather context',
            properties: [
              property('summary', 'String', true),
              property('severity', 'Integer', true),
            ],
          },
        ],
      },
    ],
  };
}

function property(
  name: string,
  kind: ITaskPropertyDefinition['kind'],
  required: boolean,
): ITaskPropertyDefinition {
  return {
    name,
    kind,
    required,
    enumValues: [],
  };
}
