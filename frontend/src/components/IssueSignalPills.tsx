import type { AttentionSignal, ShipWeekIssueSummary } from '../types';
import { createIssueSignals } from '../utils/models';
import SignalPills from './SignalPills';

export type IssueSignalPillsProps = {
  issue: ShipWeekIssueSummary;
  className?: string;
  leadingSignals?: AttentionSignal[];
  trailingSignals?: AttentionSignal[];
  computedSignalLimit?: number;
  excludeComputedLabels?: string[];
  showActionSignal?: boolean;
};

function IssueSignalPills({
  issue,
  className = 'attention-issue-signals',
  leadingSignals = [],
  trailingSignals = [],
  computedSignalLimit,
  excludeComputedLabels = [],
  showActionSignal = true,
}: IssueSignalPillsProps) {
  const excludedComputedLabels = new Set(excludeComputedLabels);
  const [actionSignal, ...stateSignals] = createIssueSignals(issue);
  let computedSignals = showActionSignal && actionSignal
    ? [actionSignal, ...stateSignals]
    : stateSignals;

  computedSignals = computedSignals.filter((signal) => !excludedComputedLabels.has(signal.label));

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

export default IssueSignalPills;
