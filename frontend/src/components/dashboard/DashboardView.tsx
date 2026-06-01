import type { FormEventHandler, RefObject } from 'react';
import type {
  AttentionBucket,
  AttentionIssueBucket,
  DashboardMode,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  PullState,
  ShipWeekLoadingState,
  ShipWeekResponse,
} from '../../types';
import { formatDuration, formatRelative, formatTime } from '../../utils/format';
import DashboardFilters from './DashboardFilters';
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
  regressionIssueBuckets: AttentionIssueBucket[];
  forMeItems: PickItem[];
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
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
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
  regressionIssueBuckets,
  forMeItems,
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
}: DashboardViewProps) {
  const shipModeActive = dashboardMode === 'ship';
  const refreshing = shipModeActive ? shipWeekLoading : pullsLoading;
  const autoRefreshCadence = formatDuration(autoRefreshIntervalMs);

  return (
    <>
      <section className="panel dashboard-refresh-panel" aria-label="Dashboard refresh status">
        <div className="dashboard-refresh-copy">
          <p className="eyebrow">Refresh</p>
          <h2>{shipModeActive ? 'Ship mode data' : 'Review queue data'}</h2>
          <p>
            {lastUpdatedAt
              ? `List updated ${formatRelative(lastUpdatedAt)} at ${formatTime(lastUpdatedAt)}.`
              : 'List has not loaded yet.'}
            {' '}
            Auto-refreshes about every {autoRefreshCadence} using cached data.
          </p>
        </div>
        <button type="button" onClick={onRefresh} disabled={refreshing}>
          {refreshing ? 'Refreshing...' : 'Refresh now'}
        </button>
      </section>

      {(shipModeActive || pullsLoading || pullRequests.length > 0 || attentionBuckets.length > 0 || regressionIssueBuckets.length > 0) && (
        <section className="panel queue-panel" aria-label="Review queue">
          {shipModeActive ? (
            <ShipWeekSection
              shipWeek={shipWeek}
              loading={shipWeekLoading}
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
            />
          ) : (pullRequests.length > 0 || regressionIssueBuckets.length > 0) && (
            <QueueOverview
              counts={developerPullRequestCounts}
              attentionBuckets={attentionBuckets}
              regressionIssueBuckets={regressionIssueBuckets}
              forMeItems={forMeItems}
              loading={pullsLoading}
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
        pullsLoading={pullsLoading}
        pullRequests={pullRequests}
        error={error}
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
