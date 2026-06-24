// @vitest-environment jsdom

import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import type {
  AppInfoResponse,
  AuthStatus,
  CheckState,
  PullRequestChecksRequest,
  PullRequestChecksResponse,
  PullRequestStreamItem,
  PullRequestSummary,
  ShipWeekIssueSummary,
  ShipWeekResponse,
  TimelineResponse,
} from './types';

type ActEnvironment = typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
};

(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

describe('App navigation', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
    window.history.replaceState(null, '', '/');
  });

  it('returns to the dashboard and clears PR hash when switching modes from detail view', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock());
    const { root } = await renderApp();

    await waitFor(() => getButton('View timeline'));

    await clickButton('View timeline');
    await waitFor(() => getButton('Back to dashboard'));
    expect(window.location.hash).toBe('#pr/microsoft%2Faspire/101');
    expect(document.body.textContent).toContain('#101 Fix dashboard navigation');

    await clickButton('Ship mode');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Ship mode data');
      expect(document.body.textContent).not.toContain('Back to dashboard');
    });
    expect(window.location.search).toContain('mode=ship');
    expect(window.location.hash).toBe('');

    await unmountApp(root);
  });

  it('force-refreshes visible checks even when loaded CI state is preserved', async () => {
    window.history.replaceState(null, '', '/');
    const fetchMock = createFetchMock({
      authenticated: true,
      checksState: 'unknown',
      visibleChecksState: 'success',
    });
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => !url.searchParams.has('refresh'))).toBe(true);
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('octocat');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    });

    await unmountApp(root);
  });

  it('highlights pull requests authored by the signed-in user', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock({
      authenticated: true,
      author: 'octocat',
    }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Yours');
    });
    const signedInRow = document.querySelector('.attention-pr-row.signed-in-user-entry');
    const metaBadge = signedInRow?.querySelector('.attention-pr-meta .signed-in-user-badge');
    expect(metaBadge?.textContent).toBe('Yours');
    expect(signedInRow?.classList.contains('signed-in-user-entry-full-bleed')).toBe(true);

    await unmountApp(root);
  });

  it('renders ready-to-merge as a row marker instead of a signal pill', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock({ readyToMerge: true }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Ready to merge');
    });
    const readyRow = document.querySelector('.attention-pr-row.ready-to-merge-entry');
    expect(readyRow).not.toBeNull();
    expect(readyRow?.classList.contains('ready-to-merge-entry-full-bleed')).toBe(true);
    expect(readyRow?.classList.contains('compact-pr-action-marker-layout')).toBe(true);
    expect(readyRow?.classList.contains('content-bounded-action-marker-layout')).toBe(true);
    expect(readyRow?.classList.contains('has-action-marker')).toBe(true);
    const readyMarker = readyRow?.querySelector('.attention-pr-action-marker');
    expect(readyMarker?.classList.contains('first-row-action-marker')).toBe(true);
    expect(readyMarker?.classList.contains('row-height-neutral-action-marker')).toBe(true);
    expect(readyMarker?.textContent).toBe('Ready to merge');
    expect(readyRow?.querySelector('.attention-pr-signals')?.textContent).not.toContain('Ready to merge');

    await unmountApp(root);
  });

  it('renders needs-review as a row marker instead of a signal pill', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock());
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Needs review');
    });
    const needsReviewRow = document.querySelector('.attention-pr-row.needs-review-entry');
    expect(needsReviewRow).not.toBeNull();
    expect(needsReviewRow?.classList.contains('compact-pr-action-marker-layout')).toBe(true);
    expect(needsReviewRow?.classList.contains('has-action-marker')).toBe(true);
    expect(needsReviewRow?.querySelector('.attention-pr-action-marker')?.textContent).toBe('Needs review');
    expect(needsReviewRow?.querySelector('.attention-pr-signals')?.textContent).not.toContain('Needs review');

    await unmountApp(root);
  });

  it('renders the action marker slot on every pull request row', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock({
      readyToMerge: true,
      extraPullRequests: [
        createPullRequest('none', {
          number: 102,
          title: 'Author response row',
          htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
          headSha: 'def456',
          review: {
            state: 'changes_requested',
            reviewerCount: 1,
            approvalCount: 0,
            changesRequestedCount: 1,
            commentedReviewCount: 0,
            unresolvedThreadCount: 0,
            lastReviewedAt: '2026-06-24T16:00:00Z',
          },
        }),
      ],
    }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Author response row');
    });
    const rows = Array.from(document.querySelectorAll('.attention-pr-row'));
    expect(rows.length).toBeGreaterThanOrEqual(2);
    for (const row of rows) {
      expect(row.classList.contains('compact-pr-action-marker-layout')).toBe(true);
      expect(row.querySelector('.attention-pr-action-marker')).not.toBeNull();
    }
    const normalRow = rows.find((row) => row.textContent?.includes('Author response row'));
    expect(normalRow?.classList.contains('has-action-marker')).toBe(false);
    const emptyMarker = normalRow?.querySelector('.attention-pr-action-marker.empty-action-marker');
    expect(emptyMarker).not.toBeNull();
    expect(emptyMarker?.getAttribute('aria-hidden')).toBe('true');
    expect(emptyMarker?.textContent).toBe('');

    await unmountApp(root);
  });

  it('uses a single purple timeline action on pull request rows', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock());
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('View timeline');
    });
    const pullRequestRow = document.querySelector('.attention-pr-row');
    expect(pullRequestRow?.querySelector<HTMLAnchorElement>('.attention-pr-number-link')?.href).toBe('https://github.com/microsoft/aspire/pull/101');
    expect(pullRequestRow?.querySelector<HTMLAnchorElement>('.attention-pr-repo-link')?.href).toBe('https://github.com/microsoft/aspire');
    expect(pullRequestRow?.querySelector('.attention-pr-github-link')).toBeNull();
    expect(pullRequestRow?.querySelector('.attention-pr-actions')?.textContent).toBe('View timeline');
    expect(pullRequestRow?.querySelector('.attention-pr-timeline-button')?.classList.contains('attention-pr-primary-action')).toBe(true);

    await unmountApp(root);
  });

  it('colors stale updated age instead of showing redundant idle pills', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock({
      updatedAt: '2026-05-01T00:00:00Z',
    }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('updated');
    });
    const pullRequestRow = document.querySelector('.attention-pr-row');
    const signalsText = pullRequestRow?.querySelector('.attention-pr-signals')?.textContent ?? '';
    expect(signalsText).not.toContain('idle');
    expect(signalsText).not.toContain('review debt');
    expect(signalsText).not.toContain('open ');
    expect(pullRequestRow?.querySelector('.attention-pr-updated-age.age-tone-danger')?.textContent).toBeTruthy();

    await unmountApp(root);
  });

  it('moves approved, open, and old-first computed age semantics into row metadata', async () => {
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', createFetchMock({
      createdAt: daysAgo(16),
      updatedAt: daysAgo(16),
      extraPullRequests: [
        createPullRequest('success', {
          number: 102,
          title: 'Approved metadata row',
          htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
          createdAt: daysAgo(10),
          updatedAt: daysAgo(1),
          lastCommitAt: daysAgo(1),
          headSha: 'def456',
          review: {
            state: 'approved',
            reviewerCount: 1,
            approvalCount: 1,
            changesRequestedCount: 0,
            commentedReviewCount: 0,
            unresolvedThreadCount: 0,
            lastReviewedAt: daysAgo(3),
            lastApprovedAt: daysAgo(3),
          },
        }),
        createPullRequest('none', {
          number: 103,
          title: 'Old first metadata row',
          htmlUrl: 'https://github.com/microsoft/aspire/pull/103',
          createdAt: daysAgo(8),
          updatedAt: daysAgo(8),
          lastCommitAt: daysAgo(8),
          headSha: 'ghi789',
        }),
      ],
    }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Old first metadata row');
    });

    const approvedRow = pullRequestRow('Approved metadata row');
    expect(approvedRow.querySelector('.attention-pr-signals')?.textContent).not.toContain('approved ');
    expect(approvedRow.querySelector('.attention-pr-meta')?.textContent).toContain('approved ');

    const oldFirstRow = pullRequestRow('Old first metadata row');
    const oldFirstSignals = oldFirstRow.querySelector('.attention-pr-signals')?.textContent ?? '';
    const oldFirstMeta = oldFirstRow.querySelector('.attention-pr-meta')?.textContent ?? '';
    expect(oldFirstSignals).not.toContain('old first');
    expect(oldFirstSignals).not.toContain('open ');
    expect(oldFirstMeta).toContain('old first');
    expect(oldFirstMeta).toContain('open ');
    await unmountApp(root);
  });

  it('force-refreshes rows that become visible after an earlier checks refresh flush', async () => {
    window.history.replaceState(null, '', '/');
    const observers = installIntersectionObserverMock();
    const fetchMock = createDelayedVisibleChecksFetchMock();
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('First visible row');
      expect(document.body.textContent).toContain('Late visible row');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');
    await waitFor(() => {
      expect(observers.length).toBeGreaterThan(1);
    });

    await act(async () => {
      triggerObservedRows(observers, 1);
    });
    await waitFor(() => {
      expect(refreshChecksRequestNumbers(fetchMock).length).toBeGreaterThan(0);
    });

    await act(async () => {
      triggerObservedRows(observers);
    });
    await waitFor(() => {
      expect(new Set(refreshChecksRequestNumbers(fetchMock))).toEqual(new Set([101, 102]));
    });

    await unmountApp(root);
  });

  it('keeps cached rows visible during hard refresh and finalizes only completed live rows', async () => {
    window.history.replaceState(null, '', '/');
    const liveRefresh = createDeferred<void>();
    const fetchMock = createHardRefreshFetchMock(liveRefresh.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached row');
    });
    await waitFor(() => {
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Stale-only cached row');
      expect(document.body.textContent).not.toContain('Live refreshed row');
    });
    expect(streamRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live refreshed row');
      expect(document.body.textContent).not.toContain('Stale-only cached row');
    });
    await waitFor(() => {
      expect(checksRequestUrls(fetchMock).some((url) => url.searchParams.get('refresh') === 'true')).toBe(true);
    });

    await unmountApp(root);
  });

  it('keeps stale-preserving live baseline rows out of the finalized rendered list', async () => {
    window.history.replaceState(null, '', '/');
    const liveRefresh = createDeferred<void>();
    const fetchMock = createStalePreservingRefreshFetchMock(liveRefresh.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Cached enriched row');
      expect((getButton('Refresh now') as HTMLButtonElement).disabled).toBe(false);
    });

    await clickButton('Refresh now');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live baseline row');
      expect(document.body.textContent).not.toContain('Live enriched row');
    });
    expect((getButton('Refreshing...') as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      liveRefresh.resolve();
    });

    await waitFor(() => {
      expect(document.body.textContent).toContain('Live enriched row');
      expect(document.body.textContent).not.toContain('Cached enriched row');
      expect(document.body.textContent).not.toContain('Live baseline row');
    });

    await unmountApp(root);
  });

  it('clears ship snapshot status on logout before ship mode reloads', async () => {
    window.history.replaceState(null, '', '/');
    const clipboard = { writeText: vi.fn().mockResolvedValue(undefined) };
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: clipboard,
    });
    vi.stubGlobal('fetch', createFetchMock({ authenticated: true }));
    const { root } = await renderApp();

    await waitFor(() => getButton('Ship mode'));
    await clickButton('Ship mode');
    await waitFor(() => getButton('Copy link'));

    await clickButton('Copy link');
    await waitFor(() => {
      expect(document.body.textContent).toContain('Share link copied.');
    });

    await clickButton('Sign out');
    await waitFor(() => getButton('Sign in'));
    await clickButton('Review mode');
    await clickButton('Ship mode');

    await waitFor(() => {
      expect(document.body.textContent).toContain('Ship mode data');
      expect(document.body.textContent).not.toContain('Share link copied.');
    });

    await unmountApp(root);
  });

  it('focuses the copied issues bucket link on direct load', async () => {
    window.history.replaceState(null, '', '/?mode=issues#bucket/regression');
    vi.stubGlobal('fetch', createFetchMock({
      issues: [createIssue({
        labels: ['regression'],
        title: 'Regression issue',
      })],
    }));
    const { root } = await renderApp();

    await waitFor(() => {
      expect(document.body.textContent).toContain('Regression issue');
    });
    const regressionTab = document.querySelector<HTMLButtonElement>('#issue-bucket-regression-tab');
    expect(regressionTab?.getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(regressionTab);
    const issueRow = document.querySelector('.attention-issue-row');
    expect(issueRow?.querySelector<HTMLAnchorElement>('.attention-issue-number-link')?.href).toBe('https://github.com/microsoft/aspire/issues/1001');
    expect(issueRow?.querySelector<HTMLAnchorElement>('.attention-issue-repo-link')?.href).toBe('https://github.com/microsoft/aspire');

    await unmountApp(root);
  });
});

type FetchMockOptions = {
  authenticated?: boolean;
  checksState?: CheckState;
  visibleChecksState?: CheckState;
  author?: string;
  readyToMerge?: boolean;
  createdAt?: string;
  updatedAt?: string;
  issues?: ShipWeekIssueSummary[];
  extraPullRequests?: PullRequestSummary[];
};

function createFetchMock(options: FetchMockOptions = {}) {
  let authenticated = options.authenticated ?? false;
  const pullRequest = createPullRequest(
    options.readyToMerge ? 'success' : (options.checksState ?? 'none'),
    {
      ...(options.author === undefined ? {} : { author: options.author }),
      ...(options.createdAt === undefined ? {} : { createdAt: options.createdAt }),
      ...(options.updatedAt === undefined ? {} : { updatedAt: options.updatedAt }),
      ...(options.readyToMerge ? {
        review: {
          state: 'approved' as const,
          reviewerCount: 1,
          approvalCount: 1,
          changesRequestedCount: 0,
          commentedReviewCount: 0,
          unresolvedThreadCount: 0,
          lastReviewedAt: '2026-06-24T16:00:00Z',
          lastApprovedAt: '2026-06-24T16:00:00Z',
        },
      } : {}),
    },
  );
  const pullRequests = [pullRequest, ...(options.extraPullRequests ?? [])];

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated,
        configured: true,
        canLogin: true,
        login: authenticated ? 'octocat' : undefined,
        message: authenticated ? 'Signed in.' : 'Using anonymous public cache.',
      });
    }

    if (url.pathname === '/api/github/logout') {
      authenticated = false;
      return jsonResponse({});
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      const repository = url.searchParams.get('repo');
      return jsonLinesResponse(repository === pullRequest.repository
        ? pullRequests.map((item) => createStreamItem(item))
        : []);
    }

    if (url.pathname === '/api/github/pulls/101/timeline') {
      return jsonResponse<TimelineResponse>({
        repository: pullRequest.repository,
        number: pullRequest.number,
        checks: pullRequest.checks,
        mergeableState: null,
        stats: {
          commitCount: 1,
          humanCommenterCount: 1,
          humanCommentCount: 1,
          reviewCount: 0,
          approvalCount: 0,
          firstHumanCommentDelayMs: 60_000,
          developers: [],
        },
        items: [
          {
            id: 'comment-1',
            event: 'commented',
            actor: 'reviewer',
            occurredAt: pullRequest.updatedAt,
            summary: 'reviewer commented',
            body: 'Looks good.',
            htmlUrl: 'https://github.com/microsoft/aspire/pull/101#issuecomment-1',
          },
        ],
      });
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: pullRequest.repository,
        pullRequests: pullRequests.map((item) => ({
          number: item.number,
          headSha: item.headSha ?? '',
          checks: item.number === pullRequest.number
            ? checksStatus(options.visibleChecksState ?? 'success')
            : item.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: options.issues ?? [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createHardRefreshFetchMock(liveRefresh: Promise<void>) {
  const cached = createPullRequest('success', {
    title: 'Cached row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const staleOnly = createPullRequest('success', {
    number: 102,
    title: 'Stale-only cached row',
    htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
    updatedAt: '2026-01-02T00:00:00Z',
  });
  const live = createPullRequest('success', {
    title: 'Live refreshed row',
    updatedAt: '2026-01-03T00:00:00Z',
  });

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('refresh') === 'true') {
        return jsonLinesStreamResponse(
          [createStreamItem(staleOnly, { isStale: true })],
          liveRefresh,
          [
            createStreamItem(live),
            { repository: live.repository, isComplete: true },
          ],
        );
      }

      return jsonLinesResponse([createStreamItem(cached)]);
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: cached.repository,
        pullRequests: [
          {
            number: cached.number,
            headSha: cached.headSha ?? '',
            checks: cached.checks,
          },
          {
            number: staleOnly.number,
            headSha: staleOnly.headSha ?? '',
            checks: staleOnly.checks,
          },
        ],
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createStalePreservingRefreshFetchMock(liveRefresh: Promise<void>) {
  const cached = createPullRequest('success', {
    title: 'Cached enriched row',
    updatedAt: '2026-01-01T00:00:00Z',
  });
  const liveBaseline = createPullRequest('unknown', {
    title: 'Live baseline row',
    updatedAt: '2026-01-03T00:00:00Z',
  });
  const liveEnriched = createPullRequest('success', {
    title: 'Live enriched row',
    updatedAt: '2026-01-03T00:00:00Z',
  });

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('repo') !== cached.repository) {
        return jsonLinesResponse([]);
      }

      if (url.searchParams.get('refresh') === 'true') {
        return jsonLinesStreamResponse(
          [
            createStreamItem(cached, { isStale: true }),
            createStreamItem(liveBaseline, { isStale: true }),
          ],
          liveRefresh,
          [
            createStreamItem(liveEnriched),
            { repository: liveEnriched.repository, isComplete: true },
          ],
        );
      }

      return jsonLinesResponse([createStreamItem(cached)]);
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: cached.repository,
        pullRequests: [cached, liveBaseline, liveEnriched].map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createDelayedVisibleChecksFetchMock() {
  const pullRequests = [
    createPullRequest('success', { title: 'First visible row' }),
    createPullRequest('success', {
      number: 102,
      title: 'Late visible row',
      htmlUrl: 'https://github.com/microsoft/aspire/pull/102',
      headSha: 'def456',
    }),
  ];

  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname === '/api/github/auth-status') {
      return jsonResponse<AuthStatus>({
        authenticated: true,
        configured: true,
        canLogin: true,
        login: 'octocat',
        message: 'Signed in.',
      });
    }

    if (url.pathname === '/api/app-info') {
      return jsonResponse<AppInfoResponse>({
        commitSha: 'test',
        shortCommitSha: 'test',
      });
    }

    if (url.pathname === '/api/github/pulls/stream') {
      if (url.searchParams.get('label')) {
        return jsonLinesResponse([]);
      }

      return jsonLinesResponse(
        url.searchParams.get('repo') === pullRequests[0].repository
          ? pullRequests.map((pullRequest) => createStreamItem(pullRequest))
          : [],
      );
    }

    if (url.pathname === '/api/github/pulls/checks') {
      return jsonResponse<PullRequestChecksResponse>({
        repository: pullRequests[0].repository,
        pullRequests: pullRequests.map((pullRequest) => ({
          number: pullRequest.number,
          headSha: pullRequest.headSha ?? '',
          checks: pullRequest.checks,
        })),
      });
    }

    if (url.pathname === '/api/github/ship-week') {
      return jsonResponse<ShipWeekResponse>({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        milestone: url.searchParams.get('milestone') ?? '13.4',
        releaseBranch: '',
        pullRequests: [],
        issues: [],
      });
    }

    if (url.pathname === '/api/github/issues/focus') {
      return jsonResponse({
        repository: url.searchParams.get('repo') ?? 'microsoft/aspire',
        issues: [],
      });
    }

    return jsonResponse({ detail: `Unhandled request: ${url.pathname}` }, 404);
  });
}

function createPullRequest(
  checksState: CheckState,
  overrides: Partial<PullRequestSummary> = {},
): PullRequestSummary {
  const now = overrides.updatedAt ?? new Date().toISOString();
  const number = overrides.number ?? 101;
  return {
    repository: 'microsoft/aspire',
    number,
    title: 'Fix dashboard navigation',
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
    commitCount: 3,
    additions: 120,
    deletions: 20,
    changedFiles: 5,
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
    checks: checksStatus(checksState),
    ...overrides,
  };
}

function createIssue(overrides: Partial<ShipWeekIssueSummary> = {}): ShipWeekIssueSummary {
  const now = overrides.updatedAt ?? new Date().toISOString();
  const number = overrides.number ?? 1001;
  return {
    repository: 'microsoft/aspire',
    number,
    title: 'Issue needs attention',
    htmlUrl: `https://github.com/microsoft/aspire/issues/${number}`,
    author: 'octocat',
    labels: [],
    assignees: [],
    milestone: null,
    updatedAt: now,
    fetchedAt: now,
    linkedOpenPullRequests: [],
    ...overrides,
  };
}

function pullRequestRow(title: string) {
  const row = Array.from(document.querySelectorAll<HTMLElement>('.attention-pr-row'))
    .find((candidate) => candidate.textContent?.includes(title));
  expect(row).toBeTruthy();
  return row!;
}

function daysAgo(days: number) {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
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

function withoutRepository(pullRequest: PullRequestSummary): Omit<PullRequestSummary, 'repository'> {
  const { repository: _repository, ...rest } = pullRequest;
  void _repository;
  return rest;
}

function createStreamItem(
  pullRequest: PullRequestSummary,
  options: Pick<PullRequestStreamItem, 'isStale'> = {},
): PullRequestStreamItem {
  return {
    repository: pullRequest.repository,
    pullRequest: withoutRepository(pullRequest),
    ...options,
  };
}

function jsonResponse<T>(body: T, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function jsonLinesResponse<T>(items: T[]) {
  return new Response(items.map((item) => JSON.stringify(item)).join('\n'), {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}

function jsonLinesStreamResponse<T>(beforeWait: T[], wait: Promise<void>, afterWait: T[]) {
  const encoder = new TextEncoder();
  return new Response(new ReadableStream({
    async start(controller) {
      enqueueJsonLines(controller, encoder, beforeWait);
      await wait;
      enqueueJsonLines(controller, encoder, afterWait);
      controller.close();
    },
  }), {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}

function enqueueJsonLines<T>(
  controller: ReadableStreamDefaultController<Uint8Array>,
  encoder: TextEncoder,
  items: T[],
) {
  for (const item of items) {
    controller.enqueue(encoder.encode(`${JSON.stringify(item)}\n`));
  }
}

async function renderApp() {
  const host = document.createElement('div');
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(<App />);
  });
  return { root };
}

async function unmountApp(root: ReturnType<typeof createRoot>) {
  await act(async () => {
    root.unmount();
  });
}

async function clickButton(name: string) {
  await act(async () => {
    getButton(name).dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
}

function getButton(name: string) {
  const button = [...document.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === name);
  if (!button) {
    throw new Error(`Unable to find button: ${name}`);
  }

  return button;
}

type AppFetchMock =
  | ReturnType<typeof createFetchMock>
  | ReturnType<typeof createHardRefreshFetchMock>
  | ReturnType<typeof createStalePreservingRefreshFetchMock>
  | ReturnType<typeof createDelayedVisibleChecksFetchMock>;

function checksRequestUrls(fetchMock: AppFetchMock) {
  return requestUrls(fetchMock, '/api/github/pulls/checks');
}

function refreshChecksRequestNumbers(fetchMock: AppFetchMock) {
  const calls = fetchMock.mock.calls as [RequestInfo | URL, RequestInit?][];
  return calls.flatMap(([input, init]) => {
    const url = new URL(input.toString(), window.location.origin);
    if (url.pathname !== '/api/github/pulls/checks' || url.searchParams.get('refresh') !== 'true') {
      return [];
    }

    const body = typeof init?.body === 'string'
      ? JSON.parse(init.body) as PullRequestChecksRequest
      : { pullRequests: [] };
    return body.pullRequests.map((pullRequest) => pullRequest.number);
  });
}

function streamRequestUrls(fetchMock: ReturnType<typeof createHardRefreshFetchMock>) {
  return requestUrls(fetchMock, '/api/github/pulls/stream');
}

function requestUrls(fetchMock: AppFetchMock, pathname: string) {
  return fetchMock.mock.calls
    .map(([input]) => new URL(input.toString(), window.location.origin))
    .filter((url) => url.pathname === pathname);
}

function createDeferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((promiseResolve) => {
    resolve = promiseResolve;
  });
  return { promise, resolve };
}

async function waitFor(assertion: () => void, timeoutMs = 1_000) {
  const start = Date.now();
  let lastError: unknown;
  while (Date.now() - start < timeoutMs) {
    try {
      assertion();
      return;
    } catch (err) {
      lastError = err;
      await act(async () => {
        await new Promise((resolve) => window.setTimeout(resolve, 10));
      });
    }
  }

  throw lastError;
}

function installIntersectionObserverMock() {
  const observers: TestIntersectionObserver[] = [];

  class TestIntersectionObserver {
    private element: Element | null = null;
    private readonly callback: IntersectionObserverCallback;

    constructor(callback: IntersectionObserverCallback) {
      this.callback = callback;
      observers.push(this);
    }

    observe(element: Element) {
      this.element = element;
    }

    unobserve() {
      this.element = null;
    }

    disconnect() {
      this.element = null;
    }

    takeRecords() {
      return [];
    }

    isObserving() {
      return this.element !== null;
    }

    trigger() {
      if (!this.element) {
        return;
      }

      this.callback([
        {
          isIntersecting: true,
          target: this.element,
        } as IntersectionObserverEntry,
      ], this as unknown as IntersectionObserver);
    }
  }

  vi.stubGlobal('IntersectionObserver', TestIntersectionObserver);
  return observers;
}

function triggerObservedRows(observers: { isObserving: () => boolean; trigger: () => void }[], limit = Number.POSITIVE_INFINITY) {
  let triggered = 0;
  for (const observer of observers) {
    if (!observer.isObserving()) {
      continue;
    }

    observer.trigger();
    triggered += 1;
    if (triggered >= limit) {
      break;
    }
  }
}
