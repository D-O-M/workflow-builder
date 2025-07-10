import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  effect,
  ElementRef,
  inject,
  Injector,
  runInInjectionContext,
  signal,
  viewChild,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

interface Node {
  id: number;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

interface Connection {
  from: number;
  to: number;
}

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ReactiveFormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements AfterViewInit {
  protected readonly title = signal('workflow-builder');
  private readonly injector = inject(Injector);

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('myCanvas');

  private get canvas(): HTMLCanvasElement {
    return this.canvasRef().nativeElement;
  }

  private ctx?: CanvasRenderingContext2D;

  protected readonly nodes = signal<Node[]>([]);
  protected readonly connections = signal<Connection[]>([]);

  protected readonly hoveredNodeId = signal<number | null>(null);

  protected readonly dragConnection = signal<
    | { from: number; x: number; y: number }
    | null
  >(null);

  protected readonly showModal = signal(false);
  private readonly newNodePos = signal<{ x: number; y: number } | null>(null);

  protected readonly nodeName = new FormControl('', { nonNullable: true });

  ngAfterViewInit(): void {
    const c = this.canvasRef();
    if (!c) return;
    this.ctx = c.nativeElement.getContext('2d')!;
    c.nativeElement.width = c.nativeElement.clientWidth;
    c.nativeElement.height = c.nativeElement.clientHeight;

    runInInjectionContext(this.injector, () => {
      effect(() => {
        this.draw();
      });
    });
  }

  protected openModal(event: MouseEvent): void {
    const rect = this.canvas!.getBoundingClientRect();
    this.newNodePos.set({
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
    });
    this.nodeName.setValue('');
    this.showModal.set(true);
  }

  protected createNode(): void {
    const name = this.nodeName.value.trim();
    const pos = this.newNodePos();
    if (!name || !pos) {
      this.showModal.set(false);
      return;
    }
    const nodes = [...this.nodes()];
    nodes.push({
      id: Date.now(),
      name,
      x: pos.x,
      y: pos.y,
      width: 300,
      height: 200,
    });
    this.nodes.set(nodes);
    this.showModal.set(false);
  }

  private getMousePos(event: MouseEvent): { x: number; y: number } {
    const rect = this.canvas!.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  protected onMouseMove(event: MouseEvent): void {
    const { x, y } = this.getMousePos(event);
    if (this.dragConnection()) {
      this.dragConnection.update((d) => (d ? { ...d, x, y } : d));
      return;
    }
    const node = this.nodes().find((n) => this.inNode(x, y, n));
    this.hoveredNodeId.set(node ? node.id : null);
  }

  protected onMouseDown(event: MouseEvent): void {
    const { x, y } = this.getMousePos(event);
    const hovered = this.hoveredNodeId();
    if (hovered !== null) {
      const node = this.nodes().find((n) => n.id === hovered)!;
      if (this.inCircle(x, y, node)) {
        this.dragConnection.set({ from: hovered, x, y });
      }
    }
  }

  protected onMouseUp(event: MouseEvent): void {
    const drag = this.dragConnection();
    if (!drag) return;
    const { x, y } = this.getMousePos(event);
    const target = this.nodes().find(
      (n) => n.id !== drag.from && this.inCircle(x, y, n)
    );
    if (target) {
      const connections = [...this.connections()];
      connections.push({ from: drag.from, to: target.id });
      this.connections.set(connections);
    }
    this.dragConnection.set(null);
  }

  protected onMouseLeave(): void {
    this.dragConnection.set(null);
    this.hoveredNodeId.set(null);
  }

  private inNode(x: number, y: number, node: Node): boolean {
    return (
      x >= node.x &&
      x <= node.x + node.width &&
      y >= node.y &&
      y <= node.y + node.height
    );
  }

  private inCircle(x: number, y: number, node: Node): boolean {
    const cx = node.x + node.width;
    const cy = node.y + node.height;
    return Math.hypot(x - cx, y - cy) <= 10;
  }

  private drawWrappedText(
    ctx: CanvasRenderingContext2D,
    text: string,
    x: number,
    y: number,
    maxWidth: number
  ): void {
    const words = text.split(/\s+/);
    let line = '';
    const lineHeight = 16;
    for (const word of words) {
      const test = line ? `${line} ${word}` : word;
      if (ctx.measureText(test).width > maxWidth && line) {
        ctx.fillText(line, x, y);
        line = word;
        y += lineHeight;
      } else {
        line = test;
      }
    }
    ctx.fillText(line, x, y);
  }

  private draw(): void {
    console.log('drawing', this.nodes().length, this.connections().length);
    const ctx = this.ctx;
    const canvas = this.canvas;
    if (!ctx || !canvas) return;

    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.strokeStyle = 'blue';
    ctx.fillStyle = 'blue';
    ctx.lineWidth = 2;

    for (const conn of this.connections()) {
      const from = this.nodes().find((n) => n.id === conn.from)!;
      const to = this.nodes().find((n) => n.id === conn.to)!;
      ctx.beginPath();
      ctx.moveTo(from.x + from.width, from.y + from.height);
      ctx.lineTo(to.x + to.width, to.y + to.height);
      ctx.stroke();
    }

    const hovered = this.hoveredNodeId();
    const drag = this.dragConnection();

    for (const node of this.nodes()) {
      ctx.strokeRect(node.x, node.y, node.width, node.height);
      this.drawWrappedText(
        ctx,
        node.name,
        node.x + 10,
        node.y + 20,
        node.width - 20
      );

      let circleColor: string | null = null;
      if (drag) {
        circleColor = drag.from === node.id ? 'blue' : 'green';
      } else if (hovered === node.id) {
        circleColor = 'blue';
      }
      if (circleColor) {
        ctx.beginPath();
        ctx.fillStyle = circleColor;
        ctx.arc(node.x + node.width, node.y + node.height, 10, 0, 2 * Math.PI);
        ctx.fill();
        ctx.fillStyle = 'white';
      }
    }

    if (drag) {
      const from = this.nodes().find((n) => n.id === drag.from)!;
      ctx.beginPath();
      ctx.moveTo(from.x + from.width, from.y + from.height);
      ctx.lineTo(drag.x, drag.y);
      ctx.stroke();
    }
  }
}
