import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import type {
  AttentionBucket,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  VisiblePullRequestHandler,
} from '../../types';
import { colorForText, formatCount, formatRelative } from '../../utils/format';
import { computeFocusItems } from './focusQueue';
import type { FocusItem } from './focusQueue';
import GitHubAvatar from '../GitHubAvatar';
import HelpTooltip from '../HelpTooltip';
import LoadingBadge from '../LoadingBadge';
import LoadingCardPlaceholders from '../LoadingCardPlaceholders';
import LoadingMetric from '../LoadingMetric';
import PullRequestList from '../PullRequestList';
import AttentionBoard from './AttentionBoard';
import FocusExclusionDialog from './FocusExclusionDialog';

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

const pullRequestListLimit = 10;
const queueOverviewHelp = 'Needs attention is the focused action queue: each PR appears once under its highest-priority actionable lane when that lane has fresh activity. Activity is lane-specific, such as the latest approval/review for merge lanes, the newest commit for re-review, or the PR update time for review-needed work. PRs with failing CI are excluded until their checks are green again.';
const needsAttentionHelp = 'Being in Needs attention means the PR has an actionable reason for someone to review, respond, or merge, and that reason was refreshed in the last 14 days. PRs with failing CI are excluded until their checks pass, and standalone signal lanes like stalled, docs, automation, community, drafts, merge conflicts, and unresolved feedback stay out of this top queue.';

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
  const [showFilterInfo, setShowFilterInfo] = useState(false);

  const focusItems = useMemo<FocusItem[]>(
    () => computeFocusItems(attentionBuckets),
    [attentionBuckets],
  );

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
          <h3>One focused action queue across repos, with lane details one click away.</h3>
          <HelpTooltip label={queueOverviewHelp} />
        </div>
        <p className="board-guidance">
          Needs attention shows one actionable row per PR; age is measured from the lane activity, not the date opened.
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
        <p>PRs with recent action-relevant activity that need someone to review, respond, or merge.</p>
        <p className="focus-info-note">
          Not finding the PR you were looking for?{' '}
          <button
            type="button"
            className="focus-info-trigger"
            onClick={() => setShowFilterInfo(true)}
          >
            See why it might be filtered out
          </button>
        </p>
        <FocusExclusionDialog open={showFilterInfo} onClose={() => setShowFilterInfo(false)} />

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
            emptyState={loading ? 'Loading review queue...' : 'No PRs with recent action-relevant activity need attention in the current results.'}
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

export default QueueOverview;
