import type { FormEventHandler, RefObject } from 'react';
import type {
  AttentionBucket,
  AttentionIssueBucket,
  DashboardMode,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  PullState,
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
  visibleChecksRefreshKey: number;
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
  visibleChecksRefreshKey,
}: DashboardViewProps) {
  const shipModeActive = dashboardMode === 'ship';
  const issuesModeActive = dashboardMode === 'issues';
  const refreshing = shipModeActive ? shipWeekLoading : issuesModeActive ? issuesLoading : pullsLoading;
  const hasLoadedData = lastUpdatedAt !== null;
  const autoRefreshCadence = formatDuration(autoRefreshIntervalMs);
  const dataTitle = shipModeActive
    ? 'Ship mode data'
    : issuesModeActive
      ? 'Issue focus data'
      : 'Review queue data';
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
            {lastUpdatedAt
              ? `List updated ${formatRelative(lastUpdatedAt)} at ${formatTime(lastUpdatedAt)}.`
              : 'List has not loaded yet.'}
            {' '}
            Auto-refreshes about every {autoRefreshCadence} using cached data.
          </p>
        </div>
        <button type="button" onClick={onRefresh} disabled={refreshing}>
          {refreshing ? (hasLoadedData ? 'Refreshing...' : 'Loading...') : 'Refresh now'}
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
              visibleChecksRefreshKey={visibleChecksRefreshKey}
            />
          ) : issuesModeActive ? (
            <IssuesOverview
              issueBuckets={issueBuckets}
              loading={issuesLoading}
              hasLoaded={hasLoadedData}
              selectedBucketId={selectedBucketId}
              onSelectBucket={onSelectBucket}
            />
          ) : (pullsLoading || pullRequests.length > 0) && (
            <QueueOverview
              counts={developerPullRequestCounts}
              attentionBuckets={attentionBuckets}
              forMeItems={forMeItems}
              loading={pullsLoading}
              hasLoaded={hasLoadedData}
              selectedBucketId={selectedBucketId}
              login={login}
              onSelectBucket={onSelectBucket}
              onSelectPullRequest={onSelectPullRequest}
              onVisiblePullRequest={onVisiblePullRequest}
              visibleChecksRefreshKey={visibleChecksRefreshKey}
            />
          )}
        </section>
      )}

      <DashboardFilters
        dashboardMode={dashboardMode}
        repo={repo}
        state={state}
        pullsLoading={pullsLoading}
        pullRequests={pullRequests}
        error={error}
        issuesLoading={issuesLoading}
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

export default DashboardView;
