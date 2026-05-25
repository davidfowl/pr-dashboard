import type { PickItem, PullRequestSummary } from '../../types';
import { shortRepoName } from '../../utils/routing';
import PullRequestSignalPills from '../PullRequestSignalPills';

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
            <PullRequestSignalPills
              pullRequest={item.pullRequest}
              className="pick-signals"
              leadingSignals={[
                { label: item.action, tone: item.tone },
                { label: item.reason.split(' · ')[0], tone: 'accent' },
              ]}
              showActionSignal={false}
            />
          </button>
        ))}
      </div>
    </section>
  );
}

export default ForMePanel;
