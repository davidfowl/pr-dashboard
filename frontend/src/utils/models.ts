import { dayMs, hourMs } from '../constants';
import type {
  ActivityMarker,
  ActivityModel,
  AttentionBucket,
  AttentionItem,
  AttentionSignal,
  DeveloperStats,
  PickItem,
  PullRequestSummary,
  SignalMilestone,
  TeamMetrics,
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

function readinessForPullRequest(pullRequest: PullRequestSummary) {
  const updatedAgeMs = Date.now() - new Date(pullRequest.updatedAt).getTime();
  let readiness = 50;

  if (pullRequest.review.state === 'approved') {
    readiness += 25;
  }

  if (pullRequest.review.state === 'reviewed') {
    readiness += 10;
  }

  if (pullRequest.review.state === 'changes_requested') {
    readiness -= 20;
  }

  if (pullRequest.review.state === 'waiting') {
    readiness -= 10;
  }

  if (pullRequest.draft) {
    readiness -= 15;
  }

  if (updatedAgeMs <= 12 * hourMs) {
    readiness += 10;
  } else if (updatedAgeMs >= 2 * dayMs) {
    readiness -= 15;
  }

  if (pullRequest.review.reviewerCount >= 3) {
    readiness += 10;
  }

  if (pullRequest.review.approvalCount >= 2) {
    readiness += 10;
  }

  return Math.max(0, Math.min(100, readiness));
}

export function createTeamMetrics(pullRequests: PullRequestSummary[]): TeamMetrics {
  const readiness = pullRequests.map(readinessForPullRequest);
  const reviewed = pullRequests.filter((pullRequest) => pullRequest.review.state !== 'waiting').length;
  return {
    averageReadiness:
      readiness.length === 0 ? 0 : Math.round(readiness.reduce((total, value) => total + value, 0) / readiness.length),
    reviewCoverage: pullRequests.length === 0 ? 0 : Math.round((reviewed / pullRequests.length) * 100),
    waiting: pullRequests.filter((pullRequest) => pullRequest.review.state === 'waiting').length,
    idle: pullRequests.filter(
      (pullRequest) =>
        pullRequest.state === 'open'
        && Date.now() - new Date(pullRequest.updatedAt).getTime() >= 2 * dayMs,
    ).length,
  };
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
    .slice(0, 5);
}

function createPersonalPick(pullRequest: PullRequestSummary, login: string): PickItem | null {
  if (pullRequest.requestedReviewers.some((reviewer) => sameLogin(reviewer, login))) {
    return {
      pullRequest,
      action: 'Review this',
      reason: `Review requested from you · ${pickReason(pullRequest)}`,
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

export function createForMeSignals(item: PickItem): AttentionSignal[] {
  const firstReason = item.reason.split(' · ')[0];
  const signals = createAttentionSignals({ pullRequest: item.pullRequest, reason: item.reason })
    .filter((signal) => signal.label !== item.action && signal.label !== actionSignal(item.pullRequest).label);

  return dedupeSignals([
    { label: firstReason, tone: 'accent' },
    ...signals,
  ]).slice(0, 7);
}

function pickScore(item: PickItem) {
  let score = item.personal ? 1000 : 0;
  if (item.action === 'Review this') score += 90;
  if (item.action === 'Merge this' || item.action === 'Finish this') score += 80;
  if (item.action === 'Respond here') score += 75;
  if (item.action === 'Finish review') score += 65;
  if (item.action === 'Unstick this') score += 45;
  if (item.pullRequest.review.state === 'changes_requested') score += 30;
  if (item.pullRequest.review.state === 'waiting') score += 45;
  if (item.pullRequest.review.state === 'reviewed') score += 25;
  if (item.pullRequest.review.state === 'approved') score += 5;
  if (isIdle(item.pullRequest)) score += 15;
  if (isBotAuthor(item.pullRequest.author)) score -= 120;
  return score
    + Math.min(80, Math.floor(ageMs(item.pullRequest.createdAt) / dayMs) * 4)
    + Math.min(20, Math.floor(updatedAgeMs(item.pullRequest) / dayMs));
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
      label: 'Ready to merge',
      summary: 'Approved and waiting on a maintainer or author to finish.',
      tone: 'success',
      metric: 'merge rate ↑',
      items: [],
    },
    {
      label: 'Needs review',
      summary: 'Ready PRs that have not had a human review yet.',
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
      summary: 'Reviewed or discussed, then went quiet.',
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
    bucketsByLabel.get(reviewBucketLabel(pullRequest))?.items.push({
      pullRequest,
      reason: reviewSignal(pullRequest),
    });
  }

  for (const bucket of buckets) {
    bucket.items.sort((first, second) => compareAttentionItems(bucket.label, first, second));
  }

  return buckets.filter((bucket) => bucket.items.length > 0);
}

function compareAttentionItems(bucket: string, first: AttentionItem, second: AttentionItem) {
  const priorityDiff = queuePriority(bucket, second.pullRequest) - queuePriority(bucket, first.pullRequest);
  if (priorityDiff !== 0) {
    return priorityDiff;
  }

  return new Date(first.pullRequest.createdAt).getTime() - new Date(second.pullRequest.createdAt).getTime();
}

function queuePriority(bucket: string, pullRequest: PullRequestSummary) {
  const openDays = Math.floor(ageMs(pullRequest.createdAt) / dayMs);
  const idleDays = Math.floor(updatedAgeMs(pullRequest) / dayMs);
  const botPenalty = isBotAuthor(pullRequest.author) ? 120 : 0;

  switch (bucket) {
    case 'Ready to merge':
      return 200 + openDays * 4 + ageDays(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt) * 3 - botPenalty;
    case 'Needs review':
      return 180 + openDays * 6 + idleDays - botPenalty;
    case 'Review started':
      return 160 + openDays * 4 + pullRequest.review.reviewerCount * 8 + pullRequest.review.commentedReviewCount - botPenalty;
    case 'Author response':
      return 220 + openDays * 3 + ageDays(pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt) * 6 - botPenalty;
    case 'Stalled':
      return 170 + idleDays * 8 + openDays * 2 - botPenalty;
    case 'Draft':
      return openDays - botPenalty;
    default:
      return openDays - botPenalty;
  }
}

function reviewBucketLabel(pullRequest: PullRequestSummary) {
  if (pullRequest.draft) {
    return 'Draft';
  }

  if (pullRequest.review.state === 'approved') {
    return 'Ready to merge';
  }

  if (pullRequest.review.state === 'waiting') {
    return 'Needs review';
  }

  if (pullRequest.review.state === 'changes_requested') {
    return 'Author response';
  }

  if (isIdle(pullRequest)) {
    return 'Stalled';
  }

  return 'Review started';
}

function reviewSignal(pullRequest: PullRequestSummary) {
  if (pullRequest.draft) {
    return 'Draft';
  }

  if (pullRequest.review.state === 'changes_requested') {
    return 'Changes requested';
  }

  if (pullRequest.review.state === 'approved') {
    return `${formatCount(pullRequest.review.approvalCount, 'approval')}`;
  }

  if (pullRequest.review.state === 'waiting') {
    return 'No reviews';
  }

  if (isIdle(pullRequest)) {
    return `Idle ${formatRelative(pullRequest.updatedAt)}`;
  }

  return formatCount(pullRequest.review.reviewerCount, 'reviewer');
}

export function createAttentionSignals(item: AttentionItem): AttentionSignal[] {
  const pullRequest = item.pullRequest;
  const signals: AttentionSignal[] = [actionSignal(pullRequest)];

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

  for (const label of pullRequest.labels.slice(0, 2)) {
    signals.push({ label, tone: 'accent' });
  }

  if (isBotAuthor(pullRequest.author)) {
    signals.push({ label: 'bot', tone: 'accent' });
  }

  return signals.slice(0, 7);
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
  if (pullRequest.draft) {
    return { label: 'draft', tone: 'muted' };
  }

  if (pullRequest.review.state === 'changes_requested') {
    return { label: 'author fix', tone: 'danger' };
  }

  if (pullRequest.review.state === 'approved') {
    return { label: 'merge', tone: 'success' };
  }

  if (pullRequest.review.state === 'waiting') {
    return { label: 'needs reviewer', tone: 'warning' };
  }

  if (isIdle(pullRequest)) {
    return { label: 'unstick', tone: 'warning' };
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

function ageDays(value: string) {
  return Math.floor(ageMs(value) / dayMs);
}

export function createTriageModel(
  pullRequest: PullRequestSummary,
  stats: TimelineStats,
  items: TimelineItem[],
): TriageModel {
  const action = actionSignal(pullRequest);
  const signals = createAttentionSignals({ pullRequest, reason: '' })
    .filter((signal) => signal.label !== action.label)
    .slice(0, 6);

  if (stats.firstReviewDelayMs != null) {
    signals.push({ label: `first review ${formatDuration(stats.firstReviewDelayMs)}`, tone: 'accent' });
  }

  if (stats.longestHumanCommentGapMs != null) {
    signals.push({ label: `longest gap ${formatDuration(stats.longestHumanCommentGapMs)}`, tone: 'warning' });
  }

  return {
    action,
    why: triageWhy(pullRequest, stats, items),
    waitingOn: waitingOn(pullRequest),
    signals: dedupeSignals(signals).slice(0, 8),
    participants: createTriageParticipants(stats.developers),
    milestones: createSignalMilestones(pullRequest, items),
  };
}

function triageWhy(pullRequest: PullRequestSummary, stats: TimelineStats, items: TimelineItem[]) {
  if (pullRequest.draft) {
    return `Draft for ${formatAge(pullRequest.createdAt)}; keep it off the active review queue.`;
  }

  if (pullRequest.review.state === 'approved') {
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

function waitingOn(pullRequest: PullRequestSummary) {
  if (pullRequest.draft || pullRequest.review.state === 'changes_requested') {
    return pullRequest.author;
  }

  if (pullRequest.review.state === 'approved') {
    return 'maintainer';
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

function dedupeSignals(signals: AttentionSignal[]) {
  const seen = new Set<string>();
  return signals.filter((signal) => {
    if (seen.has(signal.label)) {
      return false;
    }

    seen.add(signal.label);
    return true;
  });
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
