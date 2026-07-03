// @vitest-environment jsdom

import { act } from 'react';
import type { ComponentProps } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import type { PullRequestSummary } from '../types';
import PullRequestList from './PullRequestList';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('PullRequestList ordering', () => {
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

  it('keeps regression rows ahead of lower-priority focus buckets', async () => {
    const now = Date.now();
    await renderPullRequestList([
      {
        pullRequest: pullRequest({
          number: 1,
          title: 'Needs review row',
          updatedAt: new Date(now).toISOString(),
        }),
        bucketLabel: 'Needs review',
      },
      {
        pullRequest: pullRequest({
          number: 2,
          title: 'Regression row',
          updatedAt: new Date(now - 3 * 24 * 60 * 60 * 1000).toISOString(),
        }),
        bucketLabel: 'Regression',
      },
    ]);

    expect(document.body.textContent).toContain('Regression row');
    expect(document.body.textContent).not.toContain('Needs review row');
  });

  it('preserves caller order when server-ranked rows opt out of local sorting', async () => {
    await renderPullRequestList(
      [
        {
          pullRequest: pullRequest({
            number: 3,
            title: 'Server first row',
            createdAt: '2026-01-02T00:00:00Z',
            updatedAt: '2026-01-02T00:00:00Z',
          }),
          bucketLabel: 'Needs review',
        },
        {
          pullRequest: pullRequest({
            number: 4,
            title: 'Locally higher ranked row',
            createdAt: '2026-01-01T00:00:00Z',
            updatedAt: '2026-01-05T00:00:00Z',
          }),
          bucketLabel: 'Regression',
        },
      ],
      true,
      2,
    );

    const titles = [...document.querySelectorAll('.attention-pr-title')]
      .map((element) => element.textContent);
    expect(titles).toEqual(['Server first row', 'Locally higher ranked row']);
  });

  async function renderPullRequestList(
    entries: ComponentProps<typeof PullRequestList>['entries'],
    preserveOrder = false,
    limit: ComponentProps<typeof PullRequestList>['limit'] = 1,
  ) {
    const host = document.createElement('div');
    document.body.append(host);
    root = createRoot(host);
    await act(async () => {
      root?.render(
        <PullRequestList
          entries={entries}
          limit={limit}
          preserveOrder={preserveOrder}
          onSelectPullRequest={() => undefined}
        />,
      );
    });
  }
});

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
      requiresConversationResolution: false,
    },
    checks: {
      state: 'success',
      totalCount: 1,
      successCount: 1,
      failureCount: 0,
      pendingCount: 0,
      neutralCount: 0,
      skippedCount: 0,
      completedAt: now,
      failingChecks: [],
    },
    ...overrides,
  };
}
