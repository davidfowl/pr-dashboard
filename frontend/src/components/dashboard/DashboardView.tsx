import type { FormEventHandler } from 'react';
import type {
  AttentionBucket,
  DeveloperPullRequestCount,
  PickItem,
  PullRequestSummary,
  PullState,
  TeamMetrics,
} from '../../types';
import DashboardFilters from './DashboardFilters';
import ForMePanel from './ForMePanel';
import QueueOverview from './QueueOverview';
import TeamMetricsStrip from './TeamMetricsStrip';

type DashboardViewProps = {
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  teamMetrics: TeamMetrics;
  developerPullRequestCounts: DeveloperPullRequestCount[];
  automationPullRequests: PullRequestSummary[];
  communityPullRequests: PullRequestSummary[];
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
  teamMetrics,
  developerPullRequestCounts,
  automationPullRequests,
  communityPullRequests,
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
          {pullRequests.length > 0 && <TeamMetricsStrip metrics={teamMetrics} />}

          {forMeItems.length > 0 && (
            <ForMePanel
              items={forMeItems}
              login={login}
              onSelectPullRequest={onSelectPullRequest}
            />
          )}

          {pullRequests.length > 0 && (
            <QueueOverview
              counts={developerPullRequestCounts}
              automationPullRequests={automationPullRequests}
              communityPullRequests={communityPullRequests}
              attentionBuckets={attentionBuckets}
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
