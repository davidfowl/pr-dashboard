// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it } from 'vitest';
import type { AttentionBucket, PullRequestSummary } from '../../types';
import AttentionBoard from './AttentionBoard';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

function pr(number: number, overrides: Partial<PullRequestSummary> = {}): PullRequestSummary {
  const now = '2026-01-01T00:00:00Z';
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

function bucket(label: string, tone: AttentionBucket['tone'], prs: PullRequestSummary[]): AttentionBucket {
  return {
    label,
    summary: `${label} summary`,
    tone,
    metric: label,
    items: prs.map((pullRequest) => ({ pullRequest, reason: label })),
  };
}

function laneLabels(host: HTMLElement) {
  return Array.from(host.querySelectorAll('.drilldown-tile-label')).map((node) => node.textContent);
}

function visiblePullRequestNumbers(host: HTMLElement) {
  return Array.from(host.querySelectorAll('.attention-pr-number')).map(
    (node) => node.textContent?.match(/#\d+/)?.[0] ?? null,
  );
}

describe('AttentionBoard merge-blocked filter', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('hides merge-blocked PRs and collapses emptied lanes when toggled on', async () => {
    const conflicted = pr(1, { mergeableState: 'dirty' });
    const clean = pr(2, { mergeableState: 'clean' });
    const buckets = [
      bucket('Re-review needed', 'warning', [conflicted, clean]),
      bucket('Merge conflicts', 'danger', [conflicted]),
    ];

    const host = document.createElement('div');
    document.body.appendChild(host);
    const root = createRoot(host);

    await act(async () => {
      root.render(
        <AttentionBoard
          buckets={buckets}
          loading={false}
          hasLoaded
          selectedBucketId="review-bucket-re-review-needed"
          onSelectBucket={() => {}}
          onSelectPullRequest={() => {}}
          onVisiblePullRequest={() => false}
        />,
      );
    });

    expect(laneLabels(host)).toEqual(['Re-review needed', 'Merge conflicts']);
    expect(visiblePullRequestNumbers(host)).toEqual(['#1', '#2']);

    const toggle = host.querySelector<HTMLButtonElement>('.board-filter-toggle');
    expect(toggle?.textContent).toBe('Hide merge-blocked PRs');
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');

    await act(async () => {
      toggle?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(laneLabels(host)).toEqual(['Re-review needed']);
    expect(visiblePullRequestNumbers(host)).toEqual(['#2']);
    expect(toggle?.textContent).toBe('Show merge-blocked PRs');
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');

    await act(async () => {
      root.unmount();
    });
  });
});
