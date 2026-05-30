import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { dayMs } from '../../constants';
import type {
  AttentionBucket,
  AttentionIssueBucket,
  AttentionItem,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
} from '../../types';
import { colorForText, formatCount, formatRelative, initials } from '../../utils/format';
import LoadingBadge from '../LoadingBadge';
import PullRequestList from '../PullRequestList';
import AttentionBoard from './AttentionBoard';

type QueueOverviewProps = {
  counts: DeveloperPullRequestCount[];
  attentionBuckets: AttentionBucket[];
  regressionIssueBuckets: AttentionIssueBucket[];
  forMeItems: PickItem[];
  loading: boolean;
  selectedBucketId: string;
  login?: string;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

const pullRequestListLimit = 10;
const focusAgeLimitMs = 14 * dayMs;
const excludedFocusBucketLabels = new Set(['Stalled', 'Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community']);
const disqualifyingFocusBucketLabels = new Set(['Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community']);
const focusBucketRanks = new Map([
  ['Regression', -2],
  ['CI failing', -1],
  ['Approved but aging', 0],
  ['Re-review needed', 1],
  ['Ready to merge', 2],
  ['Author response', 3],
  ['Needs review', 4],
  ['Quick wins', 5],
  ['Review started', 6],
]);

function QueueOverview({
  counts,
  attentionBuckets,
  regressionIssueBuckets,
  forMeItems,
  loading,
  selectedBucketId,
  login,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
}: QueueOverviewProps) {
  const [showAllCoreMembers, setShowAllCoreMembers] = useState(false);

  const focusItems = useMemo<FocusItem[]>(() => {
    const blockedKeys = blockedFocusKeys(attentionBuckets);
    return dedupeFocusItems(
      attentionBuckets
        .filter((bucket) => !excludedFocusBucketLabels.has(bucket.label))
        .flatMap((bucket) =>
          bucket.items.map((item) => ({
            ...item,
            bucketLabel: bucket.label,
            bucketTone: bucket.tone,
          }))),
      blockedKeys,
    )
      .filter((item) => isWithinFocusAgeLimit(item.pullRequest));
  }, [attentionBuckets]);

  const coreOpenCount = counts.reduce((total, count) => total + count.openPullRequestCount, 0);
  const activeCoreCounts = counts.filter((count) => count.openPullRequestCount > 0);
  const visibleCoreCounts = showAllCoreMembers ? counts : activeCoreCounts;
  const reviewBuckets = useMemo<AttentionBucket[]>(
    () => forMeItems.length === 0
      ? attentionBuckets
      : [
        ...attentionBuckets,
        {
          label: 'For me',
          summary: login
            ? `Pull requests that need ${login}'s review or response.`
            : 'Pull requests that need your review or response.',
          tone: 'accent',
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
          <p className="empty-for-me">{loading ? 'Loading core team ownership...' : 'No loaded open PRs from core team members.'}</p>
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
          Needs attention shows actionable PRs opened in the last 14 days, dedupes overlapping lanes,
          and excludes automation/docs/community work.
        </p>
      </div>

      <section className="focus-panel" aria-label="Focused attention queue">
        <div className="attention-card-header">
          <span>Needs attention</span>
          <div className="section-loading-meta">
            {loading && <LoadingBadge />}
            <strong>{formatCount(Math.min(focusItems.length, pullRequestListLimit), 'shown')}</strong>
          </div>
        </div>
        <p>Actionable PRs across the loaded repositories.</p>

        <PullRequestList
          entries={focusItems.map((item) => ({
            pullRequest: item.pullRequest,
            bucketLabel: item.bucketLabel,
            signalProps: {
              leadingSignals: [{ label: item.bucketLabel, tone: item.bucketTone }],
              computedSignalLimit: 4,
            },
          }))}
          limit={pullRequestListLimit}
          emptyState={loading ? 'Loading review queue...' : 'No recent non-automation PRs need attention in the current results.'}
          onSelectPullRequest={onSelectPullRequest}
          onVisiblePullRequest={onVisiblePullRequest}
        />
      </section>

      <section className="owner-drilldown" aria-label="Core team ownership breakdown">
        <div className="attention-card-header">
          <span>Core team ownership</span>
          <div className="section-loading-meta">
            {loading && <LoadingBadge />}
            <strong>{formatCount(coreOpenCount, 'open PR')}</strong>
          </div>
        </div>
        <p className="board-guidance">{formatCount(activeCoreCounts.length, 'active author')} in the loaded queue.</p>
        {renderCoreOwnerDetails()}
      </section>

      {(loading || reviewBuckets.length > 0 || regressionIssueBuckets.length > 0) && (
        <AttentionBoard
          buckets={reviewBuckets}
          regressionIssueBuckets={regressionIssueBuckets}
          loading={loading}
          selectedBucketId={selectedBucketId}
          onSelectBucket={onSelectBucket}
          onSelectPullRequest={onSelectPullRequest}
          onVisiblePullRequest={onVisiblePullRequest}
        />
      )}
    </section>
  );
}

function isWithinFocusAgeLimit(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.createdAt).getTime() <= focusAgeLimitMs;
}

function blockedFocusKeys(buckets: AttentionBucket[]) {
  return new Set(
    buckets
      .filter((bucket) => disqualifyingFocusBucketLabels.has(bucket.label))
      .flatMap((bucket) => bucket.items.map((item) => pullRequestKey(item.pullRequest))),
  );
}

function dedupeFocusItems(items: FocusItem[], blockedKeys: Set<string>) {
  const itemsByPullRequest = new Map<string, FocusItem>();

  for (const item of items) {
    const key = pullRequestKey(item.pullRequest);
    if (blockedKeys.has(key)) {
      continue;
    }

    const existing = itemsByPullRequest.get(key);
    if (!existing || focusBucketRank(item.bucketLabel) < focusBucketRank(existing.bucketLabel)) {
      itemsByPullRequest.set(key, item);
    }
  }

  return [...itemsByPullRequest.values()];
}

function focusBucketRank(label: string) {
  return focusBucketRanks.get(label) ?? Number.MAX_SAFE_INTEGER;
}

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository.toLowerCase()}#${pullRequest.number}`;
}

export default QueueOverview;
