import { useMemo } from 'react';
import type { RefObject } from 'react';
import type {
  AttentionSignal,
  PullRequestSummary,
  ShipWeekIssueSummary,
  ShipWeekLoadingState,
  ShipWeekPullRequestSummary,
  ShipWeekResponse,
  VisiblePullRequestHandler,
} from '../../types';
import { formatCount } from '../../utils/format';
import { shouldHideFromSharedPullRequestLists } from '../../utils/models';
import IssueListItem from '../IssueListItem';
import LoadingBadge from '../LoadingBadge';
import LoadingCardPlaceholders from '../LoadingCardPlaceholders';
import LoadingMetric from '../LoadingMetric';
import PullRequestList from '../PullRequestList';

type ShipWeekSectionProps = {
  shipWeek: ShipWeekResponse | null;
  loading: boolean;
  hasLoaded: boolean;
  sectionLoading: ShipWeekLoadingState;
  error: string | null;
  snapshotStatus: string | null;
  snapshotError: string | null;
  snapshotExporting: boolean;
  snapshotCopying: boolean;
  showDownloadSnapshot: boolean;
  snapshotRef: RefObject<HTMLElement | null>;
  onCopyShareLink: () => void;
  onCopySnapshotImage: () => void;
  onDownloadSnapshot: () => void;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: VisiblePullRequestHandler;
  visibleChecksRefreshKey: number;
};

type ShipModePullRequestItem = {
  item: ShipWeekPullRequestSummary;
  action: string;
  tone: AttentionSignal['tone'];
};

function ShipWeekSection({
  shipWeek,
  loading,
  hasLoaded,
  sectionLoading,
  error,
  snapshotStatus,
  snapshotError,
  snapshotExporting,
  snapshotCopying,
  showDownloadSnapshot,
  snapshotRef,
  onCopyShareLink,
  onCopySnapshotImage,
  onDownloadSnapshot,
  onSelectPullRequest,
  onVisiblePullRequest,
  visibleChecksRefreshKey,
}: ShipWeekSectionProps) {
  const model = useMemo(() => shipWeek ? createShipModeModel(shipWeek) : null, [shipWeek]);
  const snapshotProgress = snapshotCopying ? 'Copying PNG...' : snapshotExporting ? 'Exporting PNG...' : '';
  const snapshotMessage = snapshotError ?? snapshotStatus ?? snapshotProgress;

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
      {loading && !shipWeek && <p className="empty-for-me">Preparing ship mode sections...</p>}
      {loading && (
        <ShipWeekLoadingProgress
          sectionLoading={sectionLoading}
          hasLoaded={hasLoaded}
        />
      )}

      {shipWeek && model && (
        <>
          <article ref={snapshotRef} className="ship-week-snapshot-card" aria-label="Shareable ship mode snapshot">
            <div className="ship-week-snapshot-heading-row">
              <div className="ship-week-snapshot-heading">
                <p className="eyebrow">Ship snapshot</p>
                <h4>{shipWeek.milestone} release scope</h4>
                <p>
                  {shipWeek.repository}
                  {shipWeek.releaseBranch ? ` · ${shipWeek.releaseBranch}` : ''}
                </p>
              </div>
              <div className="ship-week-snapshot-toolbar" data-snapshot-export="exclude">
                <div className="ship-week-snapshot-actions">
                  <button type="button" onClick={onCopyShareLink}>
                    Copy link
                  </button>
                  <button type="button" onClick={onCopySnapshotImage} disabled={loading || snapshotCopying || snapshotExporting}>
                    Copy PNG
                  </button>
                  {showDownloadSnapshot && (
                    <button type="button" onClick={onDownloadSnapshot} disabled={loading || snapshotCopying || snapshotExporting}>
                      Download PNG
                    </button>
                  )}
                </div>
                <p
                  className={`ship-week-snapshot-status${snapshotError ? ' error-text' : ''}`}
                  role={snapshotError ? 'alert' : snapshotMessage ? 'status' : undefined}
                  aria-hidden={snapshotMessage ? undefined : true}
                >
                  {snapshotMessage}
                </p>
              </div>
            </div>
            <div className="ship-week-summary-grid" aria-label="Ship mode summary">
              <SummaryCard
                label="TBD issues"
                value={shipWeek.issues.length}
                detail={shipWeek.issues.length === 0 ? 'complete' : 'need PR/backport'}
                tone={shipWeek.issues.length > 0 ? 'danger' : 'success'}
                loading={sectionLoading.issues}
                hasLoaded={hasLoaded}
              />
              <SummaryCard
                label="Needs work"
                value={model.needsWorkCount}
                detail="CI or author response"
                tone={model.needsWorkCount > 0 ? 'danger' : 'success'}
                loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
                hasLoaded={hasLoaded}
              />
              <SummaryCard
                label="Pending review"
                value={model.pendingReviewCount}
                detail="needs review or re-review"
                tone={model.pendingReviewCount > 0 ? 'warning' : 'success'}
                loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
                hasLoaded={hasLoaded}
              />
              <SummaryCard
                label="Ready"
                value={model.readyCount}
                detail="approved and unblocked"
                tone="success"
                loading={sectionLoading.milestone || sectionLoading.baseBranch || sectionLoading.docs}
                hasLoaded={hasLoaded}
              />
              <SummaryCard
                label="Docs"
                value={model.docsFromCodePullRequests.length}
                detail="generated PRs"
                tone={model.docsFromCodePullRequests.length > 0 ? 'accent' : 'success'}
                loading={sectionLoading.docs}
                hasLoaded={hasLoaded}
              />
            </div>
          </article>

          <div className="ship-week-critical-grid">
            <section className="ship-week-critical-panel accent" aria-label={`Pull requests in ${shipWeek.milestone}`}>
              <div className="attention-card-header">
                <span>PRs in {shipWeek.milestone}</span>
                <div className="section-loading-meta">
                  {sectionLoading.milestone && <SectionLoadingIndicator hasLoaded={hasLoaded} />}
                  <LoadingMetric
                    value={model.milestonePullRequests.length}
                    loading={sectionLoading.milestone}
                    hasLoaded={hasLoaded}
                    formatValue={(count) => formatCount(count, 'PR')}
                    pendingLabel="Milestone PR count is loading"
                  />
                </div>
              </div>
              <ShipModePullRequestList
                items={model.milestonePullRequests}
                loading={sectionLoading.milestone}
                hasLoaded={hasLoaded}
                emptyState={sectionLoading.milestone ? 'Loading milestone PRs...' : 'No open PRs are in or linked to this milestone.'}
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
                visibleChecksRefreshKey={visibleChecksRefreshKey}
              />
            </section>

            <section className="ship-week-critical-panel warning" aria-label={`Pull requests targeting ${shipWeek.releaseBranch}`}>
              <div className="attention-card-header">
                <span>PRs targeting {shipWeek.releaseBranch}</span>
                <div className="section-loading-meta">
                  {sectionLoading.baseBranch && <SectionLoadingIndicator hasLoaded={hasLoaded} />}
                  <LoadingMetric
                    value={model.baseBranchPullRequests.length}
                    loading={sectionLoading.baseBranch}
                    hasLoaded={hasLoaded}
                    formatValue={(count) => formatCount(count, 'PR')}
                    pendingLabel="Base-branch PR count is loading"
                  />
                </div>
              </div>
              <ShipModePullRequestList
                items={model.baseBranchPullRequests}
                loading={sectionLoading.baseBranch}
                hasLoaded={hasLoaded}
                emptyState={sectionLoading.baseBranch ? 'Loading base-branch PRs...' : 'No open PRs target the selected base branch.'}
                onSelectPullRequest={onSelectPullRequest}
                onVisiblePullRequest={onVisiblePullRequest}
                visibleChecksRefreshKey={visibleChecksRefreshKey}
              />
            </section>

            {(sectionLoading.docs || model.docsFromCodePullRequests.length > 0) && (
              <section className="ship-week-critical-panel accent" aria-label="Generated docs pull requests">
                <div className="attention-card-header">
                  <span>Generated docs PRs</span>
                  <div className="section-loading-meta">
                    {sectionLoading.docs && <SectionLoadingIndicator hasLoaded={hasLoaded} />}
                    <LoadingMetric
                      value={model.docsFromCodePullRequests.length}
                      loading={sectionLoading.docs}
                      hasLoaded={hasLoaded}
                      formatValue={(count) => formatCount(count, 'PR')}
                      pendingLabel="Generated docs PR count is loading"
                    />
                  </div>
                </div>
                <ShipModePullRequestList
                  items={model.docsFromCodePullRequests}
                  loading={sectionLoading.docs}
                  hasLoaded={hasLoaded}
                  emptyState={sectionLoading.docs ? 'Loading generated docs PRs...' : 'No open generated docs PRs are loaded.'}
                  onSelectPullRequest={onSelectPullRequest}
                  onVisiblePullRequest={onVisiblePullRequest}
                  visibleChecksRefreshKey={visibleChecksRefreshKey}
                />
              </section>
            )}
          </div>

          <section className="ship-week-critical-panel danger" aria-label="Open milestone issues TBD">
            <div className="attention-card-header">
              <span>Open issues TBD</span>
              <div className="section-loading-meta">
                {sectionLoading.issues && <SectionLoadingIndicator hasLoaded={hasLoaded} />}
                <LoadingMetric
                  value={shipWeek.issues.length}
                  loading={sectionLoading.issues}
                  hasLoaded={hasLoaded}
                  formatValue={(count) => formatCount(count, 'issue')}
                  pendingLabel="Open issue count is loading"
                />
              </div>
            </div>
            <p className="board-guidance">These are open milestone issues that are not PRs yet. Treat as TBD until there is a PR/backport path.</p>
            <ShipWeekIssueList
              issues={shipWeek.issues}
              loading={sectionLoading.issues}
              hasLoaded={hasLoaded}
              emptyState={sectionLoading.issues ? 'Loading milestone issues...' : 'No open non-PR issues in this milestone.'}
            />
          </section>
        </>
      )}
    </section>
  );
}

function SectionLoadingIndicator({ hasLoaded }: { hasLoaded: boolean }) {
  return (
    <LoadingBadge
      label={hasLoaded ? 'Refreshing' : 'Loading'}
      ariaLabel={hasLoaded ? 'Still refreshing' : 'Still loading'}
    />
  );
}

function ShipWeekLoadingProgress({
  sectionLoading,
  hasLoaded,
}: {
  sectionLoading: ShipWeekLoadingState;
  hasLoaded: boolean;
}) {
  const sections = [
    { label: 'milestone PRs', loading: sectionLoading.milestone },
    { label: 'base-branch PRs', loading: sectionLoading.baseBranch },
    { label: 'generated docs PRs', loading: sectionLoading.docs },
    { label: 'open issues', loading: sectionLoading.issues },
  ];
  const loadingSections = sections.filter((section) => section.loading);

  if (loadingSections.length === 0) {
    return null;
  }

  const readyCount = sections.length - loadingSections.length;
  const action = hasLoaded ? 'Refreshing' : 'Loading';
  const resultTiming = hasLoaded ? 'Counts stay pinned until each section finishes.' : 'Counts appear as each section finishes.';

  return (
    <p className="ship-week-loading-progress" role="status">
      {action} release scope: {readyCount} of {sections.length} data groups ready.
      {' '}
      Still loading {formatList(loadingSections.map((section) => section.label))}. {resultTiming}
    </p>
  );
}

function createShipModeModel(shipWeek: ShipWeekResponse) {
  const visiblePullRequests = shipWeek.pullRequests
    .filter((item) => !item.pullRequest.draft && !shouldHideFromSharedPullRequestLists(item.pullRequest));
  const milestonePullRequests = visiblePullRequests
    .filter((item) => item.releaseScope.inMilestone)
    .map(createShipModePullRequestItem);
  const baseBranchPullRequests = visiblePullRequests
    .filter((item) => item.releaseScope.targetsReleaseBranch)
    .map(createShipModePullRequestItem);
  const docsFromCodePullRequests = visiblePullRequests
    .filter((item) => item.releaseScope.docsFromCode)
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
  loading,
  hasLoaded,
  emptyState,
  onSelectPullRequest,
  onVisiblePullRequest,
  visibleChecksRefreshKey,
}: {
  items: ShipModePullRequestItem[];
  loading: boolean;
  hasLoaded: boolean;
  emptyState: string;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest: VisiblePullRequestHandler;
  visibleChecksRefreshKey: number;
}) {
  if (loading && !hasLoaded && items.length === 0) {
    return <LoadingCardPlaceholders label="Loading ship mode pull request cards" />;
  }

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
      visibleChecksRefreshKey={visibleChecksRefreshKey}
    />
  );
}

function ShipWeekIssueList({
  issues,
  loading,
  hasLoaded,
  emptyState,
}: {
  issues: ShipWeekIssueSummary[];
  loading: boolean;
  hasLoaded: boolean;
  emptyState: string;
}) {
  return (
    <div className="attention-list">
      {loading && !hasLoaded && issues.length === 0 ? (
        <LoadingCardPlaceholders label="Loading ship mode issue cards" />
      ) : issues.length === 0 && <p className="empty-for-me">{emptyState}</p>}
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
  hasLoaded,
}: {
  label: string;
  value: number | string;
  detail: string;
  tone?: 'accent' | 'warning' | 'danger' | 'success';
  loading?: boolean;
  hasLoaded: boolean;
}) {
  return (
    <article className={`ship-week-summary-card ${loading ? 'loading' : tone}`}>
      <div className="ship-week-summary-card-header">
        <span className="ship-week-summary-card-label">{label}</span>
        {loading && <SectionLoadingIndicator hasLoaded={hasLoaded} />}
      </div>
      <LoadingMetric
        value={value}
        loading={loading}
        hasLoaded={hasLoaded}
        formatValue={(nextValue) => typeof nextValue === 'number' ? nextValue.toLocaleString() : nextValue}
        className="ship-week-summary-card-value"
        pendingLabel={`${label} count is loading`}
      />
      <p>{loading ? (hasLoaded ? 'Refreshing...' : 'Calculating...') : detail}</p>
    </article>
  );
}

function formatList(items: string[]) {
  if (items.length <= 1) {
    return items[0] ?? '';
  }

  if (items.length === 2) {
    return `${items[0]} and ${items[1]}`;
  }

  return `${items.slice(0, -1).join(', ')}, and ${items[items.length - 1]}`;
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
