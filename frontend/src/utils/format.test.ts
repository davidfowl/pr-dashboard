import { describe, expect, it } from 'vitest';

import { formatCount } from './format';

describe('formatCount', () => {
  it('keeps shown invariant for zero, singular, and plural counts', () => {
    expect(formatCount(0, 'shown')).toBe('0 shown');
    expect(formatCount(1, 'shown')).toBe('1 shown');
    expect(formatCount(2, 'shown')).toBe('2 shown');
  });

  it('preserves default pluralization for regular count labels', () => {
    expect(formatCount(0, 'PR')).toBe('0 PRs');
    expect(formatCount(1, 'PR')).toBe('1 PR');
    expect(formatCount(2, 'PR')).toBe('2 PRs');
  });
});
