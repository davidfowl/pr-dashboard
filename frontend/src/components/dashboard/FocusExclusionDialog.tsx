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
    title: 'It has had no actionable activity in 14 days',
    detail: 'The queue keeps each PR only while its action lane has recent activity — a fresh commit, review, approval, or update in the last 14 days — so it stays focused on PRs that are actually moving. An older PR reappears as soon as it sees new activity.',
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
    title: 'It has unresolved review feedback',
    detail: 'A PR with open review threads (human or Copilot) is treated as waiting on the author to resolve them and routes to the "Unresolved feedback" bucket.',
  },
  {
    title: 'It has changes requested',
    detail: 'A PR with requested changes is treated as waiting on the author and routes to the "Author response" bucket instead of Needs attention. Once the author pushes a response, it can re-enter as "Re-review needed."',
  },
  {
    title: 'It belongs to a specialized lane',
    detail: 'Docs, Community Toolkit, bot/automation, and aged-out community PRs are routed to their own lanes rather than the main queue. Recently active community PRs show in the Community PRs list.',
  },
  {
    title: 'It has gone quiet for 7+ days (stalled)',
    detail: 'The standalone "Stalled" lane is kept out of this queue. A stalled PR still appears here when it also has an actionable reason in another lane — for example it needs review or re-review, or is ready to merge.',
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
          dialogRef.current.close();
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
            onClick={() => dialogRef.current?.close()}
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
          A hidden PR is not lost — most still show in a specific bucket below, and your own non-draft PRs can appear in the outside queue list.
        </p>
      </div>
    </dialog>
  );
}

export default FocusExclusionDialog;
