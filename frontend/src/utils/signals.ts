import type { AttentionSignal } from '../types';

export function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (seen.has(signal.label)) {
      return false;
    }

    seen.add(signal.label);
    return true;
  });
}
