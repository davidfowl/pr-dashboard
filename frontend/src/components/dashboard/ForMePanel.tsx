import type { PickItem, PullRequestSummary } from '../../types';
import { createForMeSignals } from '../../utils/models';
import { shortRepoName } from '../../utils/routing';

type ForMePanelProps = {
  items: PickItem[];
  login?: string;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

function ForMePanel({ items, login, onSelectPullRequest }: ForMePanelProps) {
  return (
    <section className="pick-panel" aria-label="For me">
      <div className="attention-card-header">
        <span>For me</span>
        <strong>{items.length}</strong>
      </div>
      <p>
        {login
          ? `Clear these before picking up more work. Oldest unresolved reviews stay on top.`
          : 'Sign in with GitHub to personalize this bucket.'}
      </p>
      <div className="pick-list">
        {items.length === 0 && (
          <p className="empty-for-me">
            Nothing directly needs you right now.
          </p>
        )}
        {items.map((item, index) => (
          <button
            key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
            type="button"
            className={`pick-card ${item.tone}`}
            onClick={() => onSelectPullRequest(item.pullRequest.repository, item.pullRequest)}
          >
            <span className="pick-rank">#{index + 1}</span>
            <span className="pick-action">{item.action}</span>
            <strong>{item.pullRequest.title}</strong>
            <span className="pick-meta">
              {shortRepoName(item.pullRequest.repository)} #{item.pullRequest.number} · {item.pullRequest.author}
            </span>
            <span className="pick-signals">
              <span className={`attention-signal ${item.tone}`}>{item.action}</span>
              {createForMeSignals(item).map((signal) => (
                <span key={signal.label} className={`attention-signal ${signal.tone ?? 'muted'}`}>
                  {signal.label}
                </span>
              ))}
            </span>
          </button>
        ))}
      </div>
    </section>
  );
}

export default ForMePanel;
