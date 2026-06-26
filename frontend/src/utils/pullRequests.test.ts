import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CheckState, PullRequestListResponse, PullRequestSummary } from '../types';
import {
  fetchPullRequests,
  replacePullRequestsByUpdatedAt,
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
      title: 'Live title from response',
      headSha: 'abc123',
      checksState: 'unknown',
      updatedAt: '2026-01-03T00:00:00Z',
    });

    const result = replacePullRequestsByUpdatedAt([loaded, staleOnly], [liveOverlay]);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      number: 1,
      title: 'Live title from response',
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

  it('replaces the final list with the authoritative loaded rows', () => {
    const current = [
      pullRequest({ number: 1, updatedAt: '2026-01-01T00:00:00Z' }),
      pullRequest({ number: 2, updatedAt: '2026-01-02T00:00:00Z' }),
    ];
    const loaded = [
      pullRequest({ number: 1, title: 'Still open', updatedAt: '2026-01-03T00:00:00Z' }),
    ];

    const result = replacePullRequestsByUpdatedAt(current, loaded);

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

  it('loads pull requests from the JSON list response', async () => {
    const live = pullRequest({ number: 1, title: 'Live row', updatedAt: '2026-01-02T00:00:00Z' });
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(pullRequestList(live.repository, [live]))));

    const result = await fetchPullRequests('/api/github/pulls/graphql?refresh=true');

    expect(result.map((pullRequest) => pullRequest.title)).toEqual(['Live row']);
    expect(result[0].repository).toBe('example/repo');
  });

  it('filters loaded pull requests after normalizing repository data', async () => {
    const rejected = pullRequest({ number: 1, title: 'Rejected row' });
    const accepted = pullRequest({
      number: 2,
      title: 'Accepted row',
      updatedAt: '2026-01-02T00:00:00Z',
    });
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(
      pullRequestList('example/repo-renamed', [rejected, accepted]),
    )));

    const result = await fetchPullRequests('/api/github/pulls/graphql?refresh=true', {
      filter: (pullRequest) => pullRequest.number === 2,
    });

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      number: 2,
      repository: 'example/repo-renamed',
      title: 'Accepted row',
    });
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
      unresolvedThreadCount: 0,
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

function withoutRepository(pullRequest: PullRequestSummary): Omit<PullRequestSummary, 'repository'> {
  const { repository: _repository, ...rest } = pullRequest;
  void _repository;
  return rest;
}

function pullRequestList(repository: string, pullRequests: PullRequestSummary[]): PullRequestListResponse {
  return {
    repository,
    pullRequests: pullRequests.map(withoutRepository),
  };
}

function jsonResponse<T>(body: T, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
