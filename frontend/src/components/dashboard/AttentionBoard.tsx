import type { AttentionBucket, PullRequestSummary } from '../../types';
import { formatRelative } from '../../utils/format';
import { createAttentionSignals } from '../../utils/models';
import { shortRepoName } from '../../utils/routing';

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
                <button
                  key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
                  type="button"
                  onClick={() => onSelectPullRequest(item.pullRequest.repository, item.pullRequest)}
                >
                  <span className="attention-pr-number">#{item.pullRequest.number}</span>
                  <span className="attention-pr-repo">{shortRepoName(item.pullRequest.repository)}</span>
                  <strong>{item.pullRequest.title}</strong>
                  <span className="attention-pr-meta">
                    {item.pullRequest.author} · updated {formatRelative(item.pullRequest.updatedAt)}
                  </span>
                  <span className="attention-pr-signals">
                    {createAttentionSignals(item).map((signal) => (
                      <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                        {signal.label}
                      </span>
                    ))}
                  </span>
                </button>
              ))}
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

export default AttentionBoard;
