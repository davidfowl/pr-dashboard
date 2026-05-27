import type { CheckState, PullRequestSummary } from '../types';
import { formatRelative } from '../utils/format';
import PullRequestSignalPills from './PullRequestSignalPills';
import type { PullRequestSignalPillsProps } from './PullRequestSignalPills';

type PullRequestListItemProps = {
  pullRequest: PullRequestSummary;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  signalProps?: Omit<PullRequestSignalPillsProps, 'pullRequest'>;
};

const checkBadgeGlyphs: Record<Exclude<CheckState, 'none'>, { glyph: string; label: string }> = {
  success: { glyph: '✓', label: 'CI passed' },
  failure: { glyph: '✗', label: 'CI failing' },
  pending: { glyph: '●', label: 'CI running' },
};

function PullRequestListItem({
  pullRequest,
  onSelectPullRequest,
  signalProps,
}: PullRequestListItemProps) {
  const checksState = pullRequest.checks?.state;
  const badge = checksState && checksState !== 'none' ? checkBadgeGlyphs[checksState] : null;
  const badgeTitle = badge
    ? `${badge.label}${pullRequest.checks?.failingChecks?.length
      ? ` · ${pullRequest.checks.failingChecks.map((failing) => failing.name).join(', ')}`
      : ''}`
    : undefined;

  return (
    <button
      type="button"
      onClick={() => onSelectPullRequest(pullRequest.repository, pullRequest)}
    >
      <span className="attention-pr-number">
        {badge && (
          <span
            className={`attention-pr-check-badge ${checksState}`}
            title={badgeTitle}
            aria-label={badgeTitle ?? badge.label}
            role="img"
          >
            <span aria-hidden="true">{badge.glyph}</span>
          </span>
        )}
        #{pullRequest.number}
      </span>
      <span className="attention-pr-repo" title={pullRequest.repository}>
        {pullRequest.repository}
      </span>
      <strong>{pullRequest.title}</strong>
      <span className="attention-pr-meta">
        {pullRequest.author} · updated {formatRelative(pullRequest.updatedAt)}
      </span>
      <PullRequestSignalPills pullRequest={pullRequest} {...signalProps} />
    </button>
  );
}

export default PullRequestListItem;
