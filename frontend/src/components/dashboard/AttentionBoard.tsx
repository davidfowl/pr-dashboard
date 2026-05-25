import { useState } from 'react';
import type { AttentionBucket, PullRequestSummary } from '../../types';
import { formatCount } from '../../utils/format';
import PullRequestListItem from '../PullRequestListItem';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type ReviewBucketTile = DrilldownTile & {
  bucket: AttentionBucket;
};

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
    <section className="attention-board" aria-label="Review state buckets">
      <div className="section-title-row">
        <p className="eyebrow">Team review board</p>
        <h3>Review state buckets</h3>
        <p className="board-guidance">
          Slice by state first, then open the ranked details only when a bucket needs attention.
        </p>
      </div>

      <TileDrilldown
        className="review-bucket-drilldown"
        ariaLabel="Review state bucket details"
        idPrefix="review-bucket"
        selectedId={selectedBucketLabel}
        tileListLabel="Review state buckets"
        tiles={bucketTiles}
        onSelect={setSelectedBucketLabel}
        renderDetails={({ bucket }) => (
          <section className={`drilldown-panel attention-card ${bucket.tone}`} aria-label={`${bucket.label} pull requests`}>
            <div className="attention-card-header">
              <span>{bucket.label}</span>
              <strong>{formatCount(bucket.items.length, 'open PR')}</strong>
            </div>
            <p>{bucket.summary} <em>{bucket.metric}</em></p>
            <div className="attention-list">
              {bucket.items.map((item) => (
                <PullRequestListItem
                  key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
                  pullRequest={item.pullRequest}
                  onSelectPullRequest={onSelectPullRequest}
                  signalProps={{
                    leadingSignals: [{ label: item.reason, tone: bucket.tone }],
                    excludeComputedLabels: [item.reason],
                    computedSignalLimit: 4,
                  }}
                />
              ))}
            </div>
          </section>
        )}
      />
    </section>
  );
}

export default AttentionBoard;
