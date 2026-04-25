import { describe, it, expect } from 'vitest';
import {
  ConnectionModel,
  buildConnectionMappingSelections,
  describeConnectionBehavior,
} from './connection-model';
import { NodeModel, Side } from './node-model';
import { PathfinderOptions } from './pathfinder';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 5 };

function createNode(
  id: number,
  name: string,
  x: number,
  y: number,
  options?: {
    inputChannelName?: string | null;
    outputChannelNames?: string[];
  },
): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id * 10,
    '0.0.1',
    name,
    x,
    y,
    320,
    220,
    options?.inputChannelName ?? null,
    options?.outputChannelNames ?? [],
    false,
  );
}

describe('ConnectionModel segments', () => {
  const source = createNode(1, 'Lookup', 0, 0, { outputChannelNames: ['Success'] });
  const destination = createNode(2, 'Forecast', 420, 80, {
    inputChannelName: 'Input',
    outputChannelNames: ['Done'],
  });
  const anchor = source.outputAnchors()[0];

  for (const side of ['left', 'right', 'top', 'bottom'] as Side[]) {
    it(`forms an unbroken line to destination side ${side}`, () => {
      const connection = new ConnectionModel(
        source,
        destination,
        anchor.index,
        side,
        side === 'left' || side === 'right' ? destination.height / 2 : destination.width / 2,
      );
      const segments = connection.segments(opts);

      expect(segments[0].x1).toBe(anchor.x);
      expect(segments[0].y1).toBe(anchor.y);
      for (let i = 1; i < segments.length; i++) {
        expect(segments[i - 1].x2).toBe(segments[i].x1);
        expect(segments[i - 1].y2).toBe(segments[i].y1);
      }
    });
  }
});

describe('ConnectionModel behavior summaries', () => {
  function createRichNode(
    id: number,
    name: string,
    x: number,
    y: number,
    options: {
      inputProperties?: Record<string, 'Integer' | 'String' | 'Boolean' | 'Guid'>;
      outputProperties?: Record<string, Record<string, 'Integer' | 'String' | 'Boolean' | 'Guid'>>;
    },
  ): NodeModel {
    return new NodeModel(
      id,
      id,
      0,
      0,
      id * 10,
      '0.0.1',
      name,
      x,
      y,
      320,
      220,
      options.inputProperties ? 'Input' : null,
      Object.keys(options.outputProperties ?? {}),
      false,
      null,
      null,
      options.inputProperties ?? {},
      options.outputProperties ?? {},
    );
  }

  it('classifies direct same-name transfer as plain', () => {
    const source = createRichNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          holiday: 'String',
        },
      },
    });
    const destination = createRichNode(2, 'Destination', 420, 0, {
      inputProperties: {
        town: 'String',
        holiday: 'String',
      },
    });

    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'plain-1', [
      { sourceProperty: 'holiday', targetProperty: 'holiday' },
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    expect(describeConnectionBehavior(connection, [connection]).kind).toBe('plain');
  });

  it('classifies remapped or partial transfer as mapped', () => {
    const source = createRichNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          holiday: 'String',
        },
      },
    });
    const destination = createRichNode(2, 'Destination', 420, 0, {
      inputProperties: {
        mappedTown: 'String',
        holiday: 'String',
      },
    });

    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'mapped-1', [
      { sourceProperty: 'town', targetProperty: 'mappedTown' },
    ]);

    const summary = describeConnectionBehavior(connection, [connection]);
    expect(summary.kind).toBe('mapped');
    expect(summary.unmappedTargetCount).toBe(1);
  });

  it('classifies same-output fan-out as dotted fan-out', () => {
    const source = createRichNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destinationA = createRichNode(2, 'A', 420, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const destinationB = createRichNode(3, 'B', 420, 260, {
      inputProperties: {
        town: 'String',
      },
    });

    const first = new ConnectionModel(source, destinationA, 0, 'left', 62, 'fanout-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destinationB, 0, 'left', 62, 'fanout-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    expect(describeConnectionBehavior(first, [first, second]).kind).toBe('fanOut');
  });

  it('classifies mapped fan-out when a split edge also remaps properties', () => {
    const source = createRichNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          holiday: 'String',
        },
      },
    });
    const destinationA = createRichNode(2, 'A', 420, 0, {
      inputProperties: {
        mappedTown: 'String',
      },
    });
    const destinationB = createRichNode(3, 'B', 420, 260, {
      inputProperties: {
        town: 'String',
      },
    });

    const first = new ConnectionModel(source, destinationA, 0, 'left', 62, 'mapped-fanout-a', [
      { sourceProperty: 'town', targetProperty: 'mappedTown' },
    ]);
    const second = new ConnectionModel(source, destinationB, 0, 'left', 62, 'mapped-fanout-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    expect(describeConnectionBehavior(first, [first, second]).kind).toBe('mappedFanOut');
  });

  it('builds modal selections with direct mappings by default', () => {
    const source = createRichNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          holiday: 'String',
        },
      },
    });
    const destination = createRichNode(2, 'Destination', 420, 0, {
      inputProperties: {
        town: 'String',
        holiday: 'String',
      },
    });

    const connection = new ConnectionModel(source, destination, 0, 'left', 62);
    expect(buildConnectionMappingSelections(connection)).toEqual({
      holiday: 'holiday',
      town: 'town',
    });
  });
});
