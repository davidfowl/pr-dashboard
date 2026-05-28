import type { FormEventHandler } from 'react';
import type { DashboardMode, PullRequestSummary, PullState } from '../../types';
import { currentRelease, defaultRepoInput, shipWeekReleaseBranchPlaceholder } from '../../constants';

type DashboardFiltersProps = {
  dashboardMode: DashboardMode;
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  shipWeekRepo: string;
  shipWeekMilestone: string;
  shipWeekReleaseBranch: string;
  shipWeekLoading: boolean;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onShipWeekRepoChange: (value: string) => void;
  onShipWeekMilestoneChange: (value: string) => void;
  onShipWeekReleaseBranchChange: (value: string) => void;
  onShipWeekSubmit: FormEventHandler<HTMLFormElement>;
};

function DashboardFilters({
  dashboardMode,
  repo,
  state,
  pullsLoading,
  pullRequests,
  error,
  shipWeekRepo,
  shipWeekMilestone,
  shipWeekReleaseBranch,
  shipWeekLoading,
  onRepoChange,
  onStateChange,
  onSubmit,
  onShipWeekRepoChange,
  onShipWeekMilestoneChange,
  onShipWeekReleaseBranchChange,
  onShipWeekSubmit,
}: DashboardFiltersProps) {
  return (
    <section className="panel controls-panel" aria-labelledby="repo-heading">
      <div>
        <p className="eyebrow">Filters</p>
        <h2 id="repo-heading">Dashboard filters</h2>
      </div>

      {dashboardMode === 'review' ? (
        <>
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
        </>
      ) : (
        <form className="repo-form ship-week-form" onSubmit={onShipWeekSubmit}>
          <label>
            <span>Ship mode repo</span>
            <input
              value={shipWeekRepo}
              onChange={(event) => onShipWeekRepoChange(event.target.value)}
              placeholder="microsoft/aspire"
              autoComplete="off"
            />
            <small>Ship mode ignores PRs outside the milestone and base branch.</small>
          </label>

          <label>
            <span>Milestone</span>
            <input
              value={shipWeekMilestone}
              onChange={(event) => onShipWeekMilestoneChange(event.target.value)}
              placeholder={currentRelease}
              autoComplete="off"
            />
          </label>

          <label>
            <span>Release branch</span>
            <input
              value={shipWeekReleaseBranch}
              onChange={(event) => onShipWeekReleaseBranchChange(event.target.value)}
              placeholder={shipWeekReleaseBranchPlaceholder}
              autoComplete="off"
            />
          </label>

          <button type="submit" disabled={shipWeekLoading}>
            {shipWeekLoading ? 'Loading...' : 'Load ship mode'}
          </button>
        </form>
      )}
    </section>
  );
}

export default DashboardFilters;
