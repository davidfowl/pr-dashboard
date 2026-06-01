import { useEffect, useRef } from 'react';
import type { CheckState, LinkedIssueSummary, PullRequestSummary } from '../types';
import { formatRelative } from '../utils/format';
import PullRequestSignalPills from './PullRequestSignalPills';
import type { PullRequestSignalPillsProps } from './PullRequestSignalPills';

type PullRequestListItemProps = {
  pullRequest: PullRequestSummary;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest?: (repository: string, pullRequest: PullRequestSummary) => void;
  signalProps?: Omit<PullRequestSignalPillsProps, 'pullRequest'>;
  linkedIssues?: LinkedIssueSummary[];
};

const checkBadgeGlyphs: Record<Exclude<CheckState, 'none'>, { glyph: string; label: string }> = {
  unknown: { glyph: '…', label: 'CI loading' },
  success: { glyph: '✓', label: 'CI passed' },
  failure: { glyph: '✗', label: 'CI failing' },
  pending: { glyph: '●', label: 'CI running' },
};

function PullRequestListItem({
  pullRequest,
  onSelectPullRequest,
  onVisiblePullRequest,
  signalProps,
  linkedIssues = [],
}: PullRequestListItemProps) {
  const itemRef = useRef<HTMLElement | null>(null);
  const checksState = pullRequest.checks?.state;
  const badge = checksState && checksState !== 'none' ? checkBadgeGlyphs[checksState] : null;
  const badgeTitle = badge
    ? `${badge.label}${pullRequest.checks?.failingChecks?.length
      ? ` · ${pullRequest.checks.failingChecks.map((failing) => failing.name).join(', ')}`
      : ''}`
    : undefined;

  useEffect(() => {
    if (
      !onVisiblePullRequest
      || pullRequest.state !== 'open'
      || !pullRequest.headSha
      || checksState !== 'unknown'
    ) {
      return;
    }

    const node = itemRef.current;
    if (!node || !('IntersectionObserver' in window)) {
      onVisiblePullRequest(pullRequest.repository, pullRequest);
      return;
    }

    let reported = false;
    const observer = new IntersectionObserver(
      (entries) => {
        if (reported || !entries.some((entry) => entry.isIntersecting)) {
          return;
        }

        reported = true;
        onVisiblePullRequest(pullRequest.repository, pullRequest);
        observer.disconnect();
      },
      { rootMargin: '160px' },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [checksState, onVisiblePullRequest, pullRequest]);

  return (
    <article ref={itemRef} className="attention-pr-row">
      <span className="attention-pr-number">
        {badge && (
          <span
            className={`attention-pr-check-badge ${checksState}`}
            title={badgeTitle}
            aria-label={badgeTitle ?? badge.label}
            role="img"
          >
            <span aria-hidden="true">{badge.glyph}</span>
          </span>
        )}
        #{pullRequest.number}
      </span>
      <span className="attention-pr-repo" title={pullRequest.repository}>
        {pullRequest.repository}
      </span>
      <a
        className="attention-pr-title"
        href={pullRequest.htmlUrl}
        target="_blank"
        rel="noreferrer"
        title={pullRequest.title}
      >
        {pullRequest.title}
      </a>
      <span className="attention-pr-meta">
        {pullRequest.author} · updated {formatRelative(pullRequest.updatedAt)}
      </span>
      <div className="attention-pr-actions">
        <a
          className="attention-pr-github-link"
          href={pullRequest.htmlUrl}
          target="_blank"
          rel="noreferrer"
          aria-label={`Open ${pullRequest.repository} #${pullRequest.number} on GitHub`}
        >
          GitHub
        </a>
        <button
          type="button"
          className="attention-pr-timeline-button"
          onClick={() => onSelectPullRequest(pullRequest.repository, pullRequest)}
          aria-label={`View timeline for ${pullRequest.repository} #${pullRequest.number}`}
        >
          View timeline
        </button>
      </div>
      <PullRequestSignalPills pullRequest={pullRequest} {...signalProps} />
      {linkedIssues.length > 0 && (
        <span className="attention-pr-linked-issues" aria-label="Linked issues">
          {linkedIssues.slice(0, 3).map((issue) => (
            <a
              key={`${issue.repository}#${issue.number}`}
              href={issue.htmlUrl}
              target="_blank"
              rel="noreferrer"
              title={`${issue.repository}#${issue.number}: ${issue.title}`}
            >
              issue #{issue.number}
            </a>
          ))}
        </span>
      )}
    </article>
  );
}

export default PullRequestListItem;
