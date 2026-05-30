import type { PullRequestStreamItem, PullRequestSummary } from '../types';
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

export async function streamPullRequests(url: string, options: StreamPullRequestsOptions = {}) {
  const pullRequests: PullRequestSummary[] = [];
  const response = await fetch(url, { signal: options.signal });

  await readJsonLines<PullRequestStreamItem>(response, (item) => {
    const pullRequest = normalizeStreamedPullRequest(item);
    if (options.filter && !options.filter(pullRequest, item)) {
      return;
    }

    pullRequests.push(pullRequest);
    options.onPullRequest?.(pullRequest, item);
  });

  return pullRequests;
}

export function upsertByUpdatedAt<T extends PullRequestCollectionItem>(items: T[], item: T) {
  return upsertManyByUpdatedAt(items, [item]);
}

export function upsertManyByUpdatedAt<T extends PullRequestCollectionItem>(items: T[], newItems: T[]) {
  if (newItems.length === 0) {
    return items;
  }

  const itemsByKey = new Map(items.map((item) => [itemKey(item), item]));
  for (const item of newItems) {
    itemsByKey.set(itemKey(item), item);
  }

  return [...itemsByKey.values()].sort((first, second) => updatedAtSortValue(second) - updatedAtSortValue(first));
}

function normalizeStreamedPullRequest(item: PullRequestStreamItem): PullRequestSummary {
  return {
    ...item.pullRequest,
    repository: item.repository,
  };
}

function itemKey(item: { repository: string; number: number }) {
  return `${item.repository.toLowerCase()}#${item.number}`;
}

function updatedAtSortValue(item: { updatedAt: string }) {
  return new Date(item.updatedAt).getTime();
}
