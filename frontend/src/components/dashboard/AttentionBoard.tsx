import { useState } from 'react';
import type { AttentionBucket, AttentionIssueBucket, PullRequestSummary } from '../../types';
import { formatCount } from '../../utils/format';
import { bucketRouteId, createBucketUrl } from '../../utils/routing';
import IssueListItem from '../IssueListItem';
import LoadingBadge from '../LoadingBadge';
import PullRequestList from '../PullRequestList';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  regressionIssueBuckets: AttentionIssueBucket[];
  loading: boolean;
  selectedBucketId: string;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type ReviewBucketTile = DrilldownTile & {
  bucket?: AttentionBucket;
  issueBucket?: AttentionIssueBucket;
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
  regressionIssueBuckets,
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
  for (const issueBucket of regressionIssueBuckets) {
    const id = bucketRouteId(issueBucket.label);
    const existingTile = bucketTiles.find((tile) => tile.id === id);
    if (existingTile) {
      existingTile.count += issueBucket.issues.length;
      existingTile.issueBucket = issueBucket;
      existingTile.loading = existingTile.loading || loading;
    } else {
      bucketTiles.push({
        id,
        label: issueBucket.label,
        count: issueBucket.issues.length,
        summary: issueBucket.metric,
        tone: issueBucket.tone,
        loading,
        issueBucket,
        url: createBucketUrl(id),
      });
    }
  }

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
          const issueBucket = tile.issueBucket;
          const pullRequestCount = bucket?.items.length ?? 0;
          const issueCount = issueBucket?.issues.length ?? 0;
          const visiblePullRequestCount = Math.min(pullRequestCount, bucketItemLimit);
          const hiddenPullRequestCount = pullRequestCount - visiblePullRequestCount;
          const visibleIssueCount = Math.min(issueCount, bucketItemLimit);
          const hiddenIssueCount = issueCount - visibleIssueCount;
          const currentCopyStatus = copyStatus?.bucketId === tile.id ? copyStatus : null;
          const detailTone = bucket?.tone ?? issueBucket?.tone ?? tile.tone;
          const detailSummary = bucket?.summary ?? issueBucket?.summary ?? '';
          const detailMetric = bucket?.metric ?? issueBucket?.metric ?? tile.summary;
          const countLabel = [
            pullRequestCount > 0 ? formatCount(pullRequestCount, 'open PR') : null,
            issueCount > 0 ? formatCount(issueCount, 'open issue') : null,
          ].filter(Boolean).join(' · ');

          return (
            <section className={`drilldown-panel attention-card ${detailTone}`} aria-label={`${tile.label} review items`}>
              <div className="attention-card-header">
                <span>{tile.label}</span>
                <div className="bucket-card-actions">
                  {tile.loading && <LoadingBadge />}
                  <strong>{countLabel || formatCount(0, 'item')}</strong>
                  <button
                    type="button"
                    className="bucket-link-button"
                    onClick={() => void copyBucketLink(tile)}
                  >
                    {currentCopyStatus?.kind === 'success' ? 'Copied' : 'Copy link'}
                  </button>
                </div>
              </div>
              <p>{detailSummary} <em>{detailMetric}</em></p>
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
              {bucket && (
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
              )}
              {issueBucket && (
                <>
                  {hiddenIssueCount > 0 && (
                    <p className="bucket-limit-note">
                      Showing top {visibleIssueCount} of {issueCount} regression issues.
                    </p>
                  )}
                  <div className="attention-list">
                    {issueBucket.issues.slice(0, bucketItemLimit).map((issue) => (
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
                </>
              )}
            </section>
          );
        }}
      />
    </section>
  );
}

export default AttentionBoard;
