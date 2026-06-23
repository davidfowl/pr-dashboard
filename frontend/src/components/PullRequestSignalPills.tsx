import type { AttentionSignal, PullRequestSummary } from '../types';
import { createAttentionSignals } from '../utils/models';
import SignalPills from './SignalPills';

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
  const excludedComputedLabels = new Set(excludeComputedLabels.map((label) => label.toLowerCase()));
  const [actionSignal, ...stateSignals] = createAttentionSignals({ pullRequest, reason: '' });
  let computedSignals = showActionSignal && actionSignal
    ? [actionSignal, ...stateSignals]
    : stateSignals;

  computedSignals = computedSignals.filter((signal) => !excludedComputedLabels.has(signal.label.toLowerCase()));

  if (computedSignalLimit !== undefined) {
    computedSignals = computedSignals.slice(0, computedSignalLimit);
  }

  return (
    <SignalPills
      className={className}
      signals={[...leadingSignals, ...computedSignals, ...trailingSignals]}
    />
  );
}

export default PullRequestSignalPills;
