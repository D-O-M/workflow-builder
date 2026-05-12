import { CanvasValidationSeverity } from './canvas-render-model';

export interface CanvasPropertyIssueSummary {
  readonly severity: CanvasValidationSeverity;
  readonly count: number;
}

export interface CanvasNodeIssueSummary {
  readonly severity: CanvasValidationSeverity;
  readonly errorCount: number;
  readonly warningCount: number;
  readonly tooltip: string;
  readonly propertyIssues: ReadonlyMap<string, CanvasPropertyIssueSummary>;
}
