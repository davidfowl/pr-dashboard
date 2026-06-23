import { describe, expect, it } from 'vitest';
import type { AttentionSignal } from '../types';
import { dedupeSignals } from './signals';

describe('dedupeSignals', () => {
  it('collapses the two unresolved-thread pill phrasings into one', () => {
    const signals: AttentionSignal[] = [
      { label: '2 unresolved threads', tone: 'danger' },
      { label: '2 unresolved', tone: 'danger' },
    ];

    const deduped = dedupeSignals(signals);

    expect(deduped).toHaveLength(1);
    // First occurrence wins, keeping the more descriptive lane-leading label.
    expect(deduped[0].label).toBe('2 unresolved threads');
  });

  it('folds unresolved-thread counts together with the "unresolved feedback" label', () => {
    const signals: AttentionSignal[] = [
      { label: 'Unresolved feedback', tone: 'danger' },
      { label: '3 unresolved', tone: 'danger' },
    ];

    expect(dedupeSignals(signals)).toHaveLength(1);
  });

  it('keeps distinct concepts separate', () => {
    const signals: AttentionSignal[] = [
      { label: '2 unresolved threads', tone: 'danger' },
      { label: 'Merge conflicts', tone: 'danger' },
    ];

    expect(dedupeSignals(signals)).toHaveLength(2);
  });
});
