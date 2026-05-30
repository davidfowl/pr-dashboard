import type { FormEventHandler } from 'react';
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
  selectedBucketId: string;
  login?: string;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onRefresh: () => void;
  onShipWeekRepoChange: (value: string) => void;
  onShipWeekMilestoneChange: (value: string) => void;
  onShipWeekReleaseBranchChange: (value: string) => void;
  onShipWeekSubmit: FormEventHandler<HTMLFormElement>;
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
  selectedBucketId,
  login,
  onRepoChange,
  onStateChange,
  onSubmit,
  onRefresh,
  onShipWeekRepoChange,
  onShipWeekMilestoneChange,
  onShipWeekReleaseBranchChange,
  onShipWeekSubmit,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
}: DashboardViewProps) {
  const shipModeActive = dashboardMode === 'ship';

  return (
    <>
      {(shipModeActive || pullsLoading || pullRequests.length > 0 || attentionBuckets.length > 0 || regressionIssueBuckets.length > 0) && (
        <section className="panel queue-panel" aria-label="Review queue">
          {shipModeActive ? (
            <ShipWeekSection
              shipWeek={shipWeek}
              loading={shipWeekLoading}
              sectionLoading={shipWeekSectionLoading}
              error={shipWeekError}
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
        onRefresh={onRefresh}
        onShipWeekRepoChange={onShipWeekRepoChange}
        onShipWeekMilestoneChange={onShipWeekMilestoneChange}
        onShipWeekReleaseBranchChange={onShipWeekReleaseBranchChange}
        onShipWeekSubmit={onShipWeekSubmit}
      />
    </>
  );
}

export default DashboardView;
