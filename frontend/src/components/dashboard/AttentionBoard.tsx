import { useState } from 'react';
import type { AttentionBucket, PullRequestSummary, VisiblePullRequestHandler } from '../../types';
import { formatCount } from '../../utils/format';
import { bucketRouteId, createBucketUrl } from '../../utils/routing';
import HelpTooltip from '../HelpTooltip';
import LoadingBadge from '../LoadingBadge';
import LoadingCardPlaceholders from '../LoadingCardPlaceholders';
import LoadingMetric from '../LoadingMetric';
import PullRequestList from '../PullRequestList';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  loading: boolean;
  hasLoaded: boolean;
  selectedBucketId: string;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: VisiblePullRequestHandler;
  visibleChecksRefreshKey: number;
};

type ReviewBucketTile = DrilldownTile & {
  bucket?: AttentionBucket;
  url?: string;
};

const bucketItemLimit = 10;
const reviewSignalHelp = 'These lanes are signals, not mutually exclusive statuses. A PR can be both Needs review and Stalled; the top queue uses the highest-priority actionable lane while this board keeps every matching signal visible.';
const stalledLaneHelp = 'Stalled means the PR has been quiet for at least 7 days. It is kept as a signal lane here, not a reason to remove the PR from Needs review, CI failing, Author response, or Ready to merge.';

type CopyStatus = {
  bucketId: string;
  kind: 'success' | 'error';
  message: string;
};

function AttentionBoard({
  buckets,
  loading,
  hasLoaded,
  selectedBucketId,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
  visibleChecksRefreshKey,
}: AttentionBoardProps) {
  const [copyStatus, setCopyStatus] = useState<CopyStatus | null>(null);
  const bucketTiles = createReviewBucketTiles(buckets, loading, hasLoaded);

  async function copyBucketLink(tile: ReviewBucketTile) {
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
    <section className="attention-board" aria-label="Review signal lanes">
      <div className="section-title-row">
        <p className="eyebrow">Team review board</p>
        <div className="section-title-heading">
          <h3>Review signal lanes</h3>
          <HelpTooltip label={reviewSignalHelp} />
          {loading && <LoadingBadge label={hasLoaded ? 'Refreshing' : 'Loading'} />}
        </div>
        <p className="board-guidance">
          Lanes can overlap: automation, docs, stale, and review-state signals stay visible without hiding each other.
        </p>
      </div>

      <TileDrilldown
        className="review-bucket-drilldown"
        ariaLabel="Review signal lane details"
        idPrefix="review-bucket"
        selectedId={selectedBucketId}
        tileListLabel="Review signal lanes"
        tiles={bucketTiles}
        onSelect={onSelectBucket}
        renderDetails={(tile) => {
          const { bucket } = tile;
          if (!bucket) {
            return (
              <section className="drilldown-panel attention-card loading-card-panel" aria-label="Loading review signal cards">
                <div className="attention-card-header">
                  <span>{tile.label}</span>
                  <LoadingBadge label="Loading" />
                </div>
                <p>Review cards will appear when this lane finishes loading.</p>
                <LoadingCardPlaceholders count={3} label="Loading review signal cards" />
              </section>
            );
          }

          const pullRequestCount = bucket.items.length;
          const visiblePullRequestCount = Math.min(pullRequestCount, bucketItemLimit);
          const hiddenPullRequestCount = pullRequestCount - visiblePullRequestCount;
          const currentCopyStatus = copyStatus?.bucketId === tile.id ? copyStatus : null;
          const helpLabel = helpLabelForBucket(bucket.label);

          return (
            <section className={`drilldown-panel attention-card ${bucket.tone}`} aria-label={`${tile.label} review items`}>
              <div className="attention-card-header">
                <div className="attention-card-title">
                  <span>{tile.label}</span>
                  {helpLabel && <HelpTooltip label={helpLabel} />}
                </div>
                <div className="bucket-card-actions">
                  {tile.loading && <LoadingBadge label={tile.hasLoaded ? 'Refreshing' : 'Loading'} />}
                  <LoadingMetric
                    value={pullRequestCount}
                    loading={tile.loading === true}
                    hasLoaded={tile.hasLoaded === true}
                    formatValue={(count) => formatCount(count, 'open PR')}
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
              <p>{bucket.summary} <em>{bucket.metric}</em></p>
              {currentCopyStatus && (
                <p
                  className={`bucket-link-status ${currentCopyStatus.kind}`}
                  role={currentCopyStatus.kind === 'error' ? 'alert' : 'status'}
                >
                  {currentCopyStatus.message}
                </p>
              )}
              {!tile.loading && hiddenPullRequestCount > 0 && (
                <p className="bucket-limit-note">
                  Showing top {visiblePullRequestCount} of {pullRequestCount}. Resolve these and the next ranked PRs will surface on refresh.
                </p>
              )}
              <PullRequestList
                entries={bucket.items.map((item) => ({
                  pullRequest: item.pullRequest,
                  bucketLabel: bucket.label,
                  signalProps: {
                    leadingSignals: [{ label: item.reason, tone: bucket.tone }],
                    excludeComputedLabels: [item.reason],
                    computedSignalLimit: 4,
                  },
                }))}
                limit={bucketItemLimit}
                preserveOrder={bucket.preserveItemOrder}
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
                visibleChecksRefreshKey={visibleChecksRefreshKey}
              />
            </section>
          );
        }}
      />
    </section>
  );
}

function createReviewBucketTiles(
  buckets: AttentionBucket[],
  loading: boolean,
  hasLoaded: boolean,
): ReviewBucketTile[] {
  if (buckets.length > 0 || hasLoaded || !loading) {
    return buckets.map((bucket) => ({
      id: bucketRouteId(bucket.label),
      label: bucket.label,
      count: bucket.items.length,
      summary: bucket.metric,
      tone: bucket.tone,
      loading,
      hasLoaded,
      bucket,
      url: createBucketUrl(bucketRouteId(bucket.label)),
    }));
  }

  return Array.from({ length: 3 }, (_, index) => ({
    id: `loading-review-lane-${index + 1}`,
    label: `Loading lane ${index + 1}`,
    count: 0,
    summary: 'Waiting for data',
    loading,
    hasLoaded,
    placeholder: true,
  }));
}

function helpLabelForBucket(label: string) {
  return label === 'Stalled' ? stalledLaneHelp : null;
}

export default AttentionBoard;
