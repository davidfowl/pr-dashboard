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
  login?: string;
  onRepoChange: (value: string) => void;
  onStateChange: (value: PullState) => void;
  onSubmit: FormEventHandler<HTMLFormElement>;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
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
  login,
  onRepoChange,
  onStateChange,
  onSubmit,
  onSelectPullRequest,
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
              login={login}
              onSelectPullRequest={onSelectPullRequest}
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
