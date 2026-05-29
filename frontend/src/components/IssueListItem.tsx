import type { ShipWeekIssueSummary } from '../types';
import { formatRelative } from '../utils/format';
import IssueSignalPills from './IssueSignalPills';
import type { IssueSignalPillsProps } from './IssueSignalPills';

type IssueListItemProps = {
  issue: ShipWeekIssueSummary;
  signalProps?: Omit<IssueSignalPillsProps, 'issue'>;
};

function IssueListItem({ issue, signalProps }: IssueListItemProps) {
  const linkedPullRequests = issue.linkedOpenPullRequests.slice(0, 3);

  return (
    <article className="attention-issue-row">
      <span className="attention-issue-number">#{issue.number}</span>
      <span className="attention-issue-repo" title={issue.repository}>
        {issue.repository}
      </span>
      <a
        className="attention-issue-title"
        href={issue.htmlUrl}
        target="_blank"
        rel="noreferrer"
        title={issue.title}
      >
        {issue.title}
      </a>
      <span className="attention-issue-meta">
        {issue.assignees.length > 0 ? issue.assignees.join(', ') : 'unowned'} ·
        {' '}
        updated {formatRelative(issue.updatedAt)}
      </span>
      <div className="attention-issue-actions">
        <a
          className="attention-issue-github-link"
          href={issue.htmlUrl}
          target="_blank"
          rel="noreferrer"
          aria-label={`Open ${issue.repository} #${issue.number} on GitHub`}
        >
          GitHub
        </a>
      </div>
      <IssueSignalPills issue={issue} {...signalProps} />
      {linkedPullRequests.length > 0 && (
        <span className="attention-issue-linked-pulls" aria-label="Linked pull requests">
          {linkedPullRequests.map((pullRequestNumber) => (
            <a
              key={pullRequestNumber}
              href={linkedPullRequestUrl(issue.repository, pullRequestNumber)}
              target="_blank"
              rel="noreferrer"
              title={`${issue.repository}#${pullRequestNumber}`}
            >
              PR #{pullRequestNumber}
            </a>
          ))}
        </span>
      )}
    </article>
  );
}

function linkedPullRequestUrl(repository: string, pullRequestNumber: number) {
  return `https://github.com/${repository}/pull/${pullRequestNumber}`;
}

export default IssueListItem;
