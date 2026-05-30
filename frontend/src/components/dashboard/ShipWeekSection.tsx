import { useMemo } from 'react';
import type {
  AttentionSignal,
  PullRequestSummary,
  ShipWeekIssueSummary,
  ShipWeekLoadingState,
  ShipWeekPullRequestSummary,
  ShipWeekResponse,
} from '../../types';
import { formatCount } from '../../utils/format';
import IssueListItem from '../IssueListItem';
import LoadingBadge from '../LoadingBadge';
import PullRequestList from '../PullRequestList';

type ShipWeekSectionProps = {
  shipWeek: ShipWeekResponse | null;
  loading: boolean;
  sectionLoading: ShipWeekLoadingState;
  error: string | null;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type ShipModePullRequestItem = {
  item: ShipWeekPullRequestSummary;
  action: string;
  tone: AttentionSignal['tone'];
};

function ShipWeekSection({
  shipWeek,
  loading,
  sectionLoading,
  error,
  onSelectPullRequest,
  onVisiblePullRequest,
}: ShipWeekSectionProps) {
  const model = useMemo(() => shipWeek ? createShipModeModel(shipWeek) : null, [shipWeek]);

  if (!shipWeek && !loading && !error) {
    return null;
  }

  return (
    <section className="ship-week-section" aria-label="Ship mode milestone tracking">
      <div className="section-title-row">
        <p className="eyebrow">Ship mode</p>
        <h3>{shipWeek ? `${shipWeek.milestone} release scope` : 'Release scope'}</h3>
        <p className="board-guidance">
          Three PR lists matter: work in the milestone, work targeting the base branch, and generated docs updates. Open milestone issues are tracked separately as TBD.
        </p>
      </div>

      {error && (
        <div className="error" role="alert">
          {error}
        </div>
      )}
      {loading && !shipWeek && <p className="empty-for-me">Loading ship mode sections...</p>}

      {shipWeek && model && (
        <>
          <div className="ship-week-summary-grid" aria-label="Ship mode summary">
            <SummaryCard
              label="TBD issues"
              value={shipWeek.issues.length}
              detail={shipWeek.issues.length === 0 ? 'complete' : 'need PR/backport'}
              tone={shipWeek.issues.length > 0 ? 'danger' : 'success'}
              loading={sectionLoading.issues}
            />
            <SummaryCard
              label="Needs work"
              value={model.needsWorkCount}
              detail="CI or author response"
              tone={model.needsWorkCount > 0 ? 'danger' : 'success'}
              loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
            />
            <SummaryCard
              label="Pending review"
              value={model.pendingReviewCount}
              detail="needs review or re-review"
              tone={model.pendingReviewCount > 0 ? 'warning' : 'success'}
              loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
            />
            <SummaryCard
              label="Ready"
              value={model.readyCount}
              detail="approved and unblocked"
              tone="success"
              loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
            />
            <SummaryCard
              label="Docs"
              value={model.docsFromCodePullRequests.length}
              detail="generated PRs"
              tone={model.docsFromCodePullRequests.length > 0 ? 'accent' : 'success'}
              loading={sectionLoading.docs}
            />
          </div>

          <div className="ship-week-critical-grid">
            <section className="ship-week-critical-panel accent" aria-label={`Pull requests in ${shipWeek.milestone}`}>
              <div className="attention-card-header">
                <span>PRs in {shipWeek.milestone}</span>
                <div className="section-loading-meta">
                  {sectionLoading.milestone && <SectionLoadingIndicator />}
                  <strong>{formatCount(model.milestonePullRequests.length, 'PR')}</strong>
                </div>
              </div>
              <ShipModePullRequestList
                items={model.milestonePullRequests}
                emptyState={sectionLoading.milestone ? 'Loading milestone PRs...' : 'No open PRs are in or linked to this milestone.'}
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
              />
            </section>

            <section className="ship-week-critical-panel warning" aria-label={`Pull requests targeting ${shipWeek.releaseBranch}`}>
              <div className="attention-card-header">
                <span>PRs targeting {shipWeek.releaseBranch}</span>
                <div className="section-loading-meta">
                  {sectionLoading.baseBranch && <SectionLoadingIndicator />}
                  <strong>{formatCount(model.baseBranchPullRequests.length, 'PR')}</strong>
                </div>
              </div>
              <ShipModePullRequestList
                items={model.baseBranchPullRequests}
                emptyState={sectionLoading.baseBranch ? 'Loading base-branch PRs...' : 'No open PRs target the selected base branch.'}
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
              />
            </section>

            {(sectionLoading.docs || model.docsFromCodePullRequests.length > 0) && (
              <section className="ship-week-critical-panel accent" aria-label="Generated docs pull requests">
                <div className="attention-card-header">
                  <span>Generated docs PRs</span>
                  <div className="section-loading-meta">
                    {sectionLoading.docs && <SectionLoadingIndicator />}
                    <strong>{formatCount(model.docsFromCodePullRequests.length, 'PR')}</strong>
                  </div>
                </div>
                <ShipModePullRequestList
                  items={model.docsFromCodePullRequests}
                  emptyState={sectionLoading.docs ? 'Loading generated docs PRs...' : 'No open generated docs PRs are loaded.'}
                  onSelectPullRequest={onSelectPullRequest}
                  onVisiblePullRequest={onVisiblePullRequest}
                />
              </section>
            )}
          </div>

          <section className="ship-week-critical-panel danger" aria-label="Open milestone issues TBD">
            <div className="attention-card-header">
              <span>Open issues TBD</span>
              <div className="section-loading-meta">
                {sectionLoading.issues && <SectionLoadingIndicator />}
                <strong>{formatCount(shipWeek.issues.length, 'issue')}</strong>
              </div>
            </div>
            <p className="board-guidance">These are open milestone issues that are not PRs yet. Treat as TBD until there is a PR/backport path.</p>
            <ShipWeekIssueList issues={shipWeek.issues} emptyState={sectionLoading.issues ? 'Loading milestone issues...' : 'No open non-PR issues in this milestone.'} />
          </section>
        </>
      )}
    </section>
  );
}

function SectionLoadingIndicator() {
  return <LoadingBadge ariaLabel="Still loading" />;
}

function createShipModeModel(shipWeek: ShipWeekResponse) {
  const milestonePullRequests = shipWeek.pullRequests
    .filter((item) => item.releaseScope.inMilestone && !item.pullRequest.draft)
    .map(createShipModePullRequestItem);
  const baseBranchPullRequests = shipWeek.pullRequests
    .filter((item) => item.releaseScope.targetsReleaseBranch && !item.pullRequest.draft)
    .map(createShipModePullRequestItem);
  const docsFromCodePullRequests = shipWeek.pullRequests
    .filter((item) => item.releaseScope.docsFromCode && !item.pullRequest.draft)
    .map(createShipModePullRequestItem);
  const actionablePullRequests = [
    ...milestonePullRequests,
    ...baseBranchPullRequests,
    ...docsFromCodePullRequests,
  ];

  return {
    milestonePullRequests,
    baseBranchPullRequests,
    docsFromCodePullRequests,
    needsWorkCount: countUniqueItems(actionablePullRequests, (item) =>
      item.action === 'CI failing' || item.action === 'Author response'),
    pendingReviewCount: countUniqueItems(actionablePullRequests, (item) =>
      item.action === 'Pending review' || item.action === 'Needs re-review'),
    readyCount: countUniqueItems(actionablePullRequests, (item) =>
      item.action === 'Ready to land'),
  };
}

function createShipModePullRequestItem(item: ShipWeekPullRequestSummary): ShipModePullRequestItem {
  const pullRequest = item.pullRequest;
  if (pullRequest.checks?.state === 'failure') {
    return { item, action: 'CI failing', tone: 'danger' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return { item, action: 'Author response', tone: 'danger' };
  }

  if (isReadyToLand(pullRequest)) {
    return { item, action: 'Ready to land', tone: 'success' };
  }

  if (needsReReview(pullRequest)) {
    return { item, action: 'Needs re-review', tone: 'warning' };
  }

  if (pullRequest.review.state === 'waiting') {
    return { item, action: 'Pending review', tone: 'warning' };
  }

  return { item, action: 'Review started', tone: 'accent' };
}

function ShipModePullRequestList({
  items,
  emptyState,
  onSelectPullRequest,
  onVisiblePullRequest,
}: {
  items: ShipModePullRequestItem[];
  emptyState: string;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
}) {
  return (
    <PullRequestList
      entries={items.map((item) => ({
        pullRequest: item.item.pullRequest,
        bucketLabel: item.action,
        signalProps: {
          leadingSignals: shipModeSignals(item),
          computedSignalLimit: 3,
        },
        linkedIssues: item.item.pullRequest.linkedIssues,
      }))}
      emptyState={emptyState}
      onSelectPullRequest={onSelectPullRequest}
      onVisiblePullRequest={onVisiblePullRequest}
    />
  );
}

function ShipWeekIssueList({ issues, emptyState }: { issues: ShipWeekIssueSummary[]; emptyState: string }) {
  return (
    <div className="attention-list">
      {issues.length === 0 && <p className="empty-for-me">{emptyState}</p>}
      {issues.map((issue) => (
        <IssueListItem
          key={`${issue.repository}#${issue.number}`}
          issue={issue}
          signalProps={{ computedSignalLimit: 5 }}
        />
      ))}
    </div>
  );
}

function shipModeSignals(item: ShipModePullRequestItem): AttentionSignal[] {
  const signals: AttentionSignal[] = [{ label: item.action, tone: item.tone }];
  if (item.item.releaseScope.targetsReleaseBranch) {
    signals.push({ label: `base ${item.item.pullRequest.baseRef}`, tone: 'warning' });
  }
  if (item.item.releaseScope.inMilestone) {
    signals.push({ label: 'milestone', tone: 'accent' });
  }
  return signals;
}

function SummaryCard({
  label,
  value,
  detail,
  tone = 'accent',
  loading = false,
}: {
  label: string;
  value: number | string;
  detail: string;
  tone?: 'accent' | 'warning' | 'danger' | 'success';
  loading?: boolean;
}) {
  return (
    <article className={`ship-week-summary-card ${tone}`}>
      <div className="ship-week-summary-card-header">
        <span className="ship-week-summary-card-label">{label}</span>
        {loading && <SectionLoadingIndicator />}
      </div>
      <strong className="ship-week-summary-card-value">{value}</strong>
      <p>{detail}</p>
    </article>
  );
}

function countUniqueItems(items: ShipModePullRequestItem[], predicate: (item: ShipModePullRequestItem) => boolean) {
  return new Set(items.filter(predicate).map((item) => pullRequestKey(item.item.pullRequest))).size;
}

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository.toLowerCase()}#${pullRequest.number}`;
}

function needsReReview(pullRequest: PullRequestSummary) {
  return pullRequest.review.lastReviewedAt != null
    && pullRequest.lastCommitAt != null
    && (pullRequest.review.state === 'reviewed' || pullRequest.review.state === 'changes_requested')
    && new Date(pullRequest.lastCommitAt).getTime() > new Date(pullRequest.review.lastReviewedAt).getTime();
}

function isReadyToLand(pullRequest: PullRequestSummary) {
  return pullRequest.review.state === 'approved'
    && pullRequest.checks?.state !== 'failure'
    && pullRequest.checks?.state !== 'pending';
}

export default ShipWeekSection;
