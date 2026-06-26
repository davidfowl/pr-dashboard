import { describe, expect, it } from 'vitest';
import type { AttentionBucket, CheckState, PullRequestSummary } from '../../types';
import { computeFocusItems } from './focusQueue';

function checks(state: CheckState): PullRequestSummary['checks'] {
  return {
    state,
    totalCount: state === 'none' || state === 'unknown' ? 0 : 1,
    successCount: state === 'success' ? 1 : 0,
    failureCount: state === 'failure' ? 1 : 0,
    pendingCount: state === 'pending' ? 1 : 0,
    neutralCount: 0,
    skippedCount: 0,
    completedAt: state === 'success' ? new Date().toISOString() : null,
    failingChecks: [],
  };
}

function pr(
  number: number,
  checksState: CheckState,
  reviewOverrides: Partial<PullRequestSummary['review']> = {},
): PullRequestSummary {
  const now = new Date().toISOString();
  return {
    repository: 'example/repo',
    number,
    title: `PR ${number}`,
    state: 'open',
    draft: false,
    author: 'octocat',
    htmlUrl: `https://github.com/example/repo/pull/${number}`,
    createdAt: now,
    updatedAt: now,
    fetchedAt: now,
    labels: [],
    requestedReviewers: [],
    linkedIssues: [],
    commitCount: 1,
    additions: 10,
    deletions: 2,
    changedFiles: 1,
    mergeableState: null,
    review: {
      state: 'waiting',
      reviewerCount: 0,
      approvalCount: 0,
      changesRequestedCount: 0,
      commentedReviewCount: 0,
      unresolvedThreadCount: 0,
      requiresConversationResolution: false,
      ...reviewOverrides,
    },
    checks: checks(checksState),
  };
}

function bucket(label: string, prs: PullRequestSummary[]): AttentionBucket {
  return {
    label,
    summary: label,
    tone: 'accent',
    metric: label,
    items: prs.map((pullRequest) => ({ pullRequest, reason: label })),
  };
}

describe('computeFocusItems', () => {
  it('excludes PRs with failing checks from the Needs attention focus queue', () => {
    const failing = pr(1, 'failure');
    const pending = pr(2, 'pending');
    const green = pr(3, 'success');

    const buckets: AttentionBucket[] = [
      bucket('Needs review', [failing, pending, green]),
      // Failing PRs also populate the dedicated CI failing bucket in real data.
      bucket('CI failing', [failing]),
    ];

    const focusNumbers = computeFocusItems(buckets).map((item) => item.pullRequest.number);

    expect(focusNumbers).not.toContain(1);
    expect(focusNumbers).toContain(2);
    expect(focusNumbers).toContain(3);
  });

  it('excludes a failing PR even when it qualifies for a high-priority lane', () => {
    const failing = pr(20, 'failure');
    const green = pr(21, 'success');

    // Both PRs sit only in a high-priority lane (not in the CI failing bucket), so the
    // checks filter — not the label exclusion — is the only thing that can drop the failing one.
    const buckets: AttentionBucket[] = [bucket('Ready to merge', [failing, green])];

    const focusNumbers = computeFocusItems(buckets).map((item) => item.pullRequest.number);

    expect(focusNumbers).not.toContain(20);
    expect(focusNumbers).toContain(21);
  });

  it('does not surface PRs solely from the CI failing lane', () => {
    const failing = pr(10, 'failure');
    const buckets: AttentionBucket[] = [bucket('CI failing', [failing])];

    expect(computeFocusItems(buckets)).toHaveLength(0);
  });

  it('excludes a PR with unresolved feedback even when it also qualifies for a high-priority lane', () => {
    // A reviewed/approved PR with open review threads lands in "Unresolved feedback" and can also
    // appear in a reviewer lane (e.g. Re-review needed / Ready to merge). It is author-blocked, so
    // it must be kept out of Needs attention regardless of the other lane.
    const blocked = pr(30, 'success', { unresolvedThreadCount: 2 });
    const clean = pr(31, 'success');

    const buckets: AttentionBucket[] = [
      bucket('Re-review needed', [blocked, clean]),
      bucket('Unresolved feedback', [blocked]),
    ];

    const focusNumbers = computeFocusItems(buckets).map((item) => item.pullRequest.number);

    expect(focusNumbers).not.toContain(30);
    expect(focusNumbers).toContain(31);
  });

  it('does not surface PRs solely from the Unresolved feedback lane', () => {
    const blocked = pr(40, 'success', { unresolvedThreadCount: 2 });
    const buckets: AttentionBucket[] = [bucket('Unresolved feedback', [blocked])];

    expect(computeFocusItems(buckets)).toHaveLength(0);
  });
});
