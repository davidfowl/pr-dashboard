import { useMemo, useState } from 'react';
import type { AttentionIssueBucket, ShipWeekIssueSummary } from '../../types';
import { formatCount } from '../../utils/format';
import { bucketRouteId, createBucketUrl } from '../../utils/routing';
import IssueListItem from '../IssueListItem';
import LoadingBadge from '../LoadingBadge';
import LoadingCardPlaceholders from '../LoadingCardPlaceholders';
import LoadingMetric from '../LoadingMetric';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type IssuesOverviewProps = {
  issueBuckets: AttentionIssueBucket[];
  loading: boolean;
  hasLoaded: boolean;
  selectedBucketId: string;
  onSelectBucket: (bucketId: string) => void;
};

type IssueBucketTile = DrilldownTile & {
  issueBucket?: AttentionIssueBucket;
  url?: string;
};

type IssueFocusItem = {
  issue: ShipWeekIssueSummary;
  bucketLabel: string;
  bucketTone: AttentionIssueBucket['tone'];
  bucketRank: number;
};

type CopyStatus = {
  bucketId: string;
  kind: 'success' | 'error';
  message: string;
};

const issueFocusLimit = 10;
const issueBucketItemLimit = 10;

function IssuesOverview({
  issueBuckets,
  loading,
  hasLoaded,
  selectedBucketId,
  onSelectBucket,
}: IssuesOverviewProps) {
  const [copyStatus, setCopyStatus] = useState<CopyStatus | null>(null);
  const issueFocusItems = useMemo(
    () => dedupeIssueFocusItems(issueBuckets.flatMap((bucket, bucketRank) =>
      bucket.issues.map((issue) => ({
        issue,
        bucketLabel: bucket.label,
        bucketTone: bucket.tone,
        bucketRank,
      })))),
    [issueBuckets],
  );
  const bucketTiles = createIssueBucketTiles(issueBuckets, loading, hasLoaded);
  const issueFocusShownCount = Math.min(issueFocusItems.length, issueFocusLimit);
  const loadingLabel = hasLoaded ? 'Refreshing' : 'Loading';

  async function copyBucketLink(tile: IssueBucketTile) {
    onSelectBucket(tile.id);
    if (!tile.url) {
      return;
    }

    if (!navigator.clipboard) {
      setCopyStatus({
        bucketId: tile.id,
        kind: 'error',
        message: 'Clipboard unavailable. Copy the address bar URL instead.',
      });
      return;
    }

    try {
      await navigator.clipboard.writeText(tile.url);
      setCopyStatus({
        bucketId: tile.id,
        kind: 'success',
        message: 'Copied bucket link.',
      });
    } catch (err) {
      setCopyStatus({
        bucketId: tile.id,
        kind: 'error',
        message: err instanceof Error ? `Could not copy link: ${err.message}` : 'Could not copy bucket link.',
      });
    }
  }

  return (
    <section className="queue-overview" aria-label="Issue focus overview">
      <div className="section-title-row">
        <p className="eyebrow">Issue focus</p>
        <h3>Focused issue buckets across repos.</h3>
        <p className="board-guidance">
          Issues are tracked separately from PR review work so manual validation and regression follow-up stay visible.
        </p>
      </div>

      <section className="focus-panel" aria-label="Focused issue queue">
        <div className="attention-card-header">
          <span>Needs attention</span>
          <div className="section-loading-meta">
            {loading && <LoadingBadge label={loadingLabel} />}
            <LoadingMetric
              value={issueFocusShownCount}
              loading={loading}
              hasLoaded={hasLoaded}
              formatValue={(count) => formatCount(count, 'shown')}
              pendingLabel="Issue focus count is loading"
            />
          </div>
        </div>
        <p>Actionable issues across the loaded repositories.</p>

        <div className="attention-list">
          {loading && !hasLoaded && issueFocusItems.length === 0 ? (
            <LoadingCardPlaceholders label="Loading issue focus cards" />
          ) : issueFocusItems.length === 0 ? (
            <p className="empty-for-me">{loading ? 'Loading issue focus...' : 'No focused issues in the current results.'}</p>
          ) : issueFocusItems.slice(0, issueFocusLimit).map((item) => (
            <IssueListItem
              key={`${item.issue.repository}#${item.issue.number}`}
              issue={item.issue}
              signalProps={{
                leadingSignals: [{ label: item.bucketLabel, tone: item.bucketTone }],
                excludeComputedLabels: [item.bucketLabel],
                computedSignalLimit: 4,
              }}
            />
          ))}
        </div>
      </section>

      {(loading || bucketTiles.length > 0) && (
        <section className="attention-board" aria-label="Issue focus lanes">
          <div className="section-title-row">
            <p className="eyebrow">Issue buckets</p>
            <div className="section-title-heading">
              <h3>Issue focus lanes</h3>
              {loading && <LoadingBadge label={loadingLabel} />}
            </div>
            <p className="board-guidance">
              Buckets can overlap; the focused list dedupes issues while details preserve each signal lane.
            </p>
          </div>

          <TileDrilldown
            className="review-bucket-drilldown"
            ariaLabel="Issue focus lane details"
            idPrefix="issue-bucket"
            selectedId={selectedBucketId}
            tileListLabel="Issue focus lanes"
            tiles={bucketTiles}
            onSelect={onSelectBucket}
            renderDetails={(tile) => {
              const issueBucket = tile.issueBucket;
              if (!issueBucket) {
                return (
                  <section className="drilldown-panel attention-card loading-card-panel" aria-label="Loading issue focus cards">
                    <div className="attention-card-header">
                      <span>{tile.label}</span>
                      <LoadingBadge label="Loading" />
                    </div>
                    <p>Issue cards will appear when this lane finishes loading.</p>
                    <LoadingCardPlaceholders count={3} label="Loading issue focus cards" />
                  </section>
                );
              }

              const visibleIssueCount = Math.min(issueBucket.issues.length, issueBucketItemLimit);
              const hiddenIssueCount = issueBucket.issues.length - visibleIssueCount;
              const currentCopyStatus = copyStatus?.bucketId === tile.id ? copyStatus : null;

              return (
                <section className={`drilldown-panel attention-card ${issueBucket.tone}`} aria-label={`${tile.label} issue items`}>
                  <div className="attention-card-header">
                    <span>{tile.label}</span>
                    <div className="bucket-card-actions">
                      {tile.loading && <LoadingBadge label={tile.hasLoaded ? 'Refreshing' : 'Loading'} />}
                      <LoadingMetric
                        value={issueBucket.issues.length}
                        loading={tile.loading === true}
                        hasLoaded={tile.hasLoaded === true}
                        formatValue={(count) => formatCount(count, 'open issue')}
                        pendingLabel={`${tile.label} count is loading`}
                      />
                      <button
                        type="button"
                        className="bucket-link-button"
                        onClick={() => void copyBucketLink(tile)}
                      >
                        {currentCopyStatus?.kind === 'success' ? 'Copied' : 'Copy link'}
                      </button>
                    </div>
                  </div>
                  <p>{issueBucket.summary} <em>{issueBucket.metric}</em></p>
                  {currentCopyStatus && (
                    <p
                      className={`bucket-link-status ${currentCopyStatus.kind}`}
                      role={currentCopyStatus.kind === 'error' ? 'alert' : 'status'}
                    >
                      {currentCopyStatus.message}
                    </p>
                  )}
                  {!tile.loading && hiddenIssueCount > 0 && (
                    <p className="bucket-limit-note">
                      Showing top {visibleIssueCount} of {issueBucket.issues.length} open issues.
                    </p>
                  )}
                  <div className="attention-list">
                    {issueBucket.issues.slice(0, issueBucketItemLimit).map((issue) => (
                      <IssueListItem
                        key={`${issue.repository}#${issue.number}`}
                        issue={issue}
                        signalProps={{
                          leadingSignals: [{ label: issueBucket.label, tone: issueBucket.tone }],
                          excludeComputedLabels: [issueBucket.label],
                          computedSignalLimit: 4,
                        }}
                      />
                    ))}
                  </div>
                </section>
              );
            }}
          />
        </section>
      )}
    </section>
  );
}

function createIssueBucketTiles(
  issueBuckets: AttentionIssueBucket[],
  loading: boolean,
  hasLoaded: boolean,
): IssueBucketTile[] {
  if (issueBuckets.length > 0 || hasLoaded || !loading) {
    return issueBuckets.map((issueBucket) => {
      const id = bucketRouteId(issueBucket.label);
      return {
        id,
        label: issueBucket.label,
        count: issueBucket.issues.length,
        summary: issueBucket.metric,
        tone: issueBucket.tone,
        loading,
        hasLoaded,
        issueBucket,
        url: createBucketUrl(id),
      };
    });
  }

  return Array.from({ length: 3 }, (_, index) => ({
    id: `loading-issue-lane-${index + 1}`,
    label: `Loading lane ${index + 1}`,
    count: 0,
    summary: 'Waiting for data',
    loading,
    hasLoaded,
    placeholder: true,
  }));
}

function dedupeIssueFocusItems(items: IssueFocusItem[]) {
  const itemsByIssue = new Map<string, IssueFocusItem>();
  for (const item of items) {
    const key = issueKey(item.issue);
    const existing = itemsByIssue.get(key);
    if (!existing || issueFocusRank(item) < issueFocusRank(existing)) {
      itemsByIssue.set(key, item);
    }
  }

  return [...itemsByIssue.values()].sort((first, second) =>
    issueFocusRank(first) - issueFocusRank(second)
    || new Date(second.issue.updatedAt).getTime() - new Date(first.issue.updatedAt).getTime());
}

function issueFocusRank(item: IssueFocusItem) {
  return item.bucketRank;
}

function issueKey(issue: ShipWeekIssueSummary) {
  return `${issue.repository.toLowerCase()}#${issue.number}`;
}

export default IssuesOverview;
