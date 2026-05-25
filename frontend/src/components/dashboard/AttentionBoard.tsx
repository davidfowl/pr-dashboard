import { useState } from 'react';
import type { AttentionBucket, PullRequestSummary } from '../../types';
import { formatCount } from '../../utils/format';
import PullRequestList from '../PullRequestList';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type ReviewBucketTile = DrilldownTile & {
  bucket: AttentionBucket;
};

const bucketItemLimit = 10;

function AttentionBoard({ buckets, onSelectPullRequest }: AttentionBoardProps) {
  const [selectedBucketLabel, setSelectedBucketLabel] = useState(buckets[0]?.label ?? '');
  const bucketTiles: ReviewBucketTile[] = buckets.map((bucket) => ({
    id: bucket.label,
    label: bucket.label,
    count: bucket.items.length,
    summary: bucket.metric,
    tone: bucket.tone,
    bucket,
  }));

  return (
    <section className="attention-board" aria-label="Review signal lanes">
      <div className="section-title-row">
        <p className="eyebrow">Team review board</p>
        <h3>Review signal lanes</h3>
        <p className="board-guidance">
          Lanes can overlap: automation, docs, stale, and review-state signals stay visible without hiding each other.
        </p>
      </div>

      <TileDrilldown
        className="review-bucket-drilldown"
        ariaLabel="Review signal lane details"
        idPrefix="review-bucket"
        selectedId={selectedBucketLabel}
        tileListLabel="Review signal lanes"
        tiles={bucketTiles}
        onSelect={setSelectedBucketLabel}
        renderDetails={({ bucket }) => {
          const visibleCount = Math.min(bucket.items.length, bucketItemLimit);
          const hiddenCount = bucket.items.length - visibleCount;

          return (
            <section className={`drilldown-panel attention-card ${bucket.tone}`} aria-label={`${bucket.label} pull requests`}>
              <div className="attention-card-header">
                <span>{bucket.label}</span>
                <strong>{formatCount(bucket.items.length, 'open PR')}</strong>
              </div>
              <p>{bucket.summary} <em>{bucket.metric}</em></p>
              {hiddenCount > 0 && (
                <p className="bucket-limit-note">
                  Showing top {visibleCount} of {bucket.items.length}. Resolve these and the next ranked PRs will surface on refresh.
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
              />
            </section>
          );
        }}
      />
    </section>
  );
}

export default AttentionBoard;
