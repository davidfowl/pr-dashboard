import { defaultRepos } from '../constants';
import type { DashboardMode } from '../types';

export function parseRepositories(value: string) {
  const repositories = value
    .split(/[,\s]+/)
    .map((repository) => repository.trim())
    .filter(Boolean);

  return [...new Set(repositories)].length > 0 ? [...new Set(repositories)] : defaultRepos;
}

const bucketHashPrefix = '#bucket/';

export function bucketRouteId(label: string) {
  return label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'bucket';
}

export function createBucketHash(bucketId: string) {
  return `${bucketHashPrefix}${encodeURIComponent(bucketId)}`;
}

export function createBucketUrl(bucketId: string) {
  return `${window.location.origin}${window.location.pathname}${window.location.search}${createBucketHash(bucketId)}`;
}

export function replaceBucketHistory(bucketId: string) {
  const hash = createBucketHash(bucketId);
  if (window.location.hash !== hash) {
    window.history.replaceState({ view: 'dashboard', bucketId }, '', hash);
  }
}

export function parseBucketHash(hash: string) {
  const match = /^#bucket\/([^/]+)$/.exec(hash);
  if (!match) {
    return null;
  }

  return {
    bucketId: decodeURIComponent(match[1]),
  };
}

export function pushDetailHistory(repository: string, number: number) {
  const hash = `#pr/${encodeURIComponent(repository)}/${number}`;
  if (window.location.hash !== hash) {
    window.history.pushState({ view: 'details', repository, number }, '', hash);
  }
}

export function parseDetailHash(hash: string) {
  const match = /^#pr\/([^/]+)\/(\d+)$/.exec(hash);
  if (!match) {
    return null;
  }

  return {
    repository: decodeURIComponent(match[1]),
    number: Number.parseInt(match[2], 10),
  };
}

export function parseDashboardMode(search: string): DashboardMode {
  const mode = new URLSearchParams(search).get('mode')?.toLowerCase();
  return mode === 'review' ? 'review' : 'ship';
}

export function pushDashboardModeHistory(mode: DashboardMode) {
  const url = new URL(window.location.href);
  url.searchParams.set('mode', mode);
  const nextUrl = `${url.pathname}${url.search}${url.hash}`;
  if (`${window.location.pathname}${window.location.search}${window.location.hash}` !== nextUrl) {
    window.history.pushState({ view: 'dashboard', mode }, '', nextUrl);
  }
}

export function shortRepoName(repository: string) {
  const parts = repository.split('/');
  return parts[parts.length - 1] ?? repository;
}
