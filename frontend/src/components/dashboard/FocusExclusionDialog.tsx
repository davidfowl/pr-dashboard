import { useEffect, useRef } from 'react';

type FocusExclusionDialogProps = {
  open: boolean;
  onClose: () => void;
};

type ExclusionReason = {
  title: string;
  detail: string;
};

const exclusionReasons: ExclusionReason[] = [
  {
    title: 'It has failing CI checks',
    detail: 'PRs with any failed check are hidden here until CI is green again (pending checks are fine). Look for it in the "CI failing" bucket on the board below.',
  },
  {
    title: 'It was opened more than 14 days ago',
    detail: 'The queue keeps only PRs opened in the last 14 days so it stays focused on recent work.',
  },
  {
    title: 'It is still a draft',
    detail: 'Draft PRs are treated as not ready for review and are kept out of the queue.',
  },
  {
    title: 'It has merge conflicts',
    detail: 'A conflicting PR is waiting on the author to rebase, not on a reviewer, so it moves to the "Merge conflicts" bucket instead.',
  },
  {
    title: 'It has unresolved Copilot review feedback',
    detail: 'A PR with open feedback from the Copilot review bot is treated as waiting on the author and routes to the "Copilot feedback" bucket.',
  },
  {
    title: 'It belongs to a specialized lane',
    detail: 'Docs, Community Toolkit, bot/automation, and community PRs are routed to their own lanes rather than the main queue.',
  },
  {
    title: 'It has gone quiet for 7+ days (stalled)',
    detail: 'A stalled PR only appears here when it also needs review, an author response, or a merge — otherwise it is kept out.',
  },
];

function FocusExclusionDialog({ open, onClose }: FocusExclusionDialogProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) {
      return;
    }

    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  return (
    <dialog
      ref={dialogRef}
      className="focus-info-dialog"
      aria-labelledby="focus-info-dialog-title"
      onClose={onClose}
      onClick={(event) => {
        if (event.target === dialogRef.current) {
          onClose();
        }
      }}
    >
      <div className="focus-info-dialog-body">
        <div className="focus-info-dialog-header">
          <h2 id="focus-info-dialog-title">Why a PR might not be in Needs attention</h2>
          <button
            type="button"
            className="focus-info-dialog-close"
            aria-label="Close"
            onClick={onClose}
          >
            ×
          </button>
        </div>
        <p className="focus-info-dialog-intro">
          The Needs attention queue is intentionally narrow — it surfaces recent, actionable PRs.
          If yours is missing, one of these is usually why:
        </p>
        <ul className="focus-info-reasons">
          {exclusionReasons.map((reason) => (
            <li key={reason.title}>
              <strong>{reason.title}</strong>
              <span>{reason.detail}</span>
            </li>
          ))}
        </ul>
        <p className="focus-info-dialog-footnote">
          A hidden PR is never lost — it still shows in its specific bucket on the board below.
        </p>
      </div>
    </dialog>
  );
}

export default FocusExclusionDialog;
