import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CheckState, PullRequestSummary } from '../types';
import {
  replacePullRequestsByUpdatedAt,
  streamPullRequests,
  upsertPullRequestByUpdatedAt,
} from './pullRequests';

describe('pull request overlays', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('preserves loaded checks when a same-head cache overlay has unknown checks', () => {
    const loaded = pullRequest({ number: 1, headSha: 'abc123', checksState: 'success' });
    const cachedOverlay = pullRequest({
      number: 1,
      title: 'Updated from cache',
      headSha: 'abc123',
      checksState: 'unknown',
      updatedAt: '2026-01-03T00:00:00Z',
    });

    const result = upsertPullRequestByUpdatedAt([loaded], cachedOverlay);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      title: 'Updated from cache',
      checks: loaded.checks,
    });
  });

  it('preserves loaded checks when final replacement has a same-head unknown overlay', () => {
    const loaded = pullRequest({ number: 1, headSha: 'abc123', checksState: 'success' });
    const staleOnly = pullRequest({ number: 2, title: 'Stale-only row' });
    const liveOverlay = pullRequest({
      number: 1,
      title: 'Live title from stream',
      headSha: 'abc123',
      checksState: 'unknown',
      updatedAt: '2026-01-03T00:00:00Z',
    });

    const result = replacePullRequestsByUpdatedAt([loaded, staleOnly], [liveOverlay]);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      number: 1,
      title: 'Live title from stream',
      checks: loaded.checks,
    });
  });

  it('uses loaded checks from the overlay instead of preserving older loaded checks', () => {
    const current = pullRequest({ number: 1, headSha: 'abc123', checksState: 'success' });
    const loadedOverlay = pullRequest({
      number: 1,
      headSha: 'abc123',
      checksState: 'pending',
      updatedAt: '2026-01-03T00:00:00Z',
    });

    const result = upsertPullRequestByUpdatedAt([current], loadedOverlay);

    expect(result[0].checks.state).toBe('pending');
  });

  it('replaces the final list with the authoritative streamed rows', () => {
    const current = [
      pullRequest({ number: 1, updatedAt: '2026-01-01T00:00:00Z' }),
      pullRequest({ number: 2, updatedAt: '2026-01-02T00:00:00Z' }),
    ];
    const streamed = [
      pullRequest({ number: 1, title: 'Still open', updatedAt: '2026-01-03T00:00:00Z' }),
    ];

    const result = replacePullRequestsByUpdatedAt(current, streamed);

    expect(result.map((pullRequest) => pullRequest.number)).toEqual([1]);
    expect(result[0].title).toBe('Still open');
  });

  it('does not preserve checks when the head sha changes', () => {
    const oldHead = pullRequest({ number: 1, headSha: 'abc123', checksState: 'success' });
    const newHead = pullRequest({ number: 1, headSha: 'def456', checksState: 'unknown' });

    const result = upsertPullRequestByUpdatedAt([oldHead], newHead);

    expect(result[0].headSha).toBe('def456');
    expect(result[0].checks.state).toBe('unknown');
  });

  it('returns only live rows after a stale overlay stream completes', async () => {
    const stale = pullRequest({ number: 1, title: 'Cached row' });
    const live = pullRequest({ number: 1, title: 'Live row', updatedAt: '2026-01-02T00:00:00Z' });
    const displayedTitles: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async () => jsonLinesResponse([
      streamItem(stale, { isStale: true }),
      streamItem(live),
      { repository: live.repository, isComplete: true },
    ])));

    const result = await streamPullRequests('/api/github/pulls/stream?refresh=true', {
      onPullRequest: (pullRequest) => displayedTitles.push(pullRequest.title),
    });

    expect(displayedTitles).toEqual(['Cached row', 'Live row']);
    expect(result.completed).toBe(true);
    expect(result.pullRequests.map((pullRequest) => pullRequest.title)).toEqual(['Live row']);
  });

  it('keeps stale overlay rows when a refresh stream does not complete', async () => {
    const stale = pullRequest({ number: 1, title: 'Cached row' });
    const liveBaseline = pullRequest({ number: 1, title: 'Live baseline', updatedAt: '2026-01-02T00:00:00Z' });
    vi.stubGlobal('fetch', vi.fn(async () => jsonLinesResponse([
      streamItem(stale, { isStale: true }),
      streamItem(liveBaseline),
    ])));

    const result = await streamPullRequests('/api/github/pulls/stream?refresh=true');

    expect(result.completed).toBe(false);
    expect(result.pullRequests.map((pullRequest) => pullRequest.title)).toEqual(['Cached row', 'Live baseline']);
  });

  it('filters stale and live stream overlays before displaying or finalizing them', async () => {
    const staleRejected = pullRequest({ number: 1, title: 'Rejected cached row' });
    const staleAccepted = pullRequest({ number: 2, title: 'Accepted cached row' });
    const liveAccepted = pullRequest({
      number: 2,
      title: 'Accepted live row',
      updatedAt: '2026-01-02T00:00:00Z',
    });
    const displayedTitles: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async () => jsonLinesResponse([
      streamItem(staleRejected, { isStale: true }),
      streamItem(staleAccepted, { isStale: true }),
      streamItem(liveAccepted),
      { repository: liveAccepted.repository, isComplete: true },
    ])));

    const result = await streamPullRequests('/api/github/pulls/stream?refresh=true', {
      filter: (pullRequest) => pullRequest.number === 2,
      onPullRequest: (pullRequest) => displayedTitles.push(pullRequest.title),
    });

    expect(displayedTitles).toEqual(['Accepted cached row', 'Accepted live row']);
    expect(result.completed).toBe(true);
    expect(result.pullRequests.map((pullRequest) => pullRequest.title)).toEqual(['Accepted live row']);
  });
});

type PullRequestOverrides = Partial<PullRequestSummary> & {
  number: number;
  checksState?: CheckState;
};

function pullRequest({ checksState: checksStateOverride, ...overrides }: PullRequestOverrides): PullRequestSummary {
  const checksState = overrides.checks?.state ?? checksStateOverride ?? 'none';
  const { number, ...rest } = overrides;

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
    review: {
      state: 'waiting',
      reviewerCount: 0,
      approvalCount: 0,
      changesRequestedCount: 0,
      commentedReviewCount: 0,
    },
    checks: {
      state: checksState,
      totalCount: checksState === 'none' || checksState === 'unknown' ? 0 : 1,
      successCount: checksState === 'success' ? 1 : 0,
      failureCount: checksState === 'failure' ? 1 : 0,
      pendingCount: checksState === 'pending' ? 1 : 0,
      neutralCount: 0,
      skippedCount: 0,
      completedAt: checksState === 'success' ? '2026-01-01T00:30:00Z' : null,
      failingChecks: [],
    },
    ...rest,
  };
}

function streamItem(
  pullRequest: PullRequestSummary,
  options: { isStale?: boolean } = {},
) {
  return {
    repository: pullRequest.repository,
    pullRequest: withoutRepository(pullRequest),
    ...options,
  };
}

function withoutRepository(pullRequest: PullRequestSummary): Omit<PullRequestSummary, 'repository'> {
  const { repository: _repository, ...rest } = pullRequest;
  void _repository;
  return rest;
}

function jsonLinesResponse<T>(items: T[]) {
  return new Response(items.map((item) => JSON.stringify(item)).join('\n'), {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}
