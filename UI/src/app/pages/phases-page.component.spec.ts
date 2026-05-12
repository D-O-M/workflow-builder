import '@angular/compiler';
import {
  EnvironmentInjector,
  Provider,
  createEnvironmentInjector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
} from '@angular/core';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BlueprintEditorService, BlueprintGraphDocument, BlueprintViewModel, PhasedBlueprintViewModel } from '../domain/blueprint-editor.service';
import { BlueprintEditorStore } from '../domain/blueprint-editor.store';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { PhasesPageComponent } from './phases-page.component';

function createBlueprint(id: number, name: string, versions: number[]): BlueprintViewModel {
  return {
    id,
    name,
    description: '',
    versions: versions.map((version) => ({
      blueprintDefinitionId: id,
      blueprintVersionId: id * 100 + version,
      description: null,
      version,
      createdAt: '2026-05-07T10:00:00Z',
      startingBlueprintNodeId: 'start',
      tasks: [],
    })),
  };
}

function createPhasedSeries(
  id: number,
  blueprints: readonly BlueprintViewModel[],
  pinnedVersionNumber = 1,
): PhasedBlueprintViewModel {
  return {
    id,
    key: `series-${id}`,
    name: 'Customer Onboarding',
    description: '',
    versions: [
      {
        phasedBlueprintVersionId: id * 100,
        phasedBlueprintDefinitionId: id,
        version: 1,
        description: null,
        createdAt: '2026-05-07T10:00:00Z',
        phases: blueprints.slice(0, 2).map((blueprint, index) => ({
          phaseId: `phase-${index + 1}`,
          phaseIndex: index,
          name: `Phase ${index + 1}`,
          blueprintDefinitionId: blueprint.id,
          blueprintName: blueprint.name,
          blueprintVersionId: blueprint.id * 100 + pinnedVersionNumber,
          blueprintVersionNumber: pinnedVersionNumber,
          versionPolicy: 'Pinned',
        })),
        transitions: [
          {
            transitionId: 'transition-1',
            sourcePhaseId: 'phase-1',
            destinationPhaseId: 'phase-2',
            handoffMappings: [],
            correlationMappings: [],
          },
        ],
      },
    ],
  };
}

describe('PhasesPageComponent', () => {
  let component: PhasesPageComponent;
  let injector: EnvironmentInjector;
  let blueprintList: BlueprintViewModel[];
  let phasedBlueprintList: PhasedBlueprintViewModel[];
  let editorService: {
    getBlueprints: ReturnType<typeof vi.fn>;
    getPhasedBlueprints: ReturnType<typeof vi.fn>;
    getPhasedBlueprintById: ReturnType<typeof vi.fn>;
    loadBlueprintGraph: ReturnType<typeof vi.fn>;
    createPhasedBlueprint: ReturnType<typeof vi.fn>;
    createPhasedBlueprintVersion: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    blueprintList = [
      createBlueprint(1, 'Intake', [2, 1]),
      createBlueprint(2, 'Fulfilment', [1]),
    ];
    phasedBlueprintList = [createPhasedSeries(9, blueprintList, 1)];

    editorService = {
      getBlueprints: vi.fn(() => of(blueprintList)),
      getPhasedBlueprints: vi.fn(() => of(phasedBlueprintList)),
      getPhasedBlueprintById: vi.fn((definitionId: number) =>
        of(phasedBlueprintList.find((series) => series.id === definitionId)!)),
      loadBlueprintGraph: vi.fn((definitionId: number, _tasks: unknown, versionId?: number | null) => {
        const blueprint = blueprintList.find((candidate) => candidate.id === definitionId)!;
        const graph: BlueprintGraphDocument = {
          blueprint,
          nodes: [],
          connections: [],
        };
        expect(versionId ?? blueprint.versions[0]?.blueprintVersionId ?? null).not.toBeNull();
        return of(graph);
      }),
      createPhasedBlueprint: vi.fn((payload) => {
        const created: PhasedBlueprintViewModel = {
          id: 42,
          key: 'series-42',
          name: payload.name,
          description: payload.description ?? '',
          versions: [
            {
              phasedBlueprintVersionId: 4200,
              phasedBlueprintDefinitionId: 42,
              version: 1,
              description: payload.description ?? null,
              createdAt: '2026-05-07T10:00:00Z',
              phases: payload.phases.map((phase: any) => ({
                ...phase,
                blueprintName: blueprintList.find((blueprint) => blueprint.id === phase.blueprintDefinitionId)?.name ?? '',
                blueprintVersionNumber: blueprintList
                  .find((blueprint) => blueprint.id === phase.blueprintDefinitionId)
                  ?.versions.find((version) => version.blueprintVersionId === phase.blueprintVersionId)
                  ?.version ?? null,
              })),
              transitions: payload.transitions,
            },
          ],
        };
        phasedBlueprintList = [created];
        return of(created);
      }),
      createPhasedBlueprintVersion: vi.fn(),
    };

    const providers: Provider[] = [
      BlueprintEditorStore,
      { provide: BlueprintEditorService, useValue: editorService },
      {
        provide: TaskDefinitionsService,
        useValue: {
          getTaskDefinitions: () => of([]),
        },
      },
      {
        provide: Router,
        useValue: {
          navigate: vi.fn(),
        },
      },
      {
        provide: ChangeDetectionScheduler,
        useValue: {
          notify: vi.fn(),
          runningTick: false,
        },
      },
      {
        provide: EffectScheduler,
        useValue: {
          add: vi.fn(),
          schedule: vi.fn(),
          flush: vi.fn(),
          remove: vi.fn(),
        },
      },
    ];

    injector = createEnvironmentInjector(providers, null as never);
    component = runInInjectionContext(injector, () => new PhasesPageComponent());
    await Promise.resolve();
  });

  afterEach(() => {
    injector.destroy();
  });

  it('summarizes saved phase series and flags older pinned blueprint versions', () => {
    const summaries = (component as any).seriesSummaries();

    expect(summaries[0]).toEqual(expect.objectContaining({
      name: 'Customer Onboarding',
      version: 1,
      phaseCount: 2,
      updateAvailable: true,
    }));
    expect(summaries[0].selectedBlueprints).toContain('Intake v1');
  });

  it('opens phase setup when creating a draft series', () => {
    (component as any).phasePanelOpen.set(false);

    (component as any).createSeries();

    expect((component as any).phasePanelOpen()).toBe(true);
    expect((component as any).addPhaseSelection()).toBeNull();
    expect((component as any).selectedSeries().phases).toEqual([]);
  });

  it('uses ambient connection presentation for both phase canvases', () => {
    expect((component as any).sourceCanvasRuntime().connectionPresentation).toBe('ambient');
    expect((component as any).destinationCanvasRuntime().connectionPresentation).toBe('ambient');
  });

  it('keeps setup open until the second phase is added and resets the add selection', async () => {
    (component as any).createSeries();

    (component as any).addPhaseSelection.set('1:101');
    await (component as any).addPhaseFromSelection('1:101');

    expect((component as any).phasePanelOpen()).toBe(true);
    expect((component as any).addPhaseSelection()).toBeNull();

    (component as any).addPhaseSelection.set('2:201');
    await (component as any).addPhaseFromSelection('2:201');

    expect((component as any).phasePanelOpen()).toBe(false);
    expect((component as any).addPhaseSelection()).toBeNull();
    expect((component as any).selectedSeries().phases).toHaveLength(2);
  });

  it('clears an active bridge drag when Escape is pressed', () => {
    (component as any).setBridgeDrag({
      kind: 'bridge-output',
      clientX: 100,
      clientY: 120,
      label: 'Output',
      propertyKind: 'String',
      mappingId: 'mapping-1',
    });
    (component as any).bridgePreviewPosition.set(0.5);
    (component as any).bridgeDropMessage.set('Drop on the bridge.');
    const preventDefault = vi.fn();

    (component as any).onWindowKeyDown({
      key: 'Escape',
      preventDefault,
      target: null,
    } as unknown as KeyboardEvent);

    expect(preventDefault).toHaveBeenCalled();
    expect((component as any).bridgeDrag()).toBeNull();
    expect((component as any).bridgePreviewPosition()).toBeNull();
    expect((component as any).bridgeDropMessage()).toBeNull();
  });

  it('clears invalid source drops and shows bridge guidance', () => {
    (component as any).setBridgeDrag({
      kind: 'source-output',
      clientX: -100,
      clientY: -100,
      label: 'Result',
      propertyKind: 'String',
      mappingId: null,
      bridgePosition: null,
    });
    (component as any).bridgePreviewPosition.set(0.25);

    (component as any).onSourceBridgeDragEnded({
      cancelled: false,
      clientX: -100,
      clientY: -100,
      nodeId: 7,
      nodeInstanceId: 'source-node',
      nodeName: 'Source',
      channelName: 'Output',
      propertyName: 'Result',
      propertyKind: 'String',
    });

    expect((component as any).bridgeDrag()).toBeNull();
    expect((component as any).bridgePreviewPosition()).toBeNull();
    expect((component as any).bridgeDropMessage()).toBe('Drop on the bridge.');
  });

  it('stages a source property from a window-level pointer release on the bridge', async () => {
    (component as any).createSeries();
    await (component as any).addPhaseFromSelection('1:101');
    await (component as any).addPhaseFromSelection('2:201');
    (component as any).bridgeDivider = () => ({
      nativeElement: {
        getBoundingClientRect: () => ({
          left: 10,
          right: 110,
          top: 20,
          bottom: 40,
          width: 100,
        }),
      },
    });
    (component as any).setBridgeDrag({
      kind: 'source-output',
      clientX: 60,
      clientY: 30,
      label: 'Source.Result',
      propertyKind: 'String',
      mappingId: null,
      bridgePosition: 0.5,
      sourceNodeId: 7,
      sourceBlueprintNodeId: 'source-node',
      sourceNodeName: 'Source',
      sourceChannelName: 'Output',
      sourceProperty: 'Result',
    });

    (component as any).onWindowPointerUp({
      clientX: 60,
      clientY: 30,
    } as PointerEvent);

    const mappings = (component as any).selectedSeries().transitions[0].mappings;
    expect(mappings).toEqual([
      expect.objectContaining({
        sourceNodeName: 'Source',
        sourceProperty: 'Result',
        bridgePosition: 0.5,
      }),
    ]);
    expect((component as any).bridgeDrag()).toBeNull();
    expect((component as any).bridgeDropMessage()).toBeNull();
  });

  it('clears invalid destination drops and shows target guidance', () => {
    (component as any).setBridgeDrag({
      kind: 'bridge-output',
      clientX: -100,
      clientY: -100,
      label: 'Result',
      propertyKind: 'String',
      mappingId: 'mapping-1',
    });

    (component as any).finishBridgeOutputDrag('mapping-1', -100, -100, false);

    expect((component as any).bridgeDrag()).toBeNull();
    expect((component as any).bridgeDropMessage()).toBe('Drop on a compatible destination input.');
  });

  it('keeps bridge guidance available as accessible text in icon-only split mode', () => {
    (component as any).splitMode.set('vertical');

    expect((component as any).bridgeGuidanceLabel()).toBe('Drag an expanded source output here.');

    (component as any).bridgeDropMessage.set('Drop on the bridge.');

    expect((component as any).bridgeGuidanceLabel()).toBe('Drop on the bridge.');
  });

  it('draws bridge curves with smooth outward tangents from node ports', () => {
    const workbenchElement = {
      getBoundingClientRect: () => ({
        left: 0,
        top: 0,
      }),
    } as HTMLElement;

    const path = (component as any).createBridgeCurvePath(
      workbenchElement,
      { clientX: 10, clientY: 20 },
      { clientX: 100, clientY: 180 },
      {
        startOutward: 'right',
        endOutward: 'left',
      },
    );

    expect(path).toBe('M 10 20 C 44 20 66 180 100 180');
    expect(path).not.toContain(' L ');
  });

  it('maps bridge handles along the selected split orientation', () => {
    (component as any).bridgeDivider = () => ({
      nativeElement: {
        getBoundingClientRect: () => ({
          left: 50,
          right: 74,
          top: 20,
          bottom: 220,
          width: 24,
          height: 200,
        }),
      },
    });

    expect((component as any).bridgeClientPoint('input', 0.25)).toEqual({
      clientX: 56,
      clientY: 20,
    });
    expect((component as any).bridgePositionFromClientPoint(56, 20)).toBe(0.25);
    expect((component as any).sourceCanvasViewportInsets().top).toBe(42);
    expect((component as any).destinationCanvasViewportInsets().top).toBe(42);

    (component as any).splitMode.set('vertical');

    expect((component as any).bridgeClientPoint('input', 0.25)).toEqual({
      clientX: 50,
      clientY: 70,
    });
    expect((component as any).bridgeClientPoint('output', 0.25)).toEqual({
      clientX: 74,
      clientY: 70,
    });
    expect((component as any).bridgePositionFromClientPoint(62, 70)).toBe(0.25);
    expect((component as any).bridgePositionFromClientPoint(112, 70)).toBeNull();
    expect((component as any).destinationCanvasViewportInsets().top).toBe(42);
  });

  it('does not draw bridge curves to property anchors outside the canvas viewport', async () => {
    (component as any).createSeries();
    await (component as any).addPhaseFromSelection('1:101');
    await (component as any).addPhaseFromSelection('2:201');
    const series = (component as any).selectedSeries();
    (component as any).selectedSeries.set({
      ...series,
      transitions: [
        {
          ...series.transitions[0],
          mappings: [
            {
              id: 'mapping-1',
              sourceNodeId: 7,
              sourceBlueprintNodeId: 'source-node',
              sourceNodeName: 'Source',
              sourceChannelName: 'Output',
              sourceProperty: 'Result',
              propertyKind: 'String',
              bridgePosition: 0.5,
              targetNodeId: null,
              targetBlueprintNodeId: null,
              targetNodeName: null,
              targetProperty: null,
            },
          ],
        },
      ],
    });
    (component as any).workbench = () => ({
      nativeElement: {
        getBoundingClientRect: () => ({
          left: 0,
          top: 0,
        }),
      },
    });
    (component as any).bridgeDivider = () => ({
      nativeElement: {
        getBoundingClientRect: () => ({
          left: 40,
          right: 140,
          top: 80,
          bottom: 104,
          width: 100,
        }),
      },
    });
    (component as any).sourceCanvas = () => ({
      bridge: {
        outputPropertyClientPoint: vi.fn(() => ({ clientX: 30, clientY: 260 })),
      },
      viewport: {
        containsClientPoint: vi.fn(() => false),
      },
    });

    (component as any).refreshBridgeLines();

    expect((component as any).bridgeCurves()).toEqual([]);
  });

  it('creates a series from pinned blueprint versions without creating duplicate blueprints', async () => {
    (component as any).createSeries();
    await (component as any).addPhaseFromSelection('1:101');
    await (component as any).addPhaseFromSelection('2:201');

    await (component as any).saveSeries();

    expect(editorService.createPhasedBlueprint).toHaveBeenCalledTimes(1);
    expect(editorService.createPhasedBlueprint.mock.calls[0][0].phases).toEqual([
      expect.objectContaining({
        blueprintDefinitionId: 1,
        blueprintVersionId: 101,
        versionPolicy: 'Pinned',
      }),
      expect.objectContaining({
        blueprintDefinitionId: 2,
        blueprintVersionId: 201,
        versionPolicy: 'Pinned',
      }),
    ]);
  });
});
