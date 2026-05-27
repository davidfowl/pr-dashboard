import { useMemo } from 'react';
import { dayMs } from '../constants';
import type { PullRequestSummary } from '../types';
import PullRequestListItem from './PullRequestListItem';
import type { PullRequestSignalPillsProps } from './PullRequestSignalPills';

export type PullRequestListEntry = {
  pullRequest: PullRequestSummary;
  bucketLabel: string;
  signalProps?: Omit<PullRequestSignalPillsProps, 'pullRequest'>;
};

type PullRequestListProps = {
  entries: PullRequestListEntry[];
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest?: (repository: string, pullRequest: PullRequestSummary) => void;
  emptyState?: string;
  limit?: number;
};

const recentlyUpdatedWindowMs = 2 * dayMs;
const approvedButAgingBucketLabel = 'Approved but aging';
const bucketRanks = new Map([
  ['CI failing', -1],
  [approvedButAgingBucketLabel, 0],
  ['Re-review needed', 1],
  ['Ready to merge', 2],
  ['Author response', 3],
  ['Needs review', 4],
  ['Quick wins', 5],
  ['Review started', 6],
]);

function PullRequestList({
  entries,
  onSelectPullRequest,
  onVisiblePullRequest,
  emptyState,
  limit,
}: PullRequestListProps) {
  const visibleEntries = useMemo(() => {
    const sortedEntries = [...entries].sort(comparePullRequestListEntries);
    return limit === undefined ? sortedEntries : sortedEntries.slice(0, limit);
  }, [entries, limit]);

  return (
    <div className="attention-list">
      {visibleEntries.length === 0 && emptyState && (
        <p className="empty-for-me">{emptyState}</p>
      )}
      {visibleEntries.map((entry) => (
        <PullRequestListItem
          key={`${entry.bucketLabel}-${entry.pullRequest.repository}-${entry.pullRequest.number}`}
          pullRequest={entry.pullRequest}
          onSelectPullRequest={onSelectPullRequest}
          onVisiblePullRequest={onVisiblePullRequest}
          signalProps={entry.signalProps}
        />
      ))}
    </div>
  );
}

function comparePullRequestListEntries(first: PullRequestListEntry, second: PullRequestListEntry) {
  return compareSameBucketWait(first, second)
    || Number(isRecentlyUpdated(second.pullRequest)) - Number(isRecentlyUpdated(first.pullRequest))
    || bucketRank(first.bucketLabel) - bucketRank(second.bucketLabel)
    || createdTime(first.pullRequest) - createdTime(second.pullRequest)
    || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
    || first.pullRequest.number - second.pullRequest.number;
}

function compareSameBucketWait(first: PullRequestListEntry, second: PullRequestListEntry) {
  if (first.bucketLabel !== second.bucketLabel) {
    return 0;
  }

  return bucketWaitTime(first.pullRequest, first.bucketLabel) - bucketWaitTime(second.pullRequest, second.bucketLabel)
    || quickWinScore(first.pullRequest, first.bucketLabel) - quickWinScore(second.pullRequest, second.bucketLabel);
}

function bucketRank(label: string) {
  return bucketRanks.get(label) ?? Number.MAX_SAFE_INTEGER;
}

function bucketWaitTime(pullRequest: PullRequestSummary, bucketLabel: string) {
  switch (bucketLabel) {
    case approvedButAgingBucketLabel:
    case 'Ready to merge':
      return dateTime(pullRequest.review.lastApprovedAt ?? pullRequest.review.lastReviewedAt);
    case 'Re-review needed':
      return dateTime(pullRequest.lastCommitAt);
    case 'Author response':
    case 'Review started':
      return dateTime(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt);
    case 'Stalled':
    case 'Needs review':
    case 'Quick wins':
    case 'Docs':
    case 'Community Toolkit':
    case 'Bots / automation':
    case 'Community':
    case 'Draft':
      return updatedTime(pullRequest);
    default:
      return updatedTime(pullRequest);
  }
}

function quickWinScore(pullRequest: PullRequestSummary, bucketLabel: string) {
  if (bucketLabel !== 'Quick wins') {
    return 0;
  }

  return pullRequest.additions + pullRequest.deletions + (pullRequest.changedFiles * 10) + (pullRequest.commitCount * 5);
}

function isRecentlyUpdated(pullRequest: PullRequestSummary) {
  return Date.now() - updatedTime(pullRequest) <= recentlyUpdatedWindowMs;
}

function createdTime(pullRequest: PullRequestSummary) {
  return dateTime(pullRequest.createdAt);
}

function updatedTime(pullRequest: PullRequestSummary) {
  return dateTime(pullRequest.updatedAt);
}

function dateTime(value?: string | null) {
  return value == null ? Number.MAX_SAFE_INTEGER : new Date(value).getTime();
}

export default PullRequestList;
