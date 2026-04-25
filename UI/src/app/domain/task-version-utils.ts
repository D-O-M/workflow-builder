import { ITaskVersion } from '../interfaces/ITaskDefinition';

export interface ParsedTaskVersion {
  readonly major: number;
  readonly minor: number;
  readonly patch: number;
  readonly valid: boolean;
}

export interface TaskVersionLaneOption {
  readonly laneKey: string;
  readonly major: number;
  readonly minor: number;
  readonly version: ITaskVersion;
}

export function parseTaskVersion(version: string): ParsedTaskVersion {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version.trim());
  if (!match) {
    return {
      major: 0,
      minor: 0,
      patch: 0,
      valid: false,
    };
  }

  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    valid: true,
  };
}

export function getTaskVersionLane(version: string): [number, number] {
  const parsed = parseTaskVersion(version);
  return [parsed.major, parsed.minor];
}

export function compareParsedTaskVersions(left: ParsedTaskVersion, right: ParsedTaskVersion): number {
  return (
    left.major - right.major ||
    left.minor - right.minor ||
    left.patch - right.patch
  );
}

export function compareTaskVersions(left: ITaskVersion, right: ITaskVersion): number {
  const semanticComparison = compareParsedTaskVersions(
    parseTaskVersion(left.version),
    parseTaskVersion(right.version),
  );

  return semanticComparison || left.id - right.id;
}

export function getLatestTaskVersion(versions: readonly ITaskVersion[]): ITaskVersion | null {
  return [...versions].sort((left, right) => compareTaskVersions(right, left))[0] ?? null;
}

export function getLatestPatchPerMinorVersion(versions: readonly ITaskVersion[]): TaskVersionLaneOption[] {
  const lanes = new Map<string, TaskVersionLaneOption>();

  for (const version of versions) {
    const parsed = parseTaskVersion(version.version);
    const laneKey = `${parsed.major}.${parsed.minor}`;
    const current = lanes.get(laneKey);
    if (!current || compareTaskVersions(version, current.version) > 0) {
      lanes.set(laneKey, {
        laneKey,
        major: parsed.major,
        minor: parsed.minor,
        version,
      });
    }
  }

  return [...lanes.values()].sort((left, right) =>
    right.major - left.major ||
    right.minor - left.minor ||
    compareTaskVersions(right.version, left.version));
}
