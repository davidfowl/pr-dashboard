import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { currentRelease } from '../../constants';
import type { AttentionBucket, AttentionItem, DeveloperPullRequestCount, PullRequestSummary } from '../../types';
import { colorForText, formatCount, formatRelative, initials } from '../../utils/format';
import { targetsCurrentRelease } from '../../utils/models';
import { shortRepoName } from '../../utils/routing';
import PullRequestListItem from '../PullRequestListItem';
import AttentionBoard from './AttentionBoard';
import TileDrilldown from './TileDrilldown';
import type { DrilldownTile } from './TileDrilldown';

type OwnerTab = 'core' | 'automation' | 'community';
type OwnerTile = DrilldownTile<OwnerTab>;

type QueueOverviewProps = {
  counts: DeveloperPullRequestCount[];
  automationPullRequests: PullRequestSummary[];
  communityPullRequests: PullRequestSummary[];
  attentionBuckets: AttentionBucket[];
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
  bucketRank: number;
  itemRank: number;
  releasePriority: boolean;
  ownership: OwnerTab;
};

const focusBucketLabels = ['Ready to merge', 'Needs review', 'Stalled', 'Author response', 'Review started'];

function QueueOverview({
  counts,
  automationPullRequests,
  communityPullRequests,
  attentionBuckets,
  onSelectPullRequest,
}: QueueOverviewProps) {
  const [selectedOwnerTab, setSelectedOwnerTab] = useState<OwnerTab>('core');
  const [showAllCoreMembers, setShowAllCoreMembers] = useState(false);

  const ownershipKeys = useMemo(() => ({
    automation: new Set(automationPullRequests.map(pullRequestKey)),
    community: new Set(communityPullRequests.map(pullRequestKey)),
  }), [automationPullRequests, communityPullRequests]);

  const focusItems = useMemo<FocusItem[]>(() => {
    const orderedBuckets = focusBucketLabels
      .map((label) => attentionBuckets.find((bucket) => bucket.label === label))
      .filter((bucket): bucket is AttentionBucket => bucket !== undefined);

    return orderedBuckets
      .flatMap((bucket, bucketRank) =>
        bucket.items.map((item, itemRank) => ({
          ...item,
          bucketLabel: bucket.label,
          bucketTone: bucket.tone,
          bucketRank,
          itemRank,
          releasePriority: targetsCurrentRelease(item.pullRequest),
          ownership: ownershipForPullRequest(item.pullRequest, ownershipKeys),
        })))
      .filter((item) => item.ownership !== 'automation')
      .sort((first, second) =>
        Number(second.releasePriority) - Number(first.releasePriority)
        || first.bucketRank - second.bucketRank
        || first.itemRank - second.itemRank)
      .slice(0, 8);
  }, [attentionBuckets, ownershipKeys]);

  const coreOpenCount = counts.reduce((total, count) => total + count.openPullRequestCount, 0);
  const activeCoreCounts = counts.filter((count) => count.openPullRequestCount > 0);
  const visibleCoreCounts = showAllCoreMembers ? counts : activeCoreCounts;
  const ownerTabs: OwnerTile[] = [
    {
      id: 'core' as const,
      label: 'Core team',
      count: coreOpenCount,
      summary: formatCount(activeCoreCounts.length, 'active author'),
    },
    {
      id: 'automation' as const,
      label: 'Bots / automation',
      count: automationPullRequests.length,
      summary: 'kept out of community',
    },
    {
      id: 'community' as const,
      label: 'Community',
      count: communityPullRequests.length,
      summary: 'human authors outside core',
    },
  ];

  function renderOwnerDetails(tab: OwnerTile) {
    if (tab.id === 'core') {
      return (
        <section className="drilldown-panel" aria-label="Core team open pull requests">
          <div className="drilldown-header">
            <span>Core team</span>
            <button type="button" onClick={() => setShowAllCoreMembers((value) => !value)}>
              {showAllCoreMembers ? 'Show active only' : `Show all ${counts.length}`}
            </button>
          </div>
          {visibleCoreCounts.length === 0 ? (
            <p className="empty-for-me">No loaded open PRs from core team members.</p>
          ) : (
            <div className="core-member-list">
              {visibleCoreCounts.map((count) => (
                <article
                  key={count.actor}
                  className="core-member-row"
                  style={{ '--developer-accent': colorForText(count.actor) } as CSSProperties}
                >
                  <span className="avatar-dot">{initials(count.actor)}</span>
                  <strong>{count.actor}</strong>
                  <span>{formatCount(count.openPullRequestCount, 'open PR')}</span>
                  <em>
                    {count.latestUpdatedAt
                      ? `${repositorySummary(count.repositories)} · updated ${formatRelative(count.latestUpdatedAt)}`
                      : 'No loaded open PRs'}
                  </em>
                </article>
              ))}
            </div>
          )}
        </section>
      );
    }

    if (tab.id === 'automation') {
      return (
        <PullRequestSection
          title="Bots / automation"
          emptyMessage="No bot or automation open PRs in the current results."
          pullRequests={automationPullRequests}
          owner="automation"
          onSelectPullRequest={onSelectPullRequest}
        />
      );
    }

    return (
      <PullRequestSection
        title="Community PRs"
        emptyMessage="No community open PRs in the current results."
        pullRequests={communityPullRequests}
        owner="community"
        onSelectPullRequest={onSelectPullRequest}
      />
    );
  }

  return (
    <section className="queue-overview" aria-label="Queue overview">
      <div className="section-title-row">
        <p className="eyebrow">Queue overview</p>
        <h3>Start with the health signals, then drill into the details.</h3>
        <p className="board-guidance">
          The focus queue hides automation noise and keeps the owner breakdown one click away.
        </p>
      </div>

      <div className="queue-stat-grid" aria-label="Action health">
        {actionStats(attentionBuckets).map((stat) => (
          <article key={stat.label} className={`queue-stat-card ${stat.tone}`}>
            <span>{stat.label}</span>
            <strong>{stat.count}</strong>
            <p>{stat.summary}</p>
          </article>
        ))}
      </div>

      <section className="focus-panel" aria-label="Focused attention queue">
        <div className="attention-card-header">
          <span>Needs attention</span>
          <strong>{formatCount(focusItems.length, 'shown')}</strong>
        </div>
        <p>Highest-signal non-automation items from the current review queue.</p>

        <div className="attention-list">
          {focusItems.length === 0 && (
            <p className="empty-for-me">No human-authored open PRs need attention in the current results.</p>
          )}
          {focusItems.map((item) => (
            <PullRequestListItem
              key={`${item.pullRequest.repository}-${item.pullRequest.number}`}
              pullRequest={item.pullRequest}
              onSelectPullRequest={onSelectPullRequest}
              signalProps={{
                leadingSignals: [{ label: item.bucketLabel, tone: item.bucketTone }],
                trailingSignals: [{ label: ownerLabel(item.ownership), tone: 'muted' }],
                computedSignalLimit: 4,
                excludeComputedLabels: [ownerLabel(item.ownership)],
              }}
            />
          ))}
        </div>
      </section>

      <TileDrilldown
        className="owner-drilldown"
        ariaLabel="Open pull request owner breakdown"
        idPrefix="owner-drilldown"
        selectedId={selectedOwnerTab}
        tileListLabel="Open PR owner categories"
        tiles={ownerTabs}
        onSelect={setSelectedOwnerTab}
        renderDetails={renderOwnerDetails}
      />

      {attentionBuckets.length > 0 && (
        <AttentionBoard buckets={attentionBuckets} onSelectPullRequest={onSelectPullRequest} />
      )}
    </section>
  );
}

type PullRequestSectionProps = {
  title: string;
  emptyMessage: string;
  pullRequests: PullRequestSummary[];
  owner: OwnerTab;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
};

function PullRequestSection({
  title,
  emptyMessage,
  pullRequests,
  owner,
  onSelectPullRequest,
}: PullRequestSectionProps) {
  const ownerSignal = { label: ownerLabel(owner), tone: 'muted' as const };

  return (
    <section className="drilldown-panel" aria-label={title}>
      <div className="attention-card-header">
        <span>{title}</span>
        <strong>{formatCount(pullRequests.length, 'open PR')}</strong>
      </div>

      <div className="attention-list">
        {pullRequests.length === 0 && (
          <p className="empty-for-me">{emptyMessage}</p>
        )}
        {pullRequests.map((pullRequest) => (
          <PullRequestListItem
            key={`${pullRequest.repository}-${pullRequest.number}`}
            pullRequest={pullRequest}
            onSelectPullRequest={onSelectPullRequest}
            signalProps={{
              trailingSignals: [ownerSignal],
              excludeComputedLabels: [ownerSignal.label],
            }}
          />
        ))}
      </div>
    </section>
  );
}

function actionStats(attentionBuckets: AttentionBucket[]) {
  return [
    {
      label: `Release ${currentRelease}`,
      tone: 'danger' as const,
      summary: 'Current-release PRs get bumped up.',
      count: attentionBuckets
        .flatMap((bucket) => bucket.items)
        .filter((item) => targetsCurrentRelease(item.pullRequest)).length,
    },
    {
      label: 'Ready to merge',
      tone: 'success' as const,
      summary: 'Approved PRs waiting to land.',
      count: bucketCount(attentionBuckets, 'Ready to merge'),
    },
    {
      label: 'Needs review',
      tone: 'warning' as const,
      summary: 'Ready PRs without a human review.',
      count: bucketCount(attentionBuckets, 'Needs review'),
    },
    {
      label: 'Stalled',
      tone: 'warning' as const,
      summary: 'Reviewed or discussed, then quiet.',
      count: bucketCount(attentionBuckets, 'Stalled'),
    },
    {
      label: 'Author response',
      tone: 'danger' as const,
      summary: 'Blocked on changes from the author.',
      count: bucketCount(attentionBuckets, 'Author response'),
    },
  ];
}

function bucketCount(attentionBuckets: AttentionBucket[], label: string) {
  return attentionBuckets.find((bucket) => bucket.label === label)?.items.length ?? 0;
}

function ownershipForPullRequest(
  pullRequest: PullRequestSummary,
  ownershipKeys: { automation: Set<string>; community: Set<string> },
): OwnerTab {
  const key = pullRequestKey(pullRequest);
  if (ownershipKeys.automation.has(key)) {
    return 'automation';
  }

  if (ownershipKeys.community.has(key)) {
    return 'community';
  }

  return 'core';
}

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository}#${pullRequest.number}`;
}

function ownerLabel(owner: OwnerTab) {
  return owner === 'automation'
    ? 'bot'
    : owner === 'community'
      ? 'community'
      : 'core';
}

function repositorySummary(repositories: string[]) {
  const visibleRepositories = repositories.slice(0, 2).map(shortRepoName);
  const overflowCount = repositories.length - visibleRepositories.length;
  const suffix = overflowCount > 0 ? `, +${formatCount(overflowCount, 'repo')}` : '';
  return visibleRepositories.length === 0
    ? 'No repositories'
    : `Across ${visibleRepositories.join(', ')}${suffix}`;
}

export default QueueOverview;
