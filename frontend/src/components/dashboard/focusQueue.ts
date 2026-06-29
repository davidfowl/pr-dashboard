import type { AttentionBucket, AttentionItem, AttentionSignal, PullRequestSummary } from '../../types';
import {
  actorIdentityKey,
  hasMergeConflicts,
  hasNeedsAuthorActionLabel,
  isChecksFailing,
  isPullRequestWithinFocusAgeLimit,
} from '../../utils/models';

export type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

export type FocusExclusionReason = {
  label: string;
  detail: string;
  tone: AttentionSignal['tone'];
};

export type FocusExclusionItem = {
  pullRequest: PullRequestSummary;
  reason: FocusExclusionReason;
  bucketLabels: string[];
};

const excludedFocusBucketLabels = new Set(['Stalled', 'Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Unresolved feedback', 'Merge conflicts', 'CI failing']);
const disqualifyingFocusBucketLabels = new Set(['Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Unresolved feedback', 'Merge conflicts']);
const specializedFocusBucketLabels = new Set(['Docs', 'Community Toolkit', 'Bots / automation', 'Community']);
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
    .filter((item) => !isChecksFailing(item.pullRequest));
}

export function computeFocusExclusionItems(
  pullRequests: PullRequestSummary[],
  attentionBuckets: AttentionBucket[],
  login?: string,
): FocusExclusionItem[] {
  if (!login) {
    return [];
  }

  const loginKey = actorIdentityKey(login);
  const focusKeys = new Set(computeFocusItems(attentionBuckets).map((item) => pullRequestKey(item.pullRequest)));
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
  return new Set(
    buckets
      .filter((bucket) => disqualifyingFocusBucketLabels.has(bucket.label))
      .flatMap((bucket) => bucket.items.map((item) => pullRequestKey(item.pullRequest))),
  );
}

function createBucketLabelsByPullRequest(buckets: AttentionBucket[]) {
  const labelsByPullRequest = new Map<string, string[]>();

  for (const bucket of buckets) {
    for (const item of bucket.items) {
      const key = pullRequestKey(item.pullRequest);
      labelsByPullRequest.set(key, [...(labelsByPullRequest.get(key) ?? []), bucket.label]);
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
      label: 'CI failing',
      detail: 'Failing checks keep it out until CI is green again.',
      tone: 'danger',
    };
  }

  if (hasMergeConflicts(pullRequest)) {
    return {
      label: 'Merge conflicts',
      detail: 'The author needs to rebase before reviewers or maintainers can finish it.',
      tone: 'danger',
    };
  }

  if (pullRequest.review.unresolvedThreadCount > 0) {
    return {
      label: 'Unresolved feedback',
      detail: 'Open review threads make it author-blocked instead of reviewer-blocked.',
      tone: 'danger',
    };
  }

  if (hasNeedsAuthorActionLabel(pullRequest)) {
    return {
      label: 'Held by label',
      detail: 'A do-not-merge or needs-author-action label keeps it out of the focused queue.',
      tone: 'danger',
    };
  }

  const specializedBucketLabel = bucketLabels.find((label) => specializedFocusBucketLabels.has(label));
  if (specializedBucketLabel) {
    return {
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
      label: 'Stale activity',
      detail: 'Its actionable lane has not had fresh activity in the last 14 days.',
      tone: 'warning',
    };
  }

  if (bucketLabels.includes('Stalled')) {
    return {
      label: 'Stalled only',
      detail: 'It has gone quiet and has no fresher actionable lane for Needs attention.',
      tone: 'warning',
    };
  }

  return {
    label: 'Outside queue',
    detail: 'It does not currently match a focused, actionable Needs attention lane.',
    tone: 'muted',
  };
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
  return focusExclusionReasonRank(first.reason.label) - focusExclusionReasonRank(second.reason.label)
    || updatedTime(second.pullRequest) - updatedTime(first.pullRequest)
    || first.pullRequest.repository.localeCompare(second.pullRequest.repository)
    || first.pullRequest.number - second.pullRequest.number;
}

function focusExclusionReasonRank(label: string) {
  if (label === 'CI failing') return 0;
  if (label === 'Merge conflicts') return 1;
  if (label === 'Unresolved feedback') return 2;
  if (label === 'Held by label') return 3;
  if (label === 'Stale activity') return 4;
  if (label.endsWith(' lane')) return 5;
  if (label === 'Stalled only') return 6;
  return 7;
}

function updatedTime(pullRequest: PullRequestSummary) {
  return new Date(pullRequest.updatedAt).getTime();
}

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository.toLowerCase()}#${pullRequest.number}`;
}
