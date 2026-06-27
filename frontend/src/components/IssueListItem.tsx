import type { ShipWeekIssueSummary } from '../types';
import { formatRelative } from '../utils/format';
import { sameLogin } from '../utils/models';
import IssueSignalPills from './IssueSignalPills';
import type { IssueSignalPillsProps } from './IssueSignalPills';

type IssueListItemProps = {
  issue: ShipWeekIssueSummary;
  signalProps?: Omit<IssueSignalPillsProps, 'issue'>;
  login?: string;
};

function IssueListItem({ issue, signalProps, login }: IssueListItemProps) {
  const linkedPullRequests = issue.linkedOpenPullRequests.slice(0, 3);
  const isSignedInAuthor = login ? sameLogin(issue.author, login) : false;

  return (
    <article className={`attention-issue-row${isSignedInAuthor ? ' signed-in-user-entry' : ''}`}>
      <span className="attention-issue-number">
        <a
          className="attention-issue-number-link"
          href={issue.htmlUrl}
          target="_blank"
          rel="noreferrer"
          aria-label={`Open ${issue.repository} #${issue.number} on GitHub`}
        >
          #{issue.number}
        </a>
      </span>
      <span className="attention-issue-repo" title={issue.repository}>
        <a
          className="attention-issue-repo-link"
          href={repositoryUrl(issue.repository)}
          target="_blank"
          rel="noreferrer"
        >
          {issue.repository}
        </a>
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
        {isSignedInAuthor && <span className="signed-in-user-badge">Yours</span>}
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

function repositoryUrl(repository: string) {
  return `https://github.com/${repository}`;
}

export default IssueListItem;
