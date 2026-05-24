import { defaultRepos } from '../constants';

export function parseRepositories(value: string) {
  const repositories = value
    .split(/[,\s]+/)
    .map((repository) => repository.trim())
    .filter(Boolean);

  return [...new Set(repositories)].length > 0 ? [...new Set(repositories)] : defaultRepos;
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

export function shortRepoName(repository: string) {
  const parts = repository.split('/');
  return parts[parts.length - 1] ?? repository;
}
