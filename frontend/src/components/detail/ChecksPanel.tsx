import type { ChecksStatus, MergeableState } from '../../types';
import { formatCount, formatRelative } from '../../utils/format';

type ChecksPanelProps = {
  checks: ChecksStatus;
  mergeableState?: MergeableState | null;
};

function ChecksPanel({ checks, mergeableState }: ChecksPanelProps) {
  const showMergeableNote = mergeableState === 'dirty'
    || mergeableState === 'blocked'
    || mergeableState === 'behind';

  if ((checks.state === 'none' || checks.totalCount === 0) && !showMergeableNote) {
    return null;
  }

  const stateLabel = checks.state === 'failure'
    ? 'CI failing'
    : checks.state === 'pending'
      ? 'CI running'
      : checks.state === 'success'
        ? 'CI passing'
        : 'No checks reported';
  const tone = checks.state === 'failure'
    ? 'danger'
    : checks.state === 'pending'
      ? 'warning'
      : checks.state === 'success'
        ? 'success'
        : 'muted';

  const counts: string[] = [];
  if (checks.successCount > 0) counts.push(`${checks.successCount} passed`);
  if (checks.failureCount > 0) counts.push(`${checks.failureCount} failing`);
  if (checks.pendingCount > 0) counts.push(`${checks.pendingCount} running`);
  if (checks.neutralCount > 0) counts.push(`${checks.neutralCount} neutral`);
  if (checks.skippedCount > 0) counts.push(`${checks.skippedCount} skipped`);

  return (
    <section className="checks-panel" aria-label="Checks status">
      <div className="section-title-row">
        <p className="eyebrow">CI checks</p>
        <h3>Checks status</h3>
      </div>
      <article className={`checks-card ${tone}`}>
        <div className="checks-card-header">
          <strong>{stateLabel}</strong>
          {checks.completedAt && (
            <time dateTime={checks.completedAt}>updated {formatRelative(checks.completedAt)}</time>
          )}
        </div>
        {counts.length > 0 && (
          <p className="checks-counts">
            {counts.join(' · ')} {checks.totalCount > 0 ? `· ${formatCount(checks.totalCount, 'check total', 'checks total')}` : ''}
          </p>
        )}
        {showMergeableNote && (
          <p className="checks-mergeable">
            {mergeableState === 'dirty' && 'Merge conflicts on base — author needs to rebase.'}
            {mergeableState === 'behind' && 'Branch is behind base — may need a sync before merging.'}
            {mergeableState === 'blocked' && 'Blocked by branch protection — required checks or approvals still missing.'}
          </p>
        )}
        {checks.failingChecks.length > 0 && (
          <ul className="failing-checks">
            {checks.failingChecks.map((failing, index) => (
              <li key={`${failing.htmlUrl ?? failing.name}-${failing.conclusion ?? ''}-${index}`}>
                {failing.htmlUrl ? (
                  <a href={failing.htmlUrl} target="_blank" rel="noreferrer">{failing.name}</a>
                ) : (
                  <span>{failing.name}</span>
                )}
                {failing.conclusion && (
                  <span className="failing-check-conclusion">{failing.conclusion}</span>
                )}
              </li>
            ))}
          </ul>
        )}
      </article>
    </section>
  );
}

export default ChecksPanel;
