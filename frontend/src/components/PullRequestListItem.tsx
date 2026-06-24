import { useEffect, useRef } from 'react';
import { dayMs } from '../constants';
import type {
  AttentionSignal,
  CheckState,
  LinkedIssueSummary,
  PullRequestSummary,
  VisiblePullRequestHandler,
} from '../types';
import { formatRelative } from '../utils/format';
import { createAttentionSignals, sameLogin } from '../utils/models';
import { rowMetadataDisplaySignals } from '../utils/rowMetadataSignals';
import PullRequestSignalPills from './PullRequestSignalPills';
import type { PullRequestSignalPillsProps } from './PullRequestSignalPills';

type PullRequestListItemProps = {
  pullRequest: PullRequestSummary;
  bucketLabel: string;
  onSelectPullRequest: (repository: string, pullRequest: PullRequestSummary) => void;
  onVisiblePullRequest?: VisiblePullRequestHandler;
  visibleChecksRefreshKey?: number;
  signalProps?: Omit<PullRequestSignalPillsProps, 'pullRequest'>;
  linkedIssues?: LinkedIssueSummary[];
  login?: string;
};

const checkBadgeGlyphs: Record<Exclude<CheckState, 'none'>, { glyph: string; label: string }> = {
  unknown: { glyph: '…', label: 'CI loading' },
  success: { glyph: '✓', label: 'CI passed' },
  failure: { glyph: '✗', label: 'CI failing' },
  pending: { glyph: '●', label: 'CI running' },
};

function PullRequestListItem({
  pullRequest,
  bucketLabel,
  onSelectPullRequest,
  onVisiblePullRequest,
  visibleChecksRefreshKey = 0,
  signalProps,
  linkedIssues = [],
  login,
}: PullRequestListItemProps) {
  const itemRef = useRef<HTMLElement | null>(null);
  const lastForcedVisibleChecksRefreshRef = useRef(0);
  const checksState = pullRequest.checks?.state;
  const shouldForceVisibleChecksRefresh =
    visibleChecksRefreshKey > 0
    && lastForcedVisibleChecksRefreshRef.current !== visibleChecksRefreshKey;
  const badge = checksState && checksState !== 'none' ? checkBadgeGlyphs[checksState] : null;
  const isSignedInAuthor = login ? sameLogin(pullRequest.author, login) : false;
  const actionMarker = rowActionMarker(bucketLabel);
  const isReadyToMerge = actionMarker?.id === 'ready-to-merge';
  const updatedAge = updatedAgeParts(pullRequest.updatedAt);
  const updatedAgeTone = staleUpdatedAgeTone(pullRequest.updatedAt);
  const metadataSignals = rowMetadataDisplaySignals(createAttentionSignals({ pullRequest, reason: '' }));
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
      || (checksState !== 'unknown' && !shouldForceVisibleChecksRefresh)
    ) {
      return;
    }

    const reportVisible = () => {
      const accepted = onVisiblePullRequest(pullRequest.repository, pullRequest, {
        forceRefresh: shouldForceVisibleChecksRefresh,
      });
      if (accepted) {
        lastForcedVisibleChecksRefreshRef.current = visibleChecksRefreshKey;
      }
    };

    const node = itemRef.current;
    if (!node || !('IntersectionObserver' in window)) {
      reportVisible();
      return;
    }

    let reported = false;
    const observer = new IntersectionObserver(
      (entries) => {
        if (reported || !entries.some((entry) => entry.isIntersecting)) {
          return;
        }

        reported = true;
        reportVisible();
        observer.disconnect();
      },
      { rootMargin: '160px' },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [checksState, onVisiblePullRequest, pullRequest, shouldForceVisibleChecksRefresh, visibleChecksRefreshKey]);

  return (
    <article
      ref={itemRef}
      className={[
        'attention-pr-row',
        'compact-pr-action-marker-layout content-bounded-action-marker-layout',
        actionMarker ? 'has-action-marker' : undefined,
        actionMarker ? `${actionMarker.id}-entry` : undefined,
        isSignedInAuthor ? 'signed-in-user-entry signed-in-user-entry-full-bleed' : undefined,
        isReadyToMerge ? 'ready-to-merge-entry-full-bleed' : undefined,
      ].filter(Boolean).join(' ')}
    >
      <span className="attention-pr-number">
        {badge && (
          <span
            className={`attention-pr-check-badge ${checksState}`}
            title={badgeTitle}
            aria-label={badgeTitle ?? badge.label}
            role="img"
          >
            {badge.glyph}
          </span>
        )}
        <a
          className="attention-pr-number-link"
          href={pullRequest.htmlUrl}
          target="_blank"
          rel="noreferrer"
          aria-label={`Open ${pullRequest.repository} #${pullRequest.number} on GitHub`}
        >
          #{pullRequest.number}
        </a>
      </span>
      <span className="attention-pr-repo" title={pullRequest.repository}>
        <a
          className="attention-pr-repo-link"
          href={repositoryUrl(pullRequest.repository)}
          target="_blank"
          rel="noreferrer"
        >
          {pullRequest.repository}
        </a>
      </span>
      <span
        className={[
          'attention-pr-action-marker',
          'first-row-action-marker',
          'row-height-neutral-action-marker',
          actionMarker ? actionMarker.id : 'empty-action-marker',
        ].join(' ')}
        aria-hidden={actionMarker ? undefined : true}
      >
        {actionMarker && <span>{actionMarker.label}</span>}
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
        {pullRequest.author} · updated{' '}
        <span className={`attention-pr-updated-age${updatedAgeTone ? ` age-tone-${updatedAgeTone}` : ''}`}>
          {updatedAge.value}
        </span>
        {updatedAge.suffix && ` ${updatedAge.suffix}`}
        {metadataSignals.map((signal) => (
          <span
            key={signal.label}
            className={rowMetadataClassName(signal)}
          >
            {` · ${signal.label}`}
          </span>
        ))}
        {isSignedInAuthor && <span className="signed-in-user-badge">Yours</span>}
      </span>
      <div className="attention-pr-actions">
        <button
          type="button"
          className="attention-pr-timeline-button attention-pr-primary-action"
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

function rowActionMarker(bucketLabel: string) {
  switch (bucketLabel) {
    case 'Ready to merge':
      return { id: 'ready-to-merge', label: 'Ready to merge' };
    case 'Needs review':
      return { id: 'needs-review', label: 'Needs review' };
    default:
      return null;
  }
}

function repositoryUrl(repository: string) {
  return `https://github.com/${repository}`;
}

function updatedAgeParts(value: string) {
  const relative = formatRelative(value);
  const [age, suffix] = relative.split(' ', 2);
  return suffix ? { value: age, suffix } : { value: relative, suffix: '' };
}

function staleUpdatedAgeTone(value: string) {
  const age = Date.now() - new Date(value).getTime();
  if (age >= 14 * dayMs) {
    return 'danger';
  }
  if (age >= 7 * dayMs) {
    return 'warning';
  }
  return null;
}

function rowMetadataTone(signal: AttentionSignal) {
  return signal.tone === 'warning' || signal.tone === 'danger' ? signal.tone : null;
}

function rowMetadataClassName(signal: AttentionSignal) {
  const tone = rowMetadataTone(signal);
  return `attention-pr-row-metadata-signal${tone ? ` age-tone-${tone}` : ''}`;
}

export default PullRequestListItem;
