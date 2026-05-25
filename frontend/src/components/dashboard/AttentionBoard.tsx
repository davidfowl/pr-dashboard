import type { AttentionBucket, PullRequestSummary } from '../../types';
import PullRequestListItem from '../PullRequestListItem';

type AttentionBoardProps = {
  buckets: AttentionBucket[];
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

function AttentionBoard({ buckets, onSelectPullRequest }: AttentionBoardProps) {
  return (
    <section className="attention-board" aria-label="What needs attention">
      <div className="section-title-row">
        <p className="eyebrow">Team review board</p>
        <h3>Current PR state</h3>
        <p className="board-guidance">Oldest unresolved PRs are ranked first so new work does not bury review debt.</p>
      </div>
      <div className="attention-grid">
        {buckets.map((bucket) => (
          <article key={bucket.label} className={`attention-card ${bucket.tone}`}>
            <div className="attention-card-header">
              <span>{bucket.label}</span>
              <strong>{bucket.items.length}</strong>
            </div>
            <p>{bucket.summary} <em>{bucket.metric}</em></p>
            <div className="attention-list">
              {bucket.items.map((item) => (
                <PullRequestListItem
                  key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
                  pullRequest={item.pullRequest}
                  onSelectPullRequest={onSelectPullRequest}
                />
              ))}
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

export default AttentionBoard;
