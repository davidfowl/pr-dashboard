import { dayMs } from '../../constants';
import type { AttentionBucket, AttentionItem, PullRequestSummary } from '../../types';
import { isChecksFailing } from '../../utils/models';

export type FocusItem = AttentionItem & {
  bucketLabel: string;
  bucketTone: AttentionBucket['tone'];
};

const focusAgeLimitMs = 14 * dayMs;
const excludedFocusBucketLabels = new Set(['Stalled', 'Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Copilot feedback', 'Merge conflicts', 'CI failing']);
const disqualifyingFocusBucketLabels = new Set(['Draft', 'Docs', 'Community Toolkit', 'Bots / automation', 'Community', 'Copilot feedback', 'Merge conflicts']);
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

function isWithinFocusAgeLimit(pullRequest: PullRequestSummary) {
  return Date.now() - new Date(pullRequest.createdAt).getTime() <= focusAgeLimitMs;
}

// Builds the top "Needs attention" focus queue: pick each PR from its highest-priority lane,
// keep only recent PRs, and exclude any PR with failing CI so it reappears once checks are green.
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
    .filter((item) => isWithinFocusAgeLimit(item.pullRequest))
    .filter((item) => !isChecksFailing(item.pullRequest));
}

function blockedFocusKeys(buckets: AttentionBucket[]) {
  return new Set(
    buckets
      .filter((bucket) => disqualifyingFocusBucketLabels.has(bucket.label))
      .flatMap((bucket) => bucket.items.map((item) => pullRequestKey(item.pullRequest))),
  );
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

function pullRequestKey(pullRequest: PullRequestSummary) {
  return `${pullRequest.repository.toLowerCase()}#${pullRequest.number}`;
}
