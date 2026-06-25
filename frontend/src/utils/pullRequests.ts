import type { CheckState, PullRequestStreamItem, PullRequestSummary } from '../types';
import { readJsonLines } from './http';

type PullRequestCollectionItem = {
  repository: string;
  number: number;
  updatedAt: string;
};

type StreamPullRequestsOptions = {
  signal?: AbortSignal;
  filter?: (pullRequest: PullRequestSummary, item: PullRequestStreamItem) => boolean;
  onPullRequest?: (pullRequest: PullRequestSummary, item: PullRequestStreamItem) => void;
};

export type StreamPullRequestsResult = {
  pullRequests: PullRequestSummary[];
  completed: boolean;
  hadStaleRows: boolean;
};

export async function streamPullRequests(url: string, options: StreamPullRequestsOptions = {}) {
  const displayedPullRequests: PullRequestSummary[] = [];
  const livePullRequests: PullRequestSummary[] = [];
  let completed = false;
  let hadStaleRows = false;
  const response = await fetch(url, { signal: options.signal });

  await readJsonLines<PullRequestStreamItem>(response, (item) => {
    if (item.isComplete) {
      completed = true;
      return;
    }

    if (!item.pullRequest) {
      return;
    }

    const pullRequest = normalizeStreamedPullRequest(item.repository, item.pullRequest);
    if (options.filter && !options.filter(pullRequest, item)) {
      return;
    }

    displayedPullRequests.push(pullRequest);
    if (!item.isStale) {
      livePullRequests.push(pullRequest);
    } else {
      hadStaleRows = true;
    }
    options.onPullRequest?.(pullRequest, item);
  });

  return {
    pullRequests: completed ? livePullRequests : displayedPullRequests,
    completed,
    hadStaleRows,
  };
}

export function upsertByUpdatedAt<T extends PullRequestCollectionItem>(items: T[], item: T) {
  return upsertManyByUpdatedAt(items, [item]);
}

export function upsertManyByUpdatedAt<T extends PullRequestCollectionItem>(
  items: T[],
  newItems: T[],
  merge?: (current: T, next: T) => T,
) {
  if (newItems.length === 0) {
    return items;
  }

  const itemsByKey = new Map(items.map((item) => [itemKey(item), item]));
  for (const item of newItems) {
    const key = itemKey(item);
    const current = itemsByKey.get(key);
    itemsByKey.set(key, current && merge ? merge(current, item) : item);
  }

  return [...itemsByKey.values()].sort((first, second) => updatedAtSortValue(second) - updatedAtSortValue(first));
}

export function upsertPullRequestByUpdatedAt(items: PullRequestSummary[], item: PullRequestSummary) {
  return upsertPullRequestsByUpdatedAt(items, [item]);
}

export function upsertPullRequestsByUpdatedAt(
  items: PullRequestSummary[],
  newItems: PullRequestSummary[],
) {
  return upsertManyByUpdatedAt(items, newItems, mergePullRequestOverlay);
}

export function replacePullRequestsByUpdatedAt(
  items: PullRequestSummary[],
  newItems: PullRequestSummary[],
) {
  const itemsByKey = new Map(items.map((item) => [itemKey(item), item]));
  return upsertManyByUpdatedAt(
    [],
    newItems.map((item) => {
      const current = itemsByKey.get(itemKey(item));
      return current ? mergePullRequestOverlay(current, item) : item;
    }),
    mergePullRequestOverlay,
  );
}

function normalizeStreamedPullRequest(
  repository: string,
  pullRequest: NonNullable<PullRequestStreamItem['pullRequest']>,
): PullRequestSummary {
  return {
    ...pullRequest,
    repository,
  };
}

function mergePullRequestOverlay(current: PullRequestSummary, next: PullRequestSummary) {
  if (
    next.headSha
    && next.headSha === current.headSha
    && shouldPreserveChecks(current.checks.state, next.checks.state)
  ) {
    return {
      ...next,
      checks: current.checks,
    };
  }

  return next;
}

function shouldPreserveChecks(currentState: CheckState, nextState: CheckState) {
  return isLoadedCheckState(currentState) && !isLoadedCheckState(nextState);
}

function isLoadedCheckState(state: CheckState) {
  return state === 'success' || state === 'failure' || state === 'pending';
}

function itemKey(item: { repository: string; number: number }) {
  return `${item.repository.toLowerCase()}#${item.number}`;
}

function updatedAtSortValue(item: { updatedAt: string }) {
  return new Date(item.updatedAt).getTime();
}
