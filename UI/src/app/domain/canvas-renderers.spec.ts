import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { CanvasBackdropRenderer } from './canvas-backdrop-renderer';
import { CanvasConnectionPreviewRenderer } from './canvas-connection-preview-renderer';
import { CanvasRenderSurface } from './canvas-draw-surface';
import { CanvasNodeRenderer } from './canvas-node-renderer';
import { CanvasOverlayRenderer } from './canvas-overlay-renderer';
import { buildCanvasRenderSnapshot, RenderedNode } from './canvas-render-model';
import { CanvasTheme } from './canvas-theme';
import { NodeModel } from './node-model';

class RecordingCanvasContext {
  readonly fillRects: Array<{ x: number; y: number; width: number; height: number }> = [];
  readonly strokeRects: Array<{ x: number; y: number; width: number; height: number }> = [];
  readonly lineWidths: number[] = [];
  readonly strokeStyles: string[] = [];
  readonly fillStyles: string[] = [];
  readonly lineDashes: number[][] = [];
  readonly text: string[] = [];
  saveCount = 0;
  restoreCount = 0;
  fillCount = 0;
  strokeCount = 0;
  createPatternCount = 0;

  private currentLineWidth = 1;
  private currentStrokeStyle = '';
  private currentFillStyle = '';

  font = '';
  globalAlpha = 1;
  shadowColor = 'transparent';
  shadowBlur = 0;
  shadowOffsetY = 0;
  textAlign: CanvasTextAlign = 'start';
  textBaseline: CanvasTextBaseline = 'alphabetic';
  lineCap: CanvasLineCap = 'butt';
  lineJoin: CanvasLineJoin = 'miter';
  globalCompositeOperation: GlobalCompositeOperation = 'source-over';

  set lineWidth(value: number) {
    this.currentLineWidth = value;
    this.lineWidths.push(value);
  }

  get lineWidth(): number {
    return this.currentLineWidth;
  }

  set strokeStyle(value: string | CanvasGradient | CanvasPattern) {
    this.currentStrokeStyle = String(value);
    this.strokeStyles.push(this.currentStrokeStyle);
  }

  get strokeStyle(): string {
    return this.currentStrokeStyle;
  }

  set fillStyle(value: string | CanvasGradient | CanvasPattern) {
    this.currentFillStyle = String(value);
    this.fillStyles.push(this.currentFillStyle);
  }

  get fillStyle(): string {
    return this.currentFillStyle;
  }

  save(): void {
    this.saveCount += 1;
  }

  restore(): void {
    this.restoreCount += 1;
  }

  clearRect(_x: number, _y: number, _width: number, _height: number): void {}
  beginPath(): void {}
  closePath(): void {}
  moveTo(_x: number, _y: number): void {}
  lineTo(_x: number, _y: number): void {}
  quadraticCurveTo(_cpx: number, _cpy: number, _x: number, _y: number): void {}
  arc(_x: number, _y: number, _radius: number, _startAngle: number, _endAngle: number): void {}
  rect(_x: number, _y: number, _width: number, _height: number): void {}

  fill(): void {
    this.fillCount += 1;
  }

  stroke(): void {
    this.strokeCount += 1;
  }

  fillRect(x: number, y: number, width: number, height: number): void {
    this.fillRects.push({ x, y, width, height });
  }

  strokeRect(x: number, y: number, width: number, height: number): void {
    this.strokeRects.push({ x, y, width, height });
  }

  fillText(text: string, _x: number, _y: number): void {
    this.text.push(text);
  }

  measureText(text: string): TextMetrics {
    return { width: text.length * 7 } as TextMetrics;
  }

  setLineDash(segments: number[]): void {
    this.lineDashes.push([...segments]);
  }

  createLinearGradient(
    _x0: number,
    _y0: number,
    _x1: number,
    _y1: number,
  ): CanvasGradient {
    return {
      addColorStop: (_offset: number, _color: string) => {},
      toString: () => '[gradient]',
    } as unknown as CanvasGradient;
  }

  createPattern(_image: CanvasImageSource, _repetition: string | null): CanvasPattern {
    this.createPatternCount += 1;
    return { toString: () => '[pattern]' } as unknown as CanvasPattern;
  }
}

function createSurface(
  ctx: RecordingCanvasContext,
  options: { readonly materialIconReady?: boolean } = {},
): CanvasRenderSurface {
  return {
    ctx: ctx as unknown as CanvasRenderingContext2D,
    fontReady: signal(true),
    materialIconReady: options.materialIconReady ?? false,
    materialIconFontFamily: "'Material Icons'",
    materialIconFontUrl: 'https://fonts.gstatic.com/material-icons.woff2',
    setFont(weight, size) {
      ctx.font = `${weight} ${size}px Inter`;
    },
    roundedRect(x, y, width, height) {
      ctx.beginPath();
      ctx.rect(x, y, width, height);
      ctx.closePath();
    },
    fillRect(x, y, width, height) {
      ctx.beginPath();
      ctx.rect(x, y, width, height);
      ctx.fill();
    },
    drawWrappedText(text, x, y, maxWidth) {
      for (const line of this.wrapText(text, maxWidth)) {
        ctx.fillText(line, x, y);
        y += 18;
      }
    },
    drawEllipsizedText(text, x, y, maxWidth) {
      const displayText = this.ellipsizeText(text, maxWidth);
      if (displayText) {
        ctx.fillText(displayText, x, y);
      }
      return displayText;
    },
    ellipsizeText(text, maxWidth) {
      if (maxWidth <= 0) {
        return '';
      }
      const maxCharacters = Math.floor(maxWidth / 7);
      return text.length <= maxCharacters ? text : `${text.slice(0, Math.max(0, maxCharacters - 3))}...`;
    },
    wrapText(text, maxWidth) {
      if (text.length * 7 <= maxWidth) {
        return [text];
      }
      const words = text.split(/\s+/);
      const lines: string[] = [];
      let line = '';
      for (const word of words) {
        const candidate = line ? `${line} ${word}` : word;
        if (candidate.length * 7 > maxWidth && line) {
          lines.push(line);
          line = word;
        } else {
          line = candidate;
        }
      }
      if (line) {
        lines.push(line);
      }
      return lines;
    },
  };
}

function createNode(): NodeModel {
  return new NodeModel(
    1,
    1,
    1,
    0,
    10,
    '1.0.0',
    'Lookup Weather',
    40,
    40,
    260,
    220,
    'Input',
    ['Output'],
    false,
    null,
    null,
    { town: 'String' },
    { Output: { forecast: 'String' } },
  );
}

function createPathfinderNode(id: number, x: number, y: number, width: number, height: number): NodeModel {
  return new NodeModel(id, id, 0, 0, id * 10, '0.0.1', 'Obstacle', x, y, width, height);
}

describe('CanvasBackdropRenderer', () => {
  it('draws the stitch backdrop and caches the pattern surface', () => {
    const ctx = new RecordingCanvasContext();
    const tileCtx = new RecordingCanvasContext();
    const previousDocument = (globalThis as { document?: unknown }).document;
    (globalThis as { document?: unknown }).document = {
      createElement: () => ({
        width: 0,
        height: 0,
        getContext: () => tileCtx as unknown as CanvasRenderingContext2D,
      }),
    };

    try {
      const renderer = new CanvasBackdropRenderer(createSurface(ctx));
      const request = {
        canvasWidth: 800,
        canvasHeight: 600,
        effect: 'stitch' as const,
        hoveredCanvasPoint: null,
        viewportOffset: { x: 0, y: 0 },
        zoomLevel: 1,
      };

      renderer.drawBackdrop(request);
      renderer.drawBackdrop(request);

      expect(ctx.createPatternCount).toBe(1);
      expect(ctx.fillRects).toHaveLength(2);
      expect(tileCtx.fillStyles).toContain(CanvasTheme.backdrop.baseDotColor);
    } finally {
      if (previousDocument === undefined) {
        delete (globalThis as { document?: unknown }).document;
      } else {
        (globalThis as { document?: unknown }).document = previousDocument;
      }
    }
  });
});

describe('CanvasOverlayRenderer', () => {
  it('renders marquee and tooltips without leaking canvas state', () => {
    const ctx = new RecordingCanvasContext();
    const renderer = new CanvasOverlayRenderer(createSurface(ctx));

    renderer.drawMarqueeRect(10, 20, 110, 90);
    renderer.drawInteractionTooltip('Open node', { x: 80, y: 80 }, { zoomLevel: 1 });
    renderer.drawFailureTooltip({ x: 20, y: 120, width: 240 } as RenderedNode, 'Timed out while waiting');

    expect(ctx.saveCount).toBe(ctx.restoreCount);
    expect(ctx.strokeRects).toEqual([{ x: 10, y: 20, width: 100, height: 70 }]);
    expect(ctx.text).toContain('Open node');
    expect(ctx.text).toContain('Failure');
  });
});

describe('CanvasConnectionPreviewRenderer', () => {
  it('uses normal preview styling for clear routes', () => {
    const ctx = new RecordingCanvasContext();
    const renderer = new CanvasConnectionPreviewRenderer(createSurface(ctx));

    renderer.drawOrthogonalLine({
      sx: 0,
      sy: 0,
      ex: 80,
      ey: 0,
      pathfinderOptions: { gridSize: 10, nodeMargin: 20 },
    });

    expect(ctx.strokeStyles).toContain(CanvasTheme.connection.selected);
    expect(ctx.lineWidths).toContain(6.2);
  });

  it('uses validation styling for obstacle routes', () => {
    const ctx = new RecordingCanvasContext();
    const renderer = new CanvasConnectionPreviewRenderer(createSurface(ctx));
    const wall = createPathfinderNode(2, 40, -1000, 40, 2000);

    renderer.drawOrthogonalLine({
      sx: 0,
      sy: 0,
      ex: 140,
      ey: 0,
      obstacles: [wall],
      pathfinderOptions: { gridSize: 10, nodeMargin: 20 },
    });

    expect(ctx.strokeStyles).toContain(CanvasTheme.validation.error.stroke);
    expect(ctx.lineWidths).toContain(5.4);
    expect(ctx.lineDashes).toContainEqual([8, 6]);
  });
});

describe('CanvasNodeRenderer', () => {
  it('draws plugin node icons with the Material Icons ligature when the font is ready', () => {
    const node = createNode();
    const snapshot = buildCanvasRenderSnapshot(
      [node],
      [],
      new Map<number, ReadonlySet<string>>(),
      'simplified',
      { gridSize: 10, nodeMargin: 20 },
      false,
    );
    const ctx = new RecordingCanvasContext();
    const renderer = new CanvasNodeRenderer(createSurface(ctx, { materialIconReady: true }));

    renderer.drawNodes({
      nodes: snapshot.nodes,
      portConnectivity: snapshot.portConnectivity,
      drag: null,
      hoveredNodeId: null,
      hoveredPortKey: null,
      selectedNodeIds: new Set<number>(),
      options: { detailTier: 'simplified' },
    });

    expect(ctx.text).toContain('Lookup Weather');
    expect(ctx.text).toContain('extension');
    expect(ctx.fillCount).toBeGreaterThan(0);
  });

  it('keeps passive summary sockets quiet while preserving property socket emphasis', () => {
    const node = createNode();
    const expandedSections = new Map<number, ReadonlySet<string>>([
      [1, new Set(['left:Input', 'right:Output'])],
    ]);
    const snapshot = buildCanvasRenderSnapshot(
      [node],
      [],
      expandedSections,
      'simplified',
      { gridSize: 10, nodeMargin: 20 },
      false,
    );
    const renderedNode = snapshot.nodes[0]!;
    const outputSection = renderedNode.layout.outputSections[0]!;
    const summaryPortKey = outputSection.summaryPort.key;
    const propertyPortKey = outputSection.propertyRows[0]!.port.key;

    const summaryCtx = new RecordingCanvasContext();
    const summaryRenderer = new CanvasNodeRenderer(createSurface(summaryCtx));
    summaryRenderer.drawNodes({
      nodes: snapshot.nodes,
      portConnectivity: snapshot.portConnectivity,
      drag: null,
      hoveredNodeId: renderedNode.id,
      hoveredPortKey: summaryPortKey,
      selectedNodeIds: new Set<number>(),
      options: { detailTier: 'simplified', passiveSummaryPorts: true },
    });

    const propertyCtx = new RecordingCanvasContext();
    const propertyRenderer = new CanvasNodeRenderer(createSurface(propertyCtx));
    propertyRenderer.drawNodes({
      nodes: snapshot.nodes,
      portConnectivity: snapshot.portConnectivity,
      drag: null,
      hoveredNodeId: renderedNode.id,
      hoveredPortKey: propertyPortKey,
      selectedNodeIds: new Set<number>(),
      options: { detailTier: 'simplified', passiveSummaryPorts: true },
    });

    expect(summaryCtx.strokeStyles).not.toContain(CanvasTheme.anchor.connected);
    expect(propertyCtx.strokeStyles).toContain(CanvasTheme.anchor.connected);
  });
});
