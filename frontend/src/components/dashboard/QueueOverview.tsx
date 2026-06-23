import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { dayMs } from '../../constants';
import type {
  AttentionBucket,
  AttentionItem,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  VisiblePullRequestHandler,
} from '../../types';
import { colorForText, formatCount, formatRelative } from '../../utils/format';
import GitHubAvatar from '../GitHubAvatar';
import HelpTooltip from '../HelpTooltip';
import LoadingBadge from '../LoadingBadge';
import LoadingCardPlaceholders from '../LoadingCardPlaceholders';
import LoadingMetric from '../LoadingMetric';
import PullRequestList from '../PullRequestList';
import AttentionBoard from './AttentionBoard';

type QueueOverviewProps = {
  counts: DeveloperPullRequestCount[];
  attentionBuckets: AttentionBucket[];
  forMeItems: PickItem[];
  loading: boolean;
  hasLoaded: boolean;
  selectedBucketId: string;
  login?: string;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: VisiblePullRequestHandler;
  visibleChecksRefreshKey: number;
};

type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

const pullRequestListLimit = 10;
const focusAgeLimitMs = 14 * dayMs;
const queueOverviewHelp = 'Needs attention picks each PR from its highest-priority action lane, keeps only PRs opened in the last 14 days, and hides standalone non-review lanes like docs, automation, community, drafts, merge conflicts, Copilot feedback, and stalled.';
const needsAttentionHelp = 'Stalled means quiet for 7+ days. It is not shown as a standalone top-queue lane, but a stalled PR still appears here when it also needs review, CI fixes, author response, or merge.';
const excludedFocusBucketLabels = new Set(['Stalled', 'Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Copilot feedback', 'Merge conflicts']);
const disqualifyingFocusBucketLabels = new Set(['Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Copilot feedback', 'Merge conflicts']);
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
  forMeItems,
  loading,
  hasLoaded,
  selectedBucketId,
  login,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
  visibleChecksRefreshKey,
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
  const focusShownCount = Math.min(focusItems.length, pullRequestListLimit);
  const loadingLabel = hasLoaded ? 'Refreshing' : 'Loading';
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
          // forMeItems arrives pre-ranked by pickScore; keep that order instead of the recency re-sort.
          preserveItemOrder: true,
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
            {showAllCoreMembers ? 'Show active only' : loading && !hasLoaded ? 'Show all' : `Show all ${counts.length}`}
          </button>
        </div>
        {visibleCoreCounts.length === 0 ? (
          loading && !hasLoaded ? (
            <LoadingCardPlaceholders
              className="core-member-loading-list"
              count={3}
              label="Loading core team ownership cards"
            />
          ) : (
            <p className="empty-for-me">{loading ? 'Loading core team ownership...' : 'No loaded open PRs from core team members.'}</p>
          )
        ) : (
          <div className="core-member-list">
            {visibleCoreCounts.map((count) => (
              <article
                key={count.actor}
                className="core-member-row"
                style={{ '--developer-accent': colorForText(count.actor) } as CSSProperties}
              >
                <GitHubAvatar login={count.actor} />
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
        <div className="section-title-heading">
          <h3>One queue across repos: recent PRs first, bucket details one click away.</h3>
          <HelpTooltip label={queueOverviewHelp} />
        </div>
        <p className="board-guidance">
          Needs attention shows primary action lanes; hover the help icon for the exact include/exclude rules.
        </p>
      </div>

      <section className="focus-panel" aria-label="Focused attention queue">
        <div className="attention-card-header">
          <div className="attention-card-title">
            <span>Needs attention</span>
            <HelpTooltip label={needsAttentionHelp} />
          </div>
          <div className="section-loading-meta">
            {loading && <LoadingBadge label={loadingLabel} />}
            <LoadingMetric
              value={focusShownCount}
              loading={loading}
              hasLoaded={hasLoaded}
              formatValue={(count) => formatCount(count, 'shown')}
              pendingLabel="Needs attention count is loading"
            />
          </div>
        </div>
        <p>Actionable PRs across the loaded repositories.</p>

        {loading && !hasLoaded && focusItems.length === 0 ? (
          <LoadingCardPlaceholders label="Loading review queue cards" />
        ) : (
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
            visibleChecksRefreshKey={visibleChecksRefreshKey}
          />
        )}
      </section>

      <section className="owner-drilldown" aria-label="Core team ownership breakdown">
        <div className="attention-card-header">
          <span>Core team ownership</span>
          <div className="section-loading-meta">
            {loading && <LoadingBadge label={loadingLabel} />}
            <LoadingMetric
              value={coreOpenCount}
              loading={loading}
              hasLoaded={hasLoaded}
              formatValue={(count) => formatCount(count, 'open PR')}
              pendingLabel="Core team ownership count is loading"
            />
          </div>
        </div>
        <p className="board-guidance">
          {loading && !hasLoaded ? (
            'Active authors will appear when the queue finishes loading.'
          ) : (
            <>
              <LoadingMetric
                value={activeCoreCounts.length}
                loading={loading}
                hasLoaded={hasLoaded}
                formatValue={(count) => formatCount(count, 'active author')}
                pendingLabel="Active author count is loading"
              />
              {' '}
              in the loaded queue.
            </>
          )}
        </p>
        {renderCoreOwnerDetails()}
      </section>

      {(loading || reviewBuckets.length > 0) && (
        <AttentionBoard
          buckets={reviewBuckets}
          loading={loading}
          hasLoaded={hasLoaded}
          selectedBucketId={selectedBucketId}
          onSelectBucket={onSelectBucket}
          onSelectPullRequest={onSelectPullRequest}
          onVisiblePullRequest={onVisiblePullRequest}
          visibleChecksRefreshKey={visibleChecksRefreshKey}
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
