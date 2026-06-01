import { useState } from 'react';
import type { AttentionBucket, PullRequestSummary } from '../../types';
import { formatCount } from '../../utils/format';
import { bucketRouteId, createBucketUrl } from '../../utils/routing';
import LoadingBadge from '../LoadingBadge';
import PullRequestList from '../PullRequestList';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  loading: boolean;
  selectedBucketId: string;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type ReviewBucketTile = DrilldownTile & {
  bucket: AttentionBucket;
  url: string;
};

const bucketItemLimit = 10;

type CopyStatus = {
  bucketId: string;
  kind: 'success' | 'error';
  message: string;
};

function AttentionBoard({
  buckets,
  loading,
  selectedBucketId,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
}: AttentionBoardProps) {
  const [copyStatus, setCopyStatus] = useState<CopyStatus | null>(null);
  const bucketTiles: ReviewBucketTile[] = buckets.map((bucket) => ({
    id: bucketRouteId(bucket.label),
    label: bucket.label,
    count: bucket.items.length,
    summary: bucket.metric,
    tone: bucket.tone,
    loading,
    bucket,
    url: createBucketUrl(bucketRouteId(bucket.label)),
  }));

  async function copyBucketLink(tile: ReviewBucketTile) {
    onSelectBucket(tile.id);

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
          {loading && <LoadingBadge />}
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
          const pullRequestCount = bucket.items.length;
          const visiblePullRequestCount = Math.min(pullRequestCount, bucketItemLimit);
          const hiddenPullRequestCount = pullRequestCount - visiblePullRequestCount;
          const currentCopyStatus = copyStatus?.bucketId === tile.id ? copyStatus : null;

          return (
            <section className={`drilldown-panel attention-card ${bucket.tone}`} aria-label={`${tile.label} review items`}>
              <div className="attention-card-header">
                <span>{tile.label}</span>
                <div className="bucket-card-actions">
                  {tile.loading && <LoadingBadge />}
                  <strong>{formatCount(pullRequestCount, 'open PR')}</strong>
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
              {hiddenPullRequestCount > 0 && (
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
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
              />
            </section>
          );
        }}
      />
    </section>
  );
}

export default AttentionBoard;
