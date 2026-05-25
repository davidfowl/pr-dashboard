import type { AttentionSignal, PullRequestSummary } from '../types';
import { createAttentionSignals } from '../utils/models';

export type PullRequestSignalPillsProps = {
  pullRequest: PullRequestSummary;
  className?: string;
  leadingSignals?: AttentionSignal[];
  trailingSignals?: AttentionSignal[];
  computedSignalLimit?: number;
  excludeComputedLabels?: string[];
  showActionSignal?: boolean;
};

function PullRequestSignalPills({
  pullRequest,
  className = 'attention-pr-signals',
  leadingSignals = [],
  trailingSignals = [],
  computedSignalLimit,
  excludeComputedLabels = [],
  showActionSignal = true,
}: PullRequestSignalPillsProps) {
  const excludedComputedLabels = new Set(excludeComputedLabels);
  const [actionSignal, ...stateSignals] = createAttentionSignals({ pullRequest, reason: '' });
  let computedSignals = showActionSignal && actionSignal
    ? [actionSignal, ...stateSignals]
    : stateSignals;

  computedSignals = computedSignals.filter((signal) => !excludedComputedLabels.has(signal.label));

  if (computedSignalLimit !== undefined) {
    computedSignals = computedSignals.slice(0, computedSignalLimit);
  }

  return (
    <span className={className}>
      {dedupeSignals([...leadingSignals, ...computedSignals, ...trailingSignals]).map((signal) => (
        <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
          {signal.label}
        </span>
      ))}
    </span>
  );
}

function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (seen.has(signal.label)) {
      return false;
    }

    seen.add(signal.label);
    return true;
  });
}

export default PullRequestSignalPills;
