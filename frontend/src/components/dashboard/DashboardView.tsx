import type { FormEventHandler, RefObject } from 'react';
import type {
  AttentionBucket,
  AttentionIssueBucket,
  DashboardMode,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  PullState,
  ReviewLoadPerfStats,
  ShipWeekIssueSummary,
  ShipWeekLoadingState,
  ShipWeekResponse,
  VisiblePullRequestHandler,
} from '../../types';
import { formatDuration, formatRelative, formatTime } from '../../utils/format';
import DashboardFilters from './DashboardFilters';
import IssuesOverview from './IssuesOverview';
import QueueOverview from './QueueOverview';
import ShipWeekSection from './ShipWeekSection';

type DashboardViewProps = {
  dashboardMode: DashboardMode;
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  developerPullRequestCounts: DeveloperPullRequestCount[];
  attentionBuckets: AttentionBucket[];
  forMeItems: PickItem[];
  issues: ShipWeekIssueSummary[];
  issueBuckets: AttentionIssueBucket[];
  issuesLoading: boolean;
  issuesError: string | null;
  shipWeek: ShipWeekResponse | null;
  shipWeekLoading: boolean;
  shipWeekSectionLoading: ShipWeekLoadingState;
  shipWeekError: string | null;
  shipWeekRepo: string;
  shipWeekMilestone: string;
  shipWeekReleaseBranch: string;
  shipWeekSnapshotStatus: string | null;
  shipWeekSnapshotError: string | null;
  shipWeekSnapshotExporting: boolean;
  shipWeekSnapshotCopying: boolean;
  showShipWeekSnapshotDownload: boolean;
  shipWeekSnapshotRef: RefObject<HTMLElement | null>;
  selectedBucketId: string;
  pullRequestSnapshotStatus: string | null;
  pullRequestSnapshotError: string | null;
  pullRequestLoadPerfStats: ReviewLoadPerfStats | null;
  lastUpdatedAt: string | null;
  autoRefreshIntervalMs: number;
  login?: string;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onRefresh: () => void;
  onShipWeekRepoChange: (value: string) => void;
  onShipWeekMilestoneChange: (value: string) => void;
  onShipWeekReleaseBranchChange: (value: string) => void;
  onShipWeekSubmit: FormEventHandler<HTMLFormElement>;
  onCopyShipWeekShareLink: () => void;
  onCopyShipWeekSnapshotImage: () => void;
  onDownloadShipWeekSnapshot: () => void;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: VisiblePullRequestHandler;
};

function DashboardView({
  dashboardMode,
  repo,
  state,
  pullsLoading,
  pullRequests,
  error,
  developerPullRequestCounts,
  attentionBuckets,
  forMeItems,
  issues,
  issueBuckets,
  issuesLoading,
  issuesError,
  shipWeek,
  shipWeekLoading,
  shipWeekSectionLoading,
  shipWeekError,
  shipWeekRepo,
  shipWeekMilestone,
  shipWeekReleaseBranch,
  shipWeekSnapshotStatus,
  shipWeekSnapshotError,
  shipWeekSnapshotExporting,
  shipWeekSnapshotCopying,
  showShipWeekSnapshotDownload,
  shipWeekSnapshotRef,
  selectedBucketId,
  pullRequestSnapshotStatus,
  pullRequestSnapshotError,
  pullRequestLoadPerfStats,
  lastUpdatedAt,
  autoRefreshIntervalMs,
  login,
  onRepoChange,
  onStateChange,
  onSubmit,
  onRefresh,
  onShipWeekRepoChange,
  onShipWeekMilestoneChange,
  onShipWeekReleaseBranchChange,
  onShipWeekSubmit,
  onCopyShipWeekShareLink,
  onCopyShipWeekSnapshotImage,
  onDownloadShipWeekSnapshot,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
}: DashboardViewProps) {
  const shipModeActive = dashboardMode === 'ship';
  const issuesModeActive = dashboardMode === 'issues';
  const refreshing = shipModeActive ? shipWeekLoading : issuesModeActive ? issuesLoading : pullsLoading;
  const hasLoadedData = lastUpdatedAt !== null
    || (!shipModeActive && !issuesModeActive && pullRequests.length > 0)
    || (issuesModeActive && issues.length > 0)
    || (shipModeActive && shipWeek !== null);
  const visibleRefreshLoading = refreshing && !hasLoadedData;
  const refreshButtonLoading = refreshing;
  const visiblePullsLoading = pullsLoading && !hasLoadedData;
  const visibleIssuesLoading = issuesLoading && !hasLoadedData;
  const displayedLastUpdatedAt = lastUpdatedAt
    ?? (!shipModeActive && !issuesModeActive && pullRequests.length > 0
      ? getLatestFetchedAt(pullRequests)
      : null);
  const autoRefreshCadence = formatDuration(autoRefreshIntervalMs);
  const dataTitle = shipModeActive
    ? 'Ship mode data'
    : issuesModeActive
      ? 'Issue focus data'
      : 'Review queue data';
  const refreshStatus = !shipModeActive && !issuesModeActive ? pullRequestSnapshotStatus : null;
  const showQueuePanel = shipModeActive
    || (issuesModeActive
      ? issuesLoading || issues.length > 0 || issueBuckets.length > 0
      : pullsLoading || pullRequests.length > 0 || attentionBuckets.length > 0);
  const queuePanelLabel = issuesModeActive ? 'Issue focus' : 'Review queue';

  return (
    <>
      <section className="panel dashboard-refresh-panel" aria-label="Dashboard refresh status">
        <div className="dashboard-refresh-copy">
          <p className="eyebrow">Refresh</p>
          <h2>{dataTitle}</h2>
          <p>
            {displayedLastUpdatedAt ? (
              <>
                List updated {formatRelative(displayedLastUpdatedAt)} at {formatTime(displayedLastUpdatedAt)}.
                {pullRequestLoadPerfStats && (
                  <span className="load-perf-stats"> {formatLoadPerfStats(pullRequestLoadPerfStats)}</span>
                )}
              </>
            ) : 'List has not loaded yet.'}
            {' '}
            Auto-refreshes about every {autoRefreshCadence} using cached data.
          </p>
          {refreshStatus && (
            <p className="dashboard-refresh-status" role="status">
              {refreshStatus}
            </p>
          )}
          {pullRequestSnapshotError && <p className="error">{pullRequestSnapshotError}</p>}
        </div>
        <button type="button" onClick={onRefresh} disabled={refreshButtonLoading}>
          {refreshButtonLoading ? (hasLoadedData ? 'Refreshing...' : 'Loading...') : 'Refresh now'}
        </button>
      </section>

      {showQueuePanel && (
        <section className="panel queue-panel" aria-label={queuePanelLabel}>
          {shipModeActive ? (
            <ShipWeekSection
              shipWeek={shipWeek}
              loading={shipWeekLoading}
              hasLoaded={hasLoadedData}
              sectionLoading={shipWeekSectionLoading}
              error={shipWeekError}
              snapshotStatus={shipWeekSnapshotStatus}
              snapshotError={shipWeekSnapshotError}
              snapshotExporting={shipWeekSnapshotExporting}
              snapshotCopying={shipWeekSnapshotCopying}
              showDownloadSnapshot={showShipWeekSnapshotDownload}
              snapshotRef={shipWeekSnapshotRef}
              onCopyShareLink={onCopyShipWeekShareLink}
              onCopySnapshotImage={onCopyShipWeekSnapshotImage}
              onDownloadSnapshot={onDownloadShipWeekSnapshot}
              onSelectPullRequest={onSelectPullRequest}
              onVisiblePullRequest={onVisiblePullRequest}
              login={login}
            />
          ) : issuesModeActive ? (
            <IssuesOverview
              issueBuckets={issueBuckets}
              loading={visibleIssuesLoading}
              hasLoaded={hasLoadedData}
              selectedBucketId={selectedBucketId}
              onSelectBucket={onSelectBucket}
              login={login}
            />
          ) : (pullsLoading || pullRequests.length > 0) && (
            <QueueOverview
              counts={developerPullRequestCounts}
              attentionBuckets={attentionBuckets}
              forMeItems={forMeItems}
              loading={visiblePullsLoading}
              hasLoaded={hasLoadedData}
              selectedBucketId={selectedBucketId}
              login={login}
              onSelectBucket={onSelectBucket}
              onSelectPullRequest={onSelectPullRequest}
              onVisiblePullRequest={onVisiblePullRequest}
            />
          )}
        </section>
      )}

      <DashboardFilters
        dashboardMode={dashboardMode}
        repo={repo}
        state={state}
        pullsLoading={visiblePullsLoading}
        pullRequests={pullRequests}
        error={error}
        issuesLoading={visibleIssuesLoading}
        issues={issues}
        issuesError={issuesError}
        shipWeekRepo={shipWeekRepo}
        shipWeekMilestone={shipWeekMilestone}
        shipWeekReleaseBranch={shipWeekReleaseBranch}
        shipWeekLoading={shipWeekLoading}
        onRepoChange={onRepoChange}
        onStateChange={onStateChange}
        onSubmit={onSubmit}
        onShipWeekRepoChange={onShipWeekRepoChange}
        onShipWeekMilestoneChange={onShipWeekMilestoneChange}
        onShipWeekReleaseBranchChange={onShipWeekReleaseBranchChange}
        onShipWeekSubmit={onShipWeekSubmit}
      />
    </>
  );
}

function formatLoadPerfStats(stats: ReviewLoadPerfStats) {
  const requestLabel = `${stats.requestCount} GraphQL ${stats.requestCount === 1 ? 'request' : 'requests'}`;
  if (stats.settledMs === null) {
    return `Shown in ${formatLoadDuration(stats.firstRowsMs)}; still refreshing (${requestLabel}).`;
  }

  if (stats.settledMs <= stats.firstRowsMs + 50) {
    return `Loaded in ${formatLoadDuration(stats.settledMs)} (${requestLabel}).`;
  }

  return `Shown in ${formatLoadDuration(stats.firstRowsMs)}; settled in ${formatLoadDuration(stats.settledMs)} (${requestLabel}).`;
}

function formatLoadDuration(durationMs: number) {
  if (durationMs < 1000) {
    return `${Math.max(0, Math.round(durationMs))}ms`;
  }

  return `${(durationMs / 1000).toFixed(durationMs < 10_000 ? 1 : 0)}s`;
}

export default DashboardView;

function getLatestFetchedAt(pullRequests: PullRequestSummary[]) {
  return pullRequests
    .map((pullRequest) => pullRequest.fetchedAt)
    .filter(Boolean)
    .sort((first, second) => new Date(second).getTime() - new Date(first).getTime())[0] ?? null;
}
