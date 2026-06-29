import { afterEach, describe, expect, it, vi } from 'vitest';
import type { PullRequestSummary, ReviewStatus, ShipWeekIssueSummary } from '../types';
import {
  createAttentionBuckets,
  createAttentionSignals,
  createFocusIssueBuckets,
  isPullRequestWithinFocusAgeLimit,
  pullRequestFocusActivityAt,
} from './models';

type PrOverrides = Partial<Omit<PullRequestSummary, 'review' | 'checks'>> & {
  number: number;
  review?: Partial<ReviewStatus>;
  checks?: Partial<PullRequestSummary['checks']>;
};

function pr(overrides: PrOverrides): PullRequestSummary {
  const { number, review: reviewOverride, checks: checksOverride, ...rest } = overrides;

  const review: ReviewStatus = {
    state: 'waiting',
    reviewerCount: 0,
    approvalCount: 0,
    changesRequestedCount: 0,
    commentedReviewCount: 0,
    unresolvedThreadCount: 0,
    requiresConversationResolution: false,
    ...reviewOverride,
  };

  return {
    repository: 'example/repo',
    number,
    title: `PR ${number}`,
    state: 'open',
    draft: false,
    author: 'octocat',
    htmlUrl: `https://github.com/example/repo/pull/${number}`,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    fetchedAt: '2026-01-01T00:00:00Z',
    labels: [],
    requestedReviewers: [],
    linkedIssues: [],
    commitCount: 1,
    additions: 10,
    deletions: 2,
    changedFiles: 1,
    mergeableState: null,
    ...rest,
    review,
    checks: {
      state: 'success',
      totalCount: 1,
      successCount: 1,
      failureCount: 0,
      pendingCount: 0,
      neutralCount: 0,
      skippedCount: 0,
      completedAt: '2026-01-01T00:30:00Z',
      failingChecks: [],
      ...checksOverride,
    },
  };
}

function inBucket(buckets: ReturnType<typeof createAttentionBuckets>, label: string, number: number) {
  return buckets.some(
    (bucket) => bucket.label === label && bucket.items.some((item) => item.pullRequest.number === number),
  );
}

function issue(overrides: Partial<ShipWeekIssueSummary> & { number: number }): ShipWeekIssueSummary {
  const { number, ...rest } = overrides;

  return {
    repository: 'example/repo',
    number,
    title: `Issue ${number}`,
    htmlUrl: `https://github.com/example/repo/issues/${number}`,
    author: 'reporter',
    labels: [],
    assignees: [],
    milestone: null,
    updatedAt: '2026-01-01T00:00:00Z',
    fetchedAt: '2026-01-01T00:00:00Z',
    linkedOpenPullRequests: [],
    ...rest,
  };
}

describe('createAttentionBuckets lane routing', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('routes a clean approved PR to Ready to merge', () => {
    const buckets = createAttentionBuckets([pr({ number: 1, review: { state: 'approved', approvalCount: 1 } })]);
    expect(inBucket(buckets, 'Ready to merge', 1)).toBe(true);
  });

  it('keeps an approved PR with merge-blocking unresolved threads out of Ready to merge', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 2, review: { state: 'approved', approvalCount: 1, unresolvedThreadCount: 2, requiresConversationResolution: true } }),
    ]);
    expect(inBucket(buckets, 'Ready to merge', 2)).toBe(false);
    expect(inBucket(buckets, 'Unresolved feedback', 2)).toBe(true);
  });

  it('keeps an approved PR with non-blocking unresolved threads in Ready to merge while still flagging feedback', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 22, review: { state: 'approved', approvalCount: 1, unresolvedThreadCount: 2, requiresConversationResolution: false } }),
    ]);
    expect(inBucket(buckets, 'Ready to merge', 22)).toBe(true);
    expect(inBucket(buckets, 'Unresolved feedback', 22)).toBe(true);
  });

  it('keeps a conflicted approved PR out of Ready to merge and in Merge conflicts', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 3, mergeableState: 'dirty', review: { state: 'approved', approvalCount: 1 } }),
    ]);
    expect(inBucket(buckets, 'Ready to merge', 3)).toBe(false);
    expect(inBucket(buckets, 'Merge conflicts', 3)).toBe(true);
  });

  it('routes a waiting PR with Copilot threads to Unresolved feedback', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 4, review: { state: 'waiting', unresolvedThreadCount: 1 } }),
    ]);
    expect(inBucket(buckets, 'Unresolved feedback', 4)).toBe(true);
  });

  it('hides a NO-MERGE-labelled PR from every lane', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 5, labels: ['NO-MERGE'], review: { state: 'approved', approvalCount: 1 } }),
    ]);
    expect(buckets.every((bucket) => bucket.items.every((item) => item.pullRequest.number !== 5))).toBe(true);
  });

  it('adds a My draft PRs bucket for draft PRs authored by the signed-in user', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 30, author: 'octocat', draft: true }),
      pr({ number: 31, author: 'hubot', draft: true }),
      pr({ number: 32, author: 'octocat' }),
      pr({ number: 34, author: 'octocat', draft: true, labels: ['NO-MERGE'] }),
    ], 'OctoCat');

    const myDrafts = buckets.find((bucket) => bucket.label === 'My draft PRs');

    expect(myDrafts?.items.map((item) => item.pullRequest.number)).toEqual([30, 34]);
    expect(inBucket(buckets, 'Draft', 30)).toBe(true);
    expect(inBucket(buckets, 'Draft', 31)).toBe(true);
    expect(inBucket(buckets, 'Draft', 34)).toBe(false);
  });

  it('omits My draft PRs when there is no signed-in user', () => {
    const buckets = createAttentionBuckets([
      pr({ number: 33, author: 'octocat', draft: true }),
    ]);

    expect(buckets.some((bucket) => bucket.label === 'My draft PRs')).toBe(false);
  });

  it('keeps quiet recent core-team PRs in Needs review instead of Stalled', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const buckets = createAttentionBuckets([
      pr({
        number: 18251,
        author: 'DamianEdwards',
        createdAt: '2026-06-16T20:28:55Z',
        updatedAt: '2026-06-19T23:06:18Z',
        commitCount: 12,
        additions: 2059,
        deletions: 40,
        changedFiles: 15,
      }),
    ]);

    expect(inBucket(buckets, 'Needs review', 18251)).toBe(true);
    expect(inBucket(buckets, 'Stalled', 18251)).toBe(false);
  });

  it('keeps old unreviewed core-team PRs reviewable while also marking them stalled', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const buckets = createAttentionBuckets([
      pr({
        number: 6,
        author: 'DamianEdwards',
        createdAt: '2026-06-10T20:28:55Z',
        updatedAt: '2026-06-15T23:06:18Z',
      }),
    ]);

    expect(inBucket(buckets, 'Needs review', 6)).toBe(true);
    expect(inBucket(buckets, 'Stalled', 6)).toBe(true);
  });

  it('keeps old ready-to-merge PRs in focus when they were approved recently', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const pullRequest = pr({
      number: 7,
      createdAt: '2026-06-09T20:28:55Z',
      updatedAt: '2026-06-22T21:00:00Z',
      review: {
        state: 'approved',
        approvalCount: 1,
        lastApprovedAt: '2026-06-22T21:00:00Z',
        lastReviewedAt: '2026-06-22T21:00:00Z',
      },
    });

    expect(pullRequestFocusActivityAt(pullRequest, 'Ready to merge')).toBe('2026-06-22T21:00:00Z');
    expect(isPullRequestWithinFocusAgeLimit(pullRequest, 'Ready to merge')).toBe(true);
    expect(createAttentionSignals({ pullRequest, reason: '' }).map((signal) => signal.label)).not.toContain('review debt');
  });

  it('does not let generic updatedAt refresh stale ready-to-merge approvals', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const pullRequest = pr({
      number: 8,
      createdAt: '2026-06-01T20:28:55Z',
      updatedAt: '2026-06-22T21:00:00Z',
      review: {
        state: 'approved',
        approvalCount: 1,
        lastApprovedAt: '2026-06-08T21:00:00Z',
        lastReviewedAt: '2026-06-08T21:00:00Z',
      },
    });

    expect(pullRequestFocusActivityAt(pullRequest, 'Ready to merge')).toBe('2026-06-08T21:00:00Z');
    expect(isPullRequestWithinFocusAgeLimit(pullRequest, 'Ready to merge')).toBe(false);
  });

  it('uses CI activity for aging signals when failing checks are the primary action', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const pullRequest = pr({
      number: 9,
      createdAt: '2026-06-01T20:28:55Z',
      updatedAt: '2026-06-22T21:00:00Z',
      review: {
        state: 'approved',
        approvalCount: 1,
        lastApprovedAt: '2026-06-08T21:00:00Z',
        lastReviewedAt: '2026-06-08T21:00:00Z',
      },
      checks: {
        state: 'failure',
        failureCount: 1,
        completedAt: '2026-06-22T21:00:00Z',
      },
    });

    expect(pullRequestFocusActivityAt(pullRequest, 'CI failing')).toBe('2026-06-22T21:00:00Z');
    expect(createAttentionSignals({ pullRequest, reason: '' }).map((signal) => signal.label)).not.toContain('review debt');
  });
});

describe('createAttentionSignals review progress', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps approval counts inside limited PR card signals for crowded approved PRs', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-23T23:31:40Z'));

    const pullRequest = pr({
      number: 7,
      title: 'Prepare 13.4 servicing update',
      baseRef: 'release/13.4',
      review: {
        state: 'approved',
        approvalCount: 2,
        lastApprovedAt: '2026-06-23T20:00:00Z',
      },
      checks: {
        state: 'pending',
        totalCount: 1,
        successCount: 0,
        pendingCount: 1,
        completedAt: null,
      },
    });

    const limitedLabels = createAttentionSignals({ pullRequest, reason: '' })
      .slice(0, 4)
      .map((signal) => signal.label);

    expect(limitedLabels).toContain('2 approvals');
  });
});

describe('createFocusIssueBuckets personal lanes', () => {
  it('adds a My issues bucket for issues assigned to the signed-in user', () => {
    const buckets = createFocusIssueBuckets([
      issue({ number: 1, assignees: ['other'] }),
      issue({ number: 2, assignees: ['davidfowl'] }),
      issue({ number: 3, assignees: ['DavidFowl'] }),
    ], 'davidfowl');

    const myIssues = buckets.find((bucket) => bucket.label === 'My issues');

    expect(myIssues?.issues.map((item) => item.number)).toEqual([2, 3]);
  });
});
