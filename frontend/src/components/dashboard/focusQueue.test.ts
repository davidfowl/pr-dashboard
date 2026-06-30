import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AttentionBucket, CheckState, PullRequestSummary } from '../../types';
import { computeCommunityItems, computeFocusExclusionItems, computeFocusItems } from './focusQueue';

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
    author: 'davidfowl',
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
  afterEach(() => {
    vi.useRealTimers();
  });

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

  it('does not surface PRs solely from the Author response lane', () => {
    const blocked = pr(42, 'success', { state: 'changes_requested', changesRequestedCount: 1 });
    const buckets: AttentionBucket[] = [bucket('Author response', [blocked])];

    expect(computeFocusItems(buckets)).toHaveLength(0);
  });

  it('excludes author-response PRs even when they also qualify for high-priority signal lanes', () => {
    const blocked = pr(43, 'success', { state: 'changes_requested', changesRequestedCount: 1 });
    const buckets: AttentionBucket[] = [
      bucket('Regression', [blocked]),
      bucket('Author response', [blocked]),
    ];

    expect(computeFocusItems(buckets)).toHaveLength(0);
  });

  it('keeps changes-requested PRs in Needs attention after the author pushes a response', () => {
    const responded = {
      ...pr(44, 'success', {
        state: 'changes_requested',
        changesRequestedCount: 1,
        lastReviewedAt: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
      }),
      lastCommitAt: new Date().toISOString(),
    };
    const buckets: AttentionBucket[] = [
      bucket('Author response', [responded]),
      bucket('Re-review needed', [responded]),
    ];

    expect(computeFocusItems(buckets).map((item) => item.bucketLabel)).toEqual(['Re-review needed']);
  });

  it('excludes recent community PRs even when they qualify for an actionable lane', () => {
    const community = { ...pr(41, 'success'), author: 'external-contributor' };
    const buckets: AttentionBucket[] = [
      bucket('Ready to merge', [community]),
    ];

    expect(computeFocusItems(buckets)).toHaveLength(0);
  });
});

describe('computeCommunityItems', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows recent community PRs outside the review buckets', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const recent = {
      ...pr(70, 'success'),
      author: 'external-contributor',
      updatedAt: '2026-06-22T23:06:18Z',
    };
    const newer = {
      ...pr(71, 'success'),
      author: 'another-contributor',
      updatedAt: '2026-06-23T20:06:18Z',
    };

    expect(computeCommunityItems([recent, newer]).map((item) => item.pullRequest.number)).toEqual([71, 70]);
  });

  it('excludes aged-out community PRs from the recent community list', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const agedOut = {
      ...pr(72, 'success'),
      author: 'external-contributor',
      updatedAt: '2026-06-08T23:06:18Z',
    };

    expect(computeCommunityItems([agedOut])).toHaveLength(0);
  });

  it('excludes core-team, draft, and held PRs from the recent community list', () => {
    const coreTeam = pr(73, 'success');
    const draft = { ...pr(74, 'success'), author: 'external-contributor', draft: true };
    const held = { ...pr(75, 'success'), author: 'external-contributor', labels: ['needs-author-action'] };

    expect(computeCommunityItems([coreTeam, draft, held])).toHaveLength(0);
  });
});

describe('computeFocusExclusionItems', () => {
  it('shows only the signed-in user open non-draft PRs that are outside Needs attention', () => {
    const failing = pr(1, 'failure');
    const inFocus = pr(2, 'success');
    const otherAuthor = { ...pr(3, 'failure'), author: 'monalisa' };
    const draft = { ...pr(4, 'failure'), draft: true };

    const buckets: AttentionBucket[] = [
      bucket('Needs review', [failing, inFocus, otherAuthor, draft]),
      bucket('CI failing', [failing, otherAuthor, draft]),
    ];

    const exclusions = computeFocusExclusionItems(
      [failing, inFocus, otherAuthor, draft],
      buckets,
      computeFocusItems(buckets),
      'davidfowl',
    );

    expect(exclusions.map((item) => item.pullRequest.number)).toEqual([1]);
    expect(exclusions[0]?.reason.label).toBe('CI failing');
  });

  it('explains held PRs that are hidden from attention buckets', () => {
    const held = { ...pr(50, 'success'), labels: ['needs-author-action'] };

    const exclusions = computeFocusExclusionItems([held], [], [], 'davidfowl');

    expect(exclusions).toHaveLength(1);
    expect(exclusions[0]?.reason.label).toBe('Held by label');
  });

  it('explains changes-requested PRs as author response work', () => {
    const blocked = pr(51, 'success', { state: 'changes_requested', changesRequestedCount: 1 });
    const buckets = [bucket('Author response', [blocked])];

    const exclusions = computeFocusExclusionItems(
      [blocked],
      buckets,
      computeFocusItems(buckets),
      'davidfowl',
    );

    expect(exclusions).toHaveLength(1);
    expect(exclusions[0]?.reason.label).toBe('Author response');
  });

  it('explains PRs whose actionable lane has aged out', () => {
    const oldDate = new Date(Date.now() - 20 * 24 * 60 * 60 * 1000).toISOString();
    const stale = { ...pr(60, 'success'), updatedAt: oldDate };
    const buckets = [bucket('Needs review', [stale])];

    const exclusions = computeFocusExclusionItems(
      [stale],
      buckets,
      computeFocusItems(buckets),
      'davidfowl',
    );

    expect(exclusions).toHaveLength(1);
    expect(exclusions[0]?.reason.label).toBe('Stale activity');
  });
});
