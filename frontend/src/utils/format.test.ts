import { describe, expect, it } from 'vitest';

import { formatCount } from './format';

describe('formatCount', () => {
  it('keeps invariant count labels unchanged for plural counts', () => {
    expect(formatCount(10, 'shown')).toBe('10 shown');
  });
});
