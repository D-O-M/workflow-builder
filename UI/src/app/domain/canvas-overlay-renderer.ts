import { RenderedNode } from './canvas-render-model';
import { CanvasRenderSurface } from './canvas-draw-surface';
import { CanvasTheme } from './canvas-theme';
import { Point } from './node-model';

export interface CanvasOverlayRenderRequest {
  readonly zoomLevel: number;
}

export class CanvasOverlayRenderer {
  constructor(private readonly surface: CanvasRenderSurface) {}

  drawFailureTooltip(node: RenderedNode, message: string): void {
    this.surface.ctx.save();
    const width = 280;
    const paddingX = 14;
    const paddingY = 12;
    const title = 'Failure';
    const lines = this.surface.wrapText(message, width - paddingX * 2);
    const bodyHeight = Math.max(lines.length, 1) * 16;
    const height = paddingY * 2 + 18 + bodyHeight + 8;
    const x = node.x + node.width / 2 - width / 2;
    const y = node.y - height - 16;

    this.surface.ctx.fillStyle = CanvasTheme.tooltipBg;
    this.surface.roundedRect(x, y, width, height, 12);
    this.surface.ctx.fill();

    this.surface.ctx.fillStyle = CanvasTheme.tooltipDanger;
    this.surface.setFont(700, 11);
    this.surface.ctx.fillText(title, x + paddingX, y + 18);

    this.surface.ctx.fillStyle = CanvasTheme.tooltipText;
    this.surface.setFont(500, 12);
    lines.forEach((line, index) => {
      this.surface.ctx.fillText(line, x + paddingX, y + paddingY + 26 + index * 16);
    });

    this.surface.ctx.restore();
  }

  drawInteractionTooltip(label: string, point: Point, request: CanvasOverlayRenderRequest): void {
    const normalizedLabel = label.trim();
    if (!normalizedLabel) {
      return;
    }

    this.surface.ctx.save();
    const scale = Math.max(request.zoomLevel, 0.2);
    this.surface.setFont(600, 11 / scale);
    const paddingX = 9 / scale;
    const height = 24 / scale;
    const maxWidth = 220 / scale;
    const displayLabel = this.surface.ellipsizeText(normalizedLabel, maxWidth - paddingX * 2);
    const textWidth = this.surface.ctx.measureText(displayLabel).width;
    const width = Math.min(maxWidth, Math.max(48 / scale, textWidth + paddingX * 2));
    const x = point.x - width / 2;
    const y = point.y - height - 8 / scale;

    this.surface.ctx.fillStyle = CanvasTheme.tooltipBg;
    this.surface.roundedRect(x, y, width, height, 7 / scale);
    this.surface.ctx.fill();

    this.surface.ctx.fillStyle = CanvasTheme.tooltipText;
    this.surface.ctx.textAlign = 'center';
    this.surface.ctx.textBaseline = 'middle';
    this.surface.ctx.fillText(displayLabel, x + width / 2, y + height / 2 + 0.5 / scale);
    this.surface.ctx.restore();
  }

  drawMarqueeRect(left: number, top: number, right: number, bottom: number): void {
    const width = right - left;
    const height = bottom - top;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = 'rgba(37, 99, 235, 0.12)';
    this.surface.ctx.strokeStyle = 'rgba(37, 99, 235, 0.82)';
    this.surface.ctx.lineWidth = 1.5;
    this.surface.ctx.setLineDash([8, 6]);
    this.surface.ctx.fillRect(left, top, width, height);
    this.surface.ctx.strokeRect(left, top, width, height);
    this.surface.ctx.restore();
  }
}
