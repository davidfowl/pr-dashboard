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

  it('keeps the unresolved-feedback action pill alongside the count pill', () => {
    // On the attention board the count reason is passed first; the action verb must survive
    // so the card still shows what to do (and its tone), not just the count.
    const signals: AttentionSignal[] = [
      { label: '2 unresolved threads', tone: 'danger' },
      { label: 'resolve feedback', tone: 'danger' },
      { label: '2 unresolved', tone: 'danger' },
    ];

    const deduped = dedupeSignals(signals);

    expect(deduped.map((signal) => signal.label)).toEqual(['2 unresolved threads', 'resolve feedback']);
  });

  it('keeps distinct concepts separate', () => {
    const signals: AttentionSignal[] = [
      { label: '2 unresolved threads', tone: 'danger' },
      { label: 'Merge conflicts', tone: 'danger' },
    ];

    expect(dedupeSignals(signals)).toHaveLength(2);
  });
});
