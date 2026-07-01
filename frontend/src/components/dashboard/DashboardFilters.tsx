import type { FormEventHandler } from 'react';
import type { DashboardMode, PullRequestSummary, PullState, ShipWeekIssueSummary } from '../../types';
import { shipWeekReleaseBranchPlaceholder } from '../../constants';

type DashboardFiltersProps = {
  dashboardMode: DashboardMode;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  issuesLoading: boolean;
  issues: ShipWeekIssueSummary[];
  issuesError: string | null;
  shipWeekMilestone: string;
  currentRelease: string;
  shipWeekReleaseBranch: string;
  shipWeekLoading: boolean;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onShipWeekMilestoneChange: (value: string) => void;
  onShipWeekReleaseBranchChange: (value: string) => void;
  onShipWeekSubmit: FormEventHandler<HTMLFormElement>;
};

function DashboardFilters({
  dashboardMode,
  state,
  pullsLoading,
  pullRequests,
  error,
  issuesLoading,
  issues,
  issuesError,
  shipWeekMilestone,
  currentRelease,
  shipWeekReleaseBranch,
  shipWeekLoading,
  onStateChange,
  onSubmit,
  onShipWeekMilestoneChange,
  onShipWeekReleaseBranchChange,
  onShipWeekSubmit,
}: DashboardFiltersProps) {
  const repoModeActive = dashboardMode !== 'ship';
  const issuesModeActive = dashboardMode === 'issues';
  const repoLoading = issuesModeActive ? issuesLoading : pullsLoading;
  const repoError = issuesModeActive ? issuesError : error;
  const loadedCount = issuesModeActive ? issues.length : pullRequests.length;

  return (
    <section className="panel controls-panel" aria-labelledby="repo-heading">
      <div>
        <p className="eyebrow">Filters</p>
        <h2 id="repo-heading">Dashboard filters</h2>
      </div>

      {repoModeActive ? (
        <>
          <form className="repo-form" onSubmit={onSubmit}>
            <label>
              <span>State</span>
              <select value={state} onChange={(event) => onStateChange(event.target.value as PullState)}>
                <option value="open">Open</option>
                <option value="closed">Closed</option>
                <option value="all">All</option>
              </select>
            </label>

            <div className="repo-form-actions">
              <button type="submit" disabled={repoLoading}>
                {repoLoading ? 'Loading...' : issuesModeActive ? 'Load issues' : 'Load PRs'}
              </button>
            </div>
          </form>

          {repoError && (
            <div className="error" role="alert">
              {repoError}
            </div>
          )}

          {repoLoading && loadedCount === 0 && (
            <p className="empty-state">{issuesModeActive ? 'Loading issues...' : 'Loading pull requests...'}</p>
          )}
          {!repoLoading && loadedCount === 0 && (
            <p className="empty-state">{issuesModeActive ? 'No issues loaded yet.' : 'No pull requests loaded yet.'}</p>
          )}
        </>
      ) : (
        <form className="repo-form ship-week-form" onSubmit={onShipWeekSubmit}>
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

          <div className="repo-form-actions">
            <button type="submit" disabled={shipWeekLoading}>
              {shipWeekLoading ? 'Loading...' : 'Load ship mode'}
            </button>
          </div>
        </form>
      )}
    </section>
  );
}

export default DashboardFilters;
