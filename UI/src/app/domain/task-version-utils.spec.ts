import { describe, expect, it } from 'vitest';
import {
  getLatestPatchPerMinorVersion,
  getLatestTaskVersion,
  getTaskVersionLane,
  parseTaskVersion,
} from './task-version-utils';
import { ITaskVersion } from '../interfaces/ITaskDefinition';

function version(id: number, value: `${number}.${number}.${number}` | string): ITaskVersion {
  return {
    id,
    version: value as `${number}.${number}.${number}`,
    state: 'Active',
    description: null,
    inputChannel: null,
    outputChannels: [],
  };
}

describe('task version selection helpers', () => {
  it('selects the latest overall semantic version', () => {
    const latest = getLatestTaskVersion([
      version(1, '1.9.9'),
      version(2, '2.0.0'),
      version(3, '1.10.8'),
    ]);

    expect(latest?.version).toBe('2.0.0');
  });

  it('keeps only the latest patch for each major minor lane', () => {
    const options = getLatestPatchPerMinorVersion([
      version(1, '1.0.0'),
      version(2, '1.0.2'),
      version(3, '1.1.0'),
      version(4, '2.0.1'),
      version(5, '2.0.0'),
    ]);

    expect(options.map((option) => option.version.version)).toEqual([
      '2.0.1',
      '1.1.0',
      '1.0.2',
    ]);
  });

  it('falls malformed versions into the zero lane', () => {
    expect(parseTaskVersion('not-a-version')).toEqual({
      major: 0,
      minor: 0,
      patch: 0,
      valid: false,
    });
    expect(getTaskVersionLane('not-a-version')).toEqual([0, 0]);
  });
});
