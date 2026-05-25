import type { PullRequestSummary } from '../types';
import { formatRelative } from '../utils/format';
import PullRequestSignalPills from './PullRequestSignalPills';
import type { PullRequestSignalPillsProps } from './PullRequestSignalPills';

type PullRequestListItemProps = {
  pullRequest: PullRequestSummary;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  signalProps?: Omit<PullRequestSignalPillsProps, 'pullRequest'>;
};

function PullRequestListItem({
  pullRequest,
  onSelectPullRequest,
  signalProps,
}: PullRequestListItemProps) {
  return (
    <button
      type="button"
      onClick={() => onSelectPullRequest(pullRequest.repository, pullRequest)}
    >
      <span className="attention-pr-number">#{pullRequest.number}</span>
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
