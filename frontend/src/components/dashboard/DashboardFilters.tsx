import type { FormEventHandler } from 'react';
import type { PullRequestSummary, PullState } from '../../types';
import { defaultRepoInput } from '../../constants';

type DashboardFiltersProps = {
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
};

function DashboardFilters({
  repo,
  state,
  pullsLoading,
  pullRequests,
  error,
  onRepoChange,
  onStateChange,
  onSubmit,
}: DashboardFiltersProps) {
  return (
    <section className="panel controls-panel" aria-labelledby="repo-heading">
      <div>
        <p className="eyebrow">Repository</p>
        <h2 id="repo-heading">Dashboard filters</h2>
      </div>

      <form className="repo-form" onSubmit={onSubmit}>
        <label>
          <span>Repositories</span>
          <input
            value={repo}
            onChange={(event) => onRepoChange(event.target.value)}
            placeholder={defaultRepoInput}
            autoComplete="off"
          />
          <small>Separate multiple repositories with commas.</small>
        </label>

        <label>
          <span>State</span>
          <select value={state} onChange={(event) => onStateChange(event.target.value as PullState)}>
            <option value="open">Open</option>
            <option value="closed">Closed</option>
            <option value="all">All</option>
          </select>
        </label>

        <button type="submit" disabled={pullsLoading}>
          {pullsLoading ? 'Loading...' : 'Load PRs'}
        </button>
      </form>

      {error && (
        <div className="error" role="alert">
          {error}
        </div>
      )}

      {pullsLoading && pullRequests.length === 0 && (
        <p className="empty-state">Loading pull requests...</p>
      )}
      {!pullsLoading && pullRequests.length === 0 && (
        <p className="empty-state">No pull requests loaded yet.</p>
      )}
    </section>
  );
}

export default DashboardFilters;
