import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { dayMs } from '../../constants';
import type { AttentionBucket, AttentionItem, DeveloperPullRequestCount, PickItem, PullRequestSummary } from '../../types';
import { colorForText, formatCount, formatRelative, initials } from '../../utils/format';
import PullRequestListItem from '../PullRequestListItem';
import AttentionBoard from './AttentionBoard';

type QueueOverviewProps = {
  counts: DeveloperPullRequestCount[];
  attentionBuckets: AttentionBucket[];
  forMeItems: PickItem[];
  login?: string;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

const pullRequestListLimit = 10;
const recentFocusWindowMs = 14 * dayMs;
const recentlyUpdatedFocusWindowMs = 2 * dayMs;
const blockedFocusBucketLabels = new Set(['Author response', 'Stalled', 'Draft', 'Community Toolkit', 'Bots / automation', 'Community']);

function QueueOverview({
  counts,
  attentionBuckets,
  forMeItems,
  login,
  onSelectPullRequest,
}: QueueOverviewProps) {
  const [showAllCoreMembers, setShowAllCoreMembers] = useState(false);

  const focusItems = useMemo<FocusItem[]>(() =>
    attentionBuckets
      .flatMap((bucket) =>
        bucket.items.map((item) => ({
          ...item,
          bucketLabel: bucket.label,
          bucketTone: bucket.tone,
        })))
      .filter((item) => !blockedFocusBucketLabels.has(item.bucketLabel))
      .filter((item) => isRecentFocusItem(item.pullRequest))
      .sort((first, second) =>
        Number(isRecentlyUpdatedFocusItem(second.pullRequest)) - Number(isRecentlyUpdatedFocusItem(first.pullRequest))
        || new Date(first.pullRequest.createdAt).getTime() - new Date(second.pullRequest.createdAt).getTime()
        || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
        || first.pullRequest.number - second.pullRequest.number)
      .slice(0, pullRequestListLimit),
  [attentionBuckets]);

  const coreOpenCount = counts.reduce((total, count) => total + count.openPullRequestCount, 0);
  const activeCoreCounts = counts.filter((count) => count.openPullRequestCount > 0);
  const visibleCoreCounts = showAllCoreMembers ? counts : activeCoreCounts;
  const reviewBuckets = useMemo(
    () => forMeItems.length === 0
      ? attentionBuckets
      : [
        ...attentionBuckets,
        {
          label: 'For me',
          summary: login
            ? `Pull requests that need ${login}'s review or response.`
            : 'Pull requests that need your review or response.',
          tone: 'accent' as const,
          metric: 'personal queue',
          items: forMeItems.map((item) => ({
            pullRequest: item.pullRequest,
            reason: item.action,
          })),
        },
      ],
    [attentionBuckets, forMeItems, login],
  );
  function renderCoreOwnerDetails() {
    return (
      <section className="drilldown-panel" aria-label="Core team open pull requests">
        <div className="drilldown-header">
          <span>Core team</span>
          <button type="button" onClick={() => setShowAllCoreMembers((value) => !value)}>
            {showAllCoreMembers ? 'Show active only' : `Show all ${counts.length}`}
          </button>
        </div>
        {visibleCoreCounts.length === 0 ? (
          <p className="empty-for-me">No loaded open PRs from core team members.</p>
        ) : (
          <div className="core-member-list">
            {visibleCoreCounts.map((count) => (
              <article
                key={count.actor}
                className="core-member-row"
                style={{ '--developer-accent': colorForText(count.actor) } as CSSProperties}
              >
                <span className="avatar-dot">{initials(count.actor)}</span>
                <strong>{count.actor}</strong>
                <span>{formatCount(count.openPullRequestCount, 'open PR')}</span>
                <em>
                  {count.latestUpdatedAt
                    ? `updated ${formatRelative(count.latestUpdatedAt)}`
                    : 'No loaded open PRs'}
                </em>
              </article>
            ))}
          </div>
        )}
      </section>
    );
  }

  return (
    <section className="queue-overview" aria-label="Queue overview">
      <div className="section-title-row">
        <p className="eyebrow">Queue overview</p>
        <h3>One queue across repos: recent PRs first, bucket details one click away.</h3>
        <p className="board-guidance">
          Needs attention shows non-bot PRs opened in the last 14 days or updated in the last 48h,
          excluding blocked/stalled lanes; fresh updates sort first, then FIFO.
        </p>
      </div>

      <section className="focus-panel" aria-label="Focused attention queue">
        <div className="attention-card-header">
          <span>Needs attention</span>
          <strong>{formatCount(focusItems.length, 'shown')}</strong>
        </div>
        <p>Actionable PRs across the loaded repositories.</p>

        <div className="attention-list">
          {focusItems.length === 0 && (
            <p className="empty-for-me">No recent non-automation PRs need attention in the current results.</p>
          )}
          {focusItems.map((item) => (
            <PullRequestListItem
              key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
              pullRequest={item.pullRequest}
              onSelectPullRequest={onSelectPullRequest}
              signalProps={{
                leadingSignals: [{ label: item.bucketLabel, tone: item.bucketTone }],
                computedSignalLimit: 4,
              }}
            />
          ))}
        </div>
      </section>

      <section className="owner-drilldown" aria-label="Core team ownership breakdown">
        <div className="attention-card-header">
          <span>Core team ownership</span>
          <strong>{formatCount(coreOpenCount, 'open PR')}</strong>
        </div>
        <p className="board-guidance">{formatCount(activeCoreCounts.length, 'active author')} in the loaded queue.</p>
        {renderCoreOwnerDetails()}
      </section>

      {reviewBuckets.length > 0 && (
        <AttentionBoard buckets={reviewBuckets} onSelectPullRequest={onSelectPullRequest} />
      )}
    </section>
  );
}

function isRecentFocusItem(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.createdAt).getTime() <= recentFocusWindowMs
    || isRecentlyUpdatedFocusItem(pullRequest);
}

function isRecentlyUpdatedFocusItem(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.updatedAt).getTime() <= recentlyUpdatedFocusWindowMs;
}

export default QueueOverview;
