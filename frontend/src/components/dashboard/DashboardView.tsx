import type { FormEventHandler } from 'react';
import type {
  AttentionBucket,
  PickItem,
  PullRequestSummary,
  PullState,
  TeamMetrics,
} from '../../types';
import AttentionBoard from './AttentionBoard';
import DashboardFilters from './DashboardFilters';
import ForMePanel from './ForMePanel';
import TeamMetricsStrip from './TeamMetricsStrip';

type DashboardViewProps = {
  repo: string;
  state: PullState;
  pullsLoading: boolean;
  pullRequests: PullRequestSummary[];
  error: string | null;
  teamMetrics: TeamMetrics;
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

          {pullRequests.length > 0 && (
            <ForMePanel
              items={forMeItems}
              login={login}
              onSelectPullRequest={onSelectPullRequest}
            />
          )}

          {attentionBuckets.length > 0 && (
            <AttentionBoard
              buckets={attentionBuckets}
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
