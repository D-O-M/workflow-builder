import { CanvasBounds } from './canvas-render-types';

export class SpatialIndex<T> {
  private readonly buckets = new Map<string, T[]>();

  constructor(private readonly bucketSize: number) {}

  insert(bounds: CanvasBounds, item: T): void {
    const [startX, endX] = this.bucketRange(bounds.left, bounds.right);
    const [startY, endY] = this.bucketRange(bounds.top, bounds.bottom);

    for (let bucketX = startX; bucketX <= endX; bucketX += 1) {
      for (let bucketY = startY; bucketY <= endY; bucketY += 1) {
        const key = `${bucketX}:${bucketY}`;
        const bucket = this.buckets.get(key);
        if (bucket) {
          bucket.push(item);
        } else {
          this.buckets.set(key, [item]);
        }
      }
    }
  }

  queryPoint(x: number, y: number): T[] {
    const bucket = this.buckets.get(`${Math.floor(x / this.bucketSize)}:${Math.floor(y / this.bucketSize)}`);
    if (!bucket || bucket.length === 0) {
      return [];
    }

    return [...new Set(bucket)];
  }

  queryBounds(bounds: CanvasBounds): T[] {
    const [startX, endX] = this.bucketRange(bounds.left, bounds.right);
    const [startY, endY] = this.bucketRange(bounds.top, bounds.bottom);
    const matches = new Set<T>();

    for (let bucketX = startX; bucketX <= endX; bucketX += 1) {
      for (let bucketY = startY; bucketY <= endY; bucketY += 1) {
        const bucket = this.buckets.get(`${bucketX}:${bucketY}`);
        if (!bucket) {
          continue;
        }

        for (const item of bucket) {
          matches.add(item);
        }
      }
    }

    return [...matches];
  }

  private bucketRange(min: number, max: number): [number, number] {
    return [Math.floor(min / this.bucketSize), Math.floor(max / this.bucketSize)];
  }
}
