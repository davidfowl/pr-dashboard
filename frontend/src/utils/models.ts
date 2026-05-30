import {
  coreTeamMembers,
  currentRelease,
  dayMs,
  docsFromCodeLabel,
  docsFromCodeRepository,
  hourMs,
} from '../constants';
import type {
  ActivityMarker,
  ActivityModel,
  AttentionBucket,
  AttentionIssueBucket,
  AttentionItem,
  AttentionSignal,
  DeveloperPullRequestCount,
  DeveloperStats,
  MergeableState,
  PickItem,
  PullRequestSummary,
  ShipWeekIssueBucket,
  ShipWeekIssueSummary,
  ShipWeekResponse,
  ShipWeekScopeGroup,
  SignalMilestone,
  TimelineItem,
  TimelineStats,
  TimelineStoryEntry,
  TriageModel,
  TriageParticipant,
} from '../types';
import {
  formatAge,
  formatCount,
  formatDateShort,
  formatDuration,
  formatRelative,
} from './format';
import { dedupeSignals } from './signals';

const approvedAgingMs = 2 * dayMs;
const communityWaitMs = 12 * hourMs;
const needsReviewFreshMs = 2 * dayMs;
const quickWinLineThreshold = 80;
const quickWinFileThreshold = 3;
const approvedButAgingBucketLabel = 'Approved but aging';
const regressionBucketLabel = 'Regression';

export function createDeveloperPullRequestCounts(pullRequests: PullRequestSummary[]): DeveloperPullRequestCount[] {
  const coreTeamKeys = new Set(coreTeamMembers.map(actorIdentityKey));
  const pullRequestsByDeveloper = new Map<string, PullRequestSummary[]>(
    coreTeamMembers.map((member) => [actorIdentityKey(member), []]),
  );

  for (const pullRequest of pullRequests) {
    if (pullRequest.state !== 'open' || isCommunityToolkitPullRequest(pullRequest)) {
      continue;
    }

    const authorKey = actorIdentityKey(pullRequest.author);
    if (coreTeamKeys.has(authorKey)) {
      pullRequestsByDeveloper.get(authorKey)?.push(pullRequest);
    }
  }

  return coreTeamMembers
    .map((member) => {
      const developerPullRequests = pullRequestsByDeveloper.get(actorIdentityKey(member)) ?? [];
      const repositories = [...new Set(developerPullRequests.map((pullRequest) => pullRequest.repository))]
        .sort((first, second) => first.localeCompare(second));
      const latestUpdatedAt = developerPullRequests
        .map((pullRequest) => pullRequest.updatedAt)
        .sort((first, second) => new Date(second).getTime() - new Date(first).getTime())[0];

      return {
        actor: member,
        openPullRequestCount: developerPullRequests.length,
        repositories,
        latestUpdatedAt,
      };
    })
    .sort((first, second) =>
      second.openPullRequestCount - first.openPullRequestCount
      || first.actor.localeCompare(second.actor));
}

export function createForMeItems(pullRequests: PullRequestSummary[], login?: string): PickItem[] {
  if (!login) {
    return [];
  }

  return pullRequests
    .filter((pullRequest) => pullRequest.state === 'open' && !pullRequest.draft)
    .map((pullRequest) => createPersonalPick(pullRequest, login))
    .filter((item): item is PickItem => item !== null)
    .sort((first, second) => pickScore(second) - pickScore(first))
    .slice(0, 10);
}

function createPersonalPick(pullRequest: PullRequestSummary, login: string): PickItem | null {
  if (sameLogin(pullRequest.author, login) && pullRequest.checks?.state === 'failure') {
    return {
      pullRequest,
      action: 'Fix CI',
      reason: `Your PR has ${formatCount(pullRequest.checks.failureCount, 'failing check')} · ${pickReason(pullRequest)}`,
      tone: 'danger',
      personal: true,
    };
  }

  if (pullRequest.requestedReviewers.some((reviewer) => sameLogin(reviewer, login))) {
    const ciSuffix = pullRequest.checks?.state === 'failure'
      ? ' · CI failing'
      : pullRequest.checks?.state === 'pending'
        ? ' · CI running'
        : '';
    return {
      pullRequest,
      action: 'Review this',
      reason: `Review requested from you · ${pickReason(pullRequest)}${ciSuffix}`,
      tone: 'warning',
      personal: true,
    };
  }

  if (sameLogin(pullRequest.author, login) && pullRequest.review.state === 'changes_requested') {
    return {
      pullRequest,
      action: 'Respond here',
      reason: `Your PR has changes requested · ${pickReason(pullRequest)}`,
      tone: 'danger',
      personal: true,
    };
  }

  if (sameLogin(pullRequest.author, login) && pullRequest.review.state === 'approved') {
    return {
      pullRequest,
      action: 'Finish this',
      reason: `Your PR is approved and still open · ${pickReason(pullRequest)}`,
      tone: 'success',
      personal: true,
    };
  }

  return null;
}

function pickScore(item: PickItem) {
  let score = item.personal ? 1000 : 0;
  if (item.action === 'Fix CI') score += 110;
  if (item.action === 'Review this') score += 90;
  if (item.action === 'Merge this' || item.action === 'Finish this') score += 80;
  if (item.action === 'Respond here') score += 75;
  if (item.action === 'Finish review') score += 65;
  if (item.action === 'Unstick this') score += 45;
  if (item.pullRequest.review.state === 'changes_requested') score += 30;
  if (item.pullRequest.review.state === 'waiting') score += 45;
  if (item.pullRequest.review.state === 'reviewed') score += 25;
  if (item.pullRequest.review.state === 'approved') score += 5;
  if (isBotAuthor(item.pullRequest.author)) score -= 120;
  return score
    + Math.min(3, Math.floor(ageMs(item.pullRequest.createdAt) / dayMs))
    + Math.min(1, Math.floor(updatedAgeMs(item.pullRequest) / dayMs));
}

function pickReason(pullRequest: PullRequestSummary) {
  const signals = [`open ${formatAge(pullRequest.createdAt)}`];
  if (pullRequest.review.approvalCount > 0) {
    signals.push(formatCount(pullRequest.review.approvalCount, 'approval'));
  } else if (pullRequest.review.reviewerCount > 0) {
    signals.push(`${formatCount(pullRequest.review.reviewerCount, 'reviewer')} · 0 approvals`);
  } else {
    signals.push('no reviews');
  }

  if (isIdle(pullRequest)) {
    signals.push(`idle ${formatAge(pullRequest.updatedAt)}`);
  }

  return signals.join(' · ');
}

function sameLogin(first: string, second: string) {
  return actorIdentityKey(first) === actorIdentityKey(second);
}

export function createAttentionBuckets(pullRequests: PullRequestSummary[]): AttentionBucket[] {
  const buckets: AttentionBucket[] = [
    {
      label: regressionBucketLabel,
      summary: 'PRs or linked issues tagged as regressions, including Aspire last-release regressions.',
      tone: 'danger',
      metric: 'regression watch',
      items: [],
    },
    {
      label: approvedButAgingBucketLabel,
      summary: 'Approved PRs that have been waiting to land for multiple days.',
      tone: 'danger',
      metric: 'land stale approvals',
      items: [],
    },
    {
      label: 'CI failing',
      summary: 'Open PRs whose head commit has failing checks. Author needs to fix before reviewers can help.',
      tone: 'danger',
      metric: 'unblock CI',
      items: [],
    },
    {
      label: 'Ready to merge',
      summary: 'Recently approved and waiting on a maintainer or author to finish.',
      tone: 'success',
      metric: 'merge rate ↑',
      items: [],
    },
    {
      label: 'Re-review needed',
      summary: 'A commit landed after human review, so the PR needs another look.',
      tone: 'warning',
      metric: 'finish loops',
      items: [],
    },
    {
      label: 'Docs',
      summary: 'Generated aspire.dev docs PRs waiting on review.',
      tone: 'accent',
      metric: 'docs review',
      items: [],
    },
    {
      label: 'Community Toolkit',
      summary: 'CommunityToolkit/Aspire PRs in the shared Aspire queue.',
      tone: 'accent',
      metric: 'toolkit review',
      items: [],
    },
    {
      label: 'Bots / automation',
      summary: 'Automated PRs kept out of the human focus queue.',
      tone: 'accent',
      metric: 'automation lane',
      items: [],
    },
    {
      label: 'Community',
      summary: 'External-contributor PRs kept out of the core-team focus lanes.',
      tone: 'accent',
      metric: 'external contributors',
      items: [],
    },
    {
      label: 'Quick wins',
      summary: 'Small recently updated core-team PRs that should be easy to drain.',
      tone: 'success',
      metric: 'drain queue',
      items: [],
    },
    {
      label: 'Needs review',
      summary: 'Recently updated core-team PRs that have not had a human review yet.',
      tone: 'warning',
      metric: 'coverage ↑',
      items: [],
    },
    {
      label: 'Review started',
      summary: 'Someone engaged, but the PR is not approved yet.',
      tone: 'accent',
      metric: 'finish reviews',
      items: [],
    },
    {
      label: 'Stalled',
      summary: 'PRs that went quiet, including stale unreviewed work.',
      tone: 'warning',
      metric: 'idle ↓',
      items: [],
    },
    {
      label: 'Author response',
      summary: 'Review is blocked on changes from the PR author.',
      tone: 'danger',
      metric: 'blockers ↓',
      items: [],
    },
    {
      label: 'Draft',
      summary: 'Not ready for the shared review queue yet.',
      tone: 'accent',
      metric: 'readiness ↑',
      items: [],
    },
  ];
  const bucketsByLabel = new Map(buckets.map((bucket) => [bucket.label, bucket]));

  for (const pullRequest of pullRequests.filter((item) => item.state === 'open')) {
    for (const bucketLabel of reviewBucketLabels(pullRequest)) {
      bucketsByLabel.get(bucketLabel)?.items.push({
        pullRequest,
        reason: reviewSignal(pullRequest, bucketLabel),
      });
    }
  }

  return buckets.filter((bucket) => bucket.items.length > 0);
}

export function createRegressionIssueBuckets(issues: ShipWeekIssueSummary[]): AttentionIssueBucket[] {
  const regressionIssues = issues
    .filter((issue) => hasRegressionLabel(issue.labels))
    .sort((first, second) => new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime());

  return regressionIssues.length === 0
    ? []
    : [
      {
        label: regressionBucketLabel,
        summary: 'Open issues tagged as regressions, including Aspire last-release regressions.',
        tone: 'danger',
        metric: 'regression watch',
        issues: regressionIssues,
      },
    ];
}

export function createShipWeekScopeGroups(shipWeek: ShipWeekResponse): ShipWeekScopeGroup[] {
  return [
    {
      id: 'milestone-prs',
      label: `Milestone ${shipWeek.milestone}`,
      summary: 'PRs in or linked to the milestone',
      tone: 'accent',
      pullRequests: shipWeek.pullRequests.filter((item) => item.releaseScope.inMilestone),
    },
    {
      id: 'release-branch-prs',
      label: shipWeek.releaseBranch,
      summary: 'PRs targeting the selected base branch',
      tone: 'warning',
      pullRequests: shipWeek.pullRequests.filter((item) => item.releaseScope.targetsReleaseBranch),
    },
    {
      id: 'release-branch-watchlist',
      label: 'Outside milestone',
      summary: 'Base-branch PRs outside the milestone',
      tone: 'danger',
      pullRequests: shipWeek.pullRequests.filter((item) => item.releaseScope.releaseBranchException),
    },
  ];
}

const shipWeekIssueBucketDefinitions: Omit<ShipWeekIssueBucket, 'issues'>[] = [
  {
    label: regressionBucketLabel,
    summary: 'Issues tagged as regressions, including Aspire last-release regressions.',
    tone: 'danger',
  },
  {
    label: 'Needs PR',
    summary: 'Open milestone issues with no inferred open PR.',
    tone: 'danger',
  },
  {
    label: 'Needs validation',
    summary: 'Issues with a PR in flight or explicit validation signals.',
    tone: 'warning',
  },
  {
    label: 'Installer/acquisition',
    summary: 'Installer, workload, setup, or acquisition work.',
    tone: 'accent',
  },
  {
    label: 'TypeScript/polyglot',
    summary: 'TypeScript, JavaScript, Node, or polyglot work.',
    tone: 'accent',
  },
  {
    label: 'CLI channel/versioning',
    summary: 'CLI, channel, feed, template, or versioning work.',
    tone: 'accent',
  },
  {
    label: 'Docs/release readiness',
    summary: 'Docs, release notes, announcements, or readiness tasks.',
    tone: 'success',
  },
  {
    label: 'Unowned',
    summary: 'No assignee and no domain bucket match.',
    tone: 'warning',
  },
];
const shipWeekIssueBucketToneByLabel = new Map(
  shipWeekIssueBucketDefinitions.map((bucket) => [bucket.label, bucket.tone]),
);
const staleIssueMs = 7 * dayMs;
const coldIssueMs = 14 * dayMs;

export function createShipWeekIssueBuckets(issues: ShipWeekIssueSummary[]): ShipWeekIssueBucket[] {
  const buckets = shipWeekIssueBucketDefinitions.map((bucket) => ({
    ...bucket,
    issues: [] as ShipWeekIssueSummary[],
  }));
  const bucketsByLabel = new Map(buckets.map((bucket) => [bucket.label, bucket]));

  for (const issue of issues) {
    for (const bucketLabel of shipWeekIssueBucketLabels(issue)) {
      bucketsByLabel.get(bucketLabel)?.issues.push(issue);
    }
  }

  return buckets.filter((bucket) => bucket.issues.length > 0);
}

export function createIssueSignals(issue: ShipWeekIssueSummary): AttentionSignal[] {
  const action = issueActionSignal(issue);
  const signals: AttentionSignal[] = [action];

  if (targetsCurrentReleaseIssue(issue)) {
    signals.push({ label: `release ${currentRelease}`, tone: 'danger' });
  }

  if (issue.linkedOpenPullRequests.length > 0) {
    signals.push({ label: formatCount(issue.linkedOpenPullRequests.length, 'open PR'), tone: 'warning' });
  }

  for (const bucketLabel of shipWeekIssueBucketLabels(issue)) {
    if (bucketLabel !== action.label) {
      signals.push({
        label: bucketLabel,
        tone: shipWeekIssueBucketToneByLabel.get(bucketLabel) ?? 'muted',
      });
    }
  }

  const updatedAge = ageMs(issue.updatedAt);
  if (updatedAge >= coldIssueMs) {
    signals.push({ label: `idle ${formatAge(issue.updatedAt)}`, tone: 'danger' });
  } else if (updatedAge >= staleIssueMs) {
    signals.push({ label: `idle ${formatAge(issue.updatedAt)}`, tone: 'warning' });
  }

  if (issue.milestone) {
    signals.push({ label: 'milestone', tone: 'accent' });
  }

  for (const label of issue.labels.slice(0, 2)) {
    signals.push({ label, tone: 'accent' });
  }

  if (isBotAuthor(issue.author)) {
    signals.push({ label: 'bot', tone: 'accent' });
  }

  return dedupeSignals(signals).slice(0, 7);
}

function issueActionSignal(issue: ShipWeekIssueSummary): AttentionSignal {
  if (hasRegressionLabel(issue.labels)) {
    return { label: regressionBucketLabel, tone: 'danger' };
  }

  if (issue.linkedOpenPullRequests.length === 0) {
    return { label: 'Needs PR', tone: 'danger' };
  }

  if (issue.assignees.length === 0) {
    return { label: 'Unowned', tone: 'warning' };
  }

  return { label: 'Needs validation', tone: 'warning' };
}

function shipWeekIssueBucketLabels(issue: ShipWeekIssueSummary) {
  const labels: string[] = [];
  let domainMatch = false;

  if (hasRegressionLabel(issue.labels)) {
    labels.push(regressionBucketLabel);
  }

  if (issue.linkedOpenPullRequests.length === 0) {
    labels.push('Needs PR');
  }

  if (issue.linkedOpenPullRequests.length > 0 || issueMatchesTerms(issue, validationTerms)) {
    labels.push('Needs validation');
  }

  if (issueMatchesTerms(issue, installerTerms)) {
    labels.push('Installer/acquisition');
    domainMatch = true;
  }

  if (issueMatchesTerms(issue, typeScriptTerms)) {
    labels.push('TypeScript/polyglot');
    domainMatch = true;
  }

  if (issueMatchesTerms(issue, cliTerms)) {
    labels.push('CLI channel/versioning');
    domainMatch = true;
  }

  if (issueMatchesTerms(issue, docsTerms)) {
    labels.push('Docs/release readiness');
    domainMatch = true;
  }

  if (issue.assignees.length === 0 && !domainMatch) {
    labels.push('Unowned');
  }

  return labels;
}

function targetsCurrentReleaseIssue(issue: ShipWeekIssueSummary) {
  return [
    issue.title,
    issue.milestone,
    ...issue.labels,
  ].some((value) => value !== undefined && value !== null && releaseSignalMatches(value, currentRelease));
}

const validationTerms = ['validation', 'validate', 'verify', 'verification', 'test', 'e2e', 'servicing validation'];
const installerTerms = ['installer', 'install', 'workload', 'acquisition', 'setup', 'visual studio', ' vs ', 'sdk'];
const typeScriptTerms = ['typescript', ' ts ', 'javascript', ' js ', 'node', 'polyglot', 'apphost'];
const cliTerms = ['cli', 'channel', 'version', 'versioning', 'feed', 'template'];
const docsTerms = ['docs', 'documentation', 'release notes', 'readme', 'release readiness', 'announcement'];

function issueMatchesTerms(issue: ShipWeekIssueSummary, terms: string[]) {
  const searchText = ` ${[
    issue.title,
    issue.author,
    ...issue.labels,
  ].join(' ').toLowerCase()} `;
  return terms.some((term) => searchText.includes(term));
}

function reviewBucketLabels(pullRequest: PullRequestSummary) {
  const labels: string[] = [];

  if (hasRegressionSignal(pullRequest)) {
    labels.push(regressionBucketLabel);
  }

  if (pullRequest.draft) {
    labels.push('Draft');
    return labels;
  }

  if (isBotAuthor(pullRequest.author)) {
    labels.push('Bots / automation');
  }

  if (isGeneratedDocsPullRequest(pullRequest)) {
    labels.push('Docs');
  }

  if (isCommunityToolkitPullRequest(pullRequest)) {
    labels.push('Community Toolkit');
  }

  const ciFailing = isChecksFailing(pullRequest);
  if (ciFailing) {
    labels.push('CI failing');
  }

  const approvedButAging = isApprovedButAging(pullRequest);
  if (approvedButAging) {
    labels.push('Approved but aging');
  }

  // Failing CI disqualifies "Ready to merge" — the PR is not actually ready to land.
  if (pullRequest.review.state === 'approved' && !approvedButAging && !ciFailing) {
    labels.push('Ready to merge');
  }

  if (needsReReview(pullRequest)) {
    labels.push('Re-review needed');
  }

  if (pullRequest.review.state === 'changes_requested') {
    labels.push('Author response');
  }

  if (isIdle(pullRequest)) {
    labels.push('Stalled');
  }

  if (isCommunityAuthor(pullRequest.author) && !isCommunityToolkitPullRequest(pullRequest)) {
    labels.push('Community');
  }

  // A small PR with red CI is not a "drain queue" candidate until CI is fixed.
  if (isQuickWin(pullRequest) && !ciFailing) {
    labels.push('Quick wins');
  }

  if (needsReview(pullRequest)) {
    labels.push('Needs review');
  }

  if (labels.length === 0) {
    labels.push('Review started');
  }

  return labels;
}

function reviewSignal(pullRequest: PullRequestSummary, bucketLabel: string) {
  if (pullRequest.draft) {
    return 'Draft';
  }

  const approvedAt = approvalAgeAt(pullRequest);
  switch (bucketLabel) {
    case regressionBucketLabel:
      return regressionSignal(pullRequest);
    case approvedButAgingBucketLabel:
      return approvedAt ? `Approved ${formatAge(approvedAt)}` : 'Approved';
    case 'CI failing':
      return formatCount(pullRequest.checks?.failureCount ?? 0, 'failing check');
    case 'Ready to merge':
      return `${formatCount(pullRequest.review.approvalCount, 'approval')}`;
    case 'Re-review needed':
      return pullRequest.lastCommitAt
        ? `Pushed ${formatAge(pullRequest.lastCommitAt)}`
        : 'Pushed after review';
    case 'Docs':
      return 'generated docs';
    case 'Community Toolkit':
      return 'CommunityToolkit/Aspire';
    case 'Bots / automation':
      return 'bot';
    case 'Community':
      return isCommunityWaiting(pullRequest)
        ? `Community · waiting ${formatAge(pullRequest.createdAt)}`
        : 'community';
    case 'Quick wins':
      return reviewFootprint(pullRequest);
    case 'Needs review':
      return 'No reviews';
    case 'Stalled':
      return `Idle ${formatRelative(pullRequest.updatedAt)}`;
    case 'Author response':
      return 'Changes requested';
    default:
      return formatCount(pullRequest.review.reviewerCount, 'reviewer');
  }
}

function isApprovedButAging(pullRequest: PullRequestSummary) {
  const approvedAt = approvalAgeAt(pullRequest);
  return pullRequest.review.state === 'approved'
    && approvedAt != null
    && ageMs(approvedAt) >= approvedAgingMs;
}

function isChecksFailing(pullRequest: PullRequestSummary) {
  return pullRequest.checks?.state === 'failure';
}

function isChecksPending(pullRequest: PullRequestSummary) {
  return pullRequest.checks?.state === 'pending' || pullRequest.checks?.state === 'unknown';
}

function hasRegressionSignal(pullRequest: PullRequestSummary) {
  return hasRegressionLabel(pullRequest.labels)
    || pullRequest.linkedIssues.some((issue) => hasRegressionLabel(issue.labels));
}

function hasRegressionLabel(labels: readonly string[]) {
  return labels.some((label) => label.toLowerCase().includes('regression'));
}

function regressionSignal(pullRequest: PullRequestSummary) {
  if (hasRegressionLabel(pullRequest.labels)) {
    return 'Regression label';
  }

  const issue = pullRequest.linkedIssues.find((linkedIssue) => hasRegressionLabel(linkedIssue.labels));
  return issue ? `Regression issue #${issue.number}` : 'Regression';
}

function approvalAgeAt(pullRequest: PullRequestSummary) {
  return pullRequest.review.lastApprovedAt ?? pullRequest.review.lastReviewedAt;
}

function needsReReview(pullRequest: PullRequestSummary) {
  return pullRequest.review.lastReviewedAt != null
    && pullRequest.lastCommitAt != null
    && (pullRequest.review.state === 'reviewed' || pullRequest.review.state === 'changes_requested')
    && new Date(pullRequest.lastCommitAt).getTime() > new Date(pullRequest.review.lastReviewedAt).getTime();
}

export function isGeneratedDocsPullRequest(pullRequest: PullRequestSummary) {
  return pullRequest.repository.toLowerCase() === docsFromCodeRepository
    && pullRequest.labels.some((label) => label.toLowerCase() === docsFromCodeLabel);
}

function isCommunityToolkitPullRequest(pullRequest: PullRequestSummary) {
  return pullRequest.repository.toLowerCase() === 'communitytoolkit/aspire';
}

function isCommunityWaiting(pullRequest: PullRequestSummary) {
  return isCommunityAuthor(pullRequest.author)
    && (
      (pullRequest.review.state === 'waiting' && ageMs(pullRequest.createdAt) >= communityWaitMs)
      || (pullRequest.review.state === 'reviewed' && isIdle(pullRequest))
    );
}

function isQuickWin(pullRequest: PullRequestSummary) {
  const linesChanged = changedLineCount(pullRequest);
  return pullRequest.review.state === 'waiting'
    && isCoreTeamAuthor(pullRequest.author)
    && !targetsCurrentRelease(pullRequest)
    && pullRequest.linkedIssues.length <= 1
    && pullRequest.commitCount <= 2
    && pullRequest.changedFiles > 0
    && pullRequest.changedFiles <= quickWinFileThreshold
    && linesChanged > 0
    && linesChanged <= quickWinLineThreshold
    && !isIdle(pullRequest);
}

function needsReview(pullRequest: PullRequestSummary) {
  return pullRequest.review.state === 'waiting'
    && isCoreTeamAuthor(pullRequest.author)
    && ageMs(pullRequest.updatedAt) < needsReviewFreshMs;
}

function reviewFootprint(pullRequest: PullRequestSummary) {
  const parts = [
    pullRequest.changedFiles > 0 ? formatCount(pullRequest.changedFiles, 'file') : null,
    changedLineCount(pullRequest) > 0 ? formatCount(changedLineCount(pullRequest), 'line') : null,
    pullRequest.commitCount > 0 ? formatCount(pullRequest.commitCount, 'commit') : null,
  ].filter(Boolean);

  return parts.slice(0, 2).join(' · ') || 'size unknown';
}

function changedLineCount(pullRequest: PullRequestSummary) {
  return pullRequest.additions + pullRequest.deletions;
}

function isCommunityAuthor(author: string) {
  return !isBotAuthor(author)
    && !isCoreTeamAuthor(author);
}

function isCoreTeamAuthor(author: string) {
  return coreTeamMembers.some((member) => actorIdentityKey(member) === actorIdentityKey(author));
}

export function createAttentionSignals(item: AttentionItem): AttentionSignal[] {
  const pullRequest = item.pullRequest;
  const signals: AttentionSignal[] = [actionSignal(pullRequest)];

  if (targetsCurrentRelease(pullRequest)) {
    signals.push({ label: `release ${currentRelease}`, tone: 'danger' });
  }

  if (hasRegressionSignal(pullRequest)) {
    signals.push({ label: regressionBucketLabel.toLowerCase(), tone: 'danger' });
  }

  if (pullRequest.baseRef?.startsWith('release/')) {
    signals.push({ label: `base ${pullRequest.baseRef}`, tone: 'danger' });
  }

  const checksSignal = checksAttentionSignal(pullRequest);
  if (checksSignal) {
    signals.push(checksSignal);
  }

  const approvedAt = approvalAgeAt(pullRequest);
  if (isApprovedButAging(pullRequest) && approvedAt) {
    signals.push({ label: `approved ${formatAge(approvedAt)}`, tone: 'danger' });
  }

  if (needsReReview(pullRequest)) {
    signals.push({ label: 'commit after review', tone: 'warning' });
  }

  if (isGeneratedDocsPullRequest(pullRequest)) {
    signals.push({ label: 'docs', tone: 'accent' });
  }

  if (isCommunityToolkitPullRequest(pullRequest)) {
    signals.push({ label: 'community toolkit', tone: 'accent' });
  }

  if (isCommunityWaiting(pullRequest)) {
    signals.push({ label: 'community wait', tone: 'warning' });
  }

  if (isQuickWin(pullRequest)) {
    signals.push({ label: 'quick win', tone: 'success' });
  }

  if (isIdle(pullRequest)) {
    signals.push({ label: `idle ${formatAge(pullRequest.updatedAt)}`, tone: 'warning' });
  }

  const ageSignal = oldFirstSignal(pullRequest);
  if (ageSignal) {
    signals.push(ageSignal);
  }

  signals.push({
    label: `open ${formatAge(pullRequest.createdAt)}`,
    tone: Date.now() - new Date(pullRequest.createdAt).getTime() >= 7 * dayMs ? 'warning' : 'muted',
  });

  const progress = reviewProgressSignal(pullRequest);
  if (progress) {
    signals.push(progress);
  }

  if (pullRequest.review.lastReviewedAt && pullRequest.review.state !== 'waiting') {
    signals.push({ label: `reviewed ${formatAge(pullRequest.review.lastReviewedAt)}`, tone: 'muted' });
  }

  if (pullRequest.review.commentedReviewCount > 0) {
    signals.push({ label: formatCount(pullRequest.review.commentedReviewCount, 'review comment'), tone: 'muted' });
  }

  const computedLabels = isGeneratedDocsPullRequest(pullRequest)
    ? pullRequest.labels.filter((label) => label.toLowerCase() !== docsFromCodeLabel)
    : pullRequest.labels;

  for (const label of computedLabels.slice(0, 2)) {
    signals.push({ label, tone: 'accent' });
  }

  if (isBotAuthor(pullRequest.author)) {
    signals.push({ label: 'bot', tone: 'accent' });
  }

  return signals.slice(0, 7);
}

function checksAttentionSignal(pullRequest: PullRequestSummary): AttentionSignal | null {
  const checks = pullRequest.checks;
  if (!checks || checks.state === 'none' || checks.state === 'unknown') {
    return null;
  }

  if (checks.state === 'failure') {
    const label = checks.failureCount > 0
      ? `CI failing · ${formatCount(checks.failureCount, 'check')}`
      : 'CI failing';
    return { label, tone: 'danger' };
  }

  if (checks.state === 'pending') {
    return { label: 'CI running', tone: 'warning' };
  }

  // Successful CI on dashboard rows is intentionally suppressed to avoid pill noise.
  return null;
}

export function targetsCurrentRelease(pullRequest: PullRequestSummary) {
  return [
    pullRequest.title,
    pullRequest.milestone,
    ...pullRequest.labels,
    ...pullRequest.linkedIssues.flatMap((issue) => [
      issue.title,
      issue.milestone,
      ...issue.labels,
    ]),
  ].some((value) => value !== undefined && releaseSignalMatches(value, currentRelease));
}

function releaseSignalMatches(value: string, release: string) {
  return new RegExp(`(^|[^0-9])${escapeRegExp(release)}([^0-9]|$)`, 'i').test(value);
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function oldFirstSignal(pullRequest: PullRequestSummary): AttentionSignal | null {
  const openAge = ageMs(pullRequest.createdAt);
  if (openAge >= 14 * dayMs) {
    return { label: 'review debt', tone: 'danger' };
  }

  if (openAge >= 7 * dayMs) {
    return { label: 'old first', tone: 'warning' };
  }

  if (openAge < 12 * hourMs) {
    return { label: 'newer', tone: 'muted' };
  }

  return null;
}

function actionSignal(pullRequest: PullRequestSummary): AttentionSignal {
  if (hasRegressionSignal(pullRequest)) {
    return { label: regressionBucketLabel.toLowerCase(), tone: 'danger' };
  }

  if (pullRequest.draft) {
    return { label: 'draft', tone: 'muted' };
  }

  if (isBotAuthor(pullRequest.author)) {
    return { label: 'automation', tone: 'accent' };
  }

  if (isGeneratedDocsPullRequest(pullRequest)) {
    return { label: 'docs review', tone: 'accent' };
  }

  if (isCommunityToolkitPullRequest(pullRequest)) {
    return { label: 'toolkit review', tone: 'accent' };
  }

  if (isChecksFailing(pullRequest)) {
    return { label: 'fix CI', tone: 'danger' };
  }

  if (isApprovedButAging(pullRequest)) {
    return { label: 'land approval', tone: 'danger' };
  }

  if (pullRequest.review.state === 'approved') {
    return isChecksPending(pullRequest)
      ? { label: 'wait for CI', tone: 'warning' }
      : { label: 'merge', tone: 'success' };
  }

  if (needsReReview(pullRequest)) {
    return { label: 're-review', tone: 'warning' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return { label: 'author fix', tone: 'danger' };
  }

  if (isIdle(pullRequest)) {
    return { label: 'unstick', tone: 'warning' };
  }

  if (isCommunityAuthor(pullRequest.author)) {
    return isCommunityWaiting(pullRequest)
      ? { label: 'community wait', tone: 'warning' }
      : { label: 'community', tone: 'accent' };
  }

  if (isQuickWin(pullRequest)) {
    return { label: 'quick win', tone: 'success' };
  }

  if (pullRequest.review.state === 'waiting') {
    return { label: 'needs reviewer', tone: 'warning' };
  }

  return { label: 'finish review', tone: 'accent' };
}

function reviewProgressSignal(pullRequest: PullRequestSummary): AttentionSignal | null {
  if (pullRequest.review.state === 'waiting') {
    return { label: 'no reviews', tone: 'warning' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return {
      label: formatCount(Math.max(1, pullRequest.review.changesRequestedCount), 'change request'),
      tone: 'danger',
    };
  }

  if (pullRequest.review.approvalCount > 0) {
    return { label: formatCount(pullRequest.review.approvalCount, 'approval'), tone: 'success' };
  }

  if (pullRequest.review.reviewerCount > 0) {
    return { label: `${formatCount(pullRequest.review.reviewerCount, 'reviewer')} · 0 approvals`, tone: 'accent' };
  }

  return null;
}

function updatedAgeMs(pullRequest: PullRequestSummary) {
  return ageMs(pullRequest.updatedAt);
}

function ageMs(value: string) {
  return Date.now() - new Date(value).getTime();
}

export function createTriageModel(
  pullRequest: PullRequestSummary,
  stats: TimelineStats,
  items: TimelineItem[],
  mergeableState?: MergeableState | null,
): TriageModel {
  const action = actionSignal(pullRequest);
  const signals = createAttentionSignals({ pullRequest, reason: '' })
    .filter((signal) => signal.label !== action.label)
    .slice(0, 6);

  if (pullRequest.checks?.state === 'success') {
    signals.push({ label: `CI ${formatCount(pullRequest.checks.successCount, 'check')} pass`, tone: 'success' });
  }

  if (mergeableState === 'dirty') {
    signals.push({ label: 'merge conflicts', tone: 'danger' });
  } else if (mergeableState === 'behind') {
    signals.push({ label: 'behind base', tone: 'warning' });
  } else if (mergeableState === 'blocked') {
    signals.push({ label: 'blocked by branch protection', tone: 'danger' });
  }

  if (stats.firstReviewDelayMs != null) {
    signals.push({ label: `first review ${formatDuration(stats.firstReviewDelayMs)}`, tone: 'accent' });
  }

  if (stats.longestHumanCommentGapMs != null) {
    signals.push({ label: `longest gap ${formatDuration(stats.longestHumanCommentGapMs)}`, tone: 'warning' });
  }

  return {
    action,
    why: triageWhy(pullRequest, stats, items, mergeableState),
    waitingOn: waitingOn(pullRequest, mergeableState),
    signals: dedupeSignals(signals).slice(0, 8),
    participants: createTriageParticipants(stats.developers),
    milestones: createSignalMilestones(pullRequest, items),
  };
}

function triageWhy(
  pullRequest: PullRequestSummary,
  stats: TimelineStats,
  items: TimelineItem[],
  mergeableState?: MergeableState | null,
) {
  if (pullRequest.draft) {
    return `Draft for ${formatAge(pullRequest.createdAt)}; keep it off the active review queue.`;
  }

  if (isChecksFailing(pullRequest)) {
    const count = pullRequest.checks?.failureCount ?? 0;
    const checksPart = count > 0 ? `${formatCount(count, 'failing check')}` : 'CI is failing';
    if (pullRequest.review.state === 'approved') {
      return `Approved, but ${checksPart} — author needs to fix CI before this can land.`;
    }
    return `${checksPart} on the latest commit — author needs to fix CI before reviewers can finish.`;
  }

  if (mergeableState === 'dirty') {
    return 'Merge conflicts on base — author needs to rebase before this can land.';
  }

  if (pullRequest.review.state === 'approved') {
    if (isChecksPending(pullRequest)) {
      return `Approved, but CI is still running on the head commit — wait for it to finish before merging.`;
    }
    return `Approved ${formatAge(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt)} ago and still open.`;
  }

  if (pullRequest.review.state === 'changes_requested') {
    return `Changes were requested; the author needs to close the loop.`;
  }

  if (pullRequest.review.state === 'waiting') {
    return `Open for ${formatAge(pullRequest.createdAt)} with no human review recorded.`;
  }

  const latestHuman = [...items]
    .filter((item) => !isBotAuthor(item.actor))
    .sort((first, second) => new Date(second.occurredAt).getTime() - new Date(first.occurredAt).getTime())[0];

  if (latestHuman) {
    return `Review started, but there is no approval yet; last human signal was ${formatAge(latestHuman.occurredAt)} ago.`;
  }

  return stats.reviewCount > 0
    ? `Review started, but there is no approval yet.`
    : `No clear human review signal yet.`;
}

function waitingOn(pullRequest: PullRequestSummary, mergeableState?: MergeableState | null) {
  if (mergeableState === 'dirty') {
    return `${pullRequest.author} (rebase)`;
  }

  if (isChecksFailing(pullRequest)) {
    return `${pullRequest.author} (CI)`;
  }

  if (pullRequest.draft || pullRequest.review.state === 'changes_requested') {
    return pullRequest.author;
  }

  if (pullRequest.review.state === 'approved') {
    return isChecksPending(pullRequest) ? 'CI' : 'maintainer';
  }

  if (pullRequest.review.state === 'waiting') {
    return 'reviewer';
  }

  return 'reviewers';
}

function participantSummary(developer: DeveloperStats) {
  const parts = [
    developer.commitCount > 0 ? formatCount(developer.commitCount, 'commit') : null,
    developer.reviewCount > 0 ? formatCount(developer.reviewCount, 'review') : null,
    developer.approvalCount > 0 ? formatCount(developer.approvalCount, 'approval') : null,
    developer.changesRequestedCount > 0 ? formatCount(developer.changesRequestedCount, 'change request') : null,
    developer.commentCount > 0 ? formatCount(developer.commentCount, 'comment') : null,
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' · ') : formatCount(developer.activityCount, 'activity', 'activities');
}

function createTriageParticipants(developers: DeveloperStats[]): TriageParticipant[] {
  const groups: { key: string; actors: string[]; developers: DeveloperStats[] }[] = [];

  for (const developer of developers) {
    const key = actorIdentityKey(developer.actor);
    const group = groups.find((candidate) => actorKeysMatch(candidate.key, key));
    if (group) {
      group.actors.push(developer.actor);
      group.developers.push(developer);
    } else {
      groups.push({ key, actors: [developer.actor], developers: [developer] });
    }
  }

  return groups
    .map((group) => {
      const developer = mergeDevelopers(group.developers, preferredParticipantName(group.actors));
      return {
        actor: developer.actor,
        role: developerRole(developer),
        summary: participantSummary(developer),
      };
    })
    .slice(0, 6);
}

function mergeDevelopers(developers: DeveloperStats[], actor: string): DeveloperStats {
  const ordered = developers.flatMap((developer) => [developer.firstActivityAt, developer.lastActivityAt]).sort();
  return {
    actor,
    activityCount: developers.reduce((total, developer) => total + developer.activityCount, 0),
    commitCount: developers.reduce((total, developer) => total + developer.commitCount, 0),
    commentCount: developers.reduce((total, developer) => total + developer.commentCount, 0),
    reviewCount: developers.reduce((total, developer) => total + developer.reviewCount, 0),
    approvalCount: developers.reduce((total, developer) => total + developer.approvalCount, 0),
    changesRequestedCount: developers.reduce((total, developer) => total + developer.changesRequestedCount, 0),
    firstActivityAt: ordered[0] ?? developers[0].firstActivityAt,
    lastActivityAt: ordered[ordered.length - 1] ?? developers[0].lastActivityAt,
  };
}

function actorIdentityKey(actor: string) {
  return actor.toLowerCase().replace(/[^a-z0-9]/g, '');
}

function actorKeysMatch(first: string, second: string) {
  return first === second
    || (Math.abs(first.length - second.length) <= 2 && (first.startsWith(second) || second.startsWith(first)));
}

function preferredParticipantName(actors: string[]) {
  const sorted = [...new Set(actors)].sort((first, second) =>
    Number(first.includes(' ')) - Number(second.includes(' '))
    || first.length - second.length
    || first.localeCompare(second));

  return sorted[0] ?? '';
}

function createSignalMilestones(pullRequest: PullRequestSummary, items: TimelineItem[]): SignalMilestone[] {
  const milestones: SignalMilestone[] = [
    {
      id: 'opened',
      occurredAt: pullRequest.createdAt,
      event: 'opened',
      title: `${pullRequest.author} opened the PR`,
      detail: `Open for ${formatAge(pullRequest.createdAt)}.`,
      tone: 'muted',
      url: pullRequest.htmlUrl,
    },
  ];

  const reviewRequests = items.filter(isReviewRequestEvent);
  if (reviewRequests.length > 0) {
    milestones.push({
      id: 'review-requests',
      occurredAt: reviewRequests[0].occurredAt,
      event: 'review requested',
      title: `${formatCount(reviewRequests.length, 'review request')}`,
      detail: summarizeReviewRequests(reviewRequests),
      tone: 'accent',
    });
  }

  const firstHumanComment = items.find((item) => item.event === 'commented' && !isBotAuthor(item.actor));
  if (firstHumanComment) {
    milestones.push({
      id: `first-human-comment-${firstHumanComment.id}`,
      occurredAt: firstHumanComment.occurredAt,
      event: 'human comment',
      title: `${firstHumanComment.actor} commented`,
      detail: 'First human discussion signal.',
      tone: 'accent',
      url: firstHumanComment.htmlUrl,
    });
  }

  for (const review of items.filter((item) => item.event === 'reviewed' && !isBotAuthor(item.actor))) {
    milestones.push({
      id: `review-${review.id}`,
      occurredAt: review.occurredAt,
      event: storyEventLabel(review),
      title: storyHeadline(review),
      detail: review.state ? `Review state: ${review.state.toLowerCase().replace(/_/g, ' ')}.` : undefined,
      tone: review.state?.toUpperCase() === 'APPROVED'
        ? 'success'
        : review.state?.toUpperCase() === 'CHANGES_REQUESTED'
          ? 'danger'
          : 'accent',
      url: review.htmlUrl,
    });
  }

  const latestCommit = [...items]
    .filter((item) => item.event === 'committed')
    .sort((first, second) => new Date(second.occurredAt).getTime() - new Date(first.occurredAt).getTime())[0];
  if (latestCommit) {
    milestones.push({
      id: `latest-commit-${latestCommit.id}`,
      occurredAt: latestCommit.occurredAt,
      event: 'latest commit',
      title: latestCommit.summary,
      detail: 'Most recent code-change signal.',
      tone: 'warning',
      url: latestCommit.htmlUrl,
    });
  }

  for (const gap of createQuietMilestones(items)) {
    milestones.push(gap);
  }

  const sorted = milestones.sort((first, second) => {
    if (first.id === 'opened') {
      return -1;
    }

    if (second.id === 'opened') {
      return 1;
    }

    return new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime();
  });

  return sorted.length > 12 ? [sorted[0], ...sorted.slice(-11)] : sorted;
}

function createQuietMilestones(items: TimelineItem[]): SignalMilestone[] {
  const ordered = [...items]
    .filter((item) => !isLowSignalTimelineEvent(item))
    .sort((first, second) => new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime());

  return ordered.flatMap((item, index) => {
    const next = ordered[index + 1];
    if (!next) {
      return [];
    }

    const gapMs = new Date(next.occurredAt).getTime() - new Date(item.occurredAt).getTime();
    if (gapMs < 2 * dayMs) {
      return [];
    }

    return [{
      id: `quiet-${item.id}-${next.id}`,
      occurredAt: item.occurredAt,
      event: 'quiet gap',
      title: `${formatDuration(gapMs)} with no high-signal activity`,
      detail: `Between ${storyEventLabel(item)} and ${storyEventLabel(next)}.`,
      tone: 'warning' as const,
    }];
  });
}

export function createTimelineStory(items: TimelineItem[]): TimelineStoryEntry[] {
  const entries: TimelineStoryEntry[] = [];
  let reviewRequests: TimelineItem[] = [];
  let hiddenEvents: TimelineItem[] = [];

  function flushSummaries() {
    if (reviewRequests.length > 0) {
      entries.push(createSummaryEntry(
        'review-requests',
        reviewRequests,
        `${formatCount(reviewRequests.length, 'review request')}`,
        summarizeReviewRequests(reviewRequests),
      ));
      reviewRequests = [];
    }

    if (hiddenEvents.length > 0) {
      entries.push(createSummaryEntry(
        'hidden-events',
        hiddenEvents,
        `${formatCount(hiddenEvents.length, 'low-signal event')} hidden`,
        summarizeEvents(hiddenEvents),
      ));
      hiddenEvents = [];
    }
  }

  for (const item of items) {
    if (isReviewRequestEvent(item)) {
      reviewRequests.push(item);
      continue;
    }

    if (isLowSignalTimelineEvent(item)) {
      hiddenEvents.push(item);
      continue;
    }

    flushSummaries();
    entries.push({
      kind: 'event',
      id: item.id,
      occurredAt: item.occurredAt,
      event: storyEventLabel(item),
      item,
    });
  }

  flushSummaries();
  return entries;
}

function createSummaryEntry(
  kind: string,
  items: TimelineItem[],
  summary: string,
  detail: string,
): TimelineStoryEntry {
  return {
    kind: 'summary',
    id: `${kind}-${items[0].id}`,
    occurredAt: items[0].occurredAt,
    event: 'summary',
    summary,
    detail,
    count: items.length,
  };
}

function isReviewRequestEvent(item: TimelineItem) {
  return item.event === 'review_requested';
}

function isLowSignalTimelineEvent(item: TimelineItem) {
  if (isBotAuthor(item.actor)) {
    return true;
  }

  if (item.event === 'committed' || item.event === 'reviewed' || item.event === 'merged' || item.event === 'closed') {
    return false;
  }

  if (item.event === 'commented') {
    return isBotAuthor(item.actor) || !item.body;
  }

  return item.event === 'labeled'
    || item.event === 'unlabeled'
    || item.event === 'assigned'
    || item.event === 'unassigned'
    || item.event === 'copilot_work_started'
    || item.event === 'cross-referenced';
}

function storyEventLabel(item: TimelineItem) {
  if (item.event === 'reviewed' && item.state) {
    return item.state.toLowerCase().replace(/_/g, ' ');
  }

  return item.event.replace(/_/g, ' ');
}

export function storyHeadline(item: TimelineItem) {
  if (item.event === 'reviewed') {
    const state = item.state?.toUpperCase();
    if (state === 'APPROVED') {
      return `${item.actor} approved`;
    }

    if (state === 'CHANGES_REQUESTED') {
      return `${item.actor} requested changes`;
    }

    if (state === 'COMMENTED') {
      return `${item.actor} left a review`;
    }
  }

  return item.summary;
}

function summarizeReviewRequests(items: TimelineItem[]) {
  return items
    .slice(0, 4)
    .map((item) => item.summary)
    .join(' · ');
}

function summarizeEvents(items: TimelineItem[]) {
  const counts = new Map<string, number>();
  for (const item of items) {
    const label = summaryEventLabel(item.event);
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }

  return [...counts.entries()]
    .sort((first, second) => second[1] - first[1])
    .slice(0, 3)
    .map(([label, count]) => `${count} ${label}`)
    .join(' · ');
}

function summaryEventLabel(event: string) {
  return event === 'review_requested'
    ? 'review requests'
    : event === 'commented'
      ? 'comments'
      : event.replace(/_/g, ' ');
}

export function createActivityModel(items: TimelineItem[]): ActivityModel | null {
  const ordered = [...items]
    .filter((item) => Number.isFinite(new Date(item.occurredAt).getTime()))
    .sort((first, second) => new Date(first.occurredAt).getTime() - new Date(second.occurredAt).getTime());

  if (ordered.length < 2) {
    return null;
  }

  const startMs = new Date(ordered[0].occurredAt).getTime();
  const endMs = new Date(ordered[ordered.length - 1].occurredAt).getTime();
  const spanMs = Math.max(hourMs, endMs - startMs);

  const markers = ordered.map((item) => {
    const occurredMs = new Date(item.occurredAt).getTime();
    return {
      id: item.id,
      left: clampPercent(((occurredMs - startMs) / spanMs) * 100),
      label: item.event.replace(/_/g, ' '),
      title: `${formatActivityTitle(item)} · ${formatRelative(item.occurredAt)}`,
      tone: activityTone(item),
    };
  });

  const gaps = ordered.flatMap((item, index) => {
    if (index === ordered.length - 1) {
      return [];
    }

    const currentMs = new Date(item.occurredAt).getTime();
    const nextMs = new Date(ordered[index + 1].occurredAt).getTime();
    const gapMs = nextMs - currentMs;

    if (gapMs < 2 * dayMs) {
      return [];
    }

    return [{
      id: `${item.id}-${ordered[index + 1].id}`,
      left: clampPercent(((currentMs - startMs) / spanMs) * 100),
      width: Math.max(3, clampPercent((gapMs / spanMs) * 100)),
      label: `${formatDuration(gapMs)} quiet`,
    }];
  });

  const humanEvents = ordered.filter((item) => !isBotAuthor(item.actor)).length;
  const botEvents = ordered.length - humanEvents;
  const reviews = markers.filter((marker) =>
    marker.tone === 'review' || marker.tone === 'approval' || marker.tone === 'changes').length;
  const commits = markers.filter((marker) => marker.tone === 'commit').length;
  const signals: AttentionSignal[] = [
    { label: `${formatCount(humanEvents, 'human event')}`, tone: humanEvents > 0 ? 'accent' : 'muted' },
    { label: `${formatCount(commits, 'commit')}`, tone: commits > 0 ? 'warning' : 'muted' },
    { label: `${formatCount(reviews, 'review')}`, tone: reviews > 0 ? 'accent' : 'muted' },
    { label: `${formatCount(botEvents, 'bot event')}`, tone: botEvents > humanEvents ? 'warning' : 'muted' },
  ];

  if (gaps.length > 0) {
    signals.push({ label: `${formatCount(gaps.length, 'quiet gap')}`, tone: 'warning' });
  }

  return {
    markers,
    gaps,
    signals,
    startLabel: formatDateShort(ordered[0].occurredAt),
    endLabel: formatDateShort(ordered[ordered.length - 1].occurredAt),
  };
}

function activityTone(item: TimelineItem): ActivityMarker['tone'] {
  if (isBotAuthor(item.actor)) {
    return 'bot';
  }

  if (item.event === 'committed') {
    return 'commit';
  }

  if (item.event === 'reviewed') {
    if (item.state?.toUpperCase() === 'APPROVED') {
      return 'approval';
    }

    if (item.state?.toUpperCase() === 'CHANGES_REQUESTED') {
      return 'changes';
    }

    return 'review';
  }

  if (item.event === 'commented') {
    return 'comment';
  }

  return 'event';
}

function formatActivityTitle(item: TimelineItem) {
  const event = item.event.replace(/_/g, ' ');
  const state = item.state ? ` (${item.state.toLowerCase().replace(/_/g, ' ')})` : '';
  return `${item.actor} ${event}${state}`;
}

function clampPercent(value: number) {
  return Math.min(100, Math.max(0, value));
}

function developerRole(developer: DeveloperStats) {
  if (developer.approvalCount > 0) {
    return 'Approver';
  }

  if (developer.changesRequestedCount > 0) {
    return 'Unblocker';
  }

  if (developer.reviewCount >= developer.commentCount && developer.reviewCount > 0) {
    return 'Reviewer';
  }

  if (developer.commentCount > developer.commitCount) {
    return 'Commentator';
  }

  if (developer.commitCount > 0) {
    return 'Builder';
  }

  return 'Participant';
}

function isIdle(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.updatedAt).getTime() >= 2 * dayMs;
}

function isBotAuthor(author: string) {
  const normalized = author.toLowerCase();
  return normalized.endsWith('[bot]')
    || normalized.includes('bot')
    || normalized === 'copilot'
    || normalized === 'github-actions';
}
