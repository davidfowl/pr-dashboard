import type { FormEventHandler } from 'react';
import type {
  AttentionBucket,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  PullState,
} from '../../types';
import DashboardFilters from './DashboardFilters';
import QueueOverview from './QueueOverview';

type DashboardViewProps = {
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  developerPullRequestCounts: DeveloperPullRequestCount[];
  attentionBuckets: AttentionBucket[];
  forMeItems: PickItem[];
  selectedBucketId: string;
  login?: string;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onSelectBucket: (bucketId: string) => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

function DashboardView({
  repo,
  state,
  pullsLoading,
  pullRequests,
  error,
  developerPullRequestCounts,
  attentionBuckets,
  forMeItems,
  selectedBucketId,
  login,
  onRepoChange,
  onStateChange,
  onSubmit,
  onSelectBucket,
  onSelectPullRequest,
  onVisiblePullRequest,
}: DashboardViewProps) {
  return (
    <>
      {(pullRequests.length > 0 || attentionBuckets.length > 0) && (
        <section className="panel queue-panel" aria-label="Review queue">
          {pullRequests.length > 0 && (
            <QueueOverview
              counts={developerPullRequestCounts}
              attentionBuckets={attentionBuckets}
              forMeItems={forMeItems}
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
        repo={repo}
        state={state}
        pullsLoading={pullsLoading}
        pullRequests={pullRequests}
        error={error}
        onRepoChange={onRepoChange}
        onStateChange={onStateChange}
        onSubmit={onSubmit}
      />
    </>
  );
}

export default DashboardView;
