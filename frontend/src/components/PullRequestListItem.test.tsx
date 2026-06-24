// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import type { CheckState, PullRequestSummary } from '../types';
import PullRequestListItem from './PullRequestListItem';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('PullRequestListItem metadata', () => {
  let root: ReturnType<typeof createRoot> | null = null;

  afterEach(async () => {
    if (root) {
      await act(async () => {
        root?.unmount();
      });
      root = null;
    }
    document.body.innerHTML = '';
  });

  it('does not show generic age/status-age semantics in the metadata column', async () => {
    await renderPullRequests([
      pullRequest({
        number: 1,
        title: 'Approved age metadata',
        createdAt: daysAgo(10),
        updatedAt: daysAgo(1),
        lastCommitAt: daysAgo(1),
        review: {
          state: 'approved',
          reviewerCount: 1,
          approvalCount: 1,
          changesRequestedCount: 0,
          commentedReviewCount: 0,
          unresolvedThreadCount: 0,
          lastApprovedAt: daysAgo(3),
          lastReviewedAt: daysAgo(3),
        },
      }),
      pullRequest({
        number: 2,
        title: 'Old first metadata',
        createdAt: daysAgo(8),
        updatedAt: daysAgo(8),
        lastCommitAt: daysAgo(8),
      }),
      pullRequest({
        number: 3,
        title: 'Review debt metadata',
        createdAt: daysAgo(16),
        updatedAt: daysAgo(16),
        lastCommitAt: daysAgo(16),
      }),
    ]);

    expect(rowSignals('Approved age metadata')).not.toContain('approved ');
    expect(rowMeta('Approved age metadata')).not.toContain('approved ');

    expect(rowSignals('Old first metadata')).not.toContain('old first');
    expect(rowSignals('Old first metadata')).not.toContain('open ');
    expect(rowMeta('Old first metadata')).not.toContain('old first');
    expect(rowMeta('Old first metadata')).not.toContain('open ');

    expect(rowSignals('Review debt metadata')).not.toContain('review debt');
    expect(rowMeta('Review debt metadata')).not.toContain('review debt');
  });

  async function renderPullRequests(pullRequests: PullRequestSummary[]) {
    const host = document.createElement('div');
    document.body.append(host);
    root = createRoot(host);
    await act(async () => {
      root?.render(
        <>
          {pullRequests.map((pullRequest) => (
            <PullRequestListItem
              key={pullRequest.number}
              pullRequest={pullRequest}
              bucketLabel="Needs review"
              onSelectPullRequest={() => undefined}
            />
          ))}
        </>,
      );
    });
  }
});

function rowSignals(title: string) {
  return pullRequestRow(title).querySelector('.attention-pr-signals')?.textContent ?? '';
}

function rowMeta(title: string) {
  return pullRequestRow(title).querySelector('.attention-pr-meta')?.textContent ?? '';
}

function pullRequestRow(title: string) {
  const row = Array.from(document.querySelectorAll<HTMLElement>('.attention-pr-row'))
    .find((candidate) => candidate.textContent?.includes(title));
  expect(row).toBeTruthy();
  return row!;
}

function pullRequest(overrides: Partial<PullRequestSummary>): PullRequestSummary {
  const now = new Date().toISOString();
  const number = overrides.number ?? 1;
  return {
    repository: 'microsoft/aspire',
    number,
    title: `PR ${number}`,
    state: 'open',
    draft: false,
    author: 'davidfowl',
    htmlUrl: `https://github.com/microsoft/aspire/pull/${number}`,
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
    lastCommitAt: now,
    headSha: 'abc123',
    baseRef: 'main',
    mergeableState: null,
    review: {
      state: 'waiting',
      reviewerCount: 0,
      approvalCount: 0,
      changesRequestedCount: 0,
      commentedReviewCount: 0,
      unresolvedThreadCount: 0,
    },
    checks: checksStatus('none'),
    ...overrides,
  };
}

function checksStatus(state: CheckState): PullRequestSummary['checks'] {
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

function daysAgo(days: number) {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
}
