import type { AttentionBucket, AttentionItem, AttentionSignal, PullRequestSummary } from '../../types';
import {
  actorIdentityKey,
  hasMergeConflicts,
  hasNeedsAuthorActionLabel,
  isAgedOutCommunityPullRequest,
  isChecksFailing,
  isCommunityPullRequest,
  isPullRequestWithinFocusAgeLimit,
} from '../../utils/models';

export type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

export type CommunityQueueItem = AttentionItem & {
  bucketLabel: 'Community';
  bucketTone: 'accent';
};

type FocusExclusionReasonKind =
  | 'ci-failing'
  | 'merge-conflicts'
  | 'unresolved-feedback'
  | 'held-by-label'
  | 'author-response'
  | 'stale-activity'
  | 'community-list'
  | 'specialized-lane'
  | 'stalled-only'
  | 'outside-queue';

export type FocusExclusionReason = {
  kind: FocusExclusionReasonKind;
  label: string;
  detail: string;
  tone: AttentionSignal['tone'];
};

export type FocusExclusionItem = {
  pullRequest: PullRequestSummary;
  reason: FocusExclusionReason;
  bucketLabels: string[];
};

const excludedFocusBucketLabels = new Set(['Stalled', 'Draft', 'My draft PRs', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Aged out community', 'Unresolved feedback', 'Merge conflicts', 'CI failing', 'Author response']);
const disqualifyingFocusBucketLabels = new Set(['Draft', 'My draft PRs', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Aged out community', 'Unresolved feedback', 'Merge conflicts']);
const specializedFocusBucketLabels = new Set(['Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Aged out community']);
const focusBucketRanks = new Map([
  ['Regression', -2],
  ['Approved but aging', 0],
  ['Re-review needed', 1],
  ['Ready to merge', 2],
  ['Author response', 3],
  ['Needs review', 4],
  ['Quick wins', 5],
  ['Review started', 6],
]);

// Builds the top "Needs attention" focus queue: pick each PR from its highest-priority lane,
// keep only PRs with recent lane activity, and exclude any PR with failing CI so it reappears
// once checks are green.
export function computeFocusItems(attentionBuckets: AttentionBucket[]): FocusItem[] {
  const blockedKeys = blockedFocusKeys(attentionBuckets);
  return dedupeFocusItems(
    attentionBuckets
      .filter((bucket) => !excludedFocusBucketLabels.has(bucket.label))
      .flatMap((bucket) =>
        bucket.items.map((item) => ({
          ...item,
          bucketLabel: bucket.label,
          bucketTone: bucket.tone,
        }))),
    blockedKeys,
  )
    .filter((item) => isPullRequestWithinFocusAgeLimit(item.pullRequest, item.bucketLabel))
    .filter((item) => !isCommunityPullRequest(item.pullRequest))
    .filter((item) => !isChecksFailing(item.pullRequest));
}

export function computeCommunityItems(pullRequests: PullRequestSummary[]): CommunityQueueItem[] {
  return pullRequests
    .filter((pullRequest) =>
      pullRequest.state === 'open'
      && !pullRequest.draft
      && !hasNeedsAuthorActionLabel(pullRequest)
      && isCommunityPullRequest(pullRequest)
      && !isAgedOutCommunityPullRequest(pullRequest))
    .map((pullRequest): CommunityQueueItem => ({
      pullRequest,
      reason: 'Community',
      bucketLabel: 'Community',
      bucketTone: 'accent',
    }))
    .sort((first, second) =>
      updatedTime(second.pullRequest) - updatedTime(first.pullRequest)
      || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
      || first.pullRequest.number - second.pullRequest.number);
}

export function computeFocusExclusionItems(
  pullRequests: PullRequestSummary[],
  attentionBuckets: AttentionBucket[],
  focusItems: FocusItem[],
  login?: string,
): FocusExclusionItem[] {
  if (!login) {
    return [];
  }

  const loginKey = actorIdentityKey(login);
  const focusKeys = new Set(focusItems.map((item) => pullRequestKey(item.pullRequest)));
  const bucketLabelsByPullRequest = createBucketLabelsByPullRequest(attentionBuckets);

  return pullRequests
    .filter((pullRequest) =>
      pullRequest.state === 'open'
      && !pullRequest.draft
      && actorIdentityKey(pullRequest.author) === loginKey
      && !focusKeys.has(pullRequestKey(pullRequest)))
    .map((pullRequest) => {
      const bucketLabels = bucketLabelsByPullRequest.get(pullRequestKey(pullRequest)) ?? [];
      return {
        pullRequest,
        reason: focusExclusionReason(pullRequest, bucketLabels),
        bucketLabels,
      };
    })
    .sort(compareFocusExclusionItems);
}

function blockedFocusKeys(buckets: AttentionBucket[]) {
  const blockedKeys = new Set(
    buckets
      .filter((bucket) => disqualifyingFocusBucketLabels.has(bucket.label))
      .flatMap((bucket) => bucket.items.map((item) => pullRequestKey(item.pullRequest))),
  );
  const bucketLabelsByPullRequest = createBucketLabelsByPullRequest(buckets);

  for (const [key, bucketLabels] of bucketLabelsByPullRequest) {
    if (isWaitingOnAuthor(bucketLabels)) {
      blockedKeys.add(key);
    }
  }

  return blockedKeys;
}

function createBucketLabelsByPullRequest(buckets: AttentionBucket[]) {
  const labelsByPullRequest = new Map<string, string[]>();

  for (const bucket of buckets) {
    for (const item of bucket.items) {
      const key = pullRequestKey(item.pullRequest);
      const labels = labelsByPullRequest.get(key);
      if (labels) {
        labels.push(bucket.label);
      } else {
        labelsByPullRequest.set(key, [bucket.label]);
      }
    }
  }

  return labelsByPullRequest;
}

function focusExclusionReason(
  pullRequest: PullRequestSummary,
  bucketLabels: string[],
): FocusExclusionReason {
  if (isChecksFailing(pullRequest)) {
    return {
      kind: 'ci-failing',
      label: 'CI failing',
      detail: 'Failing checks keep it out until CI is green again.',
      tone: 'danger',
    };
  }

  if (hasMergeConflicts(pullRequest)) {
    return {
      kind: 'merge-conflicts',
      label: 'Merge conflicts',
      detail: 'The author needs to rebase before reviewers or maintainers can finish it.',
      tone: 'danger',
    };
  }

  if (pullRequest.review.unresolvedThreadCount > 0) {
    return {
      kind: 'unresolved-feedback',
      label: 'Unresolved feedback',
      detail: 'Open review threads make it author-blocked instead of reviewer-blocked.',
      tone: 'danger',
    };
  }

  if (hasNeedsAuthorActionLabel(pullRequest)) {
    return {
      kind: 'held-by-label',
      label: 'Held by label',
      detail: 'A do-not-merge or needs-author-action label keeps it out of the focused queue.',
      tone: 'danger',
    };
  }

  if (isWaitingOnAuthor(bucketLabels)) {
    return {
      kind: 'author-response',
      label: 'Author response',
      detail: 'Changes were requested, so this is waiting on the author rather than the focused queue.',
      tone: 'danger',
    };
  }

  if (isCommunityPullRequest(pullRequest) && !isAgedOutCommunityPullRequest(pullRequest)) {
    return {
      kind: 'community-list',
      label: 'Community list',
      detail: 'Recently active external-contributor PRs show in the Community list instead of Needs attention.',
      tone: 'accent',
    };
  }

  const specializedBucketLabel = bucketLabels.find((label) => specializedFocusBucketLabels.has(label));
  if (specializedBucketLabel) {
    return {
      kind: 'specialized-lane',
      label: `${specializedBucketLabel} lane`,
      detail: `It is routed to the ${specializedBucketLabel} lane instead of Needs attention.`,
      tone: 'accent',
    };
  }

  const focusCandidateBucketLabel = bestFocusCandidateBucketLabel(bucketLabels);
  if (
    focusCandidateBucketLabel
    && !isPullRequestWithinFocusAgeLimit(pullRequest, focusCandidateBucketLabel)
  ) {
    return {
      kind: 'stale-activity',
      label: 'Stale activity',
      detail: 'Its actionable lane has not had fresh activity in the last 14 days.',
      tone: 'warning',
    };
  }

  if (bucketLabels.includes('Stalled')) {
    return {
      kind: 'stalled-only',
      label: 'Stalled only',
      detail: 'It has gone quiet and has no fresher actionable lane for Needs attention.',
      tone: 'warning',
    };
  }

  return {
    kind: 'outside-queue',
    label: 'Outside queue',
    detail: 'It does not currently match a focused, actionable Needs attention lane.',
    tone: 'muted',
  };
}

function isWaitingOnAuthor(bucketLabels: string[]) {
  return bucketLabels.includes('Author response')
    && !bucketLabels.includes('Re-review needed');
}

function bestFocusCandidateBucketLabel(bucketLabels: string[]) {
  return [...bucketLabels]
    .filter((label) => !excludedFocusBucketLabels.has(label))
    .sort((first, second) => focusBucketRank(first) - focusBucketRank(second))[0] ?? null;
}

function dedupeFocusItems(items: FocusItem[], blockedKeys: Set<string>) {
  const itemsByPullRequest = new Map<string, FocusItem>();

  for (const item of items) {
    const key = pullRequestKey(item.pullRequest);
    if (blockedKeys.has(key)) {
      continue;
    }

    const existing = itemsByPullRequest.get(key);
    if (!existing || focusBucketRank(item.bucketLabel) < focusBucketRank(existing.bucketLabel)) {
      itemsByPullRequest.set(key, item);
    }
  }

  return [...itemsByPullRequest.values()];
}

function focusBucketRank(label: string) {
  return focusBucketRanks.get(label) ?? Number.MAX_SAFE_INTEGER;
}

function compareFocusExclusionItems(first: FocusExclusionItem, second: FocusExclusionItem) {
  return focusExclusionReasonRank(first.reason.kind) - focusExclusionReasonRank(second.reason.kind)
    || updatedTime(second.pullRequest) - updatedTime(first.pullRequest)
    || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
    || first.pullRequest.number - second.pullRequest.number;
}

function focusExclusionReasonRank(kind: FocusExclusionReasonKind) {
  switch (kind) {
    case 'ci-failing':
      return 0;
    case 'merge-conflicts':
      return 1;
    case 'unresolved-feedback':
      return 2;
    case 'held-by-label':
      return 3;
    case 'author-response':
      return 4;
    case 'stale-activity':
      return 5;
    case 'community-list':
      return 6;
    case 'specialized-lane':
      return 7;
    case 'stalled-only':
      return 8;
    case 'outside-queue':
      return 9;
  }
}

function updatedTime(pullRequest: PullRequestSummary) {
  return new Date(pullRequest.updatedAt).getTime();
}

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository.toLowerCase()}#${pullRequest.number}`;
}
